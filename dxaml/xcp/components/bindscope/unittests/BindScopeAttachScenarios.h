// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// Scenario suite for the live {x:Bind} scope attach state machine.
//
// Deliberately free of any test-framework dependency so it has exactly one definition. The TAEF unit
// test in BindScopeAttachUnitTests.cpp wraps it, and a portable runner can compile the very same
// scenarios against the very same production source without a XAML build.
//
// Nothing here touches XAML: every runtime facility arrives through BindScope::IScopeHost.

#include "XamlBindScopeAttachCore.h"

#include <algorithm>
#include <cstring>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace BindScopeTests {

using namespace BindScope;
const wchar_t* const c_baseLive = L"basetree@gen7(test-token)";
const wchar_t* const c_baseStale = L"basetree@gen6(test-token)";
const wchar_t* const c_scopeV1 = L"scope@v1(test-token)";
const wchar_t* const c_scopeV2 = L"scope@v2(test-token)";

const int32_t c_idRoot = 1;
const int32_t c_idFirst = 2;
const int32_t c_idSecond = 3;

// ---------------------------------------------------------------------------------------------
// Test doubles. Everything the engine needs arrives through IScopeHost, so none of this touches
// XAML and none of it needs a running core.
// ---------------------------------------------------------------------------------------------

struct FakeElement
{
    std::wstring TypeName;
    std::wstring Name;
    void* Owner = nullptr;
    std::wstring Written;
    int WriteCount = 0;

    void Write(const std::wstring& value) { Written = value; ++WriteCount; }
};

struct FakeRoot
{
    std::map<std::wstring, FakeElement*> Names;
    std::wstring BaseTreeRevision;
    std::wstring Value = L"v0";
    std::vector<std::function<void()>*> Listeners;

    void Notify()
    {
        // Copy first: a listener may detach itself while being notified.
        std::vector<std::function<void()>*> snapshot = Listeners;
        for (auto* listener : snapshot)
        {
            (*listener)();
        }
    }

    void SetValue(const std::wstring& value) { Value = value; Notify(); }

    void Subscribe(std::function<void()>* listener) { Listeners.push_back(listener); }

    void Unsubscribe(std::function<void()>* listener)
    {
        Listeners.erase(std::remove(Listeners.begin(), Listeners.end(), listener), Listeners.end());
    }
};

// A scope with real lifecycle: it subscribes on initialize and unsubscribes on detach, so
// "skipping detach leaves a second writer" is directly observable.
struct FakeScope
{
    explicit FakeScope(bool swapPaths) : SwapPaths(swapPaths) {}

    bool SwapPaths = false;
    bool SupportsLifecycle = true;
    FakeRoot* Root = nullptr;
    FakeElement* First = nullptr;
    FakeElement* Second = nullptr;

    bool Initialized = false;
    int InitializeCount = 0;
    int DetachCount = 0;
    int RefCount = 0;
    int FailConnectOnId = -1;

    std::function<void()> Listener;

    void Connect(int32_t connectionId, void* target)
    {
        switch (connectionId)
        {
        case c_idRoot:   Root = static_cast<FakeRoot*>(target); break;
        case c_idFirst:  First = static_cast<FakeElement*>(target); break;
        case c_idSecond: Second = static_cast<FakeElement*>(target); break;
        }
    }

    void Initialize()
    {
        if (Initialized) { return; }   // idempotent by contract
        if (!Root) { return; }         // a seeded defect can reach here with no root row connected
        Initialized = true;
        ++InitializeCount;

        Listener = [this]() { Update(); };
        Root->Subscribe(&Listener);
        Update();
    }

    void Detach()
    {
        if (!Initialized) { return; }
        Initialized = false;
        ++DetachCount;
        Root->Unsubscribe(&Listener);
    }

    void Update()
    {
        if (!Root) { return; }
        if (First) { First->Write((SwapPaths ? L"B:" : L"A:") + Root->Value); }
        if (Second) { Second->Write((SwapPaths ? L"A:" : L"B:") + Root->Value); }
    }
};

