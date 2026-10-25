/* Included by the native boundary fixture: actual queue and output functions. */
static int overlap_ended_count;
static void overlap_test_ended(void* priv, int64_t id)
{
    check(id == 100, "Overlap end retains the old playback invocation"); overlap_ended_count++;
}

static inout_context* overlap_test_context(enum AVPixelFormat format)
{
    AVChannelLayout mono = AV_CHANNEL_LAYOUT_MONO;
    inout_context* ctx = inout_context_alloc(16, 16, format, (AVRational){60,1},
        48000, AV_SAMPLE_FMT_S16, &mono, 480, 1000000, 1048576, 100, 256, 256, 1, 5, 1000000);
    check(ctx != NULL, "Allocate overlap context");
    ctx->input_frame_id = ctx->output_boundary_frame_id = ctx->output_current_video_frame_id = ctx->output_current_audio_frame_id = 100;
    ctx->input_media_mask = INPUT_MEDIA_VIDEO | INPUT_MEDIA_AUDIO;
    ctx->input_start_time = AV_NOPTS_VALUE;
    ctx->input_name = av_strdup("old.avi"); ctx->fmt_ctx = avformat_alloc_context(); ctx->fmt_ctx->duration = 2 * AV_TIME_BASE;
    return ctx;
}

static AVFrame* overlap_test_picture(inout_context* ctx, int marker)
{
    AVFrame* frame = av_frame_alloc();
    frame->width = ctx->output_frame_width; frame->height = ctx->output_frame_height; frame->format = ctx->output_frame_format;
    check(av_frame_get_buffer(frame, 32) >= 0, "Allocate blend picture");
    const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(frame->format);
    for (int plane = 0; plane < 4 && frame->data[plane]; plane++) {
        int height = plane == 1 || plane == 2 ? AV_CEIL_RSHIFT(frame->height, desc->log2_chroma_h) : frame->height;
        for (int row = 0; row < height; row++) memset(frame->data[plane] + row * frame->linesize[plane], marker,
            av_image_get_linesize(frame->format, frame->width, plane));
    }
    return frame;
}

static AVFrame* overlap_test_sound(inout_context* ctx, int samples, int value)
{
    AVFrame* frame = playback_audio_slice(ctx, NULL, 0, samples);
    check(frame != NULL, "Allocate blend sound");
    for (int i = 0; i < samples; i++) ((int16_t*)frame->data[0])[i] = (int16_t)value;
    return frame;
}

static void overlap_test_enqueue(inout_context* ctx)
{
    for (int i = 0; i < 30; i++) {
        AVFrame* frame = overlap_test_picture(ctx, 20);
        frame->pts = av_rescale_q(i, (AVRational){1,30}, UNIVERSAL_TB);
        check(frame_enqueue(ctx->frame_queues[0], frame, -1, 100, ctx->input_media_mask, AV_NOPTS_VALUE, &ctx->force_exit) >= 0, "Queue 30fps source for 60fps blending");
        av_frame_free(&frame);
    }
    for (int i = 0; i < 100; i++) {
        AVFrame* frame = overlap_test_sound(ctx, 480, 1000); frame->pts = playback_audio_time(ctx, i * 480);
        check(frame_enqueue(ctx->frame_queues[1], frame, -1, 100, ctx->input_media_mask, AV_NOPTS_VALUE, &ctx->force_exit) >= 0, "Queue old audio");
        av_frame_free(&frame);
    }
}

static int overlap_queue_samples(frame_queue* queue)
{
    int count = 0;
    for (avframe_node* node = queue->front; node; node = node->next) count += node->frame->nb_samples;
    return count;
}

/* Deliberately use non-aligned row widths and independently owned planes.
 * Different padding and signs must not change logical pixel coordinates. */
