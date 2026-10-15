// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Experimental XAML hot reload: live components by the markup URI they were loaded from, so a tool can load them again
// (IXamlHotReloadService::ReloadComponents), and the root properties each load set, so properties removed from the
// markup are cleared when it is loaded again. Only stamped apps (XamlHotReloadOverrides::IsEnabledForApp) track
// anything.

#include "precomp.h"
#include "FrameworkApplication.g.h"
#include <MetadataAPI.h>
#include <wrlhelper.h>
#include <wil\resource.h>
#include <map>
#include <mutex>
#include <XamlHotReloadOverrides.h>
#include <XamlHotReloadRegistry.h>

using namespace DirectUI;

namespace
{
    using Components = XamlHotReload::ComponentTable<wrl::ComPtr<IWeakReference>, wrl::ComPtr<msy::IDispatcherQueue>>;

    // Intentionally leaked so loads during process shutdown never touch a destroyed table.
    Components& Table()
    {
        static auto* table = new Components();
        return *table;
    }

    wrl::ComPtr<IInspectable> Resolve(const wrl::ComPtr<IWeakReference>& weak)
    {
        wrl::ComPtr<IInspectable> resolved;
        if (!weak || FAILED(weak->Resolve(__uuidof(IInspectable), resolved.ReleaseAndGetAddressOf())))
        {
            resolved.Reset();
        }
        return resolved;
    }

    // What ReloadComponents waits for. Shared with dispatched callbacks, which may finish after a timed-out wait.
    struct ReloadState
    {
        std::mutex Lock;
        UINT32 Loaded = 0;
        HRESULT FirstFailure = S_OK;
        std::atomic<long> Pending{ 1 }; // the caller's own count, released once every callback is queued
        wil::unique_event_nothrow Done;

        void Finish()
        {
            if (--Pending == 0)
            {
                Done.SetEvent();
            }
        }
    };

    // On the components' own thread.
    void LoadTargets(const std::vector<Components::Target>& targets, ReloadState& state)
    {
        for (const auto& target : targets)
        {
            wrl::ComPtr<IInspectable> component = Resolve(target.Ref);
            if (!component)
            {
                continue;
            }

            xstring_ptr uri;
            HRESULT hr = xstring_ptr::CloneBuffer(target.Uri.c_str(), static_cast<XUINT32>(target.Uri.size()), &uri);
            if (SUCCEEDED(hr))
            {
                hr = FrameworkApplication::LoadComponent(
                    component.Get(), uri, static_cast<xaml_primitives::ComponentResourceLocation>(target.Location));
            }

            std::lock_guard<std::mutex> guard(state.Lock);
            if (SUCCEEDED(hr))
            {
                ++state.Loaded;
            }
            else if (SUCCEEDED(state.FirstFailure))
            {
                state.FirstFailure = hr;
            }
        }
    }
}

void XamlHotReloadOverrides::OnComponentLoaded(
    _In_ IInspectable* component,
    _In_ CDependencyObject* root,
    _In_z_ const WCHAR* uri,
    std::int32_t resourceLocation,
    std::vector<std::uint16_t> rootProperties)
{
    wrl::ComPtr<IWeakReferenceSource> source;
    wrl::ComPtr<IWeakReference> weak;
    wrl::ComPtr<IUnknown> identity;
    wrl::ComPtr<msy::IDispatcherQueue> dispatcher;
    DXamlCore* core = DXamlCore::GetCurrentNoCreate();
    if (!core ||
        FAILED(component->QueryInterface(IID_PPV_ARGS(&source))) ||
        FAILED(source->GetWeakReference(&weak)) ||
        FAILED(component->QueryInterface(IID_PPV_ARGS(&identity))) ||
        FAILED(core->GetDispatcherQueue(&dispatcher)))
    {
        return;
    }

    const std::vector<std::uint16_t> latest = rootProperties;
    const std::vector<std::uint16_t> previous = Table().Record(
        uri,
        identity.Get(),
        ::GetCurrentThreadId(),
        std::move(weak),
        std::move(dispatcher),
        resourceLocation,
        std::move(rootProperties),
        [](const wrl::ComPtr<IWeakReference>& ref) { return Resolve(ref) != nullptr; });

    // Replaced markup run again on this component: what the previous markup set on the root and this markup doesn't
    // goes back to its default, as in a clean build of this markup. (Properties code set are left alone.)
    if (!IsActive() || previous.empty())
    {
        return;
    }
    for (const auto index : XamlHotReload::RootPropertiesToClear(previous, latest))
    {
        if (const CDependencyProperty* property = MetadataAPI::GetPropertyByIndex(static_cast<KnownPropertyIndex>(index)))
        {
            IGNOREHR(root->ClearValue(property));
        }
    }
}

