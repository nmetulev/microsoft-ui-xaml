// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include <precomp.h>

#include "BindScopeAttachUnitTests.h"
#include "BindScopeAttachScenarios.h"

using namespace BindScope;
using namespace BindScopeTests;

namespace {

void LogFailures(const ScenarioList& results)
{
    for (const ScenarioOutcome& outcome : results)
    {
        if (!outcome.Pass)
        {
            WEX::Logging::Log::Comment(
                WEX::Common::String().Format(L"  %s FAILED: %s", outcome.Id, outcome.Detail.c_str()));
        }
    }
}

// Runs the suite once and asserts one named scenario passed, so a single test method reports a
// readable failure instead of a bulk count.
void VerifySingleScenario(const wchar_t* id)
{
    ScenarioList results;
    RunScenarios(Mutant::None, results);
    LogFailures(results);

    // A truncated run must not look like a clean run.
    VERIFY_ARE_EQUAL(c_expectedScenarioCount, results.size());
    VERIFY_IS_TRUE(ScenarioPassed(results, id));
}

} // namespace

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace BindScope {

void BindScopeAttachUnitTests::AttachConnectsEveryRowAndInitializes() { VerifySingleScenario(L"S01"); }
void BindScopeAttachUnitTests::RepeatAttachIsRefusedAndNeverReconnects() { VerifySingleScenario(L"S02"); }
void BindScopeAttachUnitTests::SameTypedTargetSwapIsRefusedOnIdentity() { VerifySingleScenario(L"S07"); }
void BindScopeAttachUnitTests::IncompleteTargetMapIsRefused() { VerifySingleScenario(L"S08"); }
void BindScopeAttachUnitTests::NoEffectScopeIsRefusedNotAttached() { VerifySingleScenario(L"S09"); }
void BindScopeAttachUnitTests::ReplaceStopsTheOutgoingScope() { VerifySingleScenario(L"S10"); }
void BindScopeAttachUnitTests::CachedUnrealizedRootAttachesThroughNamescope() { VerifySingleScenario(L"S13"); }
void BindScopeAttachUnitTests::UnnamedRowFromAnotherRootIsRefused() { VerifySingleScenario(L"S14"); }

void BindScopeAttachUnitTests::BaseTreeFaultDominatesAndFailsClosed()
{
    ScenarioList results;
    RunScenarios(Mutant::None, results);
    LogFailures(results);

    VERIFY_ARE_EQUAL(c_expectedScenarioCount, results.size());
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S04"));   // stale base refuses before mutation
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S05"));   // base dominates a simultaneous scope fault
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S06"));   // unknown base fails closed
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S03"));   // scope faults keep their own remedy
}

void BindScopeAttachUnitTests::DetachClearsOwnershipAndIsNotIdempotentAsSuccess()
{
    ScenarioList results;
    RunScenarios(Mutant::None, results);
    LogFailures(results);

    VERIFY_ARE_EQUAL(c_expectedScenarioCount, results.size());
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S11"));
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S12"));
}

void BindScopeAttachUnitTests::OwnershipIsPublishedOnlyAfterConnect()
{
    ScenarioList results;
    RunScenarios(Mutant::None, results);
    LogFailures(results);

    VERIFY_ARE_EQUAL(c_expectedScenarioCount, results.size());
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S17"));   // partial connect reports Desynchronized
    VERIFY_IS_TRUE(ScenarioPassed(results, L"S19"));   // ownership invisible during the connect loop
}

void BindScopeAttachUnitTests::CleanSuitePasses()
{
    ScenarioList results;
    RunScenarios(Mutant::None, results);
    LogFailures(results);

    VERIFY_ARE_EQUAL(c_expectedScenarioCount, results.size());
    VERIFY_ARE_EQUAL(static_cast<size_t>(0), CountFailures(results));
}

void BindScopeAttachUnitTests::EverySeededMutantIsKilled()
{
#if !defined(__XAML_UNITTESTS__)
    WEX::Logging::Log::Result(WEX::Logging::TestResults::Skipped, L"Seeded mutants only exist in unit test builds.");
#else
    for (const MutantExpectation& expectation : c_mutants)
    {
        ScenarioList results;
        RunScenarios(expectation.Which, results);

        // A seeded defect must not be able to hide by aborting the run.
        VERIFY_ARE_EQUAL_MSG(c_expectedScenarioCount, results.size(),
            WEX::Common::String().Format(L"mutant %s truncated the suite", expectation.Name));

        const size_t failed = CountFailures(results);

        WEX::Logging::Log::Comment(
            WEX::Common::String().Format(L"mutant %s: %u scenario(s) red", expectation.Name, static_cast<unsigned>(failed)));
        LogFailures(results);

        VERIFY_IS_TRUE_MSG(failed > static_cast<size_t>(0),
            WEX::Common::String().Format(L"mutant %s SURVIVED: no scenario enforces this guard", expectation.Name));

        VERIFY_IS_FALSE_MSG(ScenarioPassed(results, expectation.ExpectedKiller),
            WEX::Common::String().Format(L"mutant %s was expected to be caught by %s", expectation.Name, expectation.ExpectedKiller));
    }
#endif
}

} } } } }
