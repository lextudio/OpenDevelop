// WinUI 3 Hot Reload TAP.
//
// WinUI loads this DLL into the application when WinUIHotReload.Agent calls
// InitializeXamlDiagnosticsEx on its own process, and hands it an IXamlDiagnostics site. The TAP
// is deliberately thin: it keeps a table of live elements (with their XAML source positions) and
// exposes the few XamlOM operations the managed agent needs as flat C exports. All Hot Reload
// logic - the pipe, XAML diffing, element mapping, type resolution - lives in the agent.
//
// See doc/technotes/hot-reload.md, "WinUI 3: XAML Diagnostics agent", for the mechanism and the
// threading rules this file follows.

#include <windows.h>
#include <ocidl.h>
#include <inspectable.h>
#include <xamlOM.h>
#include <windows.foundation.h>
#include <winstring.h>
#include <wrl.h>
#include <wrl/module.h>

#include <cstdio>
#include <functional>
#include <memory>
#include <map>
#include <string>
#include <thread>

using namespace Microsoft::WRL;

namespace
{
    void Log(const wchar_t* format, ...)
    {
        wchar_t path[MAX_PATH];
        if (!GetEnvironmentVariableW(L"WINUI_HOTRELOAD_LOG", path, MAX_PATH))
            return;
        FILE* file = nullptr;
        if (_wfopen_s(&file, path, L"a") || !file)
            return;
        fwprintf(file, L"[tap tid %lu] ", GetCurrentThreadId());
        va_list args;
        va_start(args, format);
        vfwprintf(file, format, args);
        va_end(args);
        fputwc(L'\n', file);
        fclose(file);
    }

    // Microsoft.UI.Dispatching ABI, declared here because the Windows SDK only ships the
    // Windows.System flavour. IIDs are read from Microsoft.UI.winmd.
    MIDL_INTERFACE("2E0872A9-4E29-5F14-B688-FB96D5F9D5F8")
    IUiDispatcherQueueHandler : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE Invoke() = 0;
    };

    MIDL_INTERFACE("F6EBF8FA-BE1C-5BF6-A467-73DA28738AE8")
    IUiDispatcherQueue : public IInspectable
    {
        virtual HRESULT STDMETHODCALLTYPE CreateTimer(IInspectable** result) = 0;
        virtual HRESULT STDMETHODCALLTYPE TryEnqueue(IUiDispatcherQueueHandler* callback, boolean* result) = 0;
    };

    // Must be agile: TryEnqueue rejects a handler without the free-threaded marshaller
    // (0x8000001C), because it is invoked on a different thread than the one that queued it.
    template <typename F>
    class Handler : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IUiDispatcherQueueHandler, FtmBase>
    {
        F m_work;
    public:
        explicit Handler(F work) : m_work(std::move(work)) {}
        IFACEMETHODIMP Invoke() override { m_work(); return S_OK; }
    };

    struct Element
    {
        std::wstring Type;
        std::wstring Name;
        std::wstring File;
        unsigned int Line = 0;
        unsigned int Column = 0;
    };

    struct State
    {
        SRWLOCK Lock = SRWLOCK_INIT;
        ComPtr<IXamlDiagnostics> Diagnostics;
        ComPtr<IVisualTreeService3> Tree;
        std::map<InstanceHandle, Element> Elements;
        bool Ready = false;
    };

    State g_state;

    std::wstring Copy(BSTR value) { return value ? std::wstring(value, SysStringLen(value)) : std::wstring(); }

    class TreeCallback : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IVisualTreeServiceCallback2>
    {
    public:
        IFACEMETHODIMP OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) override
        {
            AcquireSRWLockExclusive(&g_state.Lock);
            if (mutation == Add) {
                Element& entry = g_state.Elements[element.Handle];
                entry.Type = Copy(element.Type);
                entry.Name = Copy(element.Name);
                entry.File = Copy(element.SrcInfo.FileName);
                entry.Line = element.SrcInfo.LineNumber;
                entry.Column = element.SrcInfo.ColumnNumber;
            } else if (mutation == Remove) {
                g_state.Elements.erase(element.Handle);
            }
            ReleaseSRWLockExclusive(&g_state.Lock);
            return S_OK;
        }

        IFACEMETHODIMP OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override { return S_OK; }
    };

    // Runs work on the application's UI thread and waits for it. XamlOM mutations fail with E_FAIL
    // anywhere else. The agent only calls in from its pipe thread, never from the UI thread, so the
    // wait cannot deadlock against itself.
    template <typename F>
    HRESULT RunOnUiThread(F work)
    {
        ComPtr<IXamlDiagnostics> diagnostics;
        AcquireSRWLockShared(&g_state.Lock);
        diagnostics = g_state.Diagnostics;
        ReleaseSRWLockShared(&g_state.Lock);
        if (!diagnostics)
            return E_NOT_VALID_STATE;

        ComPtr<IInspectable> dispatcherObject;
        HRESULT hr = diagnostics->GetDispatcher(&dispatcherObject);
        ComPtr<IUiDispatcherQueue> dispatcher;
        if (SUCCEEDED(hr))
            hr = dispatcherObject.As(&dispatcher);
        if (FAILED(hr))
            return hr;

        struct Shared { HANDLE Done; HRESULT Result; };
        auto shared = std::make_shared<Shared>();
        shared->Done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        shared->Result = E_PENDING;
        auto handler = Make<Handler<std::function<void()>>>(std::function<void()>([shared, work]() mutable {
            shared->Result = work();
            SetEvent(shared->Done);
        }));

        boolean queued = false;
        hr = dispatcher->TryEnqueue(handler.Get(), &queued);
        if (SUCCEEDED(hr) && !queued)
            hr = E_ABORT; // The queue is shutting down.
        if (SUCCEEDED(hr))
            hr = WaitForSingleObject(shared->Done, 10000) == WAIT_OBJECT_0
                ? shared->Result
                : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        CloseHandle(shared->Done);
        return hr;
    }

    ComPtr<IVisualTreeService3> Tree()
    {
        AcquireSRWLockShared(&g_state.Lock);
        ComPtr<IVisualTreeService3> tree = g_state.Tree;
        ReleaseSRWLockShared(&g_state.Lock);
        return tree;
    }
}

