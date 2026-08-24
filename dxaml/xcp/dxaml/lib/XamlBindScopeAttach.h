// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// Live compiled-binding ({x:Bind}) scope attach.
//
// Background, with the exact call sites this mirrors:
//
//   During a parse, BinaryFormatObjectWriter::SetConnectionIdOnCurrentInstance
//   (core\Parser\binaryformatobjectwriter.cpp) sees every x:ConnectionId node. When the current
//   instance is the root and no scope exists yet it calls GetXBindConnector, which lands on
//   IComponentConnector::GetBindingConnector(id, root) in
//   XamlParserCallbacks::XamlManagedRuntimeRPInvokes_GetXBindConnector
//   (dxaml\lib\XamlParserCallbacks.cpp). The returned scope is cached in the object writer's
//   m_qoXBindConnector (core\inc\BinaryFormatObjectWriter.h) and every connection id, the root's
//   included, is then passed to IComponentConnector::Connect on that scope.
//
//   m_qoXBindConnector lives only for the duration of that parse. Nothing in the runtime retains a
//   connectionId -> object map, and no root-level slot holds the produced scope. Template scopes are
//   different: CControlTemplate::CreateXBindConnector (core\core\elements\Template.cpp) looks the
//   scope back up from KnownPropertyIndex::XamlBindingHelper_DataTemplateComponent on the templated
//   parent, so per-container scopes do have persistent runtime ownership. The root simply has no
//   equivalent.
//
//   This file adds that missing root-level ownership record plus a post-parse replay of the connect
//   sequence, so a scope can be introduced into a tree that is already constructed and already
//   loaded. It is additive and opt-in: no existing parse path calls into it.
//
// Ownership slot choice. Two options were considered:
//   (a) an attached DependencyProperty on the root, exactly mirroring DataTemplateComponent;
//   (b) a side table keyed by the root's CDependencyObject.
// This prototype uses (b) because (a) requires new KnownPropertyIndex, type table and stable XBF
// index entries, which would spread the change across generated files and make it much harder to
// review or revert. (a) is the better production shape: it gets lifetime, observability and
// per-object storage for free, and it makes the root symmetric with the templated parent.

#include <unordered_map>

namespace DirectUI
{
    // One ownership record per live root that currently owns an externally attached scope.
    struct XamlBindScopeRecord
    {
        ctl::ComPtr<xaml_markup::IComponentConnector> Scope;
        xstring_ptr ScopeRevision;
        xstring_ptr BaseTreeRevision;
        UINT64 InstanceId = 0;
        INT32 TargetCount = 0;
        bool Desynchronized = false;
    };

    // Per-thread state. Lives on DXamlCore so it dies with the UI thread it belongs to.
    class XamlBindScopeAttach
    {
    public:
        // Result and status shapes come from the generated projection of
        // Microsoft.UI.Xaml.Markup.XamlBindScopeAttachResult.
        using Result = ABI::Microsoft::UI::Xaml::Markup::XamlBindScopeAttachResult;
        using Status = ABI::Microsoft::UI::Xaml::Markup::XamlBindScopeAttachStatus;
        using Detail = ABI::Microsoft::UI::Xaml::Markup::XamlBindScopeFailureDetail;

        static _Check_return_ HRESULT Attach(
            _In_ xaml::IDependencyObject* root,
            _In_ xaml_markup::IComponentConnector* connector,
            UINT32 idCount, _In_reads_(idCount) INT32* ids,
            UINT32 nameCount, _In_reads_(nameCount) HSTRING* stableNames,
            UINT32 typeCount, _In_reads_(typeCount) HSTRING* typeNames,
            UINT32 objectCount, _In_reads_(objectCount) IInspectable** objects,
            bool allowReplace,
            _Out_ Result* result);

        static _Check_return_ HRESULT Detach(
            _In_ xaml::IDependencyObject* root,
            _Out_ Result* result);

        static _Check_return_ HRESULT GetAttachedScope(
            _In_ xaml::IDependencyObject* root,
            _Outptr_result_maybenull_ xaml_markup::IComponentConnector** scope);

        static _Check_return_ HRESULT GetAttachedScopeRevision(_In_ xaml::IDependencyObject* root, _Out_ HSTRING* revision);

        static _Check_return_ HRESULT GetBaseTreeRevision(_In_ xaml::IDependencyObject* root, _Out_ HSTRING* revision);
        static _Check_return_ HRESULT SetBaseTreeRevision(_In_ xaml::IDependencyObject* root, _In_ HSTRING revision);

        // Called when a root is destroyed so the record does not outlive the tree.
        static void OnRootDestroyed(_In_ CDependencyObject* root);

    private:
        static std::unordered_map<CDependencyObject*, XamlBindScopeRecord>& Records();
        static std::unordered_map<CDependencyObject*, xstring_ptr>& BaseTreeRevisions();
        static UINT64 NextInstanceId();
    };
}
