// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Win8Xaml.CompilerProxies;

namespace UnitTests
{
    /// <summary>
    /// C# hot reload builds (EnableXamlHotReload). .NET hot reload adds fields to live objects without running their
    /// constructors, so generated code that live, pre-update objects can call must not assume those fields are set.
    /// </summary>
    [TestClass]
    public class CSharpHotReloadCodegenTests
    {
        private const string Page = @"<Page x:Class='LibManagedDll.BindPathParserClass'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    xmlns:dll='using:LibManagedDll'>
    <ContentControl>
        <ContentControl.ContentTemplate>
            <DataTemplate x:DataType='dll:BindPathParserClass'>
                <Grid>
                    <dll:NamedElementForPathing x:Name='helper'/>
                    <TextBox Text='{x:Bind helper.Value, Mode=TwoWay}'/>
                </Grid>
            </DataTemplate>
        </ContentControl.ContentTemplate>
    </ContentControl>
</Page>";

        private TestHelper _testHelper;

        [TestInitialize]
        public void TestInitialize()
        {
            _testHelper = new TestHelper();
        }

        // A template's Bindings objects created before the update that gave the template its first tracked binding have no
        // bindingsTracking: Recycle (called when a list recycles their containers) and SetDataRoot skip it, and the paths
        // that need it create it.
        [TestMethod]
        public void HotReload_BindingsTrackingMayBeMissingOnLiveObjects()
        {
            string hotReload = Generate(hotReload: true);
            Assert.IsTrue(Regex.IsMatch(hotReload, @"public void Recycle\(\)\s*\{\s*this\.bindingsTracking\?\.ReleaseAllListeners\(\);"), hotReload);
            StringAssert.Contains(hotReload, "this.EnsureBindingsTracking().RegisterTwoWayListener_");
            Assert.IsTrue(Regex.IsMatch(hotReload, @"private \w+Tracking EnsureBindingsTracking\(\)"), hotReload);
            Assert.IsFalse(Regex.IsMatch(hotReload, @"this\.bindingsTracking\.(ReleaseAllListeners|RegisterTwoWayListener_|UpdateChildListeners_)"), hotReload);

            string normal = Generate(hotReload: false);
            Assert.IsTrue(Regex.IsMatch(normal, @"public void Recycle\(\)\s*\{\s*this\.bindingsTracking\.ReleaseAllListeners\(\);"), normal);
            StringAssert.Contains(normal, "this.bindingsTracking.RegisterTwoWayListener_");
        }

        // Containers created before an update keep instances of a template Bindings class that the update renamed (its
        // template root or x:DataType changed); the framework still calls Recycle, ProcessBindings and Update on them. The
        // tombstone that keeps the old class must be inert, not throw.
        [TestMethod]
        public void HotReload_TombstonedBindingsClassesAreInert()
        {
            string previous = Generate(hotReload: true);
            Match bindings = Regex.Match(previous, @"class (\w+_obj\d+_Bindings\w*)\s*:");
            Assert.IsTrue(bindings.Success, previous);
            string oldName = bindings.Groups[1].Value;
            string current = Regex.Replace(previous, @"\b" + oldName + @"\b", oldName + "_renamed");

            string restored = HotReloadRetiredMembers.Restore(current, previous);
            Match tombstone = Regex.Match(restored, @"class " + oldName + @"\b.*?#pragma warning restore", RegexOptions.Singleline);
            Assert.IsTrue(tombstone.Success, restored);
            string shell = tombstone.Value;
            Assert.IsFalse(shell.Contains("throw"), shell);
            Assert.IsTrue(Regex.IsMatch(shell, @"public void Recycle\(\)\s*\{\s*\}"), shell);
            Assert.IsTrue(Regex.IsMatch(shell, @"public void ProcessBindings\([^)]*out int nextPhase\)\s*\{\s*nextPhase = -1;\s*\}"), shell);
            Assert.IsTrue(Regex.IsMatch(shell, @"public void Update\(\)\s*\{\s*\}"), shell);
            Assert.IsTrue(Regex.IsMatch(shell, @"public void Connect\([^)]*\)\s*\{\s*\}"), shell);
        }

