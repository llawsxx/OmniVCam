#include "OmniVCam.h"
#include "OmniVideoPin.h"
#include "OmniAudioPin.h"
#include "dshow_grabber.h"
HINSTANCE g_hInstance = NULL;
long g_cObjects = 0;
long g_cLocks = 0;

extern "C" {
    static void PushVideoFrameHelper(void* obj, AVFrame* frame);
    static void PushAudioFrameHelper(void *obj, AVFrame* frame);
}

static void PushVideoFrameHelper(void* obj, AVFrame* frame) {
    OmniVCam* vcam = static_cast<OmniVCam*>(obj);
    int size = av_image_get_buffer_size((enum AVPixelFormat)frame->format, frame->width, frame->height, 1);
    REFERENCE_TIME customStartTime =  av_rescale_q(frame->pts, UNIVERSAL_TB, DSHOW_TB);
    vcam->PushVideoFrame(frame->data[0], size, customStartTime);
}


static void PushAudioFrameHelper(void* obj, AVFrame* frame) {
    OmniVCam* vcam = static_cast<OmniVCam*>(obj);
    int size = av_samples_get_buffer_size(NULL, frame->ch_layout.nb_channels,frame->nb_samples,(enum AVSampleFormat) frame->format, 1);
    REFERENCE_TIME customStartTime = av_rescale_q(frame->pts, UNIVERSAL_TB, DSHOW_TB);
    vcam->PushAudioSample(frame->data[0], size, customStartTime);
}


FormatManager formatManager;
// 简化的辅助函数
int GetVideoSupportedFormatCount() { return formatManager.GetVideoFormatCount(); }
int GetAudioSupportedFormatCount() { return formatManager.GetAudioFormatCount(); }
HRESULT GetVideoSupportedFormatByIndex(int index, FormatManager::VideoFormat* pFormat) {
    return formatManager.GetVideoFormatByIndex(index, pFormat);
}
HRESULT GetAudioSupportedFormatByIndex(int index, FormatManager::AudioFormat * pFormat) {
    return formatManager.GetAudioFormatByIndex(index, pFormat);
}

REFERENCE_TIME CalculateInterval(int numerator, int denominator) {
    return (REFERENCE_TIME)(10000000.0 * denominator / numerator);
}


AM_MEDIA_TYPE* CreateVideoMediaTypeWithFrameRate(int width, int height, REFERENCE_TIME frameInterval, const GUID& subtype) {
    AM_MEDIA_TYPE* pmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!pmt) return NULL;

    ZeroMemory(pmt, sizeof(AM_MEDIA_TYPE));
    pmt->majortype = MEDIATYPE_Video;
    pmt->subtype = subtype;
    pmt->bFixedSizeSamples = TRUE;
    pmt->bTemporalCompression = FALSE;
    pmt->formattype = FORMAT_VideoInfo;

    // 计算样本大小
    int bitsPerPixel = 24; // 默认RGB24
    if (subtype == MEDIASUBTYPE_RGB32) {
        bitsPerPixel = 32;
    }
    else if (subtype == MEDIASUBTYPE_YUY2) {
        bitsPerPixel = 16;
    }
    else if (subtype == MEDIASUBTYPE_IYUV || subtype == MEDIASUBTYPE_NV12 || subtype == MEDIASUBTYPE_YV12){
        bitsPerPixel = 12;
    }

    long sampleSize = width * height * bitsPerPixel / 8;
    pmt->lSampleSize = sampleSize;

    // 创建VIDEOINFOHEADER
    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!pvi) {
        CoTaskMemFree(pmt);
        return NULL;
    }

    ZeroMemory(pvi, sizeof(VIDEOINFOHEADER));

    // 设置视频信息
    pvi->AvgTimePerFrame = frameInterval;
    pvi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pvi->bmiHeader.biWidth = width;
    pvi->bmiHeader.biHeight = height;
    pvi->bmiHeader.biPlanes = 1;
    pvi->bmiHeader.biBitCount = bitsPerPixel;
    pvi->bmiHeader.biSizeImage = sampleSize;

    // 设置压缩格式
    if (subtype == MEDIASUBTYPE_RGB24 || subtype == MEDIASUBTYPE_RGB32) {
        pvi->bmiHeader.biCompression = BI_RGB;
    }
    else if (subtype == MEDIASUBTYPE_YUY2) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('Y', 'U', 'Y', '2');
    }
    else if (subtype == MEDIASUBTYPE_IYUV) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('I', 'Y', 'U', 'V');
    }
    else if (subtype == MEDIASUBTYPE_YV12) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('N', 'V', '1', '2');
        pvi->bmiHeader.biPlanes = 3;
    }
    else if (subtype == MEDIASUBTYPE_NV12) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('N', 'V', '1', '2');
        pvi->bmiHeader.biPlanes = 2;
    }

    pvi->bmiHeader.biClrUsed = 0;
    pvi->bmiHeader.biClrImportant = 0;

    // 设置矩形
    pvi->rcSource.left = 0;
    pvi->rcSource.top = 0;
    pvi->rcSource.right = width;
    pvi->rcSource.bottom = height;
    pvi->rcTarget = pvi->rcSource;

    pmt->cbFormat = sizeof(VIDEOINFOHEADER);
    pmt->pbFormat = (BYTE*)pvi;

    return pmt;
}

