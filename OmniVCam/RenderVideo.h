#define _CRT_SECURE_NO_WARNINGS
#pragma once
#ifdef __cplusplus
extern "C" {
#endif
#include <stdio.h>
#include <sys/stat.h>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersink.h>
#include <libavfilter/buffersrc.h>
#include <libavutil/opt.h>
#include <libavutil/hwcontext.h>
#include "libavutil/audio_fifo.h"
#include <libavutil/time.h>
#include <libavutil/imgutils.h>
#include <libavutil/samplefmt.h>
#include <libavdevice/avdevice.h>
#include <Windows.h>
#include <string.h>
#include <time.h>
extern AVRational UNIVERSAL_TB;
extern AVRational DSHOW_TB;
#define ARRAY_ELEMS(a) (sizeof(a)/sizeof(a[0]))
//#define OMNI_DEBUG 0
#ifdef OMNI_DEBUG
#define DEBUG_LOG(format, ...) printf("%s ",__func__); printf(format, ##__VA_ARGS__);
#else
#define DEBUG_LOG(format, ...) ;
#endif // DEBUG



typedef struct avframe_node {
	AVFrame* frame;
	int64_t frame_id;//用于标记帧是否来自于同一个文件，open_input一次，此id更新。
	int reset;
	struct avframe_node* next;
}avframe_node;

typedef struct frame_queue {
	avframe_node* front, * rear;
	int count;
	int max_count;
	int left_count;
	int right_count;
	int center_count;
	int64_t last_pts_value;
	int64_t last_interval;
	int reached_center; //对实时流有用，当达到center时，reached_center设置为1，就可以一直读，读到数量left_count时reached_center重设为0，如果数量大于等于right_count，则清除到center位置
	CRITICAL_SECTION mutex;
	CONDITION_VARIABLE cond;
}frame_queue;

typedef struct avpacket_node {
	AVPacket* pkt;
	struct avpacket_node* next;
}avpacket_node;

typedef struct packet_queue {
	avpacket_node* front, * rear;
	int size;//in KByte
	int max_size;//in KByte
	int count;
	int max_count;
	CRITICAL_SECTION mutex;
	CONDITION_VARIABLE cond;
}packet_queue;

typedef struct codec_context
{
	AVCodecContext* avctx;
	int index;
	int exit;//解码线程结束标识
	int sent_eof;
	CRITICAL_SECTION mutex;
}codec_context;

typedef struct filter_context {
	AVFilterGraph* filter_graph;
	AVFilterInOut* input, * output;
	AVFilterContext* buffer_src, * buffer_sink;
}filter_context;

typedef struct inout_context {
	char* input_name;
	char* current_status_file_name;
	CRITICAL_SECTION input_change_mutex;
	AVFormatContext* fmt_ctx;
	codec_context decoders[3];
	packet_queue queues[3];//one video one audio one subtitle
	filter_context filter_contexts[2];
	frame_queue* frame_queues[2];//output queue, should convert to output format

	struct SwsContext* sws_ctx;
	SwrContext* swr_ctx;
	AVSubtitle sub;
	HANDLE reading_tid;
	HANDLE decode_video_tid;
	HANDLE decode_audio_tid;

	enum AVPixelFormat hw_pix_fmt;
	enum AVHWDeviceType hw_type;
	AVBufferRef* hw_device_ctx;

	int last_frame_width;
	int last_frame_height;
	int last_frame_format;

	int last_audio_sample_rate;
	int last_audio_format;
	AVChannelLayout last_audio_layout;

	int last_filtered_audio_sample_rate;
	int last_filtered_audio_format;
	AVChannelLayout last_filtered_audio_layout;

	int needs_reinit_filter;
	int needs_reinit_audio_filter;
	char* filter_text;
	char* audio_filter_text;
	CRITICAL_SECTION filter_text_mutex;

	int64_t input_frame_id;

	AVRational last_sar;

	int64_t last_video_decode_time;
	int64_t last_audio_decode_time;
	int64_t last_clock_time;

	int64_t input_start_time;
	int64_t last_packet_time; //AV_TIME_BASE
	int64_t timeout;
	int force_exit;
	int eof;

	int output_frame_width;
	int output_frame_height;
	int output_frame_format;

	int output_audio_sample_rate;
	int output_audio_format;
	AVChannelLayout output_audio_layout;

	int64_t output_time_per_video_frame;
	int64_t output_time_per_audio_frame;
	int64_t output_video_pts_time;
	int64_t output_audio_pts_time;
	int64_t output_frame_count;
	int64_t output_sample_count;
	int64_t output_time_offset;
	int64_t output_time_offset_last_adjust_time;
	int64_t output_last_audio_frame_pts; //UNIVERSAL_TB  用于计算fifo后音频的pts
	int output_last_audio_nb_samples; //用于计算fifo后音频的pts
	int output_audio_nb_samples; //用于计算fifo后音频的pts
	int64_t output_current_video_frame_id;   //这个用于识别帧是否来源于同一个视频，这个数值是递增的
	int64_t output_current_audio_frame_id;
	AVAudioFifo* output_audio_fifo;//用output audio的来初始化
	HANDLE output_thread;
	int output_exit;//强制退出标志
	int64_t output_first_start_clock_time;
	int64_t output_start_clock_time;
	int64_t output_start_shift_time;
	//int64_t output_last_clock_time;
	int64_t output_delayed_time;//输出慢了多少时间
	int64_t output_last_pause_time;
	int64_t output_drop_frame_time;//丢多少时间的帧
	AVRational output_fps;
	int64_t output_last_detect_pts_time;
	int64_t output_detect_speed_interval;
	int64_t output_last_force_refresh_time;

	void (*video_callback)(void* priv, AVFrame*);
	void (*audio_callback)(void* priv, AVFrame*);
	void* callback_private;
}inout_context;


typedef struct inout_options {
	int video_out_width;
	int video_out_height;
	int video_out_format;
	AVRational video_out_fps;
	int audio_out_sample_rate;
	int audio_out_format;
	int audio_out_channels;
	int audio_out_nb_samples;
	void (*video_callback)(void *priv, AVFrame*);
	void (*audio_callback)(void* priv, AVFrame*);
	void *callback_private;
	int send_exit;
}inout_options;

int open_thread(HANDLE* thread, LPTHREAD_START_ROUTINE start, LPVOID arg);
void free_thread(HANDLE* thread);
DWORD main_thread(LPVOID p);

#ifdef __cplusplus
}
#endif