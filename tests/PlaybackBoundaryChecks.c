/* Include the playback implementation to exercise its actual static queue,
 * held-frame, output-boundary and protocol functions. No camera is opened. */
struct inout_context;
static struct inout_context* short_context;
#define OMNIVCAM_TEST_CONTEXT_CREATED(ctx) (short_context = (ctx))
#include "../OmniVCam/RenderVideo.c"
#include <stdlib.h>

static void check(int condition, const char* message)
{
    if (!condition) { fprintf(stderr, "FAIL: %s\n", message); exit(1); }
}

static void enqueue_test_frame(frame_queue* queue, int64_t id)
{
    avframe_node* node = av_mallocz(sizeof(*node));
    node->frame = av_frame_alloc();
    node->frame->pts = id;
    node->frame_id = id;
    node->media_mask = INPUT_MEDIA_VIDEO | INPUT_MEDIA_AUDIO;
    if (queue->rear) queue->rear->next = node;
    else queue->front = node;
    queue->rear = node;
    queue->count++;
}

static void expect_frame(frame_queue* queue, int64_t expected)
{
    AVFrame* frame = av_frame_alloc();
    int64_t id = 0;
    int reset = 0, mask = 0;
    check(frame_dequeue(queue, frame, &id, &mask, 0, &reset) >= 0 && id == expected, "Protected tail then replacement output order");
    av_frame_free(&frame);
}

typedef struct output_probe {
    HANDLE current_visible;
    HANDLE target_visible;
    volatile LONG obsolete_frames;
    volatile LONG last_protected_marker;
} output_probe;

static void probe_video(void* priv, AVFrame* frame)
{
    output_probe* probe = priv;
    if (!frame->data[0]) return;
    int marker = frame->data[0][0];
    if (marker >= 10 && marker <= 21) {
        InterlockedExchange(&probe->last_protected_marker, marker);
        if (marker >= 12) SetEvent(probe->current_visible);
    }
    if (marker >= 100 && marker < 160) InterlockedIncrement(&probe->obsolete_frames);
    if (marker >= 200) SetEvent(probe->target_visible);
}

static void enqueue_video_batch(inout_context* ctx, int64_t id, int marker, int count)
{
    AVFrame* frame = av_frame_alloc();
    for (int i = 0; i < count; i++) {
        frame->width = ctx->output_frame_width;
        frame->height = ctx->output_frame_height;
        frame->format = ctx->output_frame_format;
        frame->pts = i * ctx->output_time_per_video_frame;
        check(av_frame_get_buffer(frame, 32) >= 0, "Allocate output test frame");
        memset(frame->data[0], marker + i, frame->linesize[0] * frame->height);
        check(frame_enqueue(ctx->frame_queues[0], frame, -1, id, INPUT_MEDIA_VIDEO, AV_NOPTS_VALUE, &ctx->force_exit) >= 0, "Enqueue output test batch");
    }
    av_frame_free(&frame);
}

static void check_live_output_boundary(void)
{
    AVChannelLayout mono = AV_CHANNEL_LAYOUT_MONO;
    inout_context* ctx = inout_context_alloc(16, 16, AV_PIX_FMT_BGR24, (AVRational){30, 1},
        48000, AV_SAMPLE_FMT_S16, &mono, 480, 1000000, 1024 * 1024, 100, 128, 128, 1, 5, 1000000);
    check(ctx != NULL, "Allocate real output context");
    output_probe probe = {0};
    probe.current_visible = CreateEvent(NULL, TRUE, FALSE, NULL);
    probe.target_visible = CreateEvent(NULL, TRUE, FALSE, NULL);
    ctx->video_callback = probe_video;
    ctx->callback_private = &probe;
    ctx->input_frame_id = 200;
    enqueue_video_batch(ctx, 100, 10, 12);
    enqueue_video_batch(ctx, 200, 100, 60);
    check(start_output(ctx) >= 0, "Start real paced output thread");
    check(WaitForSingleObject(probe.current_visible, 2000) == WAIT_OBJECT_0, "Protected input is actually visible before triggering");
    check(replace_prefetched_input(ctx, 100), "Replace already-decoded standby during protected playback");
    EnterCriticalSection(&ctx->input_change_mutex);
    ctx->input_frame_id = 300;
    enqueue_video_batch(ctx, 300, 200, 6);
    LeaveCriticalSection(&ctx->input_change_mutex);
    check(WaitForSingleObject(probe.target_visible, 2000) == WAIT_OBJECT_0, "Replay reaches real output without waiting for the obsolete standby or watchdog");
    check(probe.obsolete_frames == 0, "Old standby never reaches real video output");
    check(probe.last_protected_marker == 21, "Protected video plays through its final frame before replay");
    stop_output(ctx);
    CloseHandle(probe.current_visible); CloseHandle(probe.target_visible);
    inout_context_free(&ctx);
}