class __declspec(uuid("3769DAD3-CCF5-4633-81F1-BA08FA59120C")) WinUIHotReloadTap
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IObjectWithSite>
{
    ComPtr<IUnknown> m_site;

public:
    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        m_site = site;
        if (!site)
            return S_OK;

        ComPtr<IXamlDiagnostics> diagnostics;
        ComPtr<IVisualTreeService3> tree;
        HRESULT hr = site->QueryInterface(IID_PPV_ARGS(&diagnostics));
        if (SUCCEEDED(hr))
            hr = site->QueryInterface(IID_PPV_ARGS(&tree));
        Log(L"SetSite pid=%lu hr=0x%08x", GetCurrentProcessId(), hr);
        if (FAILED(hr))
            return hr;

        AcquireSRWLockExclusive(&g_state.Lock);
        g_state.Diagnostics = diagnostics;
        g_state.Tree = tree;
        ReleaseSRWLockExclusive(&g_state.Lock);

        // AdviseVisualTreeChange replays the existing tree through the UI thread and blocks until
        // it is done, and SetSite runs on the UI thread - so it has to happen elsewhere.
        std::thread([tree] {
            HRESULT result = tree->AdviseVisualTreeChange(Make<TreeCallback>().Get());
            Log(L"AdviseVisualTreeChange hr=0x%08x", result);
            if (SUCCEEDED(result)) {
                AcquireSRWLockExclusive(&g_state.Lock);
                g_state.Ready = true;
                ReleaseSRWLockExclusive(&g_state.Lock);
            }
        }).detach();
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** site) override
    {
        return m_site ? m_site.CopyTo(riid, site) : E_FAIL;
    }
};

CoCreatableClass(WinUIHotReloadTap);

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* result)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, result);
}

STDAPI DllCanUnloadNow()
{
    // WinUI holds the site for the life of the process; never unload under it.
    return S_FALSE;
}

typedef void (CALLBACK* WinUIHR_ElementCallback)(
    UINT64 handle, LPCWSTR type, LPCWSTR name, LPCWSTR file, UINT32 line, UINT32 column, void* context);

// S_OK once the TAP has a site and the live tree has been replayed; S_FALSE before that.
extern "C" HRESULT WINAPI WinUIHR_IsReady()
{
    AcquireSRWLockShared(&g_state.Lock);
    bool ready = g_state.Ready;
    ReleaseSRWLockShared(&g_state.Lock);
    return ready ? S_OK : S_FALSE;
}

// Reports every live element. The callback runs under the table lock and must not call back in.
extern "C" HRESULT WINAPI WinUIHR_GetElements(WinUIHR_ElementCallback callback, void* context)
{
    if (!callback)
        return E_INVALIDARG;
    AcquireSRWLockShared(&g_state.Lock);
    for (const auto& [handle, element] : g_state.Elements)
        callback(static_cast<UINT64>(handle), element.Type.c_str(), element.Name.c_str(),
            element.File.c_str(), element.Line, element.Column, context);
    ReleaseSRWLockShared(&g_state.Lock);
    return S_OK;
}

