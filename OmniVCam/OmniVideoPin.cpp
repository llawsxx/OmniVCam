#include "OmniVideoPin.h"
#include <uuids.h>


OmniVideoPin::OmniVideoPin(OmniVCam* pFilter)
    : m_pFilter(pFilter), m_refCount(1), m_connectedPin(NULL),
    m_allocator(NULL), m_streaming(false), m_startTime(0),
    m_currentFpsNumerator(30), m_currentFpsDenominator(1),m_currentWidth(1920),m_currentHeight(1080),m_currentFormat(AV_PIX_FMT_BGR24), m_frameCount(0) {
    InitMediaType();
    DEBUG_LOG_REF()
}

OmniVideoPin::~OmniVideoPin() {
    if (m_allocator) {
        m_allocator->Release();
        m_allocator = NULL;
    }

    if (m_connectedPin) {
        m_connectedPin->Release();
        m_connectedPin = NULL;
    }

    if (m_connectedMemPin) {
        m_connectedMemPin->Release();
        m_connectedMemPin = NULL;
    }

    if (m_mediaType.pbFormat) {
        CoTaskMemFree(m_mediaType.pbFormat);
        m_mediaType.pbFormat = NULL;
    }

    if (m_mediaType.pUnk) {
        m_mediaType.pUnk->Release();
        m_mediaType.pUnk = NULL;
    }
}


void OmniVideoPin::InitMediaType() {
    ZeroMemory(&m_mediaType, sizeof(AM_MEDIA_TYPE));
    m_mediaType.majortype = MEDIATYPE_Video;
    m_mediaType.subtype = MEDIASUBTYPE_RGB24;
    m_mediaType.bFixedSizeSamples = TRUE;
    m_mediaType.bTemporalCompression = FALSE;
    m_mediaType.lSampleSize = 1920 * 1080 * 3; // RGB24

    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (pvi) {
        ZeroMemory(pvi, sizeof(VIDEOINFOHEADER));
        pvi->AvgTimePerFrame = 333333; // 30 fps
        pvi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        pvi->bmiHeader.biWidth = 1920;
        pvi->bmiHeader.biHeight = 1080;
        pvi->bmiHeader.biPlanes = 1;
        pvi->bmiHeader.biBitCount = 24;
        pvi->bmiHeader.biCompression = BI_RGB;
        pvi->bmiHeader.biSizeImage = 1920 * 1080 * 3;

        m_mediaType.formattype = FORMAT_VideoInfo;
        m_mediaType.cbFormat = sizeof(VIDEOINFOHEADER);
        m_mediaType.pbFormat = (BYTE*)pvi;
    }
}

// IUnknown implementation
STDMETHODIMP OmniVideoPin::QueryInterface(REFIID riid, void** ppv) {
    if (riid == IID_IUnknown) {
        *ppv = static_cast<IUnknown*>(static_cast<IPin*>(this));
    }
    else if (riid == IID_IPin) {
        *ppv = static_cast<IPin*>(this);
    }
    else if (riid == IID_IMemInputPin) {
        *ppv = static_cast<IMemInputPin*>(this);
    }
    else if (riid == IID_IAMStreamConfig) {
        *ppv = static_cast<IAMStreamConfig*>(this);
    }
    else if (riid == IID_IKsPropertySet) {
        *ppv = (IKsPropertySet*)this;
    }
    else {
        *ppv = NULL;
        return E_NOINTERFACE;
    }
    AddRef();
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP_(ULONG) OmniVideoPin::AddRef() {
    long refCount = InterlockedIncrement(&m_refCount);
    DEBUG_LOG_REF()
        return refCount;
}

STDMETHODIMP_(ULONG) OmniVideoPin::Release() {
    ULONG refCount = InterlockedDecrement(&m_refCount);
    DEBUG_LOG_REF()
    if (refCount == 0) {
        delete this;
    }
    return refCount;
}

STDMETHODIMP OmniVideoPin::Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt) {
    if (!pReceivePin) return E_POINTER;
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) return VFW_E_ALREADY_CONNECTED;
    PIN_DIRECTION Dir;
    HRESULT hr = pReceivePin->QueryDirection(&Dir); if (FAILED(hr)) return hr;
    if (Dir == PINDIR_OUTPUT) {
        return VFW_E_INVALID_DIRECTION;
    }
    IMemInputPin* memPin = NULL;
    hr = pReceivePin->QueryInterface(IID_IMemInputPin, (void**)&memPin);
    if (FAILED(hr)) return hr;

    hr = pReceivePin->ReceiveConnection(this, pmt ? pmt : &m_mediaType);
    if (SUCCEEDED(hr)) {
        if (pmt) {
            // OBS setFormat和Connect的格式可能不一致
            SetFormatInternal(pmt,FALSE);
        }
        m_connectedPin = pReceivePin;
        m_connectedPin->AddRef();

        m_connectedMemPin = memPin;
        printf("m_connectedPin %p\n", m_connectedPin);
        DoAllocation();
    }
    else {
        if (memPin) {
            memPin->Release();
        }
    }
    DEBUG_LOG_REF()
    return hr;
}

