#include <windows.h>
#include "shared-memory-queue.h"

#define VIDEO_NAME L"OBSVirtualCamVideo"


video_queue_t *video_queue_create(uint32_t cx, uint32_t cy, uint64_t interval)
{
	struct video_queue vq = {0};
	struct video_queue *pvq;
	DWORD frame_size = cx * cy * 3 / 2;
	uint32_t offset_frame[3];
	DWORD size;

	size = sizeof(struct queue_header);

	ALIGN_SIZE(size, 32);

	offset_frame[0] = size;
	size += frame_size + FRAME_HEADER_SIZE;
	ALIGN_SIZE(size, 32);

	offset_frame[1] = size;
	size += frame_size + FRAME_HEADER_SIZE;
	ALIGN_SIZE(size, 32);

	offset_frame[2] = size;
	size += frame_size + FRAME_HEADER_SIZE;
	ALIGN_SIZE(size, 32);

	struct queue_header header = {0};

	header.state = SHARED_QUEUE_STATE_STARTING;
	header.cx = cx;
	header.cy = cy;
	header.interval = interval;
	vq.is_writer = true;

	for (size_t i = 0; i < 3; i++) {
		uint32_t off = offset_frame[i];
		header.offsets[i] = off;
	}

	/* fail if already in use */
	vq.handle = OpenFileMappingW(FILE_MAP_READ, false, VIDEO_NAME);
	if (vq.handle) {
		CloseHandle(vq.handle);
		return NULL;
	}

	vq.handle = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, size, VIDEO_NAME);
	if (!vq.handle) {
		return NULL;
	}

	vq.header = (struct queue_header *)MapViewOfFile(vq.handle, FILE_MAP_ALL_ACCESS, 0, 0, 0);
	if (!vq.header) {
		CloseHandle(vq.handle);
		return NULL;
	}
	memcpy(vq.header, &header, sizeof(header));

	for (size_t i = 0; i < 3; i++) {
		uint32_t off = offset_frame[i];
		vq.ts[i] = (uint64_t *)(((uint8_t *)vq.header) + off);
		vq.frame[i] = ((uint8_t *)vq.header) + off + FRAME_HEADER_SIZE;
	}
	pvq = malloc(sizeof(vq));
	if (!pvq) {
		CloseHandle(vq.handle);
		return NULL;
	}
	memcpy(pvq, &vq, sizeof(vq));
	return pvq;
}

video_queue_t *video_queue_open()
{
	struct video_queue vq = {0};

	vq.handle = OpenFileMappingW(FILE_MAP_READ, false, VIDEO_NAME);
	if (!vq.handle) {
		return NULL;
	}

	vq.header = (struct queue_header *)MapViewOfFile(vq.handle, FILE_MAP_READ, 0, 0, 0);
	if (!vq.header) {
		CloseHandle(vq.handle);
		return NULL;
	}

	struct video_queue *pvq = malloc(sizeof(vq));
	if (!pvq) {
		CloseHandle(vq.handle);
		return NULL;
	}
	memcpy(pvq, &vq, sizeof(vq));
	return pvq;
}

void video_queue_close(video_queue_t *vq)
{
	if (!vq) {
		return;
	}
	if (vq->is_writer) {
		vq->header->state = SHARED_QUEUE_STATE_STOPPING;
	}

	UnmapViewOfFile(vq->header);
	CloseHandle(vq->handle);
	free(vq);
}

void video_queue_get_info(video_queue_t *vq, uint32_t *cx, uint32_t *cy, uint64_t *interval)
{
	struct queue_header *qh = vq->header;
	*cx = qh->cx;
	*cy = qh->cy;
	*interval = qh->interval;
}

#define get_idx(inc) ((unsigned long)inc % 3)

void video_queue_write(video_queue_t *vq, uint8_t **data, uint32_t *linesize, uint64_t timestamp)
{
	struct queue_header *qh = vq->header;
	long inc = ++qh->write_idx;

	unsigned long idx = get_idx(inc);
	size_t size = linesize[0] * qh->cy;

	*vq->ts[idx] = timestamp;
	memcpy(vq->frame[idx], data[0], size);
	memcpy(vq->frame[idx] + size, data[1], size / 2);

	qh->read_idx = inc;
	qh->state = SHARED_QUEUE_STATE_READY;
}

enum queue_state video_queue_state(video_queue_t *vq)
{
	if (!vq) {
		return SHARED_QUEUE_STATE_INVALID;
	}

	enum queue_state state = (enum queue_state)vq->header->state;
	if (!vq->ready_to_read && state == SHARED_QUEUE_STATE_READY) {
		for (size_t i = 0; i < 3; i++) {
			size_t off = vq->header->offsets[i];
			vq->ts[i] = (uint64_t *)(((uint8_t *)vq->header) + off);
			vq->frame[i] = ((uint8_t *)vq->header) + off + FRAME_HEADER_SIZE;
		}
		vq->ready_to_read = true;
	}

	return state;
}

bool video_queue_read(video_queue_t *vq, uint8_t **dst, uint64_t *ts,uint32_t *read_idx)
{
	struct queue_header *qh = vq->header;
	long inc = qh->read_idx;

	if (qh->state == SHARED_QUEUE_STATE_STOPPING) {
		return false;
	}

	if (inc != vq->last_inc) {
		unsigned long idx = get_idx(inc);
		*ts = *vq->ts[idx];
		*dst = vq->frame[idx];
	}

	*read_idx = inc;
	vq->last_inc = inc;
	return true;
}
