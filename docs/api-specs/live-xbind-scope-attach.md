# Live `{x:Bind}` scope attach

Status: prototype / design note. Not a shipping API, not a product doc.

Scope of this note: introducing or replacing a compiled `{x:Bind}` scope on a **root object that is
already constructed and already loaded**. Specifically the first-binding case, where the page was
inflated from an XBF that contained no bindings at all:

```xml
<ListView x:Name="TodoList" />
→
<ListView x:Name="TodoList" ItemsSource="{x:Bind ViewModel.Items, Mode=OneWay}" />
```

Out of scope here, and owned elsewhere: stable connection ids, XBF versioning and invalidation,
template re-expansion, and any transaction API. `DataTemplate` scopes are inspected but not solved;
see the last section.

---

## 1. What actually happens today

### 1.1 Cold parse, root scope

Everything is driven from one function.

```
CApplication::LoadComponent
  dxaml\xcp\core\packaging\application.cpp:363
    ├─ resolves the XBF / node-stream for the URI (cache at :396-411)
    ├─ pComponent->SetBaseUri(pUri)                        :425
    │     guard released on success at :453, so the root keeps BaseUri
    └─ CCoreServices::ParseXamlWithExistingFrameworkRoot   :452
          └─ BinaryFormatObjectWriter::WriteNode
                dxaml\xcp\core\Parser\binaryformatobjectwriter.cpp:518
                  case ObjectWriterNodeType::SetConnectionId
                    └─ SetConnectionIdOnCurrentInstance    :1403
```

`SetConnectionIdOnCurrentInstance` is the whole contract, and it is short
(`binaryformatobjectwriter.cpp:1403-1467`):

```
for every x:ConnectionId node:

  1. m_spRuntime->SetConnectionId(m_qoEventRoot, id, currentInstance)          :1410
        → XamlManagedRuntime::SetConnectionId
            dxaml\xcp\core\Parser\xamlmanagedruntime.cpp:384
        → FxCallbacks::XamlManagedRuntimeRPInvokes_SetConnectionId             :419
        → XamlParserCallbacks::...SetConnectionId
            dxaml\xcp\dxaml\lib\XamlParserCallbacks.cpp:434
        → IComponentConnector::Connect(id, target)     on the PAGE
              (event wiring; x:Bind does not use this one)

  2. if currentInstance IS the root object, and no scope has been made yet:    :1416-1418
       m_spRuntime->GetXBindConnector(parentConnector, id, root, out)          :1426
       else m_spRuntime->GetXBindConnector(m_qoEventRoot, id, root, out)       :1437
        → XamlManagedRuntime::GetXBindConnector
            xamlmanagedruntime.cpp:424
        → FxCallbacks::XamlManagedRuntimeRPInvokes_GetXBindConnector           :448
        → XamlParserCallbacks::...GetXBindConnector
            XamlParserCallbacks.cpp:440-465
        → IComponentConnector::GetBindingConnector(id, root)   on the PAGE
              generated code creates the scope, sets its data root,
              stores it in the page's Bindings field, and subscribes to
              FrameworkElement.Loading
       result cached in m_qoXBindConnector                                     :1448

  3. m_spRuntime->SetConnectionId(m_qoXBindConnector, id, currentInstance)     :1458
        → IComponentConnector::Connect(id, target)     on the SCOPE
              this is what populates the generated scope's element fields
```

So the ordering, for a page whose root connection id is 1:

| step | call | who implements it |
|---|---|---|
| 1 | `page.Connect(1, page)` | compiler-generated on the page |
| 2 | `page.GetBindingConnector(1, page)` → scope | compiler-generated on the page |
| 3 | `scope.Connect(1, page)` | compiler-generated scope |
| 4 | `page.Connect(n, elem_n)` then `scope.Connect(n, elem_n)`, ascending n | generated |
| 5 | *(no runtime call)* the `Loading` handler the scope subscribed in step 2 fires, and the scope performs its first `Update` | generated |
| 6 | *(no runtime call)* `StopTracking` is generated-side only, reachable by the app through the page's `Bindings` field | generated |

Steps 5 and 6 have **no runtime participation at all**. That is the crux.

### 1.2 What survives the parse

Nothing that a live attach could use.

* `m_qoXBindConnector` and `m_qoParentXBindConnector` are members of the object writer
  (`dxaml\xcp\core\inc\BinaryFormatObjectWriter.h:177-178`) and die with it. They are seeded from
  `ObjectWriterSettings`, which is a local of the caller.