static AVFrame* overlap_stride_picture(enum AVPixelFormat format, int marker, int padding, int inverted)
{
    AVFrame* frame = av_frame_alloc();
    check(frame != NULL, "Allocate stride test frame");
    frame->width = 18; frame->height = 10; frame->format = format;
    const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(format);
    for (int plane = 0; plane < 4; plane++) {
        int bytes = av_image_get_linesize(format, frame->width, plane);
        if (bytes <= 0) break;
        int rows = plane == 1 || plane == 2 ? AV_CEIL_RSHIFT(frame->height, desc->log2_chroma_h) : frame->height;
        int stride = bytes + padding + plane * 3;
        frame->buf[plane] = av_buffer_alloc(stride * rows);
        check(frame->buf[plane] != NULL, "Allocate padded plane");
        memset(frame->buf[plane]->data, 0xE7, stride * rows);
        frame->linesize[plane] = inverted ? -stride : stride;
        frame->data[plane] = frame->buf[plane]->data + (inverted ? stride * (rows - 1) : 0);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < bytes; x++)
                frame->data[plane][y * frame->linesize[plane] + x] = (uint8_t)(marker + plane * 5 + y * 2 + x % 3);
    }
    return frame;
}

static void overlap_check_stride_pixels(AVFrame* frame, int marker, int check_padding)
{
    const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(frame->format);
    for (int plane = 0; plane < 4 && frame->data[plane]; plane++) {
        int bytes = av_image_get_linesize(frame->format, frame->width, plane);
        int rows = plane == 1 || plane == 2 ? AV_CEIL_RSHIFT(frame->height, desc->log2_chroma_h) : frame->height;
        for (int y = 0; y < rows; y++) {
            const uint8_t* row = frame->data[plane] + y * frame->linesize[plane];
            for (int x = 0; x < bytes; x++)
                check(row[x] == marker + plane * 5 + y * 2 + x % 3, "Every blended pixel respects its own signed plane stride");
            if (check_padding) for (int x = bytes; x < abs(frame->linesize[plane]); x++)
                check(row[x] == 0xE7, "Source padding is unchanged by blending");
        }
    }
}

static void check_overlap_strides(void)
{
    enum AVPixelFormat formats[] = { AV_PIX_FMT_BGR24, AV_PIX_FMT_RGB32, AV_PIX_FMT_YUYV422, AV_PIX_FMT_YUV420P, AV_PIX_FMT_NV12 };
    for (int f = 0; f < ARRAY_ELEMS(formats); f++) for (int signs = 0; signs < 4; signs++) {
        inout_context* ctx = overlap_test_context(formats[f]);
        AVFrame* old = overlap_stride_picture(formats[f], 20, 7, signs & 1);
        AVFrame* current = overlap_stride_picture(formats[f], 180, 37, signs & 2);
        check(abs(old->linesize[0]) != abs(current->linesize[0]), "Old and new source strides intentionally differ");
        ctx->overlap = av_mallocz(sizeof(*ctx->overlap));
        check(ctx->overlap != NULL, "Allocate stride test overlap state");
        playback_overlap* blend = av_mallocz(sizeof(*blend));
        check(blend != NULL, "Allocate stride test overlap");
        ctx->overlap->pending = blend;
        blend->leading_video = old; blend->started = 1; blend->new_id = ctx->output_boundary_frame_id;
        blend->duration = 2 * ctx->output_time_per_video_frame;
        AVFrame* mixed = playback_overlap_video(ctx, current);
        check(mixed != current, "Stride case produces a separate blended frame");
        /* PushVideoFrameHelper reads data[0] as one align=1 image. Verify that
         * contract as well as the individual plane rows used by the blender. */
        check(avframe_is_data_continuous(mixed), "Camera callback requires tightly packed contiguous blended planes");
        int offset = 0;
        const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(mixed->format);
        for (int plane = 0; plane < 4 && mixed->data[plane]; plane++) {
            int bytes = av_image_get_linesize(mixed->format, mixed->width, plane);
            int rows = plane == 1 || plane == 2 ? AV_CEIL_RSHIFT(mixed->height, desc->log2_chroma_h) : mixed->height;
            for (int y = 0; y < rows; y++) for (int x = 0; x < bytes; x++)
                check(mixed->data[0][offset++] == 100 + plane * 5 + y * 2 + x % 3,
                    "Flat camera read sees every row and chroma plane at its expected offset");
        }
        check(offset == av_image_get_buffer_size(mixed->format, mixed->width, mixed->height, 1), "Camera read covers exactly the complete image");
        overlap_check_stride_pixels(mixed, 100, 0);
        overlap_check_stride_pixels(old, 20, 1);
        overlap_check_stride_pixels(current, 180, 1);
        av_frame_free(&mixed); av_frame_free(&current); inout_context_free(&ctx);
    }
    puts("PASS: 20 stride cases across packed/planar formats, unequal per-plane padding, positive/negative source strides, every active pixel and unchanged source padding.");
}