struct FakeConnector
{
    ScopeManifest Manifest;
    bool HasManifest = true;
    bool ProducesScope = true;
    bool ScopeSupportsLifecycle = true;
    bool SwapPaths = false;
    int FailConnectOnId = -1;

    std::vector<std::unique_ptr<FakeScope>> Produced;
};

class TestHost final : public IScopeHost
{
public:
    ScopeObject ResolveName(ScopeObject root, const std::wstring& name) override
    {
        auto* fakeRoot = static_cast<FakeRoot*>(root);
        auto found = fakeRoot->Names.find(name);
        return found == fakeRoot->Names.end() ? nullptr : static_cast<ScopeObject>(found->second);
    }

    ScopeObject GetNamescopeOwner(ScopeObject target) override
    {
        // Elements know their owner; a root owns itself.
        for (FakeRoot* root : m_roots)
        {
            if (root == target) { return root; }
        }
        return static_cast<FakeElement*>(target)->Owner;
    }

    bool IsSameInstance(ScopeObject left, ScopeObject right) override { return left == right; }

    bool TypeNameEquals(ScopeObject target, const std::wstring& expectedTypeName) override
    {
        for (FakeRoot* root : m_roots)
        {
            if (root == target) { return expectedTypeName == L"FakeRoot"; }
        }
        return static_cast<FakeElement*>(target)->TypeName == expectedTypeName;
    }

    std::wstring GetBaseTreeRevision(ScopeObject root) override
    {
        return static_cast<FakeRoot*>(root)->BaseTreeRevision;
    }

    bool TryGetManifest(ScopeObject connector, ScopeManifest* manifest) override
    {
        auto* fake = static_cast<FakeConnector*>(connector);
        if (!fake->HasManifest) { return false; }
        *manifest = fake->Manifest;
        return true;
    }

    ScopeObject GetBindingConnector(ScopeObject connector, int32_t rootConnectionId, ScopeObject root) override
    {
        auto* fake = static_cast<FakeConnector*>(connector);
        if (!fake->ProducesScope || rootConnectionId != fake->Manifest.RootConnectionId) { return nullptr; }

        auto scope = std::make_unique<FakeScope>(fake->SwapPaths);
        scope->SupportsLifecycle = fake->ScopeSupportsLifecycle;
        scope->FailConnectOnId = fake->FailConnectOnId;
        scope->Root = static_cast<FakeRoot*>(root);

        FakeScope* raw = scope.get();
        fake->Produced.push_back(std::move(scope));
        return raw;
    }

    bool SupportsLifecycle(ScopeObject scope) override
    {
        return scope != nullptr && static_cast<FakeScope*>(scope)->SupportsLifecycle;
    }

    bool Connect(ScopeObject scope, int32_t connectionId, ScopeObject target) override
    {
        auto* fake = static_cast<FakeScope*>(scope);
        ++ConnectCallCount;

        // Ownership must not be observable while the scope is still being populated: a
        // half-connected scope is not a valid owner.
        if (ObservedEngine && ObservedRoot && ObservedEngine->GetOwnedScope(ObservedRoot) != nullptr)
        {
            SawOwnershipDuringConnect = true;
        }

        if (fake->FailConnectOnId == connectionId) { return false; }
        fake->Connect(connectionId, target);
        return true;
    }

    bool InitializeScope(ScopeObject scope) override
    {
        static_cast<FakeScope*>(scope)->Initialize();
        return true;
    }

    bool DetachScope(ScopeObject scope) override
    {
        static_cast<FakeScope*>(scope)->Detach();
        return true;
    }

    void AddRefScope(ScopeObject scope) override { ++static_cast<FakeScope*>(scope)->RefCount; }
    void ReleaseScope(ScopeObject scope) override { --static_cast<FakeScope*>(scope)->RefCount; }

    void RegisterRoot(FakeRoot* root) { m_roots.push_back(root); }

