// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// The rules behind Microsoft.UI.Xaml.Markup.XamlMetadataProviderRegistry: which registered
// IXamlMetadataProvider answers a type name, when a registration is refused, and which cached types an
// unregistration must forget. See docs\api-specs\XamlMetadataProviderRegistry\XamlMetadataProviderRegistry.md.
//
// Standard C++ only, so the rules can be tested without the XAML build
// (components\metadata\unittests\run-portable.ps1). MetadataAPI hosts one instance in DynamicMetadataStorage and
// supplies the locking (CStaticLock), the provider calls (IXamlMetadataProvider) and the type caches.
//
// The registry never calls a provider itself. Anything that calls out to user code is split into a "begin" step
// (under the caller's lock), the provider calls (outside it), and a "commit" step (under the lock again) that
// refuses with ConcurrentModification if the registry changed in between.

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <map>
#include <utility>
#include <vector>

namespace XamlMetadataProviderRegistry
{
    using ProviderId = std::uint64_t;
    using ClaimId = std::uint64_t;
    using RegistryGeneration = std::uint64_t;

    inline constexpr ProviderId c_invalidProviderId = 0;
    inline constexpr ClaimId c_invalidClaimId = 0;

    // Registered providers are asked, in turn, for every name the app's own provider doesn't know, so the number is
    // bounded to keep a miss cheap.
    inline constexpr std::size_t c_maxProviders = 256;

    enum class RegistryStatus : std::uint32_t
    {
        // Register: the provider was added. Unregister: the provider was removed.
        Succeeded = 0,
        // Register only: this provider (same COM identity) was already registered. Nothing changed.
        AlreadyRegistered = 1,
        // Register only: the provider describes a type name that another registered provider already supplied as a
        // *different* type. Nothing changed. ConflictingName says which name.
        Conflict = 2,
        // Nothing changed. Reason says why.
        Refused = 3,
    };

    enum class RefusalReason : std::uint32_t
    {
        None = 0,
        NullProvider = 1,
        // The registry changed while the provider was being probed outside the lock. Safe to retry.
        ConcurrentModification = 2,
        RegistryFull = 3,
        // The provider failed while being probed.
        ProviderFailed = 4,
        // Unregister only: the provider isn't registered.
        NotRegistered = 5,
    };

    // What a provider said a name is: the type's underlying TypeName. Two providers that answer a name with equal
    // keys describe the same type (for example two plugins that both reference one control library), which is not
    // an ambiguity.
    template <typename TName>
    struct TypeKey
    {
        TName Name{};
        bool IsCustom = false;   // TypeKind_Custom (a non-WinRT .NET type), otherwise TypeKind_Metadata/Primitive.

        bool operator==(const TypeKey& other) const { return IsCustom == other.IsCustom && Name == other.Name; }
        bool operator!=(const TypeKey& other) const { return !(*this == other); }
        bool operator<(const TypeKey& other) const
        {
            if (IsCustom != other.IsCustom) return !IsCustom;
            return Name < other.Name;
        }
    };

    // How a name was looked up, so a later probe can ask a new provider the same question.
    template <typename TName>
    struct Lookup
    {
        TName Name{};
        // false: IXamlMetadataProvider::GetXamlTypeByFullName(Name).
        // true: IXamlMetadataProvider::GetXamlType(TypeName{ Name, TypeKind }).
        bool ByTypeName = false;
        std::uint8_t TypeKind = 0;

        bool operator==(const Lookup& other) const
        {
            return ByTypeName == other.ByTypeName && TypeKind == other.TypeKind && Name == other.Name;
        }
    };

    template <typename TName>
    struct RegisterResult
    {
        RegistryStatus Status = RegistryStatus::Refused;
        RefusalReason Reason = RefusalReason::None;
        ProviderId Id = c_invalidProviderId;
        RegistryGeneration Generation = 0;
        TName ConflictingName{};

