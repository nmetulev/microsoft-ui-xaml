// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml.Markup.Compiler;
using Microsoft.UI.Xaml.Markup.Compiler.MSBuildInterop;

namespace XamlHotReloadEncHarness;

internal sealed class Harness
{
    // What a .NET 10 CoreCLR app reports (MetadataUpdater.GetCapabilities).
    private const string RuntimeCapabilities =
        "Baseline AddMethodToExistingType AddStaticFieldToExistingType AddInstanceFieldToExistingType NewTypeDefinition " +
        "ChangeCustomAttributes UpdateParameters GenericUpdateMethod GenericAddMethodToExistingType GenericAddFieldToExistingType " +
        "AddFieldRva AddExplicitInterfaceImplementation";

    private readonly string _projectFile;
    private readonly string _projectDir;
    private readonly string _objDir;
    private readonly string _outDir;
    private readonly string _mode;
    private readonly string _compilerExe;
    private readonly string[] _cscArgs;
    private readonly HashSet<string> _xamlBaseNames = new(StringComparer.OrdinalIgnoreCase) { "XamlTypeInfo" };
    private readonly List<string> _addedSources = new();
    private readonly List<string> _addedXamlPages = new();
    private readonly List<StepResult> _results = new();
    private readonly HashSet<string> _failedXaml = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _checksumOnlyChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool _verifyBuild;

    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;
    private Solution? _solution;
    private Solution? _committedSolution;
    private readonly List<string> _reloadedReferences = new();
    private EncSession? _enc;
    private XamlCompilerService? _service;
    private int _baselines;

    private Harness(string projectFile, string mode, string compilerExe)
    {
        _projectFile = Path.GetFullPath(projectFile);
        _projectDir = Path.GetDirectoryName(_projectFile)!;
        string inputJson = Directory.EnumerateFiles(Path.Combine(_projectDir, "obj"), "input.json", SearchOption.AllDirectories)
            .OrderBy(File.GetLastWriteTimeUtc).Last();
        _objDir = Path.GetDirectoryName(inputJson)!;
        _outDir = Path.Combine(_objDir, "xhr-" + mode);
        _mode = mode;
        _compilerExe = Path.GetFullPath(compilerExe);
        string[] lines = File.ReadAllLines(Path.Combine(_objDir, "csc.args"));
        int start = Array.FindLastIndex(lines, l => l == "/noconfig");
        _cscArgs = start > 0 ? lines[start..] : lines;
    }

    private bool HotReloadCodeGen => _mode == "hot-reload";

    public static async Task<int> RunAsync(string[] args, string sdk)
    {
        string mode = Program.Option(args, "--mode") ?? "hot-reload";
        if (mode != "off" && mode != "stable" && mode != "hot-reload")
        {
            Console.WriteLine("--mode must be off, stable or hot-reload");
            return 1;
        }
        var harness = new Harness(args[0], mode, Program.Option(args, "--compiler") ?? throw new ArgumentException("--compiler <XamlCompiler.exe> is required"));
        List<JourneyStep> steps = JourneyStep.Read(args[1]);
        if (args.Contains("--extra"))
        {
            steps.Add(new JourneyStep("x1-name-type", "An x:Name element changes type (TextBlock -> TextBox)", "MainPage.xaml", "replace",
                "<TextBlock x:Name=\"Later\"", "<TextBox x:Name=\"Later\"", "rude edit"));
        }
        harness._verifyBuild = args.Contains("--verify-build");
        // Every XamlTypeInfo the service builds from its per-page cache is checked against collecting every page.
        XamlCompilerService.VerifyTypeInfo |= harness._verifyBuild;
        Console.WriteLine($"project: {harness._projectFile}");
        Console.WriteLine($"mode: {mode}; SDK: {sdk}; compiler: {harness._compilerExe}");
        harness.ResetSources();
        try
        {
            await harness.RunJourneyAsync(steps);
        }
        finally
        {
            harness._service?.Dispose();
            harness.ResetSources();
        }
        string report = harness.Report();
        Console.WriteLine(report);
        if (Program.Option(args, "--out") is string outFile)
        {
            File.WriteAllText(outFile, report);
        }
        return 0;
    }

    private async Task RunJourneyAsync(List<JourneyStep> steps)
    {
        if (Directory.Exists(_outDir))
        {
            Directory.Delete(_outDir, recursive: true);
        }
        Directory.CreateDirectory(_outDir);
        CompilerInputs template = ReadInputs();
        foreach (MSBuildItem item in (template.XamlApplications ?? new()).Concat(template.XamlPages ?? new()))
        {
            _xamlBaseNames.Add(Path.GetFileNameWithoutExtension(item.ItemSpec));
        }

        await BuildAndStartAsync("initial build");
        foreach (JourneyStep step in steps)
        {
            _results.Add(await RunStepAsync(step));
            StepResult r = _results[^1];
            Console.WriteLine($"{r.Id,-24} {r.Outcome,-14} xaml {r.XamlMs,5:F0} ms  enc {r.EncMs,5:F0} ms  {r.Details}");
        }
    }

