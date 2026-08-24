// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Local mirror of the proposed Microsoft.UI.Xaml.Markup surface added in
// dxaml\xcp\tools\XCPTypesAutoGen\XamlOM\Model\Microsoft.UI.Xaml.Markup.cs and
// dxaml\xcp\dxaml\idl\winrt\core\microsoft.ui.xaml.coretypes2.idl.
//
// The probe runs against a shipping Windows App SDK, which does not contain these types yet, so they
// are declared here with identical shape and identical numeric values. IComponentConnector is the
// real runtime interface, not a mirror.

using System;

namespace LiveBindScopeProbe.Contracts
{
    public enum XamlBindScopeAttachStatus
    {
        Attached = 0,
        Replaced = 1,
        Detached = 2,
        AlreadyAttached = 3,
        Refused = 4,
        Desynchronized = 5,
    }

    public enum XamlBindScopeFailureDetail
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

    public struct XamlBindScopeAttachResult
    {
        public XamlBindScopeAttachStatus Status;
        public XamlBindScopeFailureDetail FailureDetail;
        public string ObservedBaseTreeRevision;
        public string RequestedScopeRevision;
        public string AppliedScopeRevision;
        public ulong OwnedScopeInstanceId;
        public int TargetsConnected;
        public int TargetsDetached;
        public int FailedConnectionId;

        public override string ToString() =>
            $"{Status}/{FailureDetail} base='{ObservedBaseTreeRevision}' req='{RequestedScopeRevision}' " +
            $"applied='{AppliedScopeRevision}' owner={OwnedScopeInstanceId} connected={TargetsConnected} " +
            $"detached={TargetsDetached} failedId={FailedConnectionId}";
    }

    // Declared by the object that produces a scope. Everything here is authored by the generated
    // side, so a tool never restates it and cannot get it wrong.
    public interface IXamlBindScopeManifest
    {
        int RootConnectionId { get; }
        int[] GetRequiredConnectionIds();
        string ScopeRevision { get; }
        string ExpectedBaseTreeRevision { get; }
    }

    // Implemented by the produced scope so the runtime can drive the two lifecycle moments a cold
    // parse gets from the Loading subscription and from never needing to stop.
    public interface IXamlBindScopeLifecycle
    {
        void InitializeScope();
        void DetachScope();
    }
}
