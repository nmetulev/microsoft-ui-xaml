// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// APP-LEVEL SIMULATION of the proposed runtime API.
//
// This is a line-for-line managed mirror of the native reference implementation in
// dxaml\xcp\dxaml\lib\XamlBindScopeAttach.cpp. It proves the API semantics and the viability of an
// externally generated connector. It does NOT prove that a public runtime hook already exists: the
// real implementation must live inside Microsoft.UI.Xaml.dll, because only the runtime can own the
// per-root record for the whole lifetime of the root and only the runtime can resolve a name in a
// namescope it owns.
//
// Two things here stand in for runtime-internal facilities, and both are marked RUNTIME BOUNDARY:
//   * name resolution, which the runtime does with CCoreServices::TryGetElementByName(name, root);
//   * namescope reachability, which the runtime does with
//     CDependencyObject::GetStandardNameScopeOwner().
// Everything else is exactly what the native code does.

using System;
using System.Collections.Generic;
using System.Linq;
using LiveBindScopeProbe.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace LiveBindScopeProbe
{
    public static class LiveBindScopeAttach
    {
        private sealed class Record
        {
            public IComponentConnector Scope;
            public string ScopeRevision;
            public string BaseTreeRevision;
            public ulong InstanceId;
            public int TargetCount;
            public bool Desynchronized;
        }

        // Side table keyed by root identity, standing in for the native
        // std::unordered_map<CDependencyObject*, XamlBindScopeRecord>.
        private static readonly Dictionary<object, Record> s_records =
            new Dictionary<object, Record>(ReferenceEqualityComparer.Instance);

        private static readonly Dictionary<object, string> s_baseTreeRevisions =
            new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

        private static ulong s_nextInstanceId;

        // Instrumentation for negative controls. Counts every Connect the API issued, so a test can
        // assert that a refusal happened before any mutation.
        public static int ConnectCallCount { get; private set; }
        public static void ResetInstrumentation() => ConnectCallCount = 0;

        public static void SetBaseTreeRevision(DependencyObject root, string revision)
        {
            if (string.IsNullOrEmpty(revision)) { s_baseTreeRevisions.Remove(root); }
            else { s_baseTreeRevisions[root] = revision; }
        }

        public static string GetBaseTreeRevision(DependencyObject root) =>
            s_baseTreeRevisions.TryGetValue(root, out var r) ? r : string.Empty;

        public static IComponentConnector GetAttachedBindingScope(DependencyObject root) =>
            s_records.TryGetValue(root, out var rec) ? rec.Scope : null;

        public static string GetAttachedScopeRevision(DependencyObject root) =>
            s_records.TryGetValue(root, out var rec) ? rec.ScopeRevision : string.Empty;

        public static XamlBindScopeAttachResult TryAttachBindingScope(
            DependencyObject root, IComponentConnector connector,
            int[] ids, string[] stableNames, string[] typeNames, object[] targets)
            => Attach(root, connector, ids, stableNames, typeNames, targets, allowReplace: false);

        public static XamlBindScopeAttachResult ReplaceBindingScope(
            DependencyObject root, IComponentConnector connector,
            int[] ids, string[] stableNames, string[] typeNames, object[] targets)
            => Attach(root, connector, ids, stableNames, typeNames, targets, allowReplace: true);

        public static XamlBindScopeAttachResult DetachBindingScope(DependencyObject root)
        {
            var result = NewResult();
            if (root == null) { throw new ArgumentNullException(nameof(root)); }

            result.ObservedBaseTreeRevision = GetBaseTreeRevision(root);

            if (!s_records.TryGetValue(root, out var existing))
            {
                return Refuse(result, XamlBindScopeFailureDetail.NothingAttached);
            }

            result.AppliedScopeRevision = existing.ScopeRevision;
            result.TargetsDetached = existing.TargetCount;

            (existing.Scope as IXamlBindScopeLifecycle)?.DetachScope();
            s_records.Remove(root);

            result.Status = XamlBindScopeAttachStatus.Detached;
            result.FailureDetail = XamlBindScopeFailureDetail.None;
            result.OwnedScopeInstanceId = 0;
            return result;
        }

        private static XamlBindScopeAttachResult Attach(
            DependencyObject root, IComponentConnector connector,
            int[] ids, string[] stableNames, string[] typeNames, object[] targets,
            bool allowReplace)
        {
            var result = NewResult();
            if (root == null) { throw new ArgumentNullException(nameof(root)); }
            if (connector == null) { throw new ArgumentNullException(nameof(connector)); }

            // (0) The generated side declares its own facts.
            if (!(connector is IXamlBindScopeManifest manifest))
            {
                return Refuse(result, XamlBindScopeFailureDetail.ScopeManifestUnavailable);
            }

            int rootConnectionId = manifest.RootConnectionId;
            string scopeRevision = manifest.ScopeRevision ?? string.Empty;
            string expectedBaseTreeRevision = manifest.ExpectedBaseTreeRevision ?? string.Empty;
            result.RequestedScopeRevision = scopeRevision;

            s_records.TryGetValue(root, out var existing);
            if (existing != null)
            {
                result.OwnedScopeInstanceId = existing.InstanceId;
                result.AppliedScopeRevision = existing.ScopeRevision;
            }

            // ----------------------------------------------------------------------------------
            // PREFLIGHT. Nothing below mutates until the MUTATION marker.
            // Fault precedence is fixed: base tree, then scope state, then manifest, then rows.
            // ----------------------------------------------------------------------------------

            // (1) Base tree. Dominates everything else and fails closed.
            result.ObservedBaseTreeRevision = GetBaseTreeRevision(root);
            if (string.IsNullOrEmpty(expectedBaseTreeRevision) || string.IsNullOrEmpty(result.ObservedBaseTreeRevision))
            {
                return Refuse(result, XamlBindScopeFailureDetail.BaseTreeRevisionUnavailable);
            }
            if (!string.Equals(expectedBaseTreeRevision, result.ObservedBaseTreeRevision, StringComparison.Ordinal))
            {
                return Refuse(result, XamlBindScopeFailureDetail.BaseTreeRevisionMismatch);
            }

            // (2) Scope state.
            if (existing != null)
            {
                if (string.Equals(scopeRevision, existing.ScopeRevision, StringComparison.Ordinal))
                {
                    result.Status = XamlBindScopeAttachStatus.AlreadyAttached;
                    result.FailureDetail = XamlBindScopeFailureDetail.ScopeRevisionAlreadyApplied;
                    result.TargetsConnected = 0;
                    return result;
                }
                if (!allowReplace)
                {
                    return Refuse(result, XamlBindScopeFailureDetail.ScopeRevisionConflict);
                }
            }

            // (3) Manifest shape.
            if (ids == null || ids.Length == 0 || stableNames == null || typeNames == null || targets == null
                || stableNames.Length != ids.Length || typeNames.Length != ids.Length || targets.Length != ids.Length)
            {
                return Refuse(result, XamlBindScopeFailureDetail.ManifestShapeInvalid);
            }
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] < 0 || Array.IndexOf(ids, ids[i]) != i)
                {
                    return Refuse(result, XamlBindScopeFailureDetail.ManifestDuplicateConnectionId, ids[i]);
                }
            }

            // (3b) Completeness against what the scope says it needs. Without this, an omitted row
            //      produces a connected scope that is silently inert for that target.
            foreach (int required in manifest.GetRequiredConnectionIds())
            {
                if (Array.IndexOf(ids, required) < 0)
                {
                    return Refuse(result, XamlBindScopeFailureDetail.ManifestIncomplete, required);
                }
            }

            // (4) Row identity.
            var rows = new List<(int Id, object Target)>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                object resolved;
                bool resolvedFromNamescope = false;

                if (!string.IsNullOrEmpty(stableNames[i]))
                {
                    // RUNTIME BOUNDARY: CCoreServices::TryGetElementByName(name, rootHandle).
                    resolved = ResolveName(root, stableNames[i]);
                    if (resolved == null)
                    {
                        return Refuse(result, XamlBindScopeFailureDetail.TargetNotResolvable, ids[i]);
                    }
                    // A cast would succeed for either of two adjacent TextBlocks. Identity would not.
                    if (targets[i] != null && !ReferenceEquals(resolved, targets[i]))
                    {
                        return Refuse(result, XamlBindScopeFailureDetail.TargetIdentityMismatch, ids[i]);
                    }
                    // Resolution came out of this root's namescope, so membership is already proven.
                    resolvedFromNamescope = true;
                }
                else
                {
                    resolved = targets[i];
                    if (resolved == null)
                    {
                        return Refuse(result, XamlBindScopeFailureDetail.TargetNotResolvable, ids[i]);
                    }
                }

                // RUNTIME BOUNDARY: CDependencyObject::GetStandardNameScopeOwner() == rootHandle.
                // Only needed for rows with no stable name, and only sound in the runtime: the tree
                // walk used here as a stand-in cannot see an unrealized tree, whereas the namescope
                // owner chain exists from parse time. That gap is one of the reasons this has to be
                // a runtime API rather than an app-level helper.
                if (!resolvedFromNamescope
                    && resolved is DependencyObject resolvedDo
                    && !ReferenceEquals(resolvedDo, root)
                    && !IsInSameNamescope(resolvedDo, root))
                {
                    return Refuse(result, XamlBindScopeFailureDetail.TargetOutsideNamescope, ids[i]);
                }

                // Secondary guard, only after identity.
                if (!string.IsNullOrEmpty(typeNames[i]) &&
                    !string.Equals(resolved.GetType().FullName, typeNames[i], StringComparison.Ordinal))
                {
                    return Refuse(result, XamlBindScopeFailureDetail.TargetTypeMismatch, ids[i]);
                }

                rows.Add((ids[i], resolved));
            }

            // (5) Root row.
            int rootRowIndex = rows.FindIndex(r => r.Id == rootConnectionId);
            if (rootRowIndex < 0 || !ReferenceEquals(rows[rootRowIndex].Target, root))
            {
                return Refuse(result, XamlBindScopeFailureDetail.ManifestMissingRootRow, rootConnectionId);
            }

            rows.Sort((a, b) => a.Id.CompareTo(b.Id));

            // ----------------------------------------------------------------------------------
            // MUTATION.
            // ----------------------------------------------------------------------------------

            // Produce the scope before detaching the outgoing one, so a connector that cannot produce
            // a scope is a clean refusal rather than a tree that lost its bindings.
            IComponentConnector scope = connector.GetBindingConnector(rootConnectionId, root);
            if (scope == null)
            {
                return Refuse(result, XamlBindScopeFailureDetail.ScopeNotProduced, rootConnectionId);
            }

            if (!(scope is IXamlBindScopeLifecycle lifecycle))
            {
                return Refuse(result, XamlBindScopeFailureDetail.ScopeLifecycleUnsupported, rootConnectionId);
            }

            bool replacing = existing != null;
            if (replacing)
            {
                (existing.Scope as IXamlBindScopeLifecycle)?.DetachScope();
                result.TargetsDetached = existing.TargetCount;
                s_records.Remove(root);
            }

            var record = new Record
            {
                Scope = scope,
                InstanceId = ++s_nextInstanceId,
                ScopeRevision = scopeRevision,
                BaseTreeRevision = expectedBaseTreeRevision,
            };

            foreach (var row in rows)
            {
                try
                {
                    ConnectCallCount++;
                    scope.Connect(row.Id, row.Target);
                }
                catch (Exception)
                {
                    record.Desynchronized = true;
                    record.TargetCount = result.TargetsConnected;
                    s_records[root] = record;

                    result.Status = XamlBindScopeAttachStatus.Desynchronized;
                    result.FailureDetail = XamlBindScopeFailureDetail.DesyncScopeState;
                    result.FailedConnectionId = row.Id;
                    result.OwnedScopeInstanceId = record.InstanceId;
                    return result;
                }
                result.TargetsConnected++;
            }

            // The Loading subscription a cold parse relies on already fired for a live root.
            lifecycle.InitializeScope();

            record.TargetCount = result.TargetsConnected;
            s_records[root] = record;

            result.OwnedScopeInstanceId = record.InstanceId;
            result.AppliedScopeRevision = scopeRevision;
            result.Status = replacing ? XamlBindScopeAttachStatus.Replaced : XamlBindScopeAttachStatus.Attached;
            result.FailureDetail = XamlBindScopeFailureDetail.None;
            return result;
        }

        private static object ResolveName(DependencyObject root, string name)
        {
            return root is FrameworkElement fe ? fe.FindName(name) : null;
        }

        private static bool IsInSameNamescope(DependencyObject candidate, DependencyObject root)
        {
            // Structural stand-in for GetStandardNameScopeOwner(): walk to the root of the tree the
            // candidate lives in and require it to be the root we were given. Off-tree roots are
            // supported, which is what makes the cached-instance case work.
            DependencyObject current = candidate;
            while (current != null)
            {
                if (ReferenceEquals(current, root)) { return true; }
                current = VisualTreeHelper.GetParent(current)
                          ?? (current as FrameworkElement)?.Parent as DependencyObject;
            }
            return false;
        }

        private static XamlBindScopeAttachResult NewResult() => new XamlBindScopeAttachResult
        {
            FailedConnectionId = -1,
            ObservedBaseTreeRevision = string.Empty,
            RequestedScopeRevision = string.Empty,
            AppliedScopeRevision = string.Empty,
        };

        private static XamlBindScopeAttachResult Refuse(
            XamlBindScopeAttachResult result, XamlBindScopeFailureDetail detail, int failedConnectionId = -1)
        {
            result.Status = XamlBindScopeAttachStatus.Refused;
            result.FailureDetail = detail;
            result.TargetsConnected = 0;
            result.FailedConnectionId = failedConnectionId;
            return result;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
