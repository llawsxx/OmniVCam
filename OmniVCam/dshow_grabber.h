#pragma once
#include<dshow.h>
#include<streams.h>
#include<atlbase.h>
#ifdef  __cplusplus
#define DllExport  extern "C" __declspec(dllexport)
#else
#define DllExport  __declspec(dllexport)
#endif //  __cplusplus

#define BR_DEBUG 0
#define GRABBER_DEBUG 1
#define ENCODE_DEBUG 1
#define DSHOW_DEBUG 1
#define PAPHOW_DEBUG 1

#include<stdint.h>
#include<windows.h>

#if GRABBER_DEBUG 
#define GBDEBUG(...) printf(__VA_ARGS__)
#else
#define GBDEBUG(...) ;
#endif
class DShowBase2{

public:
    void* operator new(size_t size) {
        void* p = CoTaskMemAlloc(size);
        if (p) ZeroMemory(p, size);
        return p;
    }

    void operator delete(void* p) {

        // 打印调用栈（Windows）
//#ifdef _WIN32
//        void* stack[10];
//        WORD frames = CaptureStackBackTrace(0, 10, stack, NULL);
//        for (WORD i = 0; i < frames; i++) {
//            GBDEBUG("  [%d] %p\n", i, stack[i]);
//        }
//#endif
        CoTaskMemFree(p);
    }
};

static HRESULT copy_media_type(AM_MEDIA_TYPE* dst, const AM_MEDIA_TYPE* src)
{
    uint8_t* pbFormat = NULL;

    if (src->cbFormat) {
        pbFormat = (uint8_t*)CoTaskMemAlloc(src->cbFormat);
        if (!pbFormat)
            return E_OUTOFMEMORY;
        memcpy(pbFormat, src->pbFormat, src->cbFormat);
    }

    *dst = *src;
    dst->pUnk = NULL;
    dst->pbFormat = pbFormat;

    return S_OK;
}

static void free_media_type(AM_MEDIA_TYPE& mt)
{
    if (mt.cbFormat != 0)
    {
        CoTaskMemFree((PVOID)mt.pbFormat);
        mt.cbFormat = 0;
        mt.pbFormat = NULL;
    }
    if (mt.pUnk != NULL)
    {
        // pUnk should not be used.
        mt.pUnk->Release();
        mt.pUnk = NULL;
    }
}


// Delete a media type structure that was allocated on the heap.
static void delete_media_type(AM_MEDIA_TYPE* pmt)
{
    if (pmt != NULL)
    {
        free_media_type(*pmt);
        CoTaskMemFree(pmt);
    }
}