/* Standalone integration backend: real files, main_thread, TCP, decoder and paced output. */
static FILE* short_trace;
static void short_video_callback(void* priv, AVFrame* frame)
{
    if (short_trace && frame->data[0]) {
        fprintf(short_trace, "%I64d,%d,%I64d,%I64d\n",
            av_gettime_relative(), frame->data[0][0], short_context->output_boundary_frame_id,
            short_context->output_current_video_frame_id);
        fflush(short_trace);
    }
}

static void write_short_media(const char* path, int fps, int count, int marker, int with_audio, int compressed)
{
    AVFormatContext* format = NULL;
    AVCodecContext* encoder = NULL;
    AVStream* video;
    AVStream* audio = NULL;
    AVPacket* packet = av_packet_alloc();
    check(avformat_alloc_output_context2(&format, NULL, compressed ? "mp4" : "avi", path) >= 0, "Create short media container");
    video = avformat_new_stream(format, NULL);
    video->time_base = (AVRational){1, fps};
    video->avg_frame_rate = video->r_frame_rate = (AVRational){fps, 1};
    if (compressed) {
        const AVCodec* codec = avcodec_find_encoder(AV_CODEC_ID_MPEG4);
        check(codec != NULL, "MPEG4 encoder is available");
        encoder = avcodec_alloc_context3(codec);
        encoder->width = encoder->height = 32;
        encoder->pix_fmt = AV_PIX_FMT_YUV420P;
        encoder->time_base = (AVRational){1, fps};
        encoder->framerate = (AVRational){fps, 1};
        encoder->gop_size = 12;
        encoder->max_b_frames = 2;
        if (format->oformat->flags & AVFMT_GLOBALHEADER) encoder->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        check(avcodec_open2(encoder, codec, NULL) >= 0, "Open delayed video encoder");
        check(avcodec_parameters_from_context(video->codecpar, encoder) >= 0, "Copy compressed video parameters");
    }
    else {
        video->codecpar->codec_type = AVMEDIA_TYPE_VIDEO;
        video->codecpar->codec_id = AV_CODEC_ID_RAWVIDEO;
        video->codecpar->format = AV_PIX_FMT_BGR24;
        video->codecpar->width = video->codecpar->height = 32;
        video->codecpar->bits_per_coded_sample = 24;
    }
    if (with_audio) {
        audio = avformat_new_stream(format, NULL);
        audio->time_base = (AVRational){1, 48000};
        audio->codecpar->codec_type = AVMEDIA_TYPE_AUDIO;
        audio->codecpar->codec_id = AV_CODEC_ID_PCM_S16LE;
        audio->codecpar->format = AV_SAMPLE_FMT_S16;
        audio->codecpar->sample_rate = 48000;
        audio->codecpar->bits_per_coded_sample = 16;
        audio->codecpar->block_align = 2;
        audio->codecpar->bit_rate = 48000 * 16;
        av_channel_layout_default(&audio->codecpar->ch_layout, 1);
    }
    check(avio_open(&format->pb, path, AVIO_FLAG_WRITE) >= 0 && avformat_write_header(format, NULL) >= 0, "Write short media header");
    for (int index = 0; index <= count; index++) {
        if (compressed) {
            AVFrame* frame = NULL;
            if (index < count) {
                frame = av_frame_alloc();
                frame->width = frame->height = 32;
                frame->format = AV_PIX_FMT_YUV420P;
                frame->pts = index;
                check(av_frame_get_buffer(frame, 32) >= 0, "Allocate encoded frame");
                memset(frame->data[0], marker + (count <= 3 ? index * 8 : 0), frame->linesize[0] * 32);
                memset(frame->data[1], 128, frame->linesize[1] * 16);
                memset(frame->data[2], 128, frame->linesize[2] * 16);
            }
            check(avcodec_send_frame(encoder, frame) >= 0, "Encode or flush a short frame");
            av_frame_free(&frame);
            while (avcodec_receive_packet(encoder, packet) >= 0) {
                av_packet_rescale_ts(packet, encoder->time_base, video->time_base);
                packet->stream_index = video->index;
                check(av_interleaved_write_frame(format, packet) >= 0, "Mux a delayed short packet");
            }
        }
        else if (index < count) {
            check(av_new_packet(packet, 32 * 32 * 3) >= 0, "Allocate raw frame packet");
            memset(packet->data, marker + (count <= 3 ? index * 8 : 0), packet->size);
            packet->pts = packet->dts = index;
            packet->duration = 1;
            packet->stream_index = video->index;
            check(av_interleaved_write_frame(format, packet) >= 0, "Mux raw short frame");
        }
        if (audio && index < count) {
            int samples = 48000 / fps;
            check(av_new_packet(packet, samples * 2) >= 0, "Allocate PCM audio packet");
            memset(packet->data, marker, packet->size);
            packet->pts = packet->dts = index * samples;
            packet->duration = samples;
            packet->stream_index = audio->index;
            check(av_interleaved_write_frame(format, packet) >= 0, "Mux short audio");
        }
    }
    check(av_write_trailer(format) >= 0, "Finalize short media");
    avcodec_free_context(&encoder);
    av_packet_free(&packet);
    avio_closep(&format->pb);
    avformat_free_context(format);
}

