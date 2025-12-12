#pragma once
#include "OmniVCam.h"

class OmniAudioPin : public IPin, public IMemInputPin, public IAMStreamConfig, public IKsPropertySet, public DShowBase {
public:
    OmniAudioPin(OmniVCam* pFilter);
    ~OmniAudioPin();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IPin
    STDMETHODIMP Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP Disconnect() override;
    STDMETHODIMP ConnectedTo(IPin** pPin) override;
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP QueryPinInfo(PIN_INFO* pInfo) override;
    STDMETHODIMP QueryDirection(PIN_DIRECTION* pPinDir) override;
    STDMETHODIMP QueryId(LPWSTR* Id) override;
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** ppEnum) override;
    STDMETHODIMP QueryInternalConnections(IPin** apPin, ULONG* nPin) override;
    STDMETHODIMP EndOfStream() override;
    STDMETHODIMP BeginFlush() override;
    STDMETHODIMP EndFlush() override;
    STDMETHODIMP NewSegment(REFERENCE_TIME tStart, REFERENCE_TIME tStop, double dRate) override;

    // IMemInputPin
    STDMETHODIMP GetAllocator(IMemAllocator** ppAllocator) override;
    STDMETHODIMP NotifyAllocator(IMemAllocator* pAllocator, BOOL bReadOnly) override;
    STDMETHODIMP GetAllocatorRequirements(ALLOCATOR_PROPERTIES* pProps) override;
    STDMETHODIMP Receive(IMediaSample* pSample) override;
    STDMETHODIMP ReceiveMultiple(IMediaSample** pSamples, long nSamples, long* nSamplesProcessed) override;
    STDMETHODIMP ReceiveCanBlock() override;

    STDMETHODIMP DoAllocation();

    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP SetFormatInternal(const AM_MEDIA_TYPE* pmt);
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** ppmt) override;
    STDMETHODIMP GetNumberOfCapabilities(int* piCount, int* piSize) override;
    STDMETHODIMP GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC) override;

    STDMETHODIMP Set(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
        DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData) override;

    STDMETHODIMP Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData,
        DWORD cbInstanceData, LPVOID pPropData, DWORD cbPropData,
        DWORD* pcbReturned) override;

    STDMETHODIMP QuerySupported(REFGUID guidPropSet, DWORD dwPropID,
        DWORD* pTypeSupport) override;

    HRESULT SetCustomFormat(const OmniAudioFormat& format);
    HRESULT PushSample(BYTE* data, long size, REFERENCE_TIME customStartTime);
    HRESULT Stop();
    HRESULT Pause();
    HRESULT Continue();
    HRESULT Run(REFERENCE_TIME tStart);

    HRESULT Active(BOOL bActive);
public:
    int m_audioChannels;
    int m_audioSampleRate;
    AVSampleFormat m_audioFormat;
private:
    OmniVCam* m_pFilter;
    volatile long m_refCount;
    IPin* m_connectedPin;
    IMemInputPin * m_connectedMemPin;
    IMemAllocator* m_allocator;
    AM_MEDIA_TYPE m_mediaType;
    volatile bool m_streaming;
    REFERENCE_TIME m_startTime;
    void InitMediaType();
};