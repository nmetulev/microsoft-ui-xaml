// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.
using OM;
using System;
using XamlOM;

namespace Microsoft.UI.Xaml.Markup
{
    [DXamlIdlGroup("coretypes2")]
    [TypeTable(IsExcludedFromDXaml = true, IsExcludedFromCore = true, IsExcludedFromNewTypeTable = true)]
    [Guids(ClassGuid = "3c1b0d18-b6ec-4773-9b81-e56a53c3923e")]
    public static class XamlReader
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static Windows.Foundation.Object Load(Windows.Foundation.String xaml)
        {
            return default(Windows.Foundation.Object);
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static Windows.Foundation.Object LoadWithInitialTemplateValidation(Windows.Foundation.String xaml)
        {
            return default(Windows.Foundation.Object);
        }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromDXaml = true, IsExcludedFromCore = true, IsExcludedFromNewTypeTable = true)]
    [HandWritten]
    [Guids(ClassGuid = "6bffe1c5-d5c6-4b6a-aaaf-373e86fd65aa")]
    public static class XamlBinaryWriter
    {
        public static XamlBinaryWriterErrorInformation Write(
            Windows.Foundation.Collections.IVector<Windows.Storage.Streams.IRandomAccessStream> inputStreams,
            Windows.Foundation.Collections.IVector<Windows.Storage.Streams.IRandomAccessStream> outputStreams,
            IXamlMetadataProvider xamlMetadataProvider)
        {
            return default(XamlBinaryWriterErrorInformation);
        }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public interface IComponentConnector
    {
        void Connect(int connectionId, Windows.Foundation.Object target);

        Microsoft.UI.Xaml.Markup.IComponentConnector GetBindingConnector(int connectionId, Windows.Foundation.Object target);
    }

    [DXamlIdlGroup("coretypes2")]
    public interface IDataTemplateComponent
    {
        void Recycle();

        void ProcessBindings(Windows.Foundation.Object item, int itemIndex, int phase, out int nextPhase);
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public interface IXamlType
    {
        IXamlType BaseType { get; }
        IXamlMember ContentProperty { get; }
        string FullName { get; }
        bool IsArray { get; }
        bool IsCollection { get; }
        bool IsConstructible { get; }
        bool IsDictionary { get; }
        bool IsMarkupExtension { get; }
        bool IsBindable { get; }
        IXamlType ItemType { get; }
        IXamlType KeyType { get; }
        IXamlType BoxedType { get; }
        Windows.UI.Xaml.Interop.TypeName UnderlyingType { get; }

        [ReturnTypeParameterName("instance")]
        object ActivateInstance();

        [ReturnTypeParameterName("instance")]
        object CreateFromString(string value);

        [ReturnTypeParameterName("xamlMember")]
        IXamlMember GetMember(string name);

        void AddToVector(object instance, object value);
        void AddToMap(object instance, object key, object value);
        void RunInitializer();
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public interface IXamlMember
    {
        bool IsAttachable { get; }
        bool IsDependencyProperty { get; }
        bool IsReadOnly { get; }
        string Name { get; }
        IXamlType TargetType { get; }
        IXamlType Type { get; }

        [ReturnTypeParameterName("value")]
        object GetValue(object instance);

        void SetValue(object instance, object value);
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public interface IXamlMetadataProvider
    {
        [DXamlOverloadName("GetXamlType")]
        [DefaultOverload]
        [ReturnTypeParameterName("xamlType")]
        IXamlType GetXamlType(Windows.UI.Xaml.Interop.TypeName type);

        [DXamlOverloadName("GetXamlType")]
        [ReturnTypeParameterName("xamlType")]
        IXamlType GetXamlTypeByFullName(string fullName);

        [ReturnTypeParameterName("definitions")]
        [CountParameterName("length")]
        XmlnsDefinition[] GetXmlnsDefinitions();
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public interface IXamlBindScopeDiagnostics
    {
        void Disable(int lineNumber, int columnNumber);
    }

    // Optional interface a compiled-binding scope implements so the runtime can drive the two
    // lifecycle moments that a cold parse gets for free.
    //
    // In a cold parse the generated scope subscribes to FrameworkElement.Loading inside
    // GetBindingConnector, and that Loading callback performs the first Update. A root that is
    // already live has already raised Loading, so nothing would ever run the first Update, and
    // Connect alone would leave the scope populated but inert. Likewise IComponentConnector has no
    // way to tell a scope it is being replaced, so an outgoing scope would keep its
    // PropertyChanged/DataContextChanged/collection listeners and keep writing its targets.
    //
    // The runtime refuses a live attach when the produced scope does not implement this interface,
    // rather than reporting success for an operation that could have no effect.
    [DXamlIdlGroup("coretypes2")]
    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_3_0)]
    [CodeGen(CodeGenLevel.LookupOnly)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public interface IXamlBindScopeLifecycle
    {
        // Called once, after every manifest row has been connected, in place of the Loading
        // callback a cold parse would have used. Must be idempotent.
        void InitializeScope();

        // Called on the outgoing scope before a replace, and on the owned scope on detach. Must
        // release every listener the scope registered (property changed, data context changed,
        // collection changed, loading) so the scope stops writing its targets.
        void DetachScope();
    }

    [DXamlIdlGroup("coretypes2")]
    [HideFromOldCodeGen]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public struct XmlnsDefinition
    {
        public string XmlNamespace { get; set; }
        public string Namespace { get; set; }
    }

    [DXamlIdlGroup("coretypes2")]
    [HideFromOldCodeGen]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public struct XamlBinaryWriterErrorInformation
    {
        public uint InputStreamIndex { get; set; }
        public uint LineNumber { get; set; }
        public uint LinePosition { get; set; }
    }

    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), 1, ForcePrimaryInterfaceGeneration = true)]
    [DXamlIdlGroup("coretypes2")]
    [Guids(ClassGuid = "09866282-4cdb-49c8-8c7d-9d40363c4f96")]
    public static class XamlMarkupHelper
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        static public void UnloadObject(Microsoft.UI.Xaml.DependencyObject element)
        {
        }
    }

    // Coarse outcome of a live compiled-binding ({x:Bind}) scope attach, replace or detach performed
    // on an already-constructed object tree. Callers branch on this; XamlBindScopeFailureDetail says
    // why. Only Attached/Replaced/Detached mutate the tree. Desynchronized means a mutation started
    // and could not be rolled back.
    [DXamlIdlGroup("coretypes2")]
    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_3_0)]
    [TypeTable(IsExcludedFromCore = true, IsExcludedFromNewTypeTable = true)]
    [EnumFlags(IsExcludedFromNative = true)]
    public enum XamlBindScopeAttachStatus
    {
        // A new scope was created and every manifest row was connected.
        Attached = 0,
        // A previously attached scope was detached and a new one was attached in its place.
        Replaced = 1,
        // A previously attached scope was detached and no new scope was attached.
        Detached = 2,
        // The root already owns a scope with the same scope revision. Nothing changed.
        AlreadyAttached = 3,
        // Preflight rejected the request. Nothing changed. See FailureDetail.
        Refused = 4,
        // Mutation began and could not be completed or rolled back. The caller must rebuild the
        // root. FailureDetail names which dimension desynchronized.
        Desynchronized = 5,
    }

    // Precise reason accompanying a XamlBindScopeAttachStatus. Deliberately fine grained so a tool
    // can tell a stale base tree from a stale scope from a bad manifest row.
    [DXamlIdlGroup("coretypes2")]
    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_3_0)]
    [TypeTable(IsExcludedFromCore = true, IsExcludedFromNewTypeTable = true)]
    [EnumFlags(IsExcludedFromNative = true)]
    public enum XamlBindScopeFailureDetail
    {
        None = 0,

        // --- base tree dimension: identity of the XBF/object graph that built the live root ---
        // The caller supplied an expected base tree revision but the root carries none, so the
        // precondition could not be evaluated. Never treated as success.
        BaseTreeRevisionUnavailable = 1,
        // The root was built from a different base tree revision than the manifest was authored
        // against. Connection ids in the manifest cannot be trusted.
        BaseTreeRevisionMismatch = 2,

        // --- scope dimension: identity of the newly generated binding scope + binding manifest ---
        // The root already owns a scope carrying this exact scope revision; attaching again would
        // duplicate writers.
        ScopeRevisionAlreadyApplied = 3,
        // The root owns a scope from a different revision; the caller must use ReplaceBindingScope.
        ScopeRevisionConflict = 4,
        // The connector returned no scope for the supplied root connection id.
        ScopeNotProduced = 5,

        // --- manifest shape ---
        // Parallel manifest arrays have mismatched lengths, or the manifest is empty.
        ManifestShapeInvalid = 6,
        // A connection id appears twice, or is negative.
        ManifestDuplicateConnectionId = 7,
        // The manifest has no row whose connection id equals the supplied root connection id, or
        // that row's target is not the root.
        ManifestMissingRootRow = 8,

        // --- per-row target identity ---
        // A row named an element that does not resolve inside the root's namescope, and supplied
        // no object either.
        TargetNotResolvable = 9,
        // A row supplied both a stable name and an object and they are not the same instance.
        // This is the check that catches two adjacent same-typed elements being swapped; a
        // successful QueryInterface/cast is explicitly not accepted as evidence of identity.
        TargetIdentityMismatch = 10,
        // The resolved target's runtime class name does not equal the row's expected type name.
        // Secondary guard only; identity is checked first.
        TargetTypeMismatch = 11,
        // The resolved target is not reachable from the root's namescope, so it does not belong to
        // this document instance.
        TargetOutsideNamescope = 12,

        // --- detach ---
        // Detach was requested but the root owns no scope.
        NothingAttached = 13,

        // --- desynchronization ---
        // Connect failed part way through populating the new scope. Scope state is indeterminate.
        DesyncScopeState = 14,
        // The base tree changed underneath the operation while it was running.
        DesyncBaseTree = 15,

        // The scope the connector produced does not implement IXamlBindScopeLifecycle, so the
        // runtime cannot run the initial update or later stop the scope. Attaching it would produce
        // an inert scope and an unstoppable writer, so the request is refused instead.
        ScopeLifecycleUnsupported = 16,
    }

    // Truthful result of an attach/replace/detach request. The runtime never reports plain success
    // for a call that had no effect, and always echoes both revision dimensions so a tool can tell
    // which one is stale without interpreting a single opaque token.
    //
    // Fault precedence, deliberately fixed: a base tree fault dominates every other fault. If the
    // live root was built from a different base tree than the manifest was authored against, the
    // connection ids in the manifest are meaningless, so the runtime refuses before it looks at the
    // scope revision, the manifest shape or any row. Zero targets are connected and Attached is
    // never returned. The remedy for a base tree fault is to re-inflate the root; the remedy for a
    // scope fault is ReplaceBindingScope.
    //
    // The check is fail closed. A caller that supplies no expected base tree revision, or a root
    // that carries none, is refused with BaseTreeRevisionUnavailable rather than attached blindly.
    [DXamlIdlGroup("coretypes2")]
    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_3_0)]
    [HideFromOldCodeGen]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    public struct XamlBindScopeAttachResult
    {
        public XamlBindScopeAttachStatus Status { get; set; }
        public XamlBindScopeFailureDetail FailureDetail { get; set; }
        // Base tree revision the runtime actually observed on the root. Empty when the root carries
        // none, which is itself a refusal. Opaque to the runtime: the producer chooses the content,
        // expected to be XBF content hash combined with source checksum, compiler/schema version and
        // connection-map hash. Without the connection-map hash the check is only partial, and that
        // is a property of the producer, not of this contract.
        public string ObservedBaseTreeRevision { get; set; }
        // Scope revision the caller asked for, echoed back so a caller can correlate results.
        public string RequestedScopeRevision { get; set; }
        // Scope revision now recorded against the root. On AlreadyAttached and on a scope conflict
        // this is the revision that was already live, which is what distinguishes "mine is stale"
        // from "mine is already applied".
        public string AppliedScopeRevision { get; set; }
        // Runtime-assigned identity of the ownership record currently held for the root, or 0 when
        // the root owns no scope. Monotonic per process. A repeat attach that is correctly refused
        // leaves this unchanged, which is independently checkable without trusting the status.
        public ulong OwnedScopeInstanceId { get; set; }
        // Rows handed to IComponentConnector::Connect on the new scope. Always 0 on any refusal.
        public int TargetsConnected { get; set; }
        // Rows the previous scope owned, when one was detached.
        public int TargetsDetached { get; set; }
        // Connection id that caused the refusal or desync, or -1 when not applicable.
        public int FailedConnectionId { get; set; }
    }

    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), 1, ForcePrimaryInterfaceGeneration = true)]
    [Platform(2, typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_2_2)]
    [Platform(3, typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_3_0)]
    [PartialFactory]
    [DXamlIdlGroup("coretypes2")]
    [Guids(ClassGuid = "5907bcb4-ff97-47ad-8049-fa5b5da86032")]
    public static class XamlBindingHelper
    {

    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), 1, ForcePrimaryInterfaceGeneration = true)]
    [Platform(2, typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_2_2)]
    [Platform(3, typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_3_0)]
    [PartialFactory]
    [DXamlIdlGroup("coretypes2")]
    [Guids(ClassGuid = "5907bcb4-ff97-47ad-8049-fa5b5da86032")]
    public static class XamlBindingHelper
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SuspendRendering(UIElement target)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void ResumeRendering(UIElement target)
        {
        }

        [Attached(TargetType = typeof(DependencyObject))]
        [NativeStorageType(OM.ValueType.valueObject)]
        public static Microsoft.UI.Xaml.Markup.IDataTemplateComponent AttachedDataTemplateComponent
        {
            get;
            set;
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static Windows.Foundation.Object ConvertValue(Type type, Windows.Foundation.Object value)
        {
            return default(Windows.Foundation.Object);
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromString(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.String value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromBoolean(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Boolean value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromChar16(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Char16 value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromDateTime(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.DateTime value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromDouble(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Double value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromInt32(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Int32 value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromUInt32(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.UInt32 value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromInt64(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Int64 value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromUInt64(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.UInt64 value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromSingle(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Float value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromPoint(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Point value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromRect(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Rect value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromSize(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Size value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromTimeSpan(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.TimeSpan value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromByte(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Byte value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromUri(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Uri value)
        {
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromObject(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.Foundation.Object value)
        {
        }

        [Version(2)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromThickness(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Microsoft.UI.Xaml.Thickness value)
        {
        }

        [Version(2)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromCornerRadius(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Microsoft.UI.Xaml.CornerRadius value)
        {
        }

        [Version(2)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetPropertyFromColor(Windows.Foundation.Object dependencyObject, DependencyProperty propertyToSet, Windows.UI.Color value)
        {
        }

        // ---------------------------------------------------------------------------------------
        // Live compiled-binding scope attach.
        //
        // A compiled-binding scope is normally produced exactly once, during parse, by
        // IComponentConnector::GetBindingConnector on the root connection id, and is then populated
        // by IComponentConnector::Connect for every subsequent connection id. Nothing survives that
        // parse: the runtime does not retain a connectionId -> object map, so a caller wanting to
        // introduce a scope into an already-constructed tree must supply the target map explicitly.
        //
        // Two independent revision dimensions are involved and both are reported back:
        //   baseTreeRevision  identity of the XBF/object graph that constructed the live root. The
        //                     producer chooses the content (XBF hash + connection-map hash +
        //                     compiler/schema version). Connection ids are only meaningful relative
        //                     to it, so every manifest row is validated against it.
        //   scopeRevision     identity of the newly generated binding scope and its manifest. It is
        //                     recorded on the root so repeat attaches are idempotent or refused.
        // The feature deliberately pairs an OLD base tree with a NEW scope, so these are never
        // required to be equal.
        // ---------------------------------------------------------------------------------------

        // Attaches a newly generated compiled-binding scope to an already-constructed root that does
        // not currently own one.
        //
        // The manifest is four parallel arrays, one row per connection id:
        //   targetConnectionIds  the compiler-assigned connection id.
        //   targetStableNames    scope-qualified x:Name of the element, or empty when it has none.
        //                        When present the runtime resolves it independently and requires the
        //                        resolved instance to be the same object as targetObjects[i].
        //   targetTypeNames      expected runtime class name, used only as a secondary guard after
        //                        identity has been established. A successful cast is never accepted
        //                        as evidence that a row refers to the intended element.
        //   targetObjects        the live object, required for rows with no stable name.
        //
        // All validation happens before any call to GetBindingConnector or Connect, so a refusal
        // leaves the tree untouched.
        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static XamlBindScopeAttachResult TryAttachBindingScope(
            Microsoft.UI.Xaml.DependencyObject root,
            Microsoft.UI.Xaml.Markup.IComponentConnector connector,
            Windows.Foundation.Int32 rootConnectionId,
            Windows.Foundation.Int32[] targetConnectionIds,
            Windows.Foundation.String[] targetStableNames,
            Windows.Foundation.String[] targetTypeNames,
            Windows.Foundation.Object[] targetObjects,
            Windows.Foundation.String expectedBaseTreeRevision,
            Windows.Foundation.String scopeRevision)
        {
            return default(XamlBindScopeAttachResult);
        }

        // Detaches whatever scope the root currently owns and attaches a new one in a single
        // operation. Preflight is identical to TryAttachBindingScope, except that an existing scope
        // is expected rather than refused. The previous scope stops receiving updates before the new
        // one is created, so no interval exists in which two scopes write the same target.
        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static XamlBindScopeAttachResult ReplaceBindingScope(
            Microsoft.UI.Xaml.DependencyObject root,
            Microsoft.UI.Xaml.Markup.IComponentConnector connector,
            Windows.Foundation.Int32 rootConnectionId,
            Windows.Foundation.Int32[] targetConnectionIds,
            Windows.Foundation.String[] targetStableNames,
            Windows.Foundation.String[] targetTypeNames,
            Windows.Foundation.Object[] targetObjects,
            Windows.Foundation.String expectedBaseTreeRevision,
            Windows.Foundation.String scopeRevision)
        {
            return default(XamlBindScopeAttachResult);
        }

        // Releases the scope the root owns, clearing runtime ownership so no writer is left behind.
        // Returns NothingAttached when the root owns no scope; this is a refusal, not a success.
        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static XamlBindScopeAttachResult DetachBindingScope(Microsoft.UI.Xaml.DependencyObject root)
        {
            return default(XamlBindScopeAttachResult);
        }

        // Ownership observability. Returns the scope the runtime currently holds for the root, or
        // null. Lets a tool prove that exactly one scope is owned.
        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static Microsoft.UI.Xaml.Markup.IComponentConnector GetAttachedBindingScope(Microsoft.UI.Xaml.DependencyObject root)
        {
            return default(Microsoft.UI.Xaml.Markup.IComponentConnector);
        }

        // Scope revision currently recorded against the root, or the empty string.
        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static Windows.Foundation.String GetAttachedScopeRevision(Microsoft.UI.Xaml.DependencyObject root)
        {
            return default(Windows.Foundation.String);
        }

        // Base tree revision stamped on the root when it was inflated, or the empty string. Written
        // by whoever produced the tree; the versioned-XBF loader is expected to own this once it
        // exists, which is the seam where the two prototypes meet.
        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static Windows.Foundation.String GetBaseTreeRevision(Microsoft.UI.Xaml.DependencyObject root)
        {
            return default(Windows.Foundation.String);
        }

        [Version(3)]
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public static void SetBaseTreeRevision(Microsoft.UI.Xaml.DependencyObject root, Windows.Foundation.String baseTreeRevision)
        {
        }
    }

    [Platform(typeof(Microsoft.UI.Xaml.WinUIContract), Microsoft.UI.Xaml.WinUIContract.WinAppSDK_2_0)]
    [TypeTable(IsExcludedFromNewTypeTable = true)]
    [DXamlIdlGroup("coretypes2")]
    public interface IXamlCondition
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        Windows.Foundation.Boolean Evaluate(Windows.Foundation.String argument);
    }

    [DXamlIdlGroup("coretypes2")]
    internal interface IXamlPredicate
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        Windows.Foundation.Boolean Evaluate(Windows.Foundation.Collections.IVectorView<string> arguments);
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(partial: true)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [Guids(ClassGuid = "43622c65-6db6-42bc-be67-2c34a6df463b")]
    [NativeName("CIsApiContractPresentPredicate")]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlPredicate))]
    internal sealed class IsApiContractPresent
        : Microsoft.UI.Xaml.DependencyObject
    {
        public IsApiContractPresent() { }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(partial: true)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [Guids(ClassGuid = "ca2964a2-d3da-4aed-b266-f937028a3d63")]
    [NativeName("CIsApiContractNotPresentPredicate")]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlPredicate))]
    internal sealed class IsApiContractNotPresent
        : Microsoft.UI.Xaml.DependencyObject
    {
        public IsApiContractNotPresent() { }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(partial: true)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [Guids(ClassGuid = "6328c17a-af24-4f3f-b99e-6813bd0b14ee")]
    [NativeName("CIsPropertyPresentPredicate")]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlPredicate))]
    internal sealed class IsPropertyPresent
        : Microsoft.UI.Xaml.DependencyObject
    {
        public IsPropertyPresent() { }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(partial: true)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [Guids(ClassGuid = "7405ce51-1c13-45a8-b7b4-3859c1dd4749")]
    [NativeName("CIsPropertyNotPresentPredicate")]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlPredicate))]
    internal sealed class IsPropertyNotPresent
        : Microsoft.UI.Xaml.DependencyObject
    {
        public IsPropertyNotPresent() { }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(partial: true)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [Guids(ClassGuid = "01da2943-8f8d-42ca-96ef-79cbc4d083ab")]
    [NativeName("CIsTypePresentPredicate")]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlPredicate))]
    internal sealed class IsTypePresent
        : Microsoft.UI.Xaml.DependencyObject
    {
        public IsTypePresent() { }
    }

    [DXamlIdlGroup("coretypes2")]
    [CodeGen(partial: true)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [Guids(ClassGuid = "e8c566d6-0e2e-414d-81a8-eb1be51a045e")]
    [NativeName("CIsTypeNotPresentPredicate")]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlPredicate))]
    internal sealed class IsTypeNotPresent
        : Microsoft.UI.Xaml.DependencyObject
    {
        public IsTypeNotPresent() { }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [IdlAttributeTarget(AttributeTargets.Class)]
    [DXamlIdlGroup("coretypes2")]
    public class MarkupExtensionReturnTypeAttribute : Attribute
    {
        public Type ReturnType { get; set; }
    }

    [CodeGen(CodeGenLevel.IdlAndPartialStub)]
    [ClassFlags(IsMarkupExtension = true)]
    [TypeTable(IsExcludedFromCore = true, ForceInclude = true)]
    [Guids(ClassGuid = "26e480bc-a928-43c6-9077-673514724e8d")]
    [DXamlIdlGroup("coretypes2")]
    public class MarkupExtension
        : Windows.Foundation.Object
    {
        public MarkupExtension() { }

        protected virtual Windows.Foundation.Object ProvideValue()
        {
            return default(Windows.Foundation.Object);
        }

        [DXamlName("ProvideValueWithIXamlServiceProvider")]
        [DXamlOverloadName("ProvideValue")]
        protected virtual Windows.Foundation.Object ProvideValue(IXamlServiceProvider serviceProvider)
        {
            return default(Windows.Foundation.Object);
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    [IdlAttributeTarget(AttributeTargets.Class)]
    [DXamlIdlGroup("coretypes2")]
    public class FullXamlMetadataProviderAttribute : Attribute
    {
    }

    [TypeTable(ForceInclude = true)]
    [DXamlIdlGroup("coretypes2")]
    public interface IProvideValueTarget
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        Windows.Foundation.Object TargetObject
        {
            get;
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        Windows.Foundation.Object TargetProperty
        {
            get;
        }
    }

    [TypeTable(ForceInclude = true)]
    [DXamlIdlGroup("coretypes2")]
    public interface IXamlTypeResolver
    {
        Windows.UI.Xaml.Interop.TypeName Resolve(Windows.Foundation.String qualifiedTypeName);
    }

    [TypeTable(ForceInclude = true)]
    [DXamlIdlGroup("coretypes2")]
    public interface IUriContext
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        Windows.Foundation.Uri BaseUri
        {
            get;
        }
    }

    [TypeTable(ForceInclude = true)]
    [DXamlIdlGroup("coretypes2")]
    public interface IRootObjectProvider
    {
        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        Windows.Foundation.Object RootObject
        {
            get;
        }
    }

    [CodeGen(partial: true, Level = CodeGenLevel.IdlAndPartialStub)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [TypeTable(IsXbfType = false, IsExcludedFromCore = true)]
    [Guids(ClassGuid = "13337e47-68e0-404f-848a-3b5cf375242b")]
    [DXamlIdlGroup("coretypes2")]
    public sealed class ProvideValueTargetProperty
        : Windows.Foundation.Object
    {
        public ProvideValueTargetProperty() { }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public string Name
        {
            get;
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public Windows.UI.Xaml.Interop.TypeName Type
        {
            get;
        }

        [CodeGen(CodeGenLevel.IdlAndPartialStub)]
        public Windows.UI.Xaml.Interop.TypeName DeclaringType
        {
            get;
        }
    }

    [CodeGen(partial: true, Level = CodeGenLevel.IdlAndPartialStub)]
    [TypeFlags(IsCreateableFromXAML = false)]
    [TypeTable(IsXbfType = false, IsExcludedFromCore = true)]
    [Guids(ClassGuid = "4958850a-79a7-4dfc-86a5-46709302212b")]
    [Implements(typeof(Microsoft.UI.Xaml.IXamlServiceProvider))]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IProvideValueTarget))]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IXamlTypeResolver))]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IUriContext))]
    [Implements(typeof(Microsoft.UI.Xaml.Markup.IRootObjectProvider))]
    internal sealed class ParserServiceProvider
        : Windows.Foundation.Object
    {
        public ParserServiceProvider() { }
    }
}