* There is **no** `connectionId → object` map anywhere. Verified by reading every consumer of
  `xdConnectionId` / `SetConnectionId` / `GetXBindConnector` in `dxaml\xcp\core\Parser\`.
* The root does **not** store the produced scope. The only thing holding it is the
  compiler-generated `Bindings` field on the page.

What does survive:

* **The namescope.** `CCoreServices::TryGetElementByName(name, referenceObject)`
  (`dxaml\xcp\core\inc\corep.h:699`), reached publicly through `FrameworkElement.FindName`
  (`dxaml\xcp\dxaml\lib\FrameworkElement_Partial.cpp:40-56`), and
  `CDependencyObject::GetStandardNameScopeOwner()` (`dxaml\xcp\core\inc\CDependencyObject.h:1049`).
  This is the only durable identity a live attach can lean on, and it exists from parse time, so it
  works for a root that was constructed but never realized.
* **`BaseUri` on the root**, stamped by `CApplication::LoadComponent` and left in place on success.

### 1.3 The asymmetry that shapes the design

Template scopes already have persistent runtime ownership. `CControlTemplate::CreateXBindConnector`
(`dxaml\xcp\core\core\elements\Template.cpp:349-417`) starts by asking whether a scope is already
stored on the templated parent:

```cpp
// Template.cpp:363-374
if (m_connectionId != -1)
{
    // See if one is already stored on the templated parent
    if (!templatedParent->IsPropertyDefaultByIndex(KnownPropertyIndex::XamlBindingHelper_DataTemplateComponent))
    { ... UnboxObjectValue(&dataTemplateComponentValue, ..., &connector); }
    else if (parent connector) { connectorGetter->GetBindingConnector(m_connectionId, templatedParentPeer, &connector); }
    ...
}
```

That slot is the public attached property `XamlBindingHelper.DataTemplateComponent`
(`microsoft.ui.xaml.coretypes2.idl:2227-2229`). Per-container scopes are owned by the runtime and can
be looked back up. **The root has no equivalent.** The proposal is to add exactly that, plus a
post-parse replay of steps 3-5 above.

---

## 2. Proposed contract

### 2.1 IDL

```idl
namespace Microsoft.UI.Xaml.Markup
{
    // Declared by the object that produces a scope.
    interface IXamlBindScopeManifest
    {
        Int32   RootConnectionId { get; };
        Int32[] GetRequiredConnectionIds();
        String  ScopeRevision { get; };
        String  ExpectedBaseTreeRevision { get; };
    };

    // Implemented by the produced scope.
    interface IXamlBindScopeLifecycle
    {
        void InitializeScope();
        void DetachScope();
    };

    enum XamlBindScopeAttachStatus
    { Attached, Replaced, Detached, AlreadyAttached, Refused, Desynchronized };

    enum XamlBindScopeFailureDetail
    {
        None,
        BaseTreeRevisionUnavailable, BaseTreeRevisionMismatch,
        ScopeRevisionAlreadyApplied, ScopeRevisionConflict, ScopeNotProduced,
        ManifestShapeInvalid, ManifestDuplicateConnectionId, ManifestMissingRootRow,
        TargetNotResolvable, TargetIdentityMismatch, TargetTypeMismatch, TargetOutsideNamescope,
        NothingAttached,
        DesyncScopeState, DesyncBaseTree,
        ScopeLifecycleUnsupported, ScopeManifestUnavailable, ManifestIncomplete
    };

    struct XamlBindScopeAttachResult
    {
        XamlBindScopeAttachStatus  Status;
        XamlBindScopeFailureDetail FailureDetail;
        String ObservedBaseTreeRevision;
        String RequestedScopeRevision;
        String AppliedScopeRevision;
        UInt64 OwnedScopeInstanceId;
        Int32  TargetsConnected;
        Int32  TargetsDetached;
        Int32  FailedConnectionId;
    };

