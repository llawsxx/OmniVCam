/* Private playback engine implementation. All cache/output operations run under
 * input_change_mutex; producers keep using the bounded ordinary frame queues. */
#pragma once
#include <math.h>

#define PLAYBACK_OVERLAP_DEFAULT 0.1
#define PLAYBACK_OVERLAP_MAX 5.0
#define PLAYBACK_OVERLAP_CACHE_BYTES (128LL * 1024 * 1024)

typedef struct playback_overlap {
    struct playback_overlap* next;
    avframe_node* tail[2];
    AVFrame* leading_video;
    AVFrame* fifo_tail;
    int64_t old_id, new_id;
    int64_t source_start, duration, output_start;
    int64_t audio_start_sample, audio_bridge_samples, audio_duration_samples;
    int64_t deferred_end;
    int started, video_done, audio_done;
    int immediate;
    int media_mask;
} playback_overlap;

typedef struct playback_overlap_state {
    playback_overlap* pending;
    playback_overlap* opening; /* Non-owning: the main thread owns this prepared cache. */
    AVFrame* last_video;
    int paused;
    int64_t paused_at, paused_sample;
} playback_overlap_state;

static double playback_overlap_seconds(const char* text)
{
    AVDictionary* options = NULL;
    const char* tab = text ? strchr(text, '\t') : NULL;
    double seconds = PLAYBACK_OVERLAP_DEFAULT;
    if (tab) {
        av_dict_parse_string(&options, tab + 1, "=", ",", 0);
        AVDictionaryEntry* option = av_dict_get(options, "overlap", NULL, 0);
        if (option) {
            char* end = NULL;
            seconds = strtod(option->value, &end);
            if (end == option->value || *end || !_finite(seconds) || seconds < 0 || seconds > PLAYBACK_OVERLAP_MAX)
                seconds = -1;
        }
    }
    av_dict_free(&options);
    return seconds;
}

static int playback_overlap_local_file(const char* text)
{
    size_t length = text ? strcspn(text, "\t") : 0;
    if (!length || *text == '<') return 0;
    /* Inspect the path only: filter/decoder options can themselves contain ':'. */
    const char* colon = memchr(text, ':', length);
    return !colon || (colon == text + 1 && isalpha((unsigned char)*text) && length > 2 &&
        (text[2] == '\\' || text[2] == '/'));
}

static void playback_overlap_free_nodes(avframe_node** head)
{
    while (*head) {
        avframe_node* node = *head; *head = node->next;
        av_frame_free(&node->frame); av_free(node);
    }
}

static void playback_overlap_free(playback_overlap* blend)
{
    if (!blend) return;
    for (int i = 0; i < 2; i++) playback_overlap_free_nodes(&blend->tail[i]);
    av_frame_free(&blend->leading_video); av_frame_free(&blend->fifo_tail); av_free(blend);
}

static void playback_overlap_clear(inout_context* ctx)
{
    playback_overlap_state* state = ctx->overlap;
    if (!state) return;
    while (state->pending) {
        playback_overlap* blend = state->pending; state->pending = blend->next;
        playback_overlap_free(blend);
    }
    state->opening = NULL;
    av_frame_free(&state->last_video);
    state->paused = 0;
}

static int64_t playback_audio_time(inout_context* ctx, int64_t samples)
{
    return av_rescale_q(samples, (AVRational){1, ctx->output_audio_sample_rate}, UNIVERSAL_TB);
}

static AVFrame* playback_audio_slice(inout_context* ctx, const AVFrame* source, int offset, int samples)
{
    AVFrame* frame = av_frame_alloc();
    if (!frame) return NULL;
    frame->format = ctx->output_audio_format; frame->sample_rate = ctx->output_audio_sample_rate;
    frame->nb_samples = samples;
    av_channel_layout_copy(&frame->ch_layout, &ctx->output_audio_layout);
    if (av_frame_get_buffer(frame, 0) < 0 || (source && av_samples_copy(frame->extended_data,
        source->extended_data, 0, offset, samples, frame->ch_layout.nb_channels, frame->format) < 0)) {
        av_frame_free(&frame); return NULL;
    }
    frame->pts = source ? source->pts + playback_audio_time(ctx, offset) : AV_NOPTS_VALUE;
    return frame;
}

/* Detach the suffix, splitting an audio frame at the sample boundary. Reattaching
 * this list on open failure restores every retained sample exactly once. */
