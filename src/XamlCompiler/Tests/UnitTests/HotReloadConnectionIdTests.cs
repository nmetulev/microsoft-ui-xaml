// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Win8Xaml.CompilerProxies;

namespace UnitTests
{
    [TestClass]
    public class HotReloadConnectionIdTests
    {
        private const string Header = @"
<Page
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    x:Class='HotReloadTests.MainPage'>
    <StackPanel>";

        private const string Footer = @"
    </StackPanel>
</Page>";

        private TestHelper testHelper;
        private DirectUISchemaContext schema;
        private readonly List<string> temporaryDirectories = new List<string>();

        [TestInitialize]
        public void Initialize()
        {
            this.testHelper = new TestHelper();
            this.schema = this.testHelper.LoadSchema(SchemaMode.ManagedRuntime);
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string directory in this.temporaryDirectories)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [TestMethod]
        public void ExperimentalMode_AppendsForInsertionsWhileCurrentModeRenumbers()
        {
            string baseline = Page(
                "<Button x:Name='FirstButton' Click='OnClick' />",
                "<Button x:Name='SecondButton' Click='OnClick' />",
                "<ListView x:Name='FirstList' />",
                "<ListView x:Name='SecondList' />");
            string inserted = Page(
                "<Button x:Name='InsertedButton' Click='OnClick' />",
                "<Button x:Name='FirstButton' Click='OnClick' />",
                "<Button x:Name='SecondButton' Click='OnClick' />",
                "<ListView x:Name='FirstList' />",
                "<ListView x:Name='SecondList' />");

            CompilationResult currentBaseline = CompileCurrentMode(baseline);
            CompilationResult currentInserted = CompileCurrentMode(inserted);
            Assert.AreNotEqual(
                currentBaseline.NamedIds["FirstButton"],
                currentInserted.NamedIds["FirstButton"],
                "The control must demonstrate the existing source-order renumbering.");

            string output = CreateTemporaryDirectory();
            CompilationResult experimentalBaseline = CompileExperimental(output, baseline);
            CompilationResult experimentalInserted = CompileExperimental(output, inserted);

            AssertExistingIdsEqual(experimentalBaseline, experimentalInserted);
            Assert.IsTrue(
                experimentalInserted.NamedIds["InsertedButton"] >
                experimentalBaseline.NamedIds.Values.Max());
            StringAssert.Contains(experimentalInserted.Manifest, "\"ledgerState\":\"continued\"");
        }

        [TestMethod]
        public void ExperimentalMode_RetiresRemovedIdsAndPreservesNamedMoves()
        {
            string output = CreateTemporaryDirectory();
            CompilationResult baseline = CompileExperimental(
                output,
                Page(
                    "<Button x:Name='FirstButton' Click='OnClick' />",
                    "<Button x:Name='SecondButton' Click='OnClick' />"));
            CompilationResult removedAndMoved = CompileExperimental(
                output,
                Page(
                    "<Button x:Name='SecondButton' Click='OnClick' />",
                    "<Button x:Name='ReplacementButton' Click='OnClick' />"));

            Assert.AreEqual(
                baseline.NamedIds["SecondButton"],
                removedAndMoved.NamedIds["SecondButton"]);
            Assert.AreNotEqual(
                baseline.NamedIds["FirstButton"],
                removedAndMoved.NamedIds["ReplacementButton"]);
            Assert.IsTrue(
                removedAndMoved.NamedIds["ReplacementButton"] >
                baseline.NamedIds.Values.Max());
            CollectionAssert.Contains(
                GetRetiredIds(removedAndMoved.Ledger),
                baseline.NamedIds["FirstButton"]);

            CompilationResult allocatedAgain = CompileExperimental(
                output,
                Page(
                    "<Button x:Name='SecondButton' Click='OnClick' />",
                    "<Button x:Name='AnotherButton' Click='OnClick' />"));
            Assert.IsTrue(
                allocatedAgain.NamedIds["AnotherButton"] >
                removedAndMoved.NamedIds.Values.Max());
            CollectionAssert.Contains(
                GetRetiredIds(allocatedAgain.Ledger),
                baseline.NamedIds["FirstButton"]);
        }