STDMETHODIMP OmniVideoPin::ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt) {
    DEBUG_LOG_REF()
        return S_OK; // Output pin doesn't receive connections
}

STDMETHODIMP OmniVideoPin::Disconnect() {
    CLock lck(m_pFilter->m_cs);
    if (!m_connectedPin) return S_FALSE;
    m_connectedPin->Release();
    m_connectedPin = NULL;
    if (m_connectedMemPin) {
        m_connectedMemPin->Release();
        m_connectedMemPin = NULL;
    }
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP OmniVideoPin::ConnectedTo(IPin** pPin) {
    if (!pPin) return E_POINTER;
    CLock lck(m_pFilter->m_cs);
    *pPin = m_connectedPin;
    if (!m_connectedPin) return VFW_E_NOT_CONNECTED;
    m_connectedPin->AddRef();
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP OmniVideoPin::ConnectionMediaType(AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    CLock lck(m_pFilter->m_cs);
    if (!m_connectedPin) return VFW_E_NOT_CONNECTED;

    ZeroMemory(pmt, sizeof(AM_MEDIA_TYPE));
    return CopyMediaType(pmt, &m_mediaType);
}

STDMETHODIMP OmniVideoPin::QueryPinInfo(PIN_INFO* pInfo) {
    if (!pInfo) return E_POINTER;

    pInfo->pFilter = (IBaseFilter*)m_pFilter;
    if (m_pFilter) ((IBaseFilter*)m_pFilter)->AddRef();
    pInfo->dir = PINDIR_OUTPUT;
    wcscpy_s(pInfo->achName, L"Video");
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP OmniVideoPin::QueryDirection(PIN_DIRECTION* pPinDir) {
    if (!pPinDir) return E_POINTER;
    *pPinDir = PINDIR_OUTPUT;
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP OmniVideoPin::QueryId(LPWSTR* Id) {
    *Id = (LPWSTR)CoTaskMemAlloc(12 * sizeof(wchar_t));
    if (*Id) wcscpy_s(*Id, 12, L"Video");
    DEBUG_LOG_REF()
    return S_OK;
}

STDMETHODIMP OmniVideoPin::QueryAccept(const AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    // Basic format checking
    if (pmt->majortype != MEDIATYPE_Video) return S_FALSE;
    if (pmt->formattype != FORMAT_VideoInfo) return S_FALSE;
    if (pmt->subtype != MEDIASUBTYPE_RGB24 && pmt->subtype != MEDIASUBTYPE_RGB32 && pmt->subtype != MEDIASUBTYPE_YUY2 &&
        pmt->subtype != MEDIASUBTYPE_IYUV && pmt->subtype != MEDIASUBTYPE_NV12) return S_FALSE;
    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)pmt->pbFormat;
    return S_OK;
}

STDMETHODIMP OmniVideoPin::QueryInternalConnections(IPin** apPin, ULONG* nPin) {
    // 我们没有内部连接
    if (nPin) *nPin = 0;
    return E_NOTIMPL;
}

STDMETHODIMP OmniVideoPin::EndOfStream() {
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return m_connectedPin->EndOfStream();
    }
    return S_OK;
}

STDMETHODIMP OmniVideoPin::BeginFlush() {
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return m_connectedPin->BeginFlush();
    }
    return S_OK;
}

STDMETHODIMP OmniVideoPin::EndFlush() {
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return m_connectedPin->EndFlush();
    }
    return S_OK;
}