static int playback_overlap_detach(inout_context* ctx, playback_overlap* blend, int stream)
{
    frame_queue* queue = ctx->frame_queues[stream];
    avframe_node** output = &blend->tail[stream];
    int result = 0;
    EnterCriticalSection(&queue->mutex);
    avframe_node** link = &queue->front;
    while (*link) {
        avframe_node* node = *link;
        if (node->frame_id == blend->old_id && node->frame->pts != AV_NOPTS_VALUE) {
            int take = node->frame->pts >= blend->source_start;
            int64_t end = node->frame->pts + (stream ? playback_audio_time(ctx, node->frame->nb_samples) : 0);
            if (stream && node->frame->pts < blend->source_start && end > blend->source_start) {
                int offset = (int)av_rescale_q(blend->source_start - node->frame->pts, UNIVERSAL_TB,
                    (AVRational){1, ctx->output_audio_sample_rate});
                offset = av_clip(offset, 0, node->frame->nb_samples);
                if (offset == 0) take = 1;
                if (offset > 0 && offset < node->frame->nb_samples) {
                    AVFrame* prefix = playback_audio_slice(ctx, node->frame, 0, offset);
                    AVFrame* suffix = playback_audio_slice(ctx, node->frame, offset, node->frame->nb_samples - offset);
                    avframe_node* split = av_mallocz(sizeof(*split));
                    if (!prefix || !suffix || !split) {
                        av_frame_free(&prefix); av_frame_free(&suffix); av_free(split); result = -1; break;
                    }
                    *split = *node; split->frame = suffix;
                    av_frame_free(&node->frame); node->frame = prefix; node->next = split;
                    queue->count++; link = &node->next; node = split; take = 1;
                }
            }
            if (take) {
                *link = node->next; node->next = NULL;
                *output = node; output = &node->next; queue->count--; continue;
            }
            if (!stream) {
                av_frame_free(&blend->leading_video);
                blend->leading_video = av_frame_clone(node->frame);
            }
        }
        link = &node->next;
    }
    queue->rear = queue->front;
    while (queue->rear && queue->rear->next) queue->rear = queue->rear->next;
    LeaveCriticalSection(&queue->mutex); WakeAllConditionVariable(&queue->cond);
    return result;
}

static void playback_overlap_trim_audio(inout_context* ctx, AVFrame** frame, int64_t first_sample)
{
    if (!*frame) return;
    int64_t offset = first_sample - av_rescale_q((*frame)->pts, UNIVERSAL_TB, (AVRational){1,ctx->output_audio_sample_rate});
    if (offset <= 0) return;
    AVFrame* suffix = offset < (*frame)->nb_samples ? playback_audio_slice(ctx, *frame, (int)offset, (*frame)->nb_samples - (int)offset) : NULL;
    av_frame_free(frame); *frame = suffix;
}

static void playback_overlap_restore(inout_context* ctx, playback_overlap* blend)
{
    if (ctx->overlap && ctx->overlap->opening == blend) ctx->overlap->opening = NULL;
    if (blend->audio_bridge_samples > 0) {
        /* Opening can fail after the output has already borrowed old samples.
         * Restore only the unplayed suffix, never replay the borrowed prefix. */
        int64_t first_sample = av_rescale_q(blend->source_start, UNIVERSAL_TB, (AVRational){1,ctx->output_audio_sample_rate}) + blend->audio_bridge_samples;
        avframe_node** link = &blend->tail[1];
        while (*link) {
            avframe_node* node = *link;
            playback_overlap_trim_audio(ctx, &node->frame, first_sample);
            if (!node->frame) { *link = node->next; av_free(node); }
            else link = &node->next;
        }
        playback_overlap_trim_audio(ctx, &blend->fifo_tail, first_sample);
        int64_t timestamp = playback_audio_time(ctx, first_sample);
        while (blend->tail[0] && blend->tail[0]->frame->pts < timestamp) {
            avframe_node* node = blend->tail[0]; blend->tail[0] = node->next;
            av_frame_free(&node->frame); av_free(node);
        }
    }
    for (int i = 0; i < 2; i++) {
        frame_queue* queue = ctx->frame_queues[i];
        EnterCriticalSection(&queue->mutex);
        if (blend->tail[i]) {
            if (queue->rear) queue->rear->next = blend->tail[i]; else queue->front = blend->tail[i];
            queue->rear = blend->tail[i]; queue->count++;
            while (queue->rear->next) { queue->rear = queue->rear->next; queue->count++; }
            blend->tail[i] = NULL;
        }
        LeaveCriticalSection(&queue->mutex); WakeAllConditionVariable(&queue->cond);
    }
    if (blend->fifo_tail) {
        /* The FIFO prefix can drain while the next file is opening. Its tail
         * remains in order before the restored ordinary audio queue. */
        av_audio_fifo_write(ctx->output_audio_fifo, (void**)blend->fifo_tail->extended_data, blend->fifo_tail->nb_samples);
        ctx->output_last_audio_frame_pts = blend->fifo_tail->pts;
        ctx->output_last_audio_nb_samples = blend->fifo_tail->nb_samples;
        av_frame_free(&blend->fifo_tail);
    }
}