    // Set to make the host report whether runtime ownership was already visible mid-connect.
    ScopeAttachEngine* ObservedEngine = nullptr;
    ScopeObject ObservedRoot = nullptr;
    bool SawOwnershipDuringConnect = false;

    int ConnectCallCount = 0;
    void ResetInstrumentation() { ConnectCallCount = 0; }

private:
    std::vector<FakeRoot*> m_roots;
};

// A whole world for one scenario: host, root, two adjacent same-typed elements.
struct World
{
    TestHost Host;
    FakeRoot Root;
    FakeElement First;
    FakeElement Second;

    explicit World(const wchar_t* baseTreeRevision = c_baseLive, bool realized = true)
    {
        Root.BaseTreeRevision = baseTreeRevision;

        First.TypeName = L"TextBlock";
        First.Name = L"First";
        First.Owner = realized ? &Root : nullptr;

        Second.TypeName = L"TextBlock";
        Second.Name = L"Second";
        Second.Owner = realized ? &Root : nullptr;

        Root.Names[L"First"] = &First;
        Root.Names[L"Second"] = &Second;
        Host.RegisterRoot(&Root);
    }
};

FakeConnector MakeConnector(const wchar_t* scopeRevision, const wchar_t* expectedBase, bool swapPaths = false)
{
    FakeConnector connector;
    connector.Manifest.RootConnectionId = c_idRoot;
    connector.Manifest.RequiredConnectionIds = { c_idRoot, c_idFirst, c_idSecond };
    connector.Manifest.ScopeRevision = scopeRevision;
    connector.Manifest.ExpectedBaseTreeRevision = expectedBase;
    connector.SwapPaths = swapPaths;
    return connector;
}

std::vector<TargetRow> MakeRows(World& world, bool swapAdjacent = false, bool dropSecond = false)
{
    std::vector<TargetRow> rows;

    TargetRow rootRow;
    rootRow.ConnectionId = c_idRoot;
    rootRow.ExpectedTypeName = L"FakeRoot";
    rootRow.Target = &world.Root;
    rows.push_back(rootRow);

    TargetRow firstRow;
    firstRow.ConnectionId = c_idFirst;
    firstRow.StableName = L"First";
    firstRow.ExpectedTypeName = L"TextBlock";
    firstRow.Target = swapAdjacent ? static_cast<ScopeObject>(&world.Second) : static_cast<ScopeObject>(&world.First);
    rows.push_back(firstRow);

    if (!dropSecond)
    {
        TargetRow secondRow;
        secondRow.ConnectionId = c_idSecond;
        secondRow.StableName = L"Second";
        secondRow.ExpectedTypeName = L"TextBlock";
        secondRow.Target = swapAdjacent ? static_cast<ScopeObject>(&world.First) : static_cast<ScopeObject>(&world.Second);
        rows.push_back(secondRow);
    }

    return rows;
}

// ---------------------------------------------------------------------------------------------
// Scenario suite. Each scenario returns pass/fail rather than asserting, so the same suite can be
// replayed under a seeded defect and the failures counted.
// ---------------------------------------------------------------------------------------------

struct ScenarioOutcome
{
    const wchar_t* Id;
    bool Pass;
    std::wstring Detail;
};

using ScenarioList = std::vector<ScenarioOutcome>;

void Record(ScenarioList& out, const wchar_t* id, bool pass, const std::wstring& detail = std::wstring())
{
    out.push_back({ id, pass, detail });
}

std::wstring Describe(const AttachResult& r)
{
    return L"status=" + std::to_wstring(static_cast<int>(r.Status))
        + L" detail=" + std::to_wstring(static_cast<int>(r.Detail))
        + L" connected=" + std::to_wstring(r.TargetsConnected)
        + L" detached=" + std::to_wstring(r.TargetsDetached)
        + L" owner=" + std::to_wstring(r.OwnedScopeInstanceId)
        + L" failedId=" + std::to_wstring(r.FailedConnectionId);
}

