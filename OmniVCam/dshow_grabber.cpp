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

DShowGrabber::DShowGrabber(GrabberCallback callback, void* callback_priv, AM_MEDIA_TYPE* type) {
	GBDEBUG("DShowGrabber::DShowGrabber(GrabberCallback callback, void* callback_priv,AM_MEDIA_TYPE *type)\n");
	ZeroMemory(&m_info, sizeof(m_info));
	m_state = State_Stopped;
	m_callback = callback;
	m_callback_priv = callback_priv;
	m_starttime = 0;
	m_clock = NULL;
	InitializeCriticalSection(&m_receive_mutex);
	InitializeConditionVariable(&m_receive_cond);
	m_receive_count = 0;
	m_ds_pin = new DShowPin(this, type);
}

DShowGrabber::~DShowGrabber() {
	GBDEBUG("DShowGrabber::~DShowGrabber()\n");
	if (m_clock) m_clock->Release();
	m_clock = NULL;
	delete m_ds_pin;
	m_ds_pin = NULL;
	DeleteCriticalSection(&m_receive_mutex);
}




HRESULT STDMETHODCALLTYPE DShowGrabber::GetClassID(CLSID* pClassID)
{
	GBDEBUG("DShowGrabber::GetClassID(CLSID* pClassID)\n");
	if (!pClassID) return E_POINTER;
	*pClassID = CLSID_NULL;
	return E_NOTIMPL;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::Stop(void)
{
	GBDEBUG("DShowGrabber::Stop(void)\n");
	EnterCriticalSection(&m_receive_mutex);
	InterlockedExchange(&m_state, State_Stopped);
	while (m_receive_count > 0)
		SleepConditionVariableCS(&m_receive_cond, &m_receive_mutex, INFINITE);
	LeaveCriticalSection(&m_receive_mutex);
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::Pause(void)
{
	GBDEBUG("DShowGrabber::Pause(void)\n");
	InterlockedExchange(&m_state, State_Paused);
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::Run(REFERENCE_TIME tStart)
{
	GBDEBUG("DShowGrabber::Run(REFERENCE_TIME tStart)\n");
	InterlockedExchange(&m_state, State_Running);
	m_starttime = tStart;
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State)
{
	GBDEBUG("DShowGrabber::GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State)\n");
	if (!State) return E_POINTER;
	*State = (FILTER_STATE)InterlockedCompareExchange(&m_state, 0, 0);
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
	*ppEnum = NULL;
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
	*ppPin = NULL;
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
	ZeroMemory(pInfo, sizeof(*pInfo));
	if (m_info.pGraph)
		m_info.pGraph->AddRef();
	*pInfo = m_info;

	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName)
{
	GBDEBUG("DShowGrabber::JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName)\n");
	m_info.pGraph = pGraph;
	m_info.achName[0] = L'\0';
	if (pName)
		wcscpy_s(m_info.achName, _countof(m_info.achName), pName);

	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowGrabber::QueryVendorInfo(LPWSTR* pVendorInfo)
{
	GBDEBUG("DShowGrabber::QueryVendorInfo(LPWSTR* pVendorInfo)\n");
	if (!pVendorInfo) return E_POINTER;
	*pVendorInfo = NULL;
	return E_NOTIMPL;
}



DShowEnumMediaTypes::DShowEnumMediaTypes(AM_MEDIA_TYPE* type) {
	GBDEBUG("DShowEnumMediaTypes::DShowEnumMediaTypes(AM_MEDIA_TYPE *type)\n");
	ZeroMemory(&m_type, sizeof(m_type));
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
	ULONG count = 0;
	if (!ppMediaTypes)
		return E_POINTER;
	if (cMediaTypes != 1 && !pcFetched) return E_POINTER;
	if (pcFetched) *pcFetched = 0;
	for (ULONG i = 0; i < cMediaTypes; ++i) ppMediaTypes[i] = NULL;
	if (!m_pos && cMediaTypes > 0) {
		if (!IsEqualGUID(m_type.majortype, GUID_NULL)) {
			AM_MEDIA_TYPE* type = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
			if (!type)
				return E_OUTOFMEMORY;
			HRESULT hr = copy_media_type(type, &m_type);
			if (FAILED(hr)) {
				CoTaskMemFree(type);
				return hr;
			}
			*ppMediaTypes = type;
			count = 1;
		}
		m_pos = 1;
	}
	if (pcFetched) *pcFetched = count;
	return count == cMediaTypes ? S_OK : S_FALSE;
}

HRESULT STDMETHODCALLTYPE DShowEnumMediaTypes::Skip(
	/* [in] */ ULONG cMediaTypes) {
	GBDEBUG("DShowEnumMediaTypes::Skip(ULONG cMediaTypes)\n");
	if (cMediaTypes == 0) return S_OK;
	if (m_pos || IsEqualGUID(m_type.majortype, GUID_NULL)) return S_FALSE;
	m_pos = 1;
	return cMediaTypes == 1 ? S_OK : S_FALSE;
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
	*ppEnum = NULL;
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
	ZeroMemory(&m_type, sizeof(m_type));
	m_connectedto = NULL;
	m_flushing = FALSE;
	if (type != NULL)
		copy_media_type(&m_type, type);
	else
		m_type.majortype = GUID_NULL;

	m_grabber = grabber;
}

DShowPin::~DShowPin()
{
	GBDEBUG("DShowPin::~DShowPin %p\n",this);
	if (m_connectedto) m_connectedto->Release();
	m_connectedto = NULL;
	free_media_type(m_type);
}


HRESULT STDMETHODCALLTYPE DShowPin::Connect(
	/* [in] */ IPin* pReceivePin,
	/* [annotation][in] */
	_In_opt_  const AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::Connect(IPin *pReceivePin, const AM_MEDIA_TYPE *pmt)\n");
	return VFW_E_INVALID_DIRECTION;
}

HRESULT STDMETHODCALLTYPE DShowPin::ReceiveConnection(
	/* [in] */ IPin* pConnector,
	/* [in] */ const AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::ReceiveConnection(IPin *pConnector,const AM_MEDIA_TYPE *pmt)\n");
	if (m_connectedto)
		return VFW_E_ALREADY_CONNECTED;
	if (InterlockedCompareExchange(&m_grabber->m_state, 0, 0) != State_Stopped)
		return VFW_E_NOT_STOPPED;
	if (!pConnector || !pmt)
		return E_POINTER;
	if (QueryAccept(pmt) != S_OK) return VFW_E_TYPE_NOT_ACCEPTED;
	PIN_DIRECTION direction;
	HRESULT hr = pConnector->QueryDirection(&direction);
	if (FAILED(hr)) return hr;
	if (direction != PINDIR_OUTPUT) return VFW_E_INVALID_DIRECTION;

	pConnector->AddRef();
	m_connectedto = pConnector;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::Disconnect(void) {
	GBDEBUG("DShowPin::Disconnect(void)\n");
	if (InterlockedCompareExchange(&m_grabber->m_state, 0, 0) != State_Stopped)
		return VFW_E_NOT_STOPPED;
	if (!m_connectedto)
		return S_FALSE;

	m_connectedto->Release();
	m_connectedto = NULL;
	InterlockedExchange(&m_flushing, FALSE);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::ConnectedTo(
	/* [annotation][out] */
	_Out_  IPin** pPin) {
	GBDEBUG("DShowPin::ConnectedTo(IPin** pPin)\n");
	if (!pPin)
		return E_POINTER;
	*pPin = NULL;
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
	if (!pmt) return E_POINTER;
	ZeroMemory(pmt, sizeof(*pmt));
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
	ZeroMemory(pInfo, sizeof(*pInfo));

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
	return GetWideString(L"In", Id);
}

HRESULT STDMETHODCALLTYPE DShowPin::QueryAccept(
	/* [in] */ const AM_MEDIA_TYPE* pmt) {
	GBDEBUG("DShowPin::QueryAccept(const AM_MEDIA_TYPE* pmt)\n");
	if (!pmt) return E_POINTER;
	if (m_type.majortype != GUID_NULL && pmt->majortype != m_type.majortype) return S_FALSE;
	if (m_type.subtype != GUID_NULL && pmt->subtype != m_type.subtype) return S_FALSE;
	if (m_type.formattype != GUID_NULL && pmt->formattype != m_type.formattype) return S_FALSE;
	if (m_type.cbFormat != pmt->cbFormat) return S_FALSE;
	if (m_type.cbFormat && (!m_type.pbFormat || !pmt->pbFormat ||
		memcmp(m_type.pbFormat, pmt->pbFormat, m_type.cbFormat) != 0)) return S_FALSE;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::EnumMediaTypes(
	/* [annotation][out] */
	_Out_  IEnumMediaTypes** ppEnum) {
	GBDEBUG("DShowPin::EnumMediaTypes(IEnumMediaTypes** ppEnum)\n");
	DShowEnumMediaTypes* _new;
	if (!ppEnum)
		return E_POINTER;
	*ppEnum = NULL;
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
	if (!nPin) return E_POINTER;
	*nPin = 0;
	return E_NOTIMPL;
}

HRESULT STDMETHODCALLTYPE DShowPin::EndOfStream(void) {
	GBDEBUG("DShowPin::EndOfStream(void)\n");
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::BeginFlush(void) {
	GBDEBUG("DShowPin::BeginFlush(void)\n");
	InterlockedExchange(&m_flushing, TRUE);
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::EndFlush(void) {
	GBDEBUG("DShowPin::EndFlush(void)\n");
	InterlockedExchange(&m_flushing, FALSE);
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
	if (!ppAllocator) return E_POINTER;
	*ppAllocator = NULL;
	return VFW_E_NO_ALLOCATOR;
}

HRESULT STDMETHODCALLTYPE DShowPin::NotifyAllocator(
	/* [in] */ IMemAllocator* pAllocator,
	/* [in] */ BOOL bReadOnly) {
	GBDEBUG("DShowPin::NotifyAllocator(IMemAllocator* pAllocator, BOOL bReadOnly)\n");
	if (!pAllocator) return E_POINTER;
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::GetAllocatorRequirements(
	/* [annotation][out] */
	_Out_  ALLOCATOR_PROPERTIES* pProps) {
	GBDEBUG("DShowPin::GetAllocatorRequirements(ALLOCATOR_PROPERTIES* pProps)\n");
	if (!pProps) return E_POINTER;
	ZeroMemory(pProps, sizeof(*pProps));
	return E_NOTIMPL;
}

HRESULT STDMETHODCALLTYPE DShowPin::Receive(
	/* [in] */ IMediaSample* pSample) {
	GBDEBUG("DShowPin::Receive(IMediaSample* pSample)\n");
	if (!pSample) return E_POINTER;
	if (InterlockedCompareExchange(&m_flushing, 0, 0)) return S_FALSE;
	if (!m_connectedto) return VFW_E_NOT_CONNECTED;
	EnterCriticalSection(&m_grabber->m_receive_mutex);
	if (InterlockedCompareExchange(&m_grabber->m_state, 0, 0) != State_Running) {
		LeaveCriticalSection(&m_grabber->m_receive_mutex);
		return VFW_E_NOT_RUNNING;
	}
	m_grabber->m_receive_count++;
	LeaveCriticalSection(&m_grabber->m_receive_mutex);
	if (m_grabber->m_callback)
		m_grabber->m_callback(m_grabber->m_callback_priv, pSample);
	EnterCriticalSection(&m_grabber->m_receive_mutex);
	m_grabber->m_receive_count--;
	if (m_grabber->m_receive_count == 0)
		WakeAllConditionVariable(&m_grabber->m_receive_cond);
	LeaveCriticalSection(&m_grabber->m_receive_mutex);
	return S_OK;
}
HRESULT STDMETHODCALLTYPE DShowPin::ReceiveMultiple(
	/* [annotation][size_is][in] */
	_In_reads_(nSamples)  IMediaSample** pSamples,
	/* [in] */ long nSamples,
	/* [annotation][out] */
	_Out_  long* nSamplesProcessed) {
	GBDEBUG("DShowPin::ReceiveMultiple(IMediaSample** pSamples, long* nSamplesProcessed)\n");
	if (!pSamples || !nSamplesProcessed) return E_POINTER;
	if (nSamples < 0) return E_INVALIDARG;
	*nSamplesProcessed = 0;
	for (long i = 0; i < nSamples; i++) {
		HRESULT hr = Receive(pSamples[i]);
		if (hr != S_OK) return hr;
		(*nSamplesProcessed)++;
	}
	return S_OK;
}

HRESULT STDMETHODCALLTYPE DShowPin::ReceiveCanBlock(void) {
	GBDEBUG("DShowPin::ReceiveCanBlock(void)\n");
	return S_FALSE;
}

DShowEnumPins::DShowEnumPins(IPin* pin) {
	GBDEBUG("DShowEnumPins::DShowEnumPins(IPin* pin)\n");
	m_pin = pin;
	if (m_pin) m_pin->AddRef();
}

DShowEnumPins::~DShowEnumPins() {
	GBDEBUG("DShowEnumPins::~DShowEnumPins()\n");
	if (m_pin) m_pin->Release();
}

HRESULT STDMETHODCALLTYPE DShowEnumPins::Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched)
{
	GBDEBUG("DShowEnumPins::Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched)\n");
	ULONG count = 0;
	if (!ppPins)
		return E_POINTER;
	if (cPins != 1 && !pcFetched)
		return E_POINTER;
	if (pcFetched) *pcFetched = 0;
	for (ULONG i = 0; i < cPins; ++i) ppPins[i] = NULL;
	if (!m_pos && cPins > 0 && m_pin) {
		m_pin->AddRef();
		*ppPins = m_pin;
		count = 1;
		m_pos = 1;
	}
	if (pcFetched) *pcFetched = count;
	return count == cPins ? S_OK : S_FALSE;
}
HRESULT STDMETHODCALLTYPE DShowEnumPins::Skip(ULONG cPins)
{
	GBDEBUG("DShowEnumPins::Skip(ULONG cPins)\n");
	if (cPins == 0) return S_OK;
	if (m_pos || !m_pin) return S_FALSE;
	m_pos = 1;
	return cPins == 1 ? S_OK : S_FALSE;
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
	*ppEnum = NULL;

	_new = new DShowEnumPins(this->m_pin);
	if (!_new)
		return E_OUTOFMEMORY;
	_new->m_pos = m_pos;

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

