// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#include "precomp.h"
#include <XamlLogging.h>

#include <StringUtilities.h>
#include <XStringUtilities.h>

#include <TypeTableStructs.h>
#include <CustomClassInfo.h>
#include <MetadataAPI.h>
#include <MockDependencyProperty.h>
#include <MockDynamicMetadataStorage.h>
#include <MockXamlMetadataProvider.h>
#include <MockSideXamlMetadataProvider.h>
#include <MockClassInfo.h>
#include <ThreadLocalStorage.h>
#include <CStaticLock.h>

#include "MetadataUnitTests.h"
#include "CustomXamlProviders.h"
#include <XamlTypeCacheGenerations.h>

using namespace DirectUI;
using namespace xaml_interop;
using namespace xaml_markup;

namespace Microsoft { namespace UI { namespace Xaml { namespace Tests { namespace Metadata {

    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameDependencyObject, L"DependencyObject");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameControl, L"Control");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameDependencyObject, L"Microsoft.UI.Xaml.DependencyObject");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameControl, L"Microsoft.UI.Xaml.Controls.Control");

    DECLARE_CONST_STRING_IN_TEST_CODE(c_NamePoint, L"Point");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameRect, L"Rect");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_NameSize, L"Size");

    DECLARE_CONST_STRING_IN_TEST_CODE(c_Name, L"Name");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_Width, L"Width");

    bool MetadataUnitTests::ClassSetup()
    {
        THROW_IF_FAILED(StaticLockGlobalInit());
        return true;
    }

    bool MetadataUnitTests::ClassCleanup()
    {
        StaticLockGlobalDeinit();
        return true;
    }

    // Verify the common storage access (DynamicMetadataStorageInstanceWithLock) locks and unlocks automatically.
    void MetadataUnitTests::StorageAccessIsThreadSafe()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        // Verify member access via DynamicMetadataStorageInstanceWithLock
        {
            DynamicMetadataStorageInstanceWithLock storage;
            VERIFY_IS_TRUE(CStaticLock::IsLocked());
            auto customPropertiesCache = storage->m_customPropertiesCache;
            VERIFY_IS_TRUE(mock->LastAccessWasLocked, L"DynamicMetadataStorage accessed with a lock");
        }

        // Verify state after DynamicMetadataStorageInstanceWithLock destruction
        VERIFY_IS_FALSE(CStaticLock::IsLocked());

        // Verify access on Reset
        mock->LastAccessWasLocked = false;
        DynamicMetadataStorageInstanceWithLock::Reset();
        VERIFY_IS_TRUE(mock->LastAccessWasLocked, L"DynamicMetadataStorage accessed with a lock");
    }

    void MetadataUnitTests::CanResolveCustomTypeByTypeName()
    {
        wrl_wrappers::HStringReference customTypeStringRef(L"CustomNamespace.CustomType");

        auto customType = MockXamlType::CreateMetadata(customTypeStringRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider provider;
        provider.GetXamlTypeByFullNameCallback = [&customTypeStringRef, customType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
        {
            if (fullName == customTypeStringRef)
            {
                return customType.CopyTo(ppXamlType);
            }

            *ppXamlType = nullptr;
            return S_OK;
        };

        wxaml_interop::TypeName customTypeName = { customTypeStringRef.Get(), wxaml_interop::TypeKind_Metadata };
        const CClassInfo* resolvedType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByTypeName(customTypeName, &resolvedType));
        VERIFY_IS_NOT_NULL(resolvedType);
        VERIFY_ARE_EQUAL(customType.Get(), static_cast<const CCustomClassInfo*>(resolvedType)->GetXamlTypeNoRef());
    }

    void MetadataUnitTests::CanResolveDirectiveOnCustomType()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        CClassInfo customType;
        customType.m_nIndex = static_cast<KnownTypeIndex>(KnownTypeCount);
        customType.m_nBaseTypeIndex = KnownTypeIndex::Control;

        const CDependencyProperty* result;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetDependencyPropertyByName(&customType, c_Name, &result, /* allowDirectives */ true));
        VERIFY_ARE_EQUAL(KnownPropertyIndex::DependencyObject_Name, result->GetIndex());
    }

    void MetadataUnitTests::IsAssignableFrom()
    {
        // Positive tests.
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::FrameworkElement));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::Control));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Control)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::String));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::String)));

        // Negative tests.
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Control, KnownTypeIndex::DependencyObject));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Control), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::FrameworkElement, KnownTypeIndex::Application));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Application)));

        // If target type index is KnownTypeIndex::Object, anything can be assigned to it.
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Object, KnownTypeIndex::ICommand));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Object), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ICommand)));

        // enum has no type handle
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Stretch, KnownTypeIndex::Stretch));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::DependencyObject, KnownTypeIndex::Stretch));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Stretch, KnownTypeIndex::SolidColorBrush));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::SolidColorBrush)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::SolidColorBrush, KnownTypeIndex::Stretch));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::SolidColorBrush), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Enumerated, KnownTypeIndex::Stretch));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Enumerated), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch)));

        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Stretch, KnownTypeIndex::Enumerated));
        VERIFY_IS_FALSE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Stretch), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Enumerated)));

        // 32-bit type handle cusp
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::ListViewBase, KnownTypeIndex::ListView));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ListViewBase), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ListView)));

        // Internal hierarchy leaking to the outside world.  Yes, Duration, RepeatBehavior and KeyTime can be assigned to TimeSpan...
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::TimeSpan, KnownTypeIndex::Duration));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TimeSpan), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Duration)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::TimeSpan, KnownTypeIndex::RepeatBehavior));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TimeSpan), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::RepeatBehavior)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::Duration, KnownTypeIndex::RepeatBehavior));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Duration), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::RepeatBehavior)));

        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(KnownTypeIndex::TimeSpan, KnownTypeIndex::KeyTime));
        VERIFY_IS_TRUE(!!MetadataAPI::IsAssignableFrom(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TimeSpan), MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::KeyTime)));
    }

    void MetadataUnitTests::GetClassInfoByName()
    {
        const CClassInfo* pType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_NameDependencyObject, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::DependencyObject, pType->GetIndex());

        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_NameControl, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Control, pType->GetIndex());
    }

    void MetadataUnitTests::GetClassInfoByFullName()
    {
        const CClassInfo* pType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_FullNameDependencyObject, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::DependencyObject, pType->GetIndex());

        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByFullName(c_FullNameControl, &pType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Control, pType->GetIndex());
    }

    void MetadataUnitTests::GetTypeNameByClassInfo()
    {
        wxaml_interop::TypeName typeNameDO = {};
        VERIFY_SUCCEEDED(MetadataAPI::GetTypeNameByClassInfo(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject), &typeNameDO));
        VERIFY_ARE_EQUAL(wxaml_interop::TypeKind::TypeKind_Metadata, typeNameDO.Kind);
        VERIFY_ARE_STRINGS_EQUAL(L"Microsoft.UI.Xaml.DependencyObject", typeNameDO.Name);

        wxaml_interop::TypeName typeNameInt32 = {};
        VERIFY_SUCCEEDED(MetadataAPI::GetTypeNameByClassInfo(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Int32), &typeNameInt32));
        VERIFY_ARE_EQUAL(wxaml_interop::TypeKind::TypeKind_Primitive, typeNameInt32.Kind);
        VERIFY_ARE_STRINGS_EQUAL(L"Int32", typeNameInt32.Name);

        WindowsDeleteString(typeNameInt32.Name);
        WindowsDeleteString(typeNameDO.Name);
    }

    void MetadataUnitTests::IsConstructible()
    {
        VERIFY_IS_TRUE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)->IsConstructible());
        VERIFY_IS_FALSE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::EventHandlerStub)->IsConstructible());
    }

    void MetadataUnitTests::BaseTypes()
    {
        VERIFY_ARE_EQUAL(KnownTypeIndex::UnknownType, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Object)->GetBaseType()->GetIndex());
        VERIFY_ARE_EQUAL(KnownTypeIndex::Object, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)->GetBaseType()->GetIndex());
        VERIFY_ARE_EQUAL(KnownTypeIndex::UIElement, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement)->GetBaseType()->GetIndex());
        VERIFY_ARE_EQUAL(KnownTypeIndex::UnknownType, MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::IVectorOfPageStackEntry)->GetBaseType()->GetIndex());
    }

    void MetadataUnitTests::ValidateISupportInitializeFlagOnTypes()
    {
        VERIFY_IS_TRUE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Selector)->IsISupportInitialize());
        VERIFY_IS_TRUE(!!MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::GridView)->IsISupportInitialize());
    }

    void MetadataUnitTests::DependencyObjectIsInGoodState()
    {
        const CClassInfo* pType = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject);
        VERIFY_ARE_EQUAL(KnownPropertyIndex::UnknownType_UnknownProperty, pType->GetContentProperty()->GetIndex());
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Microsoft_UI_Xaml, pType->GetNamespace()->GetIndex());
        VERIFY_ARE_STRINGS_EQUAL(L"DependencyObject", pType->GetName());
        VERIFY_ARE_STRINGS_EQUAL(L"Microsoft.UI.Xaml.DependencyObject", pType->GetFullName());
        VERIFY_IS_FALSE(!!pType->HasTypeConverter());
        VERIFY_IS_FALSE(!!pType->IsBindable());
        VERIFY_IS_TRUE(!!pType->IsBuiltinType());
        VERIFY_IS_FALSE(!!pType->IsCollection());
        VERIFY_IS_TRUE(!!pType->IsConstructible());
        VERIFY_IS_FALSE(!!pType->IsDictionary());
        VERIFY_IS_FALSE(!!pType->IsInterface());
        VERIFY_IS_FALSE(!!pType->IsEnum());
        VERIFY_IS_FALSE(!!pType->IsISupportInitialize());
        VERIFY_IS_FALSE(!!pType->IsMarkupExtension());
        VERIFY_IS_TRUE(!!pType->IsNullable());
        VERIFY_IS_FALSE(!!pType->IsNumericType());
        VERIFY_IS_FALSE(!!pType->IsPrimitive());
        VERIFY_IS_FALSE(!!pType->IsValueType());
        VERIFY_IS_FALSE(!!pType->IsWhitespaceSignificant());
        VERIFY_IS_FALSE(!!pType->TrimSurroundingWhitespace());
    }

    void MetadataUnitTests::GetPrimitiveClassInfo()
    {
        const CClassInfo* pNormalizedType = nullptr;

        // DO should normalize to Object.
        const CClassInfo* pTypeDO = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject);
        VERIFY_SUCCEEDED(MetadataAPI::GetPrimitiveClassInfo(pTypeDO, &pNormalizedType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Object, pNormalizedType->GetIndex());

        // Int32 should normalize to Int32.
        const CClassInfo* pTypeInt32 = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Int32);
        VERIFY_SUCCEEDED(MetadataAPI::GetPrimitiveClassInfo(pTypeInt32, &pNormalizedType));
        VERIFY_ARE_EQUAL(KnownTypeIndex::Int32, pNormalizedType->GetIndex());
    }

    void MetadataUnitTests::CanExtractNamespaceNameAndShortName()
    {
        xephemeral_string_ptr namespaceName, typeName;

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName1, L"FooNamespace.FooType");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName1, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName2, L"FooNamespace.FooSubNamespace.FooType");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName2, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName3, L"FooNamespace.FooType<BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName3, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName4, L"FooNamespace.FooSubNamespace.FooType<BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName4, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName5, L"FooNamespace.FooType<BarNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName5, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName6, L"FooNamespace.FooSubNamespace.FooType<BarNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName6, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName7, L"FooNamespace.FooType<BarNamespace.BarSubNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName7, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarSubNamespace.BarType>"));

        DECLARE_CONST_STRING_IN_TEST_CODE(c_fullTypeName8, L"FooNamespace.FooSubNamespace.FooType<BarNamespace.BarSubNamespace.BarType>");
        VERIFY_SUCCEEDED(MetadataAPI::ExtractNamespaceNameAndShortName(c_fullTypeName8, &namespaceName, &typeName));
        VERIFY_IS_TRUE(!!namespaceName.Equals(L"FooNamespace.FooSubNamespace"));
        VERIFY_IS_TRUE(!!typeName.Equals(L"FooType<BarNamespace.BarSubNamespace.BarType>"));
    }

    void MetadataUnitTests::IXamlMemberTypeMayReturnNull()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        IXamlMemberTypeMayReturnNull_XamlMember member;
        const CDependencyProperty* pResult = nullptr;

        // Import a custom property.
        const CClassInfo* pType = MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject);
        VERIFY_ARE_EQUAL(S_OK, MetadataAPI::ImportPropertyInfo(pType, &member, &pResult));
    }

    void MetadataUnitTests::FoundationTypesInCorrectNamespace()
    {
        const CClassInfo* pPointType = MetadataAPI::GetBuiltinClassInfoByName(c_NamePoint);
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Windows_Foundation, pPointType->GetNamespace()->m_nIndex);

        const CClassInfo* pRectType = MetadataAPI::GetBuiltinClassInfoByName(c_NameRect);
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Windows_Foundation, pRectType->GetNamespace()->m_nIndex);

        const CClassInfo* pSizeType = MetadataAPI::GetBuiltinClassInfoByName(c_NameSize);
        VERIFY_ARE_EQUAL(KnownNamespaceIndex::Windows_Foundation, pSizeType->GetNamespace()->m_nIndex);
    }

    void MetadataUnitTests::RequiresPeerActivation()
    {
        // Types that currently have custom logic/state in the framework peer.
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::EntranceThemeTransition)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::AddDeleteThemeTransition)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Binding)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Button)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TextBox)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::UserControl)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ContentControl)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ItemsControl)->RequiresPeerActivation());
        VERIFY_IS_TRUE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ListView)->RequiresPeerActivation());

        // Types that don't have custom logic/state in the framework peer.
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::TextBlock)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Control)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::ContentPresenter)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::StackPanel)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Grid)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Canvas)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Border)->RequiresPeerActivation());
        VERIFY_IS_FALSE(MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::Underline)->RequiresPeerActivation());
    }

    void MetadataUnitTests::GetStorageType()
    {
        CDependencyProperty dp;

        dp.SetPropertyTypeIndex(KnownTypeIndex::String);
        VERIFY_ARE_EQUAL(valueString, dp.GetStorageType());

        dp.SetPropertyTypeIndex(KnownTypeIndex::Uri);
        VERIFY_ARE_EQUAL(valueString, dp.GetStorageType());

        dp.SetPropertyTypeIndex(KnownTypeIndex::Int32);
        VERIFY_ARE_EQUAL(valueSigned, dp.GetStorageType());

        dp.SetPropertyTypeIndex(KnownTypeIndex::Double);
        dp.SetIndex(KnownPropertyIndex::TimeSpan_Seconds);
        VERIFY_ARE_EQUAL(valueDouble, dp.GetStorageType());
    }

    void MetadataUnitTests::GetOffset()
    {
        MockDependencyProperty dp;

        dp.offset = 42;
        VERIFY_ARE_EQUAL(42, dp.GetOffset());
    }

    void MetadataUnitTests::GetGroupOffset()
    {
        MockDependencyProperty dp;

        dp.groupOffset = 42;
        VERIFY_ARE_EQUAL(42, dp.GetGroupOffset());
    }

    void MetadataUnitTests::ReRegisteringBuiltinDPDoesNotAV()
    {
        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        MockDependencyProperty dp;

        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            &dp));

        // Register one more time.
        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            &dp));

        VERIFY_ARE_EQUAL(1, mock->GetStorage()->m_customDPsByTypeAndNameCache->size());
    }

    void MetadataUnitTests::ReRegisteringCustomDPSetsUnderlyingDP()
    {
        DECLARE_CONST_STRING_IN_TEST_CODE(c_fooPropertyName, L"Foo");

        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        CCustomProperty* customProp = nullptr;
        VERIFY_SUCCEEDED(CCustomProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            KnownTypeIndex::DependencyObject,
            KnownTypeIndex::FrameworkElement,
            MetaDataPropertyInfoFlags::None,
            nullptr,
            c_fooPropertyName,
            &customProp));

        VERIFY_ARE_EQUAL(nullptr, mock->GetStorage()->m_customDPsByTypeAndNameCache);

        // Register "Foo"
        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            customProp));
        VERIFY_ARE_EQUAL(1, mock->GetStorage()->m_customDPsByTypeAndNameCache->size());

        // Verify stored property
        DynamicMetadataStorage::PropertiesTable* propertiesMap
            = mock->GetStorage()->m_customDPsByTypeAndNameCache->find(KnownTypeIndex::FrameworkElement)->second.get();
        const CDependencyProperty* storedProperty1 = propertiesMap->find(c_fooPropertyName)->second;
        VERIFY_ARE_EQUAL(storedProperty1, customProp);

        // Register "Foo" again with different prop
        CCustomProperty* customProp2 = nullptr;
        VERIFY_SUCCEEDED(CCustomProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            KnownTypeIndex::DependencyObject,
            KnownTypeIndex::FrameworkElement,
            MetaDataPropertyInfoFlags::None,
            nullptr,
            c_fooPropertyName,
            &customProp2));

        VERIFY_SUCCEEDED(MetadataAPI::AssociateDependencyProperty(
            MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
            customProp2));

        VERIFY_ARE_EQUAL(1, mock->GetStorage()->m_customDPsByTypeAndNameCache->size());

        // Verify stored property is still the original property
        const CDependencyProperty* storedProperty2 = propertiesMap->find(c_fooPropertyName)->second;
        VERIFY_ARE_EQUAL(storedProperty2, customProp2);

        // Verify underlying DP was set
        const CDependencyProperty* underlyingProp = nullptr;
        VERIFY_SUCCEEDED(customProp->TryGetUnderlyingDP(&underlyingProp));
        VERIFY_ARE_EQUAL(underlyingProp, customProp2);
    }

    void MetadataUnitTests::RunClassConstructorIsDelayed()
    {
        auto mock1 = TlsProvider<ClassInfoCallbacks>::CreateWrappedObject();
        bool constructorCalled = false;
        mock1->RunClassConstructorIfNecessary = [&constructorCalled]() -> HRESULT
        {
            constructorCalled = true;
            return S_OK;
        };

        IXamlMemberTypeMayReturnNull_XamlMember member;
        const CDependencyProperty* pResult = nullptr;

        // Import a custom property.
        wrl_wrappers::HStringReference customTypeStringRef(L"CustomNamespace.CustomType");

        auto customType = MockXamlType::CreateMetadata(customTypeStringRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider provider;
        provider.GetXamlTypeByFullNameCallback = [&customTypeStringRef, customType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
        {
            if (fullName == customTypeStringRef)
            {
                return customType.CopyTo(ppXamlType);
            }

            *ppXamlType = nullptr;
            return S_OK;
        };

        wxaml_interop::TypeName customTypeName = { customTypeStringRef.Get(), wxaml_interop::TypeKind_Metadata };
        const CClassInfo* resolvedType = nullptr;
    }

    void MetadataUnitTests::RunClassConstructorIsNotDelayed()
    {
        auto mock1 = TlsProvider<ClassInfoCallbacks>::CreateWrappedObject();
        bool constructorCalled = false;
        mock1->RunClassConstructorIfNecessary = [&constructorCalled]() -> HRESULT
        {
            constructorCalled = true;
            return S_OK;
        };

        IXamlMemberTypeMayReturnNull_XamlMember member;
        const CDependencyProperty* pResult = nullptr;

        // Import a custom property.
        wrl_wrappers::HStringReference customTypeStringRef(L"CustomNamespace.CustomType");

        auto customType = MockXamlType::CreateMetadata(customTypeStringRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider provider;
        provider.GetXamlTypeByFullNameCallback = [&customTypeStringRef, customType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
        {
            if (fullName == customTypeStringRef)
            {
                return customType.CopyTo(ppXamlType);
            }

            *ppXamlType = nullptr;
            return S_OK;
        };

        wxaml_interop::TypeName customTypeName = { customTypeStringRef.Get(), wxaml_interop::TypeKind_Metadata };
        const CClassInfo* resolvedType = nullptr;
    }

    void MetadataUnitTests::TestPropertyGetters()
    {
        DECLARE_CONST_STRING_IN_TEST_CODE(c_customPropertyName, L"CustomProperty");
        DECLARE_CONST_STRING_IN_TEST_CODE(c_customDependencyPropertyName, L"CustomDependencyProperty");

        auto mock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();

        CCustomProperty* customProp = nullptr;

        VERIFY_SUCCEEDED(CCustomProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            KnownTypeIndex::DependencyObject,
            KnownTypeIndex::FrameworkElement,
            MetaDataPropertyInfoFlags::None,
            nullptr,
            c_customPropertyName,
            &customProp));

        VERIFY_IS_TRUE(customProp->Is<CCustomProperty>());
        VERIFY_IS_TRUE(customProp->Is<CDependencyProperty>());
        VERIFY_IS_FALSE(customProp->Is<CCustomDependencyProperty>());
        VERIFY_IS_FALSE(customProp->Is<CSimpleProperty>());
        VERIFY_ARE_EQUAL(customProp, customProp->AsOrNull<CCustomProperty>());
        VERIFY_ARE_EQUAL(customProp, customProp->AsOrNull<CDependencyProperty>());
        VERIFY_IS_NULL(customProp->AsOrNull<CCustomDependencyProperty>());
        VERIFY_IS_NULL(customProp->AsOrNull<CSimpleProperty>());

        CCustomDependencyProperty* customDependencyProp = nullptr;

        VERIFY_SUCCEEDED(CCustomDependencyProperty::Create(
            mock->GetStorage()->GetNextAvailablePropertyIndex(),
            MetaDataPropertyInfoFlags::None,
            c_customDependencyPropertyName,
            &customDependencyProp));

        VERIFY_IS_FALSE(customDependencyProp->Is<CCustomProperty>());
        VERIFY_IS_TRUE(customDependencyProp->Is<CDependencyProperty>());
        VERIFY_IS_TRUE(customDependencyProp->Is<CCustomDependencyProperty>());
        VERIFY_IS_FALSE(customDependencyProp->Is<CSimpleProperty>());
        VERIFY_IS_NULL(customDependencyProp->AsOrNull<CCustomProperty>());
        VERIFY_ARE_EQUAL(customDependencyProp, customDependencyProp->AsOrNull<CDependencyProperty>());
        VERIFY_ARE_EQUAL(customDependencyProp, customDependencyProp->AsOrNull<CCustomDependencyProperty>());
        VERIFY_IS_NULL(customDependencyProp->AsOrNull<CSimpleProperty>());

        const CPropertyBase* builtInProperty = MetadataAPI::GetPropertyBaseByIndex(KnownPropertyIndex::DependencyObject_Name);

        VERIFY_IS_FALSE(builtInProperty->Is<CCustomProperty>());
        VERIFY_IS_TRUE(builtInProperty->Is<CDependencyProperty>());
        VERIFY_IS_FALSE(builtInProperty->Is<CCustomDependencyProperty>());
        VERIFY_IS_FALSE(builtInProperty->Is<CSimpleProperty>());
        VERIFY_IS_NULL(builtInProperty->AsOrNull<CCustomProperty>());
        VERIFY_ARE_EQUAL(builtInProperty, builtInProperty->AsOrNull<CDependencyProperty>());
        VERIFY_IS_NULL(builtInProperty->AsOrNull<CCustomDependencyProperty>());
        VERIFY_IS_NULL(builtInProperty->AsOrNull<CSimpleProperty>());

        {
            auto prop = MetadataAPI::GetPropertyBaseByIndex(KnownPropertyIndex::DependencyObject_Name);
            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }

        {
            auto prop = MetadataAPI::GetDependencyPropertyByIndex(KnownPropertyIndex::DependencyObject_Name);
            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }

        {
            const CPropertyBase* prop = MetadataAPI::TryGetBuiltInPropertyBaseByName(
                MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject),
                c_Name,
                true);
            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }

        {
            const CPropertyBase* prop = MetadataAPI::TryGetBuiltInPropertyBaseByName(
                MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::FrameworkElement),
                c_customPropertyName);
            VERIFY_IS_NULL(prop);
        }

        {
            const CDependencyProperty* prop = nullptr;

            VERIFY_SUCCEEDED(MetadataAPI::TryGetDependencyPropertyByName(
                MetadataAPI::GetClassInfoByIndex(KnownTypeIndex::DependencyObject),
                c_Name,
                &prop,
                true));

            VERIFY_ARE_EQUAL(builtInProperty, prop);
        }
    }
    #pragma region Registered IXamlMetadataProviders (XamlMetadataProviderRegistry)

    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameNewPage, L"HotReload.NewPage");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameOtherPage, L"HotReload.OtherPage");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameAppOwnedType, L"HotReload.AppOwnedType");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNameContestedType, L"HotReload.ContestedType");
    DECLARE_CONST_STRING_IN_TEST_CODE(c_FullNamePluginWidget, L"Plugin.Widget");

    namespace
    {
        // The application's own generated provider. It knows about c_FullNameAppOwnedType, optionally
        // about c_FullNameContestedType, and nothing else - which is the situation after a new Page is
        // added to a running app.
        void SetUpAppProvider(
            MockXamlMetadataProvider& provider,
            const Microsoft::WRL::ComPtr<MockXamlType>& appOwnedType,
            const Microsoft::WRL::ComPtr<MockXamlType>& contestedType = nullptr)
        {
            auto resolve = [appOwnedType, contestedType](HSTRING fullName, IXamlType** ppXamlType) -> HRESULT
            {
                if (appOwnedType != nullptr && wrl_wrappers::HStringReference(L"HotReload.AppOwnedType") == fullName)
                {
                    return appOwnedType.CopyTo(ppXamlType);
                }

                if (contestedType != nullptr && wrl_wrappers::HStringReference(L"HotReload.ContestedType") == fullName)
                {
                    return contestedType.CopyTo(ppXamlType);
                }

                *ppXamlType = nullptr;
                return S_OK;
            };

            provider.GetXamlTypeByFullNameCallback = resolve;

            provider.GetXamlTypeCallback = [resolve](wxaml_interop::TypeName typeName, IXamlType** ppXamlType) -> HRESULT
            {
                return resolve(typeName.Name, ppXamlType);
            };
        }

        bool IsUnresolved(const CClassInfo* type)
        {
            return type != nullptr &&
                   !MetadataAPI::IsKnownIndex(type->GetIndex()) &&
                   static_cast<const CCustomClassInfo*>(type)->IsUnresolvedPlaceholder();
        }

        ULONG GetRefCount(_In_ xaml_markup::IXamlMetadataProvider* object)
        {
            object->AddRef();
            return object->Release();
        }
    }

    // 1. Before anything is registered, a name nobody describes resolves to an unresolved placeholder.
    //    That placeholder IS the cached miss the rest of these tests have to defeat.
    void MetadataUnitTests::SideProvider_UnknownTypeIsCachedAsUnresolvedMiss()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &resolved));
        VERIFY_IS_NOT_NULL(resolved);
        VERIFY_IS_TRUE(IsUnresolved(resolved), L"An undescribed name is cached as an unresolved placeholder");

        // The miss really is cached: a second lookup returns the very same placeholder rather than
        // asking the provider again.
        const CClassInfo* resolvedAgain = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &resolvedAgain));
        VERIFY_ARE_EQUAL(resolved, resolvedAgain, L"The miss is cached, not recomputed");
    }

    // 2. The headline scenario: a name that failed to resolve resolves after its provider is registered, with no
    //    separate invalidation call. Mutation proof: step 1 caches the miss, so if registration did not evict it,
    //    step 3 would return the same placeholder.
    void MetadataUnitTests::SideProvider_RegistrationForgetsCachedMisses()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        // Step 1: the name does not resolve, and the miss is cached.
        const CClassInfo* beforeRegistration = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &beforeRegistration));
        VERIFY_IS_TRUE(IsUnresolved(beforeRegistration), L"Lookup misses before registration");

        const auto typesAddedBefore = XamlTypeCacheGenerations::g_typesAdded.load();

        // Step 2: register the provider that knows the type.
        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, registration.Status);
        VERIFY_IS_TRUE(registration.IsRegistered());
        VERIFY_IS_TRUE(registration.Id != c_invalidXamlMetadataProviderId, L"A registered provider gets a real id");
        VERIFY_IS_TRUE(XamlTypeCacheGenerations::g_typesAdded.load() != typesAddedBefore,
            L"The parser's 'type not found' caches are told to refresh");

        // Step 3: the name now resolves through the registered provider.
        const CClassInfo* afterRegistration = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &afterRegistration));
        VERIFY_ARE_NOT_EQUAL(beforeRegistration, afterRegistration, L"Registration evicted the cached miss");
        VERIFY_IS_FALSE(IsUnresolved(afterRegistration), L"The name resolves through the registered provider");
        VERIFY_ARE_EQUAL(
            sideProvider->GetTypeNoRef(L"HotReload.NewPage").Get(),
            static_cast<const CCustomClassInfo*>(afterRegistration)->GetXamlTypeNoRef(),
            L"The resolved type is the one the registered provider supplied");
        VERIFY_IS_TRUE(static_cast<const CCustomClassInfo*>(afterRegistration)->GetSideProviderClaim() != 0,
            L"The type is tagged with the claim that records who supplied it");
    }

    // 3. The by-TypeName entry point (used by Frame.Navigate(typeof(NewPage))) resolves too, both for WinRT types and
    //    for non-WinRT (.NET) types, whose TypeName is assembly-qualified.
    void MetadataUnitTests::SideProvider_ResolvesByTypeName()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto sideProvider = MockSideXamlMetadataProvider::Create()
            ->WithType(L"HotReload.NewPage")
            ->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, Plugin, Version=1.0.0.0");
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get()).IsRegistered());

        wrl_wrappers::HStringReference newPageRef(L"HotReload.NewPage");
        wxaml_interop::TypeName newPageTypeName = { newPageRef.Get(), wxaml_interop::TypeKind_Metadata };

        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByTypeName(newPageTypeName, &resolved));
        VERIFY_IS_NOT_NULL(resolved);
        VERIFY_IS_FALSE(IsUnresolved(resolved), L"GetClassInfoByTypeName resolves through the registered provider");
        VERIFY_ARE_EQUAL(
            sideProvider->GetTypeNoRef(L"HotReload.NewPage").Get(),
            static_cast<const CCustomClassInfo*>(resolved)->GetXamlTypeNoRef());

        wrl_wrappers::HStringReference widgetRef(L"Plugin.Widget, Plugin, Version=1.0.0.0");
        wxaml_interop::TypeName widgetTypeName = { widgetRef.Get(), wxaml_interop::TypeKind_Custom };

        const CClassInfo* widget = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::GetClassInfoByTypeName(widgetTypeName, &widget));
        VERIFY_IS_FALSE(IsUnresolved(widget), L"A .NET type resolves by its assembly-qualified TypeName");
        VERIFY_ARE_EQUAL(
            sideProvider->GetTypeNoRef(L"Plugin.Widget").Get(),
            static_cast<const CCustomClassInfo*>(widget)->GetXamlTypeNoRef());

        // Markup finds the same type by its full name.
        const CClassInfo* widgetByName = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNamePluginWidget, false, &widgetByName));
        VERIFY_ARE_EQUAL(widget, widgetByName, L"Full name and TypeName lookups agree");
    }

    // 4. Registration is additive. Two independent halves:
    //      (a) a name already resolved keeps its identity, and
    //      (b) a name resolved for the FIRST time after registration still goes to the application
    //          provider when both providers can answer.
    //    (b) is the one that actually pins the ordering down. (a) alone would pass even if registered
    //    providers ran first, because the positive cache would hide the ordering.
    void MetadataUnitTests::SideProvider_DoesNotDisturbTypesTheAppProviderOwns()
    {
        wrl_wrappers::HStringReference appOwnedRef(L"HotReload.AppOwnedType");
        auto appOwnedType = MockXamlType::CreateMetadata(appOwnedRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        wrl_wrappers::HStringReference contestedRef(L"HotReload.ContestedType");
        auto appContestedType = MockXamlType::CreateMetadata(contestedRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, appOwnedType, appContestedType);

        const CClassInfo* before = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &before));
        VERIFY_IS_FALSE(IsUnresolved(before), L"The app provider resolves its own type");

        // A registered provider that also claims to own both app types must not be able to take either over,
        // because registered providers are only consulted after the app provider declines.
        auto sideProvider = MockSideXamlMetadataProvider::Create()
            ->WithType(L"HotReload.AppOwnedType")
            ->WithType(L"HotReload.ContestedType")
            ->WithType(L"HotReload.NewPage");
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get()).IsRegistered());

        // (a) The already-cached type is untouched.
        const CClassInfo* after = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &after));
        VERIFY_ARE_EQUAL(before, after, L"The app-owned type keeps the same CClassInfo identity");
        VERIFY_ARE_EQUAL(
            appOwnedType.Get(),
            static_cast<const CCustomClassInfo*>(after)->GetXamlTypeNoRef(),
            L"The app-owned type keeps the same IXamlType identity");

        // (b) c_FullNameContestedType has never been looked up, so this resolution really does run
        //     the provider chain. Both providers can answer; the application provider must win.
        VERIFY_ARE_NOT_EQUAL(
            appContestedType.Get(),
            sideProvider->GetTypeNoRef(L"HotReload.ContestedType").Get(),
            L"The two providers really do offer different IXamlType instances");

        const CClassInfo* contested = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameContestedType, false, &contested));
        VERIFY_IS_FALSE(IsUnresolved(contested));
        VERIFY_ARE_EQUAL(
            appContestedType.Get(),
            static_cast<const CCustomClassInfo*>(contested)->GetXamlTypeNoRef(),
            L"A contested name resolves through the application provider, not the registered provider");

        // And unregistering the provider never touches types the app provider supplied.
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded,
            MetadataAPI::UnregisterSideMetadataProvider(sideProvider.Get()).Status);
        const CClassInfo* afterUnregister = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameContestedType, false, &afterUnregister));
        VERIFY_ARE_EQUAL(contested, afterUnregister, L"App-owned types survive unregistration");
    }

    // 5. Registering the same provider twice is a no-op that says so.
    void MetadataUnitTests::SideProvider_RegistrationIsIdempotent()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");

        auto first = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, first.Status);

        auto second = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::AlreadyRegistered, second.Status);
        VERIFY_ARE_EQUAL(first.Id, second.Id, L"The original id is reported back");
        VERIFY_ARE_EQUAL(first.Generation, second.Generation, L"A no-op registration does not bump the generation");
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), MetadataAPI::GetSideMetadataProviderCount(), L"The provider is not registered twice");
        VERIFY_IS_TRUE(MetadataAPI::IsSideMetadataProviderRegistered(sideProvider.Get()));
    }

    // 6. A provider that describes a name already in use as a DIFFERENT type is refused, not resolved by ordering.
    void MetadataUnitTests::SideProvider_ConflictingProviderIsRejected()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto firstProvider = MockSideXamlMetadataProvider::Create()
            ->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, PluginV1, Version=1.0.0.0");
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded,
            MetadataAPI::RegisterSideMetadataProvider(firstProvider.Get()).Status);

        // Resolve the name so the registry knows firstProvider supplies it.
        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNamePluginWidget, false, &resolved));
        VERIFY_IS_FALSE(IsUnresolved(resolved));

        const auto generationBeforeConflict = MetadataAPI::GetMetadataProviderGeneration();

        // Another library that also defines Plugin.Widget is refused outright.
        auto conflictingProvider = MockSideXamlMetadataProvider::Create()
            ->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, PluginV2, Version=2.0.0.0");
        auto conflict = MetadataAPI::RegisterSideMetadataProvider(conflictingProvider.Get());

        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Conflict, conflict.Status);
        VERIFY_IS_FALSE(conflict.IsRegistered());
        VERIFY_ARE_EQUAL(c_invalidXamlMetadataProviderId, conflict.Id, L"A rejected provider gets no id");
        VERIFY_IS_TRUE(conflict.ConflictingName.Equals(c_FullNamePluginWidget), L"The refusal names the type");
        VERIFY_ARE_EQUAL(generationBeforeConflict, conflict.Generation, L"A rejected registration does not bump the generation");
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), MetadataAPI::GetSideMetadataProviderCount(), L"The conflicting provider was not added");

        std::wstring message;
        VERIFY_ARE_EQUAL(HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS), DescribeRegistrationResult(conflict, message));
        VERIFY_IS_TRUE(message.find(L"Plugin.Widget") != std::wstring::npos, L"The public error names the type");

        const CClassInfo* stillResolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNamePluginWidget, false, &stillResolved));
        VERIFY_ARE_EQUAL(resolved, stillResolved, L"The original type is untouched");

        // A provider that answers for a *different* name is fine.
        auto disjointProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.OtherPage");
        VERIFY_ARE_EQUAL(
            XamlMetadataProviderRegistrationStatus::Succeeded,
            MetadataAPI::RegisterSideMetadataProvider(disjointProvider.Get()).Status);
    }

    // 7. Two providers that describe the same type (two plugins referencing one library) share it: no conflict, one
    //    CClassInfo, and it survives until the last of them is unregistered.
    void MetadataUnitTests::SideProvider_SharedTypeSurvivesUntilLastProviderLeaves()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto pluginOne = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto pluginTwo = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, MetadataAPI::RegisterSideMetadataProvider(pluginOne.Get()).Status);

        const CClassInfo* shared = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &shared));
        VERIFY_IS_FALSE(IsUnresolved(shared));

        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded,
            MetadataAPI::RegisterSideMetadataProvider(pluginTwo.Get()).Status,
            L"Agreeing about a type in use is not a conflict");

        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded,
            MetadataAPI::UnregisterSideMetadataProvider(pluginOne.Get()).Status);
        const CClassInfo* afterFirstLeaves = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &afterFirstLeaves));
        VERIFY_ARE_EQUAL(shared, afterFirstLeaves, L"The type keeps its identity while another provider still supplies it");

        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded,
            MetadataAPI::UnregisterSideMetadataProvider(pluginTwo.Get()).Status);
        const CClassInfo* afterLastLeaves = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &afterLastLeaves));
        VERIFY_IS_TRUE(IsUnresolved(afterLastLeaves), L"Once nobody supplies it, the name stops resolving");
    }

    // 8. Registered providers that disagree about a name nobody has used yet make it ambiguous: it resolves to nothing,
    //    whatever the registration order. Unregistering one of them settles it.
    void MetadataUnitTests::SideProvider_AmbiguousNameResolvesToNothing()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto v1 = MockSideXamlMetadataProvider::Create()->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, PluginV1, Version=1.0.0.0");
        auto v2 = MockSideXamlMetadataProvider::Create()->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, PluginV2, Version=2.0.0.0");
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(v1.Get()).IsRegistered());
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(v2.Get()).IsRegistered(), L"Unused names can't conflict yet");

        const CClassInfo* ambiguous = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNamePluginWidget, false, &ambiguous));
        VERIFY_IS_TRUE(IsUnresolved(ambiguous), L"An ambiguous name resolves to nothing rather than to the first provider");

        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, MetadataAPI::UnregisterSideMetadataProvider(v1.Get()).Status);

        const CClassInfo* settled = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNamePluginWidget, false, &settled));
        VERIFY_IS_FALSE(IsUnresolved(settled), L"With one provider left the name resolves");
        VERIFY_ARE_EQUAL(
            v2->GetTypeNoRef(L"Plugin.Widget").Get(),
            static_cast<const CCustomClassInfo*>(settled)->GetXamlTypeNoRef());
    }

    // 9. Unregistering forgets exactly the names only that provider supplied. Objects created from those types keep
    //    working: their CClassInfo (and its IXamlType) stays reachable by index.
    void MetadataUnitTests::SideProvider_UnregisterForgetsOnlyItsTypes()
    {
        wrl_wrappers::HStringReference appOwnedRef(L"HotReload.AppOwnedType");
        auto appOwnedType = MockXamlType::CreateMetadata(appOwnedRef.Get())->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, appOwnedType);

        auto pluginA = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto pluginB = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.OtherPage");
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(pluginA.Get()).IsRegistered());
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(pluginB.Get()).IsRegistered());

        const CClassInfo* newPage = nullptr;
        const CClassInfo* otherPage = nullptr;
        const CClassInfo* appOwned = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &newPage));
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameOtherPage, false, &otherPage));
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &appOwned));
        VERIFY_IS_FALSE(IsUnresolved(newPage));
        VERIFY_IS_FALSE(IsUnresolved(otherPage));
        const KnownTypeIndex newPageIndex = newPage->GetIndex();

        const auto typesRemovedBefore = XamlTypeCacheGenerations::g_typesRemoved.load();
        const auto removal = MetadataAPI::UnregisterSideMetadataProvider(pluginA.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, removal.Status);
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), removal.OrphanedClaims.size(), L"Exactly one type lost its last provider");
        VERIFY_IS_TRUE(XamlTypeCacheGenerations::g_typesRemoved.load() != typesRemovedBefore,
            L"The parser's name caches are told to refresh");
        VERIFY_IS_FALSE(MetadataAPI::IsSideMetadataProviderRegistered(pluginA.Get()));

        const CClassInfo* newPageAfter = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &newPageAfter));
        VERIFY_IS_TRUE(IsUnresolved(newPageAfter), L"Its name no longer resolves");

        VERIFY_ARE_EQUAL(newPage, MetadataAPI::GetClassInfoByIndex(newPageIndex), L"The old type entry is still there for live objects");
        VERIFY_ARE_EQUAL(
            pluginA->GetTypeNoRef(L"HotReload.NewPage").Get(),
            static_cast<const CCustomClassInfo*>(newPage)->GetXamlTypeNoRef(),
            L"and still holds its IXamlType");

        const CClassInfo* otherPageAfter = nullptr;
        const CClassInfo* appOwnedAfter = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameOtherPage, false, &otherPageAfter));
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &appOwnedAfter));
        VERIFY_ARE_EQUAL(otherPage, otherPageAfter, L"Another provider's type is untouched");
        VERIFY_ARE_EQUAL(appOwned, appOwnedAfter, L"The app's type is untouched");

        // Registering it again brings the name back, as a new type entry.
        VERIFY_IS_TRUE(MetadataAPI::RegisterSideMetadataProvider(pluginA.Get()).IsRegistered());
        const CClassInfo* newPageAgain = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &newPageAgain));
        VERIFY_IS_FALSE(IsUnresolved(newPageAgain), L"Re-registering makes the name resolve again");
    }

    // 10. The registry owns the provider's lifetime for as long as it is registered.
    void MetadataUnitTests::SideProvider_RegistryOwnsProviderLifetime()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");

        const ULONG refCountBefore = GetRefCount(sideProvider.Get());

        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, registration.Status);

        const ULONG refCountAfterRegister = GetRefCount(sideProvider.Get());
        VERIFY_ARE_EQUAL(refCountBefore + 1, refCountAfterRegister, L"The registry took a strong reference");

        auto unregistration = MetadataAPI::UnregisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, unregistration.Status);
        VERIFY_ARE_EQUAL(registration.Id, unregistration.Id);
        VERIFY_IS_TRUE(unregistration.Generation > registration.Generation, L"Unregistration is observable");

        const ULONG refCountAfterUnregister = GetRefCount(sideProvider.Get());
        VERIFY_ARE_EQUAL(refCountBefore, refCountAfterUnregister, L"The registry released its reference");
        VERIFY_ARE_EQUAL(static_cast<size_t>(0), MetadataAPI::GetSideMetadataProviderCount());
    }

    // 11. The generation is monotonic and observable, so a stale cache cannot hide a registration.
    void MetadataUnitTests::SideProvider_GenerationIsObservable()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        const auto initialGeneration = MetadataAPI::GetMetadataProviderGeneration();

        auto sideProvider = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_IS_TRUE(registration.Generation > initialGeneration, L"Registration bumps the generation");
        VERIFY_ARE_EQUAL(registration.Generation, MetadataAPI::GetMetadataProviderGeneration());

        auto invalidation = MetadataAPI::InvalidateUnresolvedTypeCache();
        VERIFY_IS_TRUE(invalidation.Generation > registration.Generation, L"Invalidation bumps the generation");

        auto secondInvalidation = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameNewPage);
        VERIFY_IS_TRUE(secondInvalidation.Generation > invalidation.Generation, L"The generation is strictly monotonic");

        auto unregistration = MetadataAPI::UnregisterSideMetadataProvider(sideProvider.Get());
        VERIFY_IS_TRUE(unregistration.Generation > secondInvalidation.Generation, L"Unregistration bumps the generation");
    }

    // 12. Invalidation is surgical: only cached misses go.
    void MetadataUnitTests::SideProvider_InvalidationKeepsResolvedTypes()
    {
        wrl_wrappers::HStringReference appOwnedRef(L"HotReload.AppOwnedType");
        auto appOwnedType = MockXamlType::CreateMetadata(appOwnedRef.Get())
            ->WithBaseType(KnownTypeIndex::Object);

        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, appOwnedType);

        // One name resolves, two do not.
        const CClassInfo* resolvedType = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &resolvedType));
        VERIFY_IS_FALSE(IsUnresolved(resolvedType));

        const CClassInfo* missOne = nullptr;
        const CClassInfo* missTwo = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameNewPage, false, &missOne));
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameOtherPage, false, &missTwo));
        VERIFY_IS_TRUE(IsUnresolved(missOne));
        VERIFY_IS_TRUE(IsUnresolved(missTwo));

        // Targeted invalidation touches exactly one entry.
        auto targeted = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameNewPage);
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), targeted.UnresolvedEntriesEvicted);

        // Invalidating the resolved name is a no-op: resolved types are never evicted.
        auto onResolved = MetadataAPI::InvalidateUnresolvedTypeCache(c_FullNameAppOwnedType);
        VERIFY_ARE_EQUAL(static_cast<size_t>(0), onResolved.UnresolvedEntriesEvicted, L"A resolved name is never evicted");

        const CClassInfo* resolvedTypeAgain = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &resolvedTypeAgain));
        VERIFY_ARE_EQUAL(resolvedType, resolvedTypeAgain, L"The resolved type kept its identity");

        // The bulk overload takes the remaining miss and nothing else.
        auto bulk = MetadataAPI::InvalidateUnresolvedTypeCache();
        VERIFY_ARE_EQUAL(static_cast<size_t>(1), bulk.UnresolvedEntriesEvicted, L"Only the remaining miss was evicted");

        const CClassInfo* resolvedTypeFinal = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNameAppOwnedType, false, &resolvedTypeFinal));
        VERIFY_ARE_EQUAL(resolvedType, resolvedTypeFinal, L"Bulk invalidation still leaves resolved types alone");
    }

    // 13. The threading contract, stated as it actually is rather than as we would like it to be.
    //
    //     CStaticLock is a CRITICAL_SECTION, so it is recursive for the owning thread. The registry
    //     mutates its own state under that lock and probes a candidate provider *without* it. The
    //     lookup path is a different story: MetadataAPI::TryGetClassInfoByFullName already holds the
    //     lock across the call into the application's own provider, and registered providers are consulted
    //     from that same place, so they inherit that pre-existing behaviour. This test pins both
    //     halves down so the contract is explicit instead of assumed.
    void MetadataUnitTests::SideProvider_LockContractIsExplicit()
    {
        auto storageMock = TlsProvider<DynamicMetadataStorageMock>::CreateWrappedObject();
        MockXamlMetadataProvider appProvider(storageMock);
        SetUpAppProvider(appProvider, nullptr);

        VERIFY_IS_FALSE(CStaticLock::IsLocked(), L"The test starts with no lock held");

        auto sideProvider = MockSideXamlMetadataProvider::Create()
            ->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, PluginV1, Version=1.0.0.0");

        // (a) Registry state is mutated under CStaticLock, and the lock is not leaked.
        storageMock->LastAccessWasLocked = false;
        auto registration = MetadataAPI::RegisterSideMetadataProvider(sideProvider.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Succeeded, registration.Status);
        VERIFY_IS_TRUE(storageMock->LastAccessWasLocked, L"Registry state is mutated under CStaticLock");
        VERIFY_IS_FALSE(CStaticLock::IsLocked(), L"Registration does not leak the lock");

        // (b) Drive a lookup so the registry records that this provider supplies the name.
        const CClassInfo* resolved = nullptr;
        VERIFY_SUCCEEDED(MetadataAPI::TryGetClassInfoByFullName(c_FullNamePluginWidget, false, &resolved));
        VERIFY_IS_FALSE(IsUnresolved(resolved));
        VERIFY_IS_TRUE(sideProvider->GetXamlTypeByFullNameCallCount > 0, L"The registered provider really was called");
        VERIFY_IS_FALSE(CStaticLock::IsLocked(), L"Lookup does not leak the lock");

        // (c) The guarantee the registry itself makes: the conflict probe runs with no lock held, so
        //     a provider is free to call back into the metadata API while being validated.
        auto conflictingProvider = MockSideXamlMetadataProvider::Create()
            ->WithCustomType(L"Plugin.Widget", L"Plugin.Widget, PluginV2, Version=2.0.0.0");
        VERIFY_ARE_EQUAL(
            XamlMetadataProviderRegistrationStatus::Conflict,
            MetadataAPI::RegisterSideMetadataProvider(conflictingProvider.Get()).Status);
        VERIFY_IS_TRUE(conflictingProvider->GetXamlTypeByFullNameCallCount > 0, L"The candidate really was probed");
        VERIFY_IS_FALSE(conflictingProvider->WasCalledUnderStaticLock, L"The conflict probe runs without CStaticLock held");

        // (d) The pre-existing behaviour the lookup path inherits, recorded so it is not a surprise.
        //     TryGetClassInfoByFullName takes DynamicMetadataStorageInstanceWithLock and keeps it
        //     across ImportClassInfoFromMetadataProvider, which is how the application's own provider
        //     has always been invoked. A registered provider is called from the same place, so it
        //     must be as re-entrancy tolerant as an application provider already has to be.
        //     If the lookup path is ever changed to drop the lock first, flip this assertion.
        VERIFY_IS_TRUE(
            sideProvider->WasCalledUnderStaticLock,
            L"Documented today's behaviour: the lookup path invokes providers with CStaticLock held");
    }

    // 14. The result model never lies about a failure.
    void MetadataUnitTests::SideProvider_RefusesNullAndUnknownProvider()
    {
        MockXamlMetadataProvider appProvider;
        SetUpAppProvider(appProvider, nullptr);

        auto nullResult = MetadataAPI::RegisterSideMetadataProvider(nullptr);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Refused, nullResult.Status);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRefusalReason::NullProvider, nullResult.Reason);
        VERIFY_IS_FALSE(nullResult.IsRegistered());
        VERIFY_ARE_EQUAL(c_invalidXamlMetadataProviderId, nullResult.Id);

        std::wstring message;
        VERIFY_ARE_EQUAL(E_INVALIDARG, DescribeRegistrationResult(nullResult, message));

        auto nullRemoval = MetadataAPI::UnregisterSideMetadataProvider(nullptr);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Refused, nullRemoval.Status);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRefusalReason::NullProvider, nullRemoval.Reason);

        auto neverRegistered = MockSideXamlMetadataProvider::Create()->WithType(L"HotReload.NewPage");
        auto unknown = MetadataAPI::UnregisterSideMetadataProvider(neverRegistered.Get());
        VERIFY_ARE_EQUAL(XamlMetadataProviderRegistrationStatus::Refused, unknown.Status);
        VERIFY_ARE_EQUAL(XamlMetadataProviderRefusalReason::NotRegistered, unknown.Reason);
        VERIFY_IS_FALSE(MetadataAPI::IsSideMetadataProviderRegistered(neverRegistered.Get()));

        // None of these failures disturbed the registry.
        VERIFY_ARE_EQUAL(static_cast<size_t>(0), MetadataAPI::GetSideMetadataProviderCount());
    }

    #pragma endregion
} } } } }