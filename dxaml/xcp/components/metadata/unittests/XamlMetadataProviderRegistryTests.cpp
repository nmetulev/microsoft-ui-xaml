// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Tests for the rules behind XamlMetadataProviderRegistry (core\inc\XamlMetadataProviderRegistry.h): lookup order
// among registered providers, ambiguity, conflicts, co-ownership of shared types, what unregistration orphans, and
// the begin/probe/commit protocol that lets MetadataAPI call providers without holding its lock. The header is
// standard C++ only, so this builds with cl.exe alone (run-portable.ps1).
//
// MetadataUnitTests.cpp (TAEF) covers the MetadataAPI side: the type caches, placeholders and IXamlType plumbing.

#include "XamlMetadataProviderRegistry.h"

#include <cstdio>
#include <map>
#include <memory>
#include <string>
#include <vector>

using namespace XamlMetadataProviderRegistry;

namespace
{
    int g_failures = 0;
    int g_checks = 0;

    void Check(bool condition, const wchar_t* what)
    {
        ++g_checks;
        if (!condition)
        {
            ++g_failures;
            std::wprintf(L"  FAILED: %s\n", what);
        }
    }

    // A provider: lookup name -> the type it says that name is.
    struct FakeProvider : std::enable_shared_from_this<FakeProvider>
    {
        std::map<std::wstring, TypeKey<std::wstring>> Types;
        bool Fails = false;
        int Calls = 0;

        std::shared_ptr<FakeProvider> With(const std::wstring& name, const std::wstring& underlying, bool custom = false)
        {
            Types[name] = TypeKey<std::wstring>{ underlying, custom };
            return shared_from_this();
        }
    };

    using Provider = std::shared_ptr<FakeProvider>;
    using Reg = Registry<Provider, std::wstring, std::wstring>;
    using Key = TypeKey<std::wstring>;
    using LookupW = Lookup<std::wstring>;

    Provider MakeProvider()
    {
        return std::make_shared<FakeProvider>();
    }

    // The host's side of Register: begin under the lock, probe the candidate, commit. MetadataAPI does the same with
    // CStaticLock and IXamlMetadataProvider.
    Reg::RegisterResult Register(Reg& registry, const Provider& provider)
    {
        const void* identity = provider.get();
        const auto ticket = registry.BeginRegister(provider, identity);
        std::vector<Reg::ProbeAnswer> answers;
        for (const auto& probe : ticket.Probes)
        {
            Reg::ProbeAnswer answer;
            ++provider->Calls;
            if (provider->Fails)
            {
                answer.Failed = true;
            }
            else
            {
                const auto it = provider->Types.find(probe.Lookup.Name);
                if (it != provider->Types.end())
                {
                    answer.Answered = true;
                    answer.Actual = it->second;
                }
            }
            answers.push_back(answer);
        }
        return registry.CommitRegister(provider, identity, ticket, answers);
    }

    // The host's side of a lookup: snapshot, ask outside the lock, record the claim.
    Reg::ResolveOutcome Resolve(const Reg& registry, const std::wstring& name)
    {
        return Reg::Resolve(registry.Snapshot(), [&](const Reg::Entry& entry, Key& key, std::wstring& answer)
        {
            ++entry.Provider->Calls;
            const auto it = entry.Provider->Types.find(name);
            if (it == entry.Provider->Types.end())
            {
                return false;
            }
            key = it->second;
            answer = L"from#" + std::to_wstring(entry.Id);
            return true;
        });
    }

    ClaimId ResolveAndClaim(Reg& registry, const std::wstring& name, Reg::ResolveOutcome* outcomeOut = nullptr)
    {
        const auto outcome = Resolve(registry, name);
        if (outcomeOut) *outcomeOut = outcome;
        return registry.RecordClaim(LookupW{ name, false, 0 }, outcome);
    }

    void Registration()
    {
        Reg registry;
        auto a = MakeProvider()->With(L"Plugin.A", L"Plugin.A");

        const auto first = Register(registry, a);
        Check(first.Status == RegistryStatus::Succeeded && first.Id != c_invalidProviderId, L"a provider registers and gets an id");
        Check(first.Generation == 1 && registry.GetGeneration() == 1, L"registration bumps the generation");
        Check(registry.IsRegistered(a.get()) && registry.Count() == 1, L"the provider is registered");

        const auto again = Register(registry, a);
        Check(again.Status == RegistryStatus::AlreadyRegistered && again.Id == first.Id, L"registering again reports the original id");
        Check(again.Generation == first.Generation && registry.Count() == 1, L"registering again changes nothing");

        const auto nullResult = Register(registry, Provider{});
        Check(nullResult.Status == RegistryStatus::Refused && nullResult.Reason == RefusalReason::NullProvider,
            L"a null provider is refused");
        Check(registry.GetGeneration() == 1, L"a refusal doesn't bump the generation");
    }

