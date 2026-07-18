#include "dshow_camera.h"
#include "dshow_grabber.h"
#include "Threads.h"
#include "video_frame.h"

#include <dvdmedia.h>
#include <ks.h>
#include <ksmedia.h>
#include <olectl.h>
#include <atlbase.h>
#include <string>
#include <vector>
#include <deque>
#include <algorithm>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/base64.h>
#include <libavutil/imgutils.h>
#include <libavutil/pixdesc.h>
#include <libavutil/time.h>
}

struct dshow_packet_queue {
    CRITICAL_SECTION mutex;
    CONDITION_VARIABLE cond;
    std::deque<AVPacket*> packets;
    size_t bytes;
    size_t max_bytes;
    size_t max_packets;
    int stopping;
    int reset_decoder;

    dshow_packet_queue() : bytes(0), max_bytes(0), max_packets(0), stopping(0), reset_decoder(0)
    {
        InitializeCriticalSection(&mutex);
        InitializeConditionVariable(&cond);
    }

    ~dshow_packet_queue()
    {
        for (AVPacket* packet : packets) av_packet_free(&packet);
        DeleteCriticalSection(&mutex);
    }
};

struct dshow_camera {
    CComPtr<IGraphBuilder> graph;
    CComPtr<ICaptureGraphBuilder2> builder;
    CComPtr<IMediaControl> control;
    CComPtr<IBaseFilter> source;
    CComPtr<IPin> source_pin;
    DShowGrabber* grabber;
    AM_MEDIA_TYPE media_type;
    AVCodecContext* decoder;
    dshow_packet_queue video_packets;
    HANDLE video_decoder_thread;
    AVPixelFormat pixel_format;
    int width;
    int height;
    REFERENCE_TIME frame_interval;
    int64_t next_pts;
    int use_video_device_timestamp;
    int use_audio_device_timestamp;
    dshow_camera_frame_callback callback;
    CComPtr<IBaseFilter> audio_source;
    CComPtr<IPin> audio_source_pin;
    DShowGrabber* audio_grabber;
    AM_MEDIA_TYPE audio_media_type;
    AVCodecContext* audio_decoder;
    dshow_packet_queue audio_packets;
    HANDLE audio_decoder_thread;
    REFERENCE_TIME audio_next_pts;
    dshow_camera_frame_callback audio_callback;
    void* opaque;
};

static std::wstring utf8_to_wide(const char* text)
{
    if (!text || !*text) return std::wstring();
    int count = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
    if (count <= 1) return std::wstring();
    std::wstring result((size_t)count, L'\0');
    if (MultiByteToWideChar(CP_UTF8, 0, text, -1, &result[0], count) != count)
        return std::wstring();
    result.resize((size_t)count - 1);
    return result;
}