STDMETHODIMP OmniVideoPin::NewSegment(REFERENCE_TIME tStart, REFERENCE_TIME tStop, double dRate) {
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return m_connectedPin->NewSegment(tStart, tStop, dRate);
    }
    return S_OK;
}

STDMETHODIMP OmniVideoPin::GetAllocator(IMemAllocator** ppAllocator) {
    return VFW_E_NO_ALLOCATOR;
}

STDMETHODIMP OmniVideoPin::NotifyAllocator(IMemAllocator* pAllocator, BOOL bReadOnly) {
    if (!pAllocator) return E_POINTER;
    CLock lck(m_pFilter->m_cs);
    if (m_allocator) {
        m_allocator->Release();
        m_allocator = NULL;
    }
    m_allocator = pAllocator;
    if (m_allocator) m_allocator->AddRef();
    return S_OK;
}

STDMETHODIMP OmniVideoPin::GetAllocatorRequirements(ALLOCATOR_PROPERTIES* pProps) {
    return E_NOTIMPL;
}


STDMETHODIMP OmniVideoPin::Receive(IMediaSample* pSample) {
    return S_OK;
}

STDMETHODIMP OmniVideoPin::ReceiveMultiple(IMediaSample** pSamples, long nSamples, long* nSamplesProcessed) {
    return S_OK;
}

STDMETHODIMP OmniVideoPin::ReceiveCanBlock() {
    return S_FALSE;
}

static HRESULT doAllocSize(IMemAllocator* pAlloc, ALLOCATOR_PROPERTIES* prop, AM_MEDIA_TYPE* mt)
{
    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)mt->pbFormat;
    if (!pvi)return E_INVALIDARG;
    prop->cBuffers = 1;
    prop->cbAlign = 1;
    prop->cbBuffer = pvi->bmiHeader.biSizeImage;
    ALLOCATOR_PROPERTIES Actual; memset(&Actual, 0, sizeof(Actual));
    HRESULT hr = pAlloc->SetProperties(prop, &Actual);
    if (FAILED(hr)) return hr;
    if (Actual.cbBuffer < prop->cbBuffer) return E_FAIL;

    return S_OK;
}

STDMETHODIMP OmniVideoPin::DoAllocation() {
    if (m_allocator) {
        m_allocator->Release();
        m_allocator = NULL;
    }

    HRESULT hr = S_OK;
    ALLOCATOR_PROPERTIES prop;
    ZeroMemory(&prop, sizeof(prop));
    IMemInputPin* memPin = NULL;
    hr = m_connectedPin->QueryInterface(IID_IMemInputPin, (void**)&memPin);
    if (FAILED(hr)) goto end;
    memPin->GetAllocatorRequirements(&prop);
    if (prop.cbAlign == 0) {
        prop.cbAlign = 1;
    }
    hr = memPin->GetAllocator(&m_allocator);
    if (SUCCEEDED(hr)) {
        hr = doAllocSize(m_allocator, &prop, &m_mediaType);
        if (SUCCEEDED(hr)) {
            hr = memPin->NotifyAllocator(m_allocator, FALSE);
            if (SUCCEEDED(hr)) goto end;
        }
    }
    if (m_allocator) {
        m_allocator->Release();
        m_allocator = NULL;
    }

    hr = CoCreateInstance(CLSID_MemoryAllocator, 0, CLSCTX_INPROC_SERVER, IID_IMemAllocator, (void**)&m_allocator);
    if (FAILED(hr)) return hr;

    hr = doAllocSize(m_allocator, &prop, &m_mediaType);
    if (SUCCEEDED(hr)) {
        hr = memPin->NotifyAllocator(m_allocator, FALSE);
        if (SUCCEEDED(hr)) goto end;
    }

    if (m_allocator) {
        m_allocator->Release();
        m_allocator = NULL;
    }
end:
    if (memPin) {
        memPin->Release();
    }
    return hr;
}