    void Capacity()
    {
        Reg registry;
        std::vector<Provider> providers;
        for (std::size_t i = 0; i < c_maxProviders; ++i)
        {
            providers.push_back(MakeProvider());
            Register(registry, providers.back());
        }
        Check(registry.Count() == c_maxProviders, L"the registry accepts up to c_maxProviders providers");

        auto extra = MakeProvider();
        const auto full = Register(registry, extra);
        Check(full.Status == RegistryStatus::Refused && full.Reason == RefusalReason::RegistryFull, L"one more is refused");

        Check(registry.Unregister(providers.front().get()).Status == RegistryStatus::Succeeded, L"removing one makes room");
        Check(Register(registry, extra).Status == RegistryStatus::Succeeded, L"and the next one fits");
    }

    void LookupRules()
    {
        Reg registry;
        Check(Resolve(registry, L"Plugin.A").Status == Reg::ResolveStatus::NotFound, L"nothing resolves with no providers");

        auto a = MakeProvider()->With(L"Plugin.A", L"Plugin.A");
        auto b = MakeProvider()->With(L"Plugin.B", L"Plugin.B");
        Register(registry, a);
        Register(registry, b);

        const auto resolvedA = Resolve(registry, L"Plugin.A");
        Check(resolvedA.Status == Reg::ResolveStatus::Resolved && resolvedA.Owners.size() == 1, L"a name one provider knows resolves");
        Check(Resolve(registry, L"Plugin.C").Status == Reg::ResolveStatus::NotFound, L"a name nobody knows doesn't");

        // Every provider is asked even after one answered, so a duplicate can't hide behind registration order.
        const int callsBefore = a->Calls + b->Calls;
        Resolve(registry, L"Plugin.A");
        Check(a->Calls + b->Calls == callsBefore + 2, L"every registered provider is asked");
    }

    void SharedDependency()
    {
        // Two plugins that both reference one control library both describe its types. Same underlying type, so
        // it's one type with two owners, not an ambiguity.
        Reg registry;
        auto pluginOne = MakeProvider()->With(L"Shared.Card", L"Shared.Card, Shared, Version=1.0.0.0", true);
        auto pluginTwo = MakeProvider()->With(L"Shared.Card", L"Shared.Card, Shared, Version=1.0.0.0", true);
        Register(registry, pluginOne);
        Register(registry, pluginTwo);

        Reg::ResolveOutcome outcome;
        const ClaimId claim = ResolveAndClaim(registry, L"Shared.Card", &outcome);
        Check(outcome.Status == Reg::ResolveStatus::Resolved, L"a type two providers agree on resolves");
        Check(outcome.Owners.size() == 2, L"both providers own it");
        Check(outcome.Answer == L"from#1", L"the answer comes from the earliest registered provider");
        Check(claim != c_invalidClaimId && registry.OwnersOf(claim).size() == 2, L"the claim records both owners");

        const auto removed = registry.Unregister(pluginOne.get());
        Check(removed.OrphanedClaims.empty(), L"removing one owner orphans nothing");
        Check(registry.OwnersOf(claim) == std::vector<ProviderId>{ 2 }, L"the other owner keeps the type");

        const auto removedLast = registry.Unregister(pluginTwo.get());
        Check(removedLast.OrphanedClaims == std::vector<ClaimId>{ claim }, L"removing the last owner orphans the type");
        Check(registry.ClaimCount() == 0, L"and forgets the claim");
    }

    void Ambiguity()
    {
        // Two different libraries both define Contoso.Card: different types under one name.
        for (int order = 0; order < 2; ++order)
        {
            Reg registry;
            auto v1 = MakeProvider()->With(L"Contoso.Card", L"Contoso.Card, CardsV1, Version=1.0.0.0", true);
            auto v2 = MakeProvider()->With(L"Contoso.Card", L"Contoso.Card, CardsV2, Version=2.0.0.0", true);
            Register(registry, order == 0 ? v1 : v2);
            Register(registry, order == 0 ? v2 : v1);

            Reg::ResolveOutcome outcome;
            const ClaimId claim = ResolveAndClaim(registry, L"Contoso.Card", &outcome);
            Check(outcome.Status == Reg::ResolveStatus::Ambiguous, order == 0
                ? L"different types under one name are ambiguous"
                : L"... in either registration order");
            Check(outcome.Answer.empty() && outcome.Owners.empty(), L"an ambiguous name resolves to nothing");
            Check(claim == c_invalidClaimId && registry.ClaimCount() == 0, L"and claims nothing");
        }
    }