        [TestMethod]
        public void ExperimentalMode_FailsClosedForIdenticalUnnamedSiblings()
        {
            string output = CreateTemporaryDirectory();
            string xaml = Page(
                "<Button Click='OnClick' />",
                "<Button Click='OnClick' />");

            CompilationResult first = CompileExperimental(output, xaml);
            CompilationResult second = CompileExperimental(output, xaml);

            Assert.AreEqual(first.ConnectionIds.Count, second.ConnectionIds.Count);
            Assert.AreEqual(2, Regex.Matches(second.Manifest, "\"identityStatus\":\"ambiguous\"").Count);
            Assert.IsTrue(second.ConnectionIds.Min() > first.ConnectionIds.Max());
        }

        [TestMethod]
        public void ExperimentalMode_RetiresAuthoredIdentityWhenExpectedTypeChanges()
        {
            string output = CreateTemporaryDirectory();
            CompilationResult button = CompileExperimental(
                output,
                Page("<Button x:Name='Target' />"));
            CompilationResult textBlock = CompileExperimental(
                output,
                Page("<TextBlock x:Name='Target' />"));

            Assert.AreNotEqual(
                button.NamedIds["Target"],
                textBlock.NamedIds["Target"]);
            Assert.IsTrue(
                textBlock.NamedIds["Target"] > button.NamedIds["Target"]);
            CollectionAssert.Contains(
                GetRetiredIds(textBlock.Ledger),
                button.NamedIds["Target"]);
            StringAssert.Contains(textBlock.Manifest, "TextBlock");
        }

        [TestMethod]
        public void ExperimentalMode_TracksTemplateBindingsAndMultiXamlClass()
        {
            string output = CreateTemporaryDirectory();
            string primary = @"
<Page
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    x:Class='HotReloadTests.MainPage'>
    <Page.Resources>
        <DataTemplate x:Key='ItemTemplate' x:DataType='x:String'>
            <TextBlock Text='{x:Bind}' />
        </DataTemplate>
    </Page.Resources>
    <ListView x:Name='Items' ItemTemplate='{StaticResource ItemTemplate}' />
</Page>";
            string secondary = Page("<Button x:Name='SecondaryButton' Click='OnClick' />");

            CompilationResult baseline = CompileExperimental(
                output,
                new XamlSource("MainPage.xaml", primary),
                new XamlSource("MainPage.Mobile.xaml", secondary));
            CompilationResult inserted = CompileExperimental(
                output,
                new XamlSource(
                    "MainPage.xaml",
                    primary.Replace(
                        "<ListView x:Name='Items'",
                        "<Button x:Name='NewButton' Click='OnClick' />\r\n    <ListView x:Name='Items'")),
                new XamlSource("MainPage.Mobile.xaml", secondary));

            Assert.AreEqual(baseline.NamedIds["Items"], inserted.NamedIds["Items"]);
            Assert.AreEqual(
                baseline.NamedIds["SecondaryButton"],
                inserted.NamedIds["SecondaryButton"]);
            Assert.IsTrue(inserted.NamedIds["NewButton"] > baseline.ConnectionIds.Max());
            StringAssert.Contains(inserted.Manifest, "\"identityKind\":\"x:Key\"");
            StringAssert.Contains(inserted.Manifest, "\"kind\":\"property\"");
            StringAssert.Contains(inserted.Manifest, "\"path\":\"\"");
            StringAssert.Contains(inserted.Manifest, "MainPage.Mobile.xaml");
        }

        [TestMethod]
        public void ExperimentalMode_CleanBuildIsDeterministicAndDeclaresNewLineage()
        {
            string xaml = Page(
                "<Button x:Name='FirstButton' Click='OnClick' />",
                "<Button x:Name='SecondButton' Click='OnClick' />");
            CompilationResult firstClean = CompileExperimental(CreateTemporaryDirectory(), xaml);
            CompilationResult secondClean = CompileExperimental(CreateTemporaryDirectory(), xaml);

            CollectionAssert.AreEquivalent(
                firstClean.NamedIds.ToList(),
                secondClean.NamedIds.ToList());
            Assert.AreEqual(GetLineage(firstClean.Manifest), GetLineage(secondClean.Manifest));
            StringAssert.Contains(firstClean.Manifest, "\"ledgerState\":\"new-ledger\"");

            CompilationResult reorderedClean = CompileExperimental(
                CreateTemporaryDirectory(),
                Page(
                    "<Button x:Name='SecondButton' Click='OnClick' />",
                    "<Button x:Name='FirstButton' Click='OnClick' />"));
            Assert.AreNotEqual(
                GetLineage(firstClean.Manifest),
                GetLineage(reorderedClean.Manifest));
        }

