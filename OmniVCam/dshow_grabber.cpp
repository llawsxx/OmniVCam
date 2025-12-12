#include"dshow_grabber.h"

// Return a wide string - allocating memory for it
// Returns:
//    S_OK          - no error
//    E_POINTER     - ppszReturn == NULL
//    E_OUTOFMEMORY - can't allocate memory for returned string
static HRESULT GetWideString(LPCWSTR psz, __deref_out LPWSTR* ppszReturn)
{
	CheckPointer(ppszReturn, E_POINTER);
	ValidateReadWritePtr(ppszReturn, sizeof(LPWSTR));
	*ppszReturn = NULL;
	size_t nameLen;
	HRESULT hr = StringCbLengthW(psz, 100000, &nameLen);
	if (FAILED(hr)) {
		return hr;
	}
	*ppszReturn = (LPWSTR)CoTaskMemAlloc(nameLen + sizeof(WCHAR));
	if (*ppszReturn == NULL) {
		return E_OUTOFMEMORY;
	}
	CopyMemory(*ppszReturn, psz, nameLen + sizeof(WCHAR));
	return NOERROR;
}

DShowGrabber::DShowGrabber() {
	GBDEBUG("DShowGrabber::DShowGrabber()\n");
}

DShowGrabber::DShowGrabber(GrabberCallback callback, void* callback_priv, AM_MEDIA_TYPE* type) {
	GBDEBUG("DShowGrabber::DShowGrabber(GrabberCallback callback, void* callback_priv,AM_MEDIA_TYPE *type)\n");
	m_callback = callback;
	m_callback_priv = callback_priv;
	m_ds_pin = new DShowPin(this, type);
}

DShowGrabber::~DShowGrabber() {
	GBDEBUG("DShowGrabber::~DShowGrabber()\n");
	m_ds_pin->Release();
}




