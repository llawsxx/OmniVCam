#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum queue_type {
	SHARED_QUEUE_TYPE_VIDEO
};

struct queue_header {
	volatile uint32_t write_idx;
	volatile uint32_t read_idx;
	volatile uint32_t state;

	uint32_t offsets[3];

	uint32_t type;

	uint32_t cx;
	uint32_t cy;
	uint64_t interval;

	uint32_t reserved[8];
};

struct video_queue {
	HANDLE handle;
	bool ready_to_read;
	struct queue_header* header;
	uint64_t* ts[3];
	uint8_t* frame[3];
	long last_inc;
	bool is_writer;
};

#define ALIGN_SIZE(size, align) size = (((size) + (align - 1)) & (~(align - 1)))
#define FRAME_HEADER_SIZE 32

struct video_queue;
typedef struct video_queue video_queue_t;

enum queue_state {
	SHARED_QUEUE_STATE_INVALID,
	SHARED_QUEUE_STATE_STARTING,
	SHARED_QUEUE_STATE_READY,
	SHARED_QUEUE_STATE_STOPPING,
};

extern video_queue_t *video_queue_create(uint32_t cx, uint32_t cy, uint64_t interval);
extern video_queue_t *video_queue_open();
extern void video_queue_close(video_queue_t *vq);

extern void video_queue_get_info(video_queue_t *vq, uint32_t *cx, uint32_t *cy, uint64_t *interval);
extern void video_queue_write(video_queue_t *vq, uint8_t **data, uint32_t *linesize, uint64_t timestamp);
extern enum queue_state video_queue_state(video_queue_t *vq);
extern bool video_queue_read(video_queue_t* vq, uint8_t** dst, uint64_t* ts, uint32_t* read_idx);

#ifdef __cplusplus
}
#endif