AM_MEDIA_TYPE* CreateAudioMediaType(int samplesPerSec, int bitsPerSample, int channels) {
    AM_MEDIA_TYPE* pmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!pmt) return NULL;

    ZeroMemory(pmt, sizeof(AM_MEDIA_TYPE));
    pmt->majortype = MEDIATYPE_Audio;
    pmt->subtype = MEDIASUBTYPE_PCM;
    pmt->bFixedSizeSamples = TRUE;
    pmt->bTemporalCompression = FALSE;

    WAVEFORMATEX* pwfx = (WAVEFORMATEX*)CoTaskMemAlloc(sizeof(WAVEFORMATEX));
    if (pwfx) {
        ZeroMemory(pwfx, sizeof(WAVEFORMATEX));
        pwfx->wFormatTag = WAVE_FORMAT_PCM;
        pwfx->nChannels = channels;
        pwfx->nSamplesPerSec = samplesPerSec;
        pwfx->wBitsPerSample = bitsPerSample;
        pwfx->nBlockAlign = channels * bitsPerSample / 8;
        pwfx->nAvgBytesPerSec = samplesPerSec * pwfx->nBlockAlign;
        pwfx->cbSize = 0;

        pmt->formattype = FORMAT_WaveFormatEx;
        pmt->cbFormat = sizeof(WAVEFORMATEX);
        pmt->pbFormat = (BYTE*)pwfx;
        pmt->lSampleSize = pwfx->nBlockAlign;
    }

    return pmt;
}


HRESULT CopyMediaType(AM_MEDIA_TYPE* pmtDest, AM_MEDIA_TYPE* pmtSrc) {
    if (!pmtDest || !pmtSrc) return E_POINTER;

    // 复制基本字段
    pmtDest->majortype = pmtSrc->majortype;
    pmtDest->subtype = pmtSrc->subtype;
    pmtDest->bFixedSizeSamples = pmtSrc->bFixedSizeSamples;
    pmtDest->bTemporalCompression = pmtSrc->bTemporalCompression;
    pmtDest->lSampleSize = pmtSrc->lSampleSize;
    pmtDest->formattype = pmtSrc->formattype;

    // 处理 pUnk
    pmtDest->pUnk = pmtSrc->pUnk;
    if (pmtDest->pUnk) {
        pmtDest->pUnk->AddRef();
    }

    // 复制格式数据
    pmtDest->cbFormat = 0;
    pmtDest->pbFormat = NULL;

    if (pmtSrc->cbFormat > 0 && pmtSrc->pbFormat) {
        pmtDest->cbFormat = pmtSrc->cbFormat;
        pmtDest->pbFormat = (BYTE*)CoTaskMemAlloc(pmtSrc->cbFormat);
        if (!pmtDest->pbFormat) {
            pmtDest->cbFormat = 0;
            if (pmtDest->pUnk) {
                pmtDest->pUnk->Release();
                pmtDest->pUnk = NULL;
            }
            return E_OUTOFMEMORY;
        }
        memcpy(pmtDest->pbFormat, pmtSrc->pbFormat, pmtSrc->cbFormat);
    }

    return S_OK;
}

AM_MEDIA_TYPE* _CreateMediaType(const AM_MEDIA_TYPE* pSrc) {
    if (!pSrc) return NULL;

    AM_MEDIA_TYPE* pmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!pmt) return NULL;

    CopyMediaType(pmt, pSrc);
    return pmt;
}


class OmniPinEnum : public IEnumPins, public DShowBase {
public:
    OmniPinEnum(OmniVCam* pFilter) : m_refCount(1), m_pFilter(pFilter), m_index(0) {
        if (m_pFilter) ((IBaseFilter*)m_pFilter)->AddRef();
    }