static int run_short_mode(int argc, char** argv)
{
    if (strcmp(argv[1], "--generate-short") == 0) {
        check(argc == 3, "Short fixture output directory");
        char path[1024];
        const char* kinds[] = { "raw", "av", "mpeg4" };
        for (int fps = 30; fps <= 60; fps += 30)
            for (int kind = 0; kind < 3; kind++)
                for (int count = 1; count <= 4; count++) {
                    int frames = count == 4 ? fps : count;
                    sprintf_s(path, sizeof(path), "%s/%s-%d-%d.%s", argv[2], kinds[kind], fps, frames, kind == 2 ? "mp4" : "avi");
                    write_short_media(path, fps, frames, count == 4 ? 210 : 32 + count * 40, kind == 1, kind == 2);
                }
        puts("PASS: generated 1/2/3-frame AVI, AVI+PCM and delayed MPEG4/MP4 files at 30/60fps.");
        return 0;
    }
    check(argc == 6 && strcmp(argv[1], "--serve-short") == 0, "Integration backend arguments");
    inout_options options = {0};
    options.video_out_width = options.video_out_height = 32;
    options.video_out_format = AV_PIX_FMT_BGR24;
    options.video_out_fps = (AVRational){atoi(argv[3]), 1};
    options.audio_out_sample_rate = 48000;
    options.audio_out_format = AV_SAMPLE_FMT_S16;
    options.audio_out_channels = 1;
    options.audio_out_nb_samples = 480;
    options.video_callback = short_video_callback;
    options.tcp_port = atoi(argv[2]);
    options.config_path = "OMNIVCAM_SHORT_TEST_CONFIG";
    check(_putenv_s(options.config_path, argv[4]) == 0, "Isolated test configuration");
    check(fopen_s(&short_trace, argv[5], "wb") == 0, "Open actual video output trace");
    avdevice_register_all();
    HANDLE thread;
    check(open_thread(&thread, main_thread, &options) >= 0, "Start actual playback backend");
    char stop[8];
    fgets(stop, sizeof(stop), stdin);
    options.send_exit = 1;
    check(WaitForSingleObject(thread, 10000) == WAIT_OBJECT_0, "Integration backend stops cleanly");
    CloseHandle(thread);
    fclose(short_trace);
    return 0;
}