STDMETHODIMP OmniVideoPin::SetFormatInternal(const AM_MEDIA_TYPE* pmt,BOOL setFrameRate) {
    if (pmt->majortype != MEDIATYPE_Video) return VFW_E_INVALIDMEDIATYPE;
    if (pmt->formattype != FORMAT_VideoInfo) return VFW_E_INVALIDMEDIATYPE;
    if (m_connectedPin) {
        return VFW_E_ALREADY_CONNECTED;
    }
    REFERENCE_TIME AvgTimePerFrame = 0;
    if (m_mediaType.pbFormat) {
        VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)m_mediaType.pbFormat;
        AvgTimePerFrame = pvi->AvgTimePerFrame;
        CoTaskMemFree(m_mediaType.pbFormat);
        m_mediaType.pbFormat = NULL;
    }
    if (m_mediaType.pUnk) {
        m_mediaType.pUnk->Release();
        m_mediaType.pUnk = NULL;
    }

    m_mediaType.majortype = pmt->majortype;
    m_mediaType.subtype = pmt->subtype;
    m_mediaType.bFixedSizeSamples = pmt->bFixedSizeSamples;
    m_mediaType.bTemporalCompression = pmt->bTemporalCompression;
    m_mediaType.lSampleSize = pmt->lSampleSize;
    m_mediaType.formattype = pmt->formattype;

    m_mediaType.pUnk = pmt->pUnk;
    if (m_mediaType.pUnk) {
        m_mediaType.pUnk->AddRef();
    }

    if (pmt->cbFormat > 0 && pmt->pbFormat) {
        m_mediaType.cbFormat = pmt->cbFormat;
        m_mediaType.pbFormat = (BYTE*)CoTaskMemAlloc(pmt->cbFormat);
        if (!m_mediaType.pbFormat) {
            m_mediaType.cbFormat = 0;
            if (m_mediaType.pUnk) {
                m_mediaType.pUnk->Release();
                m_mediaType.pUnk = NULL;
            }
            return E_OUTOFMEMORY;
        }
        memcpy(m_mediaType.pbFormat, pmt->pbFormat, pmt->cbFormat);

        // 更新帧率信息
        if (pmt->formattype == FORMAT_VideoInfo && pmt->pbFormat) {
            VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)pmt->pbFormat;

            m_currentWidth = pvi->bmiHeader.biWidth;
            m_currentHeight = pvi->bmiHeader.biHeight;

            if (pmt->subtype == MEDIASUBTYPE_RGB24) {
                m_currentFormat = AV_PIX_FMT_BGR24;
            }

            if (pmt->subtype == MEDIASUBTYPE_RGB32) {
                m_currentFormat = AV_PIX_FMT_0RGB32;
            }

            else if (pmt->subtype == MEDIASUBTYPE_YUY2) {
                m_currentFormat = AV_PIX_FMT_YUYV422;
            }
            else if (pmt->subtype == MEDIASUBTYPE_IYUV) {
                m_currentFormat = AV_PIX_FMT_YUV420P;
            }
            else if (pmt->subtype == MEDIASUBTYPE_NV12) {
                m_currentFormat = AV_PIX_FMT_NV12;
            }
            // 根据帧间隔推断帧率（近似值）
            // 在实际应用中，应该从格式信息中获取精确的帧率
            if (setFrameRate || AvgTimePerFrame == 0) AvgTimePerFrame = pvi->AvgTimePerFrame;


            if (AvgTimePerFrame > 0) {
                double fps = 10000000.0 / AvgTimePerFrame;

                // 匹配到最接近的标准帧率
                if (fabs(fps - 1.0) < 0.1) {
                    m_currentFpsNumerator = 1;
                    m_currentFpsDenominator = 1;
                }
                else if (fabs(fps - 15.0) < 0.1) {
                    m_currentFpsNumerator = 15;
                    m_currentFpsDenominator = 1;
                }
                else if (fabs(fps - 24000.0 / 1001) < 0.01) {
                    m_currentFpsNumerator = 24000;
                    m_currentFpsDenominator = 1001;
                }
                else if (fabs(fps - 24.0) < 0.1) {
                    m_currentFpsNumerator = 24;
                    m_currentFpsDenominator = 1;
                }
                else if (fabs(fps - 25.0) < 0.1) {
                    m_currentFpsNumerator = 25;
                    m_currentFpsDenominator = 1;
                }
                else if (fabs(fps - 30000.0 / 1001) < 0.01) {
                    m_currentFpsNumerator = 30000;
                    m_currentFpsDenominator = 1001;
                }
                else if (fabs(fps - 30.0) < 0.1) {
                    m_currentFpsNumerator = 30;
                    m_currentFpsDenominator = 1;
                }
                else if (fabs(fps - 50.0) < 0.1) {
                    m_currentFpsNumerator = 50;
                    m_currentFpsDenominator = 1;
                }
                else if (fabs(fps - 60000.0 / 1001) < 0.01) {
                    m_currentFpsNumerator = 60000;
                    m_currentFpsDenominator = 1001;
                }
                else if (fabs(fps - 60.0) < 0.1) {
                    m_currentFpsNumerator = 60;
                    m_currentFpsDenominator = 1;
                }
                else {
                    // 默认使用30fps
                    m_currentFpsNumerator = 30;
                    m_currentFpsDenominator = 1;
                }
            }
        }

        // 重置帧计数器
        m_frameCount = 0;

        return S_OK;
    }
    else {
        m_mediaType.cbFormat = 0;
        m_mediaType.pbFormat = NULL;
    }

    return S_OK;
}


