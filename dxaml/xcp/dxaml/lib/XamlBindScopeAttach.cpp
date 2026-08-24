// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include "XamlBindScopeAttach.h"
#include "DependencyObject.h"
#include "FrameworkElement.g.h"

using namespace DirectUI;

namespace
{
    // COM identity. A successful QueryInterface to a shared interface proves nothing about which
    // element a manifest row refers to, so identity is always compared on IUnknown.
    bool IsSameInstance(_In_opt_ IInspectable* a, _In_opt_ IInspectable* b)
    {
        if (a == b) { return true; }
        if (!a || !b) { return false; }

        ctl::ComPtr<IUnknown> left;
        ctl::ComPtr<IUnknown> right;
        if (FAILED(a->QueryInterface(IID_PPV_ARGS(&left)))) { return false; }
        if (FAILED(b->QueryInterface(IID_PPV_ARGS(&right)))) { return false; }
        return left.Get() == right.Get();
    }

    bool IsEmpty(_In_opt_ HSTRING value)
    {
        return WindowsGetStringLen(value) == 0;
    }

    bool StringsEqual(_In_opt_ HSTRING a, _In_opt_ HSTRING b)
    {
        INT32 comparison = 0;
        return SUCCEEDED(WindowsCompareStringOrdinal(a, b, &comparison)) && comparison == 0;
    }

    void Refuse(
        _Out_ XamlBindScopeAttach::Result* result,
        XamlBindScopeAttach::Detail detail,
        INT32 failedConnectionId = -1)
    {
        result->Status = XamlBindScopeAttach::Status::XamlBindScopeAttachStatus_Refused;
        result->FailureDetail = detail;
        result->TargetsConnected = 0;
        result->FailedConnectionId = failedConnectionId;
    }

    // xstring_ptr::Promote has no HSTRING overload; it promotes to xstring_ptr_storage, xstring_ptr
    // or xruntime_string_ptr. Go through xruntime_string_ptr, which owns a real runtime string
    // handle, and hand ownership of that handle to the caller.
    _Check_return_ HRESULT PromoteToHString(_In_ const xstring_ptr& source, _Out_ HSTRING* result)
    {
        *result = nullptr;
        if (source.IsNullOrEmpty())
        {
            return S_OK;
        }

        xruntime_string_ptr runtimeString;
        IFC_RETURN(source.Promote(&runtimeString));
        *result = runtimeString.DetachHSTRING();
        return S_OK;
    }

    // A row that survived preflight: connection id plus the single live object it refers to.
    struct ResolvedRow
    {
        INT32 ConnectionId = -1;
        ctl::ComPtr<IInspectable> Target;
    };
}

std::unordered_map<CDependencyObject*, XamlBindScopeRecord>& XamlBindScopeAttach::Records()
{
    static thread_local std::unordered_map<CDependencyObject*, XamlBindScopeRecord> records;
    return records;
}

std::unordered_map<CDependencyObject*, xstring_ptr>& XamlBindScopeAttach::BaseTreeRevisions()
{
    static thread_local std::unordered_map<CDependencyObject*, xstring_ptr> revisions;
    return revisions;
}

UINT64 XamlBindScopeAttach::NextInstanceId()
{
    static thread_local UINT64 next = 0;
    return ++next;
}

