#define _CRT_SECURE_NO_WARNINGS
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <WinSock2.h>
#include <Objbase.h>
#include "RenderVideo.h"
#include "ParseConfig.h"
#include "TestCard.h"
#include "OBSVirtualCamReader.h"
#include "Utils.h"
#include "clock.h"
#include "video_frame.h"
#include "global.h"

void frame_queue_wait_empty(frame_queue* q,int64_t timeout,int *exit) {
	int64_t start_time = av_gettime_relative();
	EnterCriticalSection(&q->mutex);

	q->reached_center = 0;
	q->center_count = -1;
	q->left_count = -1;
	q->right_count = -1;

	while (q->count > 0 && !(*exit))
	{
		SleepConditionVariableCS(&q->cond, &q->mutex, COND_TIMEOUT);
		if (av_gettime_relative() - start_time >= timeout) {
			break;
		}
	}
	LeaveCriticalSection(&q->mutex);
}

void inout_ctx_frame_queue_wait_empty(inout_context *ctx, int* exit) {
	for (int i = 0; i < ARRAY_ELEMS(ctx->frame_queues); i++) {
		frame_queue_wait_empty(ctx->frame_queues[i],ctx->timeout,exit);
	}
}

static int frame_queue_is_empty(frame_queue* q)
{
	int empty;
	EnterCriticalSection(&q->mutex);
	empty = q->count <= 0;
	LeaveCriticalSection(&q->mutex);
	return empty;
}

static int inout_ctx_frame_queues_empty(inout_context* ctx)
{
	for (int i = 0; i < ARRAY_ELEMS(ctx->frame_queues); i++) {
		if (!frame_queue_is_empty(ctx->frame_queues[i])) return 0;
	}
	return 1;
}

int frame_enqueue(frame_queue *q, AVFrame* frame,int64_t timeout, int64_t frame_id, int64_t input_fmt_start_time, int *exit) {
	int64_t start_time = av_gettime_relative();
	int ret = 0;
	EnterCriticalSection(&q->mutex);

	while (q->count >= q->max_count)
	{
		SleepConditionVariableCS(&q->cond, &q->mutex, COND_TIMEOUT);

		if (av_gettime_relative() - start_time >= timeout || *exit) {
			ret = -2;
			goto end;
		}
	}

	avframe_node* node = av_mallocz(sizeof(avframe_node));
	if (!node) {
		printf("frame_node is NULL\n");
		ret = -1;
		goto end;
	}

	node->frame = av_frame_alloc();
	if (!node->frame) {
		av_free(node);
		ret = -1;
		goto end;
	}

	if (frame->pts != AV_NOPTS_VALUE && input_fmt_start_time != AV_NOPTS_VALUE) {
		frame->pts -= input_fmt_start_time;
	}

	if (q->last_pts_value != AV_NOPTS_VALUE) {
		if (frame->pts != AV_NOPTS_VALUE)
		{
			q->last_interval = frame->pts - q->last_pts_value;
		}
		else if (frame->pts == AV_NOPTS_VALUE && q->last_interval != AV_NOPTS_VALUE)
		{
			frame->pts = q->last_pts_value + q->last_interval;
		}
	}
	q->last_pts_value = frame->pts;

	av_frame_move_ref(node->frame, frame);
	node->next = NULL;
	node->frame_id = frame_id;

	if (q->rear == NULL)
	{
		q->rear = node;
		q->front = node;
	}
	else
	{
		q->rear->next = node;
		q->rear = node;
	}

	q->count += 1;

	if (q->right_count != -1 && q->center_count != -1 && q->count >= q->right_count && q->front) {
		while (1) {
			avframe_node* temp = q->front->next;
			av_frame_free(&q->front->frame);
			av_free(q->front);
			q->count -= 1;
			if (!temp) {
				q->front = q->rear = NULL;
				break;
			}
			else {
				q->front = temp;
			}

			if (q->count <= q->center_count) {
				if (q->front) {
					q->front->reset = 1;
				}
				break;
			}
		}
	}

	if (q->center_count != -1 && q->count >= q->center_count && q->reached_center == 0) {
		q->reached_center = 1;
		if (q->front) {
			q->front->reset = 1;
		}
	}
end:
	LeaveCriticalSection(&q->mutex);
	return ret;
}


int frame_dequeue(frame_queue* q, AVFrame* frame, int64_t* frame_id, int*reset) {
	av_frame_unref(frame);
	int ret = -1;
	EnterCriticalSection(&q->mutex);
	*reset = 0;

	if (q->left_count != -1 && q->center_count != -1 && q->count <= q->left_count && q->reached_center == 1) {
		q->reached_center = 0;
	}

	if (!q->front || (q->center_count != -1 && !q->reached_center))
	{
		LeaveCriticalSection(&q->mutex);
		return -1;
	}

	avframe_node* temp = q->front->next;
	av_frame_move_ref(frame, q->front->frame);
	*frame_id = q->front->frame_id;
	*reset = q->front->reset;
	av_frame_free(&q->front->frame);
	av_free(q->front);
	if (!temp) {
		q->front = q->rear = NULL;
	}
	else {
		q->front = temp;
	}
	q->count -= 1;

	ret = 0;
	LeaveCriticalSection(&q->mutex);
	WakeAllConditionVariable(&q->cond);
	return ret;
}

frame_queue* frame_queue_alloc(int max_count)
{
	frame_queue *q = av_mallocz(sizeof(frame_queue));
	if (!q) return NULL;
	InitializeCriticalSection(&q->mutex);
	InitializeConditionVariable(&q->cond);
	q->max_count = max_count >= 0 ? max_count : 0;
	q->reached_center = 0;
	q->left_count = -1;
	q->center_count = -1;
	q->right_count = -1;
	q->last_pts_value = AV_NOPTS_VALUE;
	q->last_interval = AV_NOPTS_VALUE;
	return q;
}

void frame_queue_clean(frame_queue* q)
{
	EnterCriticalSection(&q->mutex);
	q->reached_center = 0;
	q->last_interval = AV_NOPTS_VALUE;
	q->last_pts_value = AV_NOPTS_VALUE;
	if (!q->front)
	{
		LeaveCriticalSection(&q->mutex);
		return;
	}
	while (1) {
		avframe_node* temp = q->front->next;
		av_frame_free(&q->front->frame);
		av_free(q->front);
		q->count -= 1;
		//printf("%d\n", q->count);
		if (!temp) {
			q->front = q->rear = NULL;
			LeaveCriticalSection(&q->mutex);
			break;
		}
		else {
			q->front = temp;
		}
	}
}

void frame_queue_free(frame_queue **q)
{
	if (!q || !(*q)) return;
	frame_queue_clean(*q);
	DeleteCriticalSection(&(*q)->mutex);
	av_free(*q);
	*q = NULL;
}

void frame_queue_set(frame_queue *q, int left_count, int right_count, int center_count) {
	EnterCriticalSection(&q->mutex);

	q->reached_center = 0;
	if (left_count == -1 || right_count == -1) {
		if (center_count != -1)
		{
			q->left_count = 0;
			q->right_count = INT32_MAX;
			q->center_count = center_count;
		}
		else {
			q->center_count = -1;
			q->left_count = -1;
			q->right_count = -1;
		}
		goto end;
	}

	if (left_count < 0) left_count = 0;

	if (right_count - left_count < 2) right_count = left_count + 2;

	if (center_count > left_count && center_count < right_count)
		q->center_count = center_count;
	else
		q->center_count = (right_count + left_count) / 2;
	q->left_count = left_count;
	q->right_count = right_count;
end:
	LeaveCriticalSection(&q->mutex);
}

void codec_context_init(codec_context* ctx)
{
	InitializeCriticalSection(&ctx->mutex);
	ctx->index = -1;
}


void codec_context_free(codec_context* ctx)
{
	avcodec_free_context(&ctx->avctx);
	DeleteCriticalSection(&ctx->mutex);
}

void packet_queue_init(packet_queue* q, int max_size, int max_count)
{
	InitializeCriticalSection(&q->mutex);
	InitializeConditionVariable(&q->cond);
	q->max_size = max_size;
	q->max_count = max_count;
}

void packet_queue_clean(packet_queue* q) {
	avpacket_node* temp;
	if (q->front == NULL)
		return;
	while (1) {
		temp = q->front->next;
		q->size -= q->front->pkt->size;
		q->count--;
		av_packet_free(&q->front->pkt);
		av_free(q->front);
		if (!temp) {
			q->front = q->rear = NULL;
			break;
		}
		else {
			q->front = temp;
		}
	}
}

void packet_queue_destroy(packet_queue* q)
{
	packet_queue_clean(q);
	DeleteCriticalSection(&q->mutex);
}

void packet_queue_destroy_all(inout_context* ctx)
{
	for (int i = 0; i < ARRAY_ELEMS(ctx->queues); i++) {
		packet_queue_destroy(&ctx->queues[i]);
	}
}
int packet_dequeue(packet_queue* q, AVPacket* pkt)
{
	av_packet_unref(pkt);

	EnterCriticalSection(&q->mutex);

	if (!q->front)
	{
		SleepConditionVariableCS(&q->cond, &q->mutex, COND_TIMEOUT);
		if (!q->front) {
			LeaveCriticalSection(&q->mutex);
			return -1;
		}
	}

	avpacket_node* temp = q->front->next;

	av_packet_move_ref(pkt, q->front->pkt);
	av_packet_free(&q->front->pkt);
	av_free(q->front);

	if (!temp)
		q->front = q->rear = NULL;
	else
		q->front = temp;
	q->size -= pkt->size;
	q->count--;

	LeaveCriticalSection(&q->mutex);
	WakeAllConditionVariable(&q->cond);
	return 0;
}

int packet_enqueue(packet_queue* q, AVPacket* pkt, int* exit)
{
	int ret = 0;
	EnterCriticalSection(&q->mutex);
	while (q->size >= q->max_size)
	{
		SleepConditionVariableCS(&q->cond, &q->mutex, COND_TIMEOUT);
		if (*exit) {
			ret = -2;
			goto end;
		}
	}

	avpacket_node* node = av_mallocz(sizeof(avpacket_node));
	AVPacket* node_pkt;
	if (!node) {
		printf("avpacket_node is NULL\n");
		ret = -1;
		goto end;
	}

	node_pkt = av_packet_clone(pkt);
	if (!node_pkt) {
		av_free(node);
		printf("node_pkt is NULL\n");
		ret = -1;
		goto end;
	}

	node->pkt = node_pkt;
	node->next = NULL;

	if (q->rear == NULL)
	{
		q->rear = node;
		q->front = node;
	}
	else
	{
		q->rear->next = node;
		q->rear = node;
	}
	q->size += pkt->size;
	q->count++;
	//printf("%d\n", q->count);
end:
	LeaveCriticalSection(&q->mutex);
	WakeAllConditionVariable(&q->cond);
	return ret;
}


inout_context* inout_context_alloc(
	int video_out_width, int video_out_height,int video_out_format,AVRational video_out_fps,
	int audio_out_sample_rate,int audio_out_format,const AVChannelLayout *audio_out_layout, int audio_out_nb_samples,
	int64_t timeout, int packet_queue_max_size, int packet_queue_max_count,int video_frame_queue_count, int audio_frame_queue_count,int use_fixed_frame_interval,
	int output_ajust_start_if_delay_over,int av_max_offset_time)
{
	inout_context* ctx = (inout_context*)av_mallocz(sizeof(inout_context));
	AVAudioFifo* audio_fifo = NULL;
	frame_queue* video_frame_queue;
	frame_queue* audio_frame_queue;
	frame_queue* decoded_video_frame_queue;
	if (!ctx) goto failed;

	for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++)
	{
		codec_context_init(&ctx->decoders[i]);
	}

	for (int i = 0; i < ARRAY_ELEMS(ctx->queues); i++) {
		packet_queue_init(&ctx->queues[i], packet_queue_max_size, packet_queue_max_count);
	}

	video_frame_queue = frame_queue_alloc(video_frame_queue_count);
	audio_frame_queue = frame_queue_alloc(audio_frame_queue_count);
	decoded_video_frame_queue = frame_queue_alloc(VIDEO_PROCESS_QUEUE_COUNT);

	if (!video_frame_queue || !audio_frame_queue || !decoded_video_frame_queue) {
		goto failed;
	}


	if (!(audio_fifo = av_audio_fifo_alloc(audio_out_format, audio_out_layout->nb_channels, 1)))
	{
		goto failed;
	}


	InitializeCriticalSection(&ctx->filter_text_mutex);
	InitializeCriticalSection(&ctx->input_change_mutex);
	ctx->hw_pix_fmt = AV_PIX_FMT_NONE;
	ctx->hw_type = AV_HWDEVICE_TYPE_NONE;
	ctx->frame_queues[0] = video_frame_queue;
	ctx->frame_queues[1] = audio_frame_queue;
	ctx->decoded_video_frame_queue = decoded_video_frame_queue;
	ctx->output_frame_width = video_out_width;
	ctx->output_frame_height = video_out_height;
	ctx->output_frame_format = video_out_format;
	ctx->output_scale_mode = OUTPUT_SCALE_MODE_LETTERBOX;
	ctx->output_display_aspect = (AVRational) { 0, 0 };
	ctx->output_fps = video_out_fps;
	ctx->output_time_per_video_frame = av_rescale_q_rnd(1, (AVRational) { video_out_fps.den, video_out_fps.num }, UNIVERSAL_TB, AV_ROUND_UP);
	ctx->output_video_interval_ns = use_fixed_frame_interval ? util_mul_div64(1000000000ULL, video_out_fps.den, video_out_fps.num) : 0;
	ctx->output_adjust_start_time_if_delay_count_over = output_ajust_start_if_delay_over < 0 ? 0 : output_ajust_start_if_delay_over;
	ctx->output_audio_sample_rate = audio_out_sample_rate;
	ctx->output_audio_format = audio_out_format;
	ctx->output_audio_nb_samples = audio_out_nb_samples;
	ctx->output_time_per_audio_frame = av_rescale_q_rnd(audio_out_nb_samples, (AVRational) { 1, audio_out_sample_rate }, UNIVERSAL_TB, AV_ROUND_UP);
	ctx->output_audio_fifo = audio_fifo;
	ctx->output_last_audio_frame_pts = AV_NOPTS_VALUE;
	ctx->output_last_audio_nb_samples = 0;
	ctx->output_last_audio_in_sync_time = AV_NOPTS_VALUE;
	ctx->output_time_offset = AV_NOPTS_VALUE;
	ctx->output_time_offset_last_adjust_time = AV_NOPTS_VALUE;
	ctx->av_max_offset_time = FFMAX(1000000, av_max_offset_time);
	av_channel_layout_copy(&ctx->output_audio_layout, audio_out_layout);
	ctx->timeout = timeout;
	ctx->last_packet_time = AV_NOPTS_VALUE;
	ctx->input_start_time = AV_NOPTS_VALUE;
	return ctx;

failed:
	if (ctx) {
		for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++)
		{
			codec_context_free(&ctx->decoders[i]);
		}
		packet_queue_destroy_all(ctx);
		frame_queue_free(&video_frame_queue);
		frame_queue_free(&audio_frame_queue);
		frame_queue_free(&decoded_video_frame_queue);
		av_channel_layout_uninit(&ctx->output_audio_layout);
		av_audio_fifo_free(audio_fifo);
		DeleteCriticalSection(&ctx->filter_text_mutex);
		DeleteCriticalSection(&ctx->input_change_mutex);
		av_free(ctx);
	}
	return NULL;
}

void inout_context_free(inout_context** ctx)
{
	if (!ctx || !(*ctx)) return;

	(*ctx)->force_exit = 1;
	if ((*ctx)->decoded_video_frame_queue) WakeAllConditionVariable(&(*ctx)->decoded_video_frame_queue->cond);
	free_thread(&(*ctx)->reading_tid);
	free_thread(&(*ctx)->special_source_tid);
	free_thread(&(*ctx)->decode_video_tid);
	free_thread(&(*ctx)->process_video_tid);
	free_thread(&(*ctx)->decode_audio_tid);

	avformat_close_input(&((*ctx)->fmt_ctx));
	packet_queue_destroy_all(*ctx);


	for (int i = 0; i < ARRAY_ELEMS((*ctx)->decoders); i++)
	{
		codec_context_free(&(*ctx)->decoders[i]);
	}

	for (int i = 0; i < ARRAY_ELEMS((*ctx)->filter_contexts); i++)
	{
		avfilter_graph_free(&(*ctx)->filter_contexts[i].filter_graph);
		avfilter_inout_free(&(*ctx)->filter_contexts[i].input);
		avfilter_inout_free(&(*ctx)->filter_contexts[i].output);
		(*ctx)->filter_contexts[i].buffer_src = NULL, (*ctx)->filter_contexts[i].buffer_sink = NULL;
	}

	sws_free_context(&(*ctx)->sws_ctx);
	swr_free(&(*ctx)->swr_ctx);

	av_buffer_unref(&(*ctx)->hw_device_ctx);
	avsubtitle_free(&(*ctx)->sub);

	DeleteCriticalSection(&(*ctx)->filter_text_mutex);
	DeleteCriticalSection(&(*ctx)->input_change_mutex);
	av_free((*ctx)->filter_text);
	av_free((*ctx)->audio_filter_text);
	av_freep(&(*ctx)->input_name);
	frame_queue_free(&(*ctx)->frame_queues[0]);
	frame_queue_free(&(*ctx)->frame_queues[1]);
	frame_queue_free(&(*ctx)->decoded_video_frame_queue);
	av_channel_layout_uninit(&(*ctx)->output_audio_layout);
	av_channel_layout_uninit(&(*ctx)->last_audio_layout);
	av_channel_layout_uninit(&(*ctx)->last_filtered_audio_layout);
	av_audio_fifo_free((*ctx)->output_audio_fifo);
	av_free(*ctx);
	*ctx = NULL;
}