    ~OmniPinEnum() {
        if (m_pFilter) {
            ((IBaseFilter*)m_pFilter)->Release();
            m_pFilter = NULL;
        }
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) {
        if (riid == IID_IEnumPins) {
            *ppv = static_cast<IEnumPins*>(this);
            AddRef();
            return S_OK;
        }
        else if (riid == IID_IUnknown) {
            *ppv = static_cast<IUnknown*>(this);
        }
        else {
            *ppv = NULL;
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() { 
        long refCount = InterlockedIncrement(&m_refCount);
        DEBUG_LOG_REF()
        return refCount;
    }
    STDMETHODIMP_(ULONG) Release() {
        ULONG refCount = InterlockedDecrement(&m_refCount);
        DEBUG_LOG_REF()

        if (refCount == 0) {
            delete this;
        }
        return refCount;
    }

    // IEnumPins
    STDMETHODIMP Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched) {
        if (!ppPins) return E_POINTER;

        ULONG fetched = 0;
        while (fetched < cPins && m_index < 2) { // 我们有两个pin: 视频和音频
            HRESULT hr = ((IBaseFilter*)m_pFilter)->FindPin(m_index == 0 ? L"Video" : L"Audio", &ppPins[fetched]);
            if (SUCCEEDED(hr)) {
                fetched++;
            }
            m_index++;
        }

        if (pcFetched) *pcFetched = fetched;
        return (fetched == cPins) ? S_OK : S_FALSE;
    }

    STDMETHODIMP Skip(ULONG cPins) {
        m_index += cPins;
        if (m_index > 2) m_index = 2;
        return (m_index < 2) ? S_OK : S_FALSE;
    }

    STDMETHODIMP Reset() {
        m_index = 0;
        return S_OK;
    }

    STDMETHODIMP Clone(IEnumPins** ppEnum) {
        if (!ppEnum) return E_POINTER;

        OmniPinEnum* pEnum = new OmniPinEnum(m_pFilter);
        if (!pEnum) return E_OUTOFMEMORY;

        pEnum->m_index = m_index;
        *ppEnum = pEnum;
        return S_OK;
    }

private:
    volatile long m_refCount;
    OmniVCam* m_pFilter;
    ULONG m_index;
};



OmniMediaTypeEnum::OmniMediaTypeEnum(AM_MEDIA_TYPE** ppMediaTypes, ULONG count)
    : m_refCount(1), m_ppMediaTypes(ppMediaTypes), m_count(count), m_index(0) {
}

OmniMediaTypeEnum::~OmniMediaTypeEnum() {
    if (m_ppMediaTypes) {
        for (ULONG i = 0; i < m_count; i++) {
            if (m_ppMediaTypes[i]) {
                if (m_ppMediaTypes[i]->pbFormat) {
                    CoTaskMemFree(m_ppMediaTypes[i]->pbFormat);
                }
                CoTaskMemFree(m_ppMediaTypes[i]);
                m_ppMediaTypes[i] = NULL;
            }
        }
        CoTaskMemFree(m_ppMediaTypes);
    }
}

// IUnknown
STDMETHODIMP OmniMediaTypeEnum::QueryInterface(REFIID riid, void** ppv) {
    if (riid == IID_IUnknown || riid == IID_IEnumMediaTypes) {
        *ppv = static_cast<IEnumMediaTypes*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = NULL;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) OmniMediaTypeEnum::AddRef() { 
    return InterlockedIncrement(&m_refCount); 
}
STDMETHODIMP_(ULONG) OmniMediaTypeEnum::Release() {
    ULONG refCount = InterlockedDecrement(&m_refCount);
    DEBUG_LOG_REF()
    if (refCount == 0) delete this;
    return refCount;
}

// IEnumMediaTypes
STDMETHODIMP OmniMediaTypeEnum::Next(ULONG cMediaTypes, AM_MEDIA_TYPE** ppMediaTypes, ULONG* pcFetched) {
    if (!ppMediaTypes) return E_POINTER;

    ULONG fetched = 0;
    while (fetched < cMediaTypes && m_index < m_count) {
        ppMediaTypes[fetched] = _CreateMediaType(m_ppMediaTypes[m_index]);
        if (!ppMediaTypes[fetched]) break;
        fetched++;
        m_index++;
    }

    if (pcFetched) *pcFetched = fetched;
    return (fetched == cMediaTypes) ? S_OK : S_FALSE;
}

STDMETHODIMP OmniMediaTypeEnum::Skip(ULONG cMediaTypes) {
    m_index += cMediaTypes;
    if (m_index > m_count) m_index = m_count;
    return (m_index < m_count) ? S_OK : S_FALSE;
}

STDMETHODIMP OmniMediaTypeEnum::Reset() {
    m_index = 0;
    return S_OK;
}

STDMETHODIMP OmniMediaTypeEnum::Clone(IEnumMediaTypes** ppEnum) {
    if (!ppEnum) return E_POINTER;

    // 克隆需要复制所有媒体类型
    AM_MEDIA_TYPE** ppNewTypes = (AM_MEDIA_TYPE**)CoTaskMemAlloc(m_count * sizeof(AM_MEDIA_TYPE*));
    if (!ppNewTypes) return E_OUTOFMEMORY;

    for (ULONG i = 0; i < m_count; i++) {
        ppNewTypes[i] = _CreateMediaType(m_ppMediaTypes[i]);
        if (!ppNewTypes[i]) {
            // 清理已分配的内存
            for (ULONG j = 0; j < i; j++) {
                if (ppNewTypes[j]->pbFormat) CoTaskMemFree(ppNewTypes[j]->pbFormat);
                CoTaskMemFree(ppNewTypes[j]);
            }
            CoTaskMemFree(ppNewTypes);
            return E_OUTOFMEMORY;
        }
    }

    OmniMediaTypeEnum* pEnum = new OmniMediaTypeEnum(ppNewTypes, m_count);
    pEnum->m_index = m_index;
    *ppEnum = pEnum;
    return S_OK;
}


OmniVCam::OmniVCam() : m_refCount(1), m_state(State_Stopped), m_clock(NULL), m_graph(NULL), m_renderThread(NULL), m_renderOpts(NULL) {
    InitializeCriticalSection(&m_cs);
    m_videoPin = new OmniVideoPin(this);
    m_audioPin = new OmniAudioPin(this);
    m_renderOpts = (inout_options *)av_mallocz(sizeof(inout_options));
    m_noProcess[0] = CreateEvent(NULL, TRUE, TRUE, NULL);
    m_noProcess[1] = CreateEvent(NULL, TRUE, TRUE, NULL);
    InterlockedIncrement(&g_cObjects);
}

OmniVCam::~OmniVCam() {
    if (m_renderThread && m_videoPin && m_audioPin && m_renderOpts) {
        m_renderOpts->send_exit = 1;
        m_videoPin->Stop();
        m_audioPin->Stop();
        WaitForMultipleObjects(2, m_noProcess, TRUE, INFINITE);
        free_thread(&m_renderThread);
    }

    if (m_videoPin) {
        m_videoPin->Release();
        m_videoPin = NULL;
    }
    if (m_audioPin) {
        m_audioPin->Release();
        m_audioPin = NULL;
    }
    if (m_clock) {
        m_clock->Release();
        m_clock = NULL;
    }

    if (m_renderOpts) {
        av_free(m_renderOpts);
        m_renderOpts = NULL;
    }
    if (m_noProcess[0]) {
        CloseHandle(m_noProcess[0]);
        m_noProcess[0] = NULL;
    }
    if (m_noProcess[1]) {
        CloseHandle(m_noProcess[1]);
        m_noProcess[1] = NULL;
    }

    DeleteCriticalSection(&m_cs);
    InterlockedDecrement(&g_cObjects);
}
// IUnknown
STDMETHODIMP OmniVCam::QueryInterface(REFIID riid, void** ppv) {
    if (riid == IID_IUnknown) {
        *ppv = static_cast<IUnknown*>(static_cast<IBaseFilter*>(this));
    }
    else if (riid == IID_IBaseFilter) {
        *ppv = static_cast<IBaseFilter*>(this);
    }
    else if (riid == IID_IPersist) {
        *ppv = static_cast<IPersist*>(this);
    }
    else if (riid == IID_IMediaFilter) {
        *ppv = static_cast<IMediaFilter*>(this);
    }
    else {
        *ppv = NULL;
        return E_NOINTERFACE;
    }
    AddRef();
    DEBUG_LOG_REF()
    return S_OK;
}

STDMETHODIMP_(ULONG) OmniVCam::AddRef() {
    long refCount = InterlockedIncrement(&m_refCount);
    DEBUG_LOG_REF()
    return refCount;
}

STDMETHODIMP_(ULONG) OmniVCam::Release() {
    ULONG refCount = InterlockedDecrement(&m_refCount);
    DEBUG_LOG_REF()
    if (refCount == 0) {
        delete this;
    }
    return refCount;
}

// IPersist
STDMETHODIMP OmniVCam::GetClassID(CLSID* pClsID) {
    *pClsID = CLSID_OmniVCam;
    return S_OK;
}

// IMediaFilter
STDMETHODIMP OmniVCam::GetState(DWORD dwMSecs, FILTER_STATE* State) {
    CLock lck(m_cs);
    *State = m_state;
    if (m_state == State_Paused)
        return VFW_S_CANT_CUE;
    return S_OK;
}

STDMETHODIMP OmniVCam::SetSyncSource(IReferenceClock* pClock) {
    CLock lck(m_cs);
    if (m_clock) m_clock->Release();
    m_clock = pClock;
    if (m_clock) m_clock->AddRef();
    return S_OK;
}

STDMETHODIMP OmniVCam::GetSyncSource(IReferenceClock** pClock) {
    if (!pClock) return E_POINTER;
    CLock lck(m_cs);
    *pClock = m_clock;
    if (m_clock) m_clock->AddRef();
    return S_OK;
}

STDMETHODIMP OmniVCam::Stop() {
    CLock lck(m_cs);
    m_state = State_Stopped;// This method always sets the filter's state to State_Stopped, even if the method returns an error code.

    if (m_videoPin) m_videoPin->Stop();
    if (m_audioPin) m_audioPin->Stop();
    if (WaitForMultipleObjects(2, m_noProcess, TRUE, 500) != WAIT_OBJECT_0) {
        return S_FALSE;
    }

    if (m_renderThread && m_renderOpts->send_exit == 0) {
        m_renderOpts->send_exit = 1;
        m_videoPin->Stop();
        m_audioPin->Stop();
        //一定要执行完Receive再释放
        WaitForMultipleObjects(2, m_noProcess, TRUE, INFINITE);
        free_thread(&m_renderThread);
    }
    m_videoPin->Active(FALSE);
    m_audioPin->Active(FALSE);
    return S_OK;
}

STDMETHODIMP OmniVCam::Pause() {
    CLock lck(m_cs);
    m_state = State_Paused;
    if (m_videoPin) m_videoPin->Pause();
    if (m_audioPin) m_audioPin->Pause();
    //被调用Pause时Receive可能处于阻塞状态（阻塞Receive是下游筛选器暂停输出的手段？？？）
    WaitForMultipleObjects(2, m_noProcess, TRUE, 500);
    return S_OK;
}

STDMETHODIMP OmniVCam::Run(REFERENCE_TIME tStart) {
    HRESULT hr = S_OK;
    CLock lck(m_cs);
    if (m_state == State_Running) {
        return hr;
    }
    else if (m_state == State_Paused && m_renderThread) {
        m_state = State_Running;
        m_videoPin->Continue();
        m_audioPin->Continue();
        return hr;
    }

    if (m_renderThread && m_renderOpts->send_exit == 0) {
        m_renderOpts->send_exit = 1;
        m_videoPin->Stop();
        m_audioPin->Stop();
        WaitForMultipleObjects(2, m_noProcess, TRUE, INFINITE);
        free_thread(&m_renderThread);
    }

    m_state = State_Running;
    m_videoPin->Active(TRUE);
    m_audioPin->Active(TRUE);

    if (m_videoPin) m_videoPin->Run(tStart);
    if (m_audioPin) m_audioPin->Run(tStart);


    if (m_videoPin && m_audioPin) {
        m_renderOpts->video_out_width = m_videoPin->m_currentWidth;
        m_renderOpts->video_out_height = m_videoPin->m_currentHeight;
        m_renderOpts->video_out_format = m_videoPin->m_currentFormat;
        m_renderOpts->video_out_fps = { m_videoPin->m_currentFpsNumerator ,m_videoPin->m_currentFpsDenominator };
        m_renderOpts->audio_out_channels = m_audioPin->m_audioChannels;
        m_renderOpts->audio_out_sample_rate = m_audioPin->m_audioSampleRate;
        m_renderOpts->audio_out_format = m_audioPin->m_audioFormat;
        m_renderOpts->audio_out_nb_samples = 1024;
        m_renderOpts->video_callback = PushVideoFrameHelper;
        m_renderOpts->audio_callback = PushAudioFrameHelper;
        m_renderOpts->callback_private = this;
        m_renderOpts->send_exit = 0;
        if (open_thread(&m_renderThread, main_thread, m_renderOpts) < 0) {
            hr = S_FALSE;
        }
    }
    return hr;
}


STDMETHODIMP OmniVCam::EnumPins(IEnumPins** ppEnum) {
    if (!ppEnum) return E_POINTER;

    *ppEnum = new OmniPinEnum(this);
    if (!*ppEnum) return E_OUTOFMEMORY;

    return S_OK;
}

STDMETHODIMP OmniVCam::FindPin(LPCWSTR Id, IPin** ppPin) {
    if (!Id || !ppPin) return E_POINTER;
    if (wcscmp(Id, L"Video") == 0) {
        return m_videoPin->QueryInterface(IID_IPin, (void**)ppPin);
    }
    else if (wcscmp(Id, L"Audio") == 0) {
        return m_audioPin->QueryInterface(IID_IPin, (void**)ppPin);
    }

    return VFW_E_NOT_FOUND;
}

STDMETHODIMP OmniVCam::QueryFilterInfo(FILTER_INFO* pInfo) {
    if (!pInfo) return E_POINTER;
    wcscpy_s(pInfo->achName, L"OmniVCam");
    CLock lck(m_cs);
    pInfo->pGraph = m_graph;
    if (m_graph) m_graph->AddRef();
    return S_OK;
}

STDMETHODIMP OmniVCam::JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName) {
    CLock lck(m_cs);
    m_graph = pGraph;
    return S_OK;
}

STDMETHODIMP OmniVCam::QueryVendorInfo(LPWSTR* pVendorInfo) {
    *pVendorInfo = NULL;
    return E_NOTIMPL;
}

STDMETHODIMP OmniVCam::NonDelegatingQueryInterface(
    REFIID riid,
    void **ppvObject)
{
    HRESULT hr = S_OK;
    if (!ppvObject)return E_POINTER;
    *ppvObject = NULL;

    if (riid == IID_IAMStreamConfig || riid == IID_IKsPropertySet) {
        return m_videoPin->QueryInterface(riid, ppvObject);
    }

    if (riid == IID_IUnknown) {
        *ppvObject = static_cast<INonDelegatingUnknown*>(this);
    }
    else if (riid == IID_IBaseFilter) {
        *ppvObject = static_cast<IBaseFilter*>(this);
    }
    else if (riid == IID_IPersist) {
        *ppvObject = (IPersist*)this;
    }
    else if (riid == IID_IMediaFilter) {
        *ppvObject = (IMediaFilter*)this;
    }
    else {
        *ppvObject = NULL;
        hr = E_NOINTERFACE;
    }

    if (hr == S_OK) {
        AddRef();
    }
    return hr;
}
ULONG STDMETHODCALLTYPE OmniVCam::NonDelegatingAddRef(void)
{
    return InterlockedIncrement(&m_refCount);
}
ULONG STDMETHODCALLTYPE OmniVCam::NonDelegatingRelease(void)
{
    ULONG refCount = InterlockedDecrement(&m_refCount);
    DEBUG_LOG_REF()
    if (refCount == 0) {
        delete this;
    }
    return refCount;
}

// Custom methods
HRESULT OmniVCam::SetVideoFormat(const OmniVideoFormat& format) {
    return m_videoPin->SetCustomFormat(format);
}

HRESULT OmniVCam::SetAudioFormat(const OmniAudioFormat& format) {
    return m_audioPin->SetCustomFormat(format);
}

HRESULT OmniVCam::PushVideoFrame(BYTE* data, long size, REFERENCE_TIME customStartTime) {
    return m_videoPin->PushFrame(data, size, customStartTime);
}

HRESULT OmniVCam::PushAudioSample(BYTE* data, long size, REFERENCE_TIME customStartTime) {
    return m_audioPin->PushSample(data, size, customStartTime);
}






// FormatManager 实现
FormatManager::FormatManager() {
    InitializeVideoFormats();
    InitializeAudioFormats();
}

void FormatManager::InitializeVideoFormats() {
    // 定义支持的像素格式
    struct PixelFormat {
        GUID subtype;
        const wchar_t* formatName;
    };

    static const PixelFormat pixelFormats[] = {
        { MEDIASUBTYPE_RGB24, L"RGB24" },
        { MEDIASUBTYPE_IYUV, L"IYUV" },
        { MEDIASUBTYPE_YUY2, L"YUY2" },
        { MEDIASUBTYPE_NV12, L"NV12" },
        { MEDIASUBTYPE_RGB32, L"RGB32" },
    };

    // 定义所有支持的分辨率
    struct Resolution {
        int width;
        int height;
    };

    static const Resolution resolutions[] = {
        {1920, 1080},
        {1080, 1920},
        {1280, 720},
        {720, 1280},
        {2560, 1440},
        {1440, 2560},
        {3840, 2160},
        {2160, 3840},
        //{640, 360},
        //{640, 480},
        //{720, 480},
        //{720, 576}
    };

    // 定义所有支持的帧率
    struct FrameRate {
        int numerator;
        int denominator;
        const wchar_t* fpsName;
    };

    static const FrameRate frameRates[] = {
        {30000, 1001, L"29.97fps"},
        {30, 1, L"30fps"},
        {50, 1, L"50fps"},
        {60000, 1001, L"59.94fps"},
        {60, 1, L"60fps"},
        {24000, 1001, L"23.976fps"},
        {24, 1, L"24fps"},
        {25, 1, L"25fps"},
    };

    // 生成所有视频格式组合
    for (const auto& pixelFormat : pixelFormats) {
        for (const auto& res : resolutions) {
            for (const auto& fps : frameRates) {
                VideoFormat format;
                format.width = res.width;
                format.height = res.height;
                format.fpsNum = fps.numerator;
                format.fpsDen = fps.denominator;
                format.subtype = pixelFormat.subtype;
                m_videoFormats.push_back(format);
            }
        }
    }
}

void FormatManager::InitializeAudioFormats() {
    // 定义音频格式
    m_audioFormats = {
        {48000, 16, 2, L"48kHz 16-bit Stereo"},
        {44100, 16, 2, L"44.1kHz 16-bit Stereo"},
        {48000, 16, 1, L"48kHz 16-bit Mono"},
        {44100, 16, 1, L"44.1kHz 16-bit Mono"},
    };
}

HRESULT FormatManager::GetVideoFormatByIndex(int index, VideoFormat* pFormat) const {
    if (!pFormat) return E_POINTER;
    if (index < 0 || index >= (int)m_videoFormats.size()) return E_INVALIDARG;

    *pFormat = m_videoFormats[index];
    return S_OK;
}

HRESULT FormatManager::GetAudioFormatByIndex(int index, AudioFormat* pFormat) const {
    if (!pFormat) return E_POINTER;
    if (index < 0 || index >= (int)m_audioFormats.size()) return E_INVALIDARG;

    *pFormat = m_audioFormats[index];
    return S_OK;
}

AM_MEDIA_TYPE* FormatManager::CreateVideoMediaType(const VideoFormat& format) const {
    REFERENCE_TIME frameInterval = (REFERENCE_TIME)(10000000.0 * format.fpsDen / format.fpsNum);
    return CreateVideoMediaTypeWithFrameRate(format.width, format.height, frameInterval, format.subtype);
}

AM_MEDIA_TYPE* FormatManager::CreateAudioMediaType(const AudioFormat& format) const {
    return ::CreateAudioMediaType(format.samplesPerSec, format.bitsPerSample, format.channels);
}


class OmniVCamClassFactory : public IClassFactory, public DShowBase {
public:
    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = NULL;
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() { return 2; }
    STDMETHODIMP_(ULONG) Release() { return 1; }