// propertyName is the declaring type's full name plus the property, for example
// "Microsoft.UI.Xaml.FrameworkElement.Margin"; valueType is the WinRT type name WinUI's
// CreateInstance converts the string to, for example "Windows.Foundation.String".
extern "C" HRESULT WINAPI WinUIHR_SetProperty(UINT64 handle, LPCWSTR propertyName, LPCWSTR valueType, LPCWSTR value)
{
    auto tree = Tree();
    if (!tree)
        return E_NOT_VALID_STATE;
    if (!propertyName || !valueType || !value)
        return E_INVALIDARG;

    std::wstring property(propertyName), type(valueType), text(value);
    HRESULT hr = RunOnUiThread([tree, handle, property, type, text]() -> HRESULT {
        unsigned int index = 0;
        HRESULT result = tree->GetPropertyIndex(static_cast<InstanceHandle>(handle), property.c_str(), &index);
        InstanceHandle valueHandle = 0;
        if (SUCCEEDED(result)) {
            BSTR typeName = SysAllocString(type.c_str());
            BSTR valueText = SysAllocString(text.c_str());
            result = tree->CreateInstance(typeName, valueText, &valueHandle);
            SysFreeString(typeName);
            SysFreeString(valueText);
        }
        if (SUCCEEDED(result))
            result = tree->SetProperty(static_cast<InstanceHandle>(handle), valueHandle, index);
        return result;
    });
    Log(L"SetProperty %llu %s=%s (%s) hr=0x%08x", handle, propertyName, value, valueType, hr);
    return hr;
}

typedef HRESULT (CALLBACK* WinUIHR_UiCallback)(void* context);

// Runs a managed callback on the application's UI thread and waits for it. Subtree updates need
// WinUI's own XamlReader and live objects, which only the managed agent can drive; this is how it
// gets onto the one thread they work on. Must not be called from the UI thread.
extern "C" HRESULT WINAPI WinUIHR_RunOnUiThread(WinUIHR_UiCallback callback, void* context)
{
    if (!callback)
        return E_INVALIDARG;
    return RunOnUiThread([callback, context]() -> HRESULT { return callback(context); });
}

// The live object behind a handle, as an AddRef'd IInspectable the caller releases. Safe on any
// thread: it is a table lookup, the object itself is only touched by whoever uses it.
extern "C" HRESULT WINAPI WinUIHR_GetObject(UINT64 handle, IInspectable** result)
{
    if (!result)
        return E_INVALIDARG;
    *result = nullptr;
    ComPtr<IXamlDiagnostics> diagnostics;
    AcquireSRWLockShared(&g_state.Lock);
    diagnostics = g_state.Diagnostics;
    ReleaseSRWLockShared(&g_state.Lock);
    if (!diagnostics)
        return E_NOT_VALID_STATE;
    return diagnostics->GetIInspectableFromHandle(static_cast<InstanceHandle>(handle), result);
}

// Reads a string-valued property. Used by the agent's query endpoint, which lets a test verify what
// the running application shows independently of the apply path.
extern "C" HRESULT WINAPI WinUIHR_GetStringProperty(UINT64 handle, LPCWSTR propertyName, BSTR* value)
{
    if (!value || !propertyName)
        return E_INVALIDARG;
    *value = nullptr;

    auto tree = Tree();
    ComPtr<IXamlDiagnostics> diagnostics;
    AcquireSRWLockShared(&g_state.Lock);
    diagnostics = g_state.Diagnostics;
    ReleaseSRWLockShared(&g_state.Lock);
    if (!tree || !diagnostics)
        return E_NOT_VALID_STATE;

    std::wstring property(propertyName);
    auto result = std::make_shared<std::wstring>();
    HRESULT hr = RunOnUiThread([tree, diagnostics, handle, property, result]() -> HRESULT {
        unsigned int index = 0;
        HRESULT h = tree->GetPropertyIndex(static_cast<InstanceHandle>(handle), property.c_str(), &index);
        InstanceHandle valueHandle = 0;
        if (SUCCEEDED(h))
            h = tree->GetProperty(static_cast<InstanceHandle>(handle), index, &valueHandle);
        if (FAILED(h) || !valueHandle)
            return h;
        ComPtr<IInspectable> boxed;
        h = diagnostics->GetIInspectableFromHandle(valueHandle, &boxed);
        ComPtr<ABI::Windows::Foundation::IPropertyValue> propertyValue;
        if (SUCCEEDED(h))
            h = boxed.As(&propertyValue);
        HSTRING text = nullptr;
        if (SUCCEEDED(h))
            h = propertyValue->GetString(&text);
        if (SUCCEEDED(h)) {
            UINT32 length = 0;
            PCWSTR raw = WindowsGetStringRawBuffer(text, &length);
            result->assign(raw, length);
            WindowsDeleteString(text);
        }
        return h;
    });
    if (SUCCEEDED(hr))
        *value = SysAllocStringLen(result->c_str(), static_cast<UINT>(result->size()));
    return hr;
}