void inout_context_reset_input(inout_context* ctx)
{
	if (!ctx) return;
	free_thread(&ctx->reading_tid);
	free_thread(&ctx->special_source_tid);
	free_thread(&ctx->decode_video_tid);
	free_thread(&ctx->process_video_tid);
	free_thread(&ctx->decode_audio_tid);

	avformat_close_input(&ctx->fmt_ctx);


	for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++)
	{
		avcodec_free_context(&ctx->decoders[i].avctx);
		ctx->decoders[i].exit = 0;
		ctx->decoders[i].index = -1;
		ctx->decoders[i].sent_eof = 0;
	}

	for (int i = 0; i < ARRAY_ELEMS(ctx->queues); i++) {
		packet_queue_clean(&ctx->queues[i]);
	}

	EnterCriticalSection(&ctx->input_change_mutex);
	for (int i = 0; i < ARRAY_ELEMS(ctx->frame_queues); i++) {
		frame_queue_clean(ctx->frame_queues[i]);
		frame_queue_set(ctx->frame_queues[i], -1, -1, -1);
	}
	frame_queue_clean(ctx->decoded_video_frame_queue);
	ctx->output_time_offset = AV_NOPTS_VALUE;
	ctx->output_time_offset_last_adjust_time = AV_NOPTS_VALUE;
	ctx->output_last_audio_frame_pts = AV_NOPTS_VALUE;
	ctx->output_last_audio_nb_samples = 0;
	ctx->output_last_audio_in_sync_time = AV_NOPTS_VALUE;
	av_audio_fifo_reset(ctx->output_audio_fifo);
	LeaveCriticalSection(&ctx->input_change_mutex);

	for (int i = 0; i < ARRAY_ELEMS(ctx->filter_contexts); i++)
	{
		avfilter_graph_free(&ctx->filter_contexts[i].filter_graph);
		avfilter_inout_free(&ctx->filter_contexts[i].input);
		avfilter_inout_free(&ctx->filter_contexts[i].output);
		ctx->filter_contexts[i].buffer_src = NULL, ctx->filter_contexts[i].buffer_sink = NULL;
	}

	sws_free_context(&ctx->sws_ctx);
	swr_free(&ctx->swr_ctx);

	av_buffer_unref(&ctx->hw_device_ctx);
	avsubtitle_free(&ctx->sub);



	ctx->hw_pix_fmt = AV_PIX_FMT_NONE;
	ctx->hw_type = AV_HWDEVICE_TYPE_NONE;
	ctx->last_frame_width = 0;
	ctx->last_frame_height = 0;
	ctx->last_frame_format = 0;
	ctx->last_audio_sample_rate = 0;
	ctx->last_audio_format = 0;
	av_channel_layout_uninit(&ctx->last_audio_layout);

	ctx->last_filtered_audio_sample_rate = 0;
	ctx->last_filtered_audio_format = 0;
	av_channel_layout_uninit(&ctx->last_filtered_audio_layout);

	ctx->last_sar = (AVRational){0,0};
	ctx->last_packet_time = AV_NOPTS_VALUE;
	av_freep(&ctx->input_name);
	ctx->special_source_running = 0;
	ctx->force_exit = 0;
	ctx->eof = 0;
	ctx->input_start_time = AV_NOPTS_VALUE;
}

int input_call_back(void* p)
{
	inout_context* ctx = p;
	if (av_gettime_relative() - ctx->last_clock_time >= ctx->timeout)
	{
		ctx->force_exit = 1;
	}
	return ctx->force_exit;
}

int parse_string_to_avdict(const char* str, AVDictionary** dict, char delimiter) {
	if (!str || !dict) {
		return -1;
	}

	char* input = av_strdup(str);
	if (!input) {
		return -1;
	}

	char* saveptr = NULL;
	char* pair = strtok_s(input, &delimiter, &saveptr);

	while (pair) {

		char* equals = strchr(pair, '=');
		if (equals) {
			*equals = '\0';
			char* key = pair;
			char* value = equals + 1;

			if (strlen(key) > 0) {
				int ret = av_dict_set(dict, key, value, 0);
				if (ret < 0) {
					av_freep(&input);
					return -1;
				}
			}
		}

		pair = strtok_s(NULL, &delimiter, &saveptr);
	}

	av_freep(&input);
	return 0;
}

int check_hw_decode(const AVCodec* decoder, enum AVHWDeviceType type)
{
	int i;
	for (i = 0;; i++) {
		const AVCodecHWConfig* config = avcodec_get_hw_config(decoder, i);

		if (!config) {
			fprintf(stderr, "Decoder %s does not support device type %s.\n",
				decoder->name, av_hwdevice_get_type_name(type));
			fprintf(stderr, "Available device types:");
			while ((type = av_hwdevice_iterate_types(type)) != AV_HWDEVICE_TYPE_NONE)
				fprintf(stderr, " %s", av_hwdevice_get_type_name(type));
			fprintf(stderr, "\n");
			return -1;
		}
		//printf("config->methods:%d,config->type:%d\n", config->methods, config->device_type);
		if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX &&
			config->device_type == type) {

			break;
		}
	}

	return 0;
}

int hw_decoder_init(AVBufferRef** hw_device_ctx, AVCodecContext* avctx, const enum AVHWDeviceType type)
{
	int ret = -1;
	if ((ret = av_hwdevice_ctx_create(hw_device_ctx, type,
		NULL, NULL, 0)) < 0) {
		fprintf(stderr, "Failed to create specified HW device.\n");
		return -1;
	}
	avctx->hw_device_ctx = av_buffer_ref(*hw_device_ctx);
	return 0;
}

enum AVPixelFormat get_hw_pix_fmt(const AVCodec* decoder, enum AVHWDeviceType type)
{
	int i;
	const AVCodecHWConfig* config = NULL;
	for (i = 0;; i++) {
		config = avcodec_get_hw_config(decoder, i);

		if (!config) {
			return AV_PIX_FMT_NONE;
		}
		//printf("config->methods:%d,config->type:%d\n", config->methods, config->device_type);
		if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX &&
			config->device_type == type) {
			break;
		}
	}

	return config->pix_fmt;
}


int find_decoders(AVFormatContext* fmt_ctx, inout_context* ctx, int video_index, int audio_index, int subtitle_index, char* hw_decode)
{
	AVDictionary* opts = NULL;
	enum AVHWDeviceType hw_type = AV_HWDEVICE_TYPE_NONE;
	if (hw_decode) hw_type = av_hwdevice_find_type_by_name(hw_decode);

	int hw_decode_success = 0;

	int must_has_video = video_index >= 0 ? 1 : 0;
	int must_has_audio = audio_index >= 0 ? 1 : 0;
	int must_has_subtitle = subtitle_index >= 0 ? 1 : 0;
	int got_v = 0;
	int got_a = 0;
	int got_s = 0;


	for (int i = 0; i < (int)fmt_ctx->nb_streams; i++) {
		const AVCodec* pCodec = NULL;
		int ret;
		enum AVMediaType type_index = -1;
		if (fmt_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO)
		{
			if (video_index > 0 || got_v) { video_index--; continue; }
			type_index = 0;
		}
		else if (fmt_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO)
		{
			if (audio_index > 0 || got_a) { audio_index--; continue; }
			type_index = 1;
		}
		else if (fmt_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_SUBTITLE)
		{
			if (subtitle_index > 0 || got_s) { subtitle_index--; continue; }
			type_index = 2;
		}
		else {
			continue;
		}

		if (!(pCodec = avcodec_find_decoder(fmt_ctx->streams[i]->codecpar->codec_id)))
		{
			continue;
		}

		AVCodecContext* avctx = avcodec_alloc_context3(pCodec);
		if (!avctx) continue;

		if (hw_type != AV_HWDEVICE_TYPE_NONE && type_index == 0) {
			if (check_hw_decode(pCodec, hw_type) >= 0)
			{
				if (hw_decoder_init(&ctx->hw_device_ctx, avctx, hw_type) >= 0)
				{
					hw_decode_success = 1;
				}
			}
		}

		ret = avcodec_parameters_to_context(avctx, fmt_ctx->streams[i]->codecpar);
		if (ret < 0) { avcodec_free_context(&avctx); continue; }

		av_dict_set(&opts, "threads", "auto", 0);
		ret = avcodec_open2(avctx, pCodec, &opts);
		av_dict_free(&opts);

		if (ret < 0) { avcodec_free_context(&avctx); continue; }
		else if (type_index == 0) {
			if (hw_decode_success) {
				ctx->hw_pix_fmt = get_hw_pix_fmt(pCodec, hw_type);
				DEBUG_LOG("init hardware decoder success!\n");
			}
			else {
				ctx->hw_pix_fmt = AV_PIX_FMT_NONE;
			}
		}

		avcodec_free_context(&ctx->decoders[type_index].avctx);
		ctx->decoders[type_index].avctx = avctx;
		ctx->decoders[type_index].index = i;

		switch (type_index)
		{
		case 0:
			got_v = 1;
			break;
		case 1:
			got_a = 1;
			break;
		case 2:
			got_s = 1;
			break;
		default:
			break;
		}
	}

	ctx->hw_type = hw_type;

	if (must_has_video > got_v ||
		must_has_audio > got_a ||
		must_has_subtitle > got_s) {
		ctx->hw_pix_fmt = AV_PIX_FMT_NONE;
		for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++) {
			avcodec_free_context(&ctx->decoders[i].avctx);
			ctx->decoders[i].index = -1;
		}
		return -1;
	}
	return 0;
}

int input_find_decoder(const char* name,
	enum AVMediaType type, const AVCodec** pcodec)
{
	const AVCodecDescriptor* desc;
	const char* codec_string = "decoder";
	const AVCodec* codec;

	codec = avcodec_find_decoder_by_name(name);

	if (!codec && (desc = avcodec_descriptor_get_by_name(name))) {
		codec = avcodec_find_decoder(desc->id);
		if (codec)
			av_log(NULL, AV_LOG_VERBOSE, "Matched %s '%s' for codec '%s'.\n",
				codec_string, codec->name, desc->name);
	}

	if (!codec) {
		av_log(NULL, AV_LOG_FATAL, "Unknown %s '%s'\n", codec_string, name);
		return AVERROR_DECODER_NOT_FOUND;
	}
	if (codec->type != type) {
		av_log(NULL, AV_LOG_FATAL, "Invalid %s type '%s'\n", codec_string, name);
		return AVERROR(EINVAL);
	}

	*pcodec = codec;
	return 0;
}