    // IClassFactory
    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) {
        if (pUnkOuter != NULL) return CLASS_E_NOAGGREGATION;
        //要注册一下，不然用不了dshow
        avdevice_register_all();
        OmniVCam* pFilter = new OmniVCam();
        if (!pFilter) return E_OUTOFMEMORY;

        HRESULT hr = pFilter->QueryInterface(riid, ppv);
        pFilter->Release();

        return hr;
    }

    STDMETHODIMP LockServer(BOOL fLock) {
        if (fLock) InterlockedIncrement(&g_cLocks);
        else InterlockedDecrement(&g_cLocks);
        return S_OK;
    }
};




// DLL exports
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (rclsid != CLSID_OmniVCam) return CLASS_E_CLASSNOTAVAILABLE;

    static OmniVCamClassFactory factory;
    return factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() {
    return (g_cObjects == 0 && g_cLocks == 0) ? S_OK : S_FALSE;
}


HRESULT RegisterFilterClass() {
    HRESULT hr = S_OK;
    HKEY hKey = NULL;
    wchar_t clsid[39];
    wchar_t keyPath[256];

    StringFromGUID2(CLSID_OmniVCam, clsid, 39);

    // 注册 CLSID
    swprintf_s(keyPath, L"CLSID\\%s", clsid);
    hr = RegCreateKeyEx(HKEY_CLASSES_ROOT, (LPWSTR)keyPath, 0, NULL, 0, KEY_WRITE, NULL, &hKey, NULL);
    if (SUCCEEDED(hr)) {
        RegSetValueEx(hKey, NULL, 0, REG_SZ, (BYTE*)L"OmniVCam Virtual Camera", 48);
        RegCloseKey(hKey);
    }

    // 注册 InprocServer32
    swprintf_s(keyPath, L"CLSID\\%s\\InprocServer32", clsid);
    hr = RegCreateKeyEx(HKEY_CLASSES_ROOT, (LPWSTR)keyPath, 0, NULL, 0, KEY_WRITE, NULL, &hKey, NULL);
    if (SUCCEEDED(hr)) {
        wchar_t modulePath[MAX_PATH];
        GetModuleFileName(g_hInstance, (LPWSTR)modulePath, MAX_PATH);
        RegSetValueEx(hKey, NULL, 0, REG_SZ, (BYTE*)modulePath, (wcslen(modulePath) + 1) * sizeof(wchar_t));

        // 设置线程模型
        RegSetValueEx(hKey, (LPWSTR)L"ThreadingModel", 0, REG_SZ, (BYTE*)L"Both", 10);
        RegCloseKey(hKey);
    }

    return hr;
}