HRESULT STDMETHODCALLTYPE DShowGrabber::GetClassID(CLSID* pClassID)
{
	GBDEBUG("DShowGrabber::GetClassID(CLSID* pClassID)\n");
	return E_NOTIMPL;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::Stop(void)
{
	GBDEBUG("DShowGrabber::Stop(void)\n");
	m_state = State_Stopped;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::Pause(void)
{
	GBDEBUG("DShowGrabber::Pause(void)\n");
	m_state = State_Paused;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::Run(REFERENCE_TIME tStart)
{
	GBDEBUG("DShowGrabber::Run(REFERENCE_TIME tStart)\n");
	m_state = State_Running;
	m_starttime = tStart;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State)
{
	GBDEBUG("DShowGrabber::GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State)\n");
	if (!State) return E_POINTER;
	*State = m_state;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::SetSyncSource(IReferenceClock* pClock)
{
	GBDEBUG("DShowGrabber::SetSyncSource(IReferenceClock* pClock)\n");
	if (m_clock != pClock) {
		if (m_clock)
			m_clock->Release();
		m_clock = pClock;
		if (pClock)
			pClock->AddRef();
	}
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::GetSyncSource(IReferenceClock** pClock)
{
	GBDEBUG("DShowGrabber::GetSyncSource(IReferenceClock** pClock)\n");
	if (!pClock)
		return E_POINTER;
	if (m_clock)
		m_clock->AddRef();
	*pClock = m_clock;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::EnumPins(IEnumPins** ppEnum)
{
	GBDEBUG("DShowGrabber::EnumPins(IEnumPins** ppEnum)\n");
	DShowEnumPins* _new;
	HRESULT hr;
	if (!ppEnum)
		return E_POINTER;
	CComPtr<IPin> pin;
	hr = m_ds_pin->QueryInterface(IID_PPV_ARGS(&pin));
	if (hr != S_OK) return hr;
	_new = new DShowEnumPins(pin.p);
	if (!_new)
		return E_OUTOFMEMORY;

	*ppEnum = (IEnumPins*)_new;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::FindPin(LPCWSTR Id, IPin** ppPin)
{
	GBDEBUG("DShowGrabber::FindPin(LPCWSTR Id, IPin** ppPin)\n");
	IPin* pin;
	HRESULT hr;
	if (!Id || !ppPin)
		return E_POINTER;
	if (!wcscmp(Id, L"In")) {
		hr = m_ds_pin->QueryInterface(IID_PPV_ARGS(&pin));
		if (hr == S_OK) {
			*ppPin = pin;
			return S_OK;
		}
	}
	return VFW_E_NOT_FOUND;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::QueryFilterInfo(FILTER_INFO* pInfo)
{
	GBDEBUG("DShowGrabber::QueryFilterInfo(FILTER_INFO* pInfo)\n");
	if (!pInfo)
		return E_POINTER;
	if (m_info.pGraph)
		m_info.pGraph->AddRef();
	*pInfo = m_info;

	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName)
{
	GBDEBUG("DShowGrabber::JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName)\n");
	m_info.pGraph = pGraph;
	if (pName)
		wcscpy_s(m_info.achName, _countof(m_info.achName), pName);

	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::QueryVendorInfo(LPWSTR* pVendorInfo)
{
	GBDEBUG("DShowGrabber::QueryVendorInfo(LPWSTR* pVendorInfo)\n");
	if (!pVendorInfo) return E_POINTER;
	return E_NOTIMPL;
}



DShowEnumMediaTypes::DShowEnumMediaTypes(AM_MEDIA_TYPE* type) {
	GBDEBUG("DShowEnumMediaTypes::DShowEnumMediaTypes(AM_MEDIA_TYPE *type)\n");
	if (!type)
		m_type.majortype = GUID_NULL;
	else
		copy_media_type(&m_type, type);
}

DShowEnumMediaTypes::~DShowEnumMediaTypes() {
	GBDEBUG("DShowEnumMediaTypes::~DShowEnumMediaTypes()\n");
	free_media_type(m_type);
}

HRESULT STDMETHODCALLTYPE DShowEnumMediaTypes::Next(
	/* [in] */ ULONG cMediaTypes,
	/* [annotation][size_is][out] */
	_Out_writes_to_(cMediaTypes, *pcFetched)  AM_MEDIA_TYPE** ppMediaTypes,
	/* [annotation][out] */
	_Out_opt_  ULONG* pcFetched) {
	GBDEBUG("DShowGrabber::Next()\n");
	int count = 0;
	if (!ppMediaTypes)
		return E_POINTER;
	if (!m_pos && cMediaTypes == 1) {
		if (!IsEqualGUID(m_type.majortype, GUID_NULL)) {
			AM_MEDIA_TYPE* type = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
			if (!type)
				return E_OUTOFMEMORY;
			copy_media_type(type, &m_type);
			*ppMediaTypes = type;
			count = 1;
		}
		m_pos = 1;
	}
	if (pcFetched)
		*pcFetched = count;
	if (!count)
		return S_FALSE;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowEnumMediaTypes::Skip(
	/* [in] */ ULONG cMediaTypes) {
	GBDEBUG("DShowEnumMediaTypes::Skip(ULONG cMediaTypes)\n");
	if (cMediaTypes) return S_FALSE;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowEnumMediaTypes::Reset(void) {
	GBDEBUG("DShowEnumMediaTypes::Reset(void)\n");
	m_pos = 0;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowEnumMediaTypes::Clone(
	/* [annotation][out] */
	_Out_  IEnumMediaTypes** ppEnum) {
	GBDEBUG("DShowEnumMediaTypes::Clone(IEnumMediaTypes **ppEnum)\n");
	DShowEnumMediaTypes* _new;
	if (!ppEnum)
		return E_POINTER;
	_new = new DShowEnumMediaTypes(&m_type);
	if (!_new)
		return E_OUTOFMEMORY;
	_new->m_pos = m_pos;
	*ppEnum = _new;
	return S_OK;
}


DShowPin::DShowPin(DShowGrabber* grabber, AM_MEDIA_TYPE* type)
{
	GBDEBUG("DShowPin::DShowPin(AM_MEDIA_TYPE* type)\n");
	if (type != NULL)
		copy_media_type(&m_type, type);
	else
		m_type.majortype = GUID_NULL;

	m_grabber = grabber;
}

DShowPin::DShowPin() {
	GBDEBUG("DShowPin::DShowPin()\n");
	m_type.majortype = GUID_NULL;
}

DShowPin::~DShowPin()
{
	GBDEBUG("DShowPin::~DShowPin %p\n",this);
	free_media_type(m_type);
}


HRESULT STDMETHODCALLTYPE DShowPin::Connect(
	/* [in] */ IPin* pReceivePin,
	/* [annotation][in] */
	_In_opt_  const AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::Connect(IPin *pReceivePin, const AM_MEDIA_TYPE *pmt)\n");
	return S_FALSE;
}

HRESULT STDMETHODCALLTYPE DShowPin::ReceiveConnection(
	/* [in] */ IPin* pConnector,
	/* [in] */ const AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::ReceiveConnection(IPin *pConnector,const AM_MEDIA_TYPE *pmt)\n");
	if (m_connectedto)
		return VFW_E_ALREADY_CONNECTED;

	if (!pConnector)
		return E_POINTER;

	pConnector->AddRef();
	m_connectedto = pConnector;
	copy_media_type(&m_type, pmt);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::Disconnect(void) {
	GBDEBUG("DShowPin::Disconnect(void)\n");
	if (m_grabber->m_state != State_Stopped)
		return VFW_E_NOT_STOPPED;
	if (!m_connectedto)
		return S_FALSE;

	m_connectedto->Release();
	m_connectedto = NULL;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::ConnectedTo(
	/* [annotation][out] */
	_Out_  IPin** pPin) {
	GBDEBUG("DShowPin::ConnectedTo(IPin** pPin)\n");
	if (!pPin)
		return E_POINTER;
	if (!m_connectedto)
		return VFW_E_NOT_CONNECTED;
	m_connectedto->AddRef();
	*pPin = m_connectedto;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::ConnectionMediaType(
	/* [annotation][out] */
	_Out_  AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::ConnectionMediaType(AM_MEDIA_TYPE* pmt)\n");
	if (!m_connectedto)
		return VFW_E_NOT_CONNECTED;

	return copy_media_type(pmt, &m_type);
}

HRESULT STDMETHODCALLTYPE DShowPin::QueryPinInfo(
	/* [annotation][out] */
	_Out_  PIN_INFO* pInfo) {
	GBDEBUG("DShowPin::QueryPinInfo(PIN_INFO* pInfo)\n");
	if (!pInfo)
		return E_POINTER;

	if (m_grabber) {
		m_grabber->AddRef();
		pInfo->pFilter = m_grabber;
	}
	pInfo->dir = PINDIR_INPUT;
	wcscpy_s(pInfo->achName, _countof(pInfo->achName), L"Capture");

	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::QueryDirection(
	/* [annotation][out] */
	_Out_  PIN_DIRECTION* pPinDir) {
	GBDEBUG("DShowPin::QueryDirection(PIN_DIRECTION* pPinDir)\n");
	if (!pPinDir)
		return E_POINTER;
	*pPinDir = PINDIR_INPUT;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::QueryId(
	/* [annotation][out] */
	_Out_  LPWSTR* Id) {
	GBDEBUG("DShowPin::QueryId(LPWSTR* Id)\n");
	return GetWideString(L"libAV Pin", Id);
}

HRESULT STDMETHODCALLTYPE DShowPin::QueryAccept(
	/* [in] */ const AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::QueryAccept(const AM_MEDIA_TYPE* pmt)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::EnumMediaTypes(
	/* [annotation][out] */
	_Out_  IEnumMediaTypes** ppEnum) {
	GBDEBUG("DShowPin::EnumMediaTypes(IEnumMediaTypes** ppEnum)\n");
	DShowEnumMediaTypes* _new;
	if (!ppEnum)
		return E_POINTER;
	_new = new DShowEnumMediaTypes(&m_type);
	if (!_new)
		return E_OUTOFMEMORY;
	*ppEnum = _new;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::QueryInternalConnections(
	/* [annotation][out] */
	_Out_writes_to_opt_(*nPin, *nPin)  IPin** apPin,
	/* [out][in] */ ULONG* nPin) {
	GBDEBUG("DShowPin::QueryInternalConnections(IPin** apPin, ULONG * nPin)\n");
	return E_NOTIMPL;
}

HRESULT STDMETHODCALLTYPE DShowPin::EndOfStream(void) {
	GBDEBUG("DShowPin::EndOfStream(void)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::BeginFlush(void) {
	GBDEBUG("DShowPin::BeginFlush(void)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::EndFlush(void) {
	GBDEBUG("DShowPin::EndFlush(void)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::NewSegment(
	/* [in] */ REFERENCE_TIME tStart,
	/* [in] */ REFERENCE_TIME tStop,
	/* [in] */ double dRate) {
	GBDEBUG("DShowPin::NewSegment(REFERENCE_TIME tStart, REFERENCE_TIME tStop, double dRate)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::GetAllocator(
	/* [annotation][out] */
	_Out_  IMemAllocator** ppAllocator) {
	GBDEBUG("DShowPin::GetAllocator(IMemAllocator** ppAllocator)\n");
	return VFW_E_NO_ALLOCATOR;
}

HRESULT STDMETHODCALLTYPE DShowPin::NotifyAllocator(
	/* [in] */ IMemAllocator* pAllocator,
	/* [in] */ BOOL bReadOnly) {
	GBDEBUG("DShowPin::NotifyAllocator(IMemAllocator* pAllocator, BOOL bReadOnly)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::GetAllocatorRequirements(
	/* [annotation][out] */
	_Out_  ALLOCATOR_PROPERTIES* pProps) {
	GBDEBUG("DShowPin::GetAllocatorRequirements(ALLOCATOR_PROPERTIES* pProps)\n");
	return E_NOTIMPL;
}

HRESULT STDMETHODCALLTYPE DShowPin::Receive(
	/* [in] */ IMediaSample* pSample) {
	GBDEBUG("DShowPin::Receive(IMediaSample* pSample)\n");
	if (m_grabber->m_state != State_Running) return VFW_E_NOT_RUNNING;
	if (m_grabber->m_callback)
		m_grabber->m_callback(m_grabber->m_callback_priv, pSample);
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowPin::ReceiveMultiple(
	/* [annotation][size_is][in] */
	_In_reads_(nSamples)  IMediaSample** pSamples,
	/* [in] */ long nSamples,
	/* [annotation][out] */
	_Out_  long* nSamplesProcessed) {
	GBDEBUG("DShowPin::ReceiveMultiple(IMediaSample** pSamples, long* nSamplesProcessed)\n");
	if (m_grabber->m_state != State_Running) return VFW_E_NOT_RUNNING;
	for (int i = 0; i < nSamples; i++)
		Receive(pSamples[i]);
	*nSamplesProcessed = nSamples;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::ReceiveCanBlock(void) {
	GBDEBUG("DShowPin::ReceiveCanBlock(void)\n");
	return S_FALSE;
}

DShowEnumPins::DShowEnumPins(IPin* pin) {
	GBDEBUG("DShowEnumPins::DShowEnumPins(IPin* pin)\n");
	pin->AddRef();
	m_pin = pin;
}

DShowEnumPins::~DShowEnumPins() {
	GBDEBUG("DShowEnumPins::~DShowEnumPins()\n");
	m_pin->Release();
}

HRESULT STDMETHODCALLTYPE DShowEnumPins::Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched)
{
	GBDEBUG("DShowEnumPins::Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched)\n");
	int count = 0;
	if (!ppPins)
		return E_POINTER;
	if (!m_pos && cPins == 1) {
		m_pin->AddRef();
		*ppPins = m_pin;
		count = 1;
		m_pos = 1;
	}
	if (pcFetched)
		*pcFetched = count;
	if (!count)
		return S_FALSE;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowEnumPins::Skip(ULONG cPins)
{
	GBDEBUG("DShowEnumPins::Skip(ULONG cPins)\n");
	if (cPins) return S_FALSE;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowEnumPins::Reset(void)
{
	GBDEBUG("DShowEnumPins::Reset(void)\n");
	m_pos = 0;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowEnumPins::Clone(IEnumPins** ppEnum)
{
	GBDEBUG("DShowEnumPins::Clone(IEnumPins** ppEnum)\n");
	DShowEnumPins* _new;
	if (!ppEnum)
		return E_POINTER;

	_new = new DShowEnumPins(this->m_pin);
	if (!_new)
		return E_OUTOFMEMORY;

	*ppEnum = (IEnumPins*)_new;
	return S_OK;
}


//int main12() {
//    while (1) {
//        auto am = CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
//        ZeroMemory(am,sizeof(AM_MEDIA_TYPE));
//        auto a = new DShowGrabber(NULL, NULL, (AM_MEDIA_TYPE*)am);
//        LPWSTR id;
//        IEnumPins* f;
//        IPin* p;
//        IMemInputPin* ip;
//        PIN_INFO info = {0};
//        IPin* p2;
//        ULONG fetched;
//        auto b = a->EnumPins(&f);
//        while (f->Next(1, &p, &fetched) == S_OK) {
//            printf("OK\n");
//        }
//        p->QueryId(&id);
//        p->QueryInterface(&ip);
//        p->QueryPinInfo(&info);
//        a->FindPin(L"In", &p2);
//
//        p->Release();
//        f->Release();
//        a->Release();
//        ip->Release();
//        info.pFilter->Release();
//        CoTaskMemFree(am);
//        CoTaskMemFree(id);
//        p2->Release();
//        //break;
//    }
//}