        [TestMethod]
        public void ExperimentalMode_AssignsAppendOnlyIdBeforeOlderDeeperElement()
        {
            string output = CreateTemporaryDirectory();
            CompilationResult baseline = CompileExperimental(
                output,
                Page(
                    "<Grid><Button x:Name='NestedLeaf' Click='OnClick' /></Grid>"));
            CompilationResult inserted = CompileExperimental(
                output,
                Page(
                    "<Grid><Button x:Name='NestedLeaf' Click='OnClick' /></Grid>",
                    "<Button x:Name='ShallowTail' Click='OnClick' />"));

            int oldId = baseline.NamedIds["NestedLeaf"];
            int newId = inserted.NamedIds["ShallowTail"];
            Assert.AreEqual(oldId, inserted.NamedIds["NestedLeaf"]);
            Assert.IsTrue(newId > oldId);
            CollectionAssert.AreEqual(
                new[] { baseline.HarvestOrderIds[0], newId, oldId },
                inserted.HarvestOrderIds);
            StringAssert.Contains(inserted.Manifest, "\"allocationStatus\":\"reused\"");
            StringAssert.Contains(inserted.Manifest, "\"allocationStatus\":\"new\"");
        }

        [TestMethod]
        public void ExperimentalMode_ScopesDuplicateNamesAndUidsByTemplateOwner()
        {
            string output = CreateTemporaryDirectory();
            string xaml = @"
<Page
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    x:Class='HotReloadTests.MainPage'>
    <Page.Resources>
        <DataTemplate x:Key='FirstTemplate'>
            <StackPanel>
                <TextBlock x:Name='Title' />
                <Button x:Uid='SharedUid' Click='OnClick' />
            </StackPanel>
        </DataTemplate>
        <DataTemplate x:Key='SecondTemplate'>
            <StackPanel>
                <TextBlock x:Name='Title' />
                <Button x:Uid='SharedUid' Click='OnClick' />
            </StackPanel>
        </DataTemplate>
    </Page.Resources>
</Page>";

            CompilationResult result = CompileExperimental(output, xaml);

            Assert.AreEqual(2, Regex.Matches(result.Manifest, "\"xName\":\"Title\"").Count);
            Assert.AreEqual(2, Regex.Matches(result.Manifest, "\"xUid\":\"SharedUid\"").Count);
            Assert.AreEqual(0, Regex.Matches(result.Manifest, "\"identityStatus\":\"ambiguous\"").Count);
            StringAssert.Contains(result.Manifest, "FirstTemplate");
            StringAssert.Contains(result.Manifest, "SecondTemplate");
        }

        [TestMethod]
        public void ExperimentalMode_CleansRemovedClassesAndOptionOffArtifacts()
        {
            string output = CreateTemporaryDirectory();
            CompilationResult first = CompileExperimental(
                output,
                Page("<Button x:Name='FirstButton' Click='OnClick' />"));
            string sidecarDirectory = Path.Combine(output, "XamlHotReload");

            Directory.Delete(sidecarDirectory, recursive: true);
            CompilationResult clean = CompileExperimental(
                output,
                Page("<Button x:Name='FirstButton' Click='OnClick' />"));
            Assert.AreEqual(GetLineage(first.Manifest), GetLineage(clean.Manifest));
            StringAssert.Contains(clean.Manifest, "\"ledgerState\":\"new-ledger\"");

            CompileExperimental(
                output,
                Page("<Button x:Name='OtherButton' Click='OnClick' />")
                    .Replace("HotReloadTests.MainPage", "HotReloadTests.OtherPage"));
            Assert.AreEqual(
                1,
                Directory.GetFiles(sidecarDirectory, "*.xaml.connids.json").Length);
            Assert.AreEqual(
                1,
                Directory.GetFiles(sidecarDirectory, "*.xaml.hotreload.json").Length);
            StringAssert.Contains(
                File.ReadAllText(Directory.GetFiles(sidecarDirectory, "*.xaml.hotreload.json").Single()),
                "HotReloadTests.OtherPage");

            HotReloadConnectionIdLedgerSession.Clean(output);
            Assert.AreEqual(
                0,
                Directory.GetFiles(sidecarDirectory, "*.json").Length);
            Assert.IsFalse(File.Exists(Path.Combine(sidecarDirectory, "connection-ids.pending")));
        }