STDMETHODIMP OmniVideoPin::SetFormat(AM_MEDIA_TYPE* pmt) {
    DEBUG_LOG_REF()

    if (!pmt) return E_POINTER;
    if (pmt->majortype != MEDIATYPE_Video) return VFW_E_INVALIDMEDIATYPE;

    CLock lck(m_pFilter->m_cs);
    return SetFormatInternal(pmt,TRUE);
}

STDMETHODIMP OmniVideoPin::GetFormat(AM_MEDIA_TYPE** ppmt) {
    DEBUG_LOG_REF()

        if (!ppmt) return E_POINTER;

    *ppmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!*ppmt) return E_OUTOFMEMORY;

    ZeroMemory(*ppmt, sizeof(AM_MEDIA_TYPE));
    CLock lck(m_pFilter->m_cs);
    HRESULT hr = CopyMediaType(*ppmt, &m_mediaType);
    if (FAILED(hr)) {
        CoTaskMemFree(*ppmt);
        *ppmt = NULL;
        return hr;
    }

    return S_OK;
}


HRESULT OmniVideoPin::SetCustomFormat(const OmniVideoFormat& format) {
    // 检查参数有效性
    if (format.width <= 0 || format.height <= 0 || format.fpsNum <= 0 || format.fpsDen <= 0) {
        return E_INVALIDARG;
    }
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return VFW_E_ALREADY_CONNECTED;
    }

    if (m_mediaType.pbFormat) {
        CoTaskMemFree(m_mediaType.pbFormat);
        m_mediaType.pbFormat = NULL;
    }

    m_mediaType.majortype = MEDIATYPE_Video;
    m_mediaType.subtype = format.subtype;
    m_mediaType.bFixedSizeSamples = TRUE;
    m_mediaType.bTemporalCompression = FALSE;
    m_mediaType.formattype = FORMAT_VideoInfo;

    long sampleSize = 0;
    int bitsPerPixel = 0;

    if (format.subtype == MEDIASUBTYPE_RGB24) {
        bitsPerPixel = 24;
        m_currentFormat = AV_PIX_FMT_RGB24;
    }
    if (format.subtype == MEDIASUBTYPE_RGB32) {
        bitsPerPixel = 32;
        m_currentFormat = AV_PIX_FMT_0RGB32;
    }
    else if (format.subtype == MEDIASUBTYPE_YUY2) {
        bitsPerPixel = 16;
        m_currentFormat = AV_PIX_FMT_YUYV422;

    }
    else if (format.subtype == MEDIASUBTYPE_IYUV) {
        bitsPerPixel = 12;
        m_currentFormat = AV_PIX_FMT_YUV420P;

    }
    else if (format.subtype == MEDIASUBTYPE_NV12) {
        bitsPerPixel = 12;
        m_currentFormat = AV_PIX_FMT_NV12;

    }
    else {
        // 不支持的格式，默认使用RGB24
        bitsPerPixel = 24;
        m_mediaType.subtype = MEDIASUBTYPE_RGB24;
    }

    sampleSize = format.width * format.height * bitsPerPixel / 8;
    m_mediaType.lSampleSize = sampleSize;

    VIDEOINFOHEADER* pvi = (VIDEOINFOHEADER*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!pvi) {
        return E_OUTOFMEMORY;
    }

    ZeroMemory(pvi, sizeof(VIDEOINFOHEADER));
    float fps = (float)format.fpsNum / format.fpsDen;
    pvi->AvgTimePerFrame = CalculateInterval(format.fpsNum, format.fpsDen);
    pvi->dwBitRate = format.width * format.height * fps * bitsPerPixel;

    pvi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pvi->bmiHeader.biWidth = format.width;
    pvi->bmiHeader.biHeight = format.height;
    pvi->bmiHeader.biPlanes = 1;
    pvi->bmiHeader.biBitCount = bitsPerPixel;
    pvi->bmiHeader.biSizeImage = sampleSize;

    if (format.subtype == MEDIASUBTYPE_RGB24) {
        pvi->bmiHeader.biCompression = BI_RGB;
    }
    else if (format.subtype == MEDIASUBTYPE_YUY2) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('Y', 'U', 'Y', '2');
    }
    else if (format.subtype == MEDIASUBTYPE_IYUV) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('I', 'Y', 'U', 'V');
        pvi->bmiHeader.biPlanes = 3;
    }
    else if (format.subtype == MEDIASUBTYPE_NV12) {
        pvi->bmiHeader.biCompression = MAKEFOURCC('N', 'V', '1', '2');
        pvi->bmiHeader.biPlanes = 2;
    }
    else {
        pvi->bmiHeader.biCompression = BI_RGB;
    }

    pvi->bmiHeader.biClrUsed = 0;
    pvi->bmiHeader.biClrImportant = 0;

    pvi->rcSource.left = 0;
    pvi->rcSource.top = 0;
    pvi->rcSource.right = format.width;
    pvi->rcSource.bottom = format.height;

    pvi->rcTarget = pvi->rcSource;

    m_mediaType.cbFormat = sizeof(VIDEOINFOHEADER);
    m_mediaType.pbFormat = (BYTE*)pvi;

    // 设置精确的帧率信息
    m_currentFpsNumerator = format.fpsNum;
    m_currentFpsDenominator = format.fpsDen;
    m_currentWidth = format.width;
    m_currentHeight = format.height;

    m_frameCount = 0;

    return S_OK;
}

