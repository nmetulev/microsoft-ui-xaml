// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include "XamlBindScopeAttach.h"
#include "DependencyObject.h"
#include "FrameworkElement.g.h"

using namespace DirectUI;

namespace {

// -------------------------------------------------------------------------------------------
// ABI marshalling helpers.
// -------------------------------------------------------------------------------------------

std::wstring ToWString(_In_opt_ HSTRING value)
{
    UINT32 length = 0;
    const wchar_t* buffer = WindowsGetStringRawBuffer(value, &length);
    return buffer == nullptr ? std::wstring() : std::wstring(buffer, length);
}

// xstring_ptr::Promote has no HSTRING overload; it promotes to xstring_ptr_storage, xstring_ptr or
// xruntime_string_ptr. Go through xruntime_string_ptr, which owns a real runtime string handle, and
// hand ownership of that handle to the caller.
_Check_return_ HRESULT ToHString(const std::wstring& value, _Out_ HSTRING* result)
{
    *result = nullptr;
    if (value.empty())
    {
        return S_OK;
    }
    return WindowsCreateString(value.c_str(), static_cast<UINT32>(value.size()), result);
}

CDependencyObject* HandleOf(_In_opt_ IInspectable* value)
{
    if (!value)
    {
        return nullptr;
    }

    ctl::ComPtr<xaml::IDependencyObject> asDO;
    if (FAILED(ctl::ComPtr<IInspectable>(value).As(&asDO)) || !asDO)
    {
        return nullptr;
    }
    return static_cast<DependencyObject*>(asDO.Get())->GetHandle();
}

// -------------------------------------------------------------------------------------------
// The real host. Every method here is one runtime primitive; there is no policy in this file.
// -------------------------------------------------------------------------------------------

class RuntimeScopeHost final : public BindScope::IScopeHost
{
public:
    BindScope::ScopeObject ResolveName(BindScope::ScopeObject root, const std::wstring& name) override
    {
        DXamlCore* core = DXamlCore::GetCurrent();
        if (!core)
        {
            return nullptr;
        }

        CDependencyObject* rootHandle = HandleOf(static_cast<IInspectable*>(root));
        if (!rootHandle)
        {
            return nullptr;
        }

        // Namescope lookup, not a tree walk. The namescope exists from parse time, so this resolves
        // inside a root that was constructed but never realized.
        wrl_wrappers::HString nameString;
        if (FAILED(WindowsCreateString(name.c_str(), static_cast<UINT32>(name.size()), nameString.GetAddressOf())))
        {
            return nullptr;
        }

        auto named = core->GetHandle()->TryGetElementByName(xephemeral_string_ptr(nameString.Get()), rootHandle);
        if (!named)
        {
            return nullptr;
        }

        ctl::ComPtr<DependencyObject> peer;
        if (FAILED(core->GetPeer(named.get(), &peer)) || !peer)
        {
            return nullptr;
        }

        // The engine only ever compares these and hands them back to the connector, so a raw,
        // non-owning pointer is correct: the tree owns the element for the duration of the call.
        return static_cast<BindScope::ScopeObject>(ctl::as_iinspectable(peer.Get()));
    }

    BindScope::ScopeObject GetNamescopeOwner(BindScope::ScopeObject target) override
    {
        CDependencyObject* targetHandle = HandleOf(static_cast<IInspectable*>(target));
        if (!targetHandle)
        {
            return nullptr;
        }

        CDependencyObject* owner = targetHandle->GetStandardNameScopeOwner();
        if (!owner)
        {
            return nullptr;
        }

        ctl::ComPtr<IInspectable> ownerPeer;
        if (FAILED(DXamlServices::TryGetPeer(owner, IID_PPV_ARGS(&ownerPeer))) || !ownerPeer)
        {
            return nullptr;
        }
        return static_cast<BindScope::ScopeObject>(ownerPeer.Get());
    }

    // COM identity. A successful QueryInterface to a shared interface proves nothing about which
    // element a manifest row refers to, so identity is always compared on IUnknown.
    bool IsSameInstance(BindScope::ScopeObject left, BindScope::ScopeObject right) override
    {
        if (left == right)
        {
            return true;
        }
        if (!left || !right)
        {
            return false;
        }

        ctl::ComPtr<IUnknown> leftUnknown;
        ctl::ComPtr<IUnknown> rightUnknown;
        if (FAILED(static_cast<IInspectable*>(left)->QueryInterface(IID_PPV_ARGS(&leftUnknown)))) { return false; }
        if (FAILED(static_cast<IInspectable*>(right)->QueryInterface(IID_PPV_ARGS(&rightUnknown)))) { return false; }
        return leftUnknown.Get() == rightUnknown.Get();
    }

