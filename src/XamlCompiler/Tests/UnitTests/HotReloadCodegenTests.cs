// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Win8Xaml.CompilerProxies;

namespace UnitTests
{
    // EnableXamlHotReloadCodegen: generated code a hot reload tool can apply while the app runs.
    [TestClass]
    public class HotReloadCodegenTests
    {
        TestHelper _testHelper;

        [TestInitialize]
        public void SchemaInit()
        {
            _testHelper = new TestHelper();
        }

        [TestMethod]
        public void HotReloadCodegen_ConnectionIdsSurviveAnInsertedElement()
        {
            const string page = @"
<Page xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='MyStuff.MyPage'>
    <StackPanel>
        {0}
        <Button x:Name='first' Click='OnFirst' />
        <Button x:Name='second' Click='OnSecond' />
    </StackPanel>
</Page>";
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> built = ConnectionIds(string.Format(page, ""), ids, keepRecordedIds: false);
            Dictionary<string, int> recompiled = ConnectionIds(string.Format(page, "<Button x:Name='inserted' Click='OnInserted' />"), ids, keepRecordedIds: true);

            // A build numbers elements in document order; the service keeps those numbers and gives the new one the next.
            Assert.AreEqual(built["first"], recompiled["first"]);
            Assert.AreEqual(built["second"], recompiled["second"]);
            Assert.AreEqual(built.Values.Max() + 1, recompiled["inserted"]);
        }

        [TestMethod]
        public void HotReloadCodegen_CSharpRestartsBindingsAndHasNoStaticInitializers()
        {
            const string xaml = @"
<Page x:Class='LibManagedDll.BindPathParserClass' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
    <TextBlock Text='{x:Bind StringField}' />
</Page>";
            DirectUISchemaContext schema = _testHelper.LoadSchema(SchemaMode.ManagedRuntime | SchemaMode.LoadUserDll);
            foreach (bool hotReload in new[] { false, true })
            {
                string pass1 = Generate(xaml, schema, isPass1: true, hotReload);
                string pass2 = Generate(xaml, schema, isPass1: false, hotReload);

                Assert.AreEqual(hotReload, pass1.Contains("this.Bindings?.StopTracking();"), pass1);
                Assert.AreEqual(hotReload, pass1.Contains("this.Bindings?.Update();"), pass1);
                Assert.AreEqual(hotReload, pass2.Contains("this.dataRoot?.Bindings, this"), pass2);
                Assert.AreEqual(!hotReload, Regex.IsMatch(pass2, @"private static bool \w+ = false;"), pass2);
            }
        }

        [TestMethod]
        public void HotReloadCodegen_RemovedMembersAreKept()
        {
            const string before = @"namespace App
{
    partial class Page
    {
        private static bool isobj5Disabled = false;
        private int obj5;
        private void Update_obj5(int value) { obj5 = value; }
        private int ProcessBindings(object item, out int nextPhase) { nextPhase = 2; return 1; }
        private class Page_obj5_Bindings
        {
            private int field = 3;
            public Page_obj5_Bindings() { field = 4; }
            public string Name { get { return ""x""; } set { } }
        }
        private int kept;
    }
}
";
            const string after = @"namespace App
{
    partial class Page
    {
        private int kept;
    }
}
";
            MethodInfo addRemovedMembers = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.KeptMembers").GetStaticMethod("AddRemovedMembers");
            string result = (string)addRemovedMembers.Invoke(null, new object[] { before, after });

            StringAssert.Contains(result, "private static bool isobj5Disabled;");
            StringAssert.Contains(result, "private int obj5;");
            StringAssert.Contains(result, "private void Update_obj5(int value)\r\n        {\r\n        }");
            StringAssert.Contains(result, "nextPhase = -1;\r\n            return default;");
            StringAssert.Contains(result, "private class Page_obj5_Bindings");
            StringAssert.Contains(result, "private int field;");
            StringAssert.Contains(result, "get { return default; } set { }");
            Assert.AreEqual(1, Regex.Matches(result, "private int kept;").Count, result);
            Assert.IsFalse(result.Contains("= 3") || result.Contains("field = 4") || result.Contains("\"x\""), result);

            // A later compile keeps them again from the text that kept them, and the new code wins over a kept member.
            string next = (string)addRemovedMembers.Invoke(null, new object[] { result, after.Replace("private int kept;", "private int kept;\r\n        private long obj5;") });
            StringAssert.Contains(next, "private void Update_obj5(int value)");
            StringAssert.Contains(next, "private long obj5;");
            Assert.IsFalse(next.Contains("private int obj5;"), next);
        }

        private string Generate(string xaml, DirectUISchemaContext schema, bool isPass1, bool hotReload)
        {
            var context = new CodeGeneratorProjectContext(new Version(KnownVersions.Latest), "HotReloadCodegen") { IsPass1 = isPass1 };
            context.ProjectInfo.Instance.GetType().GetProperty("IsHotReloadCodegen").SetValue(context.ProjectInfo.Instance, hotReload);
            // As in a Debug build: x:Bind diagnostics add a static flag per binding.
            context.ProjectInfo.Instance.GetType().GetProperty("ShouldGenerateDisableXBind").SetValue(context.ProjectInfo.Instance, true);
            return string.Join(Environment.NewLine, _testHelper.GenerateCodeBehind(context, new List<string> { xaml }, schema, CodeGenLanguage.CSharp).Select(f => f.Contents));
        }

        // Harvests the page with recorded connection ID keys, and returns each element's ID by its x:Name or handler.
        private Dictionary<string, int> ConnectionIds(string xaml, Dictionary<string, int> ids, bool keepRecordedIds)
        {
            CompilerDomRootToken dom = _testHelper.LoadXamlDom(xaml, SchemaMode.ManagedRuntime);
            XamlClassCodeInfo classInfo = _testHelper.HarvestClassCodeInfo(".", dom, false, false);
            object keys = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.ConnectionIdKeys").CreateInstance(new object[] { ids, keepRecordedIds });
            classInfo.Instance.GetType().GetProperty("ConnectionIdKeys", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(classInfo.Instance, keys);
            XamlFileCodeInfo fileInfo = _testHelper.HarvestFileCodeInfo(".", false, classInfo, dom);
            classInfo.AddXamlFileInfo(fileInfo);
            string rewritten = new XamlConnectionIdRewriter().Parse(xaml, classInfo, fileInfo);

            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return XDocument.Parse(rewritten).Descendants().Where(e => e.Attribute(x + "ConnectionId") != null && e.Attribute(x + "Name") != null)
                .ToDictionary(e => (string)e.Attribute(x + "Name"), e => (int)e.Attribute(x + "ConnectionId"));
        }
    }
}