static std::string wide_to_utf8(const wchar_t* text)
{
    if (!text || !*text) return std::string();
    int count = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (count <= 1) return std::string();
    std::string result((size_t)count, '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, text, -1, &result[0], count, NULL, NULL) != count)
        return std::string();
    result.resize((size_t)count - 1);
    return result;
}

static std::string base64(const std::string& value)
{
    if (value.empty()) return std::string();
    std::vector<char> buffer((value.size() + 2) / 3 * 4 + 1);
    av_base64_encode(buffer.data(), (int)buffer.size(),
        reinterpret_cast<const uint8_t*>(value.data()), (int)value.size());
    return std::string(buffer.data());
}

static void set_error(char* error, size_t error_size, const char* text, HRESULT hr = S_OK)
{
    if (!error || !error_size) return;
    if (FAILED(hr)) sprintf_s(error, error_size, "%s (0x%08lx)", text, (unsigned long)hr);
    else strcpy_s(error, error_size, text);
}

static HRESULT enumerate_devices(const GUID& category, IEnumMoniker** result)
{
    if (!result) return E_POINTER;
    *result = NULL;
    CComPtr<ICreateDevEnum> dev_enum;
    HRESULT hr = dev_enum.CoCreateInstance(CLSID_SystemDeviceEnum);
    if (FAILED(hr)) return hr;
    return dev_enum->CreateClassEnumerator(category, result, 0);
}

static HRESULT bind_device(const std::wstring& wanted, const GUID& category, IBaseFilter** filter)
{
    if (!filter) return E_POINTER;
    *filter = NULL;
    CComPtr<IEnumMoniker> devices;
    HRESULT hr = enumerate_devices(category, &devices);
    if (hr != S_OK) return hr;
    CComPtr<IMoniker> moniker;
    while (devices->Next(1, &moniker, NULL) == S_OK) {
        LPOLESTR display_name = NULL;
        if (SUCCEEDED(moniker->GetDisplayName(NULL, NULL, &display_name)) &&
            display_name && wanted == display_name) {
            hr = moniker->BindToObject(NULL, NULL, IID_PPV_ARGS(filter));
            CoTaskMemFree(display_name);
            return hr;
        }
        CoTaskMemFree(display_name);
        moniker.Release();
    }
    return VFW_E_NOT_FOUND;
}

static HRESULT find_output_pin(IBaseFilter* filter, const std::wstring& wanted_id, IPin** result)
{
    if (!filter || !result) return E_POINTER;
    *result = NULL;
    CComPtr<IEnumPins> pins;
    HRESULT hr = filter->EnumPins(&pins);
    if (FAILED(hr)) return hr;
    CComPtr<IPin> pin;
    while (pins->Next(1, &pin, NULL) == S_OK) {
        PIN_DIRECTION direction;
        LPWSTR id = NULL;
        if (SUCCEEDED(pin->QueryDirection(&direction)) && direction == PINDIR_OUTPUT &&
            SUCCEEDED(pin->QueryId(&id))) {
            bool match = wanted_id.empty() || wanted_id == id;
            CoTaskMemFree(id);
            if (match) return pin.CopyTo(result);
        }
        pin.Release();
    }
    return VFW_E_NOT_FOUND;
}

static BITMAPINFOHEADER* video_header(AM_MEDIA_TYPE* type, REFERENCE_TIME* interval)
{
    if (!type || !type->pbFormat) return NULL;
    if (type->formattype == FORMAT_VideoInfo && type->cbFormat >= sizeof(VIDEOINFOHEADER)) {
        VIDEOINFOHEADER* info = reinterpret_cast<VIDEOINFOHEADER*>(type->pbFormat);
        if (interval) *interval = info->AvgTimePerFrame;
        return &info->bmiHeader;
    }
    if (type->formattype == FORMAT_VideoInfo2 && type->cbFormat >= sizeof(VIDEOINFOHEADER2)) {
        VIDEOINFOHEADER2* info = reinterpret_cast<VIDEOINFOHEADER2*>(type->pbFormat);
        if (interval) *interval = info->AvgTimePerFrame;
        return &info->bmiHeader;
    }
    return NULL;
}

struct dshow_video_format {
    AVPixelFormat pixel_format;
    AVCodecID codec_id;
};

static void dshow_set_pix_fmt_and_codec_id(dshow_video_format* info, const GUID& subtype,
    DWORD compression, WORD bits)
{
    static const GUID subtype_i420 = { 0x30323449, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    static const GUID subtype_y800 = { 0x30303859, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    static const GUID subtype_hevc = { 0x43564548, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    static const GUID subtype_h264 = { 0x34363248, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
    static const GUID subtype_p010 = { '010P', 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };

    info->pixel_format = AV_PIX_FMT_NONE;
    info->codec_id = AV_CODEC_ID_NONE;

    if (subtype == MEDIASUBTYPE_RGB24) info->pixel_format = AV_PIX_FMT_BGR24;
    else if (subtype == MEDIASUBTYPE_RGB32) info->pixel_format = AV_PIX_FMT_0RGB32;
    else if (subtype == MEDIASUBTYPE_ARGB32) info->pixel_format = AV_PIX_FMT_RGB32;
    else if (subtype == subtype_i420 || subtype == MEDIASUBTYPE_IYUV || subtype == MEDIASUBTYPE_YV12)
        info->pixel_format = AV_PIX_FMT_YUV420P;
    else if (subtype == MEDIASUBTYPE_NV12) info->pixel_format = AV_PIX_FMT_NV12;
    else if (subtype == subtype_y800) info->pixel_format = AV_PIX_FMT_GRAY8;
    else if (subtype == subtype_p010) info->pixel_format = AV_PIX_FMT_P010;
    else if (subtype == MEDIASUBTYPE_YVYU) info->pixel_format = AV_PIX_FMT_YVYU422;
    else if (subtype == MEDIASUBTYPE_YUY2) info->pixel_format = AV_PIX_FMT_YUYV422;
    else if (subtype == MEDIASUBTYPE_UYVY) info->pixel_format = AV_PIX_FMT_UYVY422;
    else if (subtype == subtype_h264) info->codec_id = AV_CODEC_ID_H264;
    else if (subtype == subtype_hevc) info->codec_id = AV_CODEC_ID_HEVC;
    else if (subtype == MEDIASUBTYPE_MJPG) info->codec_id = AV_CODEC_ID_MJPEG;
    else {
        switch (compression) {
        case MKTAG('R','G','B','2'):
        case MKTAG('R','G','B','4'): info->pixel_format = AV_PIX_FMT_0RGB32; break;
        case MKTAG('A','R','G','B'): info->pixel_format = AV_PIX_FMT_RGB32; break;
        case MKTAG('I','4','2','0'):
        case MKTAG('I','Y','U','V'):
        case MKTAG('Y','V','1','2'): info->pixel_format = AV_PIX_FMT_YUV420P; break;
        case MKTAG('N','V','1','2'): info->pixel_format = AV_PIX_FMT_NV12; break;
        case MKTAG('N','V','2','1'): info->pixel_format = AV_PIX_FMT_NV21; break;
        case MKTAG('Y','8','0','0'): info->pixel_format = AV_PIX_FMT_GRAY8; break;
        case MKTAG('P','0','1','0'): info->pixel_format = AV_PIX_FMT_P010; break;
        case MKTAG('Y','V','Y','U'): info->pixel_format = AV_PIX_FMT_YVYU422; break;
        case MKTAG('Y','U','Y','2'):
        case MKTAG('Y','U','Y','V'): info->pixel_format = AV_PIX_FMT_YUYV422; break;
        case MKTAG('U','Y','V','Y'):
        case MKTAG('H','D','Y','C'): info->pixel_format = AV_PIX_FMT_UYVY422; break;
        case MKTAG('H','2','6','4'):
        case MKTAG('h','2','6','4'):
        case MKTAG('A','V','C','1'):
        case MKTAG('X','2','6','4'): info->codec_id = AV_CODEC_ID_H264; break;
        case MKTAG('H','E','V','C'):
        case MKTAG('H','2','6','5'): info->codec_id = AV_CODEC_ID_HEVC; break;
        case MKTAG('M','J','P','G'): info->codec_id = AV_CODEC_ID_MJPEG; break;
        default: break;
        }
    }

    if (info->pixel_format == AV_PIX_FMT_NONE && info->codec_id == AV_CODEC_ID_NONE &&
        (compression == BI_RGB || compression == BI_BITFIELDS)) {
        if (bits == 16) info->pixel_format = AV_PIX_FMT_RGB555LE;
        else if (bits == 24) info->pixel_format = AV_PIX_FMT_BGR24;
        else if (bits == 32) info->pixel_format = AV_PIX_FMT_0RGB32;
    }
    if (info->pixel_format == AV_PIX_FMT_NONE && info->codec_id == AV_CODEC_ID_NONE) {
        const AVCodecTag* const tags[] = { avformat_get_riff_video_tags(), NULL };
        info->codec_id = av_codec_get_id(tags, compression);
    }
}

static WAVEFORMATEX* audio_header(AM_MEDIA_TYPE* type)
{
    if (!type || type->formattype != FORMAT_WaveFormatEx || !type->pbFormat || type->cbFormat < sizeof(WAVEFORMATEX)) return NULL;
    return reinterpret_cast<WAVEFORMATEX*>(type->pbFormat);
}

static AVSampleFormat pcm_sample_format(const WAVEFORMATEX* format)
{
    if (!format) return AV_SAMPLE_FMT_NONE;
    WORD tag = format->wFormatTag;
    if (tag == WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22) {
        const WAVEFORMATEXTENSIBLE* extensible = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        if (extensible->SubFormat.Data1 == 0x0003) tag = 0x0003;
        else if (extensible->SubFormat.Data1 == 0x0001) tag = WAVE_FORMAT_PCM;
    }
    if (tag == 0x0003) {
        if (format->wBitsPerSample == 32) return AV_SAMPLE_FMT_FLT;
        if (format->wBitsPerSample == 64) return AV_SAMPLE_FMT_DBL;
    }
    if (tag != WAVE_FORMAT_PCM) return AV_SAMPLE_FMT_NONE;
    if (format->wBitsPerSample == 8) return AV_SAMPLE_FMT_U8;
    if (format->wBitsPerSample == 16) return AV_SAMPLE_FMT_S16;
    if (format->wBitsPerSample == 32) return AV_SAMPLE_FMT_S32;
    return AV_SAMPLE_FMT_NONE;
}

static AVCodecID audio_codec_id(const WAVEFORMATEX* format)
{
    if (!format) return AV_CODEC_ID_NONE;
    switch (format->wFormatTag) {
    case 0x0055: return AV_CODEC_ID_MP3;
    case 0x0050: return AV_CODEC_ID_MP2;
    case 0x00ff:
    case 0x1600: return AV_CODEC_ID_AAC;
    default: return AV_CODEC_ID_NONE;
    }
}

static HRESULT select_format(IPin* pin, int index, AM_MEDIA_TYPE* selected)
{
    if (!pin || !selected) return E_POINTER;
    ZeroMemory(selected, sizeof(*selected));
    CComPtr<IAMStreamConfig> config;
    HRESULT hr = pin->QueryInterface(IID_PPV_ARGS(&config));
    if (FAILED(hr)) return hr;
    AM_MEDIA_TYPE* type = NULL;
    if (index < 0) hr = config->GetFormat(&type);
    else {
        int count = 0, size = 0;
        hr = config->GetNumberOfCapabilities(&count, &size);
        if (FAILED(hr) || index >= count || size <= 0) return E_INVALIDARG;
        std::vector<BYTE> caps((size_t)size);
        hr = config->GetStreamCaps(index, &type, caps.data());
        if (SUCCEEDED(hr)) hr = config->SetFormat(type);
    }
    if (FAILED(hr) || !type) {
        delete_media_type(type);
        return FAILED(hr) ? hr : E_FAIL;
    }
    hr = copy_media_type(selected, type);
    delete_media_type(type);
    return hr;
}

// This is a latency request in milliseconds.  DirectShow devices may ignore it.
static HRESULT set_audio_buffer_size(IPin* pin, int buffer_size_ms)
{
    if (!pin || buffer_size_ms <= 0) return S_FALSE;
    CComPtr<IAMStreamConfig> config;
    CComPtr<IAMBufferNegotiation> negotiation;
    AM_MEDIA_TYPE* type = NULL;
    HRESULT hr = pin->QueryInterface(IID_PPV_ARGS(&config));
    if (FAILED(hr)) return hr;
    hr = config->GetFormat(&type);
    if (FAILED(hr) || !type) return FAILED(hr) ? hr : E_FAIL;
    if (type->formattype != FORMAT_WaveFormatEx || !type->pbFormat ||
        type->cbFormat < sizeof(WAVEFORMATEX)) {
        delete_media_type(type);
        return S_FALSE;
    }
    const WAVEFORMATEX* format = reinterpret_cast<const WAVEFORMATEX*>(type->pbFormat);
    int64_t bytes = (int64_t)format->nAvgBytesPerSec * buffer_size_ms / 1000;
    delete_media_type(type);
    if (bytes <= 0 || bytes > LONG_MAX) return E_INVALIDARG;
    hr = pin->QueryInterface(IID_PPV_ARGS(&negotiation));
    if (FAILED(hr)) return hr;
    ALLOCATOR_PROPERTIES properties = { -1, (LONG)bytes, -1, -1 };
    return negotiation->SuggestAllocatorProperties(&properties);
}

static void show_properties(IUnknown* object)
{
    if (!object) return;
    CComPtr<ISpecifyPropertyPages> pages;
    if (FAILED(object->QueryInterface(IID_PPV_ARGS(&pages)))) return;
    CAUUID ids = {};
    if (FAILED(pages->GetPages(&ids))) return;
    IUnknown* objects[] = { object };
    OleCreatePropertyFrame(NULL, 0, 0, L"DirectShow", 1, objects,
        ids.cElems, ids.pElems, 0, 0, NULL);
    CoTaskMemFree(ids.pElems);
}

static void configure_analog(dshow_camera* camera, const dshow_camera_options* options)
{
    CComPtr<IAMCrossbar> crossbar;
    if (SUCCEEDED(camera->builder->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, camera->source,
        IID_PPV_ARGS(&crossbar)))) {
        if (options->crossbar_video_input >= 0 && options->crossbar_video_output >= 0)
            crossbar->Route(options->crossbar_video_output, options->crossbar_video_input);
        if (options->crossbar_audio_input >= 0 && options->crossbar_audio_output >= 0)
            crossbar->Route(options->crossbar_audio_output, options->crossbar_audio_input);
        if (options->show_crossbar_dialog) show_properties(crossbar);
    }
    if (options->show_tv_tuner_dialog) {
        CComPtr<IAMTVTuner> tuner;
        if (SUCCEEDED(camera->builder->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, camera->source,
            IID_PPV_ARGS(&tuner)))) show_properties(tuner);
    }
    if (options->show_tv_audio_dialog) {
        CComPtr<IAMTVAudio> audio;
        if (SUCCEEDED(camera->builder->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, camera->source,
            IID_PPV_ARGS(&audio)))) show_properties(audio);
    }
}

static REFERENCE_TIME get_sample_timestamp(DShowGrabber* grabber, IMediaSample* sample,
    bool use_device_timestamp, REFERENCE_TIME fallback_pts)
{
    if (!use_device_timestamp) return av_gettime_relative() * 10;
    REFERENCE_TIME start = 0, end = 0;
    if (grabber && sample && SUCCEEDED(sample->GetTime(&start, &end)))
        return start + grabber->m_starttime;
    return fallback_pts;
}

static void clear_packet_queue_locked(dshow_packet_queue* queue)
{
    while (!queue->packets.empty()) {
        AVPacket* packet = queue->packets.front();
        queue->packets.pop_front();
        av_packet_free(&packet);
    }
    queue->bytes = 0;
}

static int enqueue_packet(dshow_packet_queue* queue, const BYTE* data, int size,
    int64_t pts, int64_t duration, int key_frame)
{
    AVPacket* packet;
    if (!queue || !data || size <= 0) return -1;
    if ((size_t)size > queue->max_bytes) return -1;
    packet = av_packet_alloc();
    if (!packet || av_new_packet(packet, size) < 0) {
        av_packet_free(&packet);
        return -1;
    }
    memcpy(packet->data, data, size);
    packet->pts = packet->dts = pts;
    packet->duration = duration;
    if (key_frame) packet->flags |= AV_PKT_FLAG_KEY;

    EnterCriticalSection(&queue->mutex);
    if (queue->stopping) {
        LeaveCriticalSection(&queue->mutex);
        av_packet_free(&packet);
        return -1;
    }
    if (queue->packets.size() >= queue->max_packets ||
        queue->bytes + (size_t)size > queue->max_bytes) {
        clear_packet_queue_locked(queue);
        queue->reset_decoder = 1;
    }
    try {
        queue->packets.push_back(packet);
    }
    catch (...) {
        LeaveCriticalSection(&queue->mutex);
        av_packet_free(&packet);
        return -1;
    }
    queue->bytes += (size_t)size;
    LeaveCriticalSection(&queue->mutex);
    WakeConditionVariable(&queue->cond);
    return 0;
}

static AVPacket* dequeue_packet(dshow_packet_queue* queue, int* reset_decoder)
{
    AVPacket* packet = NULL;
    EnterCriticalSection(&queue->mutex);
    while (!queue->stopping && queue->packets.empty())
        SleepConditionVariableCS(&queue->cond, &queue->mutex, INFINITE);
    if (!queue->stopping && !queue->packets.empty()) {
        packet = queue->packets.front();
        queue->packets.pop_front();
        queue->bytes -= (size_t)packet->size;
        *reset_decoder = queue->reset_decoder;
        queue->reset_decoder = 0;
    }
    LeaveCriticalSection(&queue->mutex);
    return packet;
}

static void stop_packet_queue(dshow_packet_queue* queue)
{
    EnterCriticalSection(&queue->mutex);
    queue->stopping = 1;
    clear_packet_queue_locked(queue);
    LeaveCriticalSection(&queue->mutex);
    WakeAllConditionVariable(&queue->cond);
}

static void decode_packets(dshow_camera* camera, dshow_packet_queue* queue,
    AVCodecContext* decoder, dshow_camera_frame_callback callback)
{
    AVFrame* frame = av_frame_alloc();
    if (!frame) return;
    while (1) {
        int reset_decoder = 0;
        AVPacket* packet = dequeue_packet(queue, &reset_decoder);
        if (!packet) break;
        if (reset_decoder) avcodec_flush_buffers(decoder);
        int ret = avcodec_send_packet(decoder, packet);
        if (ret == AVERROR(EAGAIN)) {
            while (avcodec_receive_frame(decoder, frame) >= 0) {
                if (frame->pts == AV_NOPTS_VALUE) frame->pts = packet->pts;
                callback(camera->opaque, frame);
                av_frame_unref(frame);
            }
            ret = avcodec_send_packet(decoder, packet);
        }
        if (ret >= 0) {
            while (avcodec_receive_frame(decoder, frame) >= 0) {
                if (frame->pts == AV_NOPTS_VALUE) frame->pts = packet->pts;
                callback(camera->opaque, frame);
                av_frame_unref(frame);
            }
        }
        av_packet_free(&packet);
    }
    av_frame_free(&frame);
}

static DWORD WINAPI video_decoder_thread(void* opaque)
{
    dshow_camera* camera = static_cast<dshow_camera*>(opaque);
    decode_packets(camera, &camera->video_packets, camera->decoder, camera->callback);
    return 0;
}

static DWORD WINAPI audio_decoder_thread(void* opaque)
{
    dshow_camera* camera = static_cast<dshow_camera*>(opaque);
    decode_packets(camera, &camera->audio_packets, camera->audio_decoder, camera->audio_callback);
    return 0;
}

static void STDMETHODCALLTYPE sample_callback(void* opaque, IMediaSample* sample)
{
    dshow_camera* camera = static_cast<dshow_camera*>(opaque);
    if (!camera || !sample || !camera->callback) return;
    BYTE* data = NULL;
    long size = sample->GetActualDataLength();
    if (size <= 0 || FAILED(sample->GetPointer(&data)) || !data) return;
    REFERENCE_TIME start = get_sample_timestamp(camera->grabber, sample,
        camera->use_video_device_timestamp != 0, camera->next_pts);
    REFERENCE_TIME end = start + std::max<REFERENCE_TIME>(camera->frame_interval, 1);
    camera->next_pts = end;

    if (camera->decoder) {
        enqueue_packet(&camera->video_packets, data, size, start,
            end > start ? end - start : camera->frame_interval,
            sample->IsSyncPoint() == S_OK);
        return;
    }

    AVFrame* frame = av_frame_alloc();
    if (!frame) return;
    frame->format = camera->pixel_format;
    frame->width = camera->width;
    frame->height = camera->height;
    frame->pts = start;
    uint8_t* source_data[4] = {};
    int source_linesizes[4] = {};
    int source_size = av_image_get_buffer_size(camera->pixel_format,
        camera->width, camera->height, 1);
    if (source_size < 0 || size < source_size ||
        av_image_fill_arrays(source_data, source_linesizes, data,
            camera->pixel_format, camera->width, camera->height, 1) < 0 ||
        get_video_buffer(frame) < 0) {
        av_frame_free(&frame);
        return;
    }
    av_image_copy(frame->data, frame->linesize,
        (const uint8_t* const*)source_data, source_linesizes,
        camera->pixel_format, camera->width, camera->height);
    camera->callback(camera->opaque, frame);
    av_frame_free(&frame);
}

static void STDMETHODCALLTYPE audio_sample_callback(void* opaque, IMediaSample* sample)
{
    dshow_camera* camera = static_cast<dshow_camera*>(opaque);
    if (!camera || !sample || !camera->audio_callback) return;
    BYTE* data = NULL;
    long size = sample->GetActualDataLength();
    if (size <= 0 || FAILED(sample->GetPointer(&data)) || !data) return;
    WAVEFORMATEX* format = audio_header(&camera->audio_media_type);
    REFERENCE_TIME start = get_sample_timestamp(camera->audio_grabber, sample,
        camera->use_audio_device_timestamp != 0, camera->audio_next_pts);
    if (format && format->nAvgBytesPerSec > 0)
        camera->audio_next_pts = start + av_rescale(size, 10000000, format->nAvgBytesPerSec);
    if (camera->audio_decoder) {
        enqueue_packet(&camera->audio_packets, data, size, start, 0, 1);
        return;
    }

    AVSampleFormat sample_format = pcm_sample_format(format);
    int bytes_per_sample = av_get_bytes_per_sample(sample_format);
    if (!format || sample_format == AV_SAMPLE_FMT_NONE || bytes_per_sample <= 0 || format->nChannels == 0) return;
    int block_align = format->nBlockAlign > 0 ? format->nBlockAlign : bytes_per_sample * format->nChannels;
    int samples = size / block_align;
    if (samples <= 0) return;
    int audio_size = samples * block_align;
    AVFrame* frame = av_frame_alloc();
    if (!frame) return;
    frame->format = sample_format;
    frame->sample_rate = format->nSamplesPerSec;
    frame->nb_samples = samples;
    frame->pts = start;
    av_channel_layout_default(&frame->ch_layout, format->nChannels);
    if (av_frame_get_buffer(frame, 1) < 0 || !frame->data[0] || frame->linesize[0] < audio_size) {
        av_frame_free(&frame);
        return;
    }
    memcpy(frame->data[0], data, audio_size);
    camera->audio_next_pts = start + av_rescale_q(samples, AVRational{ 1, (int)format->nSamplesPerSec }, AVRational{ 1, 10000000 });
    camera->audio_callback(camera->opaque, frame);
    av_frame_free(&frame);
}

dshow_camera* dshow_camera_open(const dshow_camera_options* options,
    dshow_camera_frame_callback callback, dshow_camera_frame_callback audio_callback,
    void* opaque, char* error, size_t error_size)
{
    bool has_video = options && options->device_name_utf8 && options->device_name_utf8[0] && callback;
    bool has_audio = options && options->audio_device_name_utf8 && options->audio_device_name_utf8[0] &&
        options->audio_pin_id_utf8 && options->audio_pin_id_utf8[0] && audio_callback;
    if (!options || (!has_video && !has_audio)) {
        set_error(error, error_size, "invalid DirectShow options");
        return NULL;
    }
    dshow_camera* camera = new dshow_camera();
    ZeroMemory(&camera->media_type, sizeof(camera->media_type));
    camera->grabber = NULL;
    camera->audio_grabber = NULL;
    camera->decoder = NULL;
    camera->audio_decoder = NULL;
    camera->video_decoder_thread = NULL;
    camera->audio_decoder_thread = NULL;
    camera->video_packets.max_packets = 8;
    camera->video_packets.max_bytes = 64 * 1024 * 1024;
    camera->audio_packets.max_packets = 64;
    camera->audio_packets.max_bytes = 8 * 1024 * 1024;
    ZeroMemory(&camera->audio_media_type, sizeof(camera->audio_media_type));
    camera->pixel_format = AV_PIX_FMT_NONE;
    camera->width = camera->height = 0;
    camera->frame_interval = 333333;
    camera->next_pts = 0;
    camera->use_video_device_timestamp = options ? options->use_video_device_timestamp != 0 : 0;
    camera->use_audio_device_timestamp = options ? options->use_audio_device_timestamp != 0 : 0;
    camera->callback = callback;
    camera->audio_callback = audio_callback;
    camera->audio_next_pts = 0;
    camera->opaque = opaque;
    BITMAPINFOHEADER* bitmap = NULL;

    HRESULT hr = camera->graph.CoCreateInstance(CLSID_FilterGraph);
    if (FAILED(hr)) { set_error(error, error_size, "create filter graph failed", hr); goto failed; }
    hr = camera->builder.CoCreateInstance(CLSID_CaptureGraphBuilder2);
    if (FAILED(hr) || FAILED(hr = camera->builder->SetFiltergraph(camera->graph))) {
        set_error(error, error_size, "create capture graph builder failed", hr); goto failed;
    }
    if (has_video) {
        hr = bind_device(utf8_to_wide(options->device_name_utf8), CLSID_VideoInputDeviceCategory, &camera->source);
        if (FAILED(hr)) { set_error(error, error_size, "video device not found", hr); goto failed; }
        hr = camera->graph->AddFilter(camera->source, L"Video Capture");
        if (FAILED(hr)) { set_error(error, error_size, "add video device failed", hr); goto failed; }
        hr = find_output_pin(camera->source, utf8_to_wide(options->pin_id_utf8), &camera->source_pin);
        if (FAILED(hr)) { set_error(error, error_size, "capture output pin not found", hr); goto failed; }
        hr = select_format(camera->source_pin, options->format_index, &camera->media_type);
        if (FAILED(hr)) { set_error(error, error_size, "select capture format failed", hr); goto failed; }

        bitmap = video_header(&camera->media_type, &camera->frame_interval);
        if (!bitmap) { set_error(error, error_size, "selected format is not video"); goto failed; }
        camera->width = abs(bitmap->biWidth);
        camera->height = abs(bitmap->biHeight);
        dshow_video_format capture_format = {};
        dshow_set_pix_fmt_and_codec_id(&capture_format, camera->media_type.subtype,
            bitmap->biCompression, bitmap->biBitCount);
        camera->pixel_format = capture_format.pixel_format;
        if (camera->pixel_format == AV_PIX_FMT_NONE) {
            const AVCodec* codec = avcodec_find_decoder(capture_format.codec_id);
            if (!codec) { set_error(error, error_size, "FFmpeg decoder not found for capture format"); goto failed; }
            camera->decoder = avcodec_alloc_context3(codec);
            if (!camera->decoder) { set_error(error, error_size, "allocate FFmpeg decoder failed"); goto failed; }
            camera->decoder->width = camera->width;
            camera->decoder->height = camera->height;
            camera->decoder->codec_tag = bitmap->biCompression;
            camera->decoder->pkt_timebase = AVRational{ 1, 10000000 };
            if (avcodec_open2(camera->decoder, codec, NULL) < 0) {
                set_error(error, error_size, "open FFmpeg decoder failed"); goto failed;
            }
        }
        camera->grabber = new DShowGrabber(sample_callback, camera, &camera->media_type);
        hr = camera->graph->AddFilter(camera->grabber, L"OmniVCam Grabber");
        if (FAILED(hr)) { set_error(error, error_size, "add sample grabber failed", hr); goto failed; }
        hr = camera->builder->RenderStream(NULL, NULL, camera->source_pin, NULL, camera->grabber);
        if (FAILED(hr)) {
            set_error(error, error_size, "connect capture pin failed", hr); goto failed;
        }
    }

    if (has_audio) {
        if (has_video && options->audio_device_is_video &&
            (!options->audio_device_name_utf8 || !options->audio_device_name_utf8[0] ||
                strcmp(options->audio_device_name_utf8, options->device_name_utf8) == 0)) {
            camera->audio_source = camera->source;
        }
        else {
            const GUID& category = options->audio_device_is_video ? CLSID_VideoInputDeviceCategory : CLSID_AudioInputDeviceCategory;
            hr = bind_device(utf8_to_wide(options->audio_device_name_utf8), category, &camera->audio_source);
            if (FAILED(hr)) { set_error(error, error_size, "audio device not found", hr); goto failed; }
            hr = camera->graph->AddFilter(camera->audio_source, L"Audio Capture");
            if (FAILED(hr)) { set_error(error, error_size, "add audio device failed", hr); goto failed; }
        }
        hr = find_output_pin(camera->audio_source, utf8_to_wide(options->audio_pin_id_utf8), &camera->audio_source_pin);
        if (FAILED(hr)) { set_error(error, error_size, "audio output pin not found", hr); goto failed; }
        hr = select_format(camera->audio_source_pin, options->audio_format_index, &camera->audio_media_type);
        if (FAILED(hr)) { set_error(error, error_size, "select audio format failed", hr); goto failed; }
        // This is intentionally best effort, like FFmpeg's dshow audio_buffer_size.
        set_audio_buffer_size(camera->audio_source_pin, options->audio_buffer_size);
        WAVEFORMATEX* wave = audio_header(&camera->audio_media_type);
        if (!wave) { set_error(error, error_size, "selected format is not audio"); goto failed; }
        if (pcm_sample_format(wave) == AV_SAMPLE_FMT_NONE) {
            const AVCodec* codec = avcodec_find_decoder(audio_codec_id(wave));
            if (!codec) { set_error(error, error_size, "FFmpeg audio decoder not found"); goto failed; }
            camera->audio_decoder = avcodec_alloc_context3(codec);
            if (!camera->audio_decoder) { set_error(error, error_size, "allocate FFmpeg audio decoder failed"); goto failed; }
            camera->audio_decoder->sample_rate = wave->nSamplesPerSec;
            av_channel_layout_default(&camera->audio_decoder->ch_layout, wave->nChannels);
            camera->audio_decoder->pkt_timebase = AVRational{ 1, 10000000 };
            if (avcodec_open2(camera->audio_decoder, codec, NULL) < 0) {
                set_error(error, error_size, "open FFmpeg audio decoder failed"); goto failed;
            }
        }
        camera->audio_grabber = new DShowGrabber(audio_sample_callback, camera, &camera->audio_media_type);
        hr = camera->graph->AddFilter(camera->audio_grabber, L"OmniVCam Audio Grabber");
        if (FAILED(hr)) { set_error(error, error_size, "add audio grabber failed", hr); goto failed; }
        hr = camera->builder->RenderStream(NULL, NULL, camera->audio_source_pin, NULL, camera->audio_grabber);
        if (FAILED(hr)) {
            set_error(error, error_size, "connect audio pin failed", hr); goto failed;
        }
    }
    if (has_video) configure_analog(camera, options);
    hr = camera->graph.QueryInterface(&camera->control);
    if (FAILED(hr)) { set_error(error, error_size, "get media control failed", hr); goto failed; }
    return camera;

failed:
    dshow_camera_close(&camera);
    return NULL;
}

int dshow_camera_start(dshow_camera* camera)
{
    if (!camera || !camera->control) return -1;
    if (camera->decoder && open_thread(&camera->video_decoder_thread, video_decoder_thread, camera) < 0)
        return -1;
    if (camera->audio_decoder && open_thread(&camera->audio_decoder_thread, audio_decoder_thread, camera) < 0) {
        stop_packet_queue(&camera->video_packets);
        free_thread(&camera->video_decoder_thread);
        return -1;
    }
    if (FAILED(camera->control->Run())) {
        stop_packet_queue(&camera->video_packets);
        stop_packet_queue(&camera->audio_packets);
        free_thread(&camera->video_decoder_thread);
        free_thread(&camera->audio_decoder_thread);
        return -1;
    }
    return 0;
}

void dshow_camera_close(dshow_camera** camera_ptr)
{
    if (!camera_ptr || !*camera_ptr) return;
    dshow_camera* camera = *camera_ptr;
    if (camera->control) camera->control->Stop();
    stop_packet_queue(&camera->video_packets);
    stop_packet_queue(&camera->audio_packets);
    free_thread(&camera->video_decoder_thread);
    free_thread(&camera->audio_decoder_thread);
    camera->control.Release();
    camera->source_pin.Release();
    camera->audio_source_pin.Release();
    camera->audio_source.Release();
    camera->source.Release();
    camera->builder.Release();
    camera->graph.Release();
    if (camera->grabber) camera->grabber->Release();
    if (camera->audio_grabber) camera->audio_grabber->Release();
    camera->grabber = NULL;
    camera->audio_grabber = NULL;
    free_media_type(camera->media_type);
    free_media_type(camera->audio_media_type);
    avcodec_free_context(&camera->decoder);
    avcodec_free_context(&camera->audio_decoder);
    delete camera;
    *camera_ptr = NULL;
}

static bool append_record(std::string& output, const std::string& record)
{
    if (!output.empty()) output.push_back(';');
    output += record;
    return true;
}

static int copy_output(const std::string& value, char* output, size_t output_size)
{
    if (!output || output_size == 0 || value.size() + 1 > output_size) return -1;
    memcpy(output, value.c_str(), value.size() + 1);
    return (int)value.size();
}

int dshow_camera_list_devices(char* output, size_t output_size)
{
    HRESULT com_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    std::string records;
    const GUID* categories[] = { &CLSID_VideoInputDeviceCategory, &CLSID_AudioInputDeviceCategory };
    for (int device_type = 0; device_type < 2; ++device_type) {
        CComPtr<IEnumMoniker> devices;
        if (enumerate_devices(*categories[device_type], &devices) != S_OK) continue;
        CComPtr<IMoniker> moniker;
        while (devices->Next(1, &moniker, NULL) == S_OK) {
            CComPtr<IPropertyBag> bag;
            VARIANT name;
            LPOLESTR display_name = NULL;
            VariantInit(&name);
            if (SUCCEEDED(moniker->BindToStorage(NULL, NULL, IID_PPV_ARGS(&bag))) &&
                SUCCEEDED(bag->Read(L"FriendlyName", &name, NULL)) && name.vt == VT_BSTR &&
                SUCCEEDED(moniker->GetDisplayName(NULL, NULL, &display_name)) && display_name) {
                append_record(records, std::to_string(device_type) + "|" +
                    base64(wide_to_utf8(name.bstrVal)) + "|" + base64(wide_to_utf8(display_name)));
            }
            CoTaskMemFree(display_name);
            VariantClear(&name);
            moniker.Release();
        }
        devices.Release();
    }
    int result = copy_output(records, output, output_size);
    if (SUCCEEDED(com_hr)) CoUninitialize();
    return result;
}

static const char* video_format_name(DWORD compression, const GUID& subtype)
{
    if (compression == BI_RGB) return "RGB";
    if (compression == MKTAG('M','J','P','G') || subtype == MEDIASUBTYPE_MJPG) return "MJPEG";
    if (compression == MKTAG('H','2','6','4') || compression == MKTAG('h','2','6','4') || compression == MKTAG('A','V','C','1')) return "H.264";
    if (compression == MKTAG('H','E','V','C') || compression == MKTAG('H','2','6','5')) return "HEVC";
    if (compression == MKTAG('Y','U','Y','2')) return "YUY2";
    if (compression == MKTAG('U','Y','V','Y')) return "UYVY";
    if (compression == MKTAG('N','V','1','2')) return "NV12";
    if (compression == MKTAG('I','4','2','0') || compression == MKTAG('I','Y','U','V')) return "I420";
    return NULL;
}

static std::string format_description(AM_MEDIA_TYPE* type)
{
    REFERENCE_TIME interval = 0;
    BITMAPINFOHEADER* bitmap = video_header(type, &interval);
    if (!bitmap) {
        WAVEFORMATEX* wave = audio_header(type);
        if (!wave) return std::string("Unknown");
        const char* name = "Compressed";
        AVSampleFormat sample_format = pcm_sample_format(wave);
        if (sample_format != AV_SAMPLE_FMT_NONE) name = av_get_sample_fmt_name(sample_format);
        else if (audio_codec_id(wave) == AV_CODEC_ID_AAC) name = "AAC";
        else if (audio_codec_id(wave) == AV_CODEC_ID_MP3) name = "MP3";
        else if (audio_codec_id(wave) == AV_CODEC_ID_MP2) name = "MP2";
        char text[160];
        sprintf_s(text, "%lu Hz %u ch %u-bit %s", wave->nSamplesPerSec, wave->nChannels, wave->wBitsPerSample, name ? name : "PCM");
        return text;
    }
    dshow_video_format video_format = {};
    dshow_set_pix_fmt_and_codec_id(&video_format, type->subtype, bitmap->biCompression, bitmap->biBitCount);
    const char* name = NULL;
    if (video_format.pixel_format != AV_PIX_FMT_NONE) name = av_get_pix_fmt_name(video_format.pixel_format);
    else if (video_format.codec_id != AV_CODEC_ID_NONE) name = avcodec_get_name(video_format.codec_id);
    if (!name || strcmp(name, "unknown") == 0) name = video_format_name(bitmap->biCompression, type->subtype);
    char fallback[32];
    if (!name) {
        sprintf_s(fallback, "FourCC 0x%08lX", (unsigned long)bitmap->biCompression);
        name = fallback;
    }
    char text[160];
    double fps = interval > 0 ? 10000000.0 / interval : 0.0;
    sprintf_s(text, "%ldx%ld %.3f fps %s %u-bit", abs(bitmap->biWidth), abs(bitmap->biHeight), fps, name, bitmap->biBitCount);
    return text;
}

int dshow_camera_list_formats(const char* device_name_utf8, int device_type, char* output, size_t output_size)
{
    HRESULT com_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    std::string records;
    CComPtr<IBaseFilter> filter;
    const GUID& category = device_type == 0 ? CLSID_VideoInputDeviceCategory : CLSID_AudioInputDeviceCategory;
    if (FAILED(bind_device(utf8_to_wide(device_name_utf8), category, &filter))) {
        if (SUCCEEDED(com_hr)) CoUninitialize();
        return -1;
    }
    CComPtr<IEnumPins> pins;
    if (FAILED(filter->EnumPins(&pins))) {
        filter.Release();
        if (SUCCEEDED(com_hr)) CoUninitialize();
        return -1;
    }
    CComPtr<IPin> pin;
    while (pins->Next(1, &pin, NULL) == S_OK) {
        PIN_DIRECTION direction;
        CComPtr<IAMStreamConfig> config;
        LPWSTR pin_id = NULL;
        if (FAILED(pin->QueryDirection(&direction)) || direction != PINDIR_OUTPUT ||
            FAILED(pin->QueryInterface(IID_PPV_ARGS(&config))) || FAILED(pin->QueryId(&pin_id))) {
            CoTaskMemFree(pin_id); pin.Release(); continue;
        }
        int count = 0, size = 0;
        if (SUCCEEDED(config->GetNumberOfCapabilities(&count, &size)) && size > 0) {
            std::vector<BYTE> caps((size_t)size);
            for (int i = 0; i < count; ++i) {
                AM_MEDIA_TYPE* type = NULL;
                if (SUCCEEDED(config->GetStreamCaps(i, &type, caps.data())) && type &&
                    (type->majortype == MEDIATYPE_Video || type->majortype == MEDIATYPE_Audio)) {
                    const char* media = type->majortype == MEDIATYPE_Audio ? "audio" : "video";
                    append_record(records, std::string(media) + "|" + base64(wide_to_utf8(pin_id)) + "|" + std::to_string(i) + "|" +
                        base64(format_description(type)));
                }
                delete_media_type(type);
            }
        }
        CoTaskMemFree(pin_id);
        pin.Release();
    }
    int result = copy_output(records, output, output_size);
    pins.Release();
    filter.Release();
    if (SUCCEEDED(com_hr)) CoUninitialize();
    return result;
}

int dshow_camera_list_crossbar(const char* device_name_utf8, char* output, size_t output_size)
{
    HRESULT com_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    std::string records;
    CComPtr<IBaseFilter> filter;
    CComPtr<IGraphBuilder> graph;
    CComPtr<ICaptureGraphBuilder2> builder;
    if (FAILED(bind_device(utf8_to_wide(device_name_utf8), CLSID_VideoInputDeviceCategory, &filter)) ||
        FAILED(graph.CoCreateInstance(CLSID_FilterGraph)) ||
        FAILED(builder.CoCreateInstance(CLSID_CaptureGraphBuilder2)) ||
        FAILED(builder->SetFiltergraph(graph)) || FAILED(graph->AddFilter(filter, L"Video Capture"))) {
        if (SUCCEEDED(com_hr)) CoUninitialize();
        return -1;
    }
    CComPtr<IAMCrossbar> crossbar;
    if (SUCCEEDED(builder->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, filter, IID_PPV_ARGS(&crossbar)))) {
        long outputs = 0, inputs = 0;
        if (SUCCEEDED(crossbar->get_PinCounts(&outputs, &inputs))) {
            for (long out = 0; out < outputs; ++out) {
                long current = -1, related = -1, out_type = 0;
                crossbar->get_IsRoutedTo(out, &current);
                crossbar->get_CrossbarPinInfo(FALSE, out, &related, &out_type);
                for (long in = 0; in < inputs; ++in) {
                    long in_type = 0;
                    if (crossbar->CanRoute(out, in) == S_OK &&
                        SUCCEEDED(crossbar->get_CrossbarPinInfo(TRUE, in, &related, &in_type))) {
                        append_record(records, "route|" + std::to_string(out) + "|" + std::to_string(in) + "|" +
                            std::to_string(out_type) + "|" + std::to_string(in_type) + "|" + std::to_string(current));
                    }
                }
            }
        }
    }
    CComPtr<IAMTVTuner> tuner;
    CComPtr<IAMTVAudio> audio;
    append_record(records, std::string("features|") +
        (SUCCEEDED(builder->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, filter, IID_PPV_ARGS(&tuner))) ? "1" : "0") + "|" +
        (SUCCEEDED(builder->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, filter, IID_PPV_ARGS(&audio))) ? "1" : "0"));
    int result = copy_output(records, output, output_size);
    audio.Release();
    tuner.Release();
    crossbar.Release();
    builder.Release();
    graph.Release();
    filter.Release();
    if (SUCCEEDED(com_hr)) CoUninitialize();
    return result;
}
