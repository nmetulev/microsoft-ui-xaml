// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // Roslyn can't delete a method that an earlier update of the session added (NullReferenceException in
        // DefinitionMap.GetPreviousMethodHandle): an x:Load element added and then removed takes UpdateUnloadedElement,
        // CompleteUpdate, Update_<path> and the UnloadObject implementation with it. Removed methods are kept, inert.
        [TestMethod]
        public void HotReload_RemovedMethodsAreKeptInert()
        {
            const string before = @"namespace App
{
    partial class Page : global::Microsoft.UI.Xaml.Controls.Page, IThing
    {
        partial void UnloadObject(global::Microsoft.UI.Xaml.DependencyObject unloadableObject)
        {
            this.DisconnectUnloadedObject(6);
        }
        private void DisconnectUnloadedObject(int connectionId)
        {
            throw new global::System.ArgumentException(""Invalid connectionId."");
        }
        private void Update_Vm_Flag(global::System.Boolean obj, int phase)
        {
            this.Flag = obj;
        }
        private bool TryGet_Vm(out int value, int phase) => (value = phase) > 0;
        public void Renamed(int a) { }
        void IThing.Do() { }
        private T Generic<T>() { return default; }
        public Page() { }
        partial void InitializeXProperties()
        {
        }
    }
}";
            const string after = @"namespace App
{
    partial class Page : global::Microsoft.UI.Xaml.Controls.Page
    {
        private void DisconnectUnloadedObject(int connectionId)
        {
            throw new global::System.ArgumentException(""No unloadable elements to disconnect."");
        }
        public void Renamed(string b) { }
    }
}";
            string restored = HotReloadRetiredMembers.Restore(after, before);
            Match block = Regex.Match(restored, @"// Hot reload: removed from the markup.*?#pragma warning restore", RegexOptions.Singleline);
            Assert.IsTrue(block.Success, restored);
            string tombstones = block.Value;

            StringAssert.Contains(tombstones, "partial void UnloadObject(global::Microsoft.UI.Xaml.DependencyObject unloadableObject) { }");
            StringAssert.Contains(tombstones, "private void Update_Vm_Flag(global::System.Boolean obj, int phase) { }");
            StringAssert.Contains(tombstones, "private bool TryGet_Vm(out int value, int phase) { value = default; return default; }");
            // Live members win, whatever their signature; explicit interface implementations, generic methods,
            // constructors and partial methods other than UnloadObject (their declaration may be gone too) are not kept.
            Assert.AreEqual(1, Regex.Matches(restored, @"void DisconnectUnloadedObject\(").Count, restored);
            Assert.IsFalse(tombstones.Contains("Renamed"), tombstones);
            Assert.IsFalse(tombstones.Contains("IThing.Do"), tombstones);
            Assert.IsFalse(tombstones.Contains("Generic"), tombstones);
            Assert.IsFalse(tombstones.Contains("Page()"), tombstones);
            Assert.IsFalse(tombstones.Contains("InitializeXProperties"), tombstones);
        }

        // A type added mid-session keeps every existing type's index (XamlTypeInfo indexes types by position), and the
        // tables and CreateXamlType come in blocks, so the update only touches the last block. Normal builds don't change.
        [TestMethod]
        public void HotReload_TypeInfoKeepsTypeIndicesWhenTypesAreAdded()
        {
            const string before = @"<Page xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    xmlns:dll='using:LibManagedDll'>
    <Page.Resources>
        <dll:SimpleClass x:Key='key'/>
    </Page.Resources>
</Page>";
            string after = before.Replace("<dll:SimpleClass x:Key='key'/>", "<dll:SimpleClass x:Key='key'/>\r\n        <dll:NamedElementForPathing x:Key='added'/>");

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            (string code, List<string> order) Generate(string xaml, bool hotReload, List<string> previousOrder)
            {
                DirectUISchemaContext schema = _testHelper.LoadSchema(SchemaMode.ManagedRuntime | SchemaMode.LoadUserDll);
                TypeInfoCollector collector = _testHelper.CollectTypes(xaml, schema);
                object info = collector.SchemaInfo.Instance;
                info.GetType().GetProperty("HotReloadPreviousTypeOrder", flags).SetValue(info, previousOrder);
                var context = new CodeGeneratorProjectContext(new Version(KnownVersions.Latest), "HotReloadTypeOrder");
                context.IsLibrary = true;
                context.RootNamespace = "HotReloadTypeOrder";
                context.ProjectInfo.SetCodeGenFlags("FullXamlMetadataProvider");
                context.ProjectInfo.IsHotReloadBuild = hotReload;
                context.ProjectInfo.IsHotReloadCodeGen = hotReload;
                List<FileNameAndContentPair> pairs = _testHelper.GenerateTypeInfo(false, collector.SchemaInfo, context.ProjectInfo,
                    new ClassName("HotReloadTypeOrder.App"), CodeGenLanguage.CSharp);
                return (pairs[0].Contents, (List<string>)info.GetType().GetProperty("TypeTableOrder", flags).GetValue(info));
            }
            Dictionary<string, int> Indices(string code)
            {
                var indices = new Dictionary<string, int>();
                foreach (Match m in Regex.Matches(code, "_typeNameTable\\[(\\d+)\\] = \"([^\"]+)\";"))
                {
                    indices[m.Groups[2].Value] = int.Parse(m.Groups[1].Value);
                }
                return indices;
            }

            (string first, List<string> firstOrder) = Generate(before, true, null);
            (string sorted, _) = Generate(after, true, null);
            (string appended, List<string> appendedOrder) = Generate(after, true, firstOrder);

            Dictionary<string, int> firstIndices = Indices(first);
            Dictionary<string, int> appendedIndices = Indices(appended);
            Assert.IsTrue(firstIndices.Count > 2, first);
            Assert.IsTrue(appendedIndices.Count > firstIndices.Count, appended);
            foreach (KeyValuePair<string, int> type in firstIndices)
            {
                Assert.AreEqual(type.Value, appendedIndices[type.Key], type.Key);
            }
            foreach (KeyValuePair<string, int> type in appendedIndices.Where(t => !firstIndices.ContainsKey(t.Key)))
            {
                Assert.IsTrue(type.Value >= firstIndices.Count, type.Key);
            }
            Assert.IsTrue(appendedIndices["LibManagedDll.NamedElementForPathing"] >= firstIndices.Count, appended);
            CollectionAssert.AreEqual(firstOrder, appendedOrder.Take(firstOrder.Count).ToList());
            // Without a recorded order (a first build) the table is sorted by name, so the new type lands mid-table.
            Assert.IsTrue(Indices(sorted)["LibManagedDll.NamedElementForPathing"] < firstIndices.Count, sorted);

            StringAssert.Contains(appended, "InitTypeTables_0();");
            StringAssert.Contains(appended, "case 0: return CreateXamlType_0(typeIndex, typeName, type);");
            Assert.IsTrue(Regex.IsMatch(appended, @"return Create_\w+_SimpleClass\(typeName, type\);"), appended);
            Assert.IsTrue(Regex.IsMatch(appended, @"XamlUserType Create_\w+_SimpleClass\(string typeName, global::System.Type type\)"), appended);

            (string normal, _) = Generate(after, false, firstOrder);
            Assert.IsFalse(normal.Contains("InitTypeTables_"), normal);
            Assert.IsFalse(Regex.IsMatch(normal, @"Create_\w+_SimpleClass"), normal);
            StringAssert.Contains(normal, "switch (typeIndex)");
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
