// Minimal XAML diagnostics tap for the cache probe: keeps the XamlDiagnostics site so the app can query
// IXamlHotReloadService from it. CLSID {6A1C0F2E-3B4D-4E5F-8A9B-0C1D2E3F4A5B}.
#include <windows.h>
#include <ocidl.h>

static const CLSID CLSID_ProbeTap = { 0x6a1c0f2e, 0x3b4d, 0x4e5f, { 0x8a, 0x9b, 0x0c, 0x1d, 0x2e, 0x3f, 0x4a, 0x5b } };
static IUnknown* g_site = nullptr;

class Tap final : public IObjectWithSite
{
    LONG m_refs = 1;
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IObjectWithSite) { *ppv = static_cast<IObjectWithSite*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&m_refs); if (!r) delete this; return r; }
    STDMETHODIMP SetSite(IUnknown* site) override
    {
        if (site) { site->AddRef(); }
        IUnknown* old = static_cast<IUnknown*>(InterlockedExchangePointer(reinterpret_cast<void**>(&g_site), site));
        if (old) { old->Release(); }
        return S_OK;
    }
    STDMETHODIMP GetSite(REFIID riid, void** ppv) override
    {
        return g_site ? g_site->QueryInterface(riid, ppv) : E_FAIL;
    }
};

class Factory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = static_cast<IClassFactory*>(this); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) { return CLASS_E_NOAGGREGATION; }
        Tap* tap = new Tap();
        HRESULT hr = tap->QueryInterface(riid, ppv);
        tap->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL) override { return S_OK; }
};
static Factory g_factory;

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_ProbeTap) { return CLASS_E_CLASSNOTAVAILABLE; }
    return g_factory.QueryInterface(riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return S_FALSE; }

// Returns the site (AddRef'd) once the framework sited the tap; HRESULT_FROM_WIN32(ERROR_NOT_READY) before that.
extern "C" HRESULT __stdcall ProbeTap_GetSite(IUnknown** site)
{
    *site = g_site;
    if (!*site) { return HRESULT_FROM_WIN32(ERROR_NOT_READY); }
    (*site)->AddRef();
    return S_OK;
}
