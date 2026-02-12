#pragma once
#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include "Threads.h"
#include "OBSVirtualCamReader/shared-memory-queue.h"
#include <libavutil/imgutils.h>
#include "video_frame.h"

typedef struct {
    video_queue_t* vq;
    enum queue_state prev_state;
    uint32_t obs_cx;
    uint32_t obs_cy;
    uint64_t obs_interval;
} OBSVirtualCamReader;

OBSVirtualCamReader* obs_virtual_cam_reader_create(void);
void obs_virtual_cam_reader_destroy(OBSVirtualCamReader* reader);
void obs_virtual_cam_reader_get_obs_frame(OBSVirtualCamReader* reader, AVFrame** frame, uint64_t* obs_interval, uint32_t* read_index);
uint32_t obs_virtual_cam_reader_get_read_index(OBSVirtualCamReader* reader);
BOOL obs_virtual_cam_reader_get_is_closed(OBSVirtualCamReader* reader);