int open_input(char* fmt_name, char* name, AVDictionary** dict_opts, inout_context* ctx,
	int video_index, int audio_index, int subtitle_index, char* hw_decode, int probesize, int analyzeduration, int queue_left_count, int queue_right_count, int queue_center_count, char *vcodec_str,char* acodec_str,char * scodec_str, char* dcodec_str)
{

	if (name && (strcmp(name, "<TESTCARD>") == 0 || strcmp(name, "<TESTCARD2>") == 0 || strcmp(name, "<OBSVCAM>") == 0)) {
		av_freep(&ctx->input_name);
		ctx->input_name = av_strdup(name);
		if (strcmp(name, "<OBSVCAM>") == 0){
			frame_queue_set(ctx->frame_queues[0], queue_left_count, queue_right_count, queue_center_count);
		}
		return 0;
	}


	int ret = -1;
	AVFormatContext* fmt_ctx = NULL;
	if (!(fmt_ctx = avformat_alloc_context()))
	{
		DEBUG_LOG("avformat_alloc_context failed!\n");
		goto end;
	}

	const AVCodec* vcodec = NULL;
	const AVCodec* acodec = NULL;
	const AVCodec* scodec = NULL;
	const AVCodec* dcodec = NULL;

	if (vcodec_str)
		input_find_decoder(vcodec_str, AVMEDIA_TYPE_VIDEO, &vcodec);
	if (acodec_str)
		input_find_decoder(acodec_str, AVMEDIA_TYPE_AUDIO, &acodec);
	if (scodec_str)
		input_find_decoder(scodec_str, AVMEDIA_TYPE_SUBTITLE, &scodec);
	if (dcodec_str)
		input_find_decoder(dcodec_str, AVMEDIA_TYPE_DATA, &dcodec);

	fmt_ctx->video_codec = vcodec;
	fmt_ctx->audio_codec = acodec;
	fmt_ctx->subtitle_codec = scodec;
	fmt_ctx->data_codec = dcodec;

	fmt_ctx->video_codec_id = vcodec ? vcodec->id : AV_CODEC_ID_NONE;
	fmt_ctx->audio_codec_id = acodec ? acodec->id : AV_CODEC_ID_NONE;
	fmt_ctx->subtitle_codec_id = scodec ? scodec->id : AV_CODEC_ID_NONE;
	fmt_ctx->data_codec_id = dcodec ? dcodec->id : AV_CODEC_ID_NONE;

	ctx->last_clock_time = av_gettime_relative();
	//fmt_ctx->flags |= AVFMT_FLAG_NONBLOCK;
	fmt_ctx->interrupt_callback.callback = input_call_back;
	fmt_ctx->interrupt_callback.opaque = ctx;
	if(probesize > 0)
		fmt_ctx->probesize = probesize;
	if(analyzeduration > 0)
		fmt_ctx->max_analyze_duration = analyzeduration;
	const AVInputFormat* in_fmt = NULL;
	
	if (fmt_name) {
		in_fmt = av_find_input_format(fmt_name);
	}
	if ((ret = avformat_open_input(&fmt_ctx, name, in_fmt, dict_opts)) < 0)
	{
		goto end;
	}

	if ((ret = avformat_find_stream_info(fmt_ctx, NULL)) < 0)
	{
		DEBUG_LOG("avformat_find_stream_info() < 0\n");
		goto end;
	}

	if ((ret = find_decoders(fmt_ctx, ctx, video_index, audio_index, subtitle_index, hw_decode)) < 0)
	{
		DEBUG_LOG("find_decoders() < 0\n");
		goto end;
	}

	ctx->fmt_ctx = fmt_ctx;
	ctx->input_start_time = av_rescale_q_rnd(ctx->fmt_ctx->start_time, (AVRational) { 1, AV_TIME_BASE }, UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
	if (name)
	{
		av_freep(&ctx->input_name);
		ctx->input_name = av_strdup(name);
	}

	ctx->input_frame_id = av_gettime_relative();

	if(ctx->decoders[0].index >= 0)
		frame_queue_set(ctx->frame_queues[0], queue_left_count, queue_right_count, queue_center_count);
	else {
		frame_queue_set(ctx->frame_queues[1], queue_left_count, queue_right_count, queue_center_count);
	}

	return ret;

end:
	avformat_close_input(&fmt_ctx);
	return ret;
}

void set_last_packet_time(AVPacket *pkt, inout_context* ctx) {
	if (pkt->pts == AV_NOPTS_VALUE) return;
	int64_t start_time = ctx->fmt_ctx->streams[pkt->stream_index]->start_time;
	if (start_time == AV_NOPTS_VALUE) start_time = 0;
	ctx->last_packet_time = av_rescale_q(pkt->pts - start_time, ctx->fmt_ctx->streams[pkt->stream_index]->time_base, UNIVERSAL_TB);
}

DWORD WINAPI reading_input(LPVOID p)
{
	inout_context* ctx = (inout_context*)p;
	AVPacket* pkt = av_packet_alloc();

	int ret;

	while (1)
	{
		ret = av_read_frame(ctx->fmt_ctx, pkt);
		if (ret == AVERROR(EAGAIN)) {
			av_usleep(100000);
			if (ctx->force_exit) break;
			else continue;
		}
		else if(ret < 0){
			break;
		}

		ctx->last_clock_time = av_gettime_relative();

		if (pkt->pts == AV_NOPTS_VALUE)
		{
			pkt->pts = pkt->dts;
		}

		set_last_packet_time(pkt, ctx);


		if (pkt->stream_index == ctx->decoders[0].index)
		{
			if (packet_enqueue(&ctx->queues[0], pkt, &ctx->force_exit) < 0)
			{
				printf("packet_enqueue() failed!\n");
				break;
			}
		}
		else if (pkt->stream_index == ctx->decoders[1].index)
		{
			if (packet_enqueue(&ctx->queues[1], pkt, &ctx->force_exit) < 0)
			{
				printf("packet_enqueue() failed!\n");
				break;
			}
		}
		av_packet_unref(pkt);
		
	}
	av_packet_free(&pkt);

	ctx->eof = 1;
	return 0;
}

int decode_frame(inout_context* ctx, AVFrame* frame,AVPacket *pkt,AVFrame *decoded, enum AVMediaType type)
{
	av_packet_unref(pkt);
	av_frame_unref(frame);
	av_frame_unref(decoded);
	int ret = 0;
	int type_index = -1;
	switch (type)
	{
	case AVMEDIA_TYPE_VIDEO:
		type_index = 0;
		break;
	case AVMEDIA_TYPE_AUDIO:
		type_index = 1;
		break;
	default:
		DEBUG_LOG("unsupported type!\n");
		ret = -1;
		goto end;
	}

	EnterCriticalSection(&ctx->decoders[type_index].mutex);
	AVCodecContext* avctx = ctx->decoders[type_index].avctx;
	if (!avctx) {
		ret = -1;
		LeaveCriticalSection(&ctx->decoders[type_index].mutex);
		goto end;
	}

	while (avcodec_receive_frame(avctx, decoded) < 0)
	{
		if (packet_dequeue(&ctx->queues[type_index], pkt) >= 0) {
			if (avcodec_send_packet(avctx, pkt) < 0) {
				avcodec_flush_buffers(avctx);
			}
			av_packet_unref(pkt);
		}
		else {
			if (ctx->eof && !ctx->decoders[type_index].sent_eof) {
				ctx->decoders[type_index].sent_eof = 1;
				if (avcodec_send_packet(avctx, NULL) < 0) {
					avcodec_flush_buffers(avctx);
				}
			}
			else {
				ret = -1;
				break;
			}
		}
	}



	if (ret >= 0) {
		if (type_index == 0 && (decoded->format == ctx->hw_pix_fmt)) {
			if ((ret = av_hwframe_transfer_data(frame, decoded, 0)) < 0) {
				av_log(NULL,AV_LOG_ERROR,"Error transferring the data to system memory\n");
			}
		}
		else {
			ret = av_frame_ref(frame, decoded);
		}

		if (ret >= 0)
		{
			av_frame_copy_props(frame, decoded);
		}
	}

	LeaveCriticalSection(&ctx->decoders[type_index].mutex);

	if (ret >= 0)
	{
		frame->pts = frame->best_effort_timestamp;
		if (type == 1)
		{
			if (!frame->ch_layout.nb_channels)
				av_channel_layout_copy(&frame->ch_layout, &decoded->ch_layout);
			if (!frame->sample_rate) frame->sample_rate = avctx->sample_rate;
		}
	}
	else {
		ret = -1;
	}

end:
	return ret;
}

int need_reinit_filter(inout_context* ctx, int width, int height, int format, AVRational sar)
{
	if (ctx->needs_reinit_filter == 1) {
		ctx->needs_reinit_filter = 0;
		return 1;
	}

	if (ctx->last_frame_width != width || ctx->last_frame_height != height ||
		ctx->last_frame_format != format || ctx->last_sar.num != sar.num || ctx->last_sar.den != sar.den)
	{
		ctx->last_frame_width = width;
		ctx->last_frame_height = height;
		ctx->last_frame_format = format;
		ctx->last_sar.num = sar.num;
		ctx->last_sar.den = sar.den;
		return 1;
	}

	return 0;
}

int need_reinit_audio_filter(inout_context* ctx, AVChannelLayout *ch_layout, int sample_rate, int format)
{
	if (ctx->needs_reinit_audio_filter == 1) {
		ctx->needs_reinit_audio_filter = 0;
		return 1;
	}

	if (av_channel_layout_compare(ch_layout, &ctx->last_audio_layout) != 0 ||
		format != ctx->last_audio_format ||
		sample_rate != ctx->last_audio_sample_rate) { 
		av_channel_layout_uninit(&ctx->last_audio_layout);
		av_channel_layout_copy(&ctx->last_audio_layout, ch_layout);
		ctx->last_audio_sample_rate = sample_rate;
		ctx->last_audio_format = format;
		return 1;
	}
	return 0;
}

int init_video_filter(inout_context* ctx, int width, int height, enum AVPixelFormat pix_fmt, AVRational tb, AVRational sar)
{
	filter_context* fctx = &ctx->filter_contexts[0];
	avfilter_graph_free(&fctx->filter_graph);
	avfilter_inout_free(&fctx->input);
	avfilter_inout_free(&fctx->output);
	fctx->buffer_src = NULL, fctx->buffer_sink = NULL;

	EnterCriticalSection(&ctx->filter_text_mutex);
	char* filter = av_strdup(ctx->filter_text);
	LeaveCriticalSection(&ctx->filter_text_mutex);
	if (!filter)
	{
		//printf("filter text is NULL\n");
		return -1;
	}

	AVFilterGraph* video_filter_graph = NULL;
	AVFilterInOut* input = NULL, * output = NULL;
	AVFilterContext* buffer_src = NULL, * buffer_sink = NULL;
	int ret;
	char args[512] = { 0 };
	video_filter_graph = avfilter_graph_alloc();
	input = avfilter_inout_alloc();
	output = avfilter_inout_alloc();
	if (!video_filter_graph || !input || !output) goto failed;

	snprintf(args, sizeof(args),
		"video_size=%dx%d:pix_fmt=%d:time_base=%d/%d:pixel_aspect=%d/%d",
		width, height, pix_fmt,
		tb.num, tb.den, sar.num, sar.den);
	const AVFilter* buffersrc_filter = avfilter_get_by_name("buffer");
	const AVFilter* buffersink_filter = avfilter_get_by_name("buffersink");
	ret = avfilter_graph_create_filter(&buffer_src, buffersrc_filter, "in",
		args, NULL, video_filter_graph);
	if (ret < 0) goto failed;

	ret = avfilter_graph_create_filter(&buffer_sink, buffersink_filter, "out",
		NULL, NULL, video_filter_graph);

	if (ret < 0) goto failed;

	output->name = av_strdup("in");
	output->filter_ctx = buffer_src;
	output->pad_idx = 0;
	output->next = NULL;

	input->name = av_strdup("out");
	input->filter_ctx = buffer_sink;
	input->pad_idx = 0;
	input->next = NULL;

	if ((ret = avfilter_graph_parse_ptr(video_filter_graph, filter,
		&input, &output, NULL)) < 0)
	{
		goto failed;
	}

	if ((ret = avfilter_graph_config(video_filter_graph, NULL)) < 0)
	{
		goto failed;
	}

	fctx->filter_graph = video_filter_graph;
	fctx->input = input;
	fctx->output = output;
	fctx->buffer_src = buffer_src;
	fctx->buffer_sink = buffer_sink;
	av_free(filter);
	return 0;

failed:
	avfilter_graph_free(&video_filter_graph);
	avfilter_inout_free(&input);
	avfilter_inout_free(&output);
	av_free(filter);
	return -1;
}

static int init_audio_filter(inout_context* ctx, AVChannelLayout *ch_layout, int sample_rate, enum AVSampleFormat sample_fmt, AVRational tb)
{
	filter_context* fctx = &ctx->filter_contexts[1];
	avfilter_graph_free(&fctx->filter_graph);
	avfilter_inout_free(&fctx->input);
	avfilter_inout_free(&fctx->output);
	fctx->buffer_src = NULL, fctx->buffer_sink = NULL;

	EnterCriticalSection(&ctx->filter_text_mutex);
	char* filter = av_strdup(ctx->audio_filter_text);
	LeaveCriticalSection(&ctx->filter_text_mutex);
	if (!filter)
	{
		return -1;
	}
	char args[512];
	int ret = 0;
	AVFilterGraph* audio_filter_graph = NULL;
	AVFilterInOut* input = NULL, * output = NULL;
	AVFilterContext* buffer_src = NULL, * buffer_sink = NULL;

	const AVFilter* buffersrc_filter = avfilter_get_by_name("abuffer");
	const AVFilter* buffersink_filter = avfilter_get_by_name("abuffersink");
	audio_filter_graph = avfilter_graph_alloc();
	input = avfilter_inout_alloc();
	output = avfilter_inout_alloc();
	if (!audio_filter_graph || !input || !output) goto failed;
	const AVFilterLink* outlink;
	AVRational time_base = tb;


	ret = snprintf(args, sizeof(args),
		"time_base=%d/%d:sample_rate=%d:sample_fmt=%s:channel_layout=",
		time_base.num, time_base.den, sample_rate,
		av_get_sample_fmt_name(sample_fmt));
	av_channel_layout_describe(ch_layout, args + ret, sizeof(args) - ret);
	ret = avfilter_graph_create_filter(&buffer_src, buffersrc_filter, "in",
		args, NULL, audio_filter_graph);
	if (ret < 0) {
		av_log(NULL, AV_LOG_ERROR, "Cannot create audio buffer source\n");
		goto failed;
	}

	ret = avfilter_graph_create_filter(&buffer_sink, buffersink_filter, "out",NULL,NULL, audio_filter_graph);
	if (ret < 0) {
		av_log(NULL, AV_LOG_ERROR, "Cannot create audio buffer sink\n");
		goto failed;
	}

	output->name = av_strdup("in");
	output->filter_ctx = buffer_src;
	output->pad_idx = 0;
	output->next = NULL;

	input->name = av_strdup("out");
	input->filter_ctx = buffer_sink;
	input->pad_idx = 0;
	input->next = NULL;

	if ((ret = avfilter_graph_parse_ptr(audio_filter_graph, filter,
		&input, &output, NULL)) < 0)
		goto failed;

	if ((ret = avfilter_graph_config(audio_filter_graph, NULL)) < 0)
		goto failed;

	outlink = buffer_sink->inputs[0];
	av_channel_layout_describe(&outlink->ch_layout, args, sizeof(args));
	av_log(NULL, AV_LOG_INFO, "Output: srate:%dHz fmt:%s chlayout:%s\n",
		(int)outlink->sample_rate,
		(char*)av_x_if_null(av_get_sample_fmt_name(outlink->format), "?"),
		args);

	fctx->filter_graph = audio_filter_graph;
	fctx->input = input;
	fctx->output = output;
	fctx->buffer_src = buffer_src;
	fctx->buffer_sink = buffer_sink;
	av_free(filter);
	return 0;

failed:
	avfilter_graph_free(&audio_filter_graph);
	avfilter_inout_free(&input);
	avfilter_inout_free(&output);
	av_free(filter);
	return -1;
}


BOOL avframe_is_data_continuous(AVFrame* frame)
{
	const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(frame->format);
	int ret;
	int linesizes[4] = { 0 };
	ptrdiff_t linesizes2[4] = { 0 };
	size_t sizes[4] = { 0 };
	uint8_t* current_pointer;

	if (!desc)
		return FALSE;

	ret = av_image_fill_linesizes(linesizes, frame->format,
		frame->width);
	if (ret < 0)
		return FALSE;

	for (int i = 0; i < 4; i++)
		linesizes2[i] = linesizes[i];

	if ((ret = av_image_fill_plane_sizes(sizes, frame->format,
		frame->height, linesizes2)) < 0)
		return FALSE;

	for (int i = 0; i < 4; i++) {
		if (linesizes[i] != frame->linesize[i])
			return FALSE;
	}
	
	current_pointer = frame->data[0];
	for (int i = 0; i < 3; i++) {
		if (!frame->data[i + 1]) break;
		current_pointer += sizes[i];
		if (current_pointer != frame->data[i + 1]) {
			return FALSE;
		}
	}

	return TRUE;
}


static void flip_frame(AVFrame* frame)
{
	frame->data[0] += frame->linesize[0] * (frame->height - 1);
	frame->linesize[0] *= -1;
}

static int parse_output_scale_mode(const char* mode, int default_mode)
{
	if (!mode || mode[0] == '\0')
		return default_mode;
	if (_stricmp(mode, "fill") == 0 || _stricmp(mode, "stretch") == 0 || _stricmp(mode, "fullscreen") == 0)
		return OUTPUT_SCALE_MODE_FILL;
	if (_stricmp(mode, "letterbox") == 0 || _stricmp(mode, "fit") == 0 || _stricmp(mode, "keep_aspect") == 0)
		return OUTPUT_SCALE_MODE_LETTERBOX;
	return default_mode;
}

static const char* output_scale_mode_name(int mode)
{
	return mode == OUTPUT_SCALE_MODE_LETTERBOX ? "letterbox" : "fill";
}

static AVRational parse_display_aspect(const char* text, AVRational default_aspect)
{
	int num = 0, den = 0;
	double value = 0;

	if (!text || text[0] == '\0' || _stricmp(text, "auto") == 0 || _stricmp(text, "sar") == 0)
		return (AVRational) { 0, 0 };
	if (sscanf_s(text, "%d:%d", &num, &den) == 2 || sscanf_s(text, "%d/%d", &num, &den) == 2) {
		if (num > 0 && den > 0)
			return av_d2q((double)num / den, 100000);
		return default_aspect;
	}
	value = atof(text);
	if (value > 0)
		return av_d2q(value, 100000);
	return default_aspect;
}

static void format_display_aspect(AVRational aspect, char* buffer, int buffer_size)
{
	if (aspect.num > 0 && aspect.den > 0)
		sprintf_s(buffer, buffer_size, "%d:%d", aspect.num, aspect.den);
	else
		strcpy_s(buffer, buffer_size, "auto");
}
static int calc_letterbox_rect(const AVFrame* src, AVRational display_aspect, enum AVPixelFormat dst_format, int dst_width, int dst_height,
	int* out_x, int* out_y, int* out_width, int* out_height)
{
	int64_t display_width = src->width;
	int64_t display_height = src->height;
	AVRational sar = src->sample_aspect_ratio;
	int log2_chroma_w = 0, log2_chroma_h = 0;
	int align_w = 1, align_h = 1;

	if (display_aspect.num > 0 && display_aspect.den > 0) {
		display_width = display_aspect.num;
		display_height = display_aspect.den;
	}
	else if (sar.num > 0 && sar.den > 0) {
		display_width *= sar.num;
		display_height *= sar.den;
	}

	if (display_width <= 0 || display_height <= 0 || dst_width <= 0 || dst_height <= 0)
		return -1;

	if (display_width * dst_height > display_height * dst_width) {
		*out_width = dst_width;
		*out_height = (int)av_rescale(dst_width, display_height, display_width);
	}
	else {
		*out_height = dst_height;
		*out_width = (int)av_rescale(dst_height, display_width, display_height);
	}
	if (av_pix_fmt_get_chroma_sub_sample(dst_format, &log2_chroma_w, &log2_chroma_h) >= 0) {
		align_w = 1 << log2_chroma_w;
		align_h = 1 << log2_chroma_h;
	}

	*out_width = FFMAX(align_w, FFMIN(*out_width, dst_width));
	*out_height = FFMAX(align_h, FFMIN(*out_height, dst_height));
	*out_width &= ~(align_w - 1);
	*out_height &= ~(align_h - 1);
	*out_x = ((dst_width - *out_width) / 2) & ~(align_w - 1);
	*out_y = ((dst_height - *out_height) / 2) & ~(align_h - 1);
	return 0;
}

static int offset_frame_data(AVFrame* frame, int x, int y)
{
	int ret;
	int x_linesizes[4] = { 0 };
	ptrdiff_t dst_linesizes[4] = { 0 };
	ptrdiff_t y_offsets[4] = { 0 };

	ret = av_image_fill_linesizes(x_linesizes, frame->format, x);
	if (ret < 0)
		return ret;

	for (int i = 0; i < 4; i++) {
		dst_linesizes[i] = frame->linesize[i];
	}

	ret = av_image_fill_plane_sizes(y_offsets, frame->format, y, dst_linesizes);
	if (ret < 0)
		return ret;

	for (int i = 0; i < 4 && frame->data[i]; i++) {
		frame->data[i] += x_linesizes[i] + y_offsets[i];
	}

	return 0;
}

static int fill_black_rect(AVFrame* frame, int x, int y, int width, int height)
{
	const AVPixFmtDescriptor* desc = av_pix_fmt_desc_get(frame->format);
	ptrdiff_t linesizes[4] = { 0 };
	AVFrame rect = *frame;
	int ret;

	if (!desc || width <= 0 || height <= 0)
		return -1;

	ret = offset_frame_data(&rect, x, y);
	if (ret < 0)
		return ret;

	for (int i = 0; i < 4; i++)
		linesizes[i] = rect.linesize[i];

	if (desc->flags & AV_PIX_FMT_FLAG_ALPHA) {
		const uint32_t transparent_black[4] = { 0, 0, 0, 0 };
		return av_image_fill_color(rect.data, linesizes, rect.format,
			transparent_black, width, height, 0);
	}

	return av_image_fill_black(rect.data, linesizes, rect.format,
		desc->flags & AV_PIX_FMT_FLAG_RGB ? AVCOL_RANGE_JPEG : AVCOL_RANGE_MPEG,
		width, height);
}

static void fill_letterbox_black(AVFrame* frame, int x, int y, int width, int height)
{
	int right = x + width;
	int bottom = y + height;

	if (y > 0)
		fill_black_rect(frame, 0, 0, frame->width, y);
	if (bottom < frame->height)
		fill_black_rect(frame, 0, bottom, frame->width, frame->height - bottom);
	if (x > 0)
		fill_black_rect(frame, 0, y, x, height);
	if (right < frame->width)
		fill_black_rect(frame, right, y, frame->width - right, height);
}

int fill_output_video(inout_context* ctx, AVFrame* frame)
{
	int ret = 0, filp = 0;
	int scale_x = 0, scale_y = 0, scale_width = 0, scale_height = 0;
	int need_letterbox = 0;
	AVFrame *f = av_frame_alloc();
	if (!f) {
		goto end;
	}

	f->width = ctx->output_frame_width;
	f->height = ctx->output_frame_height;
	f->format = ctx->output_frame_format;

	if (f->format == AV_PIX_FMT_BGR24 || f->format == AV_PIX_FMT_0RGB32 || f->format == AV_PIX_FMT_RGB32) {
		filp = 1;
	}

	if (calc_letterbox_rect(frame, ctx->output_display_aspect, f->format, f->width, f->height, &scale_x, &scale_y, &scale_width, &scale_height) < 0) {
		scale_width = f->width;
		scale_height = f->height;
	}
	need_letterbox = ctx->output_scale_mode == OUTPUT_SCALE_MODE_LETTERBOX && (scale_x != 0 || scale_y != 0 || scale_width != f->width || scale_height != f->height);

	if (frame->width == f->width && frame->height == f->height &&
		frame->format == f->format && !need_letterbox)
	{
		if (!filp && avframe_is_data_continuous(frame)) {
			if ((ret = av_frame_ref(f, frame)) < 0) {
				goto end;
			}
		}
		else {
			if ((ret = get_video_buffer(f)) < 0) {
				goto end;
			}

			if(filp)
				flip_frame(f);

			av_frame_copy(f, frame);

			if (filp)
				flip_frame(f);
		}

		av_frame_copy_props(f, frame);
		if (frame_enqueue(ctx->frame_queues[0], f, ctx->timeout, ctx->input_frame_id, ctx->input_start_time, &ctx->force_exit) < 0) {
			ret = -1;
			goto end;
		}
		goto end;
	}
	if (!ctx->sws_ctx) {
		ctx->sws_ctx = sws_alloc_context();
		if (ctx->sws_ctx) {
			int nb_cpus = av_cpu_count();
			if (nb_cpus > 1)
				ctx->sws_ctx->threads = FFMIN(nb_cpus + 1, MAX_AUTO_THREADS);
			else
				ctx->sws_ctx->threads = 1;
		}
	}
	if (!ctx->sws_ctx) {
		ret = -1;
		goto end;
	}
	if ((ret = get_video_buffer(f)) < 0) {
		goto end;
	}

	if (need_letterbox) {
		fill_letterbox_black(f, scale_x, scale_y, scale_width, scale_height);
	}

	if (filp) {
		flip_frame(f);
	}
	av_frame_copy_props(f, frame); // 先复制属性，防止自动转换色域等，如需转色域，用视频滤镜
	if (need_letterbox) {
		AVFrame dst = *f;
		dst.width = scale_width;
		dst.height = scale_height;
		if (offset_frame_data(&dst, scale_x, scale_y) < 0) {
			ret = -1;
			goto end;
		}
		ret = sws_scale_frame(ctx->sws_ctx, &dst, frame);
	}
	else {
		ret = sws_scale_frame(ctx->sws_ctx, f, frame);
	}
	if (ret < 0) {
		goto end;
	}

	if (filp) {
		flip_frame(f);
	}

	if (frame_enqueue(ctx->frame_queues[0], f, ctx->timeout, ctx->input_frame_id, ctx->input_start_time, &ctx->force_exit) < 0) {
		ret = -1;
	}
end:
	av_frame_unref(frame);
	av_frame_free(&f);
	return ret;
}


int fill_output_audio(inout_context* ctx, AVFrame* frame)
{
	int ret = 0,converted_samples;
	AVFrame* f = av_frame_alloc();
	if (!f) {
		ret = -1;
		goto end;
	}


	if (av_channel_layout_compare(&frame->ch_layout, &ctx->last_filtered_audio_layout) != 0 ||
		frame->format != ctx->last_filtered_audio_format ||
		frame->sample_rate != ctx->last_filtered_audio_sample_rate) {

		if (av_channel_layout_compare(&frame->ch_layout, &ctx->output_audio_layout) != 0 ||
			frame->format != ctx->output_audio_format ||
			frame->sample_rate != ctx->output_audio_sample_rate) {

			swr_alloc_set_opts2(&ctx->swr_ctx, &ctx->output_audio_layout,
				ctx->output_audio_format,
				ctx->output_audio_sample_rate, &frame->ch_layout,
				frame->format, frame->sample_rate, 0, NULL);

			if (!ctx->swr_ctx) goto end;

			if (swr_init(ctx->swr_ctx) < 0) goto end;
		}
		else {
			swr_free(&ctx->swr_ctx);
		}
		av_channel_layout_uninit(&ctx->last_filtered_audio_layout);
		av_channel_layout_copy(&ctx->last_filtered_audio_layout, &frame->ch_layout);
		ctx->last_filtered_audio_sample_rate = frame->sample_rate;
		ctx->last_filtered_audio_format = frame->format;
	}

	if (ctx->swr_ctx) {
		int out_nb_samples = swr_get_out_samples(ctx->swr_ctx, frame->nb_samples);
		f->nb_samples = out_nb_samples;
		f->format = ctx->output_audio_format;
		av_channel_layout_copy(&f->ch_layout, &ctx->output_audio_layout);
	}
	else {
		f->nb_samples = frame->nb_samples;
		f->format = frame->format;
		av_channel_layout_copy(&f->ch_layout, &frame->ch_layout);
	}

	if (av_frame_get_buffer(f, 1) < 0) {
		goto end;
	}

	if (ctx->swr_ctx) {
		if ((converted_samples = swr_convert(ctx->swr_ctx, f->data, f->nb_samples, frame->data, frame->nb_samples)) < 0)
		{
			ret = -1;
			goto end;
		}
		else {
			f->nb_samples = converted_samples;
		}
	}
	else {
		av_frame_copy(f, frame);
	}
	av_frame_copy_props(f, frame);
	f->sample_rate = ctx->output_audio_sample_rate;

	if (frame_enqueue(ctx->frame_queues[1], f, ctx->timeout, ctx->input_frame_id, ctx->input_start_time, &ctx->force_exit) < 0) {
		ret = -1;
	}
end:
	av_frame_unref(frame);
	av_frame_free(&f);
	return ret;
}

DWORD test_card_thread(LPVOID p) {
	inout_context* ctx = (inout_context*)p;
	void* card = test_card_alloc(ctx->output_frame_width, ctx->output_frame_height, ctx->output_frame_format, ctx->output_fps, ctx->test_card_style);
	if (!card) return -1;
	ctx->input_frame_id = av_gettime_relative();
	char infoText[512] = { 0 };
	AVFrame* f;
	while (!ctx->force_exit) {
		EnterCriticalSection(&ctx->filter_text_mutex);
		snprintf(infoText, sizeof(infoText),
			"output start time shift (ns): %I64d\n"
			"frame output average block time (ns): %lf\n"
			"video queue count/max_count: %d/%d\n"
			"audio queue count/max_count: %d/%d\n"
			"fps: %d/%d\n"
			"fixed interval (ns): %I64d\n"
			"output start clock time (ns): %I64d\n"
			"video filter: %s\n"
			"audio filter: %s",
			ctx->output_start_shift_time,
			ctx->output_video_avg_block_time,
			ctx->frame_queues[0]->count,
			ctx->frame_queues[0]->max_count,
			ctx->frame_queues[1]->count,
			ctx->frame_queues[1]->max_count,
			ctx->output_fps.num,
			ctx->output_fps.den,  ctx->output_video_interval_ns, ctx->output_start_clock_time,
			ctx->filter_text,
			ctx->audio_filter_text);
		LeaveCriticalSection(&ctx->filter_text_mutex);
		if (f = test_card_draw(card, infoText)) {
			if (frame_enqueue(ctx->frame_queues[0], f, ctx->timeout, ctx->input_frame_id, ctx->input_start_time, &ctx->force_exit) < 0) {
				break;
			}
		}
		else break;
	}
	test_card_free(card);
	ctx->special_source_running = 0;
	return 0;
}

DWORD obs_virtual_cam_thread(LPVOID p) {
	inout_context* ctx = (inout_context*)p;
	OBSVirtualCamReader* reader = obs_virtual_cam_reader_create();
	if (!reader) return -1;
	ctx->input_frame_id = av_gettime_relative();
	AVFrame* f = NULL;
	int ret;
	uint64_t interval = 166666; //由于计算精度问题，60帧情况下interval比OBS渲染视频使用的要小，理论上读取间隔会比写入间隔短，所以理想情况下read_idx只会相同和相差1，永远不会丢帧
	uint64_t next_get_frame_time = os_gettime_ns();
	uint32_t read_idx = UINT32_MAX;
	uint32_t last_read_idx = UINT32_MAX;

	ctx->last_video_decode_time = av_gettime_relative();
	while (!ctx->force_exit) {
		obs_virtual_cam_reader_get_obs_frame(reader, &f, &interval, &read_idx);
		if (f) {
			ctx->last_video_decode_time = av_gettime_relative();
			f->pts = av_rescale_q(f->pts, NS_TB, UNIVERSAL_TB);
			ret = fill_output_video(ctx, f);
			av_frame_free(&f);
			if (ret < 0) goto end;
		}
		else if (obs_virtual_cam_reader_get_is_closed(reader)) {
			break;
		}

		if (av_gettime_relative() - ctx->last_video_decode_time >= ctx->timeout) {
			ctx->force_exit = 1;
			break;
		}

		if (last_read_idx == UINT32_MAX || read_idx - last_read_idx != 1) {
			//丢帧、复制帧了或者是第一帧，睡1ms马上继续读
			Sleep(1);
			//printf("read index: %u %u\n", read_idx, last_read_idx);
			next_get_frame_time = os_gettime_ns();
			last_read_idx = read_idx;
			continue;
		}
		else {
			next_get_frame_time += interval * 100ULL;
			last_read_idx = read_idx;
		}
		os_sleepto_ns(next_get_frame_time);
	}
end:
	av_frame_free(&f);
	obs_virtual_cam_reader_destroy(reader);
	ctx->special_source_running = 0;
	return 0;
}

static int process_decoded_video_frame(inout_context* ctx, AVFrame* frame, AVFrame* filtered_frame, int* filter_sent_eof)
{
	if (!(*filter_sent_eof) && need_reinit_filter(ctx, frame->width, frame->height, frame->format, frame->sample_aspect_ratio))
	{
		init_video_filter(ctx, frame->width, frame->height, frame->format,
			ctx->fmt_ctx->streams[ctx->decoders[0].index]->time_base, frame->sample_aspect_ratio);
	}

	if (ctx->filter_contexts[0].filter_graph)
	{
		if (av_buffersrc_add_frame(ctx->filter_contexts[0].buffer_src, frame) < 0)
		{
			av_frame_unref(frame);
			return 0;
		}

		while (av_buffersink_get_frame(ctx->filter_contexts[0].buffer_sink, filtered_frame) >= 0)
		{
			filtered_frame->pts = av_rescale_q_rnd(filtered_frame->pts, av_buffersink_get_time_base(ctx->filter_contexts[0].buffer_sink), UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
			if (fill_output_video(ctx, filtered_frame) < 0) return -1;
		}
	}
	else {
		frame->pts = av_rescale_q_rnd(frame->pts, ctx->fmt_ctx->streams[ctx->decoders[0].index]->time_base, UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
		if (fill_output_video(ctx, frame) < 0) return -1;
	}

	return 0;
}

static int flush_video_filter(inout_context* ctx, AVFrame* filtered_frame, int* filter_sent_eof)
{
	if (!(*filter_sent_eof) && ctx->filter_contexts[0].filter_graph) {
		*filter_sent_eof = 1;
		if (av_buffersrc_add_frame(ctx->filter_contexts[0].buffer_src, NULL) < 0)
		{
			return -1;
		}
	}

	if (ctx->filter_contexts[0].filter_graph)
	{
		while (av_buffersink_get_frame(ctx->filter_contexts[0].buffer_sink, filtered_frame) >= 0)
		{
			filtered_frame->pts = av_rescale_q_rnd(filtered_frame->pts, av_buffersink_get_time_base(ctx->filter_contexts[0].buffer_sink), UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
			if (fill_output_video(ctx, filtered_frame) < 0) return -1;
		}
	}
	return 0;
}

DWORD WINAPI process_video_thread(LPVOID p) {
	inout_context* ctx = (inout_context*)p;
	AVFrame* frame = av_frame_alloc();
	AVFrame* filtered_frame = av_frame_alloc();
	int64_t frame_id = 0;
	int reset = 0;
	int filter_sent_eof = 0;

	if (!frame || !filtered_frame) {
		goto end;
	}

	while (1)
	{
		if (frame_dequeue(ctx->decoded_video_frame_queue, frame, &frame_id, &reset) >= 0)
		{
			if (process_decoded_video_frame(ctx, frame, filtered_frame, &filter_sent_eof) < 0) goto end;
		}
		else {
			if (ctx->decoders[0].exit && frame_queue_is_empty(ctx->decoded_video_frame_queue))
			{
				flush_video_filter(ctx, filtered_frame, &filter_sent_eof);
				break;
			}
			if (ctx->force_exit)
				break;
		}
	}

end:
	av_frame_free(&filtered_frame);
	av_frame_free(&frame);
	return 0;
}

DWORD WINAPI decode_video_thread(LPVOID p) {
	inout_context* ctx = (inout_context*)p;
	AVFrame* frame = av_frame_alloc();
	AVFrame* decoded = av_frame_alloc();
	AVPacket* pkt = av_packet_alloc();

	if (!frame || !decoded || !pkt) {
		goto end;
	}

	ctx->last_video_decode_time = av_gettime_relative();
	while (1)
	{
		if (decode_frame(ctx, frame, pkt, decoded, AVMEDIA_TYPE_VIDEO) >= 0)
		{
			ctx->last_video_decode_time = av_gettime_relative();
			if (frame_enqueue(ctx->decoded_video_frame_queue, frame, ctx->timeout, ctx->input_frame_id, AV_NOPTS_VALUE, &ctx->force_exit) < 0) goto end;
		}
		else {
			if (av_gettime_relative() - ctx->last_video_decode_time >= ctx->timeout) {
				ctx->force_exit = 1;
				break;
			}
			if (ctx->eof)
			{
				break;
			}
		}
		if (ctx->force_exit)
			break;
	}

end:
	av_frame_free(&frame);
	av_frame_free(&decoded);
	av_packet_free(&pkt);
	ctx->decoders[0].exit = 1;
	if (ctx->decoded_video_frame_queue) WakeAllConditionVariable(&ctx->decoded_video_frame_queue->cond);
	return 0;
}

DWORD WINAPI decode_audio_thread(LPVOID p) {
	inout_context* ctx = (inout_context*)p;

	AVFrame* frame = av_frame_alloc();
	AVFrame* filtered_frame = av_frame_alloc();
	AVFrame* decoded = av_frame_alloc();
	AVPacket* pkt = av_packet_alloc();

	if (!frame || !filtered_frame || !decoded || !pkt) {
		goto end;
	}

	ctx->last_audio_decode_time = av_gettime_relative();

	int filter_sent_eof = 0;
	while (1)
	{
		if (ctx->filter_contexts[1].filter_graph)
		{
			while (av_buffersink_get_frame(ctx->filter_contexts[1].buffer_sink, filtered_frame) >= 0)
			{
				filtered_frame->pts = av_rescale_q_rnd(filtered_frame->pts, av_buffersink_get_time_base(ctx->filter_contexts[1].buffer_sink), UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
				if (fill_output_audio(ctx, filtered_frame) < 0) goto end;
			}
		}

		if (decode_frame(ctx, frame, pkt, decoded, AVMEDIA_TYPE_AUDIO) >= 0)
		{
			ctx->last_audio_decode_time = av_gettime_relative();

			if (!filter_sent_eof && need_reinit_audio_filter(ctx, &frame->ch_layout, frame->sample_rate, frame->format))
			{
				init_audio_filter(ctx, &frame->ch_layout, frame->sample_rate, frame->format,
					ctx->fmt_ctx->streams[ctx->decoders[1].index]->time_base);
			}

			if (ctx->filter_contexts[1].filter_graph)
			{
				if (av_buffersrc_add_frame(ctx->filter_contexts[1].buffer_src, frame) < 0)
				{
					av_frame_unref(frame);
				}
			}
			else {
				frame->pts = av_rescale_q_rnd(frame->pts, ctx->fmt_ctx->streams[ctx->decoders[1].index]->time_base, UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
				if (fill_output_audio(ctx, frame) < 0) goto end;
			}

		}
		else {
			if (av_gettime_relative() - ctx->last_audio_decode_time >= ctx->timeout) {
				ctx->force_exit = 1;
				break;
			}
			if (ctx->eof)
			{
				if (!filter_sent_eof && ctx->filter_contexts[1].filter_graph) {
					filter_sent_eof = 1;
					if (av_buffersrc_add_frame(ctx->filter_contexts[1].buffer_src, NULL) < 0)
					{
						break;
					}
				}
				else {
					break;
				}
			}
		}

		if (ctx->force_exit)
			break;
	}

end:
	av_frame_free(&filtered_frame);
	av_frame_free(&frame);
	av_frame_free(&decoded);
	av_packet_free(&pkt);
	ctx->decoders[1].exit = 1;
	return 0;
}

int start_read(inout_context* ctx) {
	int ret = -1;
	HANDLE read_tid = NULL;
	ctx->force_exit = 0;
	ctx->eof = 0;
	for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++) {
		ctx->decoders[i].exit = 0;
		ctx->decoders[i].sent_eof = 0;
	}
	HANDLE special_source_tid = NULL;
	HANDLE decode_video_tid = NULL;
	HANDLE process_video_tid = NULL;
	HANDLE decode_audio_tid = NULL;

	if (ctx->input_name) {
		if (strcmp(ctx->input_name, "<TESTCARD>") == 0 || strcmp(ctx->input_name, "<TESTCARD2>") == 0) {
			ctx->special_source_running = 1;
			if(strcmp(ctx->input_name, "<TESTCARD2>") == 0){
				ctx->test_card_style = 1;
			}
			else {
				ctx->test_card_style = 0;
			}
			ret = open_thread(&special_source_tid, test_card_thread, ctx);
			if (ret < 0) goto failed;
			ctx->special_source_tid = special_source_tid;
			return 0;
		}
		else if (strcmp(ctx->input_name, "<OBSVCAM>") == 0) {
			ctx->special_source_running = 1;
			ret = open_thread(&special_source_tid, obs_virtual_cam_thread, ctx);
			if (ret < 0) goto failed;
			ctx->special_source_tid = special_source_tid;
			return 0;
		}
	}

	if (ctx->fmt_ctx)
	{
		ret = open_thread(&read_tid, reading_input, ctx);
		if (ret < 0) goto failed;
	}
	if (ctx->decoders[0].avctx)
	{
		ret = open_thread(&decode_video_tid, decode_video_thread, ctx);
		if (ret < 0) goto failed;
		ret = open_thread(&process_video_tid, process_video_thread, ctx);
		if (ret < 0) goto failed;
	}
	if (ctx->decoders[1].avctx)
	{
		ret = open_thread(&decode_audio_tid, decode_audio_thread, ctx);
		if (ret < 0) goto failed;
	}


	ctx->reading_tid = read_tid;
	ctx->decode_video_tid = decode_video_tid;
	ctx->process_video_tid = process_video_tid;
	ctx->decode_audio_tid = decode_audio_tid;
	return 0;

failed:
	ctx->force_exit = 1;

	free_thread(&read_tid);
	free_thread(&special_source_tid);
	free_thread(&decode_video_tid);
	free_thread(&process_video_tid);
	free_thread(&decode_audio_tid);
	return -1;
}

void stop_read(inout_context* ctx, int force)
{
	if (force) {
		ctx->force_exit = 1;
		if (ctx->decoded_video_frame_queue) WakeAllConditionVariable(&ctx->decoded_video_frame_queue->cond);
	}
	free_thread(&ctx->reading_tid);
	free_thread(&ctx->special_source_tid);
	free_thread(&ctx->decode_video_tid);
	free_thread(&ctx->process_video_tid);
	free_thread(&ctx->decode_audio_tid);
}
void reset_after_seek(inout_context* ctx)
{
	if (!ctx) return;
	ctx->force_exit = 0;
	ctx->eof = 0;
	ctx->last_packet_time = AV_NOPTS_VALUE;
	for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++) {
		ctx->decoders[i].exit = 0;
		ctx->decoders[i].sent_eof = 0;
		if (ctx->decoders[i].avctx) {
			avcodec_flush_buffers(ctx->decoders[i].avctx);
		}
	}
	for (int i = 0; i < ARRAY_ELEMS(ctx->queues); i++) {
		packet_queue_clean(&ctx->queues[i]);
	}
	for (int i = 0; i < ARRAY_ELEMS(ctx->frame_queues); i++) {
		frame_queue_clean(ctx->frame_queues[i]);
	}
	frame_queue_clean(ctx->decoded_video_frame_queue);
	for (int i = 0; i < ARRAY_ELEMS(ctx->filter_contexts); i++) {
		avfilter_graph_free(&ctx->filter_contexts[i].filter_graph);
		avfilter_inout_free(&ctx->filter_contexts[i].input);
		avfilter_inout_free(&ctx->filter_contexts[i].output);
		ctx->filter_contexts[i].buffer_src = NULL;
		ctx->filter_contexts[i].buffer_sink = NULL;
	}
	ctx->needs_reinit_filter = 1;
	ctx->needs_reinit_audio_filter = 1;
	ctx->output_time_offset = AV_NOPTS_VALUE;
	ctx->output_time_offset_last_adjust_time = AV_NOPTS_VALUE;
	ctx->output_last_audio_frame_pts = AV_NOPTS_VALUE;
	ctx->output_last_audio_nb_samples = 0;
	ctx->output_last_audio_in_sync_time = AV_NOPTS_VALUE;
	av_audio_fifo_reset(ctx->output_audio_fifo);
}


void control_output_speed(inout_context* ctx)
{
	int64_t start_clock_time_with_shift = ctx->output_start_clock_time + ctx->output_start_shift_time;
	if (os_sleepto_ns(ctx->output_next_target_clock_time_ns + start_clock_time_with_shift))
	{
		DEBUG_LOG("diff_after_sleep: %I64d ns\n", (ctx->output_next_target_clock_time_ns - ((int64_t)os_gettime_ns() - start_clock_time_with_shift)));
	}
	else {
		int64_t new_output_start_clock_time = (int64_t)os_gettime_ns() - ctx->output_next_target_clock_time_ns;
		int64_t count;
		if (ctx->output_video_interval_ns) {
			count = (new_output_start_clock_time - (ctx->output_first_start_clock_time + ctx->output_start_shift_time)) / ctx->output_video_interval_ns;
			if(count > ctx->output_adjust_start_time_if_delay_count_over)
				ctx->output_start_clock_time = ctx->output_first_start_clock_time + count * ctx->output_video_interval_ns;
		}
		else {
			count = av_rescale_q(new_output_start_clock_time - (ctx->output_first_start_clock_time + ctx->output_start_shift_time), NS_TB, (AVRational) { ctx->output_fps.den, ctx->output_fps.num });
			if (count > ctx->output_adjust_start_time_if_delay_count_over)
				ctx->output_start_clock_time = ctx->output_first_start_clock_time + av_rescale_q(count, (AVRational) { ctx->output_fps.den, ctx->output_fps.num }, NS_TB);
		}
		if(count > ctx->output_adjust_start_time_if_delay_count_over)
			printf("reset output_start_clock_time %I64d\n",ctx->output_start_clock_time);
	}
}


int is_audio_fifo_full_fill(inout_context* ctx,int shift_samples)
{
	return av_audio_fifo_size(ctx->output_audio_fifo) + shift_samples >= ctx->output_audio_nb_samples;
}

int fill_audio_fifo(inout_context* ctx, AVFrame* frame)
{
	int ret = -1;
	if (!frame || frame->sample_rate != ctx->output_audio_sample_rate ||
		av_channel_layout_compare(&frame->ch_layout, &ctx->output_audio_layout) != 0 || frame->format != ctx->output_audio_format) goto end;

	if (av_audio_fifo_write(ctx->output_audio_fifo, frame->data, frame->nb_samples) < 0) {
		goto end;
	}

	ctx->output_last_audio_frame_pts = frame->pts; //保存下来，用于计算fifo后音频的pts
	ctx->output_last_audio_nb_samples = frame->nb_samples;
end:
	return ret;
}

void get_audio_fifo_pts(inout_context* ctx, int64_t *frame_pts, int64_t *frame_end_pts, int shift_samples) {
	int64_t _frame_end_pts;
	int64_t fifo_total_time;
	int64_t shift_time;
	int64_t fifo_size = av_audio_fifo_size(ctx->output_audio_fifo);
	if (fifo_size > 0 && ctx->output_last_audio_frame_pts != AV_NOPTS_VALUE) {
		_frame_end_pts = ctx->output_last_audio_frame_pts + av_rescale_q(ctx->output_last_audio_nb_samples, (AVRational) { 1, ctx->output_audio_sample_rate }, UNIVERSAL_TB);
		fifo_total_time = av_rescale_q(fifo_size, (AVRational) { 1, ctx->output_audio_sample_rate }, UNIVERSAL_TB);
		shift_time = av_rescale_q(shift_samples, (AVRational) { 1, ctx->output_audio_sample_rate }, UNIVERSAL_TB);
		*frame_pts = _frame_end_pts - fifo_total_time - shift_time;
		*frame_end_pts = _frame_end_pts;
	}
	else {
		*frame_pts = AV_NOPTS_VALUE;
		*frame_end_pts = AV_NOPTS_VALUE;
	}
}

static void shift_audio_pointer(enum AVSampleFormat format,int nb_channels, uint8_t** data, int data_elem_count, int nb_samples)
{
	const int planar = av_sample_fmt_is_planar(format);
	const int planes = planar ? nb_channels : 1;
	const int    bps = av_get_bytes_per_sample(format);
	const int offset = nb_samples * bps * (planar ? 1 : nb_channels);

	for (int i = 0; i < planes; i++) {
		if (i < data_elem_count)
			data[i] += offset;
	}
}

int audio_fifo_read_frame(inout_context* ctx, AVFrame* frame,int shift_samples)
{
	int ret;
	int64_t fifo_pts, fifo_end_pts;
	get_audio_fifo_pts(ctx, &fifo_pts, &fifo_end_pts, shift_samples);
	ret = av_frame_make_writable(frame);
	if (ret < 0) return ret;
	if (shift_samples > 0) {
		uint8_t* data[AV_NUM_DATA_POINTERS];
		for (int i = 0; i < FF_ARRAY_ELEMS(frame->data); i++) {
			data[i] = frame->data[i];
		}
		av_samples_set_silence(data, 0, shift_samples, frame->ch_layout.nb_channels, frame->format);
		shift_audio_pointer(frame->format,frame->ch_layout.nb_channels, data, AV_NUM_DATA_POINTERS, shift_samples);
		ret = av_audio_fifo_read(ctx->output_audio_fifo, data, frame->nb_samples - shift_samples);
		if (ret < 0) return ret;
	}
	else if (shift_samples < 0) {
		ret = av_audio_fifo_drain(ctx->output_audio_fifo, -shift_samples);
		if (ret < 0) return ret;
		ret = av_audio_fifo_read(ctx->output_audio_fifo, frame->data, frame->nb_samples);
	}
	else {
		ret = av_audio_fifo_read(ctx->output_audio_fifo, frame->data, frame->nb_samples);
	}

	frame->pts = fifo_pts;

	return ret;
}



void sync_output(inout_context* ctx, int64_t timestamp)
{
	//audio_id小于video_id代表此音频帧已是上一个文件的了，不能用于同步下一个文件的视频，而大于的话则代表还没读到最新文件的视频帧。
	if (timestamp == AV_NOPTS_VALUE || ctx->output_current_video_frame_id > ctx->output_current_audio_frame_id)
	{
		return;
	}

	int64_t offset_diff = ctx->output_audio_pts_time - timestamp - ctx->output_time_offset;
	//printf("%I64d\n", offset_diff);
	if (ctx->output_time_offset == AV_NOPTS_VALUE || offset_diff > ctx->output_time_per_video_frame / 4 || offset_diff < -ctx->output_time_per_video_frame / 4)
	{
		ctx->output_time_offset = ctx->output_audio_pts_time - timestamp;
	}
}

int update_offset_if_needed(inout_context* ctx, int64_t timestamp,int is_video)
{
	if (timestamp == AV_NOPTS_VALUE)
		return 0;

	if (is_video && ctx->output_current_video_frame_id < ctx->output_current_audio_frame_id) return 0;
	if (!is_video && ctx->output_current_audio_frame_id < ctx->output_current_video_frame_id) return 0;
	int ret = 0;
	int64_t output_pts_time = (is_video ? ctx->output_video_pts_time : ctx->output_audio_pts_time);
	int64_t current_time;
	if (ctx->output_time_offset == AV_NOPTS_VALUE)
	{
		current_time = av_gettime_relative();
		ctx->output_time_offset_last_adjust_time = current_time;
		ctx->output_time_offset = output_pts_time - timestamp;
		ret = 1;
		goto end;
	}
	else{
		int64_t offset_diff = timestamp + ctx->output_time_offset - output_pts_time;
		//printf("%I64d\n", offset_diff);
		if (offset_diff > ctx->av_max_offset_time || offset_diff < -ctx->av_max_offset_time) 
		{
			current_time = av_gettime_relative();
			if (ctx->output_time_offset_last_adjust_time == AV_NOPTS_VALUE || current_time - ctx->output_time_offset_last_adjust_time > 1 * 1000000) { // 1秒仅可调一次
				ctx->output_time_offset_last_adjust_time = current_time;
				ctx->output_time_offset = output_pts_time - timestamp;
				ret = 1;
				goto end;
			}
		}
	}
end:
	return ret;
}

int is_in_sync(inout_context* ctx, int64_t timestamp)//1:刚好，2:快了，0：慢了(或者必须取新帧)
{
	if (timestamp == AV_NOPTS_VALUE || ctx->output_time_offset == AV_NOPTS_VALUE) return 0;

	if (ctx->output_current_video_frame_id < ctx->output_current_audio_frame_id) return 0;

	int64_t output_pts_time = ctx->output_video_pts_time;
	int64_t interval = ctx->output_time_per_video_frame;

	int64_t diff = timestamp + ctx->output_time_offset - output_pts_time;
	if (diff >= -0.75 * interval && diff < 0.75 * interval) {
		return 1;
	}
	else if (diff > 0) {
		return 2;
	}
	else
	{
		return 0;
	}
}



#define AUDIO_SYNC_TOLERATE 5000
int is_audio_in_sync(inout_context* ctx,int *shift_samples)//1:刚好，2:快了，0：慢了(或者必须取新帧)
{
	int64_t fifo_pts;
	int64_t fifo_end_pts;

	*shift_samples = 0;

	if (ctx->output_current_audio_frame_id < ctx->output_current_video_frame_id || ctx->output_time_offset == AV_NOPTS_VALUE) return 0;

	get_audio_fifo_pts(ctx, &fifo_pts, &fifo_end_pts,0);
	if (fifo_pts == AV_NOPTS_VALUE) return 0;

	int64_t output_pts_time = ctx->output_audio_pts_time;
	int64_t output_next_pts_time = ctx->output_audio_next_pts_time;

	int64_t diff = fifo_pts + ctx->output_time_offset - output_pts_time;
	int64_t diff_end = fifo_end_pts + ctx->output_time_offset - output_pts_time;
	if (diff >= -AUDIO_SYNC_TOLERATE && diff < AUDIO_SYNC_TOLERATE) {
		ctx->output_last_audio_in_sync_time = output_pts_time;
		return 1;
	}
	else if (ctx->output_last_audio_in_sync_time != AV_NOPTS_VALUE && output_pts_time - ctx->output_last_audio_in_sync_time <= 200 * 1000) { //允许音频时间戳不稳定
		return 1;
	}
	else if (diff < output_next_pts_time - output_pts_time && diff_end > 0) {
		*shift_samples = (int)av_rescale_q(diff, UNIVERSAL_TB, (AVRational) { 1, ctx->output_audio_sample_rate });
		return 1;
	}
	else if (diff > 0) {
		return 2;
	}
	else
	{
		return 0;
	}
}



int64_t get_sync_diff(inout_context* ctx, int64_t timestamp)
{
	if (timestamp == AV_NOPTS_VALUE || ctx->output_time_offset == AV_NOPTS_VALUE)
		return AV_NOPTS_VALUE;


	return timestamp + ctx->output_time_offset - ctx->output_video_pts_time;
}


int64_t compute_audio_pts_time(inout_context* ctx,int64_t sample_count) {
	return av_rescale_q(sample_count,
		(AVRational) {
		1, ctx->output_audio_sample_rate
	}, UNIVERSAL_TB);
}

void compute_audio_pts_time_three(inout_context* ctx) {
	ctx->output_audio_prev_pts_time = compute_audio_pts_time(ctx, ctx->output_sample_count - ctx->output_audio_nb_samples);
	ctx->output_audio_pts_time = compute_audio_pts_time(ctx, ctx->output_sample_count);
	ctx->output_audio_next_pts_time = compute_audio_pts_time(ctx, ctx->output_sample_count + ctx->output_audio_nb_samples);
}

int output_audio(inout_context* ctx,AVFrame *frame)
{
	frame->pts = ctx->output_audio_pts_time;
	if (ctx->audio_callback) {
		ctx->audio_callback(ctx->callback_private, frame);
	}
	//printf("output audio: %lld", ctx->output_audio_pts_time);
	ctx->output_sample_count += ctx->output_audio_nb_samples;
	compute_audio_pts_time_three(ctx);
	return 0;
}

int output_video(inout_context* ctx, AVFrame* frame)
{
	frame->pts = ctx->output_video_pts_time;
	frame->pict_type = AV_PICTURE_TYPE_NONE;
	if (ctx->video_callback) {
		uint64_t start_time = os_gettime_ns();
		ctx->video_callback(ctx->callback_private, frame);
		uint64_t end_time = os_gettime_ns();
		ctx->output_video_avg_block_time = ctx->output_video_avg_block_time * 0.99 + (end_time - start_time) * 0.01;
	}
	//printf("output video: %lld", ctx->output_video_pts_time);
	ctx->output_frame_count += 1;
	ctx->output_video_pts_time = av_rescale_q(ctx->output_frame_count,
		(AVRational) { ctx->output_fps.den, ctx->output_fps.num }, UNIVERSAL_TB);
	if (ctx->output_video_interval_ns) {
		ctx->output_next_target_clock_time_ns = ctx->output_frame_count * ctx->output_video_interval_ns;
	}
	else {
		ctx->output_next_target_clock_time_ns = av_rescale_q(ctx->output_frame_count,
			(AVRational) {
			ctx->output_fps.den, ctx->output_fps.num
		}, NS_TB);
	}
	return 0;
}

void reset_audio_fifo(inout_context* ctx) {
	av_audio_fifo_reset(ctx->output_audio_fifo);
}

HRESULT WINAPI output_thread(LPVOID p)
{
	int ret = 0;
	inout_context* ctx = (inout_context*)p;
	AVFrame* video_frame = av_frame_alloc();
	AVFrame* audio_frame = av_frame_alloc();
	AVFrame* final_video_frame = av_frame_alloc();
	AVFrame* final_audio_frame = av_frame_alloc();
	AVFrame* fifo_out_audio_frame = av_frame_alloc();
	AVFrame* empty_audio_frame = av_frame_alloc();

	if (!video_frame || !audio_frame || !final_video_frame || !empty_audio_frame || !final_audio_frame || !fifo_out_audio_frame) {
		ret = -1;
		goto end;
	}

	final_video_frame->width = ctx->output_frame_width;
	final_video_frame->height = ctx->output_frame_height;
	final_video_frame->format = ctx->output_frame_format;

	//TODO 填充彩条
	if (get_video_buffer(final_video_frame) < 0)
	{
		printf("get_video_buffer failed!\n");
		ret = -1;
		goto end;
	}

	av_channel_layout_copy(&fifo_out_audio_frame->ch_layout, &ctx->output_audio_layout);
	fifo_out_audio_frame->format = ctx->output_audio_format;
	fifo_out_audio_frame->nb_samples = ctx->output_audio_nb_samples;

	if (av_frame_get_buffer(fifo_out_audio_frame, 1) < 0)
	{
		printf("av_frame_get_buffer failed!\n");
		ret = -1;
		goto end;
	}

	av_channel_layout_copy(&empty_audio_frame->ch_layout, &ctx->output_audio_layout);
	empty_audio_frame->format = ctx->output_audio_format;
	empty_audio_frame->nb_samples = ctx->output_audio_nb_samples;

	if (av_frame_get_buffer(empty_audio_frame, 1) < 0)
	{
		printf("av_frame_get_buffer failed!\n");
		ret = -1;
		goto end;
	}

	av_samples_set_silence(empty_audio_frame->data, 0, empty_audio_frame->nb_samples, empty_audio_frame->ch_layout.nb_channels, empty_audio_frame->format);
	int64_t last_video_sync_diff = AV_NOPTS_VALUE;
	int64_t last_audio_sync_diff = AV_NOPTS_VALUE;

	int reset;
	int audio_in_sync = 0;
	int in_sync_result = 0;

	int64_t current_time = os_gettime_ns();
	int64_t count;
	int64_t fifo_pts;
	int64_t fifo_end_pts;
	int shift_samples = 0;

	if (ctx->output_video_interval_ns) {
		count = current_time / ctx->output_video_interval_ns;
		current_time =  count * ctx->output_video_interval_ns;
	}
	else {
		count = av_rescale_q(current_time, NS_TB, (AVRational) { ctx->output_fps.den, ctx->output_fps.num });
		current_time = av_rescale_q(count, (AVRational) { ctx->output_fps.den, ctx->output_fps.num }, NS_TB);
	}
	ctx->output_first_start_clock_time = ctx->output_start_clock_time = current_time;

	compute_audio_pts_time_three(ctx);

	while (1)
	{
		if (ctx->output_exit) break;
		control_output_speed(ctx);//每编码完一轮音频+一帧视频就会进到这里看有没有快了，快了就sleep
		EnterCriticalSection(&ctx->input_change_mutex);

		while (ctx->output_audio_pts_time <= ctx->output_video_pts_time)
		{
		audio_loop:
			audio_in_sync = 0;
			shift_samples = 0;
			while (1) {
				in_sync_result = is_audio_in_sync(ctx, &shift_samples);
				if (in_sync_result == 0) {
					reset_audio_fifo(ctx);
				}
				else if (in_sync_result == 2) {
					break;
				}
				else if(is_audio_fifo_full_fill(ctx, shift_samples)){
					audio_fifo_read_frame(ctx, fifo_out_audio_frame, shift_samples);
					goto sync_end;
				}
					
				ret = frame_dequeue(ctx->frame_queues[1], audio_frame, &ctx->output_current_audio_frame_id, &reset);

				if (reset) {
					ctx->output_time_offset = AV_NOPTS_VALUE;
				}

				if (ctx->output_exit) {
					goto unlock;
				}
				if (ret >= 0)
				{
					if (audio_frame->pts != AV_NOPTS_VALUE && (ctx->output_status_time == AV_NOPTS_VALUE || audio_frame->pts >= ctx->output_status_time)) {
						ctx->output_status_time = audio_frame->pts;
					}
					last_audio_sync_diff = get_sync_diff(ctx, audio_frame->pts);
					fill_audio_fifo(ctx, audio_frame);
					get_audio_fifo_pts(ctx, &fifo_pts, &fifo_end_pts, shift_samples);
					update_offset_if_needed(ctx, fifo_pts, 0);
				}
				else {
					if (last_audio_sync_diff != AV_NOPTS_VALUE && last_audio_sync_diff < 0 && ctx->output_current_audio_frame_id >= ctx->output_current_video_frame_id) {
						ctx->output_time_offset = AV_NOPTS_VALUE;
						last_audio_sync_diff = AV_NOPTS_VALUE;
					}
					goto audio_output;
				}
			}
		sync_end:
			if (in_sync_result == 1) {
				audio_in_sync = 1;
			}
			else {
				get_audio_fifo_pts(ctx, &fifo_pts, &fifo_end_pts, 0);
				if (update_offset_if_needed(ctx, fifo_pts, 0))
					goto audio_loop;
			}

		audio_output:
			if (audio_in_sync) {
				fifo_out_audio_frame->pts = AV_NOPTS_VALUE;
				if (av_frame_ref(final_audio_frame, fifo_out_audio_frame) < 0) {
					continue;
				}
			}
			else {
				if (av_frame_ref(final_audio_frame, empty_audio_frame) < 0) {
					continue;
				}
			}
			output_audio(ctx, final_audio_frame);
			av_frame_unref(final_audio_frame);
		}

	video_loop:
		while (1)
		{
			in_sync_result = is_in_sync(ctx, video_frame->pts);
			if (in_sync_result != 0) break;
			ret = frame_dequeue(ctx->frame_queues[0], video_frame, &ctx->output_current_video_frame_id, &reset);

			if (reset) {
				ctx->output_time_offset = AV_NOPTS_VALUE;
			}

			if (ctx->output_exit) {
				goto unlock;
			}
			if (ret >= 0)
			{
				if (video_frame->pts != AV_NOPTS_VALUE) {
					ctx->output_status_time = video_frame->pts;
				}
				update_offset_if_needed(ctx, video_frame->pts, 1);
				last_video_sync_diff = get_sync_diff(ctx, video_frame->pts);
			}
			else {
				if (last_video_sync_diff != AV_NOPTS_VALUE && last_video_sync_diff < 0 && ctx->output_current_video_frame_id >= ctx->output_current_audio_frame_id) {
					ctx->output_time_offset = AV_NOPTS_VALUE;
					last_video_sync_diff = AV_NOPTS_VALUE;
				}
				goto video_output;
			}
			//printf("video deq\n");
		}

		if (in_sync_result == 1)
		{
			av_frame_unref(final_video_frame);
			av_frame_move_ref(final_video_frame, video_frame);
		}
		else {
			if (update_offset_if_needed(ctx, video_frame->pts, 1))
				goto video_loop;
		}

	video_output:
		output_video(ctx, final_video_frame);

	unlock:
		LeaveCriticalSection(&ctx->input_change_mutex);
	}

end:
	av_frame_free(&video_frame);
	av_frame_free(&audio_frame);
	av_frame_free(&final_video_frame);
	av_frame_free(&empty_audio_frame);
	av_frame_free(&fifo_out_audio_frame);
	av_frame_free(&final_audio_frame);
	ctx->output_exit = 1;

	return ret;
}

int start_output(inout_context* ctx)
{
	HANDLE out_thread;

	if (open_thread(&out_thread, output_thread, ctx) < 0)
	{
		ctx->output_exit = 1;
		return -1;
	}

	ctx->output_thread = out_thread;
	return 0;
}

void stop_output(inout_context* ctx)
{
	ctx->output_exit = 1;
	free_thread(&ctx->output_thread);
}

int get_loglevel_from_str(char* log_level_str)
{
	if (!log_level_str) goto not_found;
	const struct { const char* name; int level; } log_levels[] = {
		{ "quiet"  , AV_LOG_QUIET   },
		{ "panic"  , AV_LOG_PANIC   },
		{ "fatal"  , AV_LOG_FATAL   },
		{ "error"  , AV_LOG_ERROR   },
		{ "warning", AV_LOG_WARNING },
		{ "info"   , AV_LOG_INFO    },
		{ "verbose", AV_LOG_VERBOSE },
		{ "debug"  , AV_LOG_DEBUG   },
		{ "trace"  , AV_LOG_TRACE   },
	};

	for (int i = 0; i < ARRAY_ELEMS(log_levels); i++)
	{
		if (strstr(log_levels[i].name, log_level_str) != NULL) return log_levels[i].level;
	}

not_found:
	return av_log_get_level();
}

int is_output_exit(inout_context *ctx)
{
	return ctx->output_exit;
}

int is_input_exit(inout_context* ctx)
{
	if (ctx->special_source_running) return 0;
	int decoder_all_exit = 1;
	for (int i = 0; i < 2; i++) {
		if (ctx->decoders[i].avctx && !ctx->decoders[i].exit)
			decoder_all_exit = 0;
	}
	return ctx->force_exit || decoder_all_exit;
}



void set_input_filter(inout_context* ctx, char* filter_text)//filter_text设置为NULL即取消滤镜
{
	if (!filter_text)
	{
		return;
	}
	EnterCriticalSection(&ctx->filter_text_mutex);
	if (ctx->filter_text) {
		av_free(ctx->filter_text);
		ctx->filter_text = NULL;
	}
	if (strlen(filter_text) > 0)
		ctx->filter_text = av_strdup(filter_text);
	LeaveCriticalSection(&ctx->filter_text_mutex);
	ctx->needs_reinit_filter = 1;
	return;
}

void set_input_audio_filter(inout_context* ctx, char* filter_text)//filter_text设置为NULL即取消滤镜
{
	if (!filter_text)
	{
		return;
	}
	EnterCriticalSection(&ctx->filter_text_mutex);
	if (ctx->audio_filter_text) {
		av_free(ctx->audio_filter_text);
		ctx->audio_filter_text = NULL;
	}
	if (strlen(filter_text) > 0)
		ctx->audio_filter_text = av_strdup(filter_text);
	LeaveCriticalSection(&ctx->filter_text_mutex);
	ctx->needs_reinit_audio_filter = 1;
	return;
}

int set_input_stream_index(inout_context* ctx, int index, enum AVMediaType type)
{
	if (!ctx || !ctx->fmt_ctx) return -1;
	AVDictionary* opts = NULL;
	AVFormatContext* fmt_ctx = ctx->fmt_ctx;
	const AVCodec* pCodec = NULL;
	AVCodecContext* avctx = NULL;
	int hw_decode_success = 0;
	int ret = -1;
	int i = -1;
	int j = -1;
	int type_i = -1;

	switch (type)
	{
	case AVMEDIA_TYPE_VIDEO:
		type_i = 0;
		break;
	case AVMEDIA_TYPE_AUDIO:
		type_i = 1;
		break;
	case AVMEDIA_TYPE_SUBTITLE:
		type_i = 2;
		break;
	default:
		goto failed;
	}

	for (i = 0; i < fmt_ctx->nb_streams; i++)
	{
		if (fmt_ctx->streams[i]->codecpar->codec_type == type)
			j++;

		if (j == index) break;
	}

	if (j != index || j < 0 || i > fmt_ctx->nb_streams - 1) goto failed;

	if (ctx->decoders[type_i].index == i) goto failed;

	pCodec = avcodec_find_decoder(fmt_ctx->streams[i]->codecpar->codec_id);
	avctx = avcodec_alloc_context3(pCodec);
	if (!avctx) goto failed;

	ret = avcodec_parameters_to_context(avctx, fmt_ctx->streams[i]->codecpar);
	if (ret < 0) goto failed;

	if (type_i == 0 && ctx->hw_type != AV_HWDEVICE_TYPE_NONE) {
		enum AVHWDeviceType hw_type = ctx->hw_type;
		if (check_hw_decode(pCodec, hw_type) >= 0)
		{
			if (ctx->hw_device_ctx) {
				av_buffer_unref(&ctx->hw_device_ctx);
			}
			if (hw_decoder_init(&ctx->hw_device_ctx,avctx, hw_type) >= 0)
			{
				hw_decode_success = 1;
			}
		}
	}

	av_dict_set(&opts, "threads", "auto", 0);
	ret = avcodec_open2(avctx, pCodec, &opts);
	av_dict_free(&opts);

	if (ret < 0)  goto failed;

	EnterCriticalSection(&ctx->decoders[type_i].mutex);

	avcodec_free_context(&ctx->decoders[type_i].avctx);
	if (type_i == 0) {
		if (hw_decode_success) {
			ctx->hw_pix_fmt = get_hw_pix_fmt(pCodec, ctx->hw_type);
			printf("init hardware decoder success!\n");
		}
		else {
			ctx->hw_pix_fmt = AV_PIX_FMT_NONE;
		}
	}
	ctx->decoders[type_i].avctx = avctx;
	ctx->decoders[type_i].index = i;

	LeaveCriticalSection(&ctx->decoders[type_i].mutex);
	return 0;
failed:
	avcodec_free_context(&avctx);
	return -1;
}

int set_stream_index_from_file(inout_context* ctx, char* filename)
{
	if (!ctx || !filename) return -1;
	char buf[128] = { 0 };
	char* p = NULL;

	FILE* fp = fopen(filename, "r");
	if (!fp) return -1;

	char* key[3] = { "video_index","audio_index","subtitle_index" };
	int index[3] = { -1 ,-1,-1 };
	enum AVMediaType type[3] = { AVMEDIA_TYPE_VIDEO, AVMEDIA_TYPE_AUDIO, AVMEDIA_TYPE_SUBTITLE };


	int i = 0;
	while (fgets(buf, sizeof(buf), fp))
	{
		for (int i = 0; i < 3; i++)
		{
			if (strstr(buf, key[i]))
			{
				p = strstr(buf, "=");
				if (!p) break;

				trim_string(p, '\n');
				//puts(buf);
				index[i] = atoi(p + 1);
				break;
			}
		}
	}

	for (int i = 0; i < sizeof(index) / sizeof(int); i++)
	{
		if (index[i] >= 0);
		set_input_stream_index(ctx, index[i], type[i]);
	}
	fclose(fp);
	return 0;
}

char* join_path_free_filename(char *path,char *filename) {
	char* new_str = join_path(path, filename);
	if(filename)
		av_free(filename);
	return new_str;
}

char* av_dict_pop_value(AVDictionary** dict, char* key) {
	AVDictionaryEntry* entry = av_dict_get(*dict,key, NULL, 0);
	char* dup = NULL;
	if (entry) {
		dup = av_strdup(entry->value);
		av_dict_set(dict, key, NULL, 0);
	}
	return dup;
}
typedef enum tcp_control_command_type {
	TCP_CONTROL_NONE = 0,
	TCP_CONTROL_PLAY,
	TCP_CONTROL_SET_FILTER,
	TCP_CONTROL_SET_AUDIO_FILTER,
	TCP_CONTROL_SET_INDEX,
	TCP_CONTROL_SET_SHIFT,
	TCP_CONTROL_SEEK,
	TCP_CONTROL_SEEK_BYTE_PERCENT,
	TCP_CONTROL_SET_HW_DECODE,
	TCP_CONTROL_SET_SCALE_MODE,
	TCP_CONTROL_SET_DISPLAY_ASPECT,
	TCP_CONTROL_STOP,
	TCP_CONTROL_REOPEN
} tcp_control_command_type;

typedef struct tcp_control_command {
	tcp_control_command_type type;
	char* text;
	int video_index;
	int audio_index;
	int64_t number;
	AVRational aspect;
} tcp_control_command;

static int64_t probe_input_duration_seconds(char* text)
{
	char* input = NULL;
	char* tab_pos = NULL;
	char* format = NULL;
	char* probesize = NULL;
	char* analyze_duration = NULL;
	AVDictionary* dict = NULL;
	AVDictionary* temp_dict = NULL;
	AVFormatContext* fmt_ctx = NULL;
	const AVInputFormat* in_fmt = NULL;
	int64_t duration = 0;

	if (!text || text[0] == '\0') return -1;
	input = av_strdup(text);
	if (!input) return -1;

	tab_pos = strchr(input, '\t');
	if (tab_pos) {
		*tab_pos = '\0';
		av_dict_parse_string(&dict, tab_pos + 1, "=", ",", 0);
	}

	if (strcmp(input, "<TESTCARD>") == 0 || strcmp(input, "<TESTCARD2>") == 0 || strcmp(input, "<OBSVCAM>") == 0) {
		duration = 0;
		goto end;
	}

	if (dict && av_dict_copy(&temp_dict, dict, 0) >= 0) {
		format = av_dict_pop_value(&temp_dict, "format");
		probesize = av_dict_pop_value(&temp_dict, "probesize");
		analyze_duration = av_dict_pop_value(&temp_dict, "analyzeduration");
	}

	fmt_ctx = avformat_alloc_context();
	if (!fmt_ctx) {
		duration = -1;
		goto end;
	}
	if (probesize && atoi(probesize) > 0) fmt_ctx->probesize = atoi(probesize);
	if (analyze_duration && atoi(analyze_duration) > 0) fmt_ctx->max_analyze_duration = atoi(analyze_duration);
	if (format) in_fmt = av_find_input_format(format);

	if (avformat_open_input(&fmt_ctx, input, in_fmt, &temp_dict) < 0) {
		duration = -1;
		goto end;
	}
	if (avformat_find_stream_info(fmt_ctx, NULL) < 0) {
		duration = -1;
		goto end;
	}
	if (fmt_ctx->duration != AV_NOPTS_VALUE && fmt_ctx->duration > 0) {
		duration = (fmt_ctx->duration + AV_TIME_BASE / 2) / AV_TIME_BASE;
	}

end:
	avformat_close_input(&fmt_ctx);
	av_dict_free(&dict);
	av_dict_free(&temp_dict);
	if (format) av_free(format);
	if (probesize) av_free(probesize);
	if (analyze_duration) av_free(analyze_duration);
	av_free(input);
	return duration;
}

#define TCP_CONTROL_QUEUE_SIZE 32
#define TCP_CONTROL_BUFFER_SIZE 8192

typedef struct tcp_control_server {
	CRITICAL_SECTION mutex;
	HANDLE thread;
	SOCKET listen_socket;
	SOCKET client_socket;
	int port;
	int exit;
	int started;
	int64_t status_time;
	int64_t status_duration;
	int64_t status_size;
	char status_input[1024];
	char status_state[32];
	int scale_mode;
	AVRational display_aspect;
	tcp_control_command queue[TCP_CONTROL_QUEUE_SIZE];
	int queue_head;
	int queue_count;
} tcp_control_server;

static void tcp_control_command_free(tcp_control_command* cmd)
{
	if (!cmd) return;
	av_freep(&cmd->text);
	memset(cmd, 0, sizeof(*cmd));
}

static void tcp_control_push(tcp_control_server* server, tcp_control_command* cmd)
{
	EnterCriticalSection(&server->mutex);
	if (server->queue_count >= TCP_CONTROL_QUEUE_SIZE) {
		tcp_control_command_free(&server->queue[server->queue_head]);
		server->queue_head = (server->queue_head + 1) % TCP_CONTROL_QUEUE_SIZE;
		server->queue_count--;
	}
	int tail = (server->queue_head + server->queue_count) % TCP_CONTROL_QUEUE_SIZE;
	server->queue[tail] = *cmd;
	server->queue_count++;
	memset(cmd, 0, sizeof(*cmd));
	LeaveCriticalSection(&server->mutex);
}

static int tcp_control_pop(tcp_control_server* server, tcp_control_command* cmd)
{
	int got = 0;
	EnterCriticalSection(&server->mutex);
	if (server->queue_count > 0) {
		*cmd = server->queue[server->queue_head];
		memset(&server->queue[server->queue_head], 0, sizeof(server->queue[server->queue_head]));
		server->queue_head = (server->queue_head + 1) % TCP_CONTROL_QUEUE_SIZE;
		server->queue_count--;
		got = 1;
	}
	LeaveCriticalSection(&server->mutex);
	return got;
}

static char* tcp_trim_left(char* s)
{
	while (s && (*s == ' ' || *s == '\t')) s++;
	return s;
}

static void tcp_trim_right(char* s)
{
	int len;
	if (!s) return;
	len = (int)strlen(s);
	while (len > 0 && (s[len - 1] == '\r' || s[len - 1] == '\n' || s[len - 1] == ' ' || s[len - 1] == '\t')) {
		s[--len] = '\0';
	}
}

static int starts_with_command(const char* line, const char* command)
{
	size_t len = strlen(command);
	return _strnicmp(line, command, len) == 0 && (line[len] == '\0' || line[len] == ' ' || line[len] == '\t');
}

static void tcp_control_parse_line(tcp_control_server* server, char* line, tcp_control_command* cmd, char* reply, int reply_size)
{
	char* arg;
	char aspect_text[32];
	memset(cmd, 0, sizeof(*cmd));
	strcpy_s(reply, reply_size, "OK\n");
	tcp_trim_right(line);
	line = tcp_trim_left(line);

	if (line[0] == '\0' || starts_with_command(line, "PING")) {
		strcpy_s(reply, reply_size, "OK PONG\n");
		return;
	}
	if (starts_with_command(line, "PLAY")) {
		arg = tcp_trim_left(line + 4);
		if (!arg || arg[0] == '\0') {
			strcpy_s(reply, reply_size, "ERR PLAY requires an input\n");
			return;
		}
		cmd->type = TCP_CONTROL_PLAY;
		cmd->text = av_strdup(arg);
		return;
	}
	if (starts_with_command(line, "SET_FILTER")) {
		cmd->type = TCP_CONTROL_SET_FILTER;
		cmd->text = av_strdup(tcp_trim_left(line + 10));
		return;
	}
	if (starts_with_command(line, "SET_AUDIO_FILTER")) {
		cmd->type = TCP_CONTROL_SET_AUDIO_FILTER;
		cmd->text = av_strdup(tcp_trim_left(line + 16));
		return;
	}
	if (starts_with_command(line, "SET_SHIFT")) {
		cmd->type = TCP_CONTROL_SET_SHIFT;
		cmd->number = _atoi64(tcp_trim_left(line + 9));
		return;
	}
	if (starts_with_command(line, "STATUS")) {
		EnterCriticalSection(&server->mutex);
		format_display_aspect(server->display_aspect, aspect_text, sizeof(aspect_text));
		sprintf_s(reply, reply_size, "OK seconds=%I64d duration=%I64d size=%I64d state=%s scale_mode=%s display_aspect=%s input=%s\n", server->status_time / 1000000LL, server->status_duration / 1000000LL, server->status_size, server->status_state, output_scale_mode_name(server->scale_mode), aspect_text, server->status_input);
		LeaveCriticalSection(&server->mutex);
		return;
	}
	if (starts_with_command(line, "DURATION")) {
		int64_t duration = probe_input_duration_seconds(tcp_trim_left(line + 8));
		if (duration < 0) sprintf_s(reply, reply_size, "ERR duration unavailable\n");
		else sprintf_s(reply, reply_size, "OK duration=%I64d\n", duration);
		return;
	}
	if (starts_with_command(line, "SEEK_BYTE_PERCENT")) {
		cmd->type = TCP_CONTROL_SEEK_BYTE_PERCENT;
		cmd->number = _atoi64(tcp_trim_left(line + 17));
		return;
	}
	if (starts_with_command(line, "SEEK")) {
		cmd->type = TCP_CONTROL_SEEK;
		cmd->number = _atoi64(tcp_trim_left(line + 4));
		return;
	}
	if (starts_with_command(line, "SET_HW_DECODE")) {
		arg = tcp_trim_left(line + 13);
		cmd->type = TCP_CONTROL_SET_HW_DECODE;
		if (!arg || _stricmp(arg, "none") == 0 || _stricmp(arg, "off") == 0) arg = "";
		cmd->text = av_strdup(arg);
		return;
	}
	if (starts_with_command(line, "SET_SCALE_MODE")) {
		arg = tcp_trim_left(line + 14);
		cmd->type = TCP_CONTROL_SET_SCALE_MODE;
		cmd->number = parse_output_scale_mode(arg, -1);
		if (cmd->number < 0) {
			cmd->type = TCP_CONTROL_NONE;
			strcpy_s(reply, reply_size, "ERR SET_SCALE_MODE requires fill or letterbox\n");
		}
		return;
	}
	if (starts_with_command(line, "SET_DISPLAY_ASPECT")) {
		arg = tcp_trim_left(line + 18);
		cmd->type = TCP_CONTROL_SET_DISPLAY_ASPECT;
		cmd->aspect = parse_display_aspect(arg, (AVRational) { -1, -1 });
		if (cmd->aspect.num < 0 || cmd->aspect.den < 0) {
			cmd->type = TCP_CONTROL_NONE;
			strcpy_s(reply, reply_size, "ERR SET_DISPLAY_ASPECT requires auto or num:den\n");
		}
		return;
	}
	if (starts_with_command(line, "SET_INDEX")) {
		cmd->type = TCP_CONTROL_SET_INDEX;
		cmd->video_index = -1;
		cmd->audio_index = -1;
		arg = strstr(line, "video=");
		if (arg) cmd->video_index = atoi(arg + 6);
		arg = strstr(line, "audio=");
		if (arg) cmd->audio_index = atoi(arg + 6);
		return;
	}
	if (starts_with_command(line, "STOP")) {
		cmd->type = TCP_CONTROL_STOP;
		return;
	}
	if (starts_with_command(line, "REOPEN")) {
		cmd->type = TCP_CONTROL_REOPEN;
		return;
	}

	strcpy_s(reply, reply_size, "ERR unknown command\n");
}

static DWORD tcp_control_thread(LPVOID p)
{
	tcp_control_server* server = (tcp_control_server*)p;
	SOCKET listen_socket = INVALID_SOCKET;
	struct sockaddr_in addr;
	int yes = 1;

	listen_socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (listen_socket == INVALID_SOCKET) return 1;
	server->listen_socket = listen_socket;
	setsockopt(listen_socket, SOL_SOCKET, SO_REUSEADDR, (const char*)&yes, sizeof(yes));

	memset(&addr, 0, sizeof(addr));
	addr.sin_family = AF_INET;
	addr.sin_addr.s_addr = htonl(INADDR_ANY);
	addr.sin_port = htons((u_short)server->port);
	if (bind(listen_socket, (struct sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) goto end;
	if (listen(listen_socket, 4) == SOCKET_ERROR) goto end;
	printf("OmniVCam TCP control listening on 0.0.0.0:%d\n", server->port);

	while (!server->exit) {
		fd_set readfds;
		struct timeval tv;
		SOCKET client;
		FD_ZERO(&readfds);
		FD_SET(listen_socket, &readfds);
		tv.tv_sec = 0;
		tv.tv_usec = 200000;
		if (select(0, &readfds, NULL, NULL, &tv) <= 0) continue;
		client = accept(listen_socket, NULL, NULL);
		if (client == INVALID_SOCKET) continue;
		server->client_socket = client;

		char* buffer = (char*)av_malloc(TCP_CONTROL_BUFFER_SIZE);
		char* reply = (char*)av_malloc(TCP_CONTROL_BUFFER_SIZE);
		if (!buffer || !reply) {
			av_free(buffer);
			av_free(reply);
			closesocket(client);
			server->client_socket = INVALID_SOCKET;
			continue;
		}

		while (!server->exit) {
			tcp_control_command cmd;
			fd_set clientfds;
			struct timeval client_tv;
			int n;
			FD_ZERO(&clientfds);
			FD_SET(client, &clientfds);
			client_tv.tv_sec = 0;
			client_tv.tv_usec = 200000;
			if (select(0, &clientfds, NULL, NULL, &client_tv) <= 0) continue;
			n = recv(client, buffer, TCP_CONTROL_BUFFER_SIZE - 1, 0);
			if (n <= 0) break;
			buffer[n] = '\0';
			tcp_control_parse_line(server, buffer, &cmd, reply, TCP_CONTROL_BUFFER_SIZE);
			if (cmd.type != TCP_CONTROL_NONE) tcp_control_push(server, &cmd);
			send(client, reply, (int)strlen(reply), 0);
		}
		av_free(buffer);
		av_free(reply);
		closesocket(client);
		server->client_socket = INVALID_SOCKET;
	}
end:
	if (listen_socket != INVALID_SOCKET) closesocket(listen_socket);
	server->listen_socket = INVALID_SOCKET;
	return 0;
}

static int tcp_control_start(tcp_control_server* server, int port)
{
	WSADATA wsa;
	memset(server, 0, sizeof(*server));
	server->listen_socket = INVALID_SOCKET;
	server->port = port > 0 ? port : 16999;
	InitializeCriticalSection(&server->mutex);
	server->started = 1;
	if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return -1;
	return open_thread(&server->thread, tcp_control_thread, server);
}

static void tcp_control_stop(tcp_control_server* server)
{
	if (!server || !server->started) return;
	server->exit = 1;
	if (server->client_socket != INVALID_SOCKET) shutdown(server->client_socket, SD_BOTH);
	if (server->listen_socket != INVALID_SOCKET) shutdown(server->listen_socket, SD_BOTH);
	if (server->thread) {
		DWORD wait_result = WaitForSingleObject(server->thread, 2000);
		if (wait_result == WAIT_TIMEOUT) {
			printf("OmniVCam TCP control thread stop timed out\n");
			if (server->client_socket != INVALID_SOCKET) {
				closesocket(server->client_socket);
				server->client_socket = INVALID_SOCKET;
			}
			if (server->listen_socket != INVALID_SOCKET) {
				closesocket(server->listen_socket);
				server->listen_socket = INVALID_SOCKET;
			}
			WaitForSingleObject(server->thread, 200);
		}
		CloseHandle(server->thread);
		server->thread = NULL;
	}
	for (int i = 0; i < TCP_CONTROL_QUEUE_SIZE; i++) tcp_control_command_free(&server->queue[i]);
	DeleteCriticalSection(&server->mutex);
	WSACleanup();
}

DWORD main_thread(LPVOID p) {
	HRESULT hr = CoInitialize(NULL);

	paremeter_table_context* table = paremeter_table_alloc(48);
	inout_options* opts = (inout_options*)p;
	int use_fixed_frame_interval = 0;
	int output_ajust_start_if_delay_over = 0;
	int output_scale_mode = OUTPUT_SCALE_MODE_LETTERBOX;
	AVRational output_display_aspect = { 0, 0 };
	int av_max_offset_time = 3 * 1000000;
	int video_frame_buffer = 10, audio_frame_buffer = 50, packet_queue_size = 50 * 1024 * 1024, timeout = 30 * 1000000;
	AVRational frame_rate = opts->video_out_fps;
	const char* config_path = opts->config_path;
	int tcp_port = opts->tcp_port;
	char* pathvar = NULL;
	char* config_file_name = NULL;
	char* buf = NULL;
	char time_str[20] = { 0 };
	char str2[1024] = { 0 };
	char hw_decode[32] = "";
	char* current_input = NULL;
	char* global_video_filter = av_strdup("");
	char* global_audio_filter = av_strdup("");
	tcp_control_server control_server = { 0 };
	inout_context* ctx = NULL;

	if (!table) return -1;
	if (config_path) {
		pathvar = getenv(config_path);
		if (pathvar) printf("%s=%s\n", config_path, pathvar);
	}

	config_file_name = join_path(pathvar, "config.ini");
	update_config_from_file(config_file_name, table);
	paremeter_table_print(table);

	if (buf = get_paremeter_table_content(table, "config", "hw_decode", FALSE)) strcpy_s(hw_decode, sizeof(hw_decode), buf);
	if (buf = get_paremeter_table_content(table, "config", "tcp_port", FALSE)) tcp_port = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "log_level", FALSE)) av_log_set_level(get_loglevel_from_str(buf));
	if (buf = get_paremeter_table_content(table, "config", "video_frame_buffer", FALSE)) video_frame_buffer = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "audio_frame_buffer", FALSE)) audio_frame_buffer = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "packet_queue_size", FALSE)) packet_queue_size = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "timeout", FALSE)) timeout = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "use_fixed_frame_interval", FALSE)) use_fixed_frame_interval = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "ajust_start_time_if_delay_over", FALSE)) output_ajust_start_if_delay_over = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "av_max_offset_time", FALSE)) av_max_offset_time = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "scale_mode", FALSE)) output_scale_mode = parse_output_scale_mode(buf, output_scale_mode);
	if (buf = get_paremeter_table_content(table, "config", "display_aspect", FALSE)) output_display_aspect = parse_display_aspect(buf, output_display_aspect);

	frame_rate.den = frame_rate.den <= 0 ? 1 : frame_rate.den;
	video_frame_buffer = video_frame_buffer <= 0 ? (frame_rate.num / frame_rate.den) : video_frame_buffer;

	AVChannelLayout ch_layout = { 0 };
	av_channel_layout_default(&ch_layout, opts->audio_out_channels);
	ctx = inout_context_alloc(opts->video_out_width, opts->video_out_height, opts->video_out_format, opts->video_out_fps, opts->audio_out_sample_rate, opts->audio_out_format, &ch_layout, opts->audio_out_nb_samples, timeout, packet_queue_size, 5000, video_frame_buffer, audio_frame_buffer, use_fixed_frame_interval, output_ajust_start_if_delay_over, av_max_offset_time);
	av_channel_layout_uninit(&ch_layout);
	if (!ctx) goto end;

	ctx->video_callback = opts->video_callback;
	ctx->audio_callback = opts->audio_callback;
	ctx->callback_private = opts->callback_private;
	ctx->output_scale_mode = output_scale_mode;
	ctx->output_display_aspect = output_display_aspect;

	if (tcp_control_start(&control_server, tcp_port) < 0) {
		printf("OmniVCam TCP control start failed on port %d\n", tcp_port);
		goto end;
	}

	start_output(ctx);
	while (1) {
		tcp_control_command command;
		int should_open = 0;
		char* play_filename = NULL;
		AVDictionary* play_dict = NULL;
		AVDictionary* temp_dict = NULL;
		char* format = NULL;
		char* video_filter = NULL;
		char* audio_filter = NULL;
		char* video_index = NULL;
		char* audio_index = NULL;
		char* seek_time = NULL;
		char* analyze_duration = NULL;
		char* probesize = NULL;
		char* queue_left = NULL;
		char* queue_right = NULL;
		char* queue_center = NULL;
		char* vcodec_str = NULL;
		char* acodec_str = NULL;
		char* scodec_str = NULL;
		char* dcodec_str = NULL;
		int video_index_int = -1;
		int audio_index_int = -1;
		int analyze_duration_int = 0;
		int probesize_int = 0;
		int queue_left_int = -1;
		int queue_right_int = -1;
		int queue_center_int = -1;

		if (opts->send_exit) {
			ctx->output_exit = opts->send_exit;
			ctx->force_exit = opts->send_exit;
			break;
		}
		if (is_output_exit(ctx)) break;

		EnterCriticalSection(&control_server.mutex);
		control_server.status_time = ctx->output_status_time == AV_NOPTS_VALUE ? 0 : ctx->output_status_time;
		control_server.status_duration = (ctx->fmt_ctx && ctx->fmt_ctx->duration != AV_NOPTS_VALUE) ? ctx->fmt_ctx->duration : 0;
		control_server.status_size = (ctx->fmt_ctx && ctx->fmt_ctx->pb) ? avio_size(ctx->fmt_ctx->pb) : 0;
		control_server.scale_mode = ctx->output_scale_mode;
		control_server.display_aspect = ctx->output_display_aspect;
		strcpy_s(control_server.status_input, sizeof(control_server.status_input), current_input ? current_input : "");
		if (control_server.status_state[0] == '\0') strcpy_s(control_server.status_state, sizeof(control_server.status_state), "stopped");
		else if (strcmp(control_server.status_state, "playing") == 0 && is_input_exit(ctx) && inout_ctx_frame_queues_empty(ctx)) strcpy_s(control_server.status_state, sizeof(control_server.status_state), "ended");
		LeaveCriticalSection(&control_server.mutex);

		memset(&command, 0, sizeof(command));
		if (!tcp_control_pop(&control_server, &command)) {
			av_usleep(100000);
			continue;
		}

		switch (command.type) {
		case TCP_CONTROL_PLAY:
			av_freep(&current_input);
			current_input = av_strdup(command.text);
			EnterCriticalSection(&control_server.mutex);
			strcpy_s(control_server.status_state, sizeof(control_server.status_state), "opening");
			strcpy_s(control_server.status_input, sizeof(control_server.status_input), current_input ? current_input : "");
			LeaveCriticalSection(&control_server.mutex);
			play_filename = command.text ? av_strdup(command.text) : NULL;
			should_open = play_filename != NULL;
			break;
		case TCP_CONTROL_SET_FILTER:
			av_freep(&global_video_filter);
			global_video_filter = command.text ? av_strdup(command.text) : av_strdup("");
			set_input_filter(ctx, global_video_filter);
			printf("filter:\"%s\"\n", global_video_filter ? global_video_filter : "");
			break;
		case TCP_CONTROL_SET_AUDIO_FILTER:
			av_freep(&global_audio_filter);
			global_audio_filter = command.text ? av_strdup(command.text) : av_strdup("");
			set_input_audio_filter(ctx, global_audio_filter);
			printf("audio filter:\"%s\"\n", global_audio_filter ? global_audio_filter : "");
			break;
		case TCP_CONTROL_SET_SCALE_MODE:
			ctx->output_scale_mode = (int)command.number;
			printf("output scale mode: %s\n", output_scale_mode_name(ctx->output_scale_mode));
			break;
		case TCP_CONTROL_SET_DISPLAY_ASPECT:
			ctx->output_display_aspect = command.aspect;
			{
				char aspect_text[32];
				format_display_aspect(ctx->output_display_aspect, aspect_text, sizeof(aspect_text));
				printf("output display aspect: %s\n", aspect_text);
			}
			break;		case TCP_CONTROL_SET_INDEX:
			if (command.video_index >= 0 && set_input_stream_index(ctx, command.video_index, AVMEDIA_TYPE_VIDEO) >= 0) printf("video_index=%d\n", command.video_index);
			if (command.audio_index >= 0 && set_input_stream_index(ctx, command.audio_index, AVMEDIA_TYPE_AUDIO) >= 0) printf("audio_index=%d\n", command.audio_index);
			break;
		case TCP_CONTROL_SET_SHIFT:
			ctx->output_start_shift_time = command.number * 1000LL;
			printf("output start time shift: %I64d\n", command.number);
			break;
		case TCP_CONTROL_SEEK:
			if (ctx->fmt_ctx) {
				int64_t start_time = ctx->fmt_ctx->start_time == AV_NOPTS_VALUE ? 0 : ctx->fmt_ctx->start_time;
				int64_t seek_time_int64 = av_rescale_q(command.number, (AVRational) { 1, 1 }, (AVRational) { 1, AV_TIME_BASE }) + start_time;
				stop_read(ctx, 1);
				reset_after_seek(ctx);
				if (avformat_seek_file(ctx->fmt_ctx, -1, INT64_MIN, seek_time_int64, seek_time_int64, 0) >= 0) {
					start_read(ctx);
					printf("seek seconds: %I64d\n", command.number);
				}
				else {
					start_read(ctx);
					printf("seek seconds: %I64d failed\n", command.number);
				}
			}
			break;
		case TCP_CONTROL_SEEK_BYTE_PERCENT:
			if (ctx->fmt_ctx && ctx->fmt_ctx->pb) {
				int64_t size = avio_size(ctx->fmt_ctx->pb);
				if (size > 0) {
					int64_t percent = command.number;
					if (percent < 0) percent = 0;
					if (percent > 10000) percent = 10000;
					int64_t target = size * percent / 10000;
					stop_read(ctx, 1);
					reset_after_seek(ctx);
					if (avformat_seek_file(ctx->fmt_ctx, -1, INT64_MIN, target, target, AVSEEK_FLAG_BYTE) >= 0) {
						start_read(ctx);
						printf("seek byte percent: %I64d\n", percent);
					}
					else {
						start_read(ctx);
						printf("seek byte percent: %I64d failed\n", percent);
					}
				}
			}
			break;
		case TCP_CONTROL_SET_HW_DECODE:
			strcpy_s(hw_decode, sizeof(hw_decode), command.text ? command.text : "");
			printf("hw_decode:\"%s\"\n", hw_decode);
			if (current_input) {
				play_filename = av_strdup(current_input);
				should_open = 1;
			}
			break;
		case TCP_CONTROL_STOP:
			stop_read(ctx, 1);
			inout_context_reset_input(ctx);
			av_freep(&current_input);
			EnterCriticalSection(&control_server.mutex);
			strcpy_s(control_server.status_state, sizeof(control_server.status_state), "stopped");
			control_server.status_input[0] = '\0';
			LeaveCriticalSection(&control_server.mutex);
			break;
		case TCP_CONTROL_REOPEN:
			if (current_input) {
				play_filename = av_strdup(current_input);
				should_open = 1;
			}
			break;
		default:
			break;
		}

		if (should_open && play_filename) {
			char* tab_pos = strchr(play_filename, '\t');
			if (tab_pos) {
				*tab_pos = '\0';
				av_dict_parse_string(&play_dict, tab_pos + 1, "=", ",", 0);
			}

			stop_read(ctx, 1);
			inout_context_reset_input(ctx);

			if (play_dict && av_dict_copy(&temp_dict, play_dict, 0) >= 0) {
				format = av_dict_pop_value(&temp_dict, "format");
				video_filter = av_dict_pop_value(&temp_dict, "video_filter");
				audio_filter = av_dict_pop_value(&temp_dict, "audio_filter");
				video_index = av_dict_pop_value(&temp_dict, "video_index");
				audio_index = av_dict_pop_value(&temp_dict, "audio_index");
				seek_time = av_dict_pop_value(&temp_dict, "seek_time");
				probesize = av_dict_pop_value(&temp_dict, "probesize");
				analyze_duration = av_dict_pop_value(&temp_dict, "analyzeduration");
				queue_left = av_dict_pop_value(&temp_dict, "queue_left");
				queue_right = av_dict_pop_value(&temp_dict, "queue_right");
				queue_center = av_dict_pop_value(&temp_dict, "queue_center");
				vcodec_str = av_dict_pop_value(&temp_dict, "vcodec");
				acodec_str = av_dict_pop_value(&temp_dict, "acodec");
				scodec_str = av_dict_pop_value(&temp_dict, "scodec");
				dcodec_str = av_dict_pop_value(&temp_dict, "dcodec");
			}

			if (video_index) video_index_int = atoi(video_index);
			if (audio_index) audio_index_int = atoi(audio_index);
			if (probesize) probesize_int = atoi(probesize);
			if (analyze_duration) analyze_duration_int = atoi(analyze_duration);
			if (queue_left) queue_left_int = atoi(queue_left);
			if (queue_right) queue_right_int = atoi(queue_right);
			if (queue_center) queue_center_int = atoi(queue_center);

			if (open_input(format, play_filename, &temp_dict, ctx, video_index_int, audio_index_int, -1, hw_decode, probesize_int, analyze_duration_int, queue_left_int, queue_right_int, queue_center_int, vcodec_str, acodec_str, scodec_str, dcodec_str) >= 0) {
				set_input_filter(ctx, video_filter ? video_filter : global_video_filter);
				set_input_audio_filter(ctx, audio_filter ? audio_filter : global_audio_filter);
				if (seek_time && ctx->fmt_ctx) {
					int seek_time_int = atoi(seek_time);
					int64_t start_time = ctx->fmt_ctx->start_time == AV_NOPTS_VALUE ? 0 : ctx->fmt_ctx->start_time;
					int64_t seek_time_int64 = av_rescale_q(seek_time_int, (AVRational) { 1, 1 }, (AVRational) { 1, AV_TIME_BASE }) + start_time;
					if (avformat_seek_file(ctx->fmt_ctx, -1, INT64_MIN, seek_time_int64, seek_time_int64, 0) >= 0) {
						printf("[%s] seek: %I64d\n", play_filename, seek_time_int64);
					}
				}
				start_read(ctx);
				EnterCriticalSection(&control_server.mutex);
				strcpy_s(control_server.status_state, sizeof(control_server.status_state), "playing");
				strcpy_s(control_server.status_input, sizeof(control_server.status_input), current_input ? current_input : play_filename);
				LeaveCriticalSection(&control_server.mutex);
				sprintf_s(str2, sizeof(str2), "[%s] Playing : %s\n", get_time_string(time_str, sizeof(time_str)), play_filename);
				printf(str2);
			}
			else {
				EnterCriticalSection(&control_server.mutex);
				strcpy_s(control_server.status_state, sizeof(control_server.status_state), "error");
				strcpy_s(control_server.status_input, sizeof(control_server.status_input), current_input ? current_input : play_filename);
				LeaveCriticalSection(&control_server.mutex);
				printf("[%s] open:\"%s\" failed!\n", get_time_string(time_str, sizeof(time_str)), play_filename);
			}
		}

		av_dict_free(&play_dict);
		av_dict_free(&temp_dict);
		if (format) av_free(format);
		if (video_filter) av_free(video_filter);
		if (audio_filter) av_free(audio_filter);
		if (video_index) av_free(video_index);
		if (audio_index) av_free(audio_index);
		if (seek_time) av_free(seek_time);
		if (analyze_duration) av_free(analyze_duration);
		if (probesize) av_free(probesize);
		if (queue_left) av_free(queue_left);
		if (queue_right) av_free(queue_right);
		if (queue_center) av_free(queue_center);
		if (vcodec_str) av_free(vcodec_str);
		if (acodec_str) av_free(acodec_str);
		if (scodec_str) av_free(scodec_str);
		if (dcodec_str) av_free(dcodec_str);
		if (play_filename) av_free(play_filename);
		tcp_control_command_free(&command);
	}
