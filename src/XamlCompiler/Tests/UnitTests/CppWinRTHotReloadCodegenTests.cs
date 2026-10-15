// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Win8Xaml.CompilerProxies;

namespace UnitTests
{
    /// <summary>
    /// C++/WinRT hot reload builds (EnableXamlHotReload). A C++ app can't take new code while it runs, so a host applies a
    /// page's new markup live only when the generated C++ is unchanged. These tests cover what keeps it unchanged for edits
    /// that don't change what the markup means, and the in-place re-initialization of x:Bind; other builds are unchanged.
    /// </summary>
    [TestClass]
    public class CppWinRTHotReloadCodegenTests
    {
        private const string PageFormat = @"<Page x:Class='LibManagedDll.BindPathParserClass'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>{0}</Page>";

        private TestHelper _testHelper;
        private DirectUISchemaContext _schema;
        private readonly List<string> _temporaryDirectories = new List<string>();

        [TestInitialize]
        public void TestInitialize()
        {
            _testHelper = new TestHelper();
            _schema = _testHelper.LoadSchema(SchemaMode.ManagedRuntime | SchemaMode.LoadUserDll);
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string directory in _temporaryDirectories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        // A live reload calls GetBindingConnector again on a loaded page: the previous Bindings stop and stop listening for
        // Loading (that subscription holds a raw pointer to them), and the new ones start once the markup has loaded.
        [TestMethod]
        public void HotReload_RootBindingsRestartOnLiveReload()
        {
            string body = "<StackPanel><TextBlock Text='{x:Bind StringProperty}'/></StackPanel>";

            string hotReload = Generate(body, hotReload: true);
            StringAssert.Contains(hotReload, "Bindings->StopTracking();");
            StringAssert.Contains(hotReload, ".Loading(_hotReloadBindingsStartToken);");
            StringAssert.Contains(hotReload, "_hotReloadBindingsStartToken = element1.Loading({&*bindings, &");
            StringAssert.Contains(hotReload, "if (element1.IsLoaded())");
            StringAssert.Contains(hotReload, "element1.DispatcherQueue().TryEnqueue([hotReloadBindings = bindings->get_weak()]()");
            StringAssert.Contains(hotReload, "liveBindings->Initialize();");

            string hotReloadHeader = Generate(body, hotReload: true, isPass1: true);
            StringAssert.Contains(hotReloadHeader, "::winrt::event_token _hotReloadBindingsStartToken{};");
            StringAssert.Contains(hotReloadHeader, "#include <winrt/Microsoft.UI.Dispatching.h>");

            string normal = Generate(body, hotReload: false);
            StringAssert.Contains(normal, "Bindings = bindings;");
            StringAssert.Contains(normal, "element1.Loading({&*bindings, &");
            Assert.IsFalse(normal.Contains("_hotReloadBindingsStartToken"), normal);
            Assert.IsFalse(Generate(body, hotReload: false, isPass1: true).Contains("_hotReloadBindingsStartToken"));
        }

        // Comments next to generated code name the element instead of its line, so inserting lines leaves the C++ unchanged.
        [TestMethod]
        public void HotReload_InsertedLinesLeaveGeneratedCodeUnchanged()
        {
            string before = "<StackPanel><TextBlock Text='{x:Bind StringProperty}'/><Button x:Name='Go' Click='OnGo'/></StackPanel>";
            string after = "\r\n\r\n<StackPanel>\r\n<Border/>\r\n<TextBlock Text='{x:Bind StringProperty}'/>\r\n\r\n<Button x:Name='Go' Click='OnGo'/></StackPanel>";

            string hotReload = Generate(before, hotReload: true);
            Assert.AreEqual(hotReload, Generate(after, hotReload: true));
            Assert.IsFalse(Regex.IsMatch(hotReload, @"\.xaml line \d+"), hotReload);
            StringAssert.Contains(hotReload, "// DummyFile.xaml TextBlock");

            Assert.AreNotEqual(Generate(before, hotReload: false), Generate(after, hotReload: false),
                "The control must demonstrate that other builds carry line numbers.");
        }

        // With stable connection IDs, declarations whose order means nothing follow connection IDs (pass 2) or names
        // (pass 1), so wrapping, moving and reordering elements, and inserting unconnected ones, leave the C++ unchanged.
        [TestMethod]
        public void HotReload_MovedAndWrappedElementsLeaveGeneratedCodeUnchanged()
        {
            string before = "<StackPanel>" +
                "<Button x:Name='First' Click='OnFirst'/>" +
                "<Button x:Name='Second' Click='OnSecond'/>" +
                "<TextBlock Text='{x:Bind StringProperty}'/>" +
                "<Button Content='Unnamed' Click='OnUnnamed'/>" +
                "</StackPanel>";
            string after = "<StackPanel>" +
                "<TextBlock Text='inserted'/>" +
                "<Border><TextBlock Text='{x:Bind StringProperty}'/></Border>" +
                "<Button x:Name='Second' Click='OnSecond'/>" +
                "<Grid><Button Content='Unnamed' Click='OnUnnamed'/><Button x:Name='First' Click='OnFirst'/></Grid>" +
                "</StackPanel>";

            string output = CreateTemporaryDirectory();
            string beforeCode = GenerateWithStableIds(output, before);
            string afterCode = GenerateWithStableIds(output, after);
            Assert.AreEqual(beforeCode, afterCode);
            Assert.AreEqual(
                Generate(before, hotReload: true, isPass1: true),
                Generate(after, hotReload: true, isPass1: true));

            Assert.AreNotEqual(Generate(before, hotReload: false), Generate(after, hotReload: false),
                "The control must demonstrate that other builds follow document order.");
            Assert.AreNotEqual(
                Generate(before, hotReload: false, isPass1: true),
                Generate(after, hotReload: false, isPass1: true));
        }

        private string Generate(string body, bool hotReload, bool isPass1 = false, HotReloadConnectionIdLedgerSession ledger = null)
        {
            var context = new CodeGeneratorProjectContext(new Version(KnownVersions.Latest), "CppWinRTHotReloadCodegen");
            context.IsPass1 = isPass1;
            context.ProjectInfo.IsHotReloadBuild = hotReload;
            context.ProjectInfo.IsCppHotReloadCodeGen = hotReload;
            // C++ code generation looks up the class's header (GenerateCodeBehind sets it again for pass 2).
            context.ProjectInfo.ClassToHeaderFileMap = new Dictionary<string, string> { { "LibManagedDll.BindPathParserClass", "BindPathParserClass.h" } };
            List<FileNameAndContentPair> pairs = _testHelper.GenerateCodeBehind(
                context, new List<string> { String.Format(PageFormat, body) }, _schema, CodeGenLanguage.CppWinRT, null, ledger);
            return String.Join("\r\n", pairs.Select(pair => pair.Contents));
        }

        private string GenerateWithStableIds(string output, string body)
        {
            using (var session = new HotReloadConnectionIdLedgerSession(output, 1000))
            {
                session.BeginPublication();
                string code = Generate(body, hotReload: true, ledger: session);
                session.Commit();
                return code;
            }
        }

        private string CreateTemporaryDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "XamlCompilerCppHotReloadTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _temporaryDirectories.Add(directory);
            return directory;
        }
    }
}