    private async Task<StepResult> RunStepAsync(JourneyStep step)
    {
        if (step.Op == "reload")
        {
            // A reference change (a package added): the build's inputs again, plus the reference named by the step.
            CompilerInputs inputs = ReadInputs();
            string reference = step.Text.Trim();
            if (reference.StartsWith("pass1|", StringComparison.Ordinal))
            {
                // What MarkupCompilePass1 writes to the same input.json: pass 1 inputs, no LocalAssembly.
                reference = reference.Substring("pass1|".Length);
                inputs.IsPass1 = true;
                inputs.CompileMode = "DesignTimeBuild";
                inputs.LocalAssembly = null;
                inputs.SdkXamlPages = null;
            }
            if (!string.IsNullOrWhiteSpace(reference))
            {
                inputs.ReferenceAssemblies.Add(ReferenceItem(reference));
                // From here on the harness's own builds and the C# project reference it too, as the host's workspace does.
                _reloadedReferences.Add(reference);
                _solution = _solution!.AddMetadataReference(_projectId!, MetadataReference.CreateFromFile(reference));
            }
            XamlReloadResult reload = _service!.ReloadInputs(inputs);
            return new StepResult(step.Id, step.Title, step.Expect)
            {
                Outcome = "reloaded",
                XamlMs = reload.Elapsed.TotalMilliseconds,
                Details = $"added {reload.Added.Count}, removed {reload.Removed.Count}, released {reload.Released.Count}, full reset {reload.FullReset}, kept LocalAssembly {reload.KeptLocalAssembly}",
            };
        }
        string path = Path.Combine(_projectDir, step.File);
        bool isNew = !File.Exists(path);
        step.Apply(path);
        var result = new StepResult(step.Id, step.Title, step.Expect);
        Stopwatch xamlClock = Stopwatch.StartNew();

        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            if (isNew)
            {
                _addedXamlPages.Add(Path.GetRelativePath(_projectDir, path));
                _xamlBaseNames.Add(Path.GetFileNameWithoutExtension(path));
            }
            string? error = await CompileXamlAsync(path, result);
            if (error != null)
            {
                _failedXaml.Add(path);
                return result.With("xaml error", xamlClock, error);
            }
            _failedXaml.Remove(path);
            result.XamlMs = xamlClock.Elapsed.TotalMilliseconds;
            if (_verifyBuild)
            {
                result.Notes.Add(await VerifyAgainstBuildAsync());
            }
        }
        else
        {
            if (isNew)
            {
                _addedSources.Add(path);
            }
            SetDocument(path, File.ReadAllText(path));
            // A page whose last compile failed (for example on a handler that didn't exist yet) is compiled again.
            foreach (string page in _failedXaml.ToList())
            {
                string? error = await CompileXamlAsync(page, result);
                if (error != null)
                {
                    return result.With("xaml error", xamlClock, error);
                }
                _failedXaml.Remove(page);
                result.Notes.Add("recompiled " + Path.GetFileName(page));
            }
            result.XamlMs = xamlClock.Elapsed.TotalMilliseconds;
        }