end:
	if (ctx) {
		stop_read(ctx, 1);
		stop_output(ctx);
		inout_context_free(&ctx);
	}
	tcp_control_stop(&control_server);
	paremeter_table_free(&table);
	if (config_file_name) av_free(config_file_name);
	av_freep(&current_input);
	av_freep(&global_video_filter);
	av_freep(&global_audio_filter);

	CoUninitialize();
	return 0;
}

void video_cb(void *p, AVFrame* f) {
	printf("v: %lld\n", f->pts);
}

void audio_cb(void* p, AVFrame* f) {
	printf("a: %lld\n", f->pts);
}




int main15() {
	avdevice_register_all();
	inout_options *opts = av_mallocz(sizeof(inout_options));
	HANDLE _main_thread = NULL;
	while (1) {
		AVRational fps = { 30000,1001 };
		opts->video_out_width = 1920;
		opts->video_out_height = 1080;
		opts->video_out_format = AV_PIX_FMT_YUV420P;
		opts->video_out_fps = fps;
		opts->audio_out_channels = 2;
		opts->audio_out_sample_rate = 48000;
		opts->audio_out_format = AV_SAMPLE_FMT_S16P;
		opts->audio_out_nb_samples = 1024;
		opts->video_callback = video_cb;
		opts->audio_callback = audio_cb;
		opts->send_exit = 0;
		//ctx = inout_context_alloc(1280, 720, AV_PIX_FMT_YUV420P, fps, 22050, AV_SAMPLE_FMT_S16P, &ch_layout, 1024, 0.1 * 1000000, 20 * 1024 * 1024, 500, 10, 50);
		open_thread(&_main_thread, main_thread, opts);
		//av_usleep(0.01 * 1000000);
		opts->send_exit = 1;
		free_thread(&_main_thread);
		//if (open_input(NULL, "D:\\1.mp4",
		//	ctx, -1, -1, -1, "dxva2") >= 0) {
		//	//ctx->audio_filter_text = "volume=15dB";
		//	start_read(ctx);
		//	start_output(ctx);
		//	av_usleep(300 * 1000000);
		//}
	}
	if(opts) av_free(opts);
	//avdevice_register_all()
}