static void check_overlap_audio_waveform(int duration_samples)
{
    inout_context* ctx = overlap_test_context(AV_PIX_FMT_BGR24);
    ctx->overlap = av_mallocz(sizeof(*ctx->overlap));
    playback_overlap* blend = av_mallocz(sizeof(*blend));
    check(ctx->overlap && blend, "Allocate waveform overlap");
    ctx->overlap->pending = blend; blend->new_id = ctx->output_boundary_frame_id; blend->started = 1;
    blend->source_start = playback_audio_time(ctx, 100) + playback_audio_time(ctx, 1) / 2 + 1;
    blend->duration = playback_audio_time(ctx, duration_samples); blend->video_done = 1;
    avframe_node** tail = &blend->tail[1];
    for (int block = 0; block < (duration_samples + 2047) / 1024; block++) {
        avframe_node* node = av_mallocz(sizeof(*node)); node->frame = overlap_test_sound(ctx, 1024, 0);
        node->frame->pts = playback_audio_time(ctx, block * 1024);
        for (int i = 0; i < 1024; i++) ((int16_t*)node->frame->data[0])[i] = (int16_t)(10000 * sin(2 * 3.141592653589793 * 997 * (block * 1024 + i) / 48000));
        *tail = node; tail = &node->next;
    }
    int64_t start_sample = av_rescale_q(blend->source_start, UNIVERSAL_TB, (AVRational){1,48000});
    for (int block = 0; block < duration_samples / 1024; block++) {
        ctx->output_sample_count = block * 1024; ctx->output_audio_pts_time = playback_audio_time(ctx, ctx->output_sample_count);
        AVFrame* current = overlap_test_sound(ctx, 1024, 0);
        AVFrame* mixed = playback_overlap_audio(ctx, current);
        for (int i = 0; i < 1024; i++) {
            int sample = block * 1024 + i;
            double old = (int16_t)(10000 * sin(2 * 3.141592653589793 * 997 * (start_sample + sample) / 48000));
            int expected = (int)(old * (1.0 - (double)(sample + 1) / duration_samples));
            int actual = ((int16_t*)mixed->data[0])[i];
            if (abs(actual - expected) > 2) fprintf(stderr, "Waveform mismatch at %d: expected=%d actual=%d\n", sample, expected, actual);
            check(abs(actual - expected) <= 2, "Audio cache packet boundaries preserve every waveform sample without silence or repetition");
        }
        if (mixed != current) av_frame_free(&mixed);
        av_frame_free(&current);
    }
    inout_context_free(&ctx);
    puts("PASS: continuous sine samples across independently timestamped cached audio packets.");
}

