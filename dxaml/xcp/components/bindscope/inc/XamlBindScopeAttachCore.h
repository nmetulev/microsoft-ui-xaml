// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// Live compiled-binding ({x:Bind}) scope attach: the pure state machine.
//
// This component deliberately knows nothing about XAML. It does not include a generated projection
// header, it does not reference DirectUI, and it never touches a DependencyObject. Everything the
// runtime supplies arrives through IScopeHost, and every object is an opaque ScopeObject. That is
// what lets the real production state machine be unit tested in isolation, without building
// Microsoft.UI.Xaml.dll.
//
// Background, for the reader who has not seen the parse path:
//
//   During a cold parse, BinaryFormatObjectWriter::SetConnectionIdOnCurrentInstance
//   (xcp\core\Parser\binaryformatobjectwriter.cpp) calls IComponentConnector::GetBindingConnector on
//   the root connection id to make a scope, then IComponentConnector::Connect for every connection
//   id to populate it. None of that survives: the produced scope is cached only in the object
//   writer's m_qoXBindConnector (xcp\core\inc\BinaryFormatObjectWriter.h) and there is no
//   connectionId -> object map anywhere afterwards. Template scopes are different; they are owned
//   persistently through the attached property read back in CControlTemplate::CreateXBindConnector
//   (xcp\core\core\elements\Template.cpp). The root has no equivalent, so introducing a scope into
//   an already-constructed root means replaying that sequence against an explicitly supplied,
//   validated target map, and owning the result.

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace BindScope {

// An opaque live object. The host is the only thing that knows what it really is.
using ScopeObject = void*;

enum class AttachStatus
{
    Attached = 0,
    Replaced = 1,
    Detached = 2,
    AlreadyAttached = 3,
    Refused = 4,
    Desynchronized = 5,
};

enum class FailureDetail
{
    None = 0,

    // base tree dimension: identity of the XBF/object graph that built the live root
    BaseTreeRevisionUnavailable = 1,
    BaseTreeRevisionMismatch = 2,

    // scope dimension: identity of the newly generated scope and its binding manifest
    ScopeRevisionAlreadyApplied = 3,
    ScopeRevisionConflict = 4,
    ScopeNotProduced = 5,

    // manifest shape
    ManifestShapeInvalid = 6,
    ManifestDuplicateConnectionId = 7,
    ManifestMissingRootRow = 8,

    // per row target identity
    TargetNotResolvable = 9,
    TargetIdentityMismatch = 10,
    TargetTypeMismatch = 11,
    TargetOutsideNamescope = 12,

    NothingAttached = 13,

    DesyncScopeState = 14,
    DesyncBaseTree = 15,

    ScopeLifecycleUnsupported = 16,
    ScopeManifestUnavailable = 17,
    ManifestIncomplete = 18,
};

struct AttachResult
{
    AttachStatus Status = AttachStatus::Refused;
    FailureDetail Detail = FailureDetail::None;
    std::wstring ObservedBaseTreeRevision;
    std::wstring RequestedScopeRevision;
    std::wstring AppliedScopeRevision;
    uint64_t OwnedScopeInstanceId = 0;
    int32_t TargetsConnected = 0;
    int32_t TargetsDetached = 0;
    int32_t FailedConnectionId = -1;
};

// One row of the caller-supplied target map.
struct TargetRow
{
    int32_t ConnectionId = -1;
    std::wstring StableName;        // scope qualified x:Name, empty when the element has none
    std::wstring ExpectedTypeName;  // secondary guard only, checked after identity
    ScopeObject Target = nullptr;
};

// What the generated side declares about itself, so the caller never restates it.
struct ScopeManifest
{
    int32_t RootConnectionId = -1;
    std::vector<int32_t> RequiredConnectionIds;
    std::wstring ScopeRevision;
    std::wstring ExpectedBaseTreeRevision;
};

// Everything the runtime provides. The production adapter implements this over
// CCoreServices::TryGetElementByName, CDependencyObject::GetStandardNameScopeOwner and the
// IComponentConnector ABI; the unit tests implement it over plain maps.
class IScopeHost
{
public:
    virtual ~IScopeHost() = default;

    // Namescope resolution, relative to the root. Returns nullptr when the name does not resolve.
    // The runtime walks the namescope owner chain, which exists from parse time, so this works for a
    // root that was constructed but never realized.
    virtual ScopeObject ResolveName(ScopeObject root, const std::wstring& name) = 0;

    // Namescope owner of an object, used only for rows that carry no stable name.
    virtual ScopeObject GetNamescopeOwner(ScopeObject target) = 0;

    // Object identity. Never a cast: two adjacent elements of the same type must not compare equal.
    virtual bool IsSameInstance(ScopeObject left, ScopeObject right) = 0;

    // Secondary guard, applied only after identity has been established.
    virtual bool TypeNameEquals(ScopeObject target, const std::wstring& expectedTypeName) = 0;

    // Base tree revision stamped on the root by whoever produced the tree.
    virtual std::wstring GetBaseTreeRevision(ScopeObject root) = 0;

    // Connector surface.
    virtual bool TryGetManifest(ScopeObject connector, ScopeManifest* manifest) = 0;
    virtual ScopeObject GetBindingConnector(ScopeObject connector, int32_t rootConnectionId, ScopeObject root) = 0;

    // Scope surface.
    virtual bool SupportsLifecycle(ScopeObject scope) = 0;
    virtual bool Connect(ScopeObject scope, int32_t connectionId, ScopeObject target) = 0;
    virtual bool InitializeScope(ScopeObject scope) = 0;
    virtual bool DetachScope(ScopeObject scope) = 0;

    // Ownership lifetime. The engine holds exactly one reference per owned scope.
    virtual void AddRefScope(ScopeObject scope) = 0;
    virtual void ReleaseScope(ScopeObject scope) = 0;
};

// Seeded defects, compiled only into unit test builds. Each one disables exactly one guard so the
// suite has to prove it actually enforces that guard rather than merely describing it.
enum class Mutant
{
    None = 0,
    SkipBaseTreePrecedence,
    IdentityByTypeNotInstance,
    SkipLifecycleRequirement,
    SkipDetachOnReplace,
    SkipCompletenessCheck,
    BaseTreeFailOpen,
    PublishOwnershipBeforeConnect,
    RepeatAttachReportsAttached,
    SkipRootRowCheck,
};

class ScopeAttachEngine
{
public:
    explicit ScopeAttachEngine(IScopeHost& host) : m_host(host) {}
    ~ScopeAttachEngine();

    ScopeAttachEngine(const ScopeAttachEngine&) = delete;
    ScopeAttachEngine& operator=(const ScopeAttachEngine&) = delete;

    AttachResult Attach(ScopeObject root, ScopeObject connector, const std::vector<TargetRow>& rows, bool allowReplace);
    AttachResult Detach(ScopeObject root);

    // Ownership observability. A caller can prove exactly one scope is owned without trusting a
    // status code.
    ScopeObject GetOwnedScope(ScopeObject root) const;
    std::wstring GetOwnedScopeRevision(ScopeObject root) const;
    bool IsDesynchronized(ScopeObject root) const;

    // Called when a root goes away so a record cannot outlive the tree it describes.
    void OnRootDestroyed(ScopeObject root);

#if defined(__XAML_UNITTESTS__)
    void SetMutant(Mutant mutant) { m_mutant = mutant; }
#endif

private:
    struct Record
    {
        ScopeObject Scope = nullptr;
        std::wstring ScopeRevision;
        std::wstring BaseTreeRevision;
        uint64_t InstanceId = 0;
        int32_t TargetCount = 0;
        bool Desynchronized = false;
    };

    struct ResolvedRow
    {
        int32_t ConnectionId = -1;
        ScopeObject Target = nullptr;
    };

    bool CheckBaseTree(AttachResult& result, const std::wstring& expectedBaseTreeRevision, FailureDetail* fault);
    void PublishRecord(ScopeObject root, Record&& record);
    void DropRecord(ScopeObject root);

    bool IsMutant(Mutant mutant) const
    {
#if defined(__XAML_UNITTESTS__)
        return m_mutant == mutant;
#else
        (void)mutant;
        return false;
#endif
    }

    IScopeHost& m_host;
    std::unordered_map<ScopeObject, Record> m_records;
    uint64_t m_nextInstanceId = 0;

#if defined(__XAML_UNITTESTS__)
    Mutant m_mutant = Mutant::None;
#endif
};

} // namespace BindScope