void UnregisterFilterClass() {
    wchar_t clsid[39];
    wchar_t keyPath[256];

    StringFromGUID2(CLSID_OmniVCam, clsid, 39);

    swprintf_s(keyPath, L"CLSID\\%s\\InprocServer32", clsid);
    RegDeleteKey(HKEY_CLASSES_ROOT, (LPWSTR)keyPath);

    swprintf_s(keyPath, L"CLSID\\%s", clsid);
    RegDeleteKey(HKEY_CLASSES_ROOT, (LPWSTR)keyPath);
}


// 在 RegisterVideoCaptureDevice 中更新支持的媒体类型
HRESULT RegisterVideoCaptureDevice() {
    HRESULT hr = S_OK;
    IFilterMapper2* pFilterMapper = NULL;

    hr = CoCreateInstance(CLSID_FilterMapper2, NULL, CLSCTX_INPROC_SERVER,
        IID_IFilterMapper2, (void**)&pFilterMapper);
    if (FAILED(hr)) return hr;

    REGFILTER2 rf2;
    ZeroMemory(&rf2, sizeof(rf2));
    rf2.dwVersion = 2;
    rf2.dwMerit = MERIT_DO_NOT_USE;
    rf2.cPins2 = 1;

    // 设置视频输出Pin，支持多种媒体类型
    REGFILTERPINS2 rp2[2];
    ZeroMemory(rp2, sizeof(rp2));

    // 视频Pin - 支持多种视频格式
    rp2[0].dwFlags = REG_PINFLAG_B_OUTPUT;
    rp2[0].cInstances = 1;
    rp2[0].nMediaTypes = 5; // 支持4种视频格式

    REGPINTYPES videoMediaTypes[5];
    videoMediaTypes[0].clsMajorType = &MEDIATYPE_Video;
    videoMediaTypes[0].clsMinorType = &MEDIASUBTYPE_RGB24;

    videoMediaTypes[1].clsMajorType = &MEDIATYPE_Video;
    videoMediaTypes[1].clsMinorType = &MEDIASUBTYPE_RGB32;

    videoMediaTypes[2].clsMajorType = &MEDIATYPE_Video;
    videoMediaTypes[2].clsMinorType = &MEDIASUBTYPE_IYUV;

    videoMediaTypes[3].clsMajorType = &MEDIATYPE_Video;
    videoMediaTypes[3].clsMinorType = &MEDIASUBTYPE_NV12;

    videoMediaTypes[4].clsMajorType = &MEDIATYPE_Video;
    videoMediaTypes[4].clsMinorType = &MEDIASUBTYPE_YUY2;

    rp2[0].lpMediaType = videoMediaTypes;

    // 音频Pin
    rp2[1].dwFlags = REG_PINFLAG_B_OUTPUT;
    rp2[1].cInstances = 1;
    rp2[1].nMediaTypes = 1;

    REGPINTYPES audioMediaTypes[1];
    audioMediaTypes[0].clsMajorType = &MEDIATYPE_Audio;
    audioMediaTypes[0].clsMinorType = &MEDIASUBTYPE_PCM;

    rp2[1].lpMediaType = audioMediaTypes;

    rf2.cPins2 = 2;
    rf2.rgPins2 = rp2;

    // 注册为视频捕获源
    hr = pFilterMapper->RegisterFilter(CLSID_OmniVCam,
        L"OmniVCam Virtual Camera",
        NULL,
        &CLSID_VideoInputDeviceCategory,
        NULL,
        &rf2);

    pFilterMapper->Release();
    return hr;
}


