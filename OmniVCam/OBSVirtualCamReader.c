#include "Threads.h"
#include "OBSVirtualCamReader/shared-memory-queue.h"
#include "OBSVirtualCamReader.h"
#include "video_frame.h"


OBSVirtualCamReader* obs_virtual_cam_reader_create(void)
{
    OBSVirtualCamReader* reader = (OBSVirtualCamReader*)malloc(sizeof(OBSVirtualCamReader));
    if (!reader) return NULL;

    reader->vq = NULL;
    reader->prev_state = SHARED_QUEUE_STATE_INVALID;
    reader->obs_cx = 0;
    reader->obs_cy = 0;
    reader->obs_interval = 0;

    reader->vq = video_queue_open();
    if (reader->vq) {
        if (video_queue_state(reader->vq) == SHARED_QUEUE_STATE_READY) {
            video_queue_get_info(reader->vq, &reader->obs_cx,
                &reader->obs_cy, &reader->obs_interval);
        }
        /* don't keep it open until the filter actually starts */
        video_queue_close(reader->vq);
        reader->vq = NULL;
    }

    return reader;
}

void obs_virtual_cam_reader_destroy(OBSVirtualCamReader* reader)
{
    if (reader) {
        if (reader->vq) {
            video_queue_close(reader->vq);
        }
        free(reader);
    }
}

void obs_virtual_cam_reader_get_obs_frame(OBSVirtualCamReader* reader, AVFrame** frame, uint64_t* obs_interval, uint32_t *read_idx)
{
    *frame = NULL;

    if (!reader) return;

    if (!reader->vq) {
        reader->vq = video_queue_open();
        if(reader->obs_interval)
            *obs_interval = reader->obs_interval;
    }

    enum queue_state state = video_queue_state(reader->vq);
    if (state != reader->prev_state) {
        if (state == SHARED_QUEUE_STATE_READY) {
            /* The virtualcam output from OBS has started, get
               the actual cx / cy of the data stream */
            video_queue_get_info(reader->vq, &reader->obs_cx,
                &reader->obs_cy, &reader->obs_interval);
            if (reader->obs_interval)
                *obs_interval = reader->obs_interval;
        }
        else if (state == SHARED_QUEUE_STATE_STOPPING) {
            video_queue_close(reader->vq);
            reader->vq = NULL;
        }
        reader->prev_state = state;
    }

    uint8_t* ptr = NULL;
    uint64_t timestamp = 0;
    /* Actual output */
    if (reader->vq) {
        if (!video_queue_read(reader->vq, &ptr, &timestamp, read_idx)) {
            video_queue_close(reader->vq);
            reader->vq = NULL;
        }
        else if(ptr != NULL){
            AVFrame* dst = av_frame_alloc();
            if (dst) {
                dst->width = reader->obs_cx;
                dst->height = reader->obs_cy;
                dst->format = AV_PIX_FMT_NV12;
                if (get_video_buffer(dst) >= 0) {
                    uint8_t* src_y = ptr;
                    uint8_t* src_uv = ptr + reader->obs_cx * reader->obs_cy;
                    for (int y = 0; y < reader->obs_cy; y++) {
                        memcpy(dst->data[0] + y * dst->linesize[0], src_y + y * reader->obs_cx, reader->obs_cx);
                    }
                    for (int y = 0; y < reader->obs_cy / 2; y++) {
                        memcpy(dst->data[1] + y * dst->linesize[1], src_uv + y * reader->obs_cx, reader->obs_cx);
                    }
                    dst->pts = timestamp;
                }
                *frame = dst;
                return;
            }
        }
    }
}

uint32_t obs_virtual_cam_reader_get_read_index(OBSVirtualCamReader* reader)
{
    if (reader) {
        if (reader->vq) {
            struct queue_header* qh = reader->vq->header;
            return qh->read_idx;
        }
    }
    return UINT32_MAX;
}

BOOL obs_virtual_cam_reader_get_is_closed(OBSVirtualCamReader* reader)
{
    return reader == NULL || reader->vq == NULL;
}