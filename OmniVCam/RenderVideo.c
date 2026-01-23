#include "RenderVideo.h"
#include "ParseConfig.h"
#include "Utils.h"
#include "clock.h"
AVRational UNIVERSAL_TB = { 1,1000000 };
AVRational NS_TB = { 1,1000000000 };
AVRational DSHOW_TB = { 1,10000000 };
#define COND_TIMEOUT 100

int frame_enqueue(frame_queue *q, AVFrame* frame,int64_t timeout, int64_t frame_id, int *exit) {
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

void frame_queue_set(frame_queue *q, int left_count, int right_count) {
	EnterCriticalSection(&q->mutex);
	//一定要清空了queue才能set
	if (q->front) {
		goto end;
	}
	q->reached_center = 0;
	if (left_count == -1 || right_count == -1) {
		q->center_count = -1;
		q->left_count = -1;
		q->right_count = -1;
		goto end;
	}

	if (left_count < 0) left_count = 0;

	if (right_count - left_count < 2) right_count = left_count + 2;

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


int open_thread(HANDLE* thread, LPTHREAD_START_ROUTINE start, LPVOID arg)
{
	if (!thread) return -1;

	*thread = CreateThread(NULL, 0, start, arg, 0, NULL);

	if (*thread != NULL) return 0;
	return -1;
}

void free_thread(HANDLE* thread)
{
	if (!thread || !(*thread)) return;
	WaitForSingleObject(*thread, INFINITE);
	CloseHandle(*thread);
	*thread = NULL;
}

inout_context* inout_context_alloc(
	int video_out_width, int video_out_height,int video_out_format,AVRational video_out_fps,
	int audio_out_sample_rate,int audio_out_format,const AVChannelLayout *audio_out_layout, int audio_out_nb_samples,
	int64_t timeout, int packet_queue_max_size, int packet_queue_max_count,int video_frame_queue_count, int audio_frame_queue_count,int use_fixed_frame_interval)
{
	inout_context* ctx = (inout_context*)av_mallocz(sizeof(inout_context));
	AVAudioFifo* audio_fifo = NULL;
	frame_queue* video_frame_queue;
	frame_queue* audio_frame_queue;
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

	if (!video_frame_queue || !audio_frame_queue) {
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
	ctx->output_frame_width = video_out_width;
	ctx->output_frame_height = video_out_height;
	ctx->output_frame_format = video_out_format;
	ctx->output_fps = video_out_fps;
	ctx->output_time_per_video_frame = av_rescale_q_rnd(1, (AVRational) { video_out_fps.den, video_out_fps.num }, UNIVERSAL_TB, AV_ROUND_UP);
	ctx->output_video_interval_ns = use_fixed_frame_interval ? util_mul_div64(1000000000ULL, video_out_fps.den, video_out_fps.num) : 0;
	ctx->output_audio_sample_rate = audio_out_sample_rate;
	ctx->output_audio_format = audio_out_format;
	ctx->output_audio_nb_samples = audio_out_nb_samples;
	ctx->output_time_per_audio_frame = av_rescale_q_rnd(audio_out_nb_samples, (AVRational) { 1, audio_out_sample_rate }, UNIVERSAL_TB, AV_ROUND_UP);
	ctx->output_audio_fifo = audio_fifo;
	ctx->output_time_offset = AV_NOPTS_VALUE;
	ctx->output_time_offset_last_adjust_time = AV_NOPTS_VALUE;
	av_channel_layout_copy(&ctx->output_audio_layout, audio_out_layout);
	ctx->timeout = timeout;
	ctx->last_packet_time = AV_NOPTS_VALUE;
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

	free_thread((*ctx)->reading_tid);
	free_thread((*ctx)->decode_video_tid);
	free_thread((*ctx)->decode_audio_tid);
	av_free((*ctx)->filter_text);
	av_free((*ctx)->audio_filter_text);
	av_freep(&(*ctx)->input_name);
	av_freep(&(*ctx)->current_status_file_name);
	frame_queue_free(&(*ctx)->frame_queues[0]);
	frame_queue_free(&(*ctx)->frame_queues[1]);
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
		frame_queue_set(ctx->frame_queues[i], -1, -1);
	}
	ctx->output_time_offset = AV_NOPTS_VALUE;
	ctx->output_time_offset_last_adjust_time = AV_NOPTS_VALUE;
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


	free_thread(ctx->reading_tid);
	free_thread(ctx->decode_video_tid);
	free_thread(ctx->decode_audio_tid);
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
	av_freep(&ctx->current_status_file_name);
	ctx->force_exit = 0;
	ctx->eof = 0;
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

int open_input(char *fmt_name, char *name,char *current_status_file_name, AVDictionary ** dict_opts, inout_context* ctx,
	int video_index, int audio_index, int subtitle_index, char* hw_decode,int probesize,int analyzeduration,int queue_left_count,int queue_right_count)
{	
	int ret = -1;
	AVFormatContext* fmt_ctx = NULL;
	if (!(fmt_ctx = avformat_alloc_context()))
	{
		DEBUG_LOG("avformat_alloc_context failed!\n");
		goto end;
	}
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
	if (name)
	{
		av_freep(&ctx->input_name);
		ctx->input_name = av_strdup(name);
	}
	if (current_status_file_name) {
		av_freep(&ctx->current_status_file_name);
		ctx->current_status_file_name = av_strdup(current_status_file_name);
	}
	ctx->input_frame_id = av_gettime_relative();

	if(ctx->decoders[0].index >= 0)
		frame_queue_set(ctx->frame_queues[0], queue_left_count, queue_right_count);
	else {
		frame_queue_set(ctx->frame_queues[1], queue_left_count, queue_right_count);
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
	ctx->input_start_time = av_gettime_relative();
	int64_t last_output_txt_time = 0;
	FILE* txt_fp = NULL;
	char buf[384];
	char time_str[20];
	time_t now = time(NULL);
	int ret;
	if (ctx->current_status_file_name) {
		txt_fp = fopen(ctx->current_status_file_name, "a+");
		if (txt_fp) {
			strftime(time_str, sizeof(time_str), "%Y-%m-%d %H:%M:%S", localtime(&now));
			sprintf_s(buf, sizeof(buf), "[%s] %s\n", time_str, ctx->input_name);
			fwrite(buf, 1, strlen(buf), txt_fp);
		}
	}

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

		if (txt_fp) {
			if (ctx->last_clock_time - last_output_txt_time >= 1 * 1000000) {
				last_output_txt_time = ctx->last_clock_time;
				now = time(NULL);
				strftime(time_str, sizeof(time_str), "%Y-%m-%d %H:%M:%S", localtime(&now));
				sprintf_s(buf, sizeof(buf), "[%s] %f\n", time_str, ctx->last_packet_time / 1000000.0);
				fwrite(buf, 1, strlen(buf), txt_fp);
			}
		}

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
	if(txt_fp)
		fclose(txt_fp);
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


static int get_video_buffer(AVFrame* frame)
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

static void flip_frame(AVFrame* frame)
{
	frame->data[0] += frame->linesize[0] * (frame->height - 1);
	frame->linesize[0] *= -1;
}

int fill_output_video(inout_context* ctx, AVFrame* frame)
{
	int ret = 0,filped = 0,use_point = 0;
	AVFrame *f = av_frame_alloc();
	
	//uint8_t* data[4];
	//int linesize[4];
	//if (av_image_alloc(data, linesize, ctx->output_frame_width, ctx->output_frame_height, ctx->output_frame_format, 1) < 0) {
	//	ret = -1;
	//	goto end;
	//}
	f->width = ctx->output_frame_width;
	f->height = ctx->output_frame_height;
	f->format = ctx->output_frame_format;
	if (frame->width * frame->height > 1920 * 1080) {
		use_point = 1;
	}
	if (get_video_buffer(f) < 0) {
		goto end;
	}

	if (f->format == AV_PIX_FMT_BGR24 || f->format == AV_PIX_FMT_0RGB32) {
		flip_frame(f);
		filped = 1;
	}

	if (frame->width == ctx->output_frame_width && frame->height == ctx->output_frame_height &&
		frame->format == ctx->output_frame_format)
	{
		av_frame_copy(f, frame);
		av_frame_copy_props(f, frame);

		if (filped) {
			flip_frame(f);
			filped = 0;
		}
		if (frame_enqueue(ctx->frame_queues[0], f, ctx->timeout, ctx->input_frame_id, &ctx->force_exit) < 0) {
			ret = -1;
			goto end;
		}
		goto end;
	}
	
	ctx->sws_ctx = sws_getCachedContext(ctx->sws_ctx, frame->width, frame->height, frame->format,
		ctx->output_frame_width, ctx->output_frame_height, ctx->output_frame_format,
		use_point ? SWS_POINT : SWS_FAST_BILINEAR, NULL, NULL, NULL);
	if (!ctx->sws_ctx) {
		ret = -1;
		goto end;
	}
	sws_scale(ctx->sws_ctx, frame->data, frame->linesize, 0, frame->height,
		f->data, f->linesize);

	av_frame_copy_props(f, frame);

	if (filped) {
		flip_frame(f);
		filped = 0;
	}

	if (frame_enqueue(ctx->frame_queues[0], f, ctx->timeout, ctx->input_frame_id, &ctx->force_exit) < 0) {
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

	if (frame_enqueue(ctx->frame_queues[1], f, ctx->timeout, ctx->input_frame_id, &ctx->force_exit) < 0) {
		ret = -1;
	}
end:
	av_frame_unref(frame);
	av_frame_free(&f);
	return ret;
}


DWORD WINAPI decode_video_thread(LPVOID p) {
	inout_context* ctx = (inout_context*)p;
	AVFrame* frame = av_frame_alloc();
	AVFrame* decoded = av_frame_alloc();
	AVPacket* pkt = av_packet_alloc();
	AVFrame* filtered_frame = av_frame_alloc();

	if (!frame || !filtered_frame || !decoded || !pkt) {
		goto end;
	}

	ctx->last_video_decode_time = av_gettime_relative();
	int filter_sent_eof = 0;
	while (1)
	{
		if (ctx->filter_contexts[0].filter_graph)
		{
			while (av_buffersink_get_frame(ctx->filter_contexts[0].buffer_sink, filtered_frame) >= 0)
			{
				filtered_frame->pts = av_rescale_q_rnd(filtered_frame->pts, av_buffersink_get_time_base(ctx->filter_contexts[0].buffer_sink), UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
				if (fill_output_video(ctx, filtered_frame) < 0) goto end;
			}
		}

		if (decode_frame(ctx, frame, pkt, decoded, AVMEDIA_TYPE_VIDEO) >= 0)
		{
			ctx->last_video_decode_time = av_gettime_relative();

			if (!filter_sent_eof && need_reinit_filter(ctx, frame->width, frame->height, frame->format, frame->sample_aspect_ratio))
			{
				init_video_filter(ctx, frame->width, frame->height, frame->format,
					ctx->fmt_ctx->streams[ctx->decoders[0].index]->time_base, frame->sample_aspect_ratio);
			}

			if (ctx->filter_contexts[0].filter_graph)
			{
				if (av_buffersrc_add_frame(ctx->filter_contexts[0].buffer_src, frame) < 0)
				{
					av_frame_unref(frame);
				}
			}
			else {
				frame->pts = av_rescale_q_rnd(frame->pts, ctx->fmt_ctx->streams[ctx->decoders[0].index]->time_base, UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
				if (fill_output_video(ctx, frame) < 0) goto end;
			}
		}
		else {
			if (av_gettime_relative() - ctx->last_video_decode_time >= ctx->timeout) {
				ctx->force_exit = 1;
				break;
			}
			if (ctx->eof)
			{
				if (!filter_sent_eof && ctx->filter_contexts[0].filter_graph) {
					filter_sent_eof = 1;
					if (av_buffersrc_add_frame(ctx->filter_contexts[0].buffer_src, NULL) < 0)
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
	ctx->decoders[0].exit = 1;
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
	HANDLE decode_video_tid = NULL;
	HANDLE decode_audio_tid = NULL;

	if (ctx->fmt_ctx)
	{
		ret = open_thread(&read_tid, reading_input, ctx);
		if (ret < 0) goto failed;
	}
	if (ctx->decoders[0].avctx)
	{
		ret = open_thread(&decode_video_tid, decode_video_thread, ctx);
		if (ret < 0) goto failed;
	}
	if (ctx->decoders[1].avctx)
	{
		ret = open_thread(&decode_audio_tid, decode_audio_thread, ctx);
		if (ret < 0) goto failed;
	}


	ctx->reading_tid = read_tid;
	ctx->decode_video_tid = decode_video_tid;
	ctx->decode_audio_tid = decode_audio_tid;
	return 0;

failed:
	ctx->force_exit = 1;

	free_thread(&read_tid);
	free_thread(&decode_video_tid);
	free_thread(&decode_audio_tid);

	return -1;
}

void stop_read(inout_context* ctx, int force)
{
	if (force) {
		ctx->force_exit = 1;
	}
	free_thread(&ctx->reading_tid);
	free_thread(&ctx->decode_video_tid);
	free_thread(&ctx->decode_audio_tid);
}

//void interrupt_output_if_needed(inout_context* ctx)
//{
//	if (av_gettime_relative() - ctx->output_last_clock_time >= ctx->timeout)
//		ctx->output_exit = 1;
//}

void control_output_speed(inout_context* ctx)
{
	int64_t start_clock_time_with_shift = ctx->output_start_clock_time + ctx->output_start_shift_time;
	if (os_sleepto_ns(ctx->output_next_target_clock_time_ns + start_clock_time_with_shift))
	{
		printf("diff_after_sleep: %I64d ns\n", (ctx->output_next_target_clock_time_ns - ((int64_t)os_gettime_ns() - start_clock_time_with_shift)));
	}
	else {
		int64_t new_output_start_clock_time = (int64_t)os_gettime_ns() - ctx->output_next_target_clock_time_ns;
		int64_t count;
		if (ctx->output_video_interval_ns) {
			count = (new_output_start_clock_time - (ctx->output_first_start_clock_time + ctx->output_start_shift_time)) / ctx->output_video_interval_ns;
			if(count > 0)
				ctx->output_start_clock_time = ctx->output_first_start_clock_time + count * ctx->output_video_interval_ns;
		}
		else {
			count = av_rescale_q(new_output_start_clock_time - (ctx->output_first_start_clock_time + ctx->output_start_shift_time), NS_TB, (AVRational) { ctx->output_fps.den, ctx->output_fps.num });
			if (count > 0)
				ctx->output_start_clock_time = ctx->output_first_start_clock_time + av_rescale_q(count, (AVRational) { ctx->output_fps.den, ctx->output_fps.num }, NS_TB);
		}
		if(count > 0)
			printf("reset output_start_clock_time %I64d\n",ctx->output_start_clock_time);
	}
}


int is_audio_fifo_full_fill(inout_context* ctx)
{
	if (av_audio_fifo_size(ctx->output_audio_fifo) >= ctx->output_audio_nb_samples)
	{
		return 1;
	}
	else {
		return 0;
	}
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



int audio_fifo_read_frame(inout_context* ctx, AVFrame* frame)
{
	int ret;
	int64_t frame_end_pts;
	int64_t fifo_total_time;
	int64_t fifo_size = av_audio_fifo_size(ctx->output_audio_fifo);

	ret = av_audio_fifo_read(ctx->output_audio_fifo, frame->data, frame->nb_samples);
	if (ret < 0) return -1;

	if (ctx->output_last_audio_frame_pts != AV_NOPTS_VALUE) {
		frame_end_pts = ctx->output_last_audio_frame_pts + av_rescale_q(ctx->output_last_audio_nb_samples, (AVRational) { 1, ctx->output_audio_sample_rate }, UNIVERSAL_TB);
		fifo_total_time = av_rescale_q_rnd(fifo_size, (AVRational) { 1, ctx->output_audio_sample_rate } , UNIVERSAL_TB, AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);

		frame->pts = frame_end_pts - fifo_total_time;
	}
	else {
		frame->pts = AV_NOPTS_VALUE;
	}

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
	EnterCriticalSection(&ctx->input_change_mutex);
	int64_t output_pts_time = (is_video ? ctx->output_video_pts_time : ctx->output_audio_pts_time);
	if (ctx->output_time_offset == AV_NOPTS_VALUE)
	{
		ctx->output_time_offset = output_pts_time - timestamp;
		ret = 1;
		goto end;
	}
	else{
		int64_t offset_diff = timestamp + ctx->output_time_offset - output_pts_time;
		//printf("%I64d\n", offset_diff);
		if (offset_diff > 5 * 1000000 || offset_diff < -5 * 1000000) // 相差超过5秒就调整，1秒仅可调一次
		{
			int64_t current_time = av_gettime_relative();
			if (ctx->output_time_offset_last_adjust_time == AV_NOPTS_VALUE || current_time - ctx->output_time_offset_last_adjust_time > 1 * 1000000) {
				ctx->output_time_offset_last_adjust_time = current_time;
				ctx->output_time_offset = output_pts_time - timestamp;
				ret = 1;
				goto end;
			}
		}
	}
end:
	LeaveCriticalSection(&ctx->input_change_mutex);
	return ret;
}

int is_in_sync(inout_context* ctx, int64_t timestamp,int is_video)//1:刚好，2:快了，0：慢了(或者必须解码)
{
	if (timestamp == AV_NOPTS_VALUE || ctx->output_time_offset == AV_NOPTS_VALUE) return 0;

	if (is_video && ctx->output_current_video_frame_id < ctx->output_current_audio_frame_id) return 0;
	if (!is_video && ctx->output_current_audio_frame_id < ctx->output_current_video_frame_id) return 0;

	int64_t output_pts_time = (is_video ? ctx->output_video_pts_time : ctx->output_audio_pts_time);
	int64_t interval = (is_video ? ctx->output_time_per_video_frame : ctx->output_time_per_audio_frame);

	int64_t diff = timestamp + ctx->output_time_offset - output_pts_time;
	if (diff >= -0.75 * interval && diff < 0.75 * interval) //假如刚好有diff是-0.7501 * interval，那么会return 0，假如输入帧率和输出帧率相等，读取的下一帧diff就会是0.2499 * interval左右，允许有0.5001 * interval的抖动
		return 1;
	else if (diff > 0)
		return 2;
	else
		return 0;
}


int64_t get_sync_diff(inout_context* ctx, int64_t timestamp)
{
	if (timestamp == AV_NOPTS_VALUE || ctx->output_time_offset == AV_NOPTS_VALUE)
		return AV_NOPTS_VALUE;


	return timestamp + ctx->output_time_offset - ctx->output_video_pts_time;
}


int output_audio(inout_context* ctx,AVFrame *frame)
{
	frame->pts = ctx->output_audio_pts_time;
	if (ctx->audio_callback) {
		ctx->audio_callback(ctx->callback_private, frame);
	}
	ctx->output_audio_pts_time = av_rescale_q(ctx->output_sample_count,
		(AVRational) {1, ctx->output_audio_sample_rate}, UNIVERSAL_TB);
	ctx->output_sample_count += frame->nb_samples;
	//printf("output audio: %lld", ctx->output_audio_pts_time);
	return 0;
}

int output_video(inout_context* ctx, AVFrame* frame)
{
	frame->pts = ctx->output_video_pts_time;
	frame->pict_type = AV_PICTURE_TYPE_NONE;
	if (ctx->video_callback) {
		ctx->video_callback(ctx->callback_private, frame);
	}
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
	ctx->output_frame_count += 1;
	//printf("output video: %lld", ctx->output_video_pts_time);
	return 0;
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
	AVFrame* empty_video_frame = av_frame_alloc();
	AVFrame* empty_audio_frame = av_frame_alloc();

	if (!video_frame || !audio_frame || !final_video_frame || !empty_video_frame || !empty_audio_frame || !final_audio_frame || !fifo_out_audio_frame) {
		ret = -1;
		goto end;
	}

	empty_video_frame->width = ctx->output_frame_width;
	empty_video_frame->height = ctx->output_frame_height;
	empty_video_frame->format = ctx->output_frame_format;

	//TODO 填充彩条
	if (get_video_buffer(empty_video_frame) < 0)
	{
		printf("get_video_buffer failed!\n");
		ret = -1;
		goto end;
	}
	if (av_frame_ref(final_video_frame, empty_video_frame) < 0) {
		printf("av_frame_ref failed!\n");
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

	int64_t last_in_sync_audio_pts = AV_NOPTS_VALUE;
	int reset;
	int audio_in_sync = 0;

	ctx->output_first_start_clock_time = ctx->output_start_clock_time = os_gettime_ns();

	while (1)
	{
		if (ctx->output_exit) break;
		control_output_speed(ctx);//每编码完一轮音频+一帧视频就会进到这里看有没有快了，快了就sleep
		//interrupt_output_if_needed(ctx);

		while (ctx->output_audio_pts_time <= ctx->output_video_pts_time)
		{
		audio_loop:
			audio_in_sync = 0;
			while (is_in_sync(ctx, fifo_out_audio_frame->pts, 0) == 0) {
				while (!is_audio_fifo_full_fill(ctx))
				{
					EnterCriticalSection(&ctx->input_change_mutex);
					ret = frame_dequeue(ctx->frame_queues[1], audio_frame, &ctx->output_current_audio_frame_id, &reset);
					LeaveCriticalSection(&ctx->input_change_mutex);

					if (reset) {
						ctx->output_time_offset = AV_NOPTS_VALUE;
					}

					if (ctx->output_exit) {
						goto end;
					}
					if (ret >= 0)
					{
						last_audio_sync_diff = get_sync_diff(ctx, audio_frame->pts);
						fill_audio_fifo(ctx, audio_frame);
					}
					else {
						if (last_audio_sync_diff != AV_NOPTS_VALUE && last_audio_sync_diff < 0 && ctx->output_current_audio_frame_id >= ctx->output_current_video_frame_id) {
							ctx->output_time_offset = AV_NOPTS_VALUE;
							last_audio_sync_diff = AV_NOPTS_VALUE;
						}
						goto audio_output;
					}
				}
				if (audio_fifo_read_frame(ctx, fifo_out_audio_frame) > 0) {
					update_offset_if_needed(ctx, fifo_out_audio_frame->pts, 0);
					continue;
				}
			}

			if (is_in_sync(ctx, fifo_out_audio_frame->pts, 0) == 1 && (last_in_sync_audio_pts == AV_NOPTS_VALUE || last_in_sync_audio_pts != fifo_out_audio_frame->pts)) {
				audio_in_sync = 1;
				last_in_sync_audio_pts = fifo_out_audio_frame->pts;
			}
			else {
				if (update_offset_if_needed(ctx, fifo_out_audio_frame->pts, 1))
					goto audio_loop;
			}

		audio_output:
			av_frame_unref(final_audio_frame);
			if (audio_in_sync) {
				av_frame_ref(final_audio_frame, fifo_out_audio_frame);
			}
			else {
				av_frame_ref(final_audio_frame, empty_audio_frame);
			}
			output_audio(ctx, final_audio_frame);
		}

	video_loop:
		while (is_in_sync(ctx, video_frame->pts,1) == 0)//视频慢了或者必须dequeue(比如音频帧已经是下一个文件，或者视频帧是AV_NOPTS_VALUE)
		{
			EnterCriticalSection(&ctx->input_change_mutex);
			ret = frame_dequeue(ctx->frame_queues[0], video_frame, &ctx->output_current_video_frame_id, &reset);
			LeaveCriticalSection(&ctx->input_change_mutex);

			if (reset) {
				ctx->output_time_offset = AV_NOPTS_VALUE;
			}

			if (ctx->output_exit) {
				goto end;
			}
			if (ret >= 0)
			{
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

		if (is_in_sync(ctx, video_frame->pts, 1) == 1)
		{
			//printf("v: %I64d\n", video_frame->pts);
			av_frame_unref(final_video_frame);
			av_frame_ref(final_video_frame, video_frame);
			//printf("ref delay: %I64d\n",av_gettime_relative() - ctx->output_last_clock_time);
		}
		else {
			if (update_offset_if_needed(ctx, video_frame->pts, 1))
				goto video_loop;
		}

	video_output:
		output_video(ctx, final_video_frame);
	}

end:
	av_frame_free(&video_frame);
	av_frame_free(&audio_frame);
	av_frame_free(&final_video_frame);
	av_frame_free(&empty_video_frame);
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
	int decoder_all_exit = 1;
	for (int i = 0; i < ARRAY_ELEMS(ctx->decoders); i++) {
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

DWORD main_thread(LPVOID p) {
	HRESULT hr = CoInitialize(NULL);

	paremeter_table_context* table = paremeter_table_alloc(48);
	play_list_context* play_list_ctx = NULL;
	inout_options* opts = (inout_options*)p;

	char* play_list_file = NULL;
	char* filter_file = NULL;
	char* audio_filter_file = NULL;
	char* control_file_name = NULL;
	char* index_file_name = NULL;
	char* output_start_shift_file_name = NULL;
	int use_fixed_frame_interval = 0;
	AVRational frame_rate = opts->video_out_fps;
	const char* config_path = opts->config_path;
	if (!table) return -1;
	char* buf = NULL, * buf2 = NULL, str[1024] = { 0 }, time_str[20] = { 0 }, str2[1024] = { 0 }, hw_decode[16] = "";
	int video_frame_buffer = 10, audio_frame_buffer = 50, packet_queue_size = 50 * 1024 * 1024, timeout = 30 * 1000000, reopen_at_list_update = 0;
	int ret = -1;
	char* play_filename = NULL;
	AVDictionary* play_dict = NULL;
	AVDictionary* play_dict_in_list = NULL;
	char* pathvar = NULL;
	if(config_path){
		pathvar = getenv(config_path);
		if (pathvar)
			printf("%s=%s\n", config_path, pathvar);
	}

	char* config_file_name = join_path(pathvar, "config.ini");
	char* current_play_file_name = join_path(pathvar, "current_play_file.txt");
	char* log_file_name = join_path(pathvar,"log.txt");

	update_config_from_file(config_file_name, table);
	paremeter_table_print(table);

	play_list_file = get_paremeter_table_content(table, "config", "play_list",TRUE);
	control_file_name = get_paremeter_table_content(table, "config", "control_file", TRUE);
	filter_file = get_paremeter_table_content(table, "config", "filter_file", TRUE);
	audio_filter_file = get_paremeter_table_content(table, "config", "audio_filter_file", TRUE);
	index_file_name = get_paremeter_table_content(table, "config", "index_file", TRUE);
	output_start_shift_file_name = get_paremeter_table_content(table, "config", "output_start_time_shift", TRUE);

	play_list_file = join_path_free_filename(pathvar, play_list_file);
	control_file_name = join_path_free_filename(pathvar, control_file_name);
	filter_file = join_path_free_filename(pathvar, filter_file);
	audio_filter_file = join_path_free_filename(pathvar, audio_filter_file);
	index_file_name = join_path_free_filename(pathvar, index_file_name);
	output_start_shift_file_name = join_path_free_filename(pathvar, output_start_shift_file_name);

	if (buf = get_paremeter_table_content(table, "config", "hw_decode", FALSE)) strcpy_s(hw_decode, sizeof(hw_decode), buf);
	if (buf = get_paremeter_table_content(table, "config", "reopen_at_list_update", FALSE)) reopen_at_list_update = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "log_level", FALSE)) av_log_set_level(get_loglevel_from_str(buf));
	if (buf = get_paremeter_table_content(table, "config", "video_frame_buffer", FALSE)) video_frame_buffer = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "audio_frame_buffer", FALSE)) audio_frame_buffer = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "packet_queue_size", FALSE)) packet_queue_size = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "timeout", FALSE)) timeout = atoi(buf);
	if (buf = get_paremeter_table_content(table, "config", "use_fixed_frame_interval", FALSE)) use_fixed_frame_interval = atoi(buf);

	frame_rate.den = frame_rate.den <= 0 ? 1 : frame_rate.den;

	video_frame_buffer = video_frame_buffer <= 0 ? (frame_rate.num / frame_rate.den) : video_frame_buffer;


	AVChannelLayout ch_layout = { 0 };
	av_channel_layout_default(&ch_layout, opts->audio_out_channels);
	inout_context* ctx = inout_context_alloc(opts->video_out_width, opts->video_out_height, opts->video_out_format, opts->video_out_fps, opts->audio_out_sample_rate, opts->audio_out_format, &ch_layout, 1024, timeout, packet_queue_size, 5000, video_frame_buffer, audio_frame_buffer, use_fixed_frame_interval);
	av_channel_layout_uninit(&ch_layout);
	if (!ctx) {
		goto end;
	}
	ctx->video_callback = opts->video_callback;
	ctx->audio_callback = opts->audio_callback;
	ctx->callback_private = opts->callback_private;

	int64_t last_control_mtime = 0;
	int64_t last_filters_mtime = 0;
	int64_t last_audio_filters_mtime = 0;
	int64_t last_inputs_mtime = 0;
	int64_t last_indexs_mtime = 0;
	int64_t last_shift_mtime = 0;

	int seek_number = 1;
	int play_mode = 3;//loop_list,loop_single,loop_random
	start_output(ctx);
	while (1)
	{
		int64_t ret = 0;
		play_list* list_item;
		if (opts->send_exit) {
			ctx->output_exit = opts->send_exit;
			ctx->force_exit = opts->send_exit;
			break;
		}
		if (is_output_exit(ctx)) break;
		//ctx->output_exit = 1;
		if (ret = is_file_changed(play_list_file, last_inputs_mtime))
		{
			if (ret != -1) {
				last_inputs_mtime = ret;
				play_list_free(&play_list_ctx);
				play_list_ctx = play_list_alloc();
				if (play_list_ctx) {
					update_play_list_from_file(play_list_ctx, play_list_file);
					printf("play_list updated!\n");
					if (reopen_at_list_update) {
						stop_read(ctx, 1);
					}
				}
			}
		}

		if (ret = is_file_changed(control_file_name, last_control_mtime))
		{
			if (ret != -1) {
				last_control_mtime = ret;
				ret = control_input_playing(control_file_name, str, sizeof(str));
				av_dict_free(&play_dict);
				play_dict_in_list = NULL;
				play_filename = NULL;
				if (ret == 1)
				{
					list_item = play_list_search(play_list_ctx, str);
					if (!list_item) {
						char* tab_pos = strchr(str, '\t');
						if (tab_pos) {
							*tab_pos = '\0';
							play_filename = str;
							av_dict_parse_string(&play_dict, tab_pos + 1, "=", ",",0);
						}
						else {
							play_filename = str;
						}
					}
					else {
						play_filename = list_item->value;
						play_dict_in_list = list_item->dict;
					}
					stop_read(ctx, 1);
					goto input;
				}
				else if (ret == 2)
				{
					list_item = play_list_seek(play_list_ctx, atoi(str));
					if (list_item) {
						play_filename = list_item->value;
						play_dict_in_list = list_item->dict;
					}
					stop_read(ctx, 1);
					goto input;
				}
				else if (ret >= 3) {
					play_mode = (int)ret;
				}
			}
		}
		if (is_input_exit(ctx)) {
			switch (play_mode)
			{
			case 3:
				seek_number = 1;
				break;
			case 4:
				seek_number = 0;
				break;
			case 5:
				seek_number = generate_random_number(play_list_get_size(play_list_ctx) - 1);
				break;
			default:
				break;
			}
			list_item = play_list_seek(play_list_ctx, seek_number);
			if (list_item) {
				play_filename = list_item->value;
				play_dict_in_list = list_item->dict;
				av_dict_free(&play_dict);
			}

		input:
			if (play_filename != NULL) {
				stop_read(ctx, 0);
				inout_context_reset_input(ctx);
				AVDictionary* dict_p = play_dict_in_list ? play_dict_in_list : play_dict;
				AVDictionary* temp_dict = NULL;
				char *format = NULL;
				char* video_filter = NULL;

				char* audio_filter = NULL;
				char* video_index = NULL;
				char* audio_index = NULL;
				char* seek_time = NULL;
				char* analyze_duration = NULL;
				char* probesize = NULL;
				char* queue_left = NULL;
				char* queue_right = NULL;
				int video_index_int = -1;
				int audio_index_int = -1;
				int analyze_duration_int = 0;
				int probesize_int = 0;
				int queue_left_int = -1;
				int queue_right_int = -1;
				if (dict_p) {
					if (av_dict_copy(&temp_dict, dict_p, 0) >= 0) {
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
					}
				}

				if (video_index) {
					video_index_int = atoi(video_index);
				}
				if (audio_index) {
					audio_index_int = atoi(audio_index);
				}

				if (probesize) {
					probesize_int = atoi(probesize);
				}

				if (analyze_duration) {
					analyze_duration_int = atoi(analyze_duration);
				}


				if (queue_left) {
					queue_left_int = atoi(queue_left);
				}

				if (queue_right) {
					queue_right_int = atoi(queue_right);
				}
				

				if (open_input(format, play_filename, current_play_file_name, &temp_dict, ctx, video_index_int, audio_index_int, -1, hw_decode, probesize_int, analyze_duration_int, queue_left_int, queue_right_int) >= 0) {
					if (!video_filter) {
						buf = get_filter_text(filter_file);
						set_input_filter(ctx, buf);
						av_free(buf);
					}
					else {
						set_input_filter(ctx, video_filter);
					}

					if (!audio_filter) {
						buf = get_filter_text(audio_filter_file);
						set_input_audio_filter(ctx, buf);
						av_free(buf);
					}
					else {
						set_input_audio_filter(ctx, audio_filter);
					}
					if (seek_time && ctx->fmt_ctx) {
						int seek_time_int = atoi(seek_time);
						
						int64_t seek_time_int64 = av_rescale_q(seek_time_int, (AVRational) { 1, 1 }, (AVRational) { 1, AV_TIME_BASE }) + ctx->fmt_ctx->start_time;
						if (avformat_seek_file(ctx->fmt_ctx, -1, INT64_MIN, seek_time_int64, seek_time_int64, 0) >= 0) {
							printf("[%s] seek: %I64d\n", play_filename, seek_time_int64);
						}
						else {
							printf("[%s] seek: %I64d failed!\n", play_filename, seek_time_int64);
						}
					}
					set_stream_index_from_file(ctx, index_file_name);

					start_read(ctx);
					sprintf_s(str2, sizeof(str2), "[%s] Playing : %s\n", get_time_string(time_str,sizeof(time_str)), play_filename);
					write_text_to_file(log_file_name, str2, "a");
					printf(str2);
				}
				else {
					printf("[%s] open:\"%s\" failed!\n", get_time_string(time_str, sizeof(time_str)), play_filename);
				}

				av_dict_free(&temp_dict);

				if(format)
					av_free(format);
				if (video_filter)
					av_free(video_filter);
				if (audio_filter)
					av_free(audio_filter);
				if (video_index)
					av_free(video_index);
				if (audio_index)
					av_free(audio_index);
				if (seek_time)
					av_free(seek_time);
				if (analyze_duration)
					av_free(analyze_duration);
				if (probesize)
					av_free(probesize);
				if (queue_left)
					av_free(queue_left);
				if (queue_right)
					av_free(queue_right);
			}
		}
		if ((ret = is_file_changed(index_file_name, last_indexs_mtime)) > 0)
		{
			last_indexs_mtime = ret;
			if (set_stream_index_from_file(ctx, index_file_name) >= 0) {
				printf("set indexs!\n");
			}
		}

		if ((ret = is_file_changed(filter_file, last_filters_mtime)) > 0)
		{
			last_filters_mtime = ret;
			char* buf = get_filter_text(filter_file);
			set_input_filter(ctx, buf);
			printf("filter:\"%s\"\n", buf);
			av_free(buf);
		}

		if ((ret = is_file_changed(audio_filter_file, last_audio_filters_mtime)) > 0)
		{
			last_audio_filters_mtime = ret;
			char* buf = get_filter_text(audio_filter_file);
			set_input_audio_filter(ctx, buf);
			printf("audio filter:\"%s\"\n", buf);
			av_free(buf);
		}

		if ((ret = is_file_changed(output_start_shift_file_name, last_shift_mtime)) > 0)
		{
			last_shift_mtime = ret;
			int64_t shift = get_txt_num(output_start_shift_file_name);
			ctx->output_start_shift_time = shift * 1000LL;
			printf("output start time shift: %I64d\n", shift);
		}

		av_usleep(500000);
	}
end:
	stop_read(ctx, 1);
	//printf("stop_read\n");
	stop_output(ctx);

	//printf("stop_output\n");
	inout_context_free(&ctx);
	play_list_free(&play_list_ctx);
	paremeter_table_free(&table);

	if (config_file_name) {
		av_free(config_file_name);
	}
	if (current_play_file_name) {
		av_free(current_play_file_name);
	}
	if (log_file_name) {
		av_free(log_file_name);
	}
	if (play_list_file)
		av_free(play_list_file);
	if (control_file_name)
		av_free(control_file_name);
	if (filter_file)
		av_free(filter_file);
	if (audio_filter_file)
		av_free(audio_filter_file);
	if (index_file_name)
		av_free(index_file_name);
	if (output_start_shift_file_name)
		av_free(output_start_shift_file_name);

	if (play_dict)
		av_dict_free(&play_dict);

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
		//if (open_input(NULL, "D:\\1.mp4",NULL,
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
		inout_context* ctx = inout_context_alloc(640, 480, AV_PIX_FMT_YUV420P, (AVRational) { 25, 1 }, 48000, AV_SAMPLE_FMT_S16, & ch_layout, 1024, 30 * 1000000, 50 * 1000000, 5000, 10, 10,1);
		av_channel_layout_uninit(&ch_layout);
		open_input(NULL, "D:\\develop\\videogen\\2.mp4", NULL, NULL, ctx, -1, -1, -1, "", 0, 0, -1, -1);
		inout_context_reset_input(ctx);
		inout_context_free(&ctx);
	}
}