        Stopwatch encClock = Stopwatch.StartNew();
        EncResult enc;
        try
        {
            enc = await _enc!.GetUpdatesAsync(_solution!);
        }
        catch (Exception e)
        {
            Exception inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
            result.EncMs = encClock.Elapsed.TotalMilliseconds;
            result.Outcome = "exception";
            result.Details = $"{inner.GetType().Name}: {inner.Message} at {inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            if (Environment.GetEnvironmentVariable("XHR_HARNESS_STACKS") == "1")
            {
                Console.WriteLine(inner.ToString());
                if (_committedSolution != null)
                {
                    foreach (DocumentId changed in _solution!.GetChanges(_committedSolution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
                    {
                        string name = Path.GetFileName(_solution.GetDocument(changed)!.FilePath!);
                        File.WriteAllText(Path.Combine(_outDir, "exception-old-" + name), (await _committedSolution.GetDocument(changed)!.GetTextAsync()).ToString());
                        File.WriteAllText(Path.Combine(_outDir, "exception-new-" + name), (await _solution.GetDocument(changed)!.GetTextAsync()).ToString());
                    }
                }
            }
            string deleted = await DeletedMembersAsync();
            if (deleted.Length > 0)
            {
                result.Details += " (members the update deletes: " + deleted + ")";
            }
            await BuildAndStartAsync("rebuild after exception");
            return result;
        }
        result.EncMs = encClock.Elapsed.TotalMilliseconds;

        if (enc.Errors.Count > 0)
        {
            _enc.Discard();
            result.Outcome = "compile error";
            result.Details = string.Join("; ", enc.Errors.Take(3).Select(d => $"{d.Id} {d.GetMessage()}"));
        }
        else if (enc.RudeEdits.Count > 0)
        {
            result.Outcome = "rude edit";
            result.Details = string.Join("; ", enc.RudeEdits.Select(d => $"{d.Id} {d.GetMessage()}").Distinct().Take(4));
            await BuildAndStartAsync("rebuild after rude edit");
        }
        else if (enc.Deltas.Count > 0)
        {
            _enc.Commit();
            _committedSolution = _solution;
            result.Outcome = "applied";
            result.Details = $"{enc.Deltas.Count} delta(s): metadata {enc.Deltas.Sum(d => d.Metadata)} B, IL {enc.Deltas.Sum(d => d.IL)} B, {enc.Deltas.Sum(d => d.UpdatedTypes)} updated type(s)";
        }
        else
        {
            result.Outcome = "no change";
            result.Details = enc.Status;
        }
        if (enc.Warnings.Count > 0)
        {
            result.Notes.Add("warnings: " + string.Join("; ", enc.Warnings.Select(d => d.Id).Distinct()));
        }
        return result;
    }

    // The per-save path: pass 1 for the page, a metadata-only emit of the app with pass 1's code, pass 2 with that image,
    // and XamlTypeInfo again only when the page uses a type or member it doesn't have yet. Returns an error or null.
    private async Task<string?> CompileXamlAsync(string path, StepResult result)
    {
        XamlCompileResult p1 = _service!.CompilePass1(new[] { path });
        result.Pass1Ms += p1.Elapsed.TotalMilliseconds;
        if (!p1.Success)
        {
            return "pass 1: " + FirstError(p1);
        }
        ApplyGenerated(p1);
        Stopwatch emit = Stopwatch.StartNew();
        byte[]? image = await EmitImageAsync(_solution!);
        result.EmitMs += emit.Elapsed.TotalMilliseconds;
        XamlCompileResult p2 = _service.CompilePass2(new[] { path }, image, regenerateTypeInfo: false);
        result.Pass2Ms += p2.Elapsed.TotalMilliseconds;
        if (!p2.Success)
        {
            return "pass 2: " + FirstError(p2);
        }
        ApplyGenerated(p2);
        if (p2.XamlTypeInfoMayBeStale)
        {
            // Hot reload builds: the page alone with type info regenerated equals a full pass 2 (sorted tables), and the
            // other pages' type information is replayed from the service's cache.
            XamlCompileResult full = _service.CompilePass2(new[] { path }, image, regenerateTypeInfo: true);
            result.TypeInfoMs += full.Elapsed.TotalMilliseconds;
            if (!full.Success)
            {
                return "XamlTypeInfo: " + FirstError(full);
            }
            ApplyGenerated(full);
            XamlCompileMarker check = full.Markers.FirstOrDefault(m => m.Name == "type info check");
            result.Notes.Add("XamlTypeInfo regenerated" + (check != null ? $" (cache check: {check.Info}, {check.Milliseconds:F0} ms)" : ""));
        }
        return null;
    }

    // The bar for the compiler service: for the same sources and the same hot reload state, what it wrote equals what a
    // build writes. A build of the current sources runs in a copy of this run's output folder (so it continues the same
    // connection ID ledger), and every generated file is compared. Tombstones are the one intended difference: a service
    // compile keeps members the markup no longer produces (since the last build), a build starts a new baseline.
    private async Task<string> VerifyAgainstBuildAsync()
    {
        string scratch = Path.Combine(_objDir, "xhr-verify");
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }
        CopyDirectory(_outDir, scratch, skip: d => Path.GetFileName(d).StartsWith("baseline", StringComparison.Ordinal) || Path.GetFileName(d) == "intermediate");
        CompilerInputs inputs = ReadInputs();
        inputs.OutputPath = Path.GetRelativePath(_projectDir, scratch) + "\\";
        inputs.SavedStateFile = Path.Combine(inputs.OutputPath, "XamlSaveStateFile.xml");
        RunCompiler(inputs, pass1: true, localAssembly: null, workDir: scratch);
        string intermediate = Path.Combine(scratch, "intermediate.dll");
        File.WriteAllBytes(intermediate, await EmitImageAsync(_solution!) ?? throw new InvalidOperationException("emit failed"));
        RunCompiler(inputs, pass1: false, localAssembly: intermediate, workDir: scratch);

        int same = 0, tombstoned = 0;
        var differ = new List<string>();
        foreach (string built in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(scratch, built);
            string ext = Path.GetExtension(built);
            if (relative.StartsWith("XamlHotReload", StringComparison.OrdinalIgnoreCase) || !(ext == ".cs" || ext == ".xbf" || ext == ".xaml"))
            {
                continue;
            }
            string service = Path.Combine(_outDir, relative);
            if (!File.Exists(service))
            {
                differ.Add(relative + " (missing)");
                continue;
            }
            if (File.ReadAllBytes(built).AsSpan().SequenceEqual(File.ReadAllBytes(service)))
            {
                same++;
                continue;
            }
            if (ext == ".cs")
            {
                string withoutTombstones = s_tombstones.Replace(File.ReadAllText(service), string.Empty);
                if (withoutTombstones == File.ReadAllText(built))
                {
                    tombstoned++;
                    continue;
                }
            }
            differ.Add(relative);
        }
        return $"service vs build: {same} identical, {tombstoned} identical but for tombstones, {differ.Count} different{(differ.Count > 0 ? ": " + string.Join(", ", differ.Take(4)) : "")}";
    }

