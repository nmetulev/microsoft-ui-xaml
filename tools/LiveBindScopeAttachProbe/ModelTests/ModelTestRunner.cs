// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Headless test + mutation runner for the live {x:Bind} scope attach state machine.
//
//   ScopeAttachModelTests                 run the suite, no seeded defect
//   ScopeAttachModelTests --mutant <name> run the suite with exactly one seeded defect
//   ScopeAttachModelTests --list-mutants  print the mutant names
//
// Exit codes:
//   0  clean run passed, or a mutant run was correctly KILLED by at least one test
//   1  clean run had a failure
//   3  a mutant SURVIVED, meaning the suite does not actually enforce that guard
//
// A surviving mutant is a red result: it says the contract is not being tested where it claims to be.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveBindScopeProbe.Model
{
    internal static class Program
    {
        private const string BaseLive = "basetree@gen7(test-token)";
        private const string BaseStale = "basetree@gen6(test-token)";
        private const string ScopeV1 = "scope@v1(test-token)";
        private const string ScopeV2 = "scope@v2(test-token)";

        private static readonly List<(string Id, string Name, bool Pass, string Detail)> s_results = new();

        private static int Main(string[] args)
        {
            if (args.Contains("--list-mutants"))
            {
                foreach (var m in Enum.GetValues<Mutant>().Where(m => m != Mutant.None)) { Console.WriteLine(m); }
                return 0;
            }

            Mutant mutant = Mutant.None;
            int i = Array.IndexOf(args, "--mutant");
            if (i >= 0 && i + 1 < args.Length && !Enum.TryParse(args[i + 1], out mutant))
            {
                Console.Error.WriteLine($"unknown mutant '{args[i + 1]}'");
                return 1;
            }

            RunSuiteGuarded(mutant);
            CheckContractArtifactsAgree();

            int failed = s_results.Count(r => !r.Pass);
            var sb = new StringBuilder();
            sb.AppendLine($"=== ScopeAttachModelTests  mutant={mutant} ===");
            foreach (var r in s_results)
            {
                sb.AppendLine($"{r.Id,-8} {(r.Pass ? "PASS" : "FAIL"),-5} {r.Name}");
                if (!r.Pass) { sb.AppendLine($"         {r.Detail}"); }
            }
            sb.AppendLine($"total={s_results.Count} passed={s_results.Count - failed} failed={failed}");

            if (mutant == Mutant.None)
            {
                sb.AppendLine(failed == 0 ? "CLEAN RUN: PASS" : "CLEAN RUN: FAIL");
                Console.WriteLine(sb.ToString());
                return failed == 0 ? 0 : 1;
            }

            var killers = s_results.Where(r => !r.Pass).Select(r => r.Id).ToArray();
            if (killers.Length > 0)
            {
                sb.AppendLine($"MUTANT KILLED by: {string.Join(", ", killers)}");
                Console.WriteLine(sb.ToString());
                return 0;
            }

            sb.AppendLine("MUTANT SURVIVED: no test enforces this guard");
            Console.WriteLine(sb.ToString());
            return 3;
        }

        // Number of S-cases the suite is expected to record. A seeded defect can make a case throw,
        // which aborts the run; a truncated run is a failure, not a silently surviving mutant.
        private const int ExpectedCaseCount = 19;

        private static void RunSuiteGuarded(Mutant mutant)
        {
            try
            {
                RunSuite(mutant);
            }
            catch (Exception ex)
            {
                Check("S**", "suite ran to completion", false, $"aborted with {ex.GetType().Name}: {ex.Message}");
            }

            int recorded = s_results.Count(r => r.Id.StartsWith("S", StringComparison.Ordinal) && r.Id != "S**");
            if (recorded < ExpectedCaseCount)
            {
                Check("S##", "all cases executed", false, $"only {recorded} of {ExpectedCaseCount} cases ran");
            }
        }

        private static void RunSuite(Mutant mutant)
        {
            var host = new FakeHost();

            FakeRoot NewRoot(string revision = BaseLive)
            {
                var root = new FakeRoot { BaseTreeRevision = revision };
                root.Add("TextBlock", "First");
                root.Add("TextBlock", "Second");
                return root;
            }

            List<TargetRow> Rows(FakeRoot root, bool swapAdjacent = false, bool dropSecond = false)
            {
                var rows = new List<TargetRow>
                {
                    new() { ConnectionId = 1, StableName = null, ExpectedTypeName = "FakeRoot", Target = root },
                    new() { ConnectionId = 2, StableName = "First", ExpectedTypeName = "TextBlock",
                            Target = swapAdjacent ? root.Names["Second"] : root.Names["First"] },
                };
                if (!dropSecond)
                {
                    rows.Add(new TargetRow { ConnectionId = 3, StableName = "Second", ExpectedTypeName = "TextBlock",
                                             Target = swapAdjacent ? root.Names["First"] : root.Names["Second"] });
                }
                return rows;
            }

            FakeConnector V1(string expectedBase = BaseLive) => new(ScopeV1, expectedBase, () => new FakeScope(false));
            FakeConnector V2(string expectedBase = BaseLive) => new(ScopeV2, expectedBase, () => new FakeScope(true));

            // S01 clean attach
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var r = model.TryAttach(root, V1(), Rows(root));
                Check("S01", "attach connects every row and initializes",
                    r.Status == AttachStatus.Attached && r.FailureDetail == FailureDetail.None
                        && r.TargetsConnected == 3 && r.OwnedScopeInstanceId != 0
                        && r.AppliedScopeRevision == ScopeV1 && r.ObservedBaseTreeRevision == BaseLive
                        && root.Names["First"].Written == "A:v0" && root.Names["Second"].Written == "B:v0",
                    $"{r} first='{root.Names["First"].Written}' second='{root.Names["Second"].Written}'");
            }

            // S02 repeat attach is idempotent, never re-connects
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var connector = V1();
                var first = model.TryAttach(root, connector, Rows(root));
                object scopeAfterFirst = model.GetAttachedScope(root);
                model.ResetInstrumentation();
                var second = model.TryAttach(root, V1(), Rows(root));
                Check("S02", "repeat attach: AlreadyAttached, no connects, same ownership and scope",
                    second.Status == AttachStatus.AlreadyAttached
                        && second.FailureDetail == FailureDetail.ScopeRevisionAlreadyApplied
                        && second.TargetsConnected == 0
                        && second.OwnedScopeInstanceId == first.OwnedScopeInstanceId
                        && ReferenceEquals(model.GetAttachedScope(root), scopeAfterFirst)
                        && model.ConnectCallCount == 0,
                    $"{second} connects={model.ConnectCallCount} sameScope={ReferenceEquals(model.GetAttachedScope(root), scopeAfterFirst)}");
            }

            // S03 plain attach with a different revision refuses on the scope dimension
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                model.TryAttach(root, V1(), Rows(root));
                var r = model.TryAttach(root, V2(), Rows(root));
                Check("S03", "different revision refused, both revisions reported",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.ScopeRevisionConflict
                        && r.RequestedScopeRevision == ScopeV2 && r.AppliedScopeRevision == ScopeV1,
                    r.ToString());
            }

            // S04 base tree mismatch dominates, zero connects, no ownership
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var r = model.TryAttach(root, V1(BaseStale), Rows(root));
                Check("S04", "base tree mismatch refused before mutation",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.BaseTreeRevisionMismatch
                        && r.TargetsConnected == 0 && model.ConnectCallCount == 0
                        && model.GetAttachedScope(root) == null,
                    $"{r} connects={model.ConnectCallCount} owner={model.GetAttachedScope(root)}");
            }

            // S05 base fault dominates a simultaneous scope fault
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                model.TryAttach(root, V1(), Rows(root));
                var r = model.TryAttach(root, V2(BaseStale), Rows(root));
                Check("S05", "base fault dominates scope fault",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.BaseTreeRevisionMismatch,
                    r.ToString());
            }

            // S06 fail closed when the root carries no revision
            {
                var root = NewRoot(string.Empty);
                var model = new ScopeAttachModel(host, mutant);
                var r = model.TryAttach(root, V1(), Rows(root));
                Check("S06", "unknown base tree fails closed",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.BaseTreeRevisionUnavailable
                        && model.ConnectCallCount == 0,
                    $"{r} connects={model.ConnectCallCount}");
            }

            // S07 adjacent same-typed targets swapped: identity refuses, cast would not
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                bool castsAgree = root.Names["First"].TypeName == root.Names["Second"].TypeName;
                var r = model.TryAttach(root, V1(), Rows(root, swapAdjacent: true));
                Check("S07", "same-typed swap refused on identity while casts still agree",
                    castsAgree && r.Status == AttachStatus.Refused
                        && r.FailureDetail == FailureDetail.TargetIdentityMismatch
                        && r.FailedConnectionId == 2 && model.ConnectCallCount == 0,
                    $"{r} castsAgree={castsAgree} connects={model.ConnectCallCount}");
            }

            // S08 incomplete map refused against connector-declared required ids
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var r = model.TryAttach(root, V1(), Rows(root, dropSecond: true));
                Check("S08", "incomplete target map refused before mutation",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.ManifestIncomplete
                        && r.FailedConnectionId == 3 && model.ConnectCallCount == 0,
                    $"{r} connects={model.ConnectCallCount}");
            }

            // S09 a scope that can never be initialized or stopped is refused, not attached
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var inert = new FakeConnector("scope@inert", BaseLive, () => new InertScope());
                var r = model.TryAttach(root, inert, Rows(root));
                Check("S09", "no-effect scope refused instead of reported attached",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.ScopeLifecycleUnsupported
                        && model.GetAttachedScope(root) == null
                        && root.Names["First"].Written == null,
                    $"{r} owner={model.GetAttachedScope(root)} written='{root.Names["First"].Written}'");
            }

            // S10 replace detaches the outgoing scope; exactly one writer remains
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var c1 = V1();
                var first = model.TryAttach(root, c1, Rows(root));
                var outgoing = (FakeScope)c1.LastProduced;
                var replace = model.Replace(root, V2(), Rows(root));

                int before = root.Names["First"].WriteCount;
                root.SetValue("v1");
                int writes = root.Names["First"].WriteCount - before;

                Check("S10", "replace stops the old scope and leaves exactly one writer",
                    replace.Status == AttachStatus.Replaced && replace.TargetsDetached == 3
                        && replace.OwnedScopeInstanceId != first.OwnedScopeInstanceId
                        && outgoing.DetachCount == 1 && !outgoing.IsSubscribed
                        && writes == 1 && root.Names["First"].Written == "B:v1",
                    $"{replace} outgoingDetach={outgoing.DetachCount} outgoingSubscribed={outgoing.IsSubscribed} writesPerChange={writes} written='{root.Names["First"].Written}'");
            }

            // S11 detach clears ownership and silences the tree
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                model.TryAttach(root, V1(), Rows(root));
                var d = model.Detach(root);
                string frozen = root.Names["First"].Written;
                root.SetValue("v2");
                Check("S11", "detach clears ownership and leaves no writer",
                    d.Status == AttachStatus.Detached && d.TargetsDetached == 3
                        && model.GetAttachedScope(root) == null
                        && root.Names["First"].Written == frozen,
                    $"{d} written='{root.Names["First"].Written}' frozen='{frozen}'");
            }

            // S12 detach is not idempotent-as-success
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                model.TryAttach(root, V1(), Rows(root));
                model.Detach(root);
                var again = model.Detach(root);
                Check("S12", "second detach is a refusal, not a success",
                    again.Status == AttachStatus.Refused && again.FailureDetail == FailureDetail.NothingAttached,
                    again.ToString());
            }

            // S13 cached root: never realized, no namescope owner walk possible, named rows still work
            {
                var cached = new FakeRoot { BaseTreeRevision = BaseLive };
                var a = cached.Add("TextBlock", "First");
                var b = cached.Add("TextBlock", "Second");
                a.Owner = null;   // deliberately unrealized: no owner link to walk
                b.Owner = null;
                var model = new ScopeAttachModel(host, mutant);
                var r = model.TryAttach(cached, V1(), Rows(cached));
                Check("S13", "cached, unrealized root attaches through the namescope",
                    r.Status == AttachStatus.Attached && r.TargetsConnected == 3 && a.Written == "A:v0",
                    $"{r} written='{a.Written}'");
            }

            // S14 an unnamed row from another root is refused
            {
                var root = NewRoot();
                var other = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var rows = Rows(root);
                rows[1] = new TargetRow { ConnectionId = 2, StableName = null, ExpectedTypeName = "TextBlock", Target = other.Names["First"] };
                var r = model.TryAttach(root, V1(), rows);
                Check("S14", "unnamed row from another root refused",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.TargetOutsideNamescope
                        && model.ConnectCallCount == 0,
                    $"{r} connects={model.ConnectCallCount}");
            }

            // S15 a connector with no manifest is refused
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var r = model.TryAttach(root, new ManifestlessConnector(), Rows(root));
                Check("S15", "connector without a manifest refused",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.ScopeManifestUnavailable,
                    r.ToString());
            }

            // S16 missing root row refused
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var rows = Rows(root).Where(x => x.ConnectionId != 1).ToList();
                var connector = new FakeConnector(ScopeV1, BaseLive, () => new FakeScope(false), new[] { 2, 3 });
                var r = model.TryAttach(root, connector, rows);
                Check("S16", "manifest with no root row refused",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.ManifestMissingRootRow
                        && model.ConnectCallCount == 0,
                    $"{r} connects={model.ConnectCallCount}");
            }

            // S17 a Connect failure part way through reports desync, not success
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var connector = new FakeConnector(ScopeV1, BaseLive, () => new ThrowingScope(failOnId: 3));
                var r = model.TryAttach(root, connector, Rows(root));
                Check("S17", "partial connect reports Desynchronized and names the id",
                    r.Status == AttachStatus.Desynchronized && r.FailureDetail == FailureDetail.DesyncScopeState
                        && r.FailedConnectionId == 3 && r.TargetsConnected == 2,
                    r.ToString());
            }

            // S18 duplicate connection id refused
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var rows = Rows(root);
                rows.Add(new TargetRow { ConnectionId = 2, StableName = "First", ExpectedTypeName = "TextBlock", Target = root.Names["First"] });
                var r = model.TryAttach(root, V1(), rows);
                Check("S18", "duplicate connection id refused",
                    r.Status == AttachStatus.Refused && r.FailureDetail == FailureDetail.ManifestDuplicateConnectionId
                        && model.ConnectCallCount == 0,
                    $"{r} connects={model.ConnectCallCount}");
            }

            // S19 ownership is published only on success, never mid-connect
            {
                var root = NewRoot();
                var model = new ScopeAttachModel(host, mutant);
                var observer = new OwnershipObservingScope(() => model.GetAttachedScope(root));
                var connector = new FakeConnector(ScopeV1, BaseLive, () => observer);
                var r = model.TryAttach(root, connector, Rows(root));
                Check("S19", "ownership is not observable until the attach succeeds",
                    !observer.SawOwnershipDuringConnect && model.GetAttachedScope(root) != null
                        && r.Status == AttachStatus.Attached,
                    $"{r} sawOwnershipDuringConnect={observer.SawOwnershipDuringConnect}");
            }
        }

        // Static gate: the headless model, the probe's local mirror and the committed IDL must all
        // agree on the failure taxonomy. A drift here means the evidence no longer describes the
        // proposed contract.
        private static void CheckContractArtifactsAgree()
        {
            string repoRoot = FindRepoRoot();
            if (repoRoot == null)
            {
                Check("X01", "contract artifacts agree", false, "could not locate repository root");
                return;
            }

            string idlPath = Path.Combine(repoRoot, "dxaml", "xcp", "dxaml", "idl", "winrt", "core", "microsoft.ui.xaml.coretypes2.idl");
            string mirrorPath = Path.Combine(repoRoot, "tools", "LiveBindScopeAttachProbe", "Probe", "Contracts.cs");

            var idl = ParseIdlEnum(idlPath, "XamlBindScopeFailureDetail");
            var mirror = ParseCsEnum(mirrorPath, "XamlBindScopeFailureDetail");
            var model = Enum.GetValues<FailureDetail>().ToDictionary(v => v.ToString(), v => (int)v);

            string diff = Compare("idl-vs-model", idl, model) ?? Compare("mirror-vs-model", mirror, model);
            Check("X01", "IDL, probe mirror and headless model agree on XamlBindScopeFailureDetail",
                diff == null, diff ?? $"members={model.Count}");

            var idlStatus = ParseIdlEnum(idlPath, "XamlBindScopeAttachStatus");
            var modelStatus = Enum.GetValues<AttachStatus>().ToDictionary(v => v.ToString(), v => (int)v);
            string statusDiff = Compare("idl-vs-model", idlStatus, modelStatus);
            Check("X02", "IDL and headless model agree on XamlBindScopeAttachStatus",
                statusDiff == null, statusDiff ?? $"members={modelStatus.Count}");
        }

        private static string Compare(string label, Dictionary<string, int> a, Dictionary<string, int> b)
        {
            if (a.Count == 0) { return $"{label}: parsed 0 members"; }
            foreach (var kv in b)
            {
                if (!a.TryGetValue(kv.Key, out int v)) { return $"{label}: '{kv.Key}' missing"; }
                if (v != kv.Value) { return $"{label}: '{kv.Key}' is {v} vs {kv.Value}"; }
            }
            if (a.Count != b.Count) { return $"{label}: {a.Count} vs {b.Count} members"; }
            return null;
        }

        private static Dictionary<string, int> ParseIdlEnum(string path, string enumName)
            => ParseEnum(path, "enum " + enumName);

        private static Dictionary<string, int> ParseCsEnum(string path, string enumName)
            => ParseEnum(path, "enum " + enumName);

        private static Dictionary<string, int> ParseEnum(string path, string header)
        {
            var members = new Dictionary<string, int>();
            if (!File.Exists(path)) { return members; }

            string[] lines = File.ReadAllLines(path);
            int start = Array.FindIndex(lines, l => l.Contains(header, StringComparison.Ordinal));
            if (start < 0) { return members; }

            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//")) { continue; }
                if (line.StartsWith("}")) { break; }

                int eq = line.IndexOf('=');
                if (eq <= 0) { continue; }

                string name = line[..eq].Trim();
                string valueText = line[(eq + 1)..].TrimEnd(',', ';', ' ').Trim();
                if (name.Length == 0 || !int.TryParse(valueText, out int value)) { continue; }
                if (!name.All(c => char.IsLetterOrDigit(c) || c == '_')) { continue; }
                members[name] = value;
            }
            return members;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string git = Path.Combine(dir.FullName, ".git");
                // A worktree checkout has .git as a file pointing at the real gitdir, not a directory.
                if (Directory.Exists(git) || File.Exists(git)) { return dir.FullName; }
                dir = dir.Parent;
            }
            return null;
        }

        private static void Check(string id, string name, bool pass, string detail)
            => s_results.Add((id, name, pass, detail));

        // A seeded defect can make a test throw rather than assert. That still counts as the test
        // catching it, so an exception is recorded as a failure instead of killing the process.
        private static void Case(string id, string name, Func<(bool Pass, string Detail)> body)
        {
            try
            {
                var (pass, detail) = body();
                Check(id, name, pass, detail);
            }
            catch (Exception ex)
            {
                Check(id, name, false, $"threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal sealed class ThrowingScope : IScope, IScopeLifecycle
    {
        private readonly int _failOnId;
        public ThrowingScope(int failOnId) { _failOnId = failOnId; }

        public void Connect(int connectionId, object target)
        {
            if (connectionId == _failOnId) { throw new InvalidOperationException("simulated connect failure"); }
        }

        public void InitializeScope() { }
        public void DetachScope() { }
    }

    // Reports whether runtime ownership was already visible while the scope was still being
    // populated. It must not be: a half-connected scope is not a valid owner.
    internal sealed class OwnershipObservingScope : IScope, IScopeLifecycle
    {
        private readonly Func<object> _readOwnership;
        public OwnershipObservingScope(Func<object> readOwnership) { _readOwnership = readOwnership; }

        public bool SawOwnershipDuringConnect { get; private set; }

        public void Connect(int connectionId, object target)
        {
            if (_readOwnership() != null) { SawOwnershipDuringConnect = true; }
        }

        public void InitializeScope() { }
        public void DetachScope() { }
    }
}