        bool IsRegistered() const { return Status == RegistryStatus::Succeeded || Status == RegistryStatus::AlreadyRegistered; }
    };

    struct UnregisterResult
    {
        RegistryStatus Status = RegistryStatus::Refused;
        RefusalReason Reason = RefusalReason::None;
        ProviderId Id = c_invalidProviderId;
        RegistryGeneration Generation = 0;
        // Types no remaining provider supplies. The host must forget every cached type carrying one of these.
        std::vector<ClaimId> OrphanedClaims;
    };

    // Not thread-safe: the host serializes every call (MetadataAPI holds CStaticLock), except Resolve, which only
    // reads a Snapshot.
    template <typename TProvider, typename TName, typename TAnswer>
    class Registry
    {
    public:
        using KeyT = TypeKey<TName>;
        using LookupT = Lookup<TName>;
        using RegisterResult = ::XamlMetadataProviderRegistry::RegisterResult<TName>;
        using UnregisterResult = ::XamlMetadataProviderRegistry::UnregisterResult;

        struct Entry
        {
            TProvider Provider{};
            const void* Identity = nullptr;   // COM identity (IUnknown*), compared, never dereferenced.
            ProviderId Id = c_invalidProviderId;
        };

        // One question a registration candidate must be asked before it can join: "what is this name?"
        struct Probe
        {
            ClaimId Claim = c_invalidClaimId;
            LookupT Lookup;
            KeyT Expected;
        };

        // Phase 1 of Register, under the caller's lock.
        struct RegisterTicket
        {
            bool Decided = false;          // Result is final; skip the probes and the commit.
            RegisterResult Result;
            RegistryGeneration ObservedGeneration = 0;
            std::uint64_t ObservedClaimsVersion = 0;
            std::vector<Probe> Probes;
        };

        // What the candidate answered for one Probe. Answered=false when it doesn't know the name.
        struct ProbeAnswer
        {
            bool Answered = false;
            bool Failed = false;
            KeyT Actual;
        };

        enum class ResolveStatus : std::uint32_t { NotFound = 0, Resolved = 1, Ambiguous = 2 };

        struct ResolveOutcome
        {
            ResolveStatus Status = ResolveStatus::NotFound;
            TAnswer Answer{};
            KeyT Key;
            // Every provider that answered with Key, in registration order. Answer came from the first.
            std::vector<ProviderId> Owners;
        };

        RegistryGeneration GetGeneration() const { return m_generation; }
        std::size_t Count() const { return m_entries.size(); }
        std::size_t ClaimCount() const { return m_claims.size(); }

        bool IsRegistered(const void* identity) const { return Find(identity) != m_entries.end(); }

        std::vector<Entry> Snapshot() const { return m_entries; }

        RegisterTicket BeginRegister(const TProvider& provider, const void* identity) const
        {
            RegisterTicket ticket;
            ticket.ObservedGeneration = m_generation;
            ticket.ObservedClaimsVersion = m_claimsVersion;

            if (!provider || identity == nullptr)
            {
                ticket.Decided = true;
                ticket.Result = Refuse(RefusalReason::NullProvider);
                return ticket;
            }

            const auto existing = Find(identity);
            if (existing != m_entries.end())
            {
                ticket.Decided = true;
                ticket.Result.Status = RegistryStatus::AlreadyRegistered;
                ticket.Result.Id = existing->Id;
                ticket.Result.Generation = m_generation;
                return ticket;
            }

            if (m_entries.size() >= c_maxProviders)
            {
                ticket.Decided = true;
                ticket.Result = Refuse(RefusalReason::RegistryFull);
                return ticket;
            }

            for (const auto& claim : m_claims)
            {
                for (const auto& lookup : claim.second.Lookups)
                {
                    ticket.Probes.push_back(Probe{ claim.first, lookup, claim.second.Key });
                }
            }
            return ticket;
        }