    private static readonly System.Text.RegularExpressions.Regex s_tombstones = new(
        @"#pragma warning disable\r\n[ \t]*// Hot reload: removed from the markup since the last build\..*?#pragma warning restore\r\n",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    private static void CopyDirectory(string from, string to, Func<string, bool> skip)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (string directory in Directory.EnumerateDirectories(from).Where(d => !skip(d)))
        {
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)), skip);
        }
    }

    // A "build": the XAML compiler executable (pass 1, intermediate compile, pass 2) as MSBuild runs it, then the C#
    // compile of the final assembly; the app is (re)started from that assembly, so a new edit-and-continue session begins.
    private async Task BuildAndStartAsync(string reason)
    {
        // Not disposed: that would unload the process-wide type universe the next service reuses.
        _service = null;
        _failedXaml.Clear();
        Stopwatch clock = Stopwatch.StartNew();
        CompilerInputs inputs = ReadInputs();
        RunCompiler(inputs, pass1: true, localAssembly: null);

        // The intermediate assembly pass 2 reflects over: the sources with pass 1's code (pass 2's files are gone).
        CreateWorkspace(Path.Combine(_outDir, "unused.dll"));
        string intermediate = Path.Combine(_outDir, "intermediate", Path.GetFileNameWithoutExtension(_projectFile) + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(intermediate)!);
        File.WriteAllBytes(intermediate, await EmitImageAsync(_solution!) ?? throw new InvalidOperationException("intermediate compile failed"));
        RunCompiler(inputs, pass1: false, localAssembly: intermediate);

        _baselines++;
        string baseline = Path.Combine(_outDir, "baseline" + _baselines, Path.GetFileNameWithoutExtension(_projectFile) + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        CreateWorkspace(baseline);
        Compilation compilation = (await _solution!.GetProject(_projectId!)!.GetCompilationAsync())!;
        using (var pe = File.Create(baseline))
        using (var pdb = File.Create(Path.ChangeExtension(baseline, ".pdb")))
        {
            EmitResult emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: Path.ChangeExtension(baseline, ".pdb")));
            if (!emitted.Success)
            {
                throw new InvalidOperationException("baseline compile failed: " + string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
            }
        }
        _enc = new EncSession(_solution!, _projectId!, RuntimeCapabilities);
        _committedSolution = _solution;
        await _enc.StartAsync(_solution!);

        // The compiler service, warmed with a full compile, which must reproduce the build's files exactly.
        Stopwatch warm = Stopwatch.StartNew();
        _service = new XamlCompilerService(inputs);
        XamlCompileResult w1 = _service.CompilePass1(_service.AllXamlFiles);
        XamlCompileResult w2 = _service.CompilePass2(_service.AllXamlFiles, await EmitImageAsync(_solution!), regenerateTypeInfo: true);
        int same = 0, differ = 0;
        foreach (KeyValuePair<string, string> file in w1.GeneratedText.Concat(w2.GeneratedText).Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            DocumentId? id = _solution!.GetDocumentIdsWithFilePath(file.Key).FirstOrDefault();
            string? built = id == null ? null : (await _solution.GetDocument(id)!.GetTextAsync()).ToString();
            if (built == file.Value)
            {
                same++;
                continue;
            }
            differ++;
            if (built == null)
            {
                Console.WriteLine($"    service output isn't a document of the build: {file.Key}");
                continue;
            }
            string[] a = built.Split('\n'), b = file.Value.Split('\n');
            int line = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(n => a[n] != b[n], Math.Min(a.Length, b.Length));
            Console.WriteLine($"    service differs from build: {Path.GetFileName(file.Key)} line {line + 1} ({a.Length} vs {b.Length} lines): build '{(line < a.Length ? a[line].Trim() : "")}' service '{(line < b.Length ? b[line].Trim() : "")}'");
        }
        Console.WriteLine($"--- {reason}: baseline {_baselines} ({clock.ElapsedMilliseconds - warm.ElapsedMilliseconds} ms); service warm-up {warm.ElapsedMilliseconds} ms ok={w1.Success && w2.Success}, generated C# identical to the build: {same}, different: {differ}");
    }

    private CompilerInputs ReadInputs()
    {
        CompilerInputs inputs;
        using (FileStream s = File.OpenRead(Path.Combine(_objDir, "input.json")))
        {
            inputs = JsonSerializer.Deserialize<CompilerInputs>(s)!;
        }
        inputs.EnableHotReloadStableConnectionIds = _mode != "off";
        inputs.EnableXamlHotReload = _mode == "hot-reload";
        inputs.OutputPath = Path.GetRelativePath(_projectDir, _outDir) + "\\";
        inputs.SavedStateFile = Path.Combine(inputs.OutputPath, "XamlSaveStateFile.xml");
        inputs.XamlPages = (inputs.XamlPages ?? new()).Concat(_addedXamlPages.Select(p => new MSBuildItem
        {
            ItemSpec = p,
            FullPath = Path.Combine(_projectDir, p),
            DependentUpon = "",
            MSBuild_Link = "",
            MSBuild_TargetPath = "",
            MSBuild_XamlResourceMapName = "",
            MSBuild_XamlComponentResourceLocation = "",
        })).ToList();
        inputs.ReferenceAssemblies.AddRange(_reloadedReferences.Select(ReferenceItem));
        return inputs;
    }

    private static MSBuildItem ReferenceItem(string path) => new MSBuildItem { ItemSpec = path, FullPath = path, DependentUpon = "", MSBuild_Link = "", MSBuild_TargetPath = "", MSBuild_XamlResourceMapName = "", MSBuild_XamlComponentResourceLocation = "" };

    private void RunCompiler(CompilerInputs inputs, bool pass1, string? localAssembly, string? workDir = null)
    {
        CompilerInputs i = JsonSerializer.Deserialize<CompilerInputs>(JsonSerializer.Serialize(inputs))!;
        i.IsPass1 = pass1;
        i.CompileMode = pass1 ? "RealBuildPass1" : "RealBuildPass2";
        if (pass1)
        {
            i.LocalAssembly = null;
            i.SdkXamlPages = null;
            i.RootsLog = null;
        }
        else
        {
            i.LocalAssembly = new List<MSBuildItem> { new MSBuildItem { ItemSpec = localAssembly, FullPath = localAssembly } };
        }
        string name = pass1 ? "pass1" : "pass2";
        string input = Path.Combine(workDir ?? _outDir, name + ".json");
        string output = Path.Combine(workDir ?? _outDir, name + ".out.json");
        File.WriteAllText(input, JsonSerializer.Serialize(i));
        var start = new ProcessStartInfo(_compilerExe, $"\"{input}\" \"{output}\"") { WorkingDirectory = _projectDir, UseShellExecute = false, RedirectStandardOutput = true };
        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            using JsonDocument log = JsonDocument.Parse(File.ReadAllText(output));
            string errors = string.Join("; ", log.RootElement.GetProperty("MSBuildLogEntries").EnumerateArray()
                .Where(e => e.GetProperty("Type").GetInt32() == 2).Take(3).Select(e => e.GetProperty("Message").GetString()));
            throw new InvalidOperationException($"XamlCompiler.exe {name} failed: {errors}");
        }
    }

    // The project as csc compiles it, with the XAML compiler's files from this run's output folder, every text read now.
    private void CreateWorkspace(string assemblyPath)
    {
        Assembly[] mef = MefHostServices.DefaultAssemblies.Concat(
            new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features", "Microsoft.CodeAnalysis.ExternalAccess.HotReload" }.Select(Assembly.Load)).ToArray();
        _workspace?.Dispose();
        _workspace = new AdhocWorkspace(MefHostServices.Create(mef));
        var args = new List<string>();
        foreach (string arg in _cscArgs)
        {
            // CsWinRT's AOT generator names its helpers by discovery order: adding a type renames them (a rude edit).
            if (arg.StartsWith("/analyzer:", StringComparison.Ordinal) && arg.Contains("WinRT.SourceGenerator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            args.Add(arg.StartsWith('/') ? arg : MapGenerated(arg));
        }
        args.AddRange(_addedSources);
        args.AddRange(_reloadedReferences.Select(r => "/reference:" + r));
        foreach (string page in _addedXamlPages)
        {
            string stem = Path.Combine(_outDir, Path.ChangeExtension(page, null));
            args.Add(stem + (HotReloadCodeGen ? ".i.g.cs" : ".g.i.cs"));
            args.Add(stem + ".g.cs");
        }
        ProjectInfo info = CommandLineProject.CreateProjectInfo(Path.GetFileNameWithoutExtension(_projectFile), LanguageNames.CSharp, args, _projectDir, _workspace);
        var documents = info.Documents.DistinctBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(d => d.WithTextLoader(TextLoader.From(TextAndVersion.Create(ReadText(d.FilePath!), VersionStamp.Create(), d.FilePath))))
            .ToList();
        info = info.WithFilePath(_projectFile).WithDocuments(documents)
            .WithCompilationOutputInfo(info.CompilationOutputInfo.WithAssemblyPath(assemblyPath));
        _projectId = info.Id;
        _solution = _workspace.AddProject(info).Solution;
    }

    // obj\...\Page.g.i.cs -> <out>\Page.i.g.cs (hot reload) or <out>\Page.g.i.cs.
    private string MapGenerated(string arg)
    {
        string path = Path.GetFullPath(Path.Combine(_projectDir, arg.Trim('"')));
        if (!path.StartsWith(_objDir + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return arg;
        }
        string relative = path.Substring(_objDir.Length + 1);
        string file = Path.GetFileName(relative);
        // A hot reload build's csc.args already names pass 1 files *.i.g.cs.
        foreach (string suffix in new[] { ".g.i.cs", ".i.g.cs", ".g.cs" })
        {
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && _xamlBaseNames.Contains(file[..^suffix.Length]))
            {
                string mapped = Path.Combine(_outDir, relative)[..^suffix.Length];
                return mapped + (suffix == ".g.cs" ? ".g.cs" : HotReloadCodeGen ? ".i.g.cs" : ".g.i.cs");
            }
        }
        return arg;
    }

    private static SourceText ReadText(string path)
    {
        if (!File.Exists(path))
        {
            return SourceText.From(string.Empty, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        }
        using FileStream stream = File.OpenRead(path);
        return SourceText.From(stream, Encoding.UTF8, SourceHashAlgorithm.Sha256);
    }

    private void ApplyGenerated(XamlCompileResult result)
    {
        List<KeyValuePair<string, string>> files = result.GeneratedText.Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

        // Host rule: when the generated code changed only in its #pragma checksum (the .xaml changed, the code didn't), it
        // isn't handed to Roslyn. Roslyn commits a no-change update (DebuggingSession, ModuleUpdateStatus.None) as the new
        // committed solution without a new baseline, and its next update can't find the methods an earlier update added
        // (NullReferenceException in DefinitionMap.GetPreviousMethodHandle). Otherwise every file is updated, so the
        // checksums agree (CS1697): files held back earlier (pass 1 and pass 2 are separate compiles) go along.
        if (files.All(file => DiffersOnlyInChecksums(file.Key, file.Value)))
        {
            foreach (KeyValuePair<string, string> file in files)
            {
                _checksumOnlyChanges[file.Key] = file.Value;
            }
            return;
        }
        foreach (KeyValuePair<string, string> file in files)
        {
            _checksumOnlyChanges.Remove(file.Key);
            SetDocument(file.Key, file.Value);
        }
        foreach (KeyValuePair<string, string> file in _checksumOnlyChanges)
        {
            SetDocument(file.Key, file.Value);
        }
        _checksumOnlyChanges.Clear();
    }
    private bool DiffersOnlyInChecksums(string path, string text)
    {
        DocumentId? id = _solution!.GetDocumentIdsWithFilePath(path).FirstOrDefault();
        if (id == null)
        {
            // A new file with no code (the compiler writes " " for a ResourceDictionary without x:Class): adding it is a
            // solution change with no semantic change, which Roslyn commits without a new baseline, the same as above.
            return string.IsNullOrWhiteSpace(text.TrimStart('\uFEFF'));
        }
        string current = _solution.GetDocument(id)!.GetTextAsync().GetAwaiter().GetResult().ToString();
        return WithoutChecksums(current) == WithoutChecksums(text);
    }

    private static string WithoutChecksums(string text)
        => string.Join("\n", text.Split('\n').Where(line => !line.StartsWith("#pragma checksum ", StringComparison.Ordinal)));

    private void SetDocument(string path, string text)
    {
        SourceText source = SourceText.From(text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        DocumentId? id = _solution!.GetDocumentIdsWithFilePath(path).FirstOrDefault();
        _solution = id != null
            ? _solution.WithDocumentText(id, source)
            : _solution.AddDocument(DocumentId.CreateNewId(_projectId!), Path.GetFileName(path), source, filePath: path);
    }

    // Diagnostics for an update that throws: the members of the last applied version's source types that the new version
    // no longer declares (what edit and continue has to delete).
    private async Task<string> DeletedMembersAsync()
    {
        if (_committedSolution == null)
        {
            return string.Empty;
        }
        Compilation before = (await _committedSolution.GetProject(_projectId!)!.GetCompilationAsync())!;
        Compilation after = (await _solution!.GetProject(_projectId!)!.GetCompilationAsync())!;
        var deleted = new List<string>();
        foreach (INamedTypeSymbol type in SourceTypes(before.Assembly.GlobalNamespace))
        {
            INamedTypeSymbol? now = after.GetTypeByMetadataName(MetadataName(type));
            if (now == null)
            {
                deleted.Add(type.ToDisplayString());
                continue;
            }
            var current = new HashSet<string>(now.GetMembers().Select(m => m.ToDisplayString()));
            deleted.AddRange(type.GetMembers().Where(m => !current.Contains(m.ToDisplayString())).Select(m => m.ToDisplayString()));
        }
        return string.Join(", ", deleted.Take(12)) + (deleted.Count > 12 ? $" (+{deleted.Count - 12} more)" : "");
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceOrTypeSymbol container)
    {
        foreach (ISymbol member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                foreach (INamedTypeSymbol t in SourceTypes(ns))
                {
                    yield return t;
                }
            }
            else if (member is INamedTypeSymbol type && type.Locations.Any(l => l.IsInSource))
            {
                yield return type;
                foreach (INamedTypeSymbol nested in SourceTypes(type))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        return type.ContainingType != null
            ? MetadataName(type.ContainingType) + "+" + type.MetadataName
            : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;
    }

    private async Task<byte[]?> EmitImageAsync(Solution solution)
    {
        Compilation compilation = (await solution.GetProject(_projectId!)!.GetCompilationAsync())!;
        using var stream = new MemoryStream();
        EmitResult result = compilation.Emit(stream, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
        foreach (Diagnostic error in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5))
        {
            Console.WriteLine($"  emit: {error}");
        }
        return result.Success ? stream.ToArray() : null;
    }

    private static string FirstError(XamlCompileResult r)
    {
        XamlCompileDiagnostic? d = r.Diagnostics.FirstOrDefault(x => x.IsError);
        return d == null ? "(no diagnostics)" : $"{d.Code} {d.Message}";
    }

    private void ResetSources()
    {
        Git("checkout", "-q", "--", ".");
        Git("clean", "-fdq");
    }

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _projectDir, UseShellExecute = false };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(_projectDir);
        foreach (string a in args)
        {
            start.ArgumentList.Add(a);
        }
        using Process p = Process.Start(start)!;
        p.WaitForExit();
    }

    private string Report()
    {
        var s = new StringBuilder();
        s.AppendLine($"| step | expected | outcome | per save (ms): pass 1 + emit + pass 2 (+ XamlTypeInfo) | EnC (ms) | details |");
        s.AppendLine("|---|---|---|---|---|---|");
        foreach (StepResult r in _results)
        {
            string details = r.Details + (r.Notes.Count > 0 ? " (" + string.Join("; ", r.Notes) + ")" : "");
            string phases = r.Pass1Ms + r.Pass2Ms > 0 ? $" ({r.Pass1Ms:F0} + {r.EmitMs:F0} + {r.Pass2Ms:F0}{(r.TypeInfoMs > 0 ? $" + {r.TypeInfoMs:F0}" : "")})" : "";
            s.AppendLine($"| {r.Id} | {r.Expect} | {r.Outcome} | {r.XamlMs:F0}{phases} | {r.EncMs:F0} | {details.Replace("|", "\\|")} |");
        }
        int applied = _results.Count(r => r.Outcome == "applied" || r.Outcome == "no change");
        s.AppendLine();
        s.AppendLine($"mode {_mode}: {applied} of {_results.Count} saves applied live; rude edits {_results.Count(r => r.Outcome == "rude edit")}, exceptions {_results.Count(r => r.Outcome == "exception")}, compile errors {_results.Count(r => r.Outcome == "compile error")}, XAML errors {_results.Count(r => r.Outcome == "xaml error")}; builds (app starts) {_baselines}");
        return s.ToString();
    }
}

internal sealed class StepResult
{
    public StepResult(string id, string title, string expect)
    {
        Id = id;
        Title = title;
        Expect = expect;
    }

    public string Id { get; }
    public string Title { get; }
    public string Expect { get; }
    public string Outcome { get; set; } = "";
    public string Details { get; set; } = "";
    public double XamlMs { get; set; }
    public double EncMs { get; set; }
    public double Pass1Ms { get; set; }
    public double EmitMs { get; set; }
    public double Pass2Ms { get; set; }
    public double TypeInfoMs { get; set; }
    public List<string> Notes { get; } = new();

    public StepResult With(string outcome, Stopwatch clock, string details)
    {
        Outcome = outcome;
        XamlMs = clock.Elapsed.TotalMilliseconds;
        Details = details;
        return this;
    }
}

internal sealed class JourneyStep
{
    public JourneyStep(string id, string title, string file, string op, string? find, string text, string expect)
    {
        Id = id;
        Title = title;
        File = file;
        Op = op;
        Find = find;
        Text = text;
        Expect = expect;
    }

    public string Id { get; }
    public string Title { get; }
    public string File { get; }
    public string Op { get; }
    public string? Find { get; }
    public string Text { get; }
    public string Expect { get; }

    // The winapp journey format: segments[].actions[] with edit.op 'replace' (find/text line arrays) or 'create-file'.
    public static List<JourneyStep> Read(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var steps = new List<JourneyStep>();
        foreach (JsonElement segment in doc.RootElement.GetProperty("segments").EnumerateArray())
        {
            foreach (JsonElement action in segment.GetProperty("actions").EnumerateArray())
            {
                JsonElement edit = action.GetProperty("edit");
                static string Lines(JsonElement e) => string.Join("\r\n", e.EnumerateArray().Select(l => l.GetString()));
                string expect = action.TryGetProperty("expect", out JsonElement x) ? x.GetProperty("outcome").GetString()! : "";
                steps.Add(new JourneyStep(
                    action.GetProperty("id").GetString()!, action.GetProperty("title").GetString()!, action.GetProperty("file").GetString()!,
                    edit.GetProperty("op").GetString()!, edit.TryGetProperty("find", out JsonElement f) ? Lines(f) : null, Lines(edit.GetProperty("text")), expect));
            }
        }
        return steps;
    }

    public void Apply(string path)
    {
        if (Op == "reload")
        {
            return;
        }
        if (Op == "create-file")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, Text + "\r\n", new UTF8Encoding(true));
            return;
        }
        string current = System.IO.File.ReadAllText(path);
        string find = Find!;
        string text = Text;
        if (!current.Contains(find, StringComparison.Ordinal))
        {
            find = find.Replace("\r\n", "\n");
            text = text.Replace("\r\n", "\n");
        }
        int index = current.IndexOf(find, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"{Id}: '{Find}' not found in {path}");
        }
        System.IO.File.WriteAllText(path, current.Substring(0, index) + text + current.Substring(index + find.Length), new UTF8Encoding(true));
    }
}

