#include"video_frame.h"
int get_video_buffer(AVFrame* frame)
{
	const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(frame->format);
	int ret;
	ptrdiff_t linesizes[4];
	size_t total_size, sizes[4];

	if (!desc)
		return AVERROR(EINVAL);

	if ((ret = av_image_check_size(frame->width, frame->height, 0, NULL)) < 0)
		return ret;

	if (!frame->linesize[0]) {
		ret = av_image_fill_linesizes(frame->linesize, frame->format,
			frame->width);
		if (ret < 0)
			return ret;
	}

	for (int i = 0; i < 4; i++)
		linesizes[i] = frame->linesize[i];

	if ((ret = av_image_fill_plane_sizes(sizes, frame->format,
		frame->height, linesizes)) < 0)
		return ret;

	total_size = 0;
	for (int i = 0; i < 4; i++) {
		if (sizes[i] > SIZE_MAX - total_size)
			return AVERROR(EINVAL);
		total_size += sizes[i];
	}

	frame->buf[0] = av_buffer_alloc(total_size);
	if (!frame->buf[0]) {
		ret = AVERROR(ENOMEM);
		goto fail;
	}

	if ((ret = av_image_fill_pointers(frame->data, frame->format, frame->height,
		frame->buf[0]->data, frame->linesize)) < 0)
		goto fail;

	frame->extended_data = frame->data;
	return 0;
fail:
	av_frame_unref(frame);
	return ret;
}