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
            Assert.IsFalse(normal.Contains("EnsureBindingsTracking"), normal);
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
