// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#pragma once

// Live compiled-binding ({x:Bind}) scope attach: the DirectUI adapter.
//
// All of the logic lives in the pure component at dxaml\xcp\components\bindscope, which has no XAML
// dependency and is unit tested in isolation. This file is only the seam between that component and
// the runtime: it implements BindScope::IScopeHost over the real primitives
// (CCoreServices::TryGetElementByName, CDependencyObject::GetStandardNameScopeOwner, the
// IComponentConnector ABI) and marshals HSTRING and array parameters at the ABI boundary.
//
// Ownership slot. The experimental implementation keeps the record inside the component, in a side
// table keyed by the root's CDependencyObject. That is the smallest source diff: it needs no new
// KnownPropertyIndex, no type table entry and no stable XBF index, so it does not touch generated
// files and is trivially revertable, and it forces every mutation through the validated API rather
// than a public DependencyProperty setter. The proposed production design is an attached
// DependencyProperty on the root, mirroring XamlBindingHelper.DataTemplateComponent, which is how
// template scopes are already owned (see CControlTemplate::CreateXBindConnector,
// xcp\core\core\elements\Template.cpp). That is a report-level recommendation, not what is
// implemented here.

#include "XamlBindScopeAttachCore.h"

namespace DirectUI
{
    class XamlBindScopeAttach
    {
    public:
        using Result = ABI::Microsoft::UI::Xaml::Markup::XamlBindScopeAttachResult;

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

        // Called when a root is destroyed so an ownership record cannot outlive the tree.
        static void OnRootDestroyed(_In_ CDependencyObject* root);
    };
}