void XamlBindScopeAttach::OnRootDestroyed(_In_ CDependencyObject* root)
{
    Records().erase(root);
    BaseTreeRevisions().erase(root);
}

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
    ZeroMemory(result, sizeof(*result));
    result->FailedConnectionId = -1;

    IFCPTR_RETURN(root);
    IFCPTR_RETURN(connector);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    DXamlCore* core = DXamlCore::GetCurrent();
    IFCEXPECT_RETURN(core);

    // (0) The generated side declares its own facts. A tool never restates them, so it cannot get
    //     them wrong, and the runtime has an authoritative statement of what the scope needs.
    ctl::ComPtr<xaml_markup::IXamlBindScopeManifest> manifest;
    if (FAILED(ctl::ComPtr<xaml_markup::IComponentConnector>(connector).As(&manifest)) || !manifest)
    {
        Refuse(result, Detail::XamlBindScopeFailureDetail_ScopeManifestUnavailable);
        return S_OK;
    }

    INT32 rootConnectionId = -1;
    IFC_RETURN(manifest->get_RootConnectionId(&rootConnectionId));

    wrl_wrappers::HString scopeRevision;
    IFC_RETURN(manifest->get_ScopeRevision(scopeRevision.GetAddressOf()));

    wrl_wrappers::HString expectedBaseTreeRevision;
    IFC_RETURN(manifest->get_ExpectedBaseTreeRevision(expectedBaseTreeRevision.GetAddressOf()));

    IFC_RETURN(WindowsDuplicateString(scopeRevision.Get(), &result->RequestedScopeRevision));

    auto& records = Records();
    auto existing = records.find(rootHandle);
    if (existing != records.end())
    {
        result->OwnedScopeInstanceId = existing->second.InstanceId;
        IFC_RETURN(PromoteToHString(existing->second.ScopeRevision, &result->AppliedScopeRevision));
    }

    // ---------------------------------------------------------------------------------------
    // Preflight. Nothing below this block mutates anything until the section marked MUTATION.
    // Fault precedence is fixed: base tree first, then scope state, then manifest shape, then rows.
    // ---------------------------------------------------------------------------------------

    // (1) Base tree revision. Dominates every other fault, and fails closed: a scope that asserts
    //     nothing, or a root that carries nothing, is refused rather than attached blindly. When the
    //     base tree disagrees the connection ids in the manifest describe a different object graph,
    //     so no row can be trusted and no target may be connected.
    {
        auto revision = BaseTreeRevisions().find(rootHandle);
        if (revision != BaseTreeRevisions().end())
        {
            IFC_RETURN(PromoteToHString(revision->second, &result->ObservedBaseTreeRevision));
        }

        if (IsEmpty(expectedBaseTreeRevision.Get()) || IsEmpty(result->ObservedBaseTreeRevision))
        {
            Refuse(result, Detail::XamlBindScopeFailureDetail_BaseTreeRevisionUnavailable);
            return S_OK;
        }
        if (!StringsEqual(expectedBaseTreeRevision.Get(), result->ObservedBaseTreeRevision))
        {
            Refuse(result, Detail::XamlBindScopeFailureDetail_BaseTreeRevisionMismatch);
            return S_OK;
        }
    }

    // (2) Scope state. Only reachable once the base tree agrees.
    if (existing != records.end())
    {
        if (StringsEqual(scopeRevision.Get(), result->AppliedScopeRevision))
        {
            // Same scope already live. Never connect a second time: that is how duplicate writers
            // and duplicate listener registrations are created.
            result->Status = Status::XamlBindScopeAttachStatus_AlreadyAttached;
            result->FailureDetail = Detail::XamlBindScopeFailureDetail_ScopeRevisionAlreadyApplied;
            result->TargetsConnected = 0;
            return S_OK;
        }
        if (!allowReplace)
        {
            Refuse(result, Detail::XamlBindScopeFailureDetail_ScopeRevisionConflict);
            return S_OK;
        }
    }

    // (3) Manifest shape.
    if (idCount == 0 || nameCount != idCount || typeCount != idCount || objectCount != idCount)
    {
        Refuse(result, Detail::XamlBindScopeFailureDetail_ManifestShapeInvalid);
        return S_OK;
    }

    for (UINT32 i = 0; i < idCount; ++i)
    {
        if (ids[i] < 0)
        {
            Refuse(result, Detail::XamlBindScopeFailureDetail_ManifestDuplicateConnectionId, ids[i]);
            return S_OK;
        }
        for (UINT32 j = i + 1; j < idCount; ++j)
        {
            if (ids[i] == ids[j])
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_ManifestDuplicateConnectionId, ids[i]);
                return S_OK;
            }
        }
    }

    // (3b) Completeness. Every id the scope declared it will populate must have a row. Without this
    //      an omitted row produces a scope that is connected, reports success and is silently inert
    //      for that target, which is precisely the failure mode this contract exists to prevent.
    {
        UINT32 requiredCount = 0;
        INT32* requiredIds = nullptr;
        IFC_RETURN(manifest->GetRequiredConnectionIds(&requiredCount, &requiredIds));
        auto freeRequired = wil::scope_exit([&requiredIds] { CoTaskMemFree(requiredIds); });

        for (UINT32 r = 0; r < requiredCount; ++r)
        {
            bool found = false;
            for (UINT32 i = 0; i < idCount && !found; ++i)
            {
                found = (ids[i] == requiredIds[r]);
            }
            if (!found)
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_ManifestIncomplete, requiredIds[r]);
                return S_OK;
            }
        }
    }

    // (4) Row identity. The runtime resolves every named row itself, out of the root's namescope,
    //     and requires the caller's object to be that same instance. This is what catches two
    //     adjacent same-typed elements being swapped in the manifest: both casts would succeed, but
    //     only one instance is the one FindName returns for that name.
    std::vector<ResolvedRow> rows;
    rows.reserve(idCount);

    for (UINT32 i = 0; i < idCount; ++i)
    {
        ResolvedRow row;
        row.ConnectionId = ids[i];
        bool resolvedFromNamescope = false;

        if (!IsEmpty(stableNames[i]))
        {
            auto named = core->GetHandle()->TryGetElementByName(xephemeral_string_ptr(stableNames[i]), rootHandle);
            if (!named)
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_TargetNotResolvable, row.ConnectionId);
                return S_OK;
            }

            ctl::ComPtr<DependencyObject> peer;
            IFC_RETURN(core->GetPeer(named.get(), &peer));
            row.Target = ctl::as_iinspectable(peer.Get());

            if (objects[i] && !IsSameInstance(row.Target.Get(), objects[i]))
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_TargetIdentityMismatch, row.ConnectionId);
                return S_OK;
            }

            // Resolution came out of this root's namescope, so membership is already proven and the
            // owner check below would be redundant.
            resolvedFromNamescope = true;
        }
        else
        {
            if (!objects[i])
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_TargetNotResolvable, row.ConnectionId);
                return S_OK;
            }
            row.Target = objects[i];
        }

        // Reachability, for unnamed rows only. An unnamed row carries no independent identity, so
        // require the object to belong to this root's namescope; without it an object from another
        // live instance of the same page would be accepted.
        //
        // GetStandardNameScopeOwner walks the namescope owner chain, which exists from parse time.
        // That matters: an app-level equivalent built on the visual tree cannot validate a root that
        // has been constructed but never realized, which is exactly the cached-instance case this
        // feature has to support.
        ctl::ComPtr<xaml::IDependencyObject> targetAsDO;
        if (!resolvedFromNamescope && SUCCEEDED(row.Target.As(&targetAsDO)) && targetAsDO)
        {
            CDependencyObject* targetHandle = static_cast<DependencyObject*>(targetAsDO.Get())->GetHandle();
            if (targetHandle != rootHandle && targetHandle->GetStandardNameScopeOwner() != rootHandle)
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_TargetOutsideNamescope, row.ConnectionId);
                return S_OK;
            }
        }

        // Secondary guard only, and only after identity has been established.
        if (!IsEmpty(typeNames[i]))
        {
            wrl_wrappers::HString actualTypeName;
            IFC_RETURN(row.Target->GetRuntimeClassName(actualTypeName.GetAddressOf()));
            if (!StringsEqual(actualTypeName.Get(), typeNames[i]))
            {
                Refuse(result, Detail::XamlBindScopeFailureDetail_TargetTypeMismatch, row.ConnectionId);
                return S_OK;
            }
        }

        rows.push_back(std::move(row));
    }

    // (5) The root row must exist and must be the root itself. A cold parse always calls
    //     Connect(rootConnectionId, root) on the scope it just created.
    {
        ctl::ComPtr<IInspectable> rootAsInspectable = ctl::as_iinspectable(static_cast<DependencyObject*>(root));
        auto rootRow = std::find_if(rows.begin(), rows.end(), [&](const ResolvedRow& r)
        {
            return r.ConnectionId == rootConnectionId;
        });
        if (rootRow == rows.end() || !IsSameInstance(rootRow->Target.Get(), rootAsInspectable.Get()))
        {
            Refuse(result, Detail::XamlBindScopeFailureDetail_ManifestMissingRootRow, rootConnectionId);
            return S_OK;
        }
    }

    // Replay parse order.
    std::sort(rows.begin(), rows.end(), [](const ResolvedRow& a, const ResolvedRow& b)
    {
        return a.ConnectionId < b.ConnectionId;
    });

    // ---------------------------------------------------------------------------------------
    // MUTATION.
    // ---------------------------------------------------------------------------------------

    // Producing the scope is ordered before detaching the outgoing one so that a connector that
    // cannot produce a scope is a clean refusal rather than a tree that lost its bindings.
    ctl::ComPtr<IInspectable> rootAsInspectable = ctl::as_iinspectable(static_cast<DependencyObject*>(root));
    ctl::ComPtr<xaml_markup::IComponentConnector> scope;
    IFC_RETURN(connector->GetBindingConnector(rootConnectionId, rootAsInspectable.Get(), &scope));
    if (!scope)
    {
        Refuse(result, Detail::XamlBindScopeFailureDetail_ScopeNotProduced, rootConnectionId);
        return S_OK;
    }

    // A scope that cannot be initialized or stopped would be attached inert and could never be
    // replaced without leaking a writer. Refuse rather than report success for a no-op.
    ctl::ComPtr<xaml_markup::IXamlBindScopeLifecycle> lifecycle;
    if (FAILED(scope.As(&lifecycle)) || !lifecycle)
    {
        Refuse(result, Detail::XamlBindScopeFailureDetail_ScopeLifecycleUnsupported, rootConnectionId);
        return S_OK;
    }

    const bool replacing = existing != records.end();
    if (replacing)
    {
        ctl::ComPtr<xaml_markup::IXamlBindScopeLifecycle> outgoing;
        if (SUCCEEDED(existing->second.Scope.As(&outgoing)) && outgoing)
        {
            IFC_RETURN(outgoing->DetachScope());
        }
        result->TargetsDetached = existing->second.TargetCount;
        records.erase(existing);
    }

    XamlBindScopeRecord record;
    record.Scope = scope;
    record.InstanceId = NextInstanceId();
    IFC_RETURN(xstring_ptr::CloneRuntimeStringHandle(scopeRevision.Get(), &record.ScopeRevision));
    IFC_RETURN(xstring_ptr::CloneRuntimeStringHandle(expectedBaseTreeRevision.Get(), &record.BaseTreeRevision));

    for (const auto& row : rows)
    {
        HRESULT connectHr = scope->Connect(row.ConnectionId, row.Target.Get());
        if (FAILED(connectHr))
        {
            // Partially populated. There is no sound rollback: some targets already carry values
            // written by the new scope. Record the record as desynchronized so a later attach is
            // refused, and tell the caller exactly where it stopped.
            record.Desynchronized = true;
            record.TargetCount = result->TargetsConnected;
            records[rootHandle] = std::move(record);

            result->Status = Status::XamlBindScopeAttachStatus_Desynchronized;
            result->FailureDetail = Detail::XamlBindScopeFailureDetail_DesyncScopeState;
            result->FailedConnectionId = row.ConnectionId;
            return S_OK;
        }
        ++result->TargetsConnected;
    }

    // The Loading subscription a cold parse relies on has already fired for a live root, so the
    // first update has to be driven explicitly. Idempotent by contract.
    IFC_RETURN(lifecycle->InitializeScope());

    record.TargetCount = result->TargetsConnected;
    result->OwnedScopeInstanceId = record.InstanceId;
    IFC_RETURN(WindowsDuplicateString(scopeRevision.Get(), &result->AppliedScopeRevision));
    records[rootHandle] = std::move(record);

    result->Status = replacing
        ? Status::XamlBindScopeAttachStatus_Replaced
        : Status::XamlBindScopeAttachStatus_Attached;
    result->FailureDetail = Detail::XamlBindScopeFailureDetail_None;
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::Detach(
    _In_ xaml::IDependencyObject* root,
    _Out_ Result* result)
{
    ZeroMemory(result, sizeof(*result));
    result->FailedConnectionId = -1;
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    auto revision = BaseTreeRevisions().find(rootHandle);
    if (revision != BaseTreeRevisions().end())
    {
        IFC_RETURN(PromoteToHString(revision->second, &result->ObservedBaseTreeRevision));
    }

    auto& records = Records();
    auto existing = records.find(rootHandle);
    if (existing == records.end())
    {
        Refuse(result, Detail::XamlBindScopeFailureDetail_NothingAttached);
        return S_OK;
    }

    IFC_RETURN(PromoteToHString(existing->second.ScopeRevision, &result->AppliedScopeRevision));
    result->TargetsDetached = existing->second.TargetCount;

    ctl::ComPtr<xaml_markup::IXamlBindScopeLifecycle> lifecycle;
    if (SUCCEEDED(existing->second.Scope.As(&lifecycle)) && lifecycle)
    {
        IFC_RETURN(lifecycle->DetachScope());
    }

    records.erase(existing);

    result->Status = Status::XamlBindScopeAttachStatus_Detached;
    result->FailureDetail = Detail::XamlBindScopeFailureDetail_None;
    result->OwnedScopeInstanceId = 0;
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::GetAttachedScope(
    _In_ xaml::IDependencyObject* root,
    _Outptr_result_maybenull_ xaml_markup::IComponentConnector** scope)
{
    *scope = nullptr;
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    auto& records = Records();
    auto existing = records.find(rootHandle);
    if (existing != records.end())
    {
        IFC_RETURN(existing->second.Scope.CopyTo(scope));
    }
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::GetAttachedScopeRevision(_In_ xaml::IDependencyObject* root, _Out_ HSTRING* revision)
{
    *revision = nullptr;
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    auto& records = Records();
    auto existing = records.find(rootHandle);
    if (existing != records.end())
    {
        IFC_RETURN(PromoteToHString(existing->second.ScopeRevision, revision));
    }
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::GetBaseTreeRevision(_In_ xaml::IDependencyObject* root, _Out_ HSTRING* revision)
{
    *revision = nullptr;
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    auto& revisions = BaseTreeRevisions();
    auto existing = revisions.find(rootHandle);
    if (existing != revisions.end())
    {
        IFC_RETURN(PromoteToHString(existing->second, revision));
    }
    return S_OK;
}

_Check_return_ HRESULT XamlBindScopeAttach::SetBaseTreeRevision(_In_ xaml::IDependencyObject* root, _In_ HSTRING revision)
{
    IFCPTR_RETURN(root);

    CDependencyObject* rootHandle = static_cast<DependencyObject*>(root)->GetHandle();
    IFCEXPECT_RETURN(rootHandle);

    if (IsEmpty(revision))
    {
        BaseTreeRevisions().erase(rootHandle);
        return S_OK;
    }

    xstring_ptr stored;
    IFC_RETURN(xstring_ptr::CloneRuntimeStringHandle(revision, &stored));
    BaseTreeRevisions()[rootHandle] = std::move(stored);
    return S_OK;
}