HRESULT UnregisterVideoCaptureDevice() {
    HRESULT hr = S_OK;
    IFilterMapper2* pFilterMapper = NULL;

    hr = CoCreateInstance(CLSID_FilterMapper2, NULL, CLSCTX_INPROC_SERVER,
        IID_IFilterMapper2, (void**)&pFilterMapper);
    if (SUCCEEDED(hr)) {
        pFilterMapper->UnregisterFilter(&CLSID_VideoInputDeviceCategory,
            NULL,
            CLSID_OmniVCam);

        pFilterMapper->Release();
    }

    return hr;
}

STDAPI DllRegisterServer() {
    HRESULT hr = CoInitialize(NULL);
    // 先尝试取消注册，清理可能存在的旧注册
    DllUnregisterServer();

    // 注册 CLSID
    hr = RegisterFilterClass();
    if (FAILED(hr)) goto end;

    // 注册为视频捕获设备
    hr = RegisterVideoCaptureDevice();
end:
    CoUninitialize();
    return hr;
}

STDAPI DllUnregisterServer() {
    HRESULT hr = CoInitialize(NULL);

    // 取消注册视频捕获设备
    hr = UnregisterVideoCaptureDevice();

    // 取消注册 CLSID
    UnregisterFilterClass();

    CoUninitialize();
    return hr;
}


BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved) {
    if (dwReason == DLL_PROCESS_ATTACH) {
        g_hInstance = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    else if (dwReason == DLL_PROCESS_DETACH) {
    }
    return TRUE;
}


HRESULT GetPin(IBaseFilter* pFilter, LPWSTR PinID, IPin** ppPin)
{
    CComPtr<IEnumPins> pEnum;
    CComPtr<IPin> pPin;
    HRESULT hr;
    hr = pFilter->EnumPins(&pEnum);
    if (hr != S_OK)
        return hr;
    while (pPin = NULL, pEnum->Next(1, &pPin, 0) == S_OK)
    {
        PIN_DIRECTION pPinDir;
        LPWSTR _PinID;
        if ((hr = pPin->QueryDirection(&pPinDir)) != S_OK)
            continue;

        //if (pPinDir != PINDIR_OUTPUT)
        //    continue;

        if (pPin->QueryId(&_PinID) != S_OK)
            continue;

        if (wcscmp(PinID, _PinID) == 0) {
            CoTaskMemFree(_PinID);
            *ppPin = pPin.Detach();
            return S_OK;
        }
        CoTaskMemFree(_PinID);
    }
    return S_FALSE;
}
static
const
IID IID_ISampleGrabber = { 0x6B652FFF, 0x11FE, 0x4fce, { 0x92, 0xAD, 0x02, 0x66, 0xB5, 0xD7, 0xC7, 0x8F } };
static
const
CLSID CLSID_SampleGrabber = { 0xC1F400A0, 0x3F08, 0x11d3, { 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37 } };

int main() {
    avdevice_register_all();

    HRESULT hr;
    hr = CoInitialize(NULL);
    while (0) {
        CComPtr<IBaseFilter> pFilter;

        OmniVCam* pFilterVCam = new OmniVCam();
        if (!pFilterVCam) return E_OUTOFMEMORY;

        hr = pFilterVCam->QueryInterface(IID_PPV_ARGS(&pFilter));
        pFilterVCam->Release();
    }
    while (1) {
        printf("next...start\n");
        CComPtr<IGraphBuilder> pGraph;
        CComPtr<ICaptureGraphBuilder2> pBuilder2;
        CComPtr<IBaseFilter> pFilter;
        CComPtr<IPin> pPin;
        CComPtr<IPin> pPin2;
        CComPtr<IAMCrossbar> pXBar;
        CComPtr<IBaseFilter> pXBarFilter;
        CComPtr<DShowGrabber> pGrabber;
        CComPtr<IBaseFilter> pGrabberF;
        CComPtr<IMediaControl> control = NULL;
        CComPtr<IMediaEvent> _event = NULL;
        CComPtr<IEnumFilters> pEnum = NULL;
        CComPtr<IEnumMediaTypes> pMediaType = NULL;
        CComPtr<IEnumPins> pEnumPins = NULL;

        OmniVCam* pFilterVCam = new OmniVCam();
        if (!pFilterVCam) return E_OUTOFMEMORY;

        hr = pFilterVCam->QueryInterface(IID_PPV_ARGS(&pFilter));
        pFilterVCam->Release();


        hr = CoCreateInstance(CLSID_FilterGraph, NULL, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&pGraph));
        if (hr != S_OK) return hr;

        hr = CoCreateInstance(CLSID_CaptureGraphBuilder2, NULL, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&pBuilder2));
        if (hr != S_OK) return hr;


        hr = pGraph->AddFilter(pFilter, NULL);
        if (hr != S_OK) return hr;

        hr = pGraph->QueryInterface(IID_PPV_ARGS(&control));
        if (hr != S_OK)
            return hr;

        hr = pGraph->QueryInterface(IID_PPV_ARGS(&_event));
        if (hr != S_OK)
            return hr;


        pGrabber.Attach(new DShowGrabber(NULL, (void*)NULL, NULL));

        hr = pGrabber->QueryInterface(IID_IBaseFilter, (void**)&pGrabberF);

        hr = pGraph->AddFilter(pGrabberF.p, NULL);
        if (hr != S_OK) return hr;



          // Create the Sample Grabber filter.
        //hr = CoCreateInstance(CLSID_SampleGrabber, NULL, CLSCTX_INPROC_SERVER,
        //    IID_PPV_ARGS(&pGrabberF));
        //if (hr != S_OK) return hr;

        //hr = pGraph->AddFilter(pGrabberF, L"Sample Grabber");
        //if (hr != S_OK) return hr;



        hr = pBuilder2->SetFiltergraph(pGraph);
        if (hr != S_OK) return hr;

        hr = GetPin(pFilter.p, (LPWSTR)L"Video", &pPin);
        if (hr != S_OK) return hr;
        //hr = GetPin(pGrabberF.p, (LPWSTR)L"In", &pPin2);
        //hr = GetPin(pGrabberF.p, (LPWSTR)L"libAV Pin", &pPin2);
        if (hr != S_OK) return hr;

        //pPin->Connect(pPin2.p, NULL);
        //pPin->EnumMediaTypes(&pMediaType);

        //AM_MEDIA_TYPE* temp[1];
        //ULONG fetch;
        //while (pMediaType->Next(1, temp, &fetch) == S_OK) {
        //    DeleteMediaType(temp[0]);
        //}

        //PIN_INFO info = { 0 };
        //FILTER_INFO finfo = {0};
        //pPin->QueryPinInfo(&info);
        //info.pFilter->Release();
        //pFilter->QueryFilterInfo(&finfo);
        //finfo.pGraph->Release();
        //pGrabberF->EnumPins(&pEnumPins);

        //内存泄漏待查
        hr = pBuilder2->RenderStream(NULL, NULL, pPin.p, NULL, pGrabberF.p);
        //if (hr != S_OK) return hr;

        //HRESULT hr = pGraph->EnumFilters(&pEnum);
        //if (SUCCEEDED(hr))
        //{
        //    IBaseFilter* pFilter = NULL;
        //    while (S_OK == pEnum->Next(1, &pFilter, NULL))
        //    {
        //        // Remove the filter.
        //        pGraph->RemoveFilter(pFilter);
        //        // Reset the enumerator.
        //        pEnum->Reset();
        //        pFilter->Release();
        //    }
        //}
        //hr = pBuilder2->FindInterface(&LOOK_UPSTREAM_ONLY, NULL, pFilter, IID_PPV_ARGS(&pXBar));
        //if (hr != S_OK) return hr;

        //hr = pXBar->QueryInterface(IID_PPV_ARGS(&pXBarFilter));
        //if (hr != S_OK) return hr;
        printf("before run...\n");
        hr = control->Run();
        if (hr == S_FALSE) {
            OAFilterState pfs;
            hr = control->GetState(0, &pfs);
        }
        av_usleep(3000 * 1000000);
        //control->Pause();
        //av_usleep(2 * 1000000);
        //control->Run();
        //av_usleep(20 * 1000000);
        control->Stop();
        break;
        printf("next...\n");

    }
    CoUninitialize();
    return 0;
}