        [TestMethod]
        public void ExperimentalMode_UsesBoundedExclusiveProjectLock()
        {
            string output = CreateTemporaryDirectory();
            using (var first = new HotReloadConnectionIdLedgerSession(output, 1000))
            {
                TargetInvocationException exception = null;
                try
                {
                    using (var unexpected = new HotReloadConnectionIdLedgerSession(output, 50))
                    {
                    }
                }
                catch (TargetInvocationException ex)
                {
                    exception = ex;
                }
                Assert.IsNotNull(exception);
                Assert.IsInstanceOfType(exception.InnerException, typeof(IOException));
                StringAssert.Contains(exception.InnerException.Message, "Timed out");
            }

            using (var afterRelease = new HotReloadConnectionIdLedgerSession(output, 1000))
            {
            }

            using (var interrupted = new HotReloadConnectionIdLedgerSession(output, 1000))
            {
                interrupted.BeginPublication();
            }
            string pendingMarker = Path.Combine(
                output,
                "XamlHotReload",
                "connection-ids.pending");
            Assert.IsTrue(File.Exists(pendingMarker));
            CompileExperimental(
                output,
                Page("<Button x:Name='Recovered' />"));
            Assert.IsFalse(File.Exists(pendingMarker));
        }

        private CompilationResult CompileCurrentMode(string xaml)
        {
            string output = CreateTemporaryDirectory();
            CompilerDomRootToken domRoot = this.testHelper.LoadXamlDom(xaml, this.schema);
            XamlClassCodeInfo classInfo =
                this.testHelper.HarvestClassCodeInfo(output, domRoot, false, false);
            XamlFileCodeInfo fileInfo =
                this.testHelper.HarvestFileCodeInfo(output, false, classInfo, domRoot);
            fileInfo.ApparentRelativePath = "MainPage.xaml";
            classInfo.AddXamlFileInfo(fileInfo);
            string rewritten = new XamlConnectionIdRewriter().Parse(xaml, classInfo, fileInfo);
            return CompilationResult.From(
                new[] { rewritten },
                fileInfo.ConnectionIdElements.Select(element => element.ConnectionId),
                null,
                null);
        }

        private CompilationResult CompileExperimental(string output, string xaml)
        {
            return CompileExperimental(output, new XamlSource("MainPage.xaml", xaml));
        }

