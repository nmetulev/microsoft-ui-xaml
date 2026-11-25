// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
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

        [TestMethod]
        public void HotReloadService_ImagesWithReorderedTypesResolveTypesByName()
        {
            // The compiler service replaces the project's assembly with each newer build, at the same location; a newer
            // build can give the same types other metadata tokens.
            byte[] first = CompileImage("namespace App { public class Contact { } public class Message { } }");
            byte[] second = CompileImage("namespace App { public class Message { } public class Contact { } }");
            object universe = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.Lmr.XamlTypeUniverse").CreateInstance(new object[] { true });
            MethodInfo load = universe.GetType().GetMethod("LoadAssemblyFromImage");
            MethodInfo unload = universe.GetType().GetMethod("UnloadAssembly");
            string location = Path.Combine(Path.GetTempPath(), "App.dll");

            var firstBuild = (Assembly)load.Invoke(universe, new object[] { first, location });
            var cache = new Dictionary<Type, string> { { firstBuild.GetType("App.Contact"), "Contact" } };
            unload.Invoke(universe, new object[] { firstBuild });
            var secondBuild = (Assembly)load.Invoke(universe, new object[] { second, location });

            // Types of the first build that something still holds can create more of them; they don't stand in for the
            // second build's types, by token or by equality.
            firstBuild.GetTypes();
            Assert.AreEqual("App.Contact", secondBuild.GetType("App.Contact").FullName);
            Assert.AreEqual("App.Message", secondBuild.GetType("App.Message").FullName);
            Assert.IsFalse(cache.ContainsKey(secondBuild.GetType("App.Message")));
        }

        [TestMethod]
        public void HotReloadService_KeptMembersSurviveADesignTimeBuild()
        {
            string outputFolder = Path.Combine(Path.GetTempPath(), "HotReloadTests", Guid.NewGuid().ToString("N"));
            string page = Path.Combine(outputFolder, "Views", "MainPage.g.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(page));
            const string built = "namespace App\r\n{\r\n    partial class MainPage\r\n    {\r\n        private int obj4;\r\n    }\r\n}\r\n";
            const string recompiled = "namespace App\r\n{\r\n    partial class MainPage\r\n    {\r\n    }\r\n}\r\n";
            try
            {
                // The property-on build copies what it compiled into the app.
                File.WriteAllText(page, built);
                var history = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.XamlHotReloadHistory");
                history.GetStaticMethod("WriteGeneratedText").Invoke(null, new object[] { outputFolder, page });

                // A design-time build (an editor's, into the same obj) empties the page's generated code.
                File.Move(page, page + ".backup");
                File.WriteAllText(page, string.Empty);

                object serviceHistory = history.CreateInstance(new object[] { outputFolder });
                string appHas = (string)serviceHistory.GetType().GetMethod("GeneratedText").Invoke(serviceHistory, new object[] { page });
                MethodInfo addRemovedMembers = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.KeptMembers").GetStaticMethod("AddRemovedMembers");
                StringAssert.Contains((string)addRemovedMembers.Invoke(null, new object[] { appHas, recompiled }), "private int obj4;");
            }
            finally
            {
                Directory.Delete(outputFolder, recursive: true);
            }
        }

        [TestMethod]
        public void HotReloadService_DesignTimeInputsGetAnError()
        {
            // What a design-time build writes to input.json.
            Type inputsType = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.XamlCompilerService").GetMethod("Compile").DeclaringType
                .GetConstructors().Single().GetParameters()[0].ParameterType;
            object inputs = Activator.CreateInstance(inputsType);
            inputsType.GetProperty("IsPass1").SetValue(inputs, true);
            inputsType.GetProperty("CompileMode").SetValue(inputs, "DesignTimeBuild");

            object service = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.XamlCompilerService").CreateInstance(new object[] { inputs, Path.GetTempPath() });
            object result = service.GetType().GetMethod("Compile").Invoke(service, new object[] { 2, new[] { "MainPage.xaml" }, null });

            Assert.IsFalse((bool)result.GetType().GetProperty("Success").GetValue(result));
            var diagnostics = (System.Collections.IList)result.GetType().GetProperty("Diagnostics").GetValue(result);
            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains((string)diagnostics[0].GetType().GetProperty("Message").GetValue(diagnostics[0]), "design-time build");
        }

        [TestMethod]
        public void HotReloadService_KeepsTheBuildsXamlTypeInfoMemberOrder()
        {
            // A build that regenerated some pages can order XamlTypeInfo's members differently from a compile of every page;
            // the service keeps the build's order, so the accessors are numbered as in the app.
            const string xaml = @"
<Page x:Class='MyStuff.MyPage' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:lib='using:LibManagedDll'>
    <Page.Resources>
        <lib:MyStringListClass x:Key='a' MyListCount='1' />
        <lib:MyDictionaryClass x:Key='b' MyDictionaryCount='2' />
    </Page.Resources>
</Page>";
            DirectUISchemaContext schema = _testHelper.LoadSchema(SchemaMode.ManagedRuntime | SchemaMode.LoadUserDll);
            object schemaInfo = _testHelper.CollectTypes(xaml, schema).SchemaInfo.Instance;
            MethodInfo memberKey = schemaInfo.GetType().GetMethod("MemberKey", BindingFlags.NonPublic | BindingFlags.Static);
            Func<List<string>> order = () => ((System.Collections.IEnumerable)schemaInfo.GetType().GetProperty("UserMemberInfo").GetValue(schemaInfo))
                .Cast<object>().Select(m => (string)memberKey.Invoke(null, new[] { m })).ToList();
            List<string> found = order();
            CollectionAssert.IsSubsetOf(new[] { "LibManagedDll.MyStringListClass.MyListCount", "LibManagedDll.MyDictionaryClass.MyDictionaryCount" }, found);

            // The build's order: the same members, reversed; and one the build didn't have (it goes last).
            var build = Enumerable.Reverse(found.Skip(1)).ToList();
            schemaInfo.GetType().GetMethod("KeepMemberOrder", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(schemaInfo, new object[] { build });

            CollectionAssert.AreEqual(build.Concat(found.Take(1)).ToList(), order());
            var context = new CodeGeneratorProjectContext(new Version(KnownVersions.Latest), "HotReloadMemberOrder");
            string code = string.Join(Environment.NewLine, _testHelper.GenerateTypeInfo(false, new XamlSchemaCodeInfo(schemaInfo), context.ProjectInfo,
                new ClassName("MyStuff.App"), CodeGenLanguage.CSharp).Select(f => f.Contents));
            List<string> kept = order();
            for (int i = 0; i < kept.Count; i++)
            {
                string name = kept[i].Substring(kept[i].LastIndexOf('.') + 1);
                Assert.IsTrue(Regex.IsMatch(code, $@"private object get_{i}_\w*_{name}\("), $"accessor {i} should be {kept[i]}");
            }

            // A type lists its members (AddMemberName) in the same order.
            string firstType = kept[0].Substring(0, kept[0].LastIndexOf('.'));
            List<string> added = Regex.Matches(code, @"AddMemberName\(""(\w+)""\)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            List<string> typeMembers = kept.Where(k => k.StartsWith(firstType + ".", StringComparison.Ordinal)).Select(k => k.Substring(firstType.Length + 1))
                .Where(n => added.Count(a => a == n) == 1).ToList();
            Assert.IsTrue(typeMembers.Count > 1, string.Join(", ", kept));
            CollectionAssert.AreEqual(typeMembers, added.Where(typeMembers.Contains).ToList());
        }

        [TestMethod]
        public void HotReloadService_ARestartedServiceContinuesTheSession()
        {
            string outputFolder = Path.Combine(Path.GetTempPath(), "HotReloadTests", Guid.NewGuid().ToString("N"));
            string page = Path.Combine(outputFolder, "MainPage.g.cs");
            var history = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.XamlHotReloadHistory");
            string folder = Path.Combine(outputFolder, "XamlHotReload");
            Action build = () =>
            {
                history.GetStaticMethod("WriteConnectionIds").Invoke(null, new object[] { folder, "App.MainPage", new Dictionary<string, int> { { "/Grid", 1 }, { "/Button", 2 } } });
                history.GetStaticMethod("WriteTypeOrder").Invoke(null, new object[] { folder, new[] { "App.MainPage" } });
                history.GetStaticMethod("WriteMemberOrder").Invoke(null, new object[] { folder, new[] { "App.MainPage.Title" } });
                File.WriteAllText(page, "build code");
                history.GetStaticMethod("WriteGeneratedText").Invoke(null, new object[] { outputFolder, page });
                history.GetStaticMethod("WriteBaseline").Invoke(null, new object[] { folder });
            };
            Func<object, string> state = h =>
            {
                Type t = h.GetType();
                var ids = (Dictionary<string, int>)t.GetMethod("ConnectionIds").Invoke(h, new object[] { "App.MainPage" });
                var known = (HashSet<string>)t.GetProperty("KnownTypeInfo").GetValue(h);
                return string.Join(" | ",
                    string.Join(",", ids.OrderBy(p => p.Value).Select(p => p.Key + "=" + p.Value)),
                    string.Join(",", (IList<string>)t.GetProperty("TypeOrder").GetValue(h)),
                    string.Join(",", (IList<string>)t.GetProperty("MemberOrder").GetValue(h)),
                    (string)t.GetMethod("GeneratedText").Invoke(h, new object[] { page }),
                    known == null ? "(no type info)" : string.Join(",", known.OrderBy(k => k)),
                    string.Join(",", (IList<string>)t.GetProperty("AddedPages").GetValue(h)));
            };
            try
            {
                Directory.CreateDirectory(outputFolder);
                build();
                string built = state(history.CreateInstance(new object[] { outputFolder }));

                // Service A: a new element, a new type and member, kept members, regenerated XamlTypeInfo, a new page.
                object a = history.CreateInstance(new object[] { outputFolder });
                Type type = a.GetType();
                ((Dictionary<string, int>)type.GetMethod("ConnectionIds").Invoke(a, new object[] { "App.MainPage" }))["/TextBlock"] = 3;
                type.GetProperty("TypeOrder").SetValue(a, new List<string> { "App.MainPage", "App.Badge" });
                type.GetProperty("MemberOrder").SetValue(a, new List<string> { "App.MainPage.Title", "App.Badge.Label" });
                type.GetMethod("SetGeneratedText").Invoke(a, new object[] { page, "session code" });
                type.GetProperty("KnownTypeInfo").SetValue(a, new HashSet<string> { "App.MainPage", "App.Badge" });
                type.GetProperty("AddedPages").SetValue(a, new List<string> { "Badge.xaml" });
                type.GetMethod("Save").Invoke(a, null);
                string session = state(a);
                Assert.AreEqual("/Grid=1,/Button=2,/TextBlock=3 | App.MainPage,App.Badge | App.MainPage.Title,App.Badge.Label | session code | App.Badge,App.MainPage | Badge.xaml", session);

                // Service B, started on the same build after A stopped, carries on A's session.
                Assert.AreEqual(session, state(history.CreateInstance(new object[] { outputFolder })));

                // A new build starts a new session, in a new service and in A.
                build();
                Assert.AreEqual(built, state(history.CreateInstance(new object[] { outputFolder })));
                type.GetMethod("Refresh").Invoke(a, null);
                Assert.AreEqual(built, state(a));
            }
            finally
            {
                Directory.Delete(outputFolder, recursive: true);
            }
        }

        [TestMethod]
        public void HotReloadService_ABuildWithoutThePropertyLeavesTheSessionAlone()
        {
            string outputFolder = Path.Combine(Path.GetTempPath(), "HotReloadTests", Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(outputFolder, "XamlHotReload");
            string page = Path.Combine(outputFolder, "MainPage.g.cs");
            var history = new ProxyHelper("Microsoft.UI.Xaml.Markup.Compiler.XamlHotReloadHistory");
            MethodInfo differs = history.GetStaticMethod("DoesCodegenDiffer");
            MethodInfo setCodegen = history.GetStaticMethod("SetCodegen");
            Func<string> snapshot = () => string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f)
                .Select(f => f.Substring(folder.Length) + "=" + File.ReadAllText(f) + "@" + File.GetLastWriteTimeUtc(f).Ticks));
            try
            {
                // A build with the property, then a compile in its session.
                Directory.CreateDirectory(outputFolder);
                Assert.IsTrue((bool)differs.Invoke(null, new object[] { outputFolder, true }), "the first build with the property regenerates every page");
                history.GetStaticMethod("WriteTypeOrder").Invoke(null, new object[] { folder, new[] { "App.MainPage" } });
                history.GetStaticMethod("WriteMemberOrder").Invoke(null, new object[] { folder, new[] { "App.MainPage.Title" } });
                File.WriteAllText(page, "build code");
                history.GetStaticMethod("WriteGeneratedText").Invoke(null, new object[] { outputFolder, page });
                history.GetStaticMethod("WriteBaseline").Invoke(null, new object[] { folder });
                setCodegen.Invoke(null, new object[] { outputFolder, true });
                object service = history.CreateInstance(new object[] { outputFolder });
                service.GetType().GetMethod("SetGeneratedText").Invoke(service, new object[] { page, "session code" });
                service.GetType().GetMethod("Save").Invoke(service, null);
                Assert.IsFalse((bool)differs.Invoke(null, new object[] { outputFolder, true }));
                string before = snapshot();

                // A build without the property (the user's, or an editor's) regenerates every page but leaves the session.
                Assert.IsTrue((bool)differs.Invoke(null, new object[] { outputFolder, false }));
                setCodegen.Invoke(null, new object[] { outputFolder, false });
                Assert.AreEqual(before, snapshot());
                Assert.IsFalse((bool)differs.Invoke(null, new object[] { outputFolder, false }), "the next build without the property doesn't");
                object restarted = history.CreateInstance(new object[] { outputFolder });
                Assert.AreEqual("session code", restarted.GetType().GetMethod("GeneratedText").Invoke(restarted, new object[] { page }));

                // The next build with the property regenerates every page again.
                Assert.IsTrue((bool)differs.Invoke(null, new object[] { outputFolder, true }));
            }
            finally
            {
                Directory.Delete(outputFolder, recursive: true);
            }
        }

        private static byte[] CompileImage(string code)
        {
            CSharpCompilation compilation = CSharpCompilation.Create("App", new[] { CSharpSyntaxTree.ParseText(code) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var stream = new MemoryStream())
            {
                Assert.IsTrue(compilation.Emit(stream).Success);
                return stream.ToArray();
            }
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