        // Phase 3 of Register, under the caller's lock. answers[i] is the candidate's answer to ticket.Probes[i].
        RegisterResult CommitRegister(
            const TProvider& provider,
            const void* identity,
            const RegisterTicket& ticket,
            const std::vector<ProbeAnswer>& answers)
        {
            if (ticket.Decided)
            {
                return ticket.Result;
            }

            if (m_generation != ticket.ObservedGeneration ||
                m_claimsVersion != ticket.ObservedClaimsVersion ||
                answers.size() != ticket.Probes.size())
            {
                return Refuse(RefusalReason::ConcurrentModification);
            }

            std::vector<ClaimId> coOwned;
            for (std::size_t i = 0; i < answers.size(); ++i)
            {
                if (answers[i].Failed)
                {
                    return Refuse(RefusalReason::ProviderFailed);
                }
                if (!answers[i].Answered)
                {
                    continue;
                }
                if (answers[i].Actual != ticket.Probes[i].Expected)
                {
                    RegisterResult conflict;
                    conflict.Status = RegistryStatus::Conflict;
                    conflict.Generation = m_generation;
                    conflict.ConflictingName = ticket.Probes[i].Lookup.Name;
                    return conflict;
                }
                coOwned.push_back(ticket.Probes[i].Claim);
            }

            Entry entry;
            entry.Provider = provider;
            entry.Identity = identity;
            entry.Id = m_nextProviderId++;
            m_entries.push_back(entry);

            // The newcomer also supplies these types, so they survive if their original provider is removed.
            for (const ClaimId claimId : coOwned)
            {
                auto claim = m_claims.find(claimId);
                if (claim != m_claims.end() && !Contains(claim->second.Owners, entry.Id))
                {
                    claim->second.Owners.push_back(entry.Id);
                }
            }

            RegisterResult result;
            result.Status = RegistryStatus::Succeeded;
            result.Id = entry.Id;
            result.Generation = ++m_generation;
            return result;
        }

        UnregisterResult Unregister(const void* identity)
        {
            UnregisterResult result;
            const auto it = Find(identity);
            if (identity == nullptr || it == m_entries.end())
            {
                result.Reason = identity == nullptr ? RefusalReason::NullProvider : RefusalReason::NotRegistered;
                result.Generation = m_generation;
                return result;
            }

            result.Id = it->Id;
            m_entries.erase(it);

            for (auto claim = m_claims.begin(); claim != m_claims.end();)
            {
                auto& owners = claim->second.Owners;
                owners.erase(std::remove(owners.begin(), owners.end(), result.Id), owners.end());
                if (owners.empty())
                {
                    result.OrphanedClaims.push_back(claim->first);
                    m_claimsByKey.erase(claim->second.Key);
                    claim = m_claims.erase(claim);
                    ++m_claimsVersion;
                }
                else
                {
                    ++claim;
                }
            }

            result.Status = RegistryStatus::Succeeded;
            result.Generation = ++m_generation;
            return result;
        }

        // Asks every provider in snapshot (registration order). ask(entry) returns true and fills key/answer when
        // the provider knows the name. Answers that agree on the key are one type, owned by every provider that
        // gave it; answers that disagree are ambiguous and resolve to nothing, so resolution never depends on
        // registration order. Called without the caller's lock (it calls user code).
        template <typename TAsk>
        static ResolveOutcome Resolve(const std::vector<Entry>& snapshot, TAsk&& ask)
        {
            ResolveOutcome outcome;
            for (const auto& entry : snapshot)
            {
                KeyT key;
                TAnswer answer{};
                if (!ask(entry, key, answer))
                {
                    continue;
                }

                if (outcome.Status == ResolveStatus::NotFound)
                {
                    outcome.Status = ResolveStatus::Resolved;
                    outcome.Answer = answer;
                    outcome.Key = key;
                    outcome.Owners.push_back(entry.Id);
                }
                else if (key == outcome.Key)
                {
                    outcome.Owners.push_back(entry.Id);
                }
                else
                {
                    outcome.Status = ResolveStatus::Ambiguous;
                    outcome.Answer = TAnswer{};
                    outcome.Owners.clear();
                    return outcome;
                }
            }
            return outcome;
        }

