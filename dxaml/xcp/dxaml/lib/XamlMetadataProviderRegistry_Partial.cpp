// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Microsoft.UI.Xaml.Markup.XamlMetadataProviderRegistry: register and unregister IXamlMetadataProviders while the app
// runs. The registry and its rules live in MetadataAPI (core\inc\XamlMetadataProviderRegistry.h); this is the public
// surface. Spec: docs\api-specs\XamlMetadataProviderRegistry\XamlMetadataProviderRegistry.md.

#include "precomp.h"
#include "XamlMetadataProviderRegistry.g.h"
#include <MetadataAPI.h>

using namespace DirectUI;

_Check_return_ HRESULT
XamlMetadataProviderRegistryFactory::RegisterImpl(_In_ xaml_markup::IXamlMetadataProvider* pProvider)
{
    std::wstring message;
    const HRESULT hr = DescribeRegistrationResult(MetadataAPI::RegisterSideMetadataProvider(pProvider), message);
    if (FAILED(hr))
    {
        IFC_RETURN(ErrorHelper::OriginateError(hr, static_cast<XUINT32>(message.size()), message.c_str()));
        IFC_RETURN(hr);
    }

    return S_OK;
}

_Check_return_ HRESULT
XamlMetadataProviderRegistryFactory::UnregisterImpl(_In_ xaml_markup::IXamlMetadataProvider* pProvider, _Out_ BOOLEAN* pResult)
{
    const auto result = MetadataAPI::UnregisterSideMetadataProvider(pProvider);
    *pResult = result.Status == XamlMetadataProviderRegistrationStatus::Succeeded;
    return S_OK;
}

_Check_return_ HRESULT
XamlMetadataProviderRegistryFactory::IsRegisteredImpl(_In_ xaml_markup::IXamlMetadataProvider* pProvider, _Out_ BOOLEAN* pResult)
{
    *pResult = MetadataAPI::IsSideMetadataProviderRegistered(pProvider);
    return S_OK;
}
