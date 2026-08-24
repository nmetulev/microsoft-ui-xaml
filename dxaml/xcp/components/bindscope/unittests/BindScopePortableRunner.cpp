// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Portable runner for the component scenario suite.
//
// This compiles the REAL production source
//   dxaml\xcp\components\bindscope\XamlBindScopeAttachCore.cpp
// and the REAL shared scenarios
//   dxaml\xcp\components\bindscope\unittests\BindScopeAttachScenarios.h
// with nothing but a C++ toolchain. No TAEF, no XAML headers, no init.cmd, no product build.
//
// It exists because the component under test has no XAML dependency by construction, so the
// production state machine can be executed and mutated before the repository build is bootstrapped.
// The TAEF unit test wraps the same scenarios and is the deliverable for the repo; this runner is
// the evidence that they pass today.
//
// Usage:
//   BindScopePortableRunner            run the clean suite
//   BindScopePortableRunner --mutants  run the clean suite, then every seeded mutant
//
// Exit codes: 0 all good, 1 clean suite failed, 3 a mutant survived.

#include "BindScopeAttachScenarios.h"

#include <cstdio>
#include <cstring>

using namespace BindScope;
using namespace BindScopeTests;

namespace {

void PrintFailures(const ScenarioList& results)
{
    for (const ScenarioOutcome& outcome : results)
    {
        if (!outcome.Pass)
        {
            std::wprintf(L"    %s FAILED  %s\n", outcome.Id, outcome.Detail.c_str());
        }
    }
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    const bool runMutants = (argc > 1) && (std::wcscmp(argv[1], L"--mutants") == 0);

    std::wprintf(L"=== BindScope component scenarios (production source, no XAML build) ===\n");

    ScenarioList clean;
    RunScenarios(Mutant::None, clean);

    std::wprintf(L"clean suite: %u scenario(s), %u failed\n",
        static_cast<unsigned>(clean.size()), static_cast<unsigned>(CountFailures(clean)));
    PrintFailures(clean);

    if (clean.size() != c_expectedScenarioCount)
    {
        std::wprintf(L"CLEAN RUN: FAIL (expected %u scenarios)\n", static_cast<unsigned>(c_expectedScenarioCount));
        return 1;
    }
    if (CountFailures(clean) != 0)
    {
        std::wprintf(L"CLEAN RUN: FAIL\n");
        return 1;
    }
    std::wprintf(L"CLEAN RUN: PASS\n");

    if (!runMutants)
    {
        return 0;
    }

    std::wprintf(L"\n--- seeded mutants ---\n");
    int survivors = 0;

    for (const MutantExpectation& expectation : c_mutants)
    {
        ScenarioList results;
        RunScenarios(expectation.Which, results);

        const size_t failed = CountFailures(results);
        const bool truncated = results.size() != c_expectedScenarioCount;
        const bool expectedKillerWentRed = !ScenarioPassed(results, expectation.ExpectedKiller);

        std::wprintf(L"%-32s red=%u%s  expectedKiller=%s -> %s\n",
            expectation.Name,
            static_cast<unsigned>(failed),
            truncated ? L" (TRUNCATED)" : L"",
            expectation.ExpectedKiller,
            expectedKillerWentRed ? L"red" : L"still green");

        for (const ScenarioOutcome& outcome : results)
        {
            if (!outcome.Pass) { std::wprintf(L"      %s\n", outcome.Id); }
        }

        if (failed == 0 || truncated || !expectedKillerWentRed)
        {
            std::wprintf(L"  ^ MUTANT SURVIVED OR RAN VACUOUSLY\n");
            ++survivors;
        }
    }

    std::wprintf(L"\nmutants=%u survivors=%d\n",
        static_cast<unsigned>(sizeof(c_mutants) / sizeof(c_mutants[0])), survivors);
    std::wprintf(survivors == 0 ? L"MUTATION GATE: GREEN\n" : L"MUTATION GATE: RED\n");
    return survivors == 0 ? 0 : 3;
}