        // Records that lookup resolved to outcome.Key, under the caller's lock. Returns the claim the host tags the
        // cached type with, or c_invalidClaimId when every owner was unregistered while the lookup ran (the host
        // should then treat the name as unresolved).
        ClaimId RecordClaim(const LookupT& lookup, const ResolveOutcome& outcome)
        {
            if (outcome.Status != ResolveStatus::Resolved)
            {
                return c_invalidClaimId;
            }

            std::vector<ProviderId> owners;
            for (const ProviderId owner : outcome.Owners)
            {
                if (IsRegisteredId(owner))
                {
                    owners.push_back(owner);
                }
            }
            if (owners.empty())
            {
                return c_invalidClaimId;
            }

            ClaimId claimId;
            const auto byKey = m_claimsByKey.find(outcome.Key);
            if (byKey != m_claimsByKey.end())
            {
                claimId = byKey->second;
            }
            else
            {
                claimId = m_nextClaimId++;
                Claim claim;
                claim.Key = outcome.Key;
                m_claims.emplace(claimId, std::move(claim));
                m_claimsByKey.emplace(outcome.Key, claimId);
            }

            Claim& claim = m_claims.at(claimId);
            for (const ProviderId owner : owners)
            {
                if (!Contains(claim.Owners, owner))
                {
                    claim.Owners.push_back(owner);
                }
            }
            if (std::find(claim.Lookups.begin(), claim.Lookups.end(), lookup) == claim.Lookups.end())
            {
                claim.Lookups.push_back(lookup);
            }
            ++m_claimsVersion;
            return claimId;
        }

        // Providers that own claimId, in registration order (for diagnostics and tests).
        std::vector<ProviderId> OwnersOf(ClaimId claimId) const
        {
            const auto it = m_claims.find(claimId);
            return it == m_claims.end() ? std::vector<ProviderId>{} : it->second.Owners;
        }

        // Forgets everything (MetadataAPI::Reset). Bumps the generation so stale tickets are refused.
        void Clear()
        {
            m_entries.clear();
            m_claims.clear();
            m_claimsByKey.clear();
            ++m_claimsVersion;
            ++m_generation;
        }

        // Callers that invalidate caches without changing providers bump the generation too, so a caller can prove
        // its invalidation was observed.
        RegistryGeneration BumpGeneration() { return ++m_generation; }

    private:
        struct Claim
        {
            KeyT Key;
            std::vector<ProviderId> Owners;
            std::vector<LookupT> Lookups;
        };

        static bool Contains(const std::vector<ProviderId>& ids, ProviderId id)
        {
            return std::find(ids.begin(), ids.end(), id) != ids.end();
        }

        RegisterResult Refuse(RefusalReason reason) const
        {
            RegisterResult result;
            result.Status = RegistryStatus::Refused;
            result.Reason = reason;
            result.Generation = m_generation;
            return result;
        }

        typename std::vector<Entry>::const_iterator Find(const void* identity) const
        {
            return std::find_if(m_entries.begin(), m_entries.end(), [identity](const Entry& e) { return e.Identity == identity; });
        }

        typename std::vector<Entry>::iterator Find(const void* identity)
        {
            return std::find_if(m_entries.begin(), m_entries.end(), [identity](const Entry& e) { return e.Identity == identity; });
        }

        bool IsRegisteredId(ProviderId id) const
        {
            return std::any_of(m_entries.begin(), m_entries.end(), [id](const Entry& e) { return e.Id == id; });
        }

        std::vector<Entry> m_entries;
        std::map<ClaimId, Claim> m_claims;
        std::map<KeyT, ClaimId> m_claimsByKey;
        RegistryGeneration m_generation = 0;
        std::uint64_t m_claimsVersion = 0;
        ProviderId m_nextProviderId = 1;
        ClaimId m_nextClaimId = 1;
    };
}