static void check_resampled_audio_clock(void)
{
    inout_context* ctx = overlap_test_context(AV_PIX_FMT_BGR24);
    int64_t total = 0;
    for (int block = 0; block < 1000; block++) {
        AVFrame* frame = av_frame_alloc();
        frame->format = AV_SAMPLE_FMT_S16; frame->sample_rate = 44100; frame->nb_samples = 1024;
        av_channel_layout_default(&frame->ch_layout, 1);
        check(av_frame_get_buffer(frame, 1) >= 0, "Allocate resampler clock source");
        memset(frame->data[0], 0, frame->linesize[0]);
        frame->pts = av_rescale_q((int64_t)block * 1024, (AVRational){1,44100}, UNIVERSAL_TB);
        check(fill_output_audio(ctx, frame) >= 0, "Convert packet for continuous audio clock");
        av_frame_free(&frame);
        avframe_node* node = ctx->frame_queues[1]->front;
        if (node) {
            check(node->frame->pts == playback_audio_time(ctx, total), "Resampled packet PTS derives from total samples without accumulated rounding drift");
            total += node->frame->nb_samples;
        }
        frame_queue_clean(ctx->frame_queues[1]);
    }
    inout_context_free(&ctx);
    puts("PASS: 1000 resampled packets retain exact sample-clock timestamps.");
}

static void check_overlap_audio_bridge(void)
{
    inout_context* ctx = overlap_test_context(AV_PIX_FMT_BGR24); overlap_test_enqueue(ctx);
    playback_overlap* blend = playback_overlap_prepare(ctx, "new.avi", 0, 0);
    check(blend != NULL, "Prepare audio bridge"); ctx->overlap->opening = blend;
    AVFrame* silence = overlap_test_sound(ctx, 480, 0);
    check(!playback_overlap_audio_gap(ctx, silence), "Timed silence cannot borrow a tail before the old input's ordinary audio drains");
    frame_queue_clean(ctx->frame_queues[0]); frame_queue_clean(ctx->frame_queues[1]);
    ctx->output_paused = 1;
    check(!playback_overlap_audio_gap(ctx, silence) && blend->audio_bridge_samples == 0, "Pause neither outputs nor consumes a bridge");
    ctx->output_paused = 0;
    for (int block = 0; block < 2; block++) {
        AVFrame* bridge = playback_overlap_audio_gap(ctx, silence);
        check(bridge && ((int16_t*)bridge->data[0])[0] == 1000 && ((int16_t*)bridge->data[0])[479] == 1000,
            "Retained old audio fills a replacement readiness gap without a silent packet");
        av_frame_free(&bridge);
    }
    check(blend->audio_bridge_samples == 960, "Bridge tracks exactly the samples already sent to output");
    playback_overlap_restore(ctx, blend);
    check(ctx->overlap->opening == NULL && overlap_queue_samples(ctx->frame_queues[1]) == 3840 && ctx->frame_queues[1]->front->frame->pts == 9200000,
        "Failed replacement restores only the unplayed tail after an audio bridge");
    playback_overlap_free(blend); av_frame_free(&silence); inout_context_free(&ctx);
    puts("PASS: gap bridging, preserved timed silence/pause and failure rollback without replayed samples.");
}

