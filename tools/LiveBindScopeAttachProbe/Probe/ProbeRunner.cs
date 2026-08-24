// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LiveBindScopeProbe.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace LiveBindScopeProbe
{
    internal static class ProbeRunner
    {
        private const int RootId = 1;
        private const int TitleId = 2;
        private const int AltId = 3;
        private const int ListId = 4;

        // Test-generation tokens, not content hashes. Both revision dimensions are kept separate so
        // the semantics can be exercised; a real producer substitutes
        // hash(xbf | source checksum | compiler version | connection-map hash) for the base tree and
        // hash(generated scope | binding manifest) for the scope.
        private const string BaseTreeRevisionLive = "basetree@gen7(test-token)";
        private const string BaseTreeRevisionStale = "basetree@gen6(test-token)";

        private static readonly List<Row> s_rows = new List<Row>();
        private static StackPanel s_host;

        // Kept alive for the whole run so it is provably the same instance, never in the tree.
        private static SubjectPage s_cachedSubject;

        private sealed class Row
        {
            public string Id;
            public string Name;
            public bool Pass;
            public string Expected;
            public string Observed;
        }

        public static int Run(StackPanel host)
        {
            s_host = host;
            var started = Stopwatch.StartNew();

            // The scope lives in a binary the app has no compile-time dependency on.
            string scopeAssemblyPath = Path.Combine(AppContext.BaseDirectory, "ProbeScope.dll");
            Assembly scopeAssembly = Assembly.LoadFrom(scopeAssemblyPath);
            Type factory = scopeAssembly.GetType("LiveBindScopeProbe.GeneratedScope.ScopeFactory", throwOnError: true);

            Func<string, IComponentConnector> createV1 = rev => (IComponentConnector)factory.GetMethod("CreateV1").Invoke(null, new object[] { rev });
            Func<string, IComponentConnector> createV2 = rev => (IComponentConnector)factory.GetMethod("CreateV2").Invoke(null, new object[] { rev });
            Func<string, IComponentConnector> createInert = rev => (IComponentConnector)factory.GetMethod("CreateInert").Invoke(null, new object[] { rev });
            Func<object, bool, IComponentConnector> createBare = (page, swap) => (IComponentConnector)factory.GetMethod("CreateBareScope").Invoke(null, new object[] { page, swap });
            Action<IComponentConnector> initBare = scope => factory.GetMethod("InitializeBareScope").Invoke(null, new object[] { scope });

            // --- Oracle: cold built {x:Bind}, produced by the stock compiler and stock parser. ---
            var oracle = new OraclePage();
            host.Children.Add(oracle);

            // --- Subject: current, live, in the tree, built from an XBF with no bindings at all. ---
            var subject = new SubjectPage();
            host.Children.Add(subject);
            object subjectIdentityBefore = subject;

            // --- Cached subject: constructed and kept in a field, never parented. ---
            s_cachedSubject = new SubjectPage();

            Pump();

            LiveBindScopeAttach.SetBaseTreeRevision(subject, BaseTreeRevisionLive);
            LiveBindScopeAttach.SetBaseTreeRevision(s_cachedSubject, BaseTreeRevisionLive);

            int titleWrites = 0;
            subject.TitleText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (d, dp) => titleWrites++);

            // ==========================================================================
            // NEGATIVE CONTROLS FIRST, so that every one of them runs against a root that
            // owns no scope and can prove nothing was mutated.
            // ==========================================================================

            // N04  stale base tree revision must be refused before any mutation, and must dominate.
            LiveBindScopeAttach.ResetInstrumentation();
            var staleResult = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV1(BaseTreeRevisionStale), Ids(), Names(), Types(), Targets(subject));
            Check("N04", "stale base tree refused before mutation",
                staleResult.Status == XamlBindScopeAttachStatus.Refused
                    && staleResult.FailureDetail == XamlBindScopeFailureDetail.BaseTreeRevisionMismatch
                    && staleResult.TargetsConnected == 0
                    && LiveBindScopeAttach.ConnectCallCount == 0
                    && LiveBindScopeAttach.GetAttachedBindingScope(subject) == null,
                "Refused/BaseTreeRevisionMismatch, 0 connects, no ownership",
                $"{staleResult}; connects={LiveBindScopeAttach.ConnectCallCount}; owner={Describe(LiveBindScopeAttach.GetAttachedBindingScope(subject))}");

            // N02  two adjacent same-typed TextBlocks, swapped in the map. A cast succeeds for both,
            //      so only stable identity can catch this, and it must catch it before any Connect.
            LiveBindScopeAttach.ResetInstrumentation();
            var swappedTargets = new object[] { subject, subject.AltText, subject.TitleText, subject.TodoList };
            var swapResult = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV1(BaseTreeRevisionLive), Ids(), Names(), Types(), swappedTargets);
            bool castWouldHaveSucceeded = swappedTargets[1] is TextBlock && swappedTargets[2] is TextBlock;
            Check("N02", "same-typed target swap refused on identity, not cast",
                swapResult.Status == XamlBindScopeAttachStatus.Refused
                    && swapResult.FailureDetail == XamlBindScopeFailureDetail.TargetIdentityMismatch
                    && LiveBindScopeAttach.ConnectCallCount == 0
                    && castWouldHaveSucceeded,
                "Refused/TargetIdentityMismatch, 0 connects, while both casts succeed",
                $"{swapResult}; connects={LiveBindScopeAttach.ConnectCallCount}; castsSucceed={castWouldHaveSucceeded}");

            // N01  an omitted target row must be refused, because a scope with an unconnected target
            //      is silently inert.
            LiveBindScopeAttach.ResetInstrumentation();
            var partialResult = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV1(BaseTreeRevisionLive),
                new[] { RootId, TitleId, AltId },
                new[] { string.Empty, "TitleText", "AltText" },
                new[] { typeof(SubjectPage).FullName, typeof(TextBlock).FullName, typeof(TextBlock).FullName },
                new object[] { subject, subject.TitleText, subject.AltText });
            Check("N01", "incomplete target map refused",
                partialResult.Status == XamlBindScopeAttachStatus.Refused
                    && partialResult.FailureDetail == XamlBindScopeFailureDetail.ManifestIncomplete
                    && partialResult.FailedConnectionId == ListId
                    && LiveBindScopeAttach.ConnectCallCount == 0,
                $"Refused/ManifestIncomplete on id {ListId}, 0 connects",
                $"{partialResult}; connects={LiveBindScopeAttach.ConnectCallCount}");

            // N05  a scope that cannot be initialized or stopped must be refused, never reported as
            //      attached. This is the "S_OK with no effect is invalid" control.
            LiveBindScopeAttach.ResetInstrumentation();
            var inertResult = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createInert(BaseTreeRevisionLive), Ids(), Names(), Types(), Targets(subject));
            Check("N05", "inert scope refused instead of reported attached",
                inertResult.Status == XamlBindScopeAttachStatus.Refused
                    && inertResult.FailureDetail == XamlBindScopeFailureDetail.ScopeLifecycleUnsupported
                    && LiveBindScopeAttach.GetAttachedBindingScope(subject) == null,
                "Refused/ScopeLifecycleUnsupported, no ownership",
                $"{inertResult}; owner={Describe(LiveBindScopeAttach.GetAttachedBindingScope(subject))}");

            // Target outside this root's namescope: an element from the cached instance.
            LiveBindScopeAttach.ResetInstrumentation();
            var foreignTargets = new object[] { subject, s_cachedSubject.TitleText, subject.AltText, subject.TodoList };
            var foreignResult = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV1(BaseTreeRevisionLive), Ids(),
                new[] { string.Empty, string.Empty, "AltText", "TodoList" }, Types(), foreignTargets);
            Check("N06", "target from another live instance refused",
                foreignResult.Status == XamlBindScopeAttachStatus.Refused
                    && foreignResult.FailureDetail == XamlBindScopeFailureDetail.TargetOutsideNamescope
                    && LiveBindScopeAttach.ConnectCallCount == 0,
                "Refused/TargetOutsideNamescope, 0 connects",
                $"{foreignResult}; connects={LiveBindScopeAttach.ConnectCallCount}");

            // ==========================================================================
            // POSITIVE PATH against the cold-built oracle.
            // ==========================================================================

            LiveBindScopeAttach.ResetInstrumentation();
            var attach = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV1(BaseTreeRevisionLive), Ids(), Names(), Types(), Targets(subject));
            Pump();

            Check("T01a", "attach reported truthfully",
                attach.Status == XamlBindScopeAttachStatus.Attached
                    && attach.FailureDetail == XamlBindScopeFailureDetail.None
                    && attach.TargetsConnected == 4
                    && attach.OwnedScopeInstanceId != 0
                    && attach.ObservedBaseTreeRevision == BaseTreeRevisionLive
                    && attach.AppliedScopeRevision == attach.RequestedScopeRevision,
                "Attached/None, 4 targets, non-zero ownership id, revisions echoed",
                attach.ToString());

            Check("T01b", "initial population matches oracle",
                subject.TitleText.Text == oracle.TitleText.Text
                    && subject.AltText.Text == oracle.AltText.Text
                    && ReferenceEquals(subject.TodoList.ItemsSource, subject.ViewModel.Items),
                $"title='{oracle.TitleText.Text}' alt='{oracle.AltText.Text}' ItemsSource identical to VM.Items",
                $"title='{subject.TitleText.Text}' alt='{subject.AltText.Text}' itemsSourceIsVmItems={ReferenceEquals(subject.TodoList.ItemsSource, subject.ViewModel.Items)}");

            // T02  ObservableCollection mutation.
            oracle.ViewModel.Items.Add("c");
            subject.ViewModel.Items.Add("c");
            Pump();
            Check("T02", "ObservableCollection mutation flows",
                subject.TodoList.Items.Count == oracle.TodoList.Items.Count && subject.TodoList.Items.Count == 3,
                "oracle and subject both show 3 items",
                $"oracle={oracle.TodoList.Items.Count} subject={subject.TodoList.Items.Count}");

            // T03  property replacement.
            oracle.ViewModel.Title = "title-1";
            subject.ViewModel.Title = "title-1";
            Pump();
            Check("T03", "property change flows",
                subject.TitleText.Text == oracle.TitleText.Text && subject.TitleText.Text == "title-1",
                "both show title-1", $"oracle='{oracle.TitleText.Text}' subject='{subject.TitleText.Text}'");

            // T04  whole ViewModel replacement.
            var replacementForOracle = new TodoViewModel { Title = "title-2", Subtitle = "subtitle-2" };
            var replacementForSubject = new TodoViewModel { Title = "title-2", Subtitle = "subtitle-2" };
            replacementForOracle.Items.Add("z");
            replacementForSubject.Items.Add("z");
            oracle.ViewModel = replacementForOracle;
            subject.ViewModel = replacementForSubject;
            Pump();
            Check("T04", "ViewModel replacement stays reactive",
                subject.TitleText.Text == oracle.TitleText.Text && subject.TitleText.Text == "title-2"
                    && ReferenceEquals(subject.TodoList.ItemsSource, replacementForSubject.Items),
                "both show title-2 and ItemsSource follows the new VM",
                $"oracle='{oracle.TitleText.Text}' subject='{subject.TitleText.Text}' itemsSourceFollowed={ReferenceEquals(subject.TodoList.ItemsSource, replacementForSubject.Items)}");

            // Old VM must no longer drive the subject.
            replacementForSubject.Title = "title-3";
            subject.ViewModel.Items.Add("w");
            Pump();
            Check("T04b", "new ViewModel drives, old one is released",
                subject.TitleText.Text == "title-3",
                "subject shows title-3", $"subject='{subject.TitleText.Text}'");

            // T07  repeat attach with the same scope revision.
            ulong ownerBeforeRepeat = attach.OwnedScopeInstanceId;
            IComponentConnector scopeBeforeRepeat = LiveBindScopeAttach.GetAttachedBindingScope(subject);
            LiveBindScopeAttach.ResetInstrumentation();
            var repeat = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV1(BaseTreeRevisionLive), Ids(), Names(), Types(), Targets(subject));
            int writesBefore = titleWrites;
            replacementForSubject.Title = "title-4";
            Pump();
            int writesForOneChange = titleWrites - writesBefore;
            Check("T07", "repeat attach is refused and never duplicates writers",
                repeat.Status == XamlBindScopeAttachStatus.AlreadyAttached
                    && repeat.FailureDetail == XamlBindScopeFailureDetail.ScopeRevisionAlreadyApplied
                    && repeat.TargetsConnected == 0
                    && repeat.OwnedScopeInstanceId == ownerBeforeRepeat
                    && ReferenceEquals(LiveBindScopeAttach.GetAttachedBindingScope(subject), scopeBeforeRepeat)
                    && LiveBindScopeAttach.ConnectCallCount == 0
                    && writesForOneChange == 1,
                "AlreadyAttached, same ownership id and same scope object, 0 connects, exactly 1 write per change",
                $"{repeat}; ownerBefore={ownerBeforeRepeat}; sameScope={ReferenceEquals(LiveBindScopeAttach.GetAttachedBindingScope(subject), scopeBeforeRepeat)}; connects={LiveBindScopeAttach.ConnectCallCount}; writesPerChange={writesForOneChange}");

            // Plain attach with a different revision must refuse and point at the scope dimension.
            var conflict = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV2(BaseTreeRevisionLive), Ids(), Names(), Types(), Targets(subject));
            Check("T07b", "different revision refused by plain attach, names the scope dimension",
                conflict.Status == XamlBindScopeAttachStatus.Refused
                    && conflict.FailureDetail == XamlBindScopeFailureDetail.ScopeRevisionConflict
                    && conflict.AppliedScopeRevision != conflict.RequestedScopeRevision,
                "Refused/ScopeRevisionConflict with both revisions reported",
                conflict.ToString());

            // Base fault must dominate a simultaneous scope fault.
            var bothStale = LiveBindScopeAttach.TryAttachBindingScope(
                subject, createV2(BaseTreeRevisionStale), Ids(), Names(), Types(), Targets(subject));
            Check("T07c", "base fault dominates a simultaneous scope fault",
                bothStale.Status == XamlBindScopeAttachStatus.Refused
                    && bothStale.FailureDetail == XamlBindScopeFailureDetail.BaseTreeRevisionMismatch,
                "Refused/BaseTreeRevisionMismatch, not ScopeRevisionConflict", bothStale.ToString());

            // T05  path replacement via Replace: title and alt swap sources.
            string titleBeforeReplace = subject.TitleText.Text;
            var replace = LiveBindScopeAttach.ReplaceBindingScope(
                subject, createV2(BaseTreeRevisionLive), Ids(), Names(), Types(), Targets(subject));
            Pump();
            replacementForSubject.Title = "title-5";
            replacementForSubject.Subtitle = "subtitle-5";
            Pump();
            Check("T05", "path replacement takes over and the old path stops writing",
                replace.Status == XamlBindScopeAttachStatus.Replaced
                    && replace.TargetsDetached == 4
                    && replace.OwnedScopeInstanceId != ownerBeforeRepeat
                    && subject.TitleText.Text == "subtitle-5"
                    && subject.AltText.Text == "title-5",
                "Replaced, 4 detached, new ownership id, title shows Subtitle and alt shows Title",
                $"{replace}; title='{subject.TitleText.Text}' alt='{subject.AltText.Text}' before='{titleBeforeReplace}'");

            // N03  skip detach. Deliberately bypasses ownership to show what detach is protecting.
            var bare = createBare(subject, false);
            bare.Connect(RootId, subject);
            bare.Connect(TitleId, subject.TitleText);
            bare.Connect(AltId, subject.AltText);
            bare.Connect(ListId, subject.TodoList);
            initBare(bare);
            Pump();
            writesBefore = titleWrites;
            replacementForSubject.Title = "title-6";
            replacementForSubject.Subtitle = "subtitle-6";
            Pump();
            int writesWithTwoScopes = titleWrites - writesBefore;
            Check("N03", "skipping detach produces duplicate writers",
                writesWithTwoScopes > 1,
                "more than one write to the same target for one logical change",
                $"writes={writesWithTwoScopes} finalTitle='{subject.TitleText.Text}'");

            // Detach both and confirm the tree goes quiet.
            ((IXamlBindScopeLifecycle)bare).DetachScope();
            var detach = LiveBindScopeAttach.DetachBindingScope(subject);
            Pump();
            string titleAfterDetach = subject.TitleText.Text;
            replacementForSubject.Title = "title-7";
            replacementForSubject.Subtitle = "subtitle-7";
            Pump();
            Check("T06", "detach clears ownership and leaves no writer",
                detach.Status == XamlBindScopeAttachStatus.Detached
                    && detach.TargetsDetached == 4
                    && LiveBindScopeAttach.GetAttachedBindingScope(subject) == null
                    && subject.TitleText.Text == titleAfterDetach,
                "Detached, ownership cleared, target frozen at its last value",
                $"{detach}; owner={Describe(LiveBindScopeAttach.GetAttachedBindingScope(subject))}; title='{subject.TitleText.Text}' expected='{titleAfterDetach}'");

            var detachAgain = LiveBindScopeAttach.DetachBindingScope(subject);
            Check("T06b", "second detach is a refusal, not a success",
                detachAgain.Status == XamlBindScopeAttachStatus.Refused
                    && detachAgain.FailureDetail == XamlBindScopeFailureDetail.NothingAttached,
                "Refused/NothingAttached", detachAgain.ToString());

            // T08  same process, same instance, and a cached off-tree instance.
            Check("T08a", "current page is the same instance in the same process",
                ReferenceEquals(subject, subjectIdentityBefore)
                    && ReferenceEquals(s_host.Children[1], subject),
                "same object, still parented", $"pid={Process.GetCurrentProcess().Id} sameInstance={ReferenceEquals(subject, subjectIdentityBefore)}");

            object cachedIdentityBefore = s_cachedSubject;
            var cachedAttach = LiveBindScopeAttach.TryAttachBindingScope(
                s_cachedSubject, createV1(BaseTreeRevisionLive), Ids(), Names(), Types(), Targets(s_cachedSubject));
            s_cachedSubject.ViewModel.Title = "cached-title-1";
            Pump();
            Check("T08b", "cached, never-parented instance attaches and stays live",
                cachedAttach.Status == XamlBindScopeAttachStatus.Attached
                    && cachedAttach.TargetsConnected == 4
                    && ReferenceEquals(s_cachedSubject, cachedIdentityBefore)
                    && s_cachedSubject.TitleText.Text == "cached-title-1",
                "Attached, 4 targets, same instance, updates flow off-tree",
                $"{cachedAttach}; title='{s_cachedSubject.TitleText.Text}' sameInstance={ReferenceEquals(s_cachedSubject, cachedIdentityBefore)}");

            started.Stop();
            return Report(started.Elapsed, scopeAssembly, subject, oracle);
        }

        private static int[] Ids() => new[] { RootId, TitleId, AltId, ListId };

        private static string[] Names() => new[] { string.Empty, "TitleText", "AltText", "TodoList" };

        private static string[] Types() => new[]
        {
            typeof(SubjectPage).FullName,
            typeof(TextBlock).FullName,
            typeof(TextBlock).FullName,
            typeof(ListView).FullName,
        };

        private static object[] Targets(SubjectPage page) =>
            new object[] { page, page.TitleText, page.AltText, page.TodoList };

        private static string Describe(object o) => o == null ? "<null>" : o.GetType().Name;

        private static void Pump()
        {
            // Layout and binding writes in this probe are synchronous, but give the dispatcher a
            // turn so anything queued by the framework settles before assertions.
            s_host.UpdateLayout();
        }

        private static void Check(string id, string name, bool pass, string expected, string observed)
        {
            s_rows.Add(new Row { Id = id, Name = name, Pass = pass, Expected = expected, Observed = observed });
        }

        private static int Report(TimeSpan elapsed, Assembly scopeAssembly, SubjectPage subject, OraclePage oracle)
        {
            int failed = s_rows.Count(r => !r.Pass);
            var sb = new StringBuilder();

            sb.AppendLine("=== LiveBindScopeProbe ===");
            sb.AppendLine("LANE: app-level simulation of the proposed runtime API.");
            sb.AppendLine("      Proves API semantics and external generated-connector viability.");
            sb.AppendLine("      Does NOT prove a public runtime hook exists; the shipped WinUI runtime is unmodified.");
            sb.AppendLine($"pid={Process.GetCurrentProcess().Id}");
            sb.AppendLine($"process={Process.GetCurrentProcess().MainModule?.FileName}");
            sb.AppendLine($"scopeAssembly={scopeAssembly.Location}");
            sb.AppendLine($"scopeAssemblyMvid={scopeAssembly.ManifestModule.ModuleVersionId}");
            sb.AppendLine($"appAssembly={typeof(ProbeRunner).Assembly.Location}");
            sb.AppendLine($"oracleBaseUri={oracle.BaseUri}");
            sb.AppendLine($"subjectBaseUri={subject.BaseUri}");
            sb.AppendLine($"baseTreeRevision(test token)={BaseTreeRevisionLive}");
            sb.AppendLine($"durationMs={(int)elapsed.TotalMilliseconds}");
            sb.AppendLine();
            sb.AppendLine($"{"ID",-6} {"RESULT",-6} {"CASE",-58}");
            sb.AppendLine(new string('-', 100));

            foreach (var r in s_rows)
            {
                sb.AppendLine($"{r.Id,-6} {(r.Pass ? "PASS" : "FAIL"),-6} {r.Name,-58}");
                sb.AppendLine($"       expected: {r.Expected}");
                sb.AppendLine($"       observed: {r.Observed}");
            }

            sb.AppendLine(new string('-', 100));
            sb.AppendLine($"total={s_rows.Count} passed={s_rows.Count - failed} failed={failed}");
            sb.AppendLine(failed == 0 ? "OVERALL: PASS" : "OVERALL: FAIL");

            string text = sb.ToString();
            Console.WriteLine(text);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-results.txt"), text);
            return failed == 0 ? 0 : 2;
        }

        public static void WriteFatal(Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-results.txt"), "PROBE FATAL:\n" + ex);
            }
            catch { }
        }
    }
}
