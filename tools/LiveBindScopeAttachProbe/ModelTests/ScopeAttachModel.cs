// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Headless model of the live {x:Bind} scope attach state machine.
//
// This is a third expression of the algorithm in dxaml\xcp\dxaml\lib\XamlBindScopeAttach.cpp, with
// the runtime dependencies hoisted behind IScopeHost so it can run with no XAML core, no window and
// no Windows App SDK. Its job is precedence and state-machine coverage, and to be mutated.
//
// Step numbering below matches the native file one to one:
//
//   model step | XamlBindScopeAttach.cpp
//   -----------|------------------------------------------------------------------
//   (0)        | manifest QI                                      Attach step (0)
//   (1)        | base tree revision, dominates, fail closed       Attach step (1)
//   (2)        | scope state                                      Attach step (2)
//   (3)        | manifest shape / duplicate ids                   Attach step (3)
//   (3b)       | completeness vs connector-declared required ids  Attach step (3b)
//   (4)        | per row identity, namescope, type guard          Attach step (4)
//   (5)        | root row present and is the root                 Attach step (5)
//   (6)        | ascending sort                                   Attach step (6)
//   (7)(8)     | produce scope, require lifecycle                 MUTATION 7-8
//   (9)        | detach outgoing                                  MUTATION 9
//   (10)       | Connect loop, desync on failure                  MUTATION 10
//   (11)       | InitializeScope                                  MUTATION 11
//   (12)       | publish ownership                                MUTATION 12