    bool TypeNameEquals(BindScope::ScopeObject target, const std::wstring& expectedTypeName) override
    {
        wrl_wrappers::HString actual;
        if (FAILED(static_cast<IInspectable*>(target)->GetRuntimeClassName(actual.GetAddressOf())))
        {
            return false;
        }
        return ToWString(actual.Get()) == expectedTypeName;
    }

    std::wstring GetBaseTreeRevision(BindScope::ScopeObject root) override
    {
        CDependencyObject* rootHandle = HandleOf(static_cast<IInspectable*>(root));
        if (!rootHandle)
        {
            return std::wstring();
        }

        auto& revisions = BaseTreeRevisions();
        auto found = revisions.find(rootHandle);
        return found == revisions.end() ? std::wstring() : found->second;
    }

    bool TryGetManifest(BindScope::ScopeObject connector, BindScope::ScopeManifest* manifest) override
    {
        ctl::ComPtr<xaml_markup::IXamlBindScopeManifest> manifestInterface;
        if (FAILED(ctl::ComPtr<IInspectable>(static_cast<IInspectable*>(connector)).As(&manifestInterface)) || !manifestInterface)
        {
            return false;
        }

        INT32 rootConnectionId = -1;
        if (FAILED(manifestInterface->get_RootConnectionId(&rootConnectionId))) { return false; }

        wrl_wrappers::HString scopeRevision;
        if (FAILED(manifestInterface->get_ScopeRevision(scopeRevision.GetAddressOf()))) { return false; }

        wrl_wrappers::HString expectedBaseTreeRevision;
        if (FAILED(manifestInterface->get_ExpectedBaseTreeRevision(expectedBaseTreeRevision.GetAddressOf()))) { return false; }

        UINT32 requiredCount = 0;
        INT32* requiredIds = nullptr;
        if (FAILED(manifestInterface->GetRequiredConnectionIds(&requiredCount, &requiredIds))) { return false; }
        auto freeRequired = wil::scope_exit([&requiredIds] { CoTaskMemFree(requiredIds); });

        manifest->RootConnectionId = rootConnectionId;
        manifest->ScopeRevision = ToWString(scopeRevision.Get());
        manifest->ExpectedBaseTreeRevision = ToWString(expectedBaseTreeRevision.Get());
        manifest->RequiredConnectionIds.assign(requiredIds, requiredIds + requiredCount);
        return true;
    }

    BindScope::ScopeObject GetBindingConnector(BindScope::ScopeObject connector, INT32 rootConnectionId, BindScope::ScopeObject root) override
    {
        auto* connectorInterface = static_cast<xaml_markup::IComponentConnector*>(
            ResolveConnector(static_cast<IInspectable*>(connector)));
        if (!connectorInterface)
        {
            return nullptr;
        }

        ctl::ComPtr<xaml_markup::IComponentConnector> scope;
        if (FAILED(connectorInterface->GetBindingConnector(rootConnectionId, static_cast<IInspectable*>(root), &scope)) || !scope)
        {
            return nullptr;
        }

        // The engine takes a reference through AddRefScope when it publishes ownership, so hold the
        // produced scope alive for the duration of this attach.
        m_pendingScope = scope;
        return static_cast<BindScope::ScopeObject>(scope.Get());
    }

    bool SupportsLifecycle(BindScope::ScopeObject scope) override
    {
        if (!scope)
        {
            return false;
        }
        ctl::ComPtr<xaml_markup::IXamlBindScopeLifecycle> lifecycle;
        return SUCCEEDED(ctl::ComPtr<IInspectable>(static_cast<IInspectable*>(scope)).As(&lifecycle)) && lifecycle;
    }