void RunScenarios(Mutant mutant, ScenarioList& out)
{
    // S01 attach connects everything and initializes
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S01",
            r.Status == AttachStatus::Attached
            && r.Detail == FailureDetail::None
            && r.TargetsConnected == 3
            && r.OwnedScopeInstanceId != 0
            && r.AppliedScopeRevision == c_scopeV1
            && r.ObservedBaseTreeRevision == c_baseLive
            && world.First.Written == L"A:v0"
            && world.Second.Written == L"B:v0",
            Describe(r) + L" first=" + world.First.Written + L" second=" + world.Second.Written);
    }

    // S02 repeat attach is refused and never re-connects
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector first = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult a = engine.Attach(&world.Root, &first, MakeRows(world), false);
        ScopeObject scopeAfterFirst = engine.GetOwnedScope(&world.Root);

        world.Host.ResetInstrumentation();
        FakeConnector second = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult b = engine.Attach(&world.Root, &second, MakeRows(world), false);

        Record(out, L"S02",
            b.Status == AttachStatus::AlreadyAttached
            && b.Detail == FailureDetail::ScopeRevisionAlreadyApplied
            && b.TargetsConnected == 0
            && b.OwnedScopeInstanceId == a.OwnedScopeInstanceId
            && engine.GetOwnedScope(&world.Root) == scopeAfterFirst
            && world.Host.ConnectCallCount == 0,
            Describe(b) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S03 a different revision is refused by plain attach, on the scope dimension
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector v1 = MakeConnector(c_scopeV1, c_baseLive);
        engine.Attach(&world.Root, &v1, MakeRows(world), false);

        FakeConnector v2 = MakeConnector(c_scopeV2, c_baseLive, true);
        AttachResult r = engine.Attach(&world.Root, &v2, MakeRows(world), false);

        Record(out, L"S03",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::ScopeRevisionConflict
            && r.RequestedScopeRevision == c_scopeV2
            && r.AppliedScopeRevision == c_scopeV1,
            Describe(r));
    }

    // S04 stale base tree refuses before any mutation
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseStale);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S04",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::BaseTreeRevisionMismatch
            && r.TargetsConnected == 0
            && world.Host.ConnectCallCount == 0
            && engine.GetOwnedScope(&world.Root) == nullptr,
            Describe(r) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S05 base fault dominates a simultaneous scope fault
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector v1 = MakeConnector(c_scopeV1, c_baseLive);
        engine.Attach(&world.Root, &v1, MakeRows(world), false);

        FakeConnector v2Stale = MakeConnector(c_scopeV2, c_baseStale, true);
        AttachResult r = engine.Attach(&world.Root, &v2Stale, MakeRows(world), false);

        Record(out, L"S05",
            r.Status == AttachStatus::Refused && r.Detail == FailureDetail::BaseTreeRevisionMismatch,
            Describe(r));
    }

    // S06 an unknown base tree fails closed
    {
        World world(L"");
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S06",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::BaseTreeRevisionUnavailable
            && world.Host.ConnectCallCount == 0,
            Describe(r) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S07 adjacent same-typed elements swapped: identity refuses where a type guard cannot
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        const bool typeGuardAgreesForBoth =
            world.Host.TypeNameEquals(&world.First, L"TextBlock") &&
            world.Host.TypeNameEquals(&world.Second, L"TextBlock");

        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world, true), false);

        Record(out, L"S07",
            typeGuardAgreesForBoth
            && r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::TargetIdentityMismatch
            && r.FailedConnectionId == c_idFirst
            && world.Host.ConnectCallCount == 0,
            Describe(r) + L" typeGuardAgreesForBoth=" + std::to_wstring(typeGuardAgreesForBoth)
            + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S08 incomplete target map refused against connector-declared required ids
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world, false, true), false);

        Record(out, L"S08",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::ManifestIncomplete
            && r.FailedConnectionId == c_idSecond
            && world.Host.ConnectCallCount == 0,
            Describe(r) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S09 a scope that can never be initialized or stopped is refused, not attached
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(L"scope@inert", c_baseLive);
        connector.ScopeSupportsLifecycle = false;
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S09",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::ScopeLifecycleUnsupported
            && engine.GetOwnedScope(&world.Root) == nullptr
            && world.First.Written.empty(),
            Describe(r) + L" written=" + world.First.Written);
    }

    // S10 replace stops the outgoing scope and leaves exactly one writer
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector v1 = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult a = engine.Attach(&world.Root, &v1, MakeRows(world), false);
        FakeScope* outgoing = v1.Produced.empty() ? nullptr : v1.Produced.front().get();

        FakeConnector v2 = MakeConnector(c_scopeV2, c_baseLive, true);
        AttachResult r = engine.Attach(&world.Root, &v2, MakeRows(world), true);

        const int writesBefore = world.First.WriteCount;
        world.Root.SetValue(L"v1");
        const int writesForOneChange = world.First.WriteCount - writesBefore;

        Record(out, L"S10",
            r.Status == AttachStatus::Replaced
            && r.TargetsDetached == 3
            && r.OwnedScopeInstanceId != a.OwnedScopeInstanceId
            && outgoing != nullptr
            && outgoing->DetachCount == 1
            && writesForOneChange == 1
            && world.First.Written == L"B:v1",
            Describe(r)
            + L" outgoingDetach=" + std::to_wstring(outgoing ? outgoing->DetachCount : -1)
            + L" writesPerChange=" + std::to_wstring(writesForOneChange)
            + L" written=" + world.First.Written);
    }

    // S11 detach clears ownership, releases the scope and silences the tree
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector v1 = MakeConnector(c_scopeV1, c_baseLive);
        engine.Attach(&world.Root, &v1, MakeRows(world), false);
        FakeScope* scope = v1.Produced.empty() ? nullptr : v1.Produced.front().get();

        AttachResult d = engine.Detach(&world.Root);
        const std::wstring frozen = world.First.Written;
        world.Root.SetValue(L"v2");

        Record(out, L"S11",
            d.Status == AttachStatus::Detached
            && d.TargetsDetached == 3
            && engine.GetOwnedScope(&world.Root) == nullptr
            && scope != nullptr && scope->RefCount == 0
            && world.First.Written == frozen,
            Describe(d) + L" refCount=" + std::to_wstring(scope ? scope->RefCount : -1)
            + L" written=" + world.First.Written + L" frozen=" + frozen);
    }

    // S12 a second detach is a refusal, not a success
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector v1 = MakeConnector(c_scopeV1, c_baseLive);
        engine.Attach(&world.Root, &v1, MakeRows(world), false);
        engine.Detach(&world.Root);
        AttachResult again = engine.Detach(&world.Root);

        Record(out, L"S12",
            again.Status == AttachStatus::Refused && again.Detail == FailureDetail::NothingAttached,
            Describe(again));
    }

    // S13 a cached, never realized root attaches through the namescope.
    //     The elements have no owner, so a reachability walk would refuse; name resolution must not.
    {
        World world(c_baseLive, /* realized */ false);
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S13",
            r.Status == AttachStatus::Attached
            && r.TargetsConnected == 3
            && world.First.Written == L"A:v0",
            Describe(r) + L" written=" + world.First.Written);
    }

    // S14 an unnamed row whose object belongs to another root is refused
    {
        World world;
        World other;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        world.Host.RegisterRoot(&other.Root);

        std::vector<TargetRow> rows = MakeRows(world);
        rows[1].StableName.clear();               // no stable name, so only reachability can judge it
        rows[1].Target = &other.First;

        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, rows, false);

        Record(out, L"S14",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::TargetOutsideNamescope
            && world.Host.ConnectCallCount == 0,
            Describe(r) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S15 a connector that declares no manifest is refused
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        connector.HasManifest = false;
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S15",
            r.Status == AttachStatus::Refused && r.Detail == FailureDetail::ScopeManifestUnavailable,
            Describe(r));
    }

    // S16 a manifest with no root row is refused
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        std::vector<TargetRow> rows = MakeRows(world);
        rows.erase(rows.begin());                 // drop the root row

        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        connector.Manifest.RequiredConnectionIds = { c_idFirst, c_idSecond };

        AttachResult r = engine.Attach(&world.Root, &connector, rows, false);

        Record(out, L"S16",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::ManifestMissingRootRow
            && world.Host.ConnectCallCount == 0,
            Describe(r) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S17 a connect failure part way through reports desync and names the id
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        connector.FailConnectOnId = c_idSecond;

        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S17",
            r.Status == AttachStatus::Desynchronized
            && r.Detail == FailureDetail::DesyncScopeState
            && r.FailedConnectionId == c_idSecond
            && r.TargetsConnected == 2
            && engine.IsDesynchronized(&world.Root),
            Describe(r));
    }

    // S18 a duplicate connection id is refused
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        std::vector<TargetRow> rows = MakeRows(world);
        rows.push_back(rows[1]);                  // repeat connection id 2

        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, rows, false);

        Record(out, L"S18",
            r.Status == AttachStatus::Refused
            && r.Detail == FailureDetail::ManifestDuplicateConnectionId
            && world.Host.ConnectCallCount == 0,
            Describe(r) + L" connects=" + std::to_wstring(world.Host.ConnectCallCount));
    }

    // S19 ownership is published only after every row has connected
    {
        World world;
        ScopeAttachEngine engine(world.Host);
#if defined(__XAML_UNITTESTS__)
        engine.SetMutant(mutant);
#endif
        world.Host.ObservedEngine = &engine;
        world.Host.ObservedRoot = &world.Root;

        FakeConnector connector = MakeConnector(c_scopeV1, c_baseLive);
        AttachResult r = engine.Attach(&world.Root, &connector, MakeRows(world), false);

        Record(out, L"S19",
            r.Status == AttachStatus::Attached
            && !world.Host.SawOwnershipDuringConnect
            && engine.GetOwnedScope(&world.Root) != nullptr,
            Describe(r) + L" sawOwnershipDuringConnect="
            + std::to_wstring(world.Host.SawOwnershipDuringConnect));
    }
}