static void check_playback_overlap(void)
{
    int log_level = av_log_get_level(); av_log_set_level(AV_LOG_ERROR);
    check_overlap_strides();
    check_overlap_audio_waveform(4800);
    check_overlap_audio_waveform(72000);
    check_resampled_audio_clock();
    check_overlap_audio_bridge();
    check(playback_overlap_seconds("a.avi") == 0.1 && playback_overlap_seconds("a.avi\toverlap=0") == 0 &&
        playback_overlap_seconds("a.avi\toverlap=0.375,loop=1") == 0.375, "Default, disabled and configured overlap seconds");
    check(playback_overlap_seconds("a\toverlap=nan") < 0 && playback_overlap_seconds("a\toverlap=-1") < 0 &&
        playback_overlap_seconds("a\toverlap=5.01") < 0 && playback_overlap_seconds("a\toverlap=bad") < 0,
        "Overlap options reject non-finite, negative, excessive and invalid values");
    check(playback_overlap_local_file("C:\\media\\a.mp4\tvideo_filter=scale=32:32") && playback_overlap_local_file("relative.avi") &&
        !playback_overlap_local_file("rtsp://camera") && !playback_overlap_local_file("<TESTCARDCAL>"), "Only paths, including paths with filter options, enable overlap");
    enum AVPixelFormat formats[] = { AV_PIX_FMT_BGR24, AV_PIX_FMT_RGB32, AV_PIX_FMT_YUYV422, AV_PIX_FMT_YUV420P, AV_PIX_FMT_NV12 };
    for (int f = 0; f < ARRAY_ELEMS(formats); f++) {
        inout_context* ctx = overlap_test_context(formats[f]); overlap_test_enqueue(ctx);
        playback_overlap* blend = playback_overlap_prepare(ctx, "new.avi\toverlap=0.1", 0, 0);
        check(blend && blend->duration == 1000000 && ctx->frame_queues[0]->count == 27 && overlap_queue_samples(ctx->frame_queues[1]) == 43200,
            "Natural overlap detaches only the final 100ms of differently paced source frames and samples");
        check(blend->tail[1]->frame->pts == 9000000 && blend->tail[1]->frame->nb_samples == 480, "Audio tail starts at the shared source-time boundary");
        ctx->input_frame_id = 200; playback_overlap_commit(ctx, blend); ctx->output_boundary_frame_id = 200;
        ctx->input_ended_callback = overlap_test_ended; overlap_ended_count = 0;
        check(playback_overlap_boundary(ctx, 200, 100), "Old-input ended waits through actual overlap output");
        AVFrame* picture = overlap_test_picture(ctx, 200);
        AVFrame* sound = overlap_test_sound(ctx, 480, 3000);
        AVFrame* first_mixed = playback_overlap_video(ctx, picture);
        int held_marker = first_mixed->data[0][0]; if (first_mixed != picture) av_frame_free(&first_mixed);
        ctx->output_paused = 1;
        first_mixed = playback_overlap_video(ctx, picture);
        check(first_mixed->data[0][0] == held_marker, "Pausing retains the displayed composite");
        if (first_mixed != picture) av_frame_free(&first_mixed);
        ctx->output_video_pts_time = 10000000;
        first_mixed = playback_overlap_video(ctx, picture);
        check(first_mixed->data[0][0] == held_marker && !blend->video_done && !blend->audio_done, "Paused output clocks cannot consume an overlap");
        if (first_mixed != picture) av_frame_free(&first_mixed);
        ctx->output_paused = 0;
        first_mixed = playback_overlap_video(ctx, picture);
        check(first_mixed->data[0][0] == held_marker, "Resuming continues the same fade weight");
        if (first_mixed != picture) av_frame_free(&first_mixed);
        blend->output_start = 0; ctx->output_video_pts_time = 0;
        int previous = 0;
        for (int tick = 0; tick < 6; tick++) {
            ctx->output_video_pts_time = av_rescale_q(tick, (AVRational){1,60}, UNIVERSAL_TB);
            AVFrame* mixed = playback_overlap_video(ctx, picture);
            int marker = mixed->data[0][0];
            check(marker > previous && marker <= 200, "Blend weights progress at output fps, including duplicated 30fps source pictures");
            check(picture->data[0][0] == 200, "Repeated source pictures are never modified in-place");
            previous = marker;
            if (mixed != picture) av_frame_free(&mixed);
            playback_overlap_finish(ctx);
        }
        check(previous == 200 && overlap_ended_count == 0, "Video reaches full new input while old audio completion remains pending");
        for (int block = 0; block < 10; block++) {
            ctx->output_sample_count = block * 480;
            ctx->output_audio_pts_time = playback_audio_time(ctx, ctx->output_sample_count);
            AVFrame* mixed = playback_overlap_audio(ctx, sound);
            int actual = ((int16_t*)mixed->data[0])[0];
            check(actual >= 1000 && actual <= 3000 && ((int16_t*)sound->data[0])[0] == 3000, "Audio ramps through old and new samples without modifying its input");
            if (mixed != sound) av_frame_free(&mixed);
            playback_overlap_finish(ctx);
        }
        check(overlap_ended_count == 1 && ctx->overlap->pending == NULL, "Old input ends exactly once after both media complete, then releases all tail caches");
        av_frame_free(&picture); av_frame_free(&sound); inout_context_free(&ctx);
    }
    inout_context* ctx = overlap_test_context(AV_PIX_FMT_BGR24); overlap_test_enqueue(ctx);
    playback_overlap* blend = playback_overlap_prepare(ctx, "new.avi\toverlap=0.11301", 0, 0);
    check(blend && overlap_queue_samples(ctx->frame_queues[1]) + overlap_queue_samples((frame_queue*)&(frame_queue){.front=blend->tail[1]}) == 48000,
        "Fractional-duration sample splitting preserves the full old audio");
    check(blend->tail[1] && llabs(blend->tail[1]->frame->pts - blend->source_start) <= playback_audio_time(ctx, 1),
        "A sub-sample split keeps its partial first audio packet in the overlap cache");
    playback_overlap_restore(ctx, blend); playback_overlap_free(blend);
    check(ctx->frame_queues[0]->count == 30 && overlap_queue_samples(ctx->frame_queues[1]) == 48000, "Failed new input can restore the complete old tail");
    blend = playback_overlap_prepare(ctx, "prefetched.avi", 0, 0); ctx->input_frame_id = 200; playback_overlap_commit(ctx, blend);
    enqueue_video_batch(ctx, 200, 100, 2);
    check(replace_prefetched_input(ctx, 100) && ctx->overlap->pending == NULL && ctx->frame_queues[0]->count == 30 &&
        overlap_queue_samples(ctx->frame_queues[1]) == 48000, "Replacing a prefetched input restores the protected old tail before purging the obsolete cache");
    blend = playback_overlap_prepare(ctx, "replacement.avi", 0, 100);
    check(blend && blend->old_id == 100, "Anchored replay blends against actual output instead of decoder lookahead");
    playback_overlap_restore(ctx, blend); playback_overlap_free(blend);
    check(!playback_overlap_prepare(ctx, "off.avi\toverlap=0", 0, 0) && ctx->frame_queues[0]->count == 30, "Zero duration leaves ordinary playback untouched");
    frame_queue_drain_state before;
    frame_queue_set(ctx->frame_queues[0], 1, 9, 4);
    frame_queue_begin_natural_drain(ctx->frame_queues[0], 60, &before); frame_queue_set(ctx->frame_queues[0], -1, -1, -1);
    frame_queue_end_natural_drain(ctx->frame_queues[0], &before);
    check(ctx->frame_queues[0]->left_count == 1 && ctx->frame_queues[0]->center_count == 4 && ctx->frame_queues[0]->right_count == 9,
        "Canceled handoffs restore all queue watermarks and original bounded capacity");
    frame_queue_clean(ctx->frame_queues[0]); frame_queue_clean(ctx->frame_queues[1]);
    frame_queue_set(ctx->frame_queues[0], -1, -1, -1); frame_queue_set(ctx->frame_queues[1], -1, -1, -1);
    ctx->input_frame_id = 100; ctx->output_discard_after_frame_id = ctx->output_discard_through_frame_id = 0;
    AVFrame* sound = av_frame_alloc(); sound->format = AV_SAMPLE_FMT_S16; sound->sample_rate = 44100; sound->nb_samples = 11025;
    av_channel_layout_default(&sound->ch_layout, 1); sound->pts = 0;
    check(av_frame_get_buffer(sound, 0) >= 0, "Allocate 44.1kHz source"); memset(sound->data[0], 0, sound->linesize[0]);
    check(fill_output_audio(ctx, sound) >= 0, "Resample 44.1kHz to output 48kHz"); flush_output_audio_resampler(ctx);
    check(overlap_queue_samples(ctx->frame_queues[1]) == 12000, "Resampler EOF flush retains all 12000 converted samples, including its delayed tail");
    av_frame_free(&sound); inout_context_free(&ctx);
    av_log_set_level(log_level);
    puts("PASS: overlap options, five negotiated video formats, source/output fps alignment, audio sample ramps, EOF resampler tails, deferred output completion, rollback, anchored replacement and queue watermarks.");
}