using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveBindScopeProbe.Model
{
    public enum AttachStatus { Attached, Replaced, Detached, AlreadyAttached, Refused, Desynchronized }

    public enum FailureDetail
    {
        None = 0,
        BaseTreeRevisionUnavailable = 1,
        BaseTreeRevisionMismatch = 2,
        ScopeRevisionAlreadyApplied = 3,
        ScopeRevisionConflict = 4,
        ScopeNotProduced = 5,
        ManifestShapeInvalid = 6,
        ManifestDuplicateConnectionId = 7,
        ManifestMissingRootRow = 8,
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
    }

    public struct AttachResult
    {
        public AttachStatus Status;
        public FailureDetail FailureDetail;
        public string ObservedBaseTreeRevision;
        public string RequestedScopeRevision;
        public string AppliedScopeRevision;
        public ulong OwnedScopeInstanceId;
        public int TargetsConnected;
        public int TargetsDetached;
        public int FailedConnectionId;

        public override string ToString() =>
            $"{Status}/{FailureDetail} observedBase='{ObservedBaseTreeRevision}' requested='{RequestedScopeRevision}' " +
            $"applied='{AppliedScopeRevision}' owner={OwnedScopeInstanceId} connected={TargetsConnected} " +
            $"detached={TargetsDetached} failedId={FailedConnectionId}";
    }

    public sealed class TargetRow
    {
        public int ConnectionId;
        public string StableName;
        public string ExpectedTypeName;
        public object Target;
    }

    public interface IScopeManifest
    {
        int RootConnectionId { get; }
        int[] RequiredConnectionIds { get; }
        string ScopeRevision { get; }
        string ExpectedBaseTreeRevision { get; }
    }

    public interface IScopeLifecycle
    {
        void InitializeScope();
        void DetachScope();
    }

    public interface IConnector
    {
        object GetBindingConnector(int connectionId, object root);
    }

    public interface IScope
    {
        void Connect(int connectionId, object target);
    }

    // Everything the runtime supplies. In the product these are
    // CCoreServices::TryGetElementByName and CDependencyObject::GetStandardNameScopeOwner.
    public interface IScopeHost
    {
        object ResolveName(object root, string name);
        object GetNamescopeOwner(object target);
        string GetRuntimeTypeName(object target);
        string GetBaseTreeRevision(object root);
    }

    // Selects exactly one seeded defect. Every mutant must be killed by at least one test.
    public enum Mutant
    {
        None = 0,
        SkipBaseTreePrecedence,         // evaluate scope state before base tree
        IdentityByTypeNotInstance,      // accept a row when only the type matches
        SkipLifecycleRequirement,       // attach a scope that cannot be initialized or stopped
        SkipDetachOnReplace,            // leave the outgoing scope subscribed
        SkipCompletenessCheck,          // allow a manifest that misses a required id
        BaseTreeFailOpen,               // treat a missing revision as "no check needed"
        PublishOwnershipBeforeConnect,  // take ownership before the Connect loop completes
        RepeatAttachReportsAttached,    // report Attached for an already applied revision
        SkipRootRowCheck,               // do not require a row for the root connection id
    }

    public sealed class ScopeAttachModel
    {
        private sealed class Record
        {
            public object Scope;
            public string ScopeRevision;
            public string BaseTreeRevision;
            public ulong InstanceId;
            public int TargetCount;
            public bool Desynchronized;
        }

        private readonly IScopeHost _host;
        private readonly Mutant _mutant;
        private readonly Dictionary<object, Record> _records = new(ReferenceEqualityComparer.Instance);
        private ulong _nextInstanceId;

        public int ConnectCallCount { get; private set; }
        public void ResetInstrumentation() => ConnectCallCount = 0;

        public ScopeAttachModel(IScopeHost host, Mutant mutant = Mutant.None)
        {
            _host = host;
            _mutant = mutant;
        }

        public object GetAttachedScope(object root) => _records.TryGetValue(root, out var r) ? r.Scope : null;

        public string GetAttachedScopeRevision(object root) => _records.TryGetValue(root, out var r) ? r.ScopeRevision : string.Empty;

        public AttachResult TryAttach(object root, IConnector connector, IReadOnlyList<TargetRow> rows)
            => Attach(root, connector, rows, allowReplace: false);

        public AttachResult Replace(object root, IConnector connector, IReadOnlyList<TargetRow> rows)
            => Attach(root, connector, rows, allowReplace: true);

        public AttachResult Detach(object root)
        {
            var result = NewResult();
            result.ObservedBaseTreeRevision = _host.GetBaseTreeRevision(root) ?? string.Empty;

            if (!_records.TryGetValue(root, out var existing))
            {
                return Refuse(result, FailureDetail.NothingAttached);
            }

            result.AppliedScopeRevision = existing.ScopeRevision;
            result.TargetsDetached = existing.TargetCount;
            (existing.Scope as IScopeLifecycle)?.DetachScope();
            _records.Remove(root);

            result.Status = AttachStatus.Detached;
            result.FailureDetail = FailureDetail.None;
            result.OwnedScopeInstanceId = 0;
            return result;
        }

        private AttachResult Attach(object root, IConnector connector, IReadOnlyList<TargetRow> rows, bool allowReplace)
        {
            var result = NewResult();

            // (0) manifest
            if (connector is not IScopeManifest manifest)
            {
                return Refuse(result, FailureDetail.ScopeManifestUnavailable);
            }

            string scopeRevision = manifest.ScopeRevision ?? string.Empty;
            string expectedBase = manifest.ExpectedBaseTreeRevision ?? string.Empty;
            result.RequestedScopeRevision = scopeRevision;
            result.ObservedBaseTreeRevision = _host.GetBaseTreeRevision(root) ?? string.Empty;

            _records.TryGetValue(root, out var existing);
            if (existing != null)
            {
                result.OwnedScopeInstanceId = existing.InstanceId;
                result.AppliedScopeRevision = existing.ScopeRevision;
            }

            bool baseFirst = _mutant != Mutant.SkipBaseTreePrecedence;

            // (1) base tree, dominates every other fault
            if (baseFirst && !CheckBaseTree(ref result, expectedBase, out FailureDetail baseFault))
            {
                return Refuse(result, baseFault);
            }

            // (2) scope state
            if (existing != null)
            {
                if (string.Equals(scopeRevision, existing.ScopeRevision, StringComparison.Ordinal))
                {
                    if (_mutant == Mutant.RepeatAttachReportsAttached)
                    {
                        result.Status = AttachStatus.Attached;
                        result.FailureDetail = FailureDetail.None;
                        result.TargetsConnected = existing.TargetCount;
                        return result;
                    }
                    result.Status = AttachStatus.AlreadyAttached;
                    result.FailureDetail = FailureDetail.ScopeRevisionAlreadyApplied;
                    result.TargetsConnected = 0;
                    return result;
                }
                if (!allowReplace)
                {
                    return Refuse(result, FailureDetail.ScopeRevisionConflict);
                }
            }

            if (!baseFirst && !CheckBaseTree(ref result, expectedBase, out FailureDetail lateBaseFault))
            {
                return Refuse(result, lateBaseFault);
            }

            // (3) manifest shape
            if (rows == null || rows.Count == 0)
            {
                return Refuse(result, FailureDetail.ManifestShapeInvalid);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].ConnectionId < 0 || rows.Take(i).Any(r => r.ConnectionId == rows[i].ConnectionId))
                {
                    return Refuse(result, FailureDetail.ManifestDuplicateConnectionId, rows[i].ConnectionId);
                }
            }

            // (3b) completeness against connector-declared required ids
            if (_mutant != Mutant.SkipCompletenessCheck)
            {
                foreach (int required in manifest.RequiredConnectionIds)
                {
                    if (!rows.Any(r => r.ConnectionId == required))
                    {
                        return Refuse(result, FailureDetail.ManifestIncomplete, required);
                    }
                }
            }

            // (4) per row identity
            var resolvedRows = new List<(int Id, object Target)>(rows.Count);
            foreach (TargetRow row in rows)
            {
                object resolved;
                bool fromNamescope = false;

                if (!string.IsNullOrEmpty(row.StableName))
                {
                    resolved = _host.ResolveName(root, row.StableName);
                    if (resolved == null)
                    {
                        return Refuse(result, FailureDetail.TargetNotResolvable, row.ConnectionId);
                    }

                    bool identityOk = _mutant == Mutant.IdentityByTypeNotInstance
                        ? row.Target == null || _host.GetRuntimeTypeName(resolved) == _host.GetRuntimeTypeName(row.Target)
                        : row.Target == null || ReferenceEquals(resolved, row.Target);

                    if (!identityOk)
                    {
                        return Refuse(result, FailureDetail.TargetIdentityMismatch, row.ConnectionId);
                    }

                    // Under the mutant a type match is accepted and the caller's object is used,
                    // which is precisely the silent misconnection this contract exists to stop.
                    if (_mutant == Mutant.IdentityByTypeNotInstance && row.Target != null)
                    {
                        resolved = row.Target;
                    }
                    else
                    {
                        fromNamescope = true;
                    }
                }
                else
                {
                    resolved = row.Target;
                    if (resolved == null)
                    {
                        return Refuse(result, FailureDetail.TargetNotResolvable, row.ConnectionId);
                    }
                }

                if (!fromNamescope && !ReferenceEquals(resolved, root)
                    && !ReferenceEquals(_host.GetNamescopeOwner(resolved), root))
                {
                    return Refuse(result, FailureDetail.TargetOutsideNamescope, row.ConnectionId);
                }

                if (!string.IsNullOrEmpty(row.ExpectedTypeName)
                    && !string.Equals(_host.GetRuntimeTypeName(resolved), row.ExpectedTypeName, StringComparison.Ordinal))
                {
                    return Refuse(result, FailureDetail.TargetTypeMismatch, row.ConnectionId);
                }

                resolvedRows.Add((row.ConnectionId, resolved));
            }

            // (5) root row
            if (_mutant != Mutant.SkipRootRowCheck)
            {
                int rootIndex = resolvedRows.FindIndex(r => r.Id == manifest.RootConnectionId);
                if (rootIndex < 0 || !ReferenceEquals(resolvedRows[rootIndex].Target, root))
                {
                    return Refuse(result, FailureDetail.ManifestMissingRootRow, manifest.RootConnectionId);
                }
            }

            // (6) parse order
            resolvedRows.Sort((a, b) => a.Id.CompareTo(b.Id));

            // ------------------------------ MUTATION ------------------------------

            // (7) produce the scope before detaching the outgoing one
            object scope = connector.GetBindingConnector(manifest.RootConnectionId, root);
            if (scope == null)
            {
                return Refuse(result, FailureDetail.ScopeNotProduced, manifest.RootConnectionId);
            }

            // (8) lifecycle is mandatory
            var lifecycle = scope as IScopeLifecycle;
            if (_mutant != Mutant.SkipLifecycleRequirement && lifecycle == null)
            {
                return Refuse(result, FailureDetail.ScopeLifecycleUnsupported, manifest.RootConnectionId);
            }

            bool replacing = existing != null;
            if (replacing)
            {
                // (9) stop the outgoing scope before the new one exists in the record
                if (_mutant != Mutant.SkipDetachOnReplace)
                {
                    (existing.Scope as IScopeLifecycle)?.DetachScope();
                }
                result.TargetsDetached = existing.TargetCount;
                _records.Remove(root);
            }

            var record = new Record
            {
                Scope = scope,
                InstanceId = ++_nextInstanceId,
                ScopeRevision = scopeRevision,
                BaseTreeRevision = expectedBase,
            };

            if (_mutant == Mutant.PublishOwnershipBeforeConnect)
            {
                record.TargetCount = resolvedRows.Count;
                _records[root] = record;
            }

            // (10) connect
            foreach (var row in resolvedRows)
            {
                try
                {
                    ConnectCallCount++;
                    ((IScope)scope).Connect(row.Id, row.Target);
                }
                catch (Exception)
                {
                    record.Desynchronized = true;
                    record.TargetCount = result.TargetsConnected;
                    _records[root] = record;

                    result.Status = AttachStatus.Desynchronized;
                    result.FailureDetail = FailureDetail.DesyncScopeState;
                    result.FailedConnectionId = row.Id;
                    result.OwnedScopeInstanceId = record.InstanceId;
                    return result;
                }
                result.TargetsConnected++;
            }

            // (11) first update, in place of the Loading callback
            lifecycle?.InitializeScope();

            // (12) publish ownership
            record.TargetCount = result.TargetsConnected;
            _records[root] = record;

            result.OwnedScopeInstanceId = record.InstanceId;
            result.AppliedScopeRevision = scopeRevision;
            result.Status = replacing ? AttachStatus.Replaced : AttachStatus.Attached;
            result.FailureDetail = FailureDetail.None;
            return result;
        }

        // (1) base tree: dominates, fails closed.
        private bool CheckBaseTree(ref AttachResult result, string expectedBase, out FailureDetail fault)
        {
            fault = FailureDetail.None;

            bool missing = string.IsNullOrEmpty(expectedBase) || string.IsNullOrEmpty(result.ObservedBaseTreeRevision);
            if (missing)
            {
                if (_mutant == Mutant.BaseTreeFailOpen) { return true; }
                fault = FailureDetail.BaseTreeRevisionUnavailable;
                return false;
            }

            if (!string.Equals(expectedBase, result.ObservedBaseTreeRevision, StringComparison.Ordinal))
            {
                fault = FailureDetail.BaseTreeRevisionMismatch;
                return false;
            }
            return true;
        }

        private static AttachResult NewResult() => new AttachResult
        {
            FailedConnectionId = -1,
            ObservedBaseTreeRevision = string.Empty,
            RequestedScopeRevision = string.Empty,
            AppliedScopeRevision = string.Empty,
        };

        private static AttachResult Refuse(AttachResult result, FailureDetail detail, int failedConnectionId = -1)
        {
            result.Status = AttachStatus.Refused;
            result.FailureDetail = detail;
            result.TargetsConnected = 0;
            result.FailedConnectionId = failedConnectionId;
            return result;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