static void playback_overlap_replace(inout_context* ctx, int64_t anchor)
{
    playback_overlap** link = ctx->overlap ? &ctx->overlap->pending : NULL;
    while (link && *link) {
        playback_overlap* blend = *link;
        if (blend->new_id > anchor && !blend->started) {
            if (blend->old_id == anchor && !blend->immediate) playback_overlap_restore(ctx, blend);
            *link = blend->next; playback_overlap_free(blend);
        }
        else link = &blend->next;
    }
}

static int64_t playback_overlap_bytes(playback_overlap* blend)
{
    int64_t bytes = 0;
    for (int stream = 0; stream < 2; stream++)
        for (avframe_node* node = blend->tail[stream]; node; node = node->next)
            bytes += stream ? (int64_t)node->frame->nb_samples * node->frame->ch_layout.nb_channels * av_get_bytes_per_sample(node->frame->format) :
                FFMAX(0, av_image_get_buffer_size(node->frame->format, node->frame->width, node->frame->height, 1));
    if (blend->leading_video) bytes += FFMAX(0, av_image_get_buffer_size(blend->leading_video->format, blend->leading_video->width, blend->leading_video->height, 1));
    if (blend->fifo_tail) bytes += (int64_t)blend->fifo_tail->nb_samples * blend->fifo_tail->ch_layout.nb_channels * av_get_bytes_per_sample(blend->fifo_tail->format);
    return bytes;
}

