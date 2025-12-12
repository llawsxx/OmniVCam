#include "OmniAudioPin.h"
#include <uuids.h>


OmniAudioPin::OmniAudioPin(OmniVCam* pFilter)
    : m_pFilter(pFilter), m_refCount(1), m_connectedPin(NULL),
    m_allocator(NULL), m_streaming(false), m_startTime(0),m_audioChannels(2),m_audioSampleRate(48000),m_audioFormat(AV_SAMPLE_FMT_S16) {
    DEBUG_LOG_REF()
    InitMediaType();
}

OmniAudioPin::~OmniAudioPin(){
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

void OmniAudioPin::InitMediaType() {
    ZeroMemory(&m_mediaType, sizeof(AM_MEDIA_TYPE));
    m_mediaType.majortype = MEDIATYPE_Audio;
    m_mediaType.subtype = MEDIASUBTYPE_PCM;
    m_mediaType.bFixedSizeSamples = TRUE;
    m_mediaType.bTemporalCompression = FALSE;

    WAVEFORMATEX* pwfx = (WAVEFORMATEX*)CoTaskMemAlloc(sizeof(WAVEFORMATEX));
    if (pwfx) {
        ZeroMemory(pwfx, sizeof(WAVEFORMATEX));
        pwfx->wFormatTag = WAVE_FORMAT_PCM;
        pwfx->nChannels = 2;
        pwfx->nSamplesPerSec = 48000;
        pwfx->wBitsPerSample = 16;
        pwfx->nBlockAlign = pwfx->nChannels * pwfx->wBitsPerSample / 8;
        pwfx->nAvgBytesPerSec = pwfx->nSamplesPerSec * pwfx->nBlockAlign;
        pwfx->cbSize = 0;

        m_mediaType.formattype = FORMAT_WaveFormatEx;
        m_mediaType.cbFormat = sizeof(WAVEFORMATEX);
        m_mediaType.pbFormat = (BYTE*)pwfx;
        m_mediaType.lSampleSize = pwfx->nBlockAlign;
    }
}

// IUnknown implementation
STDMETHODIMP OmniAudioPin::QueryInterface(REFIID riid, void** ppv) {
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

STDMETHODIMP_(ULONG) OmniAudioPin::AddRef() {
    long refCount = InterlockedIncrement(&m_refCount);
    DEBUG_LOG_REF()
    return refCount;
}

STDMETHODIMP_(ULONG) OmniAudioPin::Release() {
    ULONG refCount = InterlockedDecrement(&m_refCount);
    DEBUG_LOG_REF()
    if (refCount == 0) {
        delete this;
    }
    return refCount;
}

// IPin implementation
STDMETHODIMP OmniAudioPin::Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt) {
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
            SetFormatInternal(pmt);
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

STDMETHODIMP OmniAudioPin::ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt) {
    DEBUG_LOG_REF()
    return S_OK; // Output pin doesn't receive connections
}

STDMETHODIMP OmniAudioPin::Disconnect() {
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

STDMETHODIMP OmniAudioPin::ConnectedTo(IPin** pPin) {
    if (!pPin) return E_POINTER;
    CLock lck(m_pFilter->m_cs);
    *pPin = m_connectedPin;
    if (!m_connectedPin) return VFW_E_NOT_CONNECTED;
    m_connectedPin->AddRef();
    DEBUG_LOG_REF()
    return S_OK;
}

STDMETHODIMP OmniAudioPin::ConnectionMediaType(AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    CLock lck(m_pFilter->m_cs);
    if (!m_connectedPin) return VFW_E_NOT_CONNECTED;
    ZeroMemory(pmt, sizeof(AM_MEDIA_TYPE));
    return CopyMediaType(pmt, &m_mediaType);
}

STDMETHODIMP OmniAudioPin::QueryPinInfo(PIN_INFO* pInfo) {
    if (!pInfo) return E_POINTER;

    pInfo->pFilter = (IBaseFilter*)m_pFilter;
    if (m_pFilter) ((IBaseFilter*)m_pFilter)->AddRef();
    pInfo->dir = PINDIR_OUTPUT;
    wcscpy_s(pInfo->achName, L"Audio");
    DEBUG_LOG_REF()
    return S_OK;
}

STDMETHODIMP OmniAudioPin::QueryDirection(PIN_DIRECTION* pPinDir) {
    if (!pPinDir) return E_POINTER;
    *pPinDir = PINDIR_OUTPUT;
    DEBUG_LOG_REF()
    return S_OK;
}

STDMETHODIMP OmniAudioPin::QueryId(LPWSTR* Id) {
    *Id = (LPWSTR)CoTaskMemAlloc(12 * sizeof(wchar_t));
    if (*Id) wcscpy_s(*Id, 12, L"Audio");
    DEBUG_LOG_REF()
    return S_OK;
}

STDMETHODIMP OmniAudioPin::QueryAccept(const AM_MEDIA_TYPE* pmt) {
    if (!pmt) return E_POINTER;
    if (pmt->majortype != MEDIATYPE_Audio) return S_FALSE;
    if (pmt->formattype != FORMAT_WaveFormatEx) return S_FALSE;
    if (pmt->subtype != MEDIASUBTYPE_PCM) return S_FALSE;

    return S_OK;
}

STDMETHODIMP OmniAudioPin::QueryInternalConnections(IPin** apPin, ULONG* nPin) {
    // 我们没有内部连接
    if (nPin) *nPin = 0;
    return E_NOTIMPL;
}

STDMETHODIMP OmniAudioPin::EndOfStream() {
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return m_connectedPin->EndOfStream();
    }
    return S_OK;
}

STDMETHODIMP OmniAudioPin::BeginFlush() {
    CLock lck(m_pFilter->m_cs);

    if (m_connectedPin) {
        return m_connectedPin->BeginFlush();
    }
    return S_OK;
}

STDMETHODIMP OmniAudioPin::EndFlush() {
    CLock lck(m_pFilter->m_cs);

    if (m_connectedPin) {
        return m_connectedPin->EndFlush();
    }
    return S_OK;
}

STDMETHODIMP OmniAudioPin::NewSegment(REFERENCE_TIME tStart, REFERENCE_TIME tStop, double dRate) {
    CLock lck(m_pFilter->m_cs);

    if (m_connectedPin) {
        return m_connectedPin->NewSegment(tStart, tStop, dRate);
    }
    return S_OK;
}

STDMETHODIMP OmniAudioPin::GetAllocator(IMemAllocator** ppAllocator) {
    return VFW_E_NO_ALLOCATOR;
}

STDMETHODIMP OmniAudioPin::NotifyAllocator(IMemAllocator* pAllocator, BOOL bReadOnly) {
    CLock lck(m_pFilter->m_cs);
    m_allocator = pAllocator;
    if (m_allocator) m_allocator->AddRef();
    return S_OK;
}

STDMETHODIMP OmniAudioPin::GetAllocatorRequirements(ALLOCATOR_PROPERTIES* pProps) {
    return E_NOTIMPL;
}
STDMETHODIMP OmniAudioPin::Receive(IMediaSample* pSample) {
    return S_OK; // We're an output pin, we don't receive samples
}

STDMETHODIMP OmniAudioPin::ReceiveMultiple(IMediaSample** pSamples, long nSamples, long* nSamplesProcessed) {
    return S_OK;
}

STDMETHODIMP OmniAudioPin::ReceiveCanBlock() {
    return S_FALSE;
}


STDMETHODIMP OmniAudioPin::SetFormatInternal(const AM_MEDIA_TYPE* pmt) {
    if (pmt->majortype != MEDIATYPE_Audio) return VFW_E_INVALIDMEDIATYPE;
    if (pmt->formattype != FORMAT_WaveFormatEx) return VFW_E_INVALIDMEDIATYPE;
    if (m_connectedPin) {
        return VFW_E_ALREADY_CONNECTED;
    }

    if (m_mediaType.pbFormat) {
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

        WAVEFORMATEX* pwfx = (WAVEFORMATEX*)m_mediaType.pbFormat;
        m_audioChannels = pwfx->nChannels;
        m_audioSampleRate = pwfx->nSamplesPerSec;
    }
    else {
        m_mediaType.cbFormat = 0;
        m_mediaType.pbFormat = NULL;
    }

    return S_OK;
}

STDMETHODIMP OmniAudioPin::SetFormat(AM_MEDIA_TYPE* pmt) {
    DEBUG_LOG_REF()
    if (!pmt) return E_POINTER;
    if (pmt->majortype != MEDIATYPE_Audio) return VFW_E_INVALIDMEDIATYPE;

    CLock lck(m_pFilter->m_cs);
    return SetFormatInternal(pmt);
}

STDMETHODIMP OmniAudioPin::GetFormat(AM_MEDIA_TYPE** ppmt) {
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

static HRESULT doAllocSize(IMemAllocator* pAlloc, ALLOCATOR_PROPERTIES* prop, AM_MEDIA_TYPE* mt)
{

    WAVEFORMATEX* pwfx = (WAVEFORMATEX*)mt->pbFormat;
    if (!pwfx)return E_UNEXPECTED;
    prop->cBuffers = 1;
    prop->cbAlign = 1;
    prop->cbBuffer = pwfx->nBlockAlign * 1024;
    ALLOCATOR_PROPERTIES Actual; memset(&Actual, 0, sizeof(Actual));
    HRESULT hr = pAlloc->SetProperties(prop, &Actual);
    if (FAILED(hr)) return hr;
    if (Actual.cbBuffer < prop->cbBuffer) return E_FAIL;

    return S_OK;
}

STDMETHODIMP OmniAudioPin::DoAllocation() {
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

HRESULT OmniAudioPin::SetCustomFormat(const OmniAudioFormat& format) {
    CLock lck(m_pFilter->m_cs);
    if (m_connectedPin) {
        return VFW_E_ALREADY_CONNECTED;
    }
    WAVEFORMATEX* pwfx = (WAVEFORMATEX*)m_mediaType.pbFormat;
    if (!pwfx) return E_UNEXPECTED;

    pwfx->nSamplesPerSec = format.samplesPerSec;
    pwfx->wBitsPerSample = format.bitsPerSample;
    pwfx->nChannels = format.channels;
    pwfx->nBlockAlign = format.channels * format.bitsPerSample / 8;
    pwfx->nAvgBytesPerSec = format.samplesPerSec * pwfx->nBlockAlign;

    m_mediaType.lSampleSize = pwfx->nBlockAlign;

    m_audioChannels = pwfx->nChannels;
    m_audioSampleRate = pwfx->nSamplesPerSec;
    return S_OK;
}

HRESULT OmniAudioPin::PushSample(BYTE* data, long size, REFERENCE_TIME customStartTime) {
    HRESULT hr = S_OK;
    if (!data || size <= 0) return E_INVALIDARG;
    ResetEvent(m_pFilter->m_noProcess[1]);
    IMemInputPin* pInputPin = m_connectedMemPin;
    IMediaSample* pSample;
    if (!m_allocator) hr = VFW_E_NO_ALLOCATOR;
    if (!m_streaming) hr = VFW_E_WRONG_STATE;
    if(hr != S_OK){
        goto end;
    }

    if (!m_connectedPin || !m_connectedMemPin) {
        hr = S_FALSE;
        goto end;
    }

    if (SUCCEEDED(m_allocator->GetBuffer(&pSample, NULL, NULL, 0))) {
        BYTE* pBuffer;
        if (SUCCEEDED(pSample->GetPointer(&pBuffer))) {
            // Calculate duration based on audio format
            WAVEFORMATEX* pwfx = (WAVEFORMATEX*)m_mediaType.pbFormat;
            if (pwfx && pwfx->nAvgBytesPerSec > 0) {
                long bytesToCopy = min(size, pSample->GetSize());
                memcpy(pBuffer, data, bytesToCopy);
                pSample->SetActualDataLength(bytesToCopy);

                // Calculate timestamps
                REFERENCE_TIME duration = (REFERENCE_TIME)bytesToCopy * 10000000 / pwfx->nAvgBytesPerSec;
                REFERENCE_TIME start;
                if (customStartTime != -1) {
                    start = customStartTime;
                }
                else {
                    start = m_startTime;
                }
                REFERENCE_TIME end = start + duration;
                pSample->SetTime(&start, &end);
                pSample->SetSyncPoint(TRUE);
                m_startTime = end;
                hr = pInputPin->Receive(pSample);
            }
        }
        pSample->Release();
    }
end:
    SetEvent(m_pFilter->m_noProcess[1]);
    return hr;
}

HRESULT OmniAudioPin::Active(BOOL bActive) {
    HRESULT hr;
    if (!m_connectedPin || !m_allocator) return E_FAIL;
    if (bActive) hr = m_allocator->Commit();
    else hr = m_allocator->Decommit();
    return hr;
}

HRESULT OmniAudioPin::Stop() {
    DEBUG_LOG_REF()
    m_streaming = false;
    return S_OK;
}

HRESULT OmniAudioPin::Pause() {
    DEBUG_LOG_REF()
    m_streaming = false;
    return S_OK;
}

HRESULT OmniAudioPin::Continue() {
    DEBUG_LOG_REF()
    m_streaming = true;
    return S_OK;
}

HRESULT OmniAudioPin::Run(REFERENCE_TIME tStart) {
    DEBUG_LOG_REF()
    m_streaming = true;
    m_startTime = tStart;
    return S_OK;
}



STDMETHODIMP OmniAudioPin::EnumMediaTypes(IEnumMediaTypes** ppEnum) {
    if (!ppEnum) return E_POINTER;

    const int totalFormats = GetAudioSupportedFormatCount();
    AM_MEDIA_TYPE** ppMediaTypes = (AM_MEDIA_TYPE**)CoTaskMemAlloc(totalFormats * sizeof(AM_MEDIA_TYPE*));
    if (!ppMediaTypes) return E_OUTOFMEMORY;

    ZeroMemory(ppMediaTypes, totalFormats * sizeof(AM_MEDIA_TYPE*));

    bool success = true;
    for (int i = 0; i < totalFormats; i++) {
        FormatManager::AudioFormat format;
        if (SUCCEEDED(GetAudioSupportedFormatByIndex(i, &format))) {
            ppMediaTypes[i] = formatManager.CreateAudioMediaType(format);
            if (!ppMediaTypes[i]) {
                success = false;
                break;
            }
        }
        else {
            success = false;
            break;
        }
    }

    if (!success) {
        // 清理已分配的内存
        for (int i = 0; i < totalFormats; i++) {
            if (ppMediaTypes[i]) {
                if (ppMediaTypes[i]->pbFormat) {
                    CoTaskMemFree(ppMediaTypes[i]->pbFormat);
                }
                CoTaskMemFree(ppMediaTypes[i]);
            }
        }
        CoTaskMemFree(ppMediaTypes);
        return E_OUTOFMEMORY;
    }

    *ppEnum = new OmniMediaTypeEnum(ppMediaTypes, totalFormats);
    if (!*ppEnum) {
        // 清理内存
        for (int i = 0; i < totalFormats; i++) {
            if (ppMediaTypes[i]) {
                if (ppMediaTypes[i]->pbFormat) {
                    CoTaskMemFree(ppMediaTypes[i]->pbFormat);
                }
                CoTaskMemFree(ppMediaTypes[i]);
            }
        }
        CoTaskMemFree(ppMediaTypes);
        return E_OUTOFMEMORY;
    }

    return S_OK;
}

STDMETHODIMP OmniAudioPin::GetNumberOfCapabilities(int* piCount, int* piSize) {
    if (!piCount || !piSize) return E_POINTER;

    *piCount = GetAudioSupportedFormatCount();
    *piSize = sizeof(AUDIO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP OmniAudioPin::GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) {
    if (!ppmt || !pSCC) return E_POINTER;

    FormatManager::AudioFormat format;
    HRESULT hr = GetAudioSupportedFormatByIndex(iIndex, &format);
    if (FAILED(hr)) return hr;

    *ppmt = formatManager.CreateAudioMediaType(format);
    if (!*ppmt) return E_OUTOFMEMORY;

    // 填充音频能力信息
    AUDIO_STREAM_CONFIG_CAPS* pCaps = (AUDIO_STREAM_CONFIG_CAPS*)pSCC;
    ZeroMemory(pCaps, sizeof(AUDIO_STREAM_CONFIG_CAPS));

    if ((*ppmt)->formattype == FORMAT_WaveFormatEx && (*ppmt)->pbFormat) {
        WAVEFORMATEX* pwfx = (WAVEFORMATEX*)(*ppmt)->pbFormat;

        pCaps->MinimumChannels = pwfx->nChannels;
        pCaps->MaximumChannels = pwfx->nChannels;
        pCaps->ChannelsGranularity = 0;

        pCaps->MinimumBitsPerSample = pwfx->wBitsPerSample;
        pCaps->MaximumBitsPerSample = pwfx->wBitsPerSample;
        pCaps->BitsPerSampleGranularity = 0;

        pCaps->MinimumSampleFrequency = pwfx->nSamplesPerSec;
        pCaps->MaximumSampleFrequency = pwfx->nSamplesPerSec;
        pCaps->SampleFrequencyGranularity = 0;
    }

    return S_OK;
}

STDMETHODIMP OmniAudioPin::Set(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
    DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData) {
    return E_NOTIMPL;
}

STDMETHODIMP OmniAudioPin::Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
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

STDMETHODIMP OmniAudioPin::QuerySupported(REFGUID guidPropSet, DWORD dwPropID,
    DWORD* pTypeSupport) {
    if (guidPropSet != AMPROPSETID_Pin) return E_PROP_SET_UNSUPPORTED;
    if (dwPropID != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    // We support getting this property, but not setting it.
    if (pTypeSupport) *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    return S_OK;
}