REFERENCE_TIME OmniVideoPin::GetFrameDuration() {
    return CalculateFrameDuration();
}

HRESULT OmniVideoPin::PushFrame(BYTE* data, long size, REFERENCE_TIME customStartTime) {
    HRESULT hr = S_OK;
    if (!data || size <= 0) return E_INVALIDARG;
    ResetEvent(m_pFilter->m_noProcess[0]);
    IMemInputPin* pInputPin = m_connectedMemPin;
    IMediaSample* pSample;
    if (!m_allocator) hr = VFW_E_NO_ALLOCATOR;
    if (!m_streaming) hr = VFW_E_WRONG_STATE;
    if (hr != S_OK) {
        goto end;
    }

    if (!m_connectedPin || !m_connectedMemPin) {
        hr = S_FALSE;
        goto end;
    }

    hr = m_allocator->GetBuffer(&pSample, NULL, NULL, 0);

    if (SUCCEEDED(hr)) {

        BYTE* pBuffer = NULL;
        hr = pSample->GetPointer(&pBuffer);
        if (SUCCEEDED(hr)) {
            // 复制数据
            long sampleSize = pSample->GetSize();
            long copySize = min(size, sampleSize);
            memcpy(pBuffer, data, copySize);
            pSample->SetActualDataLength(copySize);

            // 使用精确的帧率计算时间戳
            REFERENCE_TIME frameDuration = CalculateFrameDuration();
            REFERENCE_TIME startTime;

            if (customStartTime != -1) {
                startTime = customStartTime;
            }
            else {
                startTime = CalculateFrameTime(m_frameCount);
            }
            REFERENCE_TIME endTime = startTime + frameDuration;

            pSample->SetTime(&startTime, &endTime);
            pSample->SetSyncPoint(TRUE);
            // 更新帧计数器
            m_frameCount++;
            //{char buf[512]; sprintf(buf,"Receive 1 %I64d\n", av_gettime_relative()); FILE *fp1 = fopen("debbug.txt","a+");fwrite(buf,1,strlen(buf),fp1); fclose(fp1); }
            hr = pInputPin->Receive(pSample);
            //{ char buf[512]; sprintf(buf, "Receive 2 %I64d\n",av_gettime_relative()); FILE* fp1 = fopen("debbug.txt", "a+"); fwrite(buf, 1, strlen(buf), fp1); fclose(fp1); }
        }
        pSample->Release();
    }
end:
    SetEvent(m_pFilter->m_noProcess[0]);
    return hr;
}