int main14() {
	char* pathvar = "OMNI_VCAM_CONFIG";
	//pathvar = getenv(CONFIG_ROOT_ENV);
	//if (pathvar)
	//	printf("CONFIG_ROOT_ENV=%s", pathvar);


	while (1) {

		paremeter_table_context* table = paremeter_table_alloc(48);

		char* play_list = NULL;
		char* filter_file = NULL;
		char* audio_filter_file = NULL;
		char* control_file_name = NULL;
		char* index_file_name = NULL;
		char* config_file_name = "config.ini";
		play_list_context* play_list_ctx;
		update_config_from_file(config_file_name, table);
		paremeter_table_print(table);

		play_list = get_paremeter_table_content(table, "config", "play_list",TRUE);
		control_file_name = get_paremeter_table_content(table, "config", "control_file", TRUE);
		filter_file = get_paremeter_table_content(table, "config", "filter_file", TRUE);
		audio_filter_file = get_paremeter_table_content(table, "config", "audio_filter_file", TRUE);
		index_file_name = get_paremeter_table_content(table, "config", "index_file", TRUE);
		char* join = join_path(pathvar, play_list);

		play_list_ctx = play_list_alloc();
		update_play_list_from_file(play_list_ctx, "input.txt");
		play_list_free(&play_list_ctx);
		paremeter_table_free(&table);


		if (play_list)
			av_free(play_list);
		if (control_file_name)
			av_free(control_file_name);
		if (filter_file)
			av_free(filter_file);
		if (audio_filter_file)
			av_free(audio_filter_file);
		if (index_file_name)
			av_free(index_file_name);
		if (join) av_free(join);
		//printf(join);
		//av_free(join);
	}
	return 0;
}

int main11() {
	while (1) {
		AVBufferRef* hw_device_ctx = NULL;
		av_hwdevice_ctx_create(&hw_device_ctx, AV_HWDEVICE_TYPE_D3D11VA, NULL, NULL, 0);
		av_buffer_unref(&hw_device_ctx);
	}
	while (1) {
		AVChannelLayout ch_layout = { 0 };
		av_channel_layout_default(&ch_layout, 2);
		inout_context* ctx = inout_context_alloc(640, 480, AV_PIX_FMT_YUV420P, (AVRational) { 25, 1 }, 48000, AV_SAMPLE_FMT_S16, & ch_layout, 1024, 30 * 1000000, 50 * 1000000, 5000, 10, 10,1,0,3000000);
		av_channel_layout_uninit(&ch_layout);
		open_input(NULL, "D:\\develop\\videogen\\2.mp4", NULL, ctx, -1, -1, -1, "", 0, 0, -1, -1,-1,NULL, NULL, NULL, NULL);
		inout_context_reset_input(ctx);
		inout_context_free(&ctx);
	}
}