    void Conflicts()
    {
        Reg registry;
        auto cards = MakeProvider()->With(L"Contoso.Card", L"Contoso.Card, CardsV1", true)->With(L"Contoso.Deck", L"Contoso.Deck, CardsV1", true);
        Register(registry, cards);
        const ClaimId claim = ResolveAndClaim(registry, L"Contoso.Card");
        Check(claim != c_invalidClaimId, L"the name resolves and is claimed");

        const auto generation = registry.GetGeneration();
        auto rival = MakeProvider()->With(L"Contoso.Card", L"Contoso.Card, CardsV2", true);
        const auto conflict = Register(registry, rival);
        Check(conflict.Status == RegistryStatus::Conflict, L"a provider redefining a resolved name is refused");
        Check(conflict.ConflictingName == L"Contoso.Card", L"the refusal names the type");
        Check(!registry.IsRegistered(rival.get()) && registry.GetGeneration() == generation, L"and nothing changed");

        // Conflicts are only about names already in use. Contoso.Deck was never resolved, so a rival for it alone
        // registers, and the name becomes ambiguous instead of silently binding to whichever came first.
        auto deckRival = MakeProvider()->With(L"Contoso.Deck", L"Contoso.Deck, CardsV2", true);
        Check(Register(registry, deckRival).Status == RegistryStatus::Succeeded, L"a rival for an unused name registers");
        Check(Resolve(registry, L"Contoso.Deck").Status == Reg::ResolveStatus::Ambiguous, L"and that name is ambiguous");

        // A newcomer that agrees about a resolved name joins as a co-owner.
        auto agrees = MakeProvider()->With(L"Contoso.Card", L"Contoso.Card, CardsV1", true);
        Check(Register(registry, agrees).Status == RegistryStatus::Succeeded, L"a provider agreeing about a resolved name registers");
        Check(registry.OwnersOf(claim).size() == 2, L"and co-owns it");
        Check(registry.Unregister(cards.get()).OrphanedClaims.empty(), L"so removing the original keeps the type");
    }

    void Unregistration()
    {
        Reg registry;
        auto a = MakeProvider()->With(L"Plugin.A", L"Plugin.A")->With(L"Plugin.Base", L"Plugin.Base");
        auto b = MakeProvider()->With(L"Plugin.B", L"Plugin.B");
        Register(registry, a);
        Register(registry, b);
        const ClaimId claimA = ResolveAndClaim(registry, L"Plugin.A");
        const ClaimId claimBase = ResolveAndClaim(registry, L"Plugin.Base");
        const ClaimId claimB = ResolveAndClaim(registry, L"Plugin.B");

        const auto generation = registry.GetGeneration();
        const auto removed = registry.Unregister(a.get());
        Check(removed.Status == RegistryStatus::Succeeded && removed.Id == 1, L"a registered provider is removed");
        Check(removed.Generation == generation + 1, L"removal bumps the generation");
        Check(removed.OrphanedClaims.size() == 2 &&
              std::find(removed.OrphanedClaims.begin(), removed.OrphanedClaims.end(), claimA) != removed.OrphanedClaims.end() &&
              std::find(removed.OrphanedClaims.begin(), removed.OrphanedClaims.end(), claimBase) != removed.OrphanedClaims.end(),
            L"every type only it supplied is orphaned");
        Check(registry.OwnersOf(claimB).size() == 1, L"other providers' types are untouched");
        Check(Resolve(registry, L"Plugin.A").Status == Reg::ResolveStatus::NotFound, L"its names stop resolving");
        Check(!registry.IsRegistered(a.get()), L"it is no longer registered");

        const auto again = registry.Unregister(a.get());
        Check(again.Status == RegistryStatus::Refused && again.Reason == RefusalReason::NotRegistered, L"removing twice is refused");
        Check(again.Generation == removed.Generation, L"without bumping the generation");
        Check(registry.Unregister(nullptr).Reason == RefusalReason::NullProvider, L"removing null is refused");

        const auto back = Register(registry, a);
        Check(back.Status == RegistryStatus::Succeeded && back.Id != removed.Id, L"re-registering gives a new id");
        Check(ResolveAndClaim(registry, L"Plugin.A") != claimA, L"and a new claim, so stale cache tags never match it");
    }

