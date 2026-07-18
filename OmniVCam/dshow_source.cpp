#include "dshow_source.h"
#include "dshow_camera.h"

extern "C" {
#include <libavutil/base64.h>
#include <libavutil/time.h>
}

static char* pop_value(AVDictionary** dict, const char* key)
{
    AVDictionaryEntry* entry;
    char* value;
    if (!dict || !*dict) return NULL;
    entry = av_dict_get(*dict, key, NULL, 0);
    if (!entry) return NULL;
    value = av_strdup(entry->value);
    av_dict_set(dict, key, NULL, 0);
    return value;
}

static char* pop_base64(AVDictionary** dict, const char* key)
{
    char* encoded = pop_value(dict, key);
    char* decoded = NULL;
    if (encoded) {
        int length = (int)strlen(encoded);
        decoded = (char*)av_malloc(length + 1);
        if (decoded) {
            int decoded_length = av_base64_decode((uint8_t*)decoded, encoded, length);
            if (decoded_length < 0) av_freep(&decoded);
            else decoded[decoded_length] = '\0';
        }
        av_free(encoded);
    }
    return decoded;
}

static int pop_integer(AVDictionary** dict, const char* key, int fallback)
{
    char* value = pop_value(dict, key);
    int result = value ? atoi(value) : fallback;
    av_free(value);
    return result;
}

static void reset_options(source_options* options)
{
    if (!options) return;
    av_freep(&options->device);
    av_freep(&options->pin);
    av_freep(&options->audio_device);
    av_freep(&options->audio_pin);
    memset(options, 0, sizeof(*options));
    options->format = -1;
    options->audio_format = -1;
    options->video_in = options->video_out = -1;
    options->audio_in = options->audio_out = -1;
}

int dshow_source_open(inout_context* ctx, AVDictionary** dict, int queue_left, int queue_right, int queue_center)
{
    if (!ctx) return -1;
    dshow_source_reset(ctx);
    source_options* options = &ctx->dshow_options;
    options->format = -1;
    options->audio_format = -1;
    options->video_in = options->video_out = -1;
    options->audio_in = options->audio_out = -1;
    options->device = pop_base64(dict, "dshow_device_b64");
    options->pin = pop_base64(dict, "dshow_pin_b64");
    options->format = pop_integer(dict, "dshow_format", -1);
    options->audio_device = pop_base64(dict, "dshow_audio_device_b64");
    options->audio_pin = pop_base64(dict, "dshow_audio_pin_b64");
    options->audio_format = pop_integer(dict, "dshow_audio_format", -1);
    options->audio_device_is_video = pop_integer(dict, "dshow_audio_device_type", 1) == 0;
    options->video_in = pop_integer(dict, "dshow_xbar_video_in", -1);
    options->video_out = pop_integer(dict, "dshow_xbar_video_out", -1);
    options->audio_in = pop_integer(dict, "dshow_xbar_audio_in", -1);
    options->audio_out = pop_integer(dict, "dshow_xbar_audio_out", -1);
    options->crossbar_dialog = pop_integer(dict, "dshow_crossbar_dialog", 0) != 0;
    options->tuner_dialog = pop_integer(dict, "dshow_tv_tuner_dialog", 0) != 0;
    options->audio_dialog = pop_integer(dict, "dshow_tv_audio_dialog", 0) != 0;
    if ((!options->device || !options->device[0]) && (!options->audio_device || !options->audio_device[0])) {
        reset_options(options);
        return -1;
    }
    frame_queue_set(ctx->frame_queues[0], queue_left, queue_right, queue_center);
    return 0;
}

static void frame_callback(void* opaque, void* frame_ptr)
{
    inout_context* ctx = (inout_context*)opaque;
    AVFrame* frame = (AVFrame*)frame_ptr;
    if (!ctx || !frame || ctx->force_exit) return;
    ctx->last_video_decode_time = av_gettime_relative();
    fill_output_video(ctx, frame);
}

static void audio_frame_callback(void* opaque, void* frame_ptr)
{
    inout_context* ctx = (inout_context*)opaque;
    AVFrame* frame = (AVFrame*)frame_ptr;
    if (!ctx || !frame || ctx->force_exit) return;
    ctx->last_audio_decode_time = av_gettime_relative();
    fill_output_audio(ctx, frame);
}

DWORD dshow_source_thread(LPVOID opaque)
{
    inout_context* ctx = (inout_context*)opaque;
    source_options* source = &ctx->dshow_options;
    dshow_camera_options options = {};
    dshow_camera* camera = NULL;
    char error[512] = {};
    HRESULT com_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    options.device_name_utf8 = source->device;
    options.pin_id_utf8 = source->pin;
    options.format_index = source->format;
    options.audio_device_name_utf8 = source->audio_device;
    options.audio_pin_id_utf8 = source->audio_pin;
    options.audio_format_index = source->audio_format;
    options.audio_device_is_video = source->audio_device_is_video;
    options.crossbar_video_input = source->video_in;
    options.crossbar_video_output = source->video_out;
    options.crossbar_audio_input = source->audio_in;
    options.crossbar_audio_output = source->audio_out;
    options.show_crossbar_dialog = source->crossbar_dialog;
    options.show_tv_tuner_dialog = source->tuner_dialog;
    options.show_tv_audio_dialog = source->audio_dialog;
    ctx->input_frame_id = av_gettime_relative();
    ctx->last_video_decode_time = av_gettime_relative();
    ctx->last_audio_decode_time = av_gettime_relative();
    camera = dshow_camera_open(&options, frame_callback, audio_frame_callback, ctx, error, sizeof(error));
    if (!camera || dshow_camera_start(camera) < 0) {
        av_log(NULL, AV_LOG_ERROR, "DirectShow camera open failed: %s\n", error[0] ? error : "start graph failed");
        ctx->force_exit = 1;
        goto end;
    }
    while (!ctx->force_exit) Sleep(20);
end:
    dshow_camera_close(&camera);
    ctx->special_source_running = 0;
    if (SUCCEEDED(com_hr)) CoUninitialize();
    return 0;
}

void dshow_source_reset(inout_context* ctx)
{
    if (ctx) reset_options(&ctx->dshow_options);
}

void dshow_source_free(inout_context* ctx)
{
    dshow_source_reset(ctx);
}