REFERENCE_TIME OmniVideoPin::CalculateFrameDuration() {
    return (REFERENCE_TIME)(10000000.0 * m_currentFpsDenominator / m_currentFpsNumerator);
}

// 新增：计算指定帧的时间戳
REFERENCE_TIME OmniVideoPin::CalculateFrameTime(int frameNumber) {
    return m_startTime + (REFERENCE_TIME)(frameNumber * 10000000.0 * m_currentFpsDenominator / m_currentFpsNumerator);
}

HRESULT OmniVideoPin::Active(BOOL bActive) {
    HRESULT hr;
    if (!m_connectedPin || !m_allocator) return E_FAIL;
    if (bActive) hr = m_allocator->Commit();
    else hr = m_allocator->Decommit();
    return hr;
}

HRESULT OmniVideoPin::Stop() {
    DEBUG_LOG_REF()
    m_streaming = false;
    return S_OK;
}

HRESULT OmniVideoPin::Pause() {
    DEBUG_LOG_REF()
    m_streaming = false;
    return S_OK;
}

HRESULT OmniVideoPin::Continue() {
    DEBUG_LOG_REF()
    m_streaming = true;
    return S_OK;
}

HRESULT OmniVideoPin::Run(REFERENCE_TIME tStart) {
    DEBUG_LOG_REF()
    m_streaming = true;
    m_startTime = tStart;
    m_frameCount = 0;
    return S_OK;
}