internal sealed record EncResult(string Status, IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> RudeEdits, IReadOnlyList<Diagnostic> Warnings, IReadOnlyList<EncDelta> Deltas);

internal sealed record EncDelta(int Metadata, int IL, int UpdatedTypes);

// The SDK's hot reload service (Microsoft.CodeAnalysis.ExternalAccess.HotReload, the engine behind dotnet watch),
// through reflection: it computes edit-and-continue deltas between the session's baseline and a solution, and reports
// rude edits.
internal sealed class EncSession
{
    private readonly Type _type;
    private readonly object _service;
    private readonly object _runningProjects;

    public EncSession(Solution solution, ProjectId projectId, string capabilities)
    {
        _type = Type.GetType("Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService, Microsoft.CodeAnalysis.ExternalAccess.HotReload", throwOnError: true)!;
        ImmutableArray<string> caps = capabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
        Func<ValueTask<ImmutableArray<string>>> provider = () => ValueTask.FromResult(caps);
        _service = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { solution.Services, provider }, null)!;

        Type infoType = _type.GetNestedType("RunningProjectInfo")!;
        object info = Activator.CreateInstance(infoType)!;
        Type pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(ProjectId), infoType);
        Array pairs = Array.CreateInstance(pairType, 1);
        pairs.SetValue(Activator.CreateInstance(pairType, projectId, info), 0);
        _runningProjects = typeof(ImmutableDictionary).GetMethods()
            .First(m => m.Name == nameof(ImmutableDictionary.CreateRange) && m.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(ProjectId), infoType)
            .Invoke(null, new object[] { pairs })!;
    }

    public Task StartAsync(Solution solution)
        => (Task)_type.GetMethod("StartSessionAsync")!.Invoke(_service, new object[] { solution, CancellationToken.None })!;

    public async Task<EncResult> GetUpdatesAsync(Solution solution)
    {
        var task = (Task)_type.GetMethod("GetUpdatesAsync")!.Invoke(_service, new object[] { solution, _runningProjects, CancellationToken.None })!;
        await task.ConfigureAwait(false);
        object updates = task.GetType().GetProperty("Result")!.GetValue(task)!;
        object? Get(string name) => updates.GetType().GetProperty(name)!.GetValue(updates);

        var persistent = (ImmutableArray<Diagnostic>)Get("PersistentDiagnostics")!;
        var errors = persistent.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = persistent.Where(d => d.Severity != DiagnosticSeverity.Error).ToList();
        var rude = new List<Diagnostic>();
        foreach (object entry in (IEnumerable)Get("TransientDiagnostics")!)
        {
            var diagnostics = (ImmutableArray<Diagnostic>)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            rude.AddRange(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            warnings.AddRange(diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error));
        }
        var deltas = new List<EncDelta>();
        foreach (object update in (IEnumerable)Get("ProjectUpdates")!)
        {
            T Field<T>(string name) => (T)update.GetType().GetField(name)!.GetValue(update)!;
            deltas.Add(new EncDelta(Field<ImmutableArray<byte>>("MetadataDelta").Length, Field<ImmutableArray<byte>>("ILDelta").Length, Field<ImmutableArray<int>>("UpdatedTypes").Length));
        }
        return new EncResult(Get("Status")!.ToString()!, errors, rude, warnings, deltas);
    }

    public void Commit() => _type.GetMethod("CommitUpdate")!.Invoke(_service, null);

    public void Discard()
    {
        try
        {
            _type.GetMethod("DiscardUpdate")!.Invoke(_service, null);
        }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
        {
            // No pending update (the update failed before one was created).
        }
    }
}