static playback_overlap* playback_overlap_prepare(inout_context* ctx, const char* next, int immediate, int64_t anchor)
{
    double seconds = playback_overlap_seconds(next);
    playback_overlap* blend = NULL;
    if (seconds <= 0 || !playback_overlap_local_file(next) || !playback_overlap_local_file(ctx->input_name) || !ctx->fmt_ctx)
        return NULL;
    EnterCriticalSection(&ctx->input_change_mutex);
    if (!ctx->overlap) ctx->overlap = av_mallocz(sizeof(*ctx->overlap));
    if (!ctx->overlap || !(blend = av_mallocz(sizeof(*blend)))) goto done;
    blend->immediate = immediate;
    blend->old_id = anchor > 0 ? anchor : immediate ? ctx->output_boundary_frame_id : ctx->input_frame_id;
    if (blend->old_id <= 0) goto unavailable;
    blend->duration = av_rescale_q((int64_t)(seconds * AV_TIME_BASE + 0.5), AV_TIME_BASE_Q, UNIVERSAL_TB);
    int image_bytes = av_image_get_buffer_size(ctx->output_frame_format, ctx->output_frame_width, ctx->output_frame_height, 1);
    if (image_bytes > 0) blend->duration = FFMIN(blend->duration,
        ctx->output_time_per_video_frame * FFMAX(1, PLAYBACK_OVERLAP_CACHE_BYTES / image_bytes));
    int64_t audio_first = AV_NOPTS_VALUE;
    int64_t first = AV_NOPTS_VALUE, end = AV_NOPTS_VALUE, audio_end = AV_NOPTS_VALUE, previous = AV_NOPTS_VALUE, interval = ctx->output_time_per_video_frame;
    for (int stream = 0; stream < 2; stream++) {
        frame_queue* queue = ctx->frame_queues[stream];
        EnterCriticalSection(&queue->mutex);
        for (avframe_node* node = queue->front; node; node = node->next) {
            if (node->frame_id != blend->old_id || node->frame->pts == AV_NOPTS_VALUE) continue;
            /* Old audio still fades out when the target has no audio track. */
            blend->media_mask |= node->media_mask & INPUT_MEDIA_AUDIO;
            if (first == AV_NOPTS_VALUE || node->frame->pts < first) first = node->frame->pts;
            int64_t node_end;
            if (stream) {
                if (audio_first == AV_NOPTS_VALUE) audio_first = node->frame->pts;
                node_end = node->frame->pts + playback_audio_time(ctx, node->frame->nb_samples); audio_end = node_end;
            }
            else {
                if (previous != AV_NOPTS_VALUE && node->frame->pts > previous) interval = node->frame->pts - previous;
                previous = node->frame->pts; node_end = node->frame->pts + interval;
            }
            end = end == AV_NOPTS_VALUE ? node_end : FFMAX(end, node_end);
        }
        LeaveCriticalSection(&queue->mutex);
    }
    /* Timestamp rounding can put video one clock tick past the exact audio
     * boundary. Snap that sub-sample difference before splitting either queue. */
    if (audio_end != AV_NOPTS_VALUE && end >= audio_end && end - audio_end <= playback_audio_time(ctx, 1)) end = audio_end;
    if (immediate) {
        if (first == AV_NOPTS_VALUE) first = 0;
        blend->source_start = first;
        if (ctx->overlap->last_video) blend->leading_video = av_frame_clone(ctx->overlap->last_video);
        /* PLAY takes only the next interval from the actual visible batch;
         * an already decoded standby is never used as its old side. */
        for (int stream = 0; stream < 2; stream++) {
            avframe_node** tail = &blend->tail[stream];
            frame_queue* queue = ctx->frame_queues[stream];
            EnterCriticalSection(&queue->mutex);
            for (avframe_node* node = queue->front; node; node = node->next) {
                if (node->frame_id != blend->old_id || node->frame->pts == AV_NOPTS_VALUE ||
                    node->frame->pts >= first + blend->duration + playback_audio_time(ctx, ctx->output_audio_nb_samples)) continue;
                avframe_node* copy = av_mallocz(sizeof(*copy));
                if (!copy) break;
                *copy = *node; copy->next = NULL; copy->frame = av_frame_clone(node->frame);
                if (!copy->frame) { av_free(copy); break; }
                *tail = copy; tail = &copy->next;
            }
            LeaveCriticalSection(&queue->mutex);
        }
    }
    else {
        if (first == AV_NOPTS_VALUE || end <= first + ctx->output_time_per_video_frame) goto unavailable;
        /* Leave a complete output tick so tiny, prefetched inputs cannot vanish
         * between two neighboring overlaps. Short tails shorten the blend. */
        blend->duration = FFMIN(blend->duration, (end - first) / 2);
        if (blend->duration < ctx->output_time_per_video_frame) goto unavailable;
        blend->source_start = end - blend->duration;
        /* Leave an integral number of ordinary output blocks before the cache.
         * Otherwise the boundary reset discards a partial old FIFO block. Keep
         * this extra (at most one block) tail in the independent cache. */
        int count = av_audio_fifo_size(ctx->output_audio_fifo);
        if (ctx->output_current_audio_frame_id == blend->old_id && count > 0 && ctx->output_last_audio_frame_pts != AV_NOPTS_VALUE)
            audio_first = ctx->output_last_audio_frame_pts + playback_audio_time(ctx, ctx->output_last_audio_nb_samples) - playback_audio_time(ctx, count);
        if (audio_first != AV_NOPTS_VALUE && ctx->output_audio_nb_samples > 0) {
            AVRational tb = {1, ctx->output_audio_sample_rate};
            int64_t head = av_rescale_q(audio_first, UNIVERSAL_TB, tb);
            int64_t cut = av_rescale_q(blend->source_start, UNIVERSAL_TB, tb);
            if (cut > head) {
                cut = head + ((cut - head) / ctx->output_audio_nb_samples) * ctx->output_audio_nb_samples;
                int64_t aligned = playback_audio_time(ctx, cut);
                if (aligned < first + ctx->output_time_per_video_frame) goto unavailable;
                blend->source_start = aligned;
            }
        }
        if (playback_overlap_detach(ctx, blend, 0) < 0 || playback_overlap_detach(ctx, blend, 1) < 0) {
            playback_overlap_restore(ctx, blend); goto unavailable;
        }
    }
    /* Retain the old FIFO's future samples separately before boundary reset.
     * For a natural handoff, only remove its suffix inside the overlap window. */
    if (ctx->output_current_audio_frame_id == blend->old_id && ctx->output_last_audio_frame_pts != AV_NOPTS_VALUE) {
        int count = av_audio_fifo_size(ctx->output_audio_fifo);
        if (count) blend->media_mask |= INPUT_MEDIA_AUDIO;
        int64_t fifo_end = ctx->output_last_audio_frame_pts + playback_audio_time(ctx, ctx->output_last_audio_nb_samples);
        int64_t fifo_start = fifo_end - playback_audio_time(ctx, count);
        int skip = immediate ? 0 : (int)av_rescale_q(blend->source_start - fifo_start, UNIVERSAL_TB, (AVRational){1, ctx->output_audio_sample_rate});
        skip = av_clip(skip, 0, count);
        if (count > skip) {
            AVFrame* all = playback_audio_slice(ctx, NULL, 0, count);
            if (all && av_audio_fifo_peek(ctx->output_audio_fifo, (void**)all->extended_data, count) == count) {
                all->pts = fifo_start;
                blend->fifo_tail = playback_audio_slice(ctx, all, skip, count - skip);
                if (blend->fifo_tail && !immediate) {
                    av_audio_fifo_reset(ctx->output_audio_fifo);
                    if (skip) av_audio_fifo_write(ctx->output_audio_fifo, (void**)all->extended_data, skip);
                    ctx->output_last_audio_frame_pts = fifo_start;
                    ctx->output_last_audio_nb_samples = skip;
                }
                if (immediate && blend->fifo_tail) blend->source_start = fifo_start;
            }
            av_frame_free(&all);
        }
    }
    if (!blend->leading_video && !blend->tail[0] && !blend->tail[1] && !blend->fifo_tail) goto unavailable;
    goto done;
unavailable:
    playback_overlap_free(blend); blend = NULL;
done:
    LeaveCriticalSection(&ctx->input_change_mutex);
    return blend;
}