STDMETHODIMP OmniVideoPin::EnumMediaTypes(IEnumMediaTypes** ppEnum) {
    if (!ppEnum) return E_POINTER;

    const int totalFormats = GetVideoSupportedFormatCount();
    AM_MEDIA_TYPE** ppMediaTypes = (AM_MEDIA_TYPE**)CoTaskMemAlloc(totalFormats * sizeof(AM_MEDIA_TYPE*));
    if (!ppMediaTypes) return E_OUTOFMEMORY;

    ZeroMemory(ppMediaTypes, totalFormats * sizeof(AM_MEDIA_TYPE*));

    bool success = true;
    for (int i = 0; i < totalFormats; i++) {
        FormatManager::VideoFormat format;
        if (SUCCEEDED(GetVideoSupportedFormatByIndex(i, &format))) {
            ppMediaTypes[i] = formatManager.CreateVideoMediaType(format);
            if (!ppMediaTypes[i]) {
                success = false;
                break;
            }
        }
    }

    if (!success) {
        for (int i = 0; i < totalFormats; i++) {
            if (ppMediaTypes[i]) {
                if (ppMediaTypes[i]->pbFormat) CoTaskMemFree(ppMediaTypes[i]->pbFormat);
                CoTaskMemFree(ppMediaTypes[i]);
            }
        }
        CoTaskMemFree(ppMediaTypes);
        return E_OUTOFMEMORY;
    }

    *ppEnum = new OmniMediaTypeEnum(ppMediaTypes, totalFormats);
    if (!*ppEnum) {
        for (int i = 0; i < totalFormats; i++) {
            if (ppMediaTypes[i]) {
                if (ppMediaTypes[i]->pbFormat) CoTaskMemFree(ppMediaTypes[i]->pbFormat);
                CoTaskMemFree(ppMediaTypes[i]);
            }
        }
        CoTaskMemFree(ppMediaTypes);
        return E_OUTOFMEMORY;
    }
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP OmniVideoPin::GetNumberOfCapabilities(int* piCount, int* piSize) {
    if (!piCount || !piSize) return E_POINTER;

    *piCount = GetVideoSupportedFormatCount();
    *piSize = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    DEBUG_LOG_REF()
        return S_OK;
}

STDMETHODIMP OmniVideoPin::GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) {
    if (!ppmt || !pSCC) return E_POINTER;

    FormatManager::VideoFormat format;
    HRESULT hr = GetVideoSupportedFormatByIndex(iIndex, &format);
    if (FAILED(hr)) return hr;

    *ppmt = formatManager.CreateVideoMediaType(format);
    if (!*ppmt) return E_OUTOFMEMORY;

    // 填充能力信息
    VIDEO_STREAM_CONFIG_CAPS* pCaps = (VIDEO_STREAM_CONFIG_CAPS*)pSCC;
    ZeroMemory(pCaps, sizeof(VIDEO_STREAM_CONFIG_CAPS));

    pCaps->MinOutputSize.cx = format.width;
    pCaps->MinOutputSize.cy = format.height;
    pCaps->MaxOutputSize.cx = format.width;
    pCaps->MaxOutputSize.cy = format.height;
    pCaps->OutputGranularityX = 0;
    pCaps->OutputGranularityY = 0;

    REFERENCE_TIME frameInterval = (REFERENCE_TIME)(10000000.0 * format.fpsDen / format.fpsNum);
    pCaps->MinFrameInterval = frameInterval;
    pCaps->MaxFrameInterval = frameInterval;

    pCaps->VideoStandard = AnalogVideo_None;
    pCaps->MinBitsPerSecond = (DWORD)((*ppmt)->lSampleSize * 8 * 10000000.0 / frameInterval);
    pCaps->MaxBitsPerSecond = pCaps->MinBitsPerSecond;
    DEBUG_LOG_REF()
        return S_OK;
}


STDMETHODIMP OmniVideoPin::Set(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
    DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData) {
    return E_NOTIMPL;
}

STDMETHODIMP OmniVideoPin::Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
    DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData,
    DWORD* pcbReturned) {

    if (guidPropSet != AMPROPSETID_Pin)             return E_PROP_SET_UNSUPPORTED;
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)        return E_PROP_ID_UNSUPPORTED;
    if (pPropData == NULL && pcbReturned == NULL)   return E_POINTER;

    if (pcbReturned) *pcbReturned = sizeof(GUID);
    if (pPropData == NULL)          return S_OK; // Caller just wants to know the size. 
    if (cbPropData < sizeof(GUID))  return E_UNEXPECTED;// The buffer is too small.

    *(GUID*)pPropData = PIN_CATEGORY_CAPTURE;
    return S_OK;
}

STDMETHODIMP OmniVideoPin::QuerySupported(REFGUID guidPropSet, DWORD dwPropID,
    DWORD* pTypeSupport) {
    if (guidPropSet != AMPROPSETID_Pin) return E_PROP_SET_UNSUPPORTED;
    if (dwPropID != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    // We support getting this property, but not setting it.
    if (pTypeSupport) *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    return S_OK;
}