    runtimeclass XamlBindingHelper
    {
        static XamlBindScopeAttachResult TryAttachBindingScope(
            DependencyObject root, IComponentConnector connector,
            Int32[] targetConnectionIds, String[] targetStableNames,
            String[] targetTypeNames, Object[] targetObjects);

        static XamlBindScopeAttachResult ReplaceBindingScope( /* same */ );
        static XamlBindScopeAttachResult DetachBindingScope(DependencyObject root);

        static IComponentConnector GetAttachedBindingScope(DependencyObject root);
        static String GetAttachedScopeRevision(DependencyObject root);
        static String GetBaseTreeRevision(DependencyObject root);
        static void   SetBaseTreeRevision(DependencyObject root, String baseTreeRevision);
    };
}
```

### 2.2 Why each argument exists

| argument | why it cannot be dropped |
|---|---|
| `root` | The operation is per-root. It is also the namescope reference object for resolving every named row. |
| `connector` | The runtime cannot construct the scope; only generated code can. This is the same object the parser would have called `GetBindingConnector` on. |
| `targetConnectionIds` | **Not reconstructible.** No `connectionId → object` map survives the parse (§1.2). Ids are compiler-assigned and opaque to the runtime. |
| `targetStableNames` | The only durable identity in the runtime is the namescope. A name lets the runtime resolve the target *itself* and compare instances, instead of trusting the caller. This is what makes a wrong same-typed target detectable. |
| `targetTypeNames` | Secondary guard, applied only after identity. Catches a manifest authored against a different document that happens to use the same names. Never sufficient on its own: a cast succeeding proves nothing. |
| `targetObjects` | Required for rows with no `x:Name`, which the compiler emits routinely. Also cross-checked against the name resolution when both are present. |

Everything the generated side already knows — root connection id, required ids, scope revision, base
tree revision — comes from `IXamlBindScopeManifest` on the connector rather than being restated by the
caller. That removes a class of caller error and is what lets the runtime refuse an incomplete map.

### 2.3 Why `IXamlBindScopeLifecycle` is necessary

`IComponentConnector` has exactly two methods and neither can do these jobs:

* **First update.** In a cold parse the scope subscribes to `FrameworkElement.Loading` inside
  `GetBindingConnector`, and that callback runs the first `Update`. A live root already raised
  `Loading` and will not raise it again, so `Connect` alone leaves the scope populated and inert.
* **Stopping.** Nothing can tell an outgoing scope it has been replaced, so it keeps its
  `PropertyChanged` / `DataContextChanged` / `CollectionChanged` / `Loading` registrations and keeps
  writing its targets.

Generated `*_Bindings` classes already have `Initialize()` and `StopTracking()`; implementing this
interface is a two-line adapter. A scope that does not implement it is **refused**
(`ScopeLifecycleUnsupported`) rather than attached, because attaching it could only produce an inert
scope and an unstoppable writer — the "S_OK with no effect" failure mode.

### 2.4 Two revision dimensions, never compared to each other

The feature deliberately pairs an **old base tree** with a **new scope**, so requiring the two to
match would refuse the feature itself.

* `baseTreeRevision` — identity of the XBF/object graph that constructed the live root. Producer
  chooses the content; expected to be a hash over XBF content, source checksum, compiler/schema
  version and the connection map. Source checksum alone is insufficient: the same source bytes can be
  assigned different connection ids after a ledger reset. Without the connection-map component the
  check is only partial, and that is a property of the producer, not of this contract.
* `scopeRevision` — identity of the generated scope and its binding manifest. Recorded on the root so
  repeat attaches are idempotent-or-refused.

**Fault precedence is fixed and base dominates.** If the base tree disagrees, every connection id in
the manifest describes a different object graph, so nothing can be trusted: refuse first, connect
zero targets, never return `Attached`. Remedy is re-inflate. A scope fault with an agreeing base tree
is remedied by `ReplaceBindingScope`, in the same process, on the same instance. The check is **fail
closed**: a connector that asserts no base tree revision, or a root that carries none, is refused with
`BaseTreeRevisionUnavailable`.

### 2.5 Algorithm

Preflight is total. Nothing mutates until every check has passed.

```
PREFLIGHT (no mutation)
  0. connector must implement IXamlBindScopeManifest      → ScopeManifestUnavailable
  1. base tree: expected present, observed present, equal → BaseTreeRevisionUnavailable / Mismatch
  2. scope state: same revision → AlreadyAttached ; different revision and not replacing
                                                          → ScopeRevisionConflict
  3. manifest shape: array lengths agree, no negative or duplicate ids
  3b. completeness: every connector-declared required id has a row → ManifestIncomplete
  4. per row:
       named   → resolve through root's namescope; supplied object must be the SAME INSTANCE
                 → TargetNotResolvable / TargetIdentityMismatch
                 (membership already proven; no further reachability check)
       unnamed → object required; GetStandardNameScopeOwner(target) must be root
                 → TargetNotResolvable / TargetOutsideNamescope
       then, only after identity: runtime class name must equal expected → TargetTypeMismatch
  5. exactly one row for the root connection id, and its target must be the root
                                                          → ManifestMissingRootRow
  6. sort rows ascending by connection id (parse order)