        // A live XamlTypeInfoProvider created its list of library providers (OtherProviders) before an update that added a
        // package or project reference; the reset after an update rebuilds it, so the new library's types are found.
        [TestMethod]
        public void HotReload_TypeInfoResetRebuildsOtherProviders()
        {
            string reset = HotReloadReset(GenerateTypeInfoWithOtherProvider(hotReload: true, "FullXamlMetadataProvider"));
            StringAssert.Contains(reset, "_otherProviders = null;");
            StringAssert.Contains(reset, "_typeTable = null;");

            // Filled from outside by AddOtherProvider: the list is not the provider's to rebuild.
            string addedFromOutside = HotReloadReset(GenerateTypeInfoWithOtherProvider(hotReload: true, "FullXamlMetadataProvider, DoNotGenerateOtherProviders"));
            Assert.IsFalse(addedFromOutside.Contains("_otherProviders"), addedFromOutside);

            string normal = GenerateTypeInfoWithOtherProvider(hotReload: false, "FullXamlMetadataProvider");
            Assert.IsFalse(normal.Contains("HotReloadResetIfUpdated"), normal);
            StringAssert.Contains(normal, "_otherProviders = otherProviders;");
        }

        private string GenerateTypeInfoWithOtherProvider(bool hotReload, string codeGenFlags)
        {
            const string xaml = @"<Page xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    xmlns:dll='using:LibManagedDll'>
    <Page.Resources>
        <dll:SimpleClass x:Key='key'/>
    </Page.Resources>
</Page>";
            DirectUISchemaContext schema = _testHelper.LoadSchema(SchemaMode.ManagedRuntime | SchemaMode.LoadUserDll);
            TypeInfoCollector collector = _testHelper.CollectTypes(xaml, schema);

            // Any type stands in for a library's metadata provider: code generation only uses its name.
            object provider = new TypeForCodeGen(schema.GetXamlType(new XamlTypeName("using:LibManagedDll", "SimpleClass"))).Instance;
            var providers = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(provider.GetType()));
            providers.Add(provider);
            collector.SchemaInfo.Instance.GetType().GetProperty("OtherMetadataProviders").SetValue(collector.SchemaInfo.Instance, providers);

            var context = new CodeGeneratorProjectContext(new Version(KnownVersions.Latest), "HotReloadOtherProviders");
            context.IsLibrary = true;
            context.RootNamespace = "HotReloadOtherProviders";
            context.ProjectInfo.SetCodeGenFlags(codeGenFlags);
            context.ProjectInfo.IsHotReloadBuild = hotReload;
            context.ProjectInfo.IsHotReloadCodeGen = hotReload;
            List<FileNameAndContentPair> pairs = _testHelper.GenerateTypeInfo(false, collector.SchemaInfo, context.ProjectInfo,
                new ClassName("HotReloadOtherProviders.App"), CodeGenLanguage.CSharp);
            Assert.AreEqual(1, pairs.Count);
            return pairs[0].Contents;
        }

        private static string HotReloadReset(string typeInfo)
        {
            Match reset = Regex.Match(typeInfo, @"private void HotReloadResetIfUpdated\(\)\s*\{.*?\n        \}", RegexOptions.Singleline);
            Assert.IsTrue(reset.Success, typeInfo);
            return reset.Value;
        }

        private string Generate(bool hotReload)
        {
            var context = new CodeGeneratorProjectContext(new Version(KnownVersions.Latest), "CSharpHotReloadCodegen");
            context.ProjectInfo.IsHotReloadBuild = hotReload;
            context.ProjectInfo.IsHotReloadCodeGen = hotReload;
            DirectUISchemaContext schema = _testHelper.LoadSchema(SchemaMode.ManagedRuntime | SchemaMode.LoadUserDll);
            List<FileNameAndContentPair> pairs = _testHelper.GenerateCodeBehind(context, new List<string> { Page }, schema, CodeGenLanguage.CSharp);
            Assert.AreEqual(1, pairs.Count);
            return pairs[0].Contents;
        }
    }
}