    bool Connect(BindScope::ScopeObject scope, INT32 connectionId, BindScope::ScopeObject target) override
    {
        ctl::ComPtr<xaml_markup::IComponentConnector> connector;
        if (FAILED(ctl::ComPtr<IInspectable>(static_cast<IInspectable*>(scope)).As(&connector)) || !connector)
        {
            return false;
        }
        return SUCCEEDED(connector->Connect(connectionId, static_cast<IInspectable*>(target)));
    }

    bool InitializeScope(BindScope::ScopeObject scope) override
    {
        ctl::ComPtr<xaml_markup::IXamlBindScopeLifecycle> lifecycle;
        if (FAILED(ctl::ComPtr<IInspectable>(static_cast<IInspectable*>(scope)).As(&lifecycle)) || !lifecycle)
        {
            return false;
        }
        return SUCCEEDED(lifecycle->InitializeScope());
    }

    bool DetachScope(BindScope::ScopeObject scope) override
    {
        ctl::ComPtr<xaml_markup::IXamlBindScopeLifecycle> lifecycle;
        if (FAILED(ctl::ComPtr<IInspectable>(static_cast<IInspectable*>(scope)).As(&lifecycle)) || !lifecycle)
        {
            return false;
        }
        return SUCCEEDED(lifecycle->DetachScope());
    }

    void AddRefScope(BindScope::ScopeObject scope) override
    {
        if (scope) { static_cast<IInspectable*>(scope)->AddRef(); }
    }

    void ReleaseScope(BindScope::ScopeObject scope) override
    {
        if (scope) { static_cast<IInspectable*>(scope)->Release(); }
    }

    void ClearPendingScope() { m_pendingScope.Reset(); }

    // Base tree revision is stamped by whoever produced the tree. The versioned XBF loader is
    // expected to own writing it; until then a tool can set it explicitly. Kept next to the host so
    // the pure component stays free of any notion of where a revision comes from.
    static std::unordered_map<CDependencyObject*, std::wstring>& BaseTreeRevisions()
    {
        static thread_local std::unordered_map<CDependencyObject*, std::wstring> revisions;
        return revisions;
    }

private:
    static IInspectable* ResolveConnector(_In_ IInspectable* connector)
    {
        return connector;
    }

    ctl::ComPtr<xaml_markup::IComponentConnector> m_pendingScope;
};

RuntimeScopeHost& Host()
{
    static thread_local RuntimeScopeHost host;
    return host;
}

BindScope::ScopeAttachEngine& Engine()
{
    static thread_local BindScope::ScopeAttachEngine engine(Host());
    return engine;
}

// -------------------------------------------------------------------------------------------
// Result marshalling. The component's result is plain C++; the ABI result is the projected struct.
// -------------------------------------------------------------------------------------------

_Check_return_ HRESULT ToAbiResult(const BindScope::AttachResult& source, _Out_ XamlBindScopeAttach::Result* result)
{
    ZeroMemory(result, sizeof(*result));

    result->Status = static_cast<ABI::Microsoft::UI::Xaml::Markup::XamlBindScopeAttachStatus>(source.Status);
    result->FailureDetail = static_cast<ABI::Microsoft::UI::Xaml::Markup::XamlBindScopeFailureDetail>(source.Detail);
    result->OwnedScopeInstanceId = source.OwnedScopeInstanceId;
    result->TargetsConnected = source.TargetsConnected;
    result->TargetsDetached = source.TargetsDetached;
    result->FailedConnectionId = source.FailedConnectionId;

    IFC_RETURN(ToHString(source.ObservedBaseTreeRevision, &result->ObservedBaseTreeRevision));
    IFC_RETURN(ToHString(source.RequestedScopeRevision, &result->RequestedScopeRevision));
    IFC_RETURN(ToHString(source.AppliedScopeRevision, &result->AppliedScopeRevision));
    return S_OK;
}

} // namespace