MUTATION
  7. scope = connector.GetBindingConnector(rootConnectionId, root)
        null → ScopeNotProduced, still no tree mutation
  8. scope must implement IXamlBindScopeLifecycle          → ScopeLifecycleUnsupported
        (both 7 and 8 are ordered BEFORE detaching the outgoing scope, so a connector that
         cannot produce a usable scope is a clean refusal, not a page that lost its bindings)
  9. if replacing: outgoing.DetachScope(); drop ownership record
 10. for each row ascending: scope.Connect(id, target)
        failure at row k → record desynchronized, return Desynchronized/DesyncScopeState,
        FailedConnectionId = k. There is no sound rollback: earlier targets already carry
        values written by the new scope.
 11. scope.InitializeScope()      // stands in for the Loading callback
 12. publish ownership record; return Attached or Replaced
```

### 2.6 Ownership slot

The prototype uses a **side table keyed by the root's `CDependencyObject`**, holding the scope, both
revisions, the connected-target count, a monotonic ownership instance id, and a desync flag.

Recommendation for production is the **attached DependencyProperty**, mirroring
`DataTemplateComponent`: it makes the root symmetric with the templated parent, gets lifetime and
per-object storage for free, and is observable through existing DP APIs. The prototype does not use it
because a new attached DP requires new `KnownPropertyIndex`, type-table and stable-XBF-index entries,
spreading the change across generated files and making it much harder to review or revert. The record
also needs more than a connector pointer, so the productized DP value should be a small record object
rather than the bare `IComponentConnector`.

---

## 3. Evidence

Fixture: `tools\LiveBindScopeAttachProbe`. **App-level simulation** on a stock, unmodified shipping
`Microsoft.UI.Xaml.dll`; it proves the API semantics and that an externally generated connector in a
side assembly can drive a live page like a cold-built scope. It does not prove a public runtime hook
exists.

Run: 20 cases, 20 passed, 196 ms, single process, current page instance never rebuilt. Full transcript
in `tools\LiveBindScopeAttachProbe\probe-results.txt`.

### Positive, against a cold-built `{x:Bind}` oracle in the same window

| case | assertion | observed |
|---|---|---|
| T01a | attach reported truthfully | `Attached/None`, 4 connected, ownership id 1, both revisions echoed |
| T01b | initial population matches oracle | subject `title-0` / `subtitle-0` equal to oracle; `ItemsSource` reference-identical to `ViewModel.Items` |
| T02 | `ObservableCollection` mutation | oracle 3 items, subject 3 items |
| T03 | property change | both `title-1` |
| T04 | whole ViewModel replacement | both `title-2`, `ItemsSource` follows the new VM |
| T04b | old ViewModel released | subject follows the new VM only |
| T05 | path replacement | `Replaced`, 4 detached, new ownership id, title now shows `Subtitle`, alt shows `Title`, old path silent |
| T06 | detach | `Detached`, ownership null, target frozen at last value across further VM changes |
| T06b | detach idempotence | second detach `Refused/NothingAttached`, not a success |
| T07 | repeat attach | `AlreadyAttached/ScopeRevisionAlreadyApplied`, same ownership id, same scope object, 0 connects, **exactly 1 write per logical change** |
| T07b | scope conflict | `Refused/ScopeRevisionConflict`, both revisions reported |
| T07c | precedence | base + scope both stale → `BaseTreeRevisionMismatch`, not `ScopeRevisionConflict` |
| T08a | same instance, same process | same object, still parented |
| T08b | cached, never-parented instance | `Attached`, 4 connected, same instance, updates flow off-tree |

### Negative controls

| case | control | observed |
|---|---|---|
| N01 | omit the `ListView` row | `Refused/ManifestIncomplete`, `FailedConnectionId=4`, **0 Connect calls** |
| N02 | swap two adjacent same-typed `TextBlock`s | `Refused/TargetIdentityMismatch`, `FailedConnectionId=2`, 0 Connect calls, while the probe simultaneously records `castsSucceed=True` |
| N03 | skip detach, force a second scope | **4 writes to one target for one logical change** — the duplicate-writer test goes red, proving detach is load-bearing |
| N04 | stale base tree revision | `Refused/BaseTreeRevisionMismatch`, 0 connects, no ownership taken |
| N05 | scope with no lifecycle interface | `Refused/ScopeLifecycleUnsupported`, no ownership — an `S_OK`-shaped no-effect is impossible |
| N06 | target belonging to a different live instance of the same page type | `Refused/TargetOutsideNamescope`, 0 connects |

### A design correction the fixture forced

Run 1 was 19/20. T08b, the cached never-parented instance, was refused `TargetOutsideNamescope`,
because an app-level reachability check must walk the visual tree and an unrealized tree has no visual
parents. `GetStandardNameScopeOwner()` does not have that problem: it walks the namescope owner chain,
which exists from parse time. Both implementations were corrected so reachability is applied **only to
unnamed rows** — a row resolved through the root's namescope has already proven membership. Run 2 is
20/20.

This is direct evidence that the cached-instance case is not soundly implementable above the runtime
boundary.

---

## 4. How this composes

* **Versioned XBF, fresh pages.** Out of scope here. Once the XBF for the document carries the new
  root `x:ConnectionId=1`, a fresh inflation runs the normal connector path in §1.1 with no
  involvement from this API. This API exists only for roots that already exist.
* **Versioned XBF, `baseTreeRevision`.** `CApplication::LoadComponent` already leaves `BaseUri` on the
  root (`application.cpp:425`, guard released `:453`), and the node-stream cache is keyed by canonical
  URI (`:396-397`). That is the natural place for the loader to stamp `baseTreeRevision`, which is why
  the prototype exposes `SetBaseTreeRevision` — the XBF lane owns writing it, this lane owns reading
  and enforcing it.
* **Stable-id compiler manifest.** `targetConnectionIds` + `targetStableNames` + `targetTypeNames` are
  exactly a stable-id manifest projection. When the compiler emits a manifest with scope-qualified
  stable keys, the tool passes it straight through, and `IXamlBindScopeManifest.GetRequiredConnectionIds`
  is the compiler's own completeness statement.
* **Publication interlock stays separate and strict.** Pairing an XBF with its normal generated
  `Connect` still requires XBF embedded source checksum == generated pragma checksum. Live attach does
  not weaken that; it validates rows against `baseTreeRevision` and records `scopeRevision`.

---

## 5. `DataTemplate` boundary — inspected, not solved

How template scopes work today:

* One scope per realized container. `CControlTemplate::LoadContent`
  (`Template.cpp:254-270`) calls `CreateXBindConnector(templatedParent)` before expanding, then
  `ConnectTemplate(xBindConnector)` after, which calls `Connect(m_connectionId, templatePeer)`.
* The scope is looked back up from the attached DP on the templated parent
  (`Template.cpp:363-374`), and the connector is held as a weak reference
  (`CFrameworkTemplate::m_spParentXBindConnector`, `dxaml\xcp\core\inc\Template.h:75`).
* Phase processing and recycling go through `IDataTemplateComponent`:
  `ProcessBindings(item, itemIndex, phase, out nextPhase)` and `Recycle()`, driven from
  `ListViewBase_Partial_ContainerPhase.cpp` with the phase loop owned by `BuildTreeService`.

**Could the same attach API serve an existing template tree?** Mechanically yes, for a single realized
container, *if* the external tool supplies every live target for that container and the produced scope
implements the lifecycle interface. `FindName` resolves inside a realized template namescope, so
identity validation works there too.

**What is still missing, and is not solved here:**

1. **Enumeration.** There is no runtime API to enumerate realized containers for a given
   `ItemsControl`/`ItemsRepeater`, so a tool cannot discover the set of roots to attach to.
2. **Recycle pool.** Containers in the recycle pool are neither realized nor discoverable, and will be
   re-realized from the old `DataTemplate` content, producing containers with no new scope. Attaching
   to live containers alone leaves the pool inconsistent.
3. **Phase processing.** `ProcessBindings` is the template equivalent of `InitializeScope`, and the
   phase loop is owned by `BuildTreeService`. A live-attached template scope would have to be joined to
   that loop, and the correct `nextPhase` for a partially processed container is unspecified.
4. **`Recycle` ownership.** The runtime calls `Recycle()` on the scope it found in the attached DP.
   Replacing that scope mid-flight has to interact with `Recycle` semantics, which the root case has no
   analogue for.
5. **Old object graph.** Elements added or removed by the new template cannot be produced by attach at
   all; that is template re-expansion, explicitly out of scope.

Conclusion: the attach API generalizes to a *supplied* existing template tree, but enumeration,
re-realization and recycle-pool control remain unsolved and are the actual blockers.

---

## 6. Recommendation

**Proceed, narrowed to the root/page case.**

The contract is small, it is entirely additive and opt-in, normal behaviour is untouched, and the run
shows the semantics are sufficient to make a live page indistinguishable from a cold-built one across
initial population, collection mutation, property and view-model replacement, path replacement, detach
and repeat attach — with every negative control refusing before mutation.

Two things must land with it, not after it:

* `IXamlBindScopeLifecycle`, or the API can only produce inert scopes and unstoppable writers;
* connector-declared required ids, or an omitted row silently under-connects.

Do **not** extend to `DataTemplate` in this workstream. Section 5 lists five unsolved problems there,
and at least two of them (recycle pool, phase-loop participation) are runtime work of a different size.
