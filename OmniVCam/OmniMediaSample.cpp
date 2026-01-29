#include "OmniMediaSample.h"

OmniMediaSample::OmniMediaSample(BYTE* pBuffer, LONG cbBuffer, AVFrame *frame)
    : m_refCount(1)
    , m_cbActualData(0)
    , m_StartTime(0)
    , m_EndTime(0)
    , m_bSyncPoint(FALSE)
    , m_bPreroll(FALSE)
    , m_bDiscontinuity(FALSE)
    , m_bMediaTimeValid(FALSE)
    , m_MediaStart(0)
    , m_MediaEnd(0)
    , m_pMediaType(nullptr)
{
    m_frameRef = av_frame_clone(frame);
    if (m_frameRef) {
        m_pBuffer = pBuffer;
        m_cbBuffer = cbBuffer;
        if (pBuffer && cbBuffer > 0) {
            m_cbActualData = cbBuffer;
        }
    }
    else {
        m_pBuffer = nullptr;
        m_cbBuffer = 0;
    }
}

OmniMediaSample::~OmniMediaSample()
{
    // 释放媒体类型
    if (m_pMediaType) {

        if (m_pMediaType->pbFormat) {
            CoTaskMemFree(m_pMediaType->pbFormat);
            m_pMediaType->pbFormat = nullptr;
        }

        if (m_pMediaType->pUnk) {
            m_pMediaType->pUnk->Release();
            m_pMediaType->pUnk = nullptr;
        }
        CoTaskMemFree(m_pMediaType);
        m_pMediaType = nullptr;
    }
    if (m_frameRef) {
        av_frame_free(&m_frameRef);
    }
}

// IUnknown接口实现
STDMETHODIMP OmniMediaSample::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr) {
        return E_POINTER;
    }

    if (riid == IID_IUnknown || riid == IID_IMediaSample) {
        *ppv = static_cast<IMediaSample*>(this);
        AddRef();
        return S_OK;
    }

    *ppv = nullptr;
    return E_NOINTERFACE;
}

STDMETHODIMP_(ULONG) OmniMediaSample::AddRef()
{
    return InterlockedIncrement(&m_refCount);
}

STDMETHODIMP_(ULONG) OmniMediaSample::Release()
{
    LONG cRef = InterlockedDecrement(&m_refCount);
    if (cRef == 0) {
        delete this;
        return 0;
    }
    return cRef;
}

// IMediaSample接口实现
STDMETHODIMP OmniMediaSample::GetPointer(BYTE** ppBuffer)
{
    if (ppBuffer == nullptr) {
        return E_POINTER;
    }

    *ppBuffer = m_pBuffer;
    return m_pBuffer ? S_OK : E_FAIL;
}

STDMETHODIMP_(LONG) OmniMediaSample::GetSize()
{
    return m_cbBuffer;
}

STDMETHODIMP OmniMediaSample::GetTime(REFERENCE_TIME* pTimeStart, REFERENCE_TIME* pTimeEnd)
{
    if (pTimeStart) {
        *pTimeStart = m_StartTime;
    }

    if (pTimeEnd) {
        *pTimeEnd = m_EndTime;
    }

    // 如果设置了时间，返回S_OK；否则返回VFW_E_SAMPLE_TIME_NOT_SET
    return (m_StartTime != 0 || m_EndTime != 0) ? S_OK : VFW_E_SAMPLE_TIME_NOT_SET;
}

STDMETHODIMP OmniMediaSample::SetTime(REFERENCE_TIME* pTimeStart, REFERENCE_TIME* pTimeEnd)
{
    if (pTimeStart) {
        m_StartTime = *pTimeStart;
    }

    if (pTimeEnd) {
        m_EndTime = *pTimeEnd;
    }

    return S_OK;
}

STDMETHODIMP OmniMediaSample::IsSyncPoint()
{
    return m_bSyncPoint ? S_OK : S_FALSE;
}

STDMETHODIMP OmniMediaSample::SetSyncPoint(BOOL bIsSyncPoint)
{
    m_bSyncPoint = bIsSyncPoint;
    return S_OK;
}

STDMETHODIMP OmniMediaSample::IsPreroll()
{
    return m_bPreroll ? S_OK : S_FALSE;
}

STDMETHODIMP OmniMediaSample::SetPreroll(BOOL bIsPreroll)
{
    m_bPreroll = bIsPreroll;
    return S_OK;
}

STDMETHODIMP OmniMediaSample::GetActualDataLength()
{
    return m_cbActualData;
}

STDMETHODIMP OmniMediaSample::SetActualDataLength(LONG lActualDataLength)
{
    if (lActualDataLength < 0 || lActualDataLength > m_cbBuffer) {
        return VFW_E_BUFFER_OVERFLOW;
    }

    m_cbActualData = lActualDataLength;
    return S_OK;
}

STDMETHODIMP OmniMediaSample::GetMediaType(AM_MEDIA_TYPE** ppMediaType)
{
    if (ppMediaType == nullptr) {
        return E_POINTER;
    }

    *ppMediaType = nullptr;

    if (m_pMediaType == nullptr) {
        return S_FALSE;
    }

    // 复制媒体类型
    *ppMediaType = _CreateMediaType(m_pMediaType);
    if (*ppMediaType == nullptr) {
        return E_OUTOFMEMORY;
    }

    return S_OK;
}

STDMETHODIMP OmniMediaSample::SetMediaType(AM_MEDIA_TYPE* pMediaType)
{
    // 释放旧的媒体类型
    if (m_pMediaType->pbFormat) {
        CoTaskMemFree(m_pMediaType->pbFormat);
        m_pMediaType->pbFormat = nullptr;
    }

    if (m_pMediaType->pUnk) {
        m_pMediaType->pUnk->Release();
        m_pMediaType->pUnk = nullptr;
    }

    CoTaskMemFree(m_pMediaType);
    m_pMediaType = nullptr;

    // 复制新的媒体类型
    if (pMediaType) {
        m_pMediaType = _CreateMediaType(pMediaType);
        if (m_pMediaType == nullptr) {
            return E_OUTOFMEMORY;
        }
    }
    return S_OK;
}

STDMETHODIMP OmniMediaSample::IsDiscontinuity()
{
    return m_bDiscontinuity ? S_OK : S_FALSE;
}

STDMETHODIMP OmniMediaSample::SetDiscontinuity(BOOL bDiscontinuity)
{
    m_bDiscontinuity = bDiscontinuity;
    return S_OK;
}

STDMETHODIMP OmniMediaSample::GetMediaTime(LONGLONG* pTimeStart, LONGLONG* pTimeEnd)
{
    if (!m_bMediaTimeValid) {
        return VFW_E_MEDIA_TIME_NOT_SET;
    }

    if (pTimeStart) {
        *pTimeStart = m_MediaStart;
    }

    if (pTimeEnd) {
        *pTimeEnd = m_MediaEnd;
    }

    return S_OK;
}

STDMETHODIMP OmniMediaSample::SetMediaTime(LONGLONG* pTimeStart, LONGLONG* pTimeEnd)
{
    if (pTimeStart && pTimeEnd) {
        m_MediaStart = *pTimeStart;
        m_MediaEnd = *pTimeEnd;
        m_bMediaTimeValid = TRUE;
    }
    else {
        m_bMediaTimeValid = FALSE;
    }

    return S_OK;
}