#define DECLARE_REF_IMPLEMENT(name) \
private: \
long m_ref = 1; \
public: \
ULONG STDMETHODCALLTYPE AddRef(void) override \
{ \
    GBDEBUG(#name"::AddRef:%d\n",m_ref+1); \
    return InterlockedIncrement(&m_ref); \
} \
ULONG STDMETHODCALLTYPE Release(void) override \
{ \
    long ref = InterlockedDecrement(&this->m_ref); \
    GBDEBUG(#name"::Release:%d\n",ref); \
    if (ref == 0) { \
        delete this; \
    } \
    return ref; \
} 



typedef void (STDMETHODCALLTYPE* GrabberCallback)(void* priv, IMediaSample* sample);
class DShowPin;
class DShowGrabber :public IBaseFilter,public DShowBase2
{
private:
    FILTER_INFO m_info;
    DShowPin* m_ds_pin;
    
public:
    DShowGrabber(GrabberCallback callback, void* callback_priv, AM_MEDIA_TYPE* type);
    DShowGrabber();
    ~DShowGrabber();
    FILTER_STATE m_state;
    GrabberCallback m_callback;
    void* m_callback_priv;
    REFERENCE_TIME m_starttime;
    IReferenceClock* m_clock;
    // 通过 IBaseFilter 继承

    HRESULT STDMETHODCALLTYPE GetClassID(CLSID* pClassID) override;
    HRESULT STDMETHODCALLTYPE Stop(void) override;

    HRESULT STDMETHODCALLTYPE Pause(void) override;

    HRESULT STDMETHODCALLTYPE Run(REFERENCE_TIME tStart) override;

    HRESULT STDMETHODCALLTYPE GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State) override;

    HRESULT STDMETHODCALLTYPE SetSyncSource(IReferenceClock* pClock) override;

    HRESULT STDMETHODCALLTYPE GetSyncSource(IReferenceClock** pClock) override;

    HRESULT STDMETHODCALLTYPE EnumPins(IEnumPins** ppEnum) override;

    HRESULT STDMETHODCALLTYPE FindPin(LPCWSTR Id, IPin** ppPin) override;

    HRESULT STDMETHODCALLTYPE QueryFilterInfo(FILTER_INFO* pInfo) override;

    HRESULT STDMETHODCALLTYPE JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName) override;

    HRESULT STDMETHODCALLTYPE QueryVendorInfo(LPWSTR* pVendorInfo) override;


    //DECLARE_REF_IMPLEMENT(DShowGrabber)

    private: long m_ref = 1; public: ULONG __stdcall AddRef(void) override {
        printf("DShowGrabber""::AddRef:%d\n", m_ref + 1); return _InterlockedIncrement(&m_ref);
    } ULONG __stdcall Release(void) override {
        long ref = _InterlockedDecrement(&this->m_ref); printf("DShowGrabber""::Release:%d\n", ref); if (ref == 0) {
            delete this;
        } return ref;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override 
    {
        GBDEBUG("DShowGrabber::QueryInterface()\n");
        if (NULL == ppvObject) return E_POINTER; 
        if (IsEqualGUID(riid, IID_IUnknown)) {
            AddRef();
            *ppvObject = (IUnknown*)this;
        }
        else if (IsEqualGUID(riid, IID_IBaseFilter)) {
            AddRef();
            *ppvObject = (IBaseFilter*)this;
        }
        else if (riid == IID_IPersist) {
            AddRef();
            *ppvObject = (IPersist*)this;
        }
        else if (riid == IID_IMediaFilter) {
            AddRef();
            *ppvObject = (IMediaFilter*)this;
        }
        else {
            *ppvObject = NULL;
            return E_NOINTERFACE;
        }
        return S_OK;
    }
};


class DShowPin :public IPin, public IMemInputPin,public DShowBase2
{
    DShowGrabber* m_grabber;
    IPin* m_connectedto;
    AM_MEDIA_TYPE m_type;
public:
    DShowPin(DShowGrabber* m_grabber, AM_MEDIA_TYPE* type);
    DShowPin();
    ~DShowPin();
    HRESULT STDMETHODCALLTYPE Connect(
        /* [in] */ IPin* pReceivePin,
        /* [annotation][in] */
        _In_opt_  const AM_MEDIA_TYPE* pmt) override;

    HRESULT STDMETHODCALLTYPE ReceiveConnection(
        /* [in] */ IPin* pConnector,
        /* [in] */ const AM_MEDIA_TYPE* pmt) override;

    HRESULT STDMETHODCALLTYPE Disconnect(void) override;

    HRESULT STDMETHODCALLTYPE ConnectedTo(
        /* [annotation][out] */
        _Out_  IPin** pPin) override;

    HRESULT STDMETHODCALLTYPE ConnectionMediaType(
        /* [annotation][out] */
        _Out_  AM_MEDIA_TYPE* pmt) override;

    HRESULT STDMETHODCALLTYPE QueryPinInfo(
        /* [annotation][out] */
        _Out_  PIN_INFO* pInfo) override;

    HRESULT STDMETHODCALLTYPE QueryDirection(
        /* [annotation][out] */
        _Out_  PIN_DIRECTION* pPinDir) override;
    HRESULT STDMETHODCALLTYPE QueryId(
        /* [annotation][out] */
        _Out_  LPWSTR* Id) override;
    HRESULT STDMETHODCALLTYPE QueryAccept(
        /* [in] */ const AM_MEDIA_TYPE* pmt) override;

    HRESULT STDMETHODCALLTYPE EnumMediaTypes(
        /* [annotation][out] */
        _Out_  IEnumMediaTypes** ppEnum) override;

    HRESULT STDMETHODCALLTYPE QueryInternalConnections(
        /* [annotation][out] */
        _Out_writes_to_opt_(*nPin, *nPin)  IPin** apPin,
        /* [out][in] */ ULONG* nPin) override;

    HRESULT STDMETHODCALLTYPE EndOfStream(void) override;

    HRESULT STDMETHODCALLTYPE BeginFlush(void) override;

    HRESULT STDMETHODCALLTYPE EndFlush(void) override;
    

    HRESULT STDMETHODCALLTYPE NewSegment(
        /* [in] */ REFERENCE_TIME tStart,
        /* [in] */ REFERENCE_TIME tStop,
        /* [in] */ double dRate) override;

    HRESULT STDMETHODCALLTYPE GetAllocator(
        /* [annotation][out] */
        _Out_  IMemAllocator** ppAllocator) override;

    HRESULT STDMETHODCALLTYPE NotifyAllocator(
        /* [in] */ IMemAllocator* pAllocator,
        /* [in] */ BOOL bReadOnly) override;

    HRESULT STDMETHODCALLTYPE GetAllocatorRequirements(
        /* [annotation][out] */
        _Out_  ALLOCATOR_PROPERTIES* pProps) override;

    HRESULT STDMETHODCALLTYPE Receive(
        /* [in] */ IMediaSample* pSample) override;

    HRESULT STDMETHODCALLTYPE ReceiveMultiple(
        /* [annotation][size_is][in] */
        _In_reads_(nSamples)  IMediaSample** pSamples,
        /* [in] */ long nSamples,
        /* [annotation][out] */
        _Out_  long* nSamplesProcessed) override;

    HRESULT STDMETHODCALLTYPE ReceiveCanBlock(void) override;

    //DECLARE_REF_IMPLEMENT(DShowPin)
    private: long m_ref = 1; public: ULONG __stdcall AddRef(void) override {
        printf("DShowPin""::AddRef:%d\n", m_ref + 1); return _InterlockedIncrement(&m_ref);
    } ULONG __stdcall Release(void) override {
        long ref = _InterlockedDecrement(&this->m_ref); printf("DShowPin""::Release:%d\n", ref); if (ref == 0) {
            delete this;
        } return ref;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
    {
        GBDEBUG("DShowPin::QueryInterface()\n");
        if (NULL == ppvObject) return E_POINTER;
        if (IsEqualGUID(riid, IID_IUnknown)) {
            AddRef();
            *ppvObject = (IUnknown*)((IPin*)this);
        }
        else if (IsEqualGUID(riid, IID_IPin)) {
            AddRef();
            *ppvObject = (IPin*)this;
        }
        else if (IsEqualGUID(riid, IID_IMemInputPin)) {
            AddRef();
            *ppvObject = (IMemInputPin*)this;
        }
        else {
            *ppvObject = NULL;
            return E_NOINTERFACE;
        }
        return S_OK;
    }
};

class DShowEnumMediaTypes :public IEnumMediaTypes, public DShowBase2
{
    int m_pos = 0;
    AM_MEDIA_TYPE m_type;

public:
    DShowEnumMediaTypes(AM_MEDIA_TYPE* type);
    ~DShowEnumMediaTypes();

    HRESULT STDMETHODCALLTYPE Next(
        /* [in] */ ULONG cMediaTypes,
        /* [annotation][size_is][out] */
        _Out_writes_to_(cMediaTypes, *pcFetched)  AM_MEDIA_TYPE** ppMediaTypes,
        /* [annotation][out] */
        _Out_opt_  ULONG* pcFetched);

    HRESULT STDMETHODCALLTYPE Skip(
        /* [in] */ ULONG cMediaTypes);

    HRESULT STDMETHODCALLTYPE Reset(void);

    HRESULT STDMETHODCALLTYPE Clone(
        /* [annotation][out] */
        _Out_  IEnumMediaTypes** ppEnum);

    //DECLARE_REF_IMPLEMENT(DShowEnumMediaTypes)
    private: long m_ref = 1; public: ULONG __stdcall AddRef(void) override {
        printf("DShowEnumMediaTypes""::AddRef:%d\n", m_ref + 1); return _InterlockedIncrement(&m_ref);
    } ULONG __stdcall Release(void) override {
        long ref = _InterlockedDecrement(&this->m_ref); printf("DShowEnumMediaTypes""::Release:%d\n", ref); if (ref == 0) {
            delete this;
        } return ref;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
    {
        GBDEBUG("DShowEnumMediaTypes::QueryInterface()\n");
        if (NULL == ppvObject) return E_POINTER;
        if (IsEqualGUID(riid, IID_IUnknown)) {
            AddRef();
            *ppvObject = (IUnknown*)this;
        }
        else if (IsEqualGUID(riid, IID_IEnumMediaTypes)) {
            AddRef();
            *ppvObject = (IEnumMediaTypes*)this;
        }
        else {
            *ppvObject = NULL;
            return E_NOINTERFACE;
        }
        return S_OK;
    }
};

class DShowEnumPins :public IEnumPins
{
    int m_pos = 0;
    // 通过 IEnumPins 继承
    IPin* m_pin;

public:
    DShowEnumPins(IPin* pin);
    ~DShowEnumPins();

    HRESULT STDMETHODCALLTYPE Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched) override;
    HRESULT STDMETHODCALLTYPE Skip(ULONG cPins) override;
    HRESULT STDMETHODCALLTYPE Reset(void) override;
    HRESULT STDMETHODCALLTYPE Clone(IEnumPins** ppEnum) override;
    //DECLARE_REF_IMPLEMENT(DShowEnumPins)
    private: long m_ref = 1; public: ULONG __stdcall AddRef(void) override {
        printf("DShowEnumPins""::AddRef:%d\n", m_ref + 1); return _InterlockedIncrement(&m_ref);
    } ULONG __stdcall Release(void) override {
        long ref = _InterlockedDecrement(&this->m_ref); printf("DShowEnumPins""::Release:%d\n", ref); if (ref == 0) {
            delete this;
        } return ref;
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
    {
        GBDEBUG("DShowEnumPins::QueryInterface()\n");
        if (NULL == ppvObject) return E_POINTER;
        if (IsEqualGUID(riid, IID_IUnknown)) {
            AddRef();
            *ppvObject = (IUnknown*)this;
        }
        else if (IsEqualGUID(riid, IID_IEnumPins)) {
            AddRef();
            *ppvObject = (IEnumPins*)this;
        }
        else {
            *ppvObject = NULL;
            return E_NOINTERFACE;
        }
        return S_OK;
    }

};