static void playback_overlap_commit(inout_context* ctx, playback_overlap* blend)
{
    if (!blend) return;
    EnterCriticalSection(&ctx->input_change_mutex);
    if (ctx->overlap->opening == blend) ctx->overlap->opening = NULL;
    blend->new_id = ctx->input_frame_id;
    blend->media_mask |= ctx->input_media_mask;
    /* A target shorter than the retained tail uses a regular handoff. This
     * preserves its first/last picture and every old tail sample. */
    int64_t length = ctx->fmt_ctx && ctx->fmt_ctx->duration > 0 ? universal_from_av_time_base(ctx->fmt_ctx->duration) : 0;
    if (length && length < blend->duration * 2) {
        if (!blend->immediate) playback_overlap_restore(ctx, blend);
        playback_overlap_free(blend);
    }
    else {
        playback_overlap** link = &ctx->overlap->pending;
        int count = 0;
        int64_t bytes = playback_overlap_bytes(blend);
        while (*link) { bytes += playback_overlap_bytes(*link); link = &(*link)->next; count++; }
        if (count >= 32 || bytes > PLAYBACK_OVERLAP_CACHE_BYTES) {
            if (!blend->immediate) playback_overlap_restore(ctx, blend);
            playback_overlap_free(blend);
        }
        else { *link = blend; av_log(NULL, AV_LOG_INFO, "overlap prepared old=%I64d new=%I64d duration_ms=%I64d\n",
            blend->old_id, blend->new_id, av_rescale_q(blend->duration, UNIVERSAL_TB, (AVRational){1,1000})); }
    }
    LeaveCriticalSection(&ctx->input_change_mutex);
}

static playback_overlap* playback_overlap_current(inout_context* ctx)
{
    if (ctx->overlap) for (playback_overlap* blend = ctx->overlap->pending; blend; blend = blend->next)
        if (blend->started && blend->new_id == ctx->output_boundary_frame_id) return blend;
    return NULL;
}

