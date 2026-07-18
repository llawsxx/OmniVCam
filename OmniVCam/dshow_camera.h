#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct dshow_camera_options {
    const char* device_name_utf8;
    const char* pin_id_utf8;
    int format_index;
    const char* audio_device_name_utf8;
    const char* audio_pin_id_utf8;
    int audio_format_index;
    int audio_device_is_video;
    int crossbar_video_input;
    int crossbar_video_output;
    int crossbar_audio_input;
    int crossbar_audio_output;
    int show_crossbar_dialog;
    int show_tv_tuner_dialog;
    int show_tv_audio_dialog;
} dshow_camera_options;

typedef void (*dshow_camera_frame_callback)(void* opaque, void* avframe);

typedef struct dshow_camera dshow_camera;

dshow_camera* dshow_camera_open(const dshow_camera_options* options,
    dshow_camera_frame_callback video_callback, dshow_camera_frame_callback audio_callback,
    void* opaque, char* error, size_t error_size);
int dshow_camera_start(dshow_camera* camera);
void dshow_camera_close(dshow_camera** camera);

/* Compact UTF-8 records for the TCP controller. String fields are Base64. */
int dshow_camera_list_devices(char* output, size_t output_size);
int dshow_camera_list_formats(const char* device_name_utf8, int device_type, char* output, size_t output_size);
int dshow_camera_list_crossbar(const char* device_name_utf8, char* output, size_t output_size);

#ifdef __cplusplus
}
#endif