int main(int argc, char** argv)
{
    if (argc > 1) return run_short_mode(argc, argv);
    inout_context* ctx = av_mallocz(sizeof(*ctx));
    AVFrame* video = av_frame_alloc();
    AVFrame* audio = av_frame_alloc();
    int sample = 123;
    void* sample_data[] = { &sample };
    InitializeCriticalSection(&ctx->input_change_mutex);
    for (int i = 0; i < 2; i++) ctx->frame_queues[i] = frame_queue_alloc(20);
    ctx->output_audio_fifo = av_audio_fifo_alloc(AV_SAMPLE_FMT_S32, 1, 16);
    ctx->input_frame_id = 250;
    ctx->output_boundary_frame_id = 100;
    ctx->output_boundary_media_mask = INPUT_MEDIA_VIDEO | INPUT_MEDIA_AUDIO;
    ctx->output_current_video_frame_id = 200;
    ctx->output_current_audio_frame_id = 200;
    ctx->output_audio_boundary_frame_id = 200;
    ctx->output_time_offset = 1234;
    video->pts = audio->pts = 200;
    av_audio_fifo_write(ctx->output_audio_fifo, sample_data, 1);
    for (int i = 0; i < 2; i++) {
        enqueue_test_frame(ctx->frame_queues[i], 100);
        enqueue_test_frame(ctx->frame_queues[i], 100);
        enqueue_test_frame(ctx->frame_queues[i], 200);
        enqueue_test_frame(ctx->frame_queues[i], 250);
    }
    check(replace_prefetched_input(ctx, 100), "Decoder lookahead beyond protected batch is replaced immediately");
    for (int i = 0; i < 2; i++) check(ctx->frame_queues[i]->count == 2, "Keep protected frames and remove all later prefetched batches");
    /* A producer can enqueue once after the first purge, before it observes abort. */
    enqueue_test_frame(ctx->frame_queues[0], 250);
    check(discard_replaced_output(ctx, video, audio), "Discard held frames as well as queued frames");
    check(video->pts == AV_NOPTS_VALUE && audio->pts == AV_NOPTS_VALUE, "Canceled held video/audio cannot leak into output");
    check(av_audio_fifo_size(ctx->output_audio_fifo) == 0, "Discard superseded audio FIFO");
    check(ctx->output_current_video_frame_id == 100 && ctx->output_current_audio_frame_id == 100, "Retain the protected batch as the synchronization anchor");
    check(ctx->output_time_offset == 1234, "Protected tail retains its current output clock");
    for (int i = 0; i < 2; i++) {
        enqueue_test_frame(ctx->frame_queues[i], 300);
        expect_frame(ctx->frame_queues[i], 100);
        expect_frame(ctx->frame_queues[i], 100);
        expect_frame(ctx->frame_queues[i], 300);
        check(ctx->frame_queues[i]->count == 0 && !ctx->frame_queues[i]->front && !ctx->frame_queues[i]->rear, "Queue bookkeeping after replacement");
    }
    ctx->input_frame_id = 100;
    check(!replace_prefetched_input(ctx, 100), "A still-decoding protected input must finish naturally");
    ctx->input_frame_id = ctx->output_boundary_frame_id = 400;
    ctx->output_current_video_frame_id = ctx->output_current_audio_frame_id = 400;
    check(replace_prefetched_input(ctx, 300) && discard_replaced_output(ctx, video, audio), "Handle command processing after the old standby already reached output");
    check(ctx->output_time_offset == AV_NOPTS_VALUE, "A canceled output batch cannot keep its clock");

    tcp_control_server* server = av_mallocz(sizeof(*server));
    InitializeCriticalSection(&server->mutex);
    InitializeCriticalSection(&server->event_mutex);
    for (int64_t id = 1; id <= 2 * FRAME_QUEUE_MAX_COUNT_LIMIT + 2; id++)
        tcp_control_register_input(server, id, "one-frame-loop.mp4");
    tcp_control_notify_input_started(server, 1);
    check(server->status_actual_frame_id == 1 && server->event_queue_count == 1,
        "One-frame loops retain output identity across maximum audio/video queue lookahead");
    server->input_history_count = server->input_history_head = 0;
    server->event_queue_count = server->event_queue_head = 0;
    server->status_actual_frame_id = 0;
    tcp_control_register_input(server, 100, "same.mp4");
    tcp_control_register_input(server, 200, "obsolete standby.mp4");
    tcp_control_register_input(server, 300, "same.mp4");
    tcp_control_notify_input_started(server, 100);
    check(server->status_actual_frame_id == 100, "Initial actual output batch");
    tcp_control_notify_input_started(server, 300);
    check(server->status_actual_frame_id == 300, "Skipped prefetched batches do not become actual output");
    tcp_control_notify_input_started(server, 100);
    check(server->status_actual_frame_id == 300, "Delayed old callbacks cannot roll back actual status");
    char hash[TCP_CONTROL_INPUT_SUMMARY_SIZE];
    tcp_control_sha256("same.mp4", hash);
    check(strcmp(server->status_actual_input, hash) == 0, "Repeated files retain the same hash but different batch IDs");
    server->event_queue_count = server->event_queue_head = 0;
    server->status_actual_frame_id = 100;
    ctx->input_started_callback = tcp_control_notify_input_started;
    ctx->input_started_private = server;
    ctx->input_ended_callback = tcp_control_notify_input_ended;
    ctx->input_ended_private = server;
    ctx->output_boundary_frame_id = 100;
    ctx->output_audio_boundary_frame_id = 100;
    ctx->output_current_video_frame_id = 100;
    ctx->output_current_audio_frame_id = 300;
    check(!reset_output_at_input_boundary(ctx, 300, INPUT_MEDIA_VIDEO | INPUT_MEDIA_AUDIO) && server->status_actual_frame_id == 100,
        "Leading new audio cannot prematurely announce the new visible input");
    ctx->output_current_video_frame_id = 300;
    check(reset_output_at_input_boundary(ctx, 300, INPUT_MEDIA_VIDEO | INPUT_MEDIA_AUDIO) && server->status_actual_frame_id == 300,
        "Publish actual output when both streams reach the exact replacement batch");
    tcp_control_event event;
    check(tcp_control_pop_event(server, &event) && event.type == TCP_CONTROL_EVENT_INPUT_STARTED && event.frame_id == 300,
        "Output-start event identifies the actual replacement before the old end event");
    check(tcp_control_pop_event(server, &event) && event.type == TCP_CONTROL_EVENT_INPUT_ENDED && event.frame_id == 100,
        "End event retains the original batch identity when intermediate inputs were skipped");
    int64_t old_special_id = ctx->input_frame_id;
    AVDictionary* special_options = NULL;
    check(open_input(NULL, "<TESTCARDCAL>", &special_options, ctx, -1, -1, -1, "", 0, 0, -1, -1, -1, NULL, NULL, NULL, NULL) >= 0 && ctx->input_frame_id > old_special_id,
        "Special-source batch ID is fixed before registration and producer startup");
    av_dict_free(&special_options);
    av_freep(&ctx->input_name);
    tcp_control_command command;
    char reply[1024];
    char next[] = "PLAY_NEXT same.mp4\tafter_frame_id=100,loop=1";
    tcp_control_parse_line(server, next, &command, reply, sizeof(reply));
    check(command.type == TCP_CONTROL_PLAY_NEXT && command.number == 100 && strcmp(command.text, "same.mp4\tloop=1") == 0, "Anchor is parsed without contaminating input identity/options");
    av_freep(&command.text);
    char no_options[] = "PLAY_NEXT same.mp4\tafter_frame_id=300";
    tcp_control_parse_line(server, no_options, &command, reply, sizeof(reply));
    check(command.number == 300 && strcmp(command.text, "same.mp4") == 0, "Private-only options preserve the original input identity");
    av_freep(&command.text);
    char invalid[] = "PLAY_NEXT same.mp4\tafter_frame_id=abc";
    tcp_control_parse_line(server, invalid, &command, reply, sizeof(reply));
    check(command.type == TCP_CONTROL_NONE && strncmp(reply, "ERR", 3) == 0, "Reject invalid batch anchors");
    char append[] = "PLAY_NEXT_APPEND same.mp4";
    tcp_control_parse_line(server, append, &command, reply, sizeof(reply));
    check(command.type == TCP_CONTROL_NONE && strstr(reply, "unknown command"), "Append protocol has been removed");
    DeleteCriticalSection(&server->event_mutex);
    DeleteCriticalSection(&server->mutex);
    av_free(server);
    av_frame_free(&video); av_frame_free(&audio);
    for (int i = 0; i < 2; i++) frame_queue_free(&ctx->frame_queues[i]);
    av_audio_fifo_free(ctx->output_audio_fifo);
    DeleteCriticalSection(&ctx->input_change_mutex);
    av_free(ctx);
    check_live_output_boundary();
    puts("PASS: paced output keeps the protected final frame and skips the obsolete standby; output batch synchronization, protected tails, superseded read-ahead/held frames/audio, command races, repeated-file IDs and anchored PLAY_NEXT; append removed.");
    return 0;
}