    void ConcurrentChanges()
    {
        Reg registry;
        auto a = MakeProvider()->With(L"Plugin.A", L"Plugin.A");
        auto b = MakeProvider();

        // Another registration lands between begin and commit.
        const auto ticket = registry.BeginRegister(a, a.get());
        Register(registry, b);
        const auto raced = registry.CommitRegister(a, a.get(), ticket, {});
        Check(raced.Status == RegistryStatus::Refused && raced.Reason == RefusalReason::ConcurrentModification,
            L"a registry change during the probe refuses the commit");
        Check(!registry.IsRegistered(a.get()), L"the raced provider isn't registered");
        Check(Register(registry, a).Status == RegistryStatus::Succeeded, L"a retry succeeds");

        // A lookup claims a name between begin and commit: the candidate was never asked about it.
        auto c = MakeProvider()->With(L"Plugin.A", L"Plugin.A.Other");
        const auto ticket2 = registry.BeginRegister(c, c.get());
        ResolveAndClaim(registry, L"Plugin.A");
        const auto raced2 = registry.CommitRegister(c, c.get(), ticket2, {});
        Check(raced2.Reason == RefusalReason::ConcurrentModification, L"a new claim during the probe refuses the commit");
        Check(Register(registry, c).Status == RegistryStatus::Conflict, L"and the retry sees the conflict");

        // A provider removed while a lookup it answered was running.
        auto d = MakeProvider()->With(L"Plugin.D", L"Plugin.D");
        Register(registry, d);
        const auto outcome = Resolve(registry, L"Plugin.D");
        registry.Unregister(d.get());
        Check(registry.RecordClaim(LookupW{ L"Plugin.D", false, 0 }, outcome) == c_invalidClaimId,
            L"a lookup answered only by a since-removed provider claims nothing");

        // A provider that fails while being probed.
        auto broken = MakeProvider()->With(L"Plugin.A", L"Plugin.A");
        broken->Fails = true;
        const auto failed = Register(registry, broken);
        Check(failed.Status == RegistryStatus::Refused && failed.Reason == RefusalReason::ProviderFailed, L"a failing probe refuses");
    }

    void Probes()
    {
        Reg registry;
        auto a = MakeProvider()->With(L"Plugin.A", L"Plugin.A, Plugin", true);
        Register(registry, a);

        // The same type looked up two ways: by full name (markup) and by TypeName (typeof(Plugin.A) in code).
        const auto outcome = Resolve(registry, L"Plugin.A");
        const ClaimId byName = registry.RecordClaim(LookupW{ L"Plugin.A", false, 0 }, outcome);
        const ClaimId byTypeName = registry.RecordClaim(LookupW{ L"Plugin.A, Plugin", true, 1 }, outcome);
        Check(byName == byTypeName && registry.ClaimCount() == 1, L"one type is one claim however it was looked up");

        auto candidate = MakeProvider();
        const auto ticket = registry.BeginRegister(candidate, candidate.get());
        Check(ticket.Probes.size() == 2, L"a candidate is asked every way the type was looked up");
        bool sawTypeName = false;
        for (const auto& probe : ticket.Probes)
        {
            sawTypeName |= probe.Lookup.ByTypeName && probe.Lookup.TypeKind == 1 && probe.Lookup.Name == L"Plugin.A, Plugin";
        }
        Check(sawTypeName, L"including by TypeName with the original kind");

        registry.Clear();
        Check(registry.Count() == 0 && registry.ClaimCount() == 0, L"Clear forgets providers and claims");
        Check(registry.CommitRegister(candidate, candidate.get(), ticket, std::vector<Reg::ProbeAnswer>(2)).Reason ==
            RefusalReason::ConcurrentModification, L"and refuses tickets taken before it");
    }
}

int wmain()
{
    std::wprintf(L"=== XAML metadata provider registry tests (standard C++ only) ===\n");
    Registration();
    Capacity();
    LookupRules();
    SharedDependency();
    Ambiguity();
    Conflicts();
    Unregistration();
    ConcurrentChanges();
    Probes();
    std::wprintf(L"%d check(s), %d failed\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