static int playback_overlap_boundary(inout_context* ctx, int64_t new_id, int64_t ended_id)
{
    playback_overlap** link = ctx->overlap ? &ctx->overlap->pending : NULL;
    int deferred = 0;
    while (link && *link) {
        playback_overlap* blend = *link;
        if (blend->new_id < new_id) {
            if (blend->deferred_end && ctx->input_ended_callback) ctx->input_ended_callback(ctx->input_ended_private, blend->deferred_end);
            *link = blend->next; playback_overlap_free(blend); continue;
        }
        if (blend->new_id == new_id) {
            blend->started = 1; blend->output_start = blend->media_mask & INPUT_MEDIA_AUDIO ?
                FFMIN(ctx->output_video_pts_time, ctx->output_audio_pts_time) : ctx->output_video_pts_time;
            blend->audio_start_sample = ctx->output_sample_count;
            AVRational sample_tb = {1, ctx->output_audio_sample_rate};
            blend->audio_duration_samples = FFMAX(1, av_rescale_q(blend->duration, UNIVERSAL_TB, sample_tb));
            int64_t end_sample = AV_NOPTS_VALUE;
            for (avframe_node* audio = blend->tail[1]; audio; audio = audio->next)
                end_sample = av_rescale_q(audio->frame->pts, UNIVERSAL_TB, sample_tb) + audio->frame->nb_samples;
            if (blend->fifo_tail) end_sample = FFMAX(end_sample, av_rescale_q(blend->fifo_tail->pts, UNIVERSAL_TB, sample_tb) + blend->fifo_tail->nb_samples);
            if (end_sample != AV_NOPTS_VALUE) {
                int64_t available = end_sample - av_rescale_q(blend->source_start, UNIVERSAL_TB, sample_tb) - blend->audio_bridge_samples;
                if (available > 0) blend->audio_duration_samples = FFMIN(blend->audio_duration_samples, available);
            }
            blend->video_done = !(blend->media_mask & INPUT_MEDIA_VIDEO);
            blend->audio_done = !(blend->media_mask & INPUT_MEDIA_AUDIO);
            blend->deferred_end = ended_id; deferred = ended_id > 0;
        }
        link = &blend->next;
    }
    return deferred;
}

static void playback_overlap_finish(inout_context* ctx)
{
    playback_overlap** link = ctx->overlap ? &ctx->overlap->pending : NULL;
    while (link && *link) {
        playback_overlap* blend = *link;
        if (blend->started && blend->video_done && blend->audio_done) {
            if (blend->deferred_end && ctx->input_ended_callback) ctx->input_ended_callback(ctx->input_ended_private, blend->deferred_end);
            *link = blend->next; playback_overlap_free(blend);
        }
        else link = &blend->next;
    }
}

static void playback_overlap_pause_clock(inout_context* ctx)
{
    playback_overlap_state* state = ctx->overlap;
    if (!state) return;
    if (ctx->output_paused && !state->paused) {
        state->paused = 1; state->paused_at = ctx->output_video_pts_time;
        state->paused_sample = ctx->output_sample_count;
    }
    else if (!ctx->output_paused && state->paused) {
        int64_t duration = FFMAX(0, ctx->output_video_pts_time - state->paused_at);
        for (playback_overlap* blend = state->pending; blend; blend = blend->next)
            if (blend->started) {
                blend->output_start += duration;
                blend->audio_start_sample += FFMAX(0, ctx->output_sample_count - state->paused_sample);
            }
        state->paused = 0;
    }
}

static AVFrame* playback_overlap_video(inout_context* ctx, AVFrame* current)
{
    playback_overlap_pause_clock(ctx);
    playback_overlap* blend = playback_overlap_current(ctx);
    if (ctx->output_paused) {
        AVFrame* held = blend && ctx->overlap->last_video ? av_frame_clone(ctx->overlap->last_video) : NULL;
        return held ? held : current;
    }
    AVFrame* output = current;
    if (blend && !blend->video_done) {
        int64_t elapsed = FFMAX(0, ctx->output_video_pts_time - blend->output_start);
        AVFrame* old = blend->leading_video;
        for (avframe_node* node = blend->tail[0]; node; node = node->next) {
            if (node->frame->pts <= blend->source_start + elapsed || !old) old = node->frame;
            else break;
        }
        if (elapsed < blend->duration && old && old->format == current->format && old->width == current->width && old->height == current->height) {
            /* The camera callback consumes data[0] as one tightly packed image.
             * av_frame_make_writable may reallocate with padded rows/planes, so
             * allocate through the same layout helper as fill_output_video. */
            AVFrame* mixed = av_frame_alloc();
            if (mixed) {
                mixed->width = current->width; mixed->height = current->height; mixed->format = current->format;
                if (get_video_buffer(mixed) < 0 || av_frame_copy_props(mixed, current) < 0) av_frame_free(&mixed);
            }
            if (mixed) {
                int weight = (int)av_rescale(FFMIN(blend->duration, elapsed + ctx->output_time_per_video_frame), 65536, blend->duration);
                const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(current->format);
                for (int plane = 0; plane < 4 && mixed->data[plane]; plane++) {
                    /* Blend active row bytes only; each frame has its own signed stride. */
                    int width = av_image_get_linesize(current->format, current->width, plane);
                    int height = plane == 1 || plane == 2 ? AV_CEIL_RSHIFT(current->height, desc->log2_chroma_h) : current->height;
                    for (int y = 0; y < height; y++) {
                        uint8_t* dest = mixed->data[plane] + y * mixed->linesize[plane];
                        const uint8_t* from = old->data[plane] + y * old->linesize[plane];
                        const uint8_t* to = current->data[plane] + y * current->linesize[plane];
                        for (int x = 0; x < width; x++) dest[x] = (uint8_t)((from[x] * (65536 - weight) + to[x] * weight + 32768) >> 16);
                    }
                }
                output = mixed;
            }
        }
        if (elapsed + ctx->output_time_per_video_frame >= blend->duration) blend->video_done = 1;
    }
    if (!ctx->overlap) ctx->overlap = av_mallocz(sizeof(*ctx->overlap));
    if (ctx->overlap) { av_frame_free(&ctx->overlap->last_video); ctx->overlap->last_video = av_frame_clone(output); }
    return output;
}

