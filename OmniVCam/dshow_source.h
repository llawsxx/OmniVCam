#pragma once

#include "RenderVideo.h"

#ifdef __cplusplus
extern "C" {
#endif

int dshow_source_open(inout_context* ctx, AVDictionary** options,
    int queue_left, int queue_right, int queue_center);
DWORD dshow_source_thread(LPVOID opaque);
void dshow_source_reset(inout_context* ctx);
void dshow_source_free(inout_context* ctx);

#ifdef __cplusplus
}
#endif
