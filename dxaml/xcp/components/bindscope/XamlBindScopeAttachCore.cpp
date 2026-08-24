// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include "XamlBindScopeAttachCore.h"

#include <algorithm>

namespace BindScope {

namespace {

AttachResult NewResult()
{
    AttachResult result;
    result.FailedConnectionId = -1;
    return result;
}

AttachResult Refuse(AttachResult result, FailureDetail detail, int32_t failedConnectionId = -1)
{
    result.Status = AttachStatus::Refused;
    result.Detail = detail;
    result.TargetsConnected = 0;
    result.FailedConnectionId = failedConnectionId;
    return result;
}

} // namespace

ScopeAttachEngine::~ScopeAttachEngine()
{
    for (auto& entry : m_records)
    {
        if (entry.second.Scope)
        {
            m_host.ReleaseScope(entry.second.Scope);
        }
    }
    m_records.clear();
}

ScopeObject ScopeAttachEngine::GetOwnedScope(ScopeObject root) const
{
    auto found = m_records.find(root);
    return found == m_records.end() ? nullptr : found->second.Scope;
}

std::wstring ScopeAttachEngine::GetOwnedScopeRevision(ScopeObject root) const
{
    auto found = m_records.find(root);
    return found == m_records.end() ? std::wstring() : found->second.ScopeRevision;
}

bool ScopeAttachEngine::IsDesynchronized(ScopeObject root) const
{
    auto found = m_records.find(root);
    return found != m_records.end() && found->second.Desynchronized;
}

void ScopeAttachEngine::OnRootDestroyed(ScopeObject root)
{
    DropRecord(root);
}

void ScopeAttachEngine::PublishRecord(ScopeObject root, Record&& record)
{
    DropRecord(root);
    if (record.Scope)
    {
        m_host.AddRefScope(record.Scope);
    }
    m_records[root] = std::move(record);
}

void ScopeAttachEngine::DropRecord(ScopeObject root)
{
    auto found = m_records.find(root);
    if (found == m_records.end())
    {
        return;
    }
    if (found->second.Scope)
    {
        m_host.ReleaseScope(found->second.Scope);
    }
    m_records.erase(found);
}

// (1) Base tree revision. Dominates every other fault and fails closed: a scope that asserts
//     nothing, or a root that carries nothing, is refused rather than attached blindly. When the
//     base tree disagrees, every connection id in the map describes a different object graph, so no
//     row can be trusted and no target may be connected.
bool ScopeAttachEngine::CheckBaseTree(AttachResult& result, const std::wstring& expectedBaseTreeRevision, FailureDetail* fault)
{
    *fault = FailureDetail::None;

    const bool missing = expectedBaseTreeRevision.empty() || result.ObservedBaseTreeRevision.empty();
    if (missing)
    {
        if (IsMutant(Mutant::BaseTreeFailOpen))
        {
            return true;
        }
        *fault = FailureDetail::BaseTreeRevisionUnavailable;
        return false;
    }

    if (expectedBaseTreeRevision != result.ObservedBaseTreeRevision)
    {
        *fault = FailureDetail::BaseTreeRevisionMismatch;
        return false;
    }
    return true;
}

AttachResult ScopeAttachEngine::Detach(ScopeObject root)
{
    AttachResult result = NewResult();
    if (!root)
    {
        return Refuse(result, FailureDetail::NothingAttached);
    }

    result.ObservedBaseTreeRevision = m_host.GetBaseTreeRevision(root);

    auto found = m_records.find(root);
    if (found == m_records.end())
    {
        return Refuse(result, FailureDetail::NothingAttached);
    }

    result.AppliedScopeRevision = found->second.ScopeRevision;
    result.TargetsDetached = found->second.TargetCount;

    if (found->second.Scope && m_host.SupportsLifecycle(found->second.Scope))
    {
        m_host.DetachScope(found->second.Scope);
    }

    DropRecord(root);

    result.Status = AttachStatus::Detached;
    result.Detail = FailureDetail::None;
    result.OwnedScopeInstanceId = 0;
    return result;
}

AttachResult ScopeAttachEngine::Attach(
    ScopeObject root,
    ScopeObject connector,
    const std::vector<TargetRow>& rows,
    bool allowReplace)
{
    AttachResult result = NewResult();

    if (!root || !connector)
    {
        return Refuse(result, FailureDetail::ManifestShapeInvalid);
    }

    // (0) The generated side declares its own facts, so a caller never restates them and the engine
    //     has an authoritative statement of what the scope needs.
    ScopeManifest manifest;
    if (!m_host.TryGetManifest(connector, &manifest))
    {
        return Refuse(result, FailureDetail::ScopeManifestUnavailable);
    }

    result.RequestedScopeRevision = manifest.ScopeRevision;
    result.ObservedBaseTreeRevision = m_host.GetBaseTreeRevision(root);

    auto existing = m_records.find(root);
    const bool hasExisting = existing != m_records.end();
    if (hasExisting)
    {
        result.OwnedScopeInstanceId = existing->second.InstanceId;
        result.AppliedScopeRevision = existing->second.ScopeRevision;
    }

    // ---------------------------------------------------------------------------------------
    // PREFLIGHT. Nothing below mutates anything until the section marked MUTATION.
    // Fault precedence is fixed: base tree, then scope state, then manifest shape, then rows.
    // ---------------------------------------------------------------------------------------

    const bool baseFirst = !IsMutant(Mutant::SkipBaseTreePrecedence);
    FailureDetail baseFault = FailureDetail::None;

    if (baseFirst && !CheckBaseTree(result, manifest.ExpectedBaseTreeRevision, &baseFault))
    {
        return Refuse(result, baseFault);
    }

    // (2) Scope state. Only reachable once the base tree agrees.
    if (hasExisting)
    {
        if (manifest.ScopeRevision == existing->second.ScopeRevision)
        {
            if (IsMutant(Mutant::RepeatAttachReportsAttached))
            {
                result.Status = AttachStatus::Attached;
                result.Detail = FailureDetail::None;
                result.TargetsConnected = existing->second.TargetCount;
                return result;
            }

            // Same scope already live. Never connect a second time: that is how duplicate writers
            // and duplicate listener registrations are created.
            result.Status = AttachStatus::AlreadyAttached;
            result.Detail = FailureDetail::ScopeRevisionAlreadyApplied;
            result.TargetsConnected = 0;
            return result;
        }
        if (!allowReplace)
        {
            return Refuse(result, FailureDetail::ScopeRevisionConflict);
        }
    }

    if (!baseFirst && !CheckBaseTree(result, manifest.ExpectedBaseTreeRevision, &baseFault))
    {
        return Refuse(result, baseFault);
    }

    // (3) Manifest shape.
    if (rows.empty())
    {
        return Refuse(result, FailureDetail::ManifestShapeInvalid);
    }
    for (size_t i = 0; i < rows.size(); ++i)
    {
        if (rows[i].ConnectionId < 0)
        {
            return Refuse(result, FailureDetail::ManifestDuplicateConnectionId, rows[i].ConnectionId);
        }
        for (size_t j = i + 1; j < rows.size(); ++j)
        {
            if (rows[i].ConnectionId == rows[j].ConnectionId)
            {
                return Refuse(result, FailureDetail::ManifestDuplicateConnectionId, rows[i].ConnectionId);
            }
        }
    }

    // (3b) Completeness against what the scope declared it needs. Without this, an omitted row
    //      produces a connected scope that is silently inert for that target, which is exactly the
    //      failure mode this contract exists to prevent.
    if (!IsMutant(Mutant::SkipCompletenessCheck))
    {
        for (int32_t required : manifest.RequiredConnectionIds)
        {
            const bool present = std::any_of(rows.begin(), rows.end(),
                [required](const TargetRow& row) { return row.ConnectionId == required; });
            if (!present)
            {
                return Refuse(result, FailureDetail::ManifestIncomplete, required);
            }
        }
    }

    // (4) Per row identity.
    std::vector<ResolvedRow> resolvedRows;
    resolvedRows.reserve(rows.size());

    for (const TargetRow& row : rows)
    {
        ScopeObject resolved = nullptr;
        bool resolvedFromNamescope = false;

        if (!row.StableName.empty())
        {
            resolved = m_host.ResolveName(root, row.StableName);
            if (!resolved)
            {
                return Refuse(result, FailureDetail::TargetNotResolvable, row.ConnectionId);
            }

            // A cast succeeds for either of two adjacent same-typed elements. Identity does not.
            const bool identityOk = IsMutant(Mutant::IdentityByTypeNotInstance)
                ? (row.Target == nullptr || m_host.TypeNameEquals(row.Target, row.ExpectedTypeName))
                : (row.Target == nullptr || m_host.IsSameInstance(resolved, row.Target));

            if (!identityOk)
            {
                return Refuse(result, FailureDetail::TargetIdentityMismatch, row.ConnectionId);
            }

            if (IsMutant(Mutant::IdentityByTypeNotInstance) && row.Target != nullptr)
            {
                // The mutant accepts a type match and then uses the caller's object, which is the
                // silent misconnection.
                resolved = row.Target;
            }
            else
            {
                // Resolution came out of this root's namescope, so membership is already proven and
                // the owner check below would be redundant. This is also what makes a constructed
                // but never realized root work: there is no tree to walk, but the namescope exists.
                resolvedFromNamescope = true;
            }
        }
        else
        {
            resolved = row.Target;
            if (!resolved)
            {
                return Refuse(result, FailureDetail::TargetNotResolvable, row.ConnectionId);
            }
        }

        if (!resolvedFromNamescope
            && !m_host.IsSameInstance(resolved, root)
            && !m_host.IsSameInstance(m_host.GetNamescopeOwner(resolved), root))
        {
            return Refuse(result, FailureDetail::TargetOutsideNamescope, row.ConnectionId);
        }

        // Secondary guard, only after identity has been established.
        if (!row.ExpectedTypeName.empty() && !m_host.TypeNameEquals(resolved, row.ExpectedTypeName))
        {
            return Refuse(result, FailureDetail::TargetTypeMismatch, row.ConnectionId);
        }

        ResolvedRow resolvedRow;
        resolvedRow.ConnectionId = row.ConnectionId;
        resolvedRow.Target = resolved;
        resolvedRows.push_back(resolvedRow);
    }

    // (5) The root row must exist and must be the root itself. A cold parse always calls
    //     Connect(rootConnectionId, root) on the scope it just created.
    if (!IsMutant(Mutant::SkipRootRowCheck))
    {
        auto rootRow = std::find_if(resolvedRows.begin(), resolvedRows.end(),
            [&manifest](const ResolvedRow& r) { return r.ConnectionId == manifest.RootConnectionId; });

        if (rootRow == resolvedRows.end() || !m_host.IsSameInstance(rootRow->Target, root))
        {
            return Refuse(result, FailureDetail::ManifestMissingRootRow, manifest.RootConnectionId);
        }
    }

    // (6) Replay parse order.
    std::sort(resolvedRows.begin(), resolvedRows.end(),
        [](const ResolvedRow& a, const ResolvedRow& b) { return a.ConnectionId < b.ConnectionId; });

    // ---------------------------------------------------------------------------------------
    // MUTATION.
    // ---------------------------------------------------------------------------------------

    // (7) Producing the scope is ordered before detaching the outgoing one, so a connector that
    //     cannot produce a scope is a clean refusal rather than a tree that lost its bindings.
    ScopeObject scope = m_host.GetBindingConnector(connector, manifest.RootConnectionId, root);
    if (!scope)
    {
        return Refuse(result, FailureDetail::ScopeNotProduced, manifest.RootConnectionId);
    }

    // (8) A scope that cannot be initialized or stopped would be attached inert and could never be
    //     replaced without leaking a writer. Refuse rather than report success for a no-op.
    if (!IsMutant(Mutant::SkipLifecycleRequirement) && !m_host.SupportsLifecycle(scope))
    {
        return Refuse(result, FailureDetail::ScopeLifecycleUnsupported, manifest.RootConnectionId);
    }

    Record record;
    record.Scope = scope;
    record.InstanceId = ++m_nextInstanceId;
    record.ScopeRevision = manifest.ScopeRevision;
    record.BaseTreeRevision = manifest.ExpectedBaseTreeRevision;

    // (9) Stop the outgoing scope before anything else writes the same targets.
    const bool replacing = hasExisting;
    if (replacing)
    {
        if (!IsMutant(Mutant::SkipDetachOnReplace) && m_host.SupportsLifecycle(existing->second.Scope))
        {
            m_host.DetachScope(existing->second.Scope);
        }
        result.TargetsDetached = existing->second.TargetCount;
        DropRecord(root);
    }

    if (IsMutant(Mutant::PublishOwnershipBeforeConnect))
    {
        Record early = record;
        early.TargetCount = static_cast<int32_t>(resolvedRows.size());
        PublishRecord(root, std::move(early));
    }

    // (10) Connect.
    for (const ResolvedRow& row : resolvedRows)
    {
        if (!m_host.Connect(scope, row.ConnectionId, row.Target))
        {
            // Partially populated. There is no sound rollback: some targets already carry values
            // written by the new scope. Record the desync so a later attach is refused, and tell the
            // caller exactly where it stopped.
            record.Desynchronized = true;
            record.TargetCount = result.TargetsConnected;
            PublishRecord(root, std::move(record));

            result.Status = AttachStatus::Desynchronized;
            result.Detail = FailureDetail::DesyncScopeState;
            result.FailedConnectionId = row.ConnectionId;
            result.OwnedScopeInstanceId = m_records[root].InstanceId;
            return result;
        }
        ++result.TargetsConnected;
    }

    // (11) The Loading subscription a cold parse relies on has already fired for a live root, so the
    //      first update has to be driven explicitly. Idempotent by contract.
    if (m_host.SupportsLifecycle(scope))
    {
        m_host.InitializeScope(scope);
    }

    // (12) Publish ownership only now.
    record.TargetCount = result.TargetsConnected;
    const uint64_t instanceId = record.InstanceId;
    PublishRecord(root, std::move(record));

    result.OwnedScopeInstanceId = instanceId;
    result.AppliedScopeRevision = manifest.ScopeRevision;
    result.Status = replacing ? AttachStatus::Replaced : AttachStatus::Attached;
    result.Detail = FailureDetail::None;
    return result;
}

} // namespace BindScope
