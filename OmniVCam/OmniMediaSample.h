#pragma once
#include <windows.h>
#include <strmif.h>
#include "OmniVCam.h"
class OmniMediaSample : public IMediaSample, public DShowBase
{
private:
    LONG m_refCount;                    // 引用计数
    BYTE* m_pBuffer;                // 数据缓冲区
    LONG m_cbBuffer;                // 缓冲区大小
    LONG m_cbActualData;            // 实际数据长度
    REFERENCE_TIME m_StartTime;     // 开始时间
    REFERENCE_TIME m_EndTime;       // 结束时间
    BOOL m_bSyncPoint;              // 是否关键帧
    BOOL m_bPreroll;                // 是否预卷
    BOOL m_bDiscontinuity;          // 是否不连续
    BOOL m_bMediaTimeValid;         // 媒体时间是否有效
    LONGLONG m_MediaStart;          // 媒体开始时间
    LONGLONG m_MediaEnd;            // 媒体结束时间
    AM_MEDIA_TYPE* m_pMediaType;    // 媒体类型
    AVFrame* m_frameRef;
public:
    // 构造函数
    OmniMediaSample(BYTE* pBuffer, LONG cbBuffer, AVFrame* frame);

    virtual ~OmniMediaSample();

    // IUnknown接口
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMediaSample接口
    STDMETHODIMP GetPointer(BYTE** ppBuffer) override;
    STDMETHODIMP_(LONG) GetSize() override;
    STDMETHODIMP GetTime(REFERENCE_TIME* pTimeStart, REFERENCE_TIME* pTimeEnd) override;
    STDMETHODIMP SetTime(REFERENCE_TIME* pTimeStart, REFERENCE_TIME* pTimeEnd) override;
    STDMETHODIMP IsSyncPoint() override;
    STDMETHODIMP SetSyncPoint(BOOL bIsSyncPoint) override;
    STDMETHODIMP IsPreroll() override;
    STDMETHODIMP SetPreroll(BOOL bIsPreroll) override;
    STDMETHODIMP GetActualDataLength() override;
    STDMETHODIMP SetActualDataLength(LONG lActualDataLength) override;
    STDMETHODIMP GetMediaType(AM_MEDIA_TYPE** ppMediaType) override;
    STDMETHODIMP SetMediaType(AM_MEDIA_TYPE* pMediaType) override;
    STDMETHODIMP IsDiscontinuity() override;
    STDMETHODIMP SetDiscontinuity(BOOL bDiscontinuity) override;
    STDMETHODIMP GetMediaTime(LONGLONG* pTimeStart, LONGLONG* pTimeEnd) override;
    STDMETHODIMP SetMediaTime(LONGLONG* pTimeStart, LONGLONG* pTimeEnd) override;
};