struct MutantExpectation
{
    Mutant Which;
    const wchar_t* Name;
    const wchar_t* ExpectedKiller;   // at least this scenario must go red
};

const MutantExpectation c_mutants[] =
{
    { Mutant::SkipBaseTreePrecedence,        L"SkipBaseTreePrecedence",        L"S05" },
    { Mutant::IdentityByTypeNotInstance,     L"IdentityByTypeNotInstance",     L"S07" },
    { Mutant::SkipLifecycleRequirement,      L"SkipLifecycleRequirement",      L"S09" },
    { Mutant::SkipDetachOnReplace,           L"SkipDetachOnReplace",           L"S10" },
    { Mutant::SkipCompletenessCheck,         L"SkipCompletenessCheck",         L"S08" },
    { Mutant::BaseTreeFailOpen,              L"BaseTreeFailOpen",              L"S06" },
    { Mutant::PublishOwnershipBeforeConnect, L"PublishOwnershipBeforeConnect", L"S19" },
    { Mutant::RepeatAttachReportsAttached,   L"RepeatAttachReportsAttached",   L"S02" },
    { Mutant::SkipRootRowCheck,              L"SkipRootRowCheck",              L"S16" },
};

const size_t c_expectedScenarioCount = 19;

inline bool ScenarioPassed(const ScenarioList& list, const wchar_t* id)
{
    for (const ScenarioOutcome& outcome : list)
    {
        if (wcscmp(outcome.Id, id) == 0) { return outcome.Pass; }
    }
    return false;
}

inline size_t CountFailures(const ScenarioList& list)
{
    size_t failed = 0;
    for (const ScenarioOutcome& outcome : list)
    {
        if (!outcome.Pass) { ++failed; }
    }
    return failed;
}

} // namespace BindScopeTests