_Check_return_ HRESULT XamlHotReloadOverrides::ReloadComponents(_In_z_ const WCHAR* uri, _Out_ UINT32* count)
{
    *count = 0;
    const auto targets = Table().Match(uri);
    if (targets.empty())
    {
        return S_OK;
    }

    // Loading again is a hot reload action: components start from fresh resources (CApplication::LoadComponent) and
    // removed root properties are cleared.
    Activate();

    std::map<std::uint32_t, std::vector<Components::Target>> byThread;
    for (const auto& target : targets)
    {
        byThread[target.ThreadId].push_back(target);
    }

    auto state = std::make_shared<ReloadState>();
    IFC_RETURN(state->Done.create(wil::EventOptions::ManualReset));

    const std::uint32_t currentThread = ::GetCurrentThreadId();
    for (auto& [threadId, group] : byThread)
    {
        if (threadId == currentThread)
        {
            LoadTargets(group, *state);
            continue;
        }

        // The component's own thread loads it; a thread that has shut down has nothing left to load.
        const wrl::ComPtr<msy::IDispatcherQueue> dispatcher = group.front().Thread;
        ++state->Pending;
        boolean enqueued = false;
        const HRESULT hr = dispatcher->TryEnqueue(
            WRLHelper::MakeAgileCallback<msy::IDispatcherQueueHandler>([group = std::move(group), state]() -> HRESULT
            {
                LoadTargets(group, *state);
                state->Finish();
                return S_OK;
            }).Get(),
            &enqueued);
        if (FAILED(hr) || !enqueued)
        {
            --state->Pending;
        }
    }

    state->Finish();
    constexpr DWORD c_timeoutMs = 30 * 1000;
    const bool finished = state->Done.wait(c_timeoutMs);

    std::lock_guard<std::mutex> guard(state->Lock);
    *count = state->Loaded;
    if (FAILED(state->FirstFailure))
    {
        return state->FirstFailure;
    }
    return finished ? S_OK : HRESULT_FROM_WIN32(ERROR_TIMEOUT);
}

_Check_return_ HRESULT XamlHotReloadOverrides::GetLiveComponents(
    _In_z_ const WCHAR* uri,
    _Out_ UINT32* count,
    _Outptr_result_buffer_maybenull_(*count) IInspectable*** components)
{
    *count = 0;
    *components = nullptr;

    std::vector<wrl::ComPtr<IInspectable>> live;
    const std::uint32_t currentThread = ::GetCurrentThreadId();
    for (const auto& target : Table().Match(uri))
    {
        if (target.ThreadId == currentThread)
        {
            if (auto component = Resolve(target.Ref))
            {
                live.push_back(std::move(component));
            }
        }
    }
    if (live.empty())
    {
        return S_OK;
    }

    auto array = static_cast<IInspectable**>(::CoTaskMemAlloc(sizeof(IInspectable*) * live.size()));
    IFCOOMFAILFAST(array);
    for (size_t i = 0; i < live.size(); ++i)
    {
        array[i] = live[i].Detach();
    }
    *components = array;
    *count = static_cast<UINT32>(live.size());
    return S_OK;
}