static uint8_t* playback_audio_sample(AVFrame* frame, int sample, int channel)
{
    int planar = av_sample_fmt_is_planar(frame->format);
    int bytes = av_get_bytes_per_sample(frame->format);
    return frame->extended_data[planar ? channel : 0] + (size_t)(planar ? sample : sample * frame->ch_layout.nb_channels + channel) * bytes;
}

static double playback_read_sample(uint8_t* data, enum AVSampleFormat format)
{
    switch (av_get_packed_sample_fmt(format)) {
    case AV_SAMPLE_FMT_U8: return (double)*data - 128;
    case AV_SAMPLE_FMT_S16: return *(int16_t*)data;
    case AV_SAMPLE_FMT_S32: return *(int32_t*)data;
    case AV_SAMPLE_FMT_S64: return (double)*(int64_t*)data;
    case AV_SAMPLE_FMT_FLT: return *(float*)data;
    case AV_SAMPLE_FMT_DBL: return *(double*)data;
    default: return 0;
    }
}

static void playback_write_sample(uint8_t* data, enum AVSampleFormat format, double value)
{
    switch (av_get_packed_sample_fmt(format)) {
    case AV_SAMPLE_FMT_U8: *data = (uint8_t)av_clip((int)(value + 128), 0, 255); break;
    case AV_SAMPLE_FMT_S16: *(int16_t*)data = (int16_t)av_clip((int)value, -32768, 32767); break;
    case AV_SAMPLE_FMT_S32: *(int32_t*)data = (int32_t)FFMAX(-2147483648.0, FFMIN(2147483647.0, value)); break;
    case AV_SAMPLE_FMT_S64: *(int64_t*)data = (int64_t)FFMAX(-9223372036854775808.0, FFMIN(9223372036854774784.0, value)); break;
    case AV_SAMPLE_FMT_FLT: *(float*)data = (float)value; break;
    case AV_SAMPLE_FMT_DBL: *(double*)data = value; break;
    default: break;
    }
}

/* Both bridge and fade use the same sample-domain packet lookup. */
static AVFrame* playback_overlap_old_audio(inout_context* ctx, playback_overlap* blend, avframe_node** cursor,
    int64_t sample, int64_t* index)
{
    AVRational tb = {1, ctx->output_audio_sample_rate};
    while (*cursor && av_rescale_q((*cursor)->frame->pts, UNIVERSAL_TB, tb) + (*cursor)->frame->nb_samples <= sample)
        *cursor = (*cursor)->next;
    AVFrame* old = *cursor ? (*cursor)->frame : NULL;
    if (blend->fifo_tail) {
        int64_t first = av_rescale_q(blend->fifo_tail->pts, UNIVERSAL_TB, tb);
        if (sample >= first && sample < first + blend->fifo_tail->nb_samples) old = blend->fifo_tail;
    }
    *index = old ? sample - av_rescale_q(old->pts, UNIVERSAL_TB, tb) : -1;
    return old && *index >= 0 && *index < old->nb_samples ? old : NULL;
}

/* Continue the retained old waveform while the replacement's first audio/video
 * pair is becoming ready. A silent output block here would click twice. */