_Check_return_ HRESULT XamlBindScopeAttach::Attach(
    _In_ xaml::IDependencyObject* root,
    _In_ xaml_markup::IComponentConnector* connector,
    UINT32 idCount, _In_reads_(idCount) INT32* ids,
    UINT32 nameCount, _In_reads_(nameCount) HSTRING* stableNames,
    UINT32 typeCount, _In_reads_(typeCount) HSTRING* typeNames,
    UINT32 objectCount, _In_reads_(objectCount) IInspectable** objects,
    bool allowReplace,
    _Out_ Result* result)
{
    IFCPTR_RETURN(root);
    IFCPTR_RETURN(connector);

    std::vector<BindScope::TargetRow> rows;

    // Array shape is validated by the component, but the arrays have to agree before they can be
    // zipped into rows at all.
    if (idCount != 0 && nameCount == idCount && typeCount == idCount && objectCount == idCount)
    {
        rows.reserve(idCount);
        for (UINT32 i = 0; i < idCount; ++i)
        {
            BindScope::TargetRow row;
            row.ConnectionId = ids[i];
            row.StableName = ToWString(stableNames[i]);
            row.ExpectedTypeName = ToWString(typeNames[i]);
            row.Target = static_cast<BindScope::ScopeObject>(objects[i]);
            rows.push_back(std::move(row));
        }
    }

    ctl::ComPtr<IInspectable> rootAsInspectable = ctl::as_iinspectable(static_cast<DependencyObject*>(root));

    BindScope::AttachResult attachResult = Engine().Attach(
        static_cast<BindScope::ScopeObject>(rootAsInspectable.Get()),
        static_cast<BindScope::ScopeObject>(connector),
        rows,
        allowReplace);

    Host().ClearPendingScope();

    IFC_RETURN(ToAbiResult(attachResult, result));
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::Detach(
    _In_ xaml::IDependencyObject* root,
    _Out_ Result* result)
{
    IFCPTR_RETURN(root);

    ctl::ComPtr<IInspectable> rootAsInspectable = ctl::as_iinspectable(static_cast<DependencyObject*>(root));
    BindScope::AttachResult detachResult = Engine().Detach(static_cast<BindScope::ScopeObject>(rootAsInspectable.Get()));

    IFC_RETURN(ToAbiResult(detachResult, result));
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::GetAttachedScope(
    _In_ xaml::IDependencyObject* root,
    _Outptr_result_maybenull_ xaml_markup::IComponentConnector** scope)
{
    *scope = nullptr;
    IFCPTR_RETURN(root);

    ctl::ComPtr<IInspectable> rootAsInspectable = ctl::as_iinspectable(static_cast<DependencyObject*>(root));
    BindScope::ScopeObject owned = Engine().GetOwnedScope(static_cast<BindScope::ScopeObject>(rootAsInspectable.Get()));
    if (!owned)
    {
        return S_OK;
    }

    ctl::ComPtr<xaml_markup::IComponentConnector> connector;
    IFC_RETURN(ctl::ComPtr<IInspectable>(static_cast<IInspectable*>(owned)).As(&connector));
    IFC_RETURN(connector.CopyTo(scope));
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::GetAttachedScopeRevision(_In_ xaml::IDependencyObject* root, _Out_ HSTRING* revision)
{
    *revision = nullptr;
    IFCPTR_RETURN(root);

    ctl::ComPtr<IInspectable> rootAsInspectable = ctl::as_iinspectable(static_cast<DependencyObject*>(root));
    IFC_RETURN(ToHString(Engine().GetOwnedScopeRevision(static_cast<BindScope::ScopeObject>(rootAsInspectable.Get())), revision));
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::GetBaseTreeRevision(_In_ xaml::IDependencyObject* root, _Out_ HSTRING* revision)
{
    *revision = nullptr;
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    auto& revisions = RuntimeScopeHost::BaseTreeRevisions();
    auto found = revisions.find(rootHandle);
    if (found != revisions.end())
    {
        IFC_RETURN(ToHString(found->second, revision));
    }
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::SetBaseTreeRevision(_In_ xaml::IDependencyObject* root, _In_ HSTRING revision)
{
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    auto& revisions = RuntimeScopeHost::BaseTreeRevisions();
    const std::wstring value = ToWString(revision);
    if (value.empty())
    {
        revisions.erase(rootHandle);
    }
    else
    {
        revisions[rootHandle] = value;
    }
    return S_OK;
}

void XamlBindScopeAttach::OnRootDestroyed(_In_ CDependencyObject* root)
{
    RuntimeScopeHost::BaseTreeRevisions().erase(root);

    ctl::ComPtr<IInspectable> peer;
    if (SUCCEEDED(DXamlServices::TryGetPeer(root, IID_PPV_ARGS(&peer))) && peer)
    {
        Engine().OnRootDestroyed(static_cast<BindScope::ScopeObject>(peer.Get()));
    }
}
