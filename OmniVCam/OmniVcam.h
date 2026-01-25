#pragma once
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <dshow.h>
#include <streams.h>
#include <initguid.h>
#include <olectl.h>
#include <math.h> 
#include <vector>
#include "atlbase.h"

extern "C"{
    #include "RenderVideo.h"
    #include "global.h"
}

// {8B1F6F00-9A2B-4C87-9A1F-3D5A0C8B3E1A}
DEFINE_GUID(CLSID_OmniVCam,
    0x8b1f6f00, 0x9a2b, 0x4c87, 0x9a, 0x1f, 0x3d, 0x5a, 0xc, 0x8b, 0x3e, 0x1a);
DEFINE_GUID(CLSID_OmniVCam2,
    0x8b1f6f00, 0x9a2b, 0x4c87, 0x9a, 0x1f, 0x3d, 0x5a, 0xc, 0x8b, 0x3e, 0x1b);
DEFINE_GUID(CLSID_OmniVCam3,
    0x8b1f6f00, 0x9a2b, 0x4c87, 0x9a, 0x1f, 0x3d, 0x5a, 0xc, 0x8b, 0x3e, 0x1c);
DEFINE_GUID(CLSID_OmniVCam4,
    0x8b1f6f00, 0x9a2b, 0x4c87, 0x9a, 0x1f, 0x3d, 0x5a, 0xc, 0x8b, 0x3e, 0x1d);

#define CONFIG_ROOT_ENV "OMNI_VCAM_CONFIG"
#define CONFIG_ROOT_ENV2 "OMNI_VCAM_CONFIG2"
#define CONFIG_ROOT_ENV3 "OMNI_VCAM_CONFIG3"
#define CONFIG_ROOT_ENV4 "OMNI_VCAM_CONFIG4"

#ifdef DEBUG
    #define DEBUG_LOG_REF() printf("%s refCount: %ld address: %p\n",__FUNCTION__, m_refCount, (void*)this);
    //#define DEBUG_LOG_REF() {char buf[512]; sprintf(buf,"%s refCount: %ld address: %p\n",__FUNCTION__, m_refCount, (void*)this); FILE *fp1 = fopen("debbug.txt","a+");fwrite(buf,1,strlen(buf),fp1); fclose(fp1); }
#else
    #define DEBUG_LOG_REF() ;
#endif // DEBUG

#define SAFE_RELEASE(A)  if(A){ (A)->Release(); (A)=NULL; }

struct CLock {
    CRITICAL_SECTION& m_cs;
    CLock(CRITICAL_SECTION& cs) :m_cs(cs) {
        EnterCriticalSection(&m_cs);
    }
    ~CLock() {
        LeaveCriticalSection(&m_cs);
    }
};

class DShowBase {

public:
    void* operator new(size_t size) {
        void* p = CoTaskMemAlloc(size);
        if (p) ZeroMemory(p, size);
        return p;
    }

    void operator delete(void* p) {
        CoTaskMemFree(p);
    }
};


class FormatManager {
public:
    struct VideoFormat {
        int width;
        int height;
        int fpsNum;
        int fpsDen;
        GUID subtype;
    };

    struct AudioFormat {
        int samplesPerSec;
        int bitsPerSample;
        int channels;
        const wchar_t* description;
    };

    FormatManager();

    // 视频格式管理
    int GetVideoFormatCount() const { return (int)m_videoFormats.size(); }
    const VideoFormat& GetVideoFormat(int index) const { return m_videoFormats[index]; }
    HRESULT GetVideoFormatByIndex(int index, VideoFormat* pFormat) const;

    // 音频格式管理
    int GetAudioFormatCount() const { return (int)m_audioFormats.size(); }
    const AudioFormat& GetAudioFormat(int index) const { return m_audioFormats[index]; }
    HRESULT GetAudioFormatByIndex(int index, AudioFormat* pFormat) const;

    // 媒体类型创建
    AM_MEDIA_TYPE* CreateVideoMediaType(const VideoFormat& format) const;
    AM_MEDIA_TYPE* CreateAudioMediaType(const AudioFormat& format) const;

private:
    void InitializeVideoFormats();
    void InitializeAudioFormats();

    std::vector<VideoFormat> m_videoFormats;
    std::vector<AudioFormat> m_audioFormats;
};

extern FormatManager formatManager;