static AVFrame* playback_overlap_audio_gap(inout_context* ctx, AVFrame* silence)
{
    if (!ctx->overlap || ctx->output_paused || playback_overlap_current(ctx)) return NULL;
    playback_overlap* blend = ctx->overlap->opening;
    if (!blend) for (playback_overlap* candidate = ctx->overlap->pending; candidate; candidate = candidate->next)
        if (!candidate->started && candidate->old_id == ctx->output_boundary_frame_id) { blend = candidate; break; }
    if (!blend || blend->old_id != ctx->output_boundary_frame_id || !(blend->media_mask & INPUT_MEDIA_AUDIO)) return NULL;
    /* A genuine timed gap inside the old input must stay silent. Borrow its
     * retained tail only after all ordinary old audio has been consumed. */
    if (ctx->output_current_audio_frame_id == blend->old_id && av_audio_fifo_size(ctx->output_audio_fifo) > 0) return NULL;
    frame_queue* queue = ctx->frame_queues[1];
    int old_pending = 0;
    EnterCriticalSection(&queue->mutex);
    for (avframe_node* node = queue->front; node; node = node->next)
        if (node->frame_id == blend->old_id) { old_pending = 1; break; }
    LeaveCriticalSection(&queue->mutex);
    if (old_pending) return NULL;
    AVFrame* frame = playback_audio_slice(ctx, NULL, 0, silence->nb_samples);
    if (!frame) return NULL;
    av_samples_set_silence(frame->extended_data, 0, frame->nb_samples, frame->ch_layout.nb_channels, frame->format);
    int64_t source = av_rescale_q(blend->source_start, UNIVERSAL_TB, (AVRational){1,ctx->output_audio_sample_rate}) + blend->audio_bridge_samples;
    avframe_node* cursor = blend->tail[1];
    for (int i = 0; i < frame->nb_samples; i++) {
        int64_t index;
        AVFrame* old = playback_overlap_old_audio(ctx, blend, &cursor, source + i, &index);
        if (old) for (int channel = 0; channel < frame->ch_layout.nb_channels; channel++)
            memcpy(playback_audio_sample(frame, i, channel), playback_audio_sample(old, (int)index, channel), av_get_bytes_per_sample(frame->format));
    }
    blend->audio_bridge_samples += frame->nb_samples;
    return frame;
}

static AVFrame* playback_overlap_audio(inout_context* ctx, AVFrame* current)
{
    playback_overlap_pause_clock(ctx);
    if (ctx->output_paused) return current;
    playback_overlap* blend = playback_overlap_current(ctx);
    if (!blend || blend->audio_done) return current;
    /* Choose both packet and offset on one integer sample clock. Rounding
     * time differences separately can produce index == nb_samples while the
     * packet end timestamp is still ahead, inserting an isolated zero sample. */
    AVRational sample_tb = {1, ctx->output_audio_sample_rate};
    int64_t elapsed = ctx->output_sample_count - blend->audio_start_sample;
    int64_t duration = blend->audio_duration_samples > 0 ? blend->audio_duration_samples : FFMAX(1, av_rescale_q(blend->duration, UNIVERSAL_TB, sample_tb));
    int64_t source_start = av_rescale_q(blend->source_start, UNIVERSAL_TB, sample_tb) + blend->audio_bridge_samples;
    AVFrame* mixed = av_frame_clone(current);
    if (!mixed || av_frame_make_writable(mixed) < 0) { av_frame_free(&mixed); return current; }
    avframe_node* node = blend->tail[1];
    for (int sample = 0; sample < current->nb_samples; sample++) {
        int64_t position = elapsed + sample;
        if (position < 0 || position >= duration) continue;
        int64_t source_sample = source_start + position;
        int64_t index;
        AVFrame* old = playback_overlap_old_audio(ctx, blend, &node, source_sample, &index);
        double weight = (double)(position + 1) / duration;
        for (int channel = 0; channel < mixed->ch_layout.nb_channels; channel++) {
            uint8_t* dest = playback_audio_sample(mixed, sample, channel);
            double previous = old && index >= 0 && index < old->nb_samples ? playback_read_sample(playback_audio_sample(old, (int)index, channel), old->format) : 0;
            playback_write_sample(dest, mixed->format, previous * (1.0 - weight) + playback_read_sample(dest, mixed->format) * weight);
        }
    }
    if (elapsed + current->nb_samples >= duration) blend->audio_done = 1;
    return mixed;
}