        private CompilationResult CompileExperimental(string output, params XamlSource[] sources)
        {
            List<string> rewrittenFiles = new List<string>();
            List<int> harvestOrderIds = new List<int>();
            XamlClassCodeInfo classInfo = null;

            using (var session = new HotReloadConnectionIdLedgerSession(output, 1000))
            {
                session.BeginPublication();
                foreach (XamlSource source in sources)
                {
                    string sourcePath = Path.Combine(output, source.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
                    File.WriteAllText(sourcePath, source.Xaml);
                    CompilerDomRootToken domRoot =
                        this.testHelper.LoadXamlDom(source.Xaml, this.schema);
                    if (classInfo == null)
                    {
                        classInfo = this.testHelper.HarvestClassCodeInfo(
                            output,
                            domRoot,
                            false,
                            false);
                        classInfo.EnableHotReloadConnectionIds(session);
                    }

                    classInfo.BeginHotReloadXamlFile(source.Path, domRoot);
                    XamlFileCodeInfo fileInfo = this.testHelper.HarvestFileCodeInfo(
                        output,
                        false,
                        classInfo,
                        domRoot);
                    fileInfo.ApparentRelativePath = source.Path;
                    fileInfo.FullPathToXamlFile = sourcePath;
                    fileInfo.SourceXamlGivenPath = fileInfo.FullPathToXamlFile;
                    fileInfo.RelativePathFromGeneratedCodeToXamlFile = source.Path;
                    classInfo.AddXamlFileInfo(fileInfo);
                    harvestOrderIds.AddRange(
                        fileInfo.ConnectionIdElements.Select(element => element.ConnectionId));
                    rewrittenFiles.Add(
                        new XamlConnectionIdRewriter().Parse(source.Xaml, classInfo, fileInfo));
                }

                classInfo.PrepareHotReloadConnectionIdArtifacts();
                session.Commit();
            }

            string manifest = File.ReadAllText(
                Directory.GetFiles(
                    Path.Combine(output, "XamlHotReload"),
                    "*.xaml.hotreload.json").Single());
            string ledger = File.ReadAllText(
                Directory.GetFiles(
                    Path.Combine(output, "XamlHotReload"),
                    "*.xaml.connids.json").Single());
            return CompilationResult.From(
                rewrittenFiles,
                harvestOrderIds,
                manifest,
                ledger);
        }

        private string CreateTemporaryDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "XamlCompilerHotReloadTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            this.temporaryDirectories.Add(directory);
            return directory;
        }

        private static string Page(params string[] elements)
        {
            return Header + "\r\n        " +
                String.Join("\r\n        ", elements) +
                Footer;
        }

        private static void AssertExistingIdsEqual(
            CompilationResult expected,
            CompilationResult actual)
        {
            foreach (KeyValuePair<string, int> pair in expected.NamedIds)
            {
                Assert.AreEqual(pair.Value, actual.NamedIds[pair.Key], pair.Key);
            }
        }

        private static string GetLineage(string manifest)
        {
            return Regex.Match(manifest, "\"ledgerLineage\":\"([^\"]+)\"").Groups[1].Value;
        }

        private static List<int> GetRetiredIds(string ledger)
        {
            string values = Regex.Match(
                ledger,
                "\"retiredConnectionIds\":\\[(?<ids>[^\\]]*)\\]")
                .Groups["ids"]
                .Value;
            return String.IsNullOrWhiteSpace(values)
                ? new List<int>()
                : values.Split(',').Select(Int32.Parse).ToList();
        }

        private sealed class XamlSource
        {
            public XamlSource(string path, string xaml)
            {
                this.Path = path;
                this.Xaml = xaml;
            }

            public string Path { get; private set; }
            public string Xaml { get; private set; }
        }

        private sealed class CompilationResult
        {
            private static readonly Regex ConnectionRegex =
                new Regex("x:ConnectionId='(?<id>[0-9]+)'", RegexOptions.Compiled);
            private static readonly Regex NamedConnectionRegex =
                new Regex(
                    "x:ConnectionId='(?<id>[0-9]+)'[^>]*x:Name='(?<name>[^']+)'",
                    RegexOptions.Compiled);

            public Dictionary<string, int> NamedIds { get; private set; }
            public List<int> ConnectionIds { get; private set; }
            public List<int> HarvestOrderIds { get; private set; }
            public string Manifest { get; private set; }
            public string Ledger { get; private set; }

            public static CompilationResult From(
                IEnumerable<string> rewrittenFiles,
                IEnumerable<int> harvestOrderIds,
                string manifest,
                string ledger)
            {
                string rewritten = String.Join("\r\n", rewrittenFiles);
                return new CompilationResult
                {
                    NamedIds = NamedConnectionRegex.Matches(rewritten)
                        .Cast<Match>()
                        .GroupBy(match => match.Groups["name"].Value)
                        .Where(group => group.Count() == 1)
                        .ToDictionary(
                            group => group.Key,
                            group => Int32.Parse(group.Single().Groups["id"].Value)),
                    ConnectionIds = ConnectionRegex.Matches(rewritten)
                        .Cast<Match>()
                        .Select(match => Int32.Parse(match.Groups["id"].Value))
                        .ToList(),
                    HarvestOrderIds = harvestOrderIds.ToList(),
                    Manifest = manifest,
                    Ledger = ledger,
                };
            }
        }
    }
}