class OmniVCam;
class OmniVideoPin;
class OmniAudioPin;
class OmniVCamClassFactory;

// Video format structure
struct OmniVideoFormat {
    int width;
    int height;
    int fpsNum;
    int fpsDen;
    GUID subtype;
};

// Audio format structure
struct OmniAudioFormat {
    int samplesPerSec;
    int bitsPerSample;
    int channels;
};

extern HINSTANCE g_hInstance;
extern long g_cObjects;
extern long g_cLocks;


class OmniMediaTypeEnum : public IEnumMediaTypes,public DShowBase {
public:
    OmniMediaTypeEnum(AM_MEDIA_TYPE** ppMediaTypes, ULONG count);

    ~OmniMediaTypeEnum();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv);

    STDMETHODIMP_(ULONG) AddRef();
    STDMETHODIMP_(ULONG) Release();

    // IEnumMediaTypes
    STDMETHODIMP Next(ULONG cMediaTypes, AM_MEDIA_TYPE** ppMediaTypes, ULONG* pcFetched);

    STDMETHODIMP Skip(ULONG cMediaTypes);

    STDMETHODIMP Reset();

    STDMETHODIMP Clone(IEnumMediaTypes** ppEnum);

private:
    volatile long m_refCount;
    AM_MEDIA_TYPE** m_ppMediaTypes;  // 改为二级指针
    ULONG m_count;
    ULONG m_index;
};

class OmniVCam : public IBaseFilter,public INonDelegatingUnknown, public DShowBase {
public:
    OmniVCam(const GUID *clsId,const char* configPath);
    ~OmniVCam();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv);
    STDMETHODIMP_(ULONG) AddRef();

    STDMETHODIMP_(ULONG) Release();

    // IPersist
    STDMETHODIMP GetClassID(CLSID* pClsID);

    // IMediaFilter
    STDMETHODIMP GetState(DWORD dwMSecs, FILTER_STATE* State);

    STDMETHODIMP SetSyncSource(IReferenceClock* pClock);

    STDMETHODIMP GetSyncSource(IReferenceClock** pClock);

    STDMETHODIMP Stop();

    STDMETHODIMP Pause();

    STDMETHODIMP Run(REFERENCE_TIME tStart);


    STDMETHODIMP EnumPins(IEnumPins** ppEnum);

    STDMETHODIMP FindPin(LPCWSTR Id, IPin** ppPin);

    STDMETHODIMP QueryFilterInfo(FILTER_INFO* pInfo);

    STDMETHODIMP JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName);

    STDMETHODIMP QueryVendorInfo(LPWSTR* pVendorInfo);

    ////INonDelegatingUnknown
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void ** ppvObject);
    STDMETHODIMP_(ULONG) NonDelegatingAddRef(void);
    STDMETHODIMP_(ULONG) NonDelegatingRelease(void);



    // Custom methods
    HRESULT SetVideoFormat(const OmniVideoFormat& format);

    HRESULT SetAudioFormat(const OmniAudioFormat& format);

    HRESULT PushVideoFrame(BYTE* data, long size, REFERENCE_TIME customStartTime);

    HRESULT PushAudioSample(BYTE* data, long size, REFERENCE_TIME customStartTime);

    CRITICAL_SECTION m_cs;
    IFilterGraph* m_graph;
    HANDLE m_noProcess[2];
private:
    volatile long m_refCount;
    FILTER_STATE m_state;
    IReferenceClock* m_clock;
    OmniAudioPin* m_audioPin;
    OmniVideoPin* m_videoPin;
    inout_options* m_renderOpts;
    HANDLE m_renderThread;
    const GUID* m_clsId;
    const char* m_configPath;
};



AM_MEDIA_TYPE* _CreateMediaType(const AM_MEDIA_TYPE* pSrc);
HRESULT CopyMediaType(AM_MEDIA_TYPE* pmtDest, AM_MEDIA_TYPE* pmtSrc);
REFERENCE_TIME CalculateInterval(int numerator, int denominator);
int GetVideoSupportedFormatCount();
int GetAudioSupportedFormatCount();
HRESULT GetVideoSupportedFormatByIndex(int index, FormatManager::VideoFormat* pFormat);
HRESULT GetAudioSupportedFormatByIndex(int index, FormatManager::AudioFormat* pFormat);
