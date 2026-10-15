// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.UI.Xaml.Markup.Compiler;
using Microsoft.UI.Xaml.Markup.Compiler.MSBuildInterop;

// Developer tool for the XAML compiler service spike.
//
//   Any command: --hot-reload reads every input as a hot reload (Debug) build passes it: EnableXamlHotReload
//   and EnableHotReloadStableConnectionIds on.
//
//   prepare <build input.json> <outputPath> <destDir>
//       Writes pass1.json/pass2.json for XamlCompiler.exe, redirected to <outputPath>
//       (relative to the project), so a full out-of-process build can be the byte-for-byte baseline.
//
//   bench <build input.json> <outputPath> --pages a.xaml[,b.xaml] [--iterations N]
//         [--typeinfo all|skip] [--asm file|image|tempdll] [--asm-path <dll>] [--warmup full|none]
//       Creates a warm service, then recompiles the given pages N times (pass 1 + pass 2) and
//       prints per-phase timings.
//
//   tombstones <generated dir> <out dir>
//       Hot reload tombstone self-test on generated C# (.g.cs, .i.g.cs): for every file, the inventory of fields and
//       nested types must be a no-op on the file itself; then every inventoried member is removed and restored
//       as a tombstone, and the result is written to <out dir> (same layout) to compile with 'emit --replace'.
//
//   emit <rsp> <projectDir> <out.dll> [--replace <from>=<to>]... [--no-generators]
//       Builds the project's intermediate assembly with Roslyn in-process from the csc response
//       file the build used, and reports parse, generator, full-emit and metadata-only-emit times.
//       Writes the metadata-only image to <out.dll>.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: prepare | bench | emit (see Program.cs)");
            return 1;
        }
        // --hot-reload: every input is read as a hot reload (Debug) build would pass it.
        s_hotReload = args.Contains("--hot-reload");
        args = args.Where(a => a != "--hot-reload").ToArray();
        switch (args[0])
        {
            case "prepare": return Prepare(args[1], args[2], args[3]);
            case "bench": return Bench(args.Skip(1).ToArray());
            case "emit": return Emit(args.Skip(1).ToArray());
            case "edit": return EditScenario(args.Skip(1).ToArray());
            case "tombstones": return TombstoneSelfTest(args[1], args[2]);
            default:
                Console.WriteLine($"unknown command {args[0]}");
                return 1;
        }
    }

    private static bool s_hotReload;

    private static CompilerInputs ReadInputs(string path)
    {
        using FileStream s = File.OpenRead(path);
        CompilerInputs inputs = JsonSerializer.Deserialize<CompilerInputs>(s);
        if (s_hotReload)
        {
            inputs.EnableXamlHotReload = true;
            inputs.EnableHotReloadStableConnectionIds = true;
        }
        return inputs;
    }

    private static int Prepare(string buildInputJson, string outputPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (bool pass1 in new[] { true, false })
        {
            CompilerInputs i = ReadInputs(buildInputJson);
            i.OutputPath = outputPath.EndsWith("\\") ? outputPath : outputPath + "\\";
            i.SavedStateFile = Path.Combine(i.OutputPath, "XamlSaveStateFile.xml");
            i.IsPass1 = pass1;
            if (pass1)
            {
                // What MarkupCompilePass1 passes for C#: no local assembly, no SDK XAML, no roots log.
                i.LocalAssembly = null;
                i.SdkXamlPages = null;
                i.RootsLog = null;
                i.CompileMode = "RealBuildPass1";
            }
            else
            {
                i.CompileMode = "RealBuildPass2";
            }
            string file = Path.Combine(destDir, pass1 ? "pass1.json" : "pass2.json");
            File.WriteAllText(file, JsonSerializer.Serialize(i));
            Console.WriteLine($"wrote {file}");
        }
        return 0;
    }

    private static string Option(string[] args, string name, string fallback = null)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static int Bench(string[] args)
    {
        string buildInputJson = args[0];
        string outputPath = args[1];
        string[] pages = Option(args, "--pages", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int iterations = int.Parse(Option(args, "--iterations", "5"));
        bool regenerateTypeInfo = Option(args, "--typeinfo", "all") == "all";
        string asmMode = Option(args, "--asm", "file");
        string warmup = Option(args, "--warmup", "full");

        CompilerInputs inputs = ReadInputs(buildInputJson);
        string projectDir = Path.GetDirectoryName(inputs.ProjectPath);
        string asmPath = Option(args, "--asm-path", Path.Combine(projectDir, inputs.LocalAssembly[0].ItemSpec));
        byte[] asmBytes = File.ReadAllBytes(asmPath);
        Console.WriteLine($"project: {inputs.ProjectPath}");
        Console.WriteLine($"pages in project: {inputs.XamlPages.Count + (inputs.XamlApplications?.Count ?? 0)}, references: {inputs.ReferenceAssemblies.Count}");
        Console.WriteLine($"local assembly: {asmPath} ({asmBytes.Length:N0} bytes), mode={asmMode}, typeinfo={(regenerateTypeInfo ? "all" : "skip")}");

        Stopwatch processClock = Stopwatch.StartNew();
        using var service = new XamlCompilerService(inputs, outputPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "xcsvc-" + Environment.ProcessId);
        Directory.CreateDirectory(tempDir);
        int tempCounter = 0;

        XamlCompileResult RunPass2(IEnumerable<string> files, bool typeInfo)
        {
            switch (asmMode)
            {
                case "image":
                    // A fresh array each time, as a new Roslyn emit would hand us.
                    return service.CompilePass2(files, (byte[])asmBytes.Clone(), typeInfo);
                case "tempdll":
                    {
                        Stopwatch w = Stopwatch.StartNew();
                        string temp = Path.Combine(tempDir, $"{tempCounter++}", Path.GetFileName(asmPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(temp));
                        File.WriteAllBytes(temp, asmBytes);
                        w.Stop();
                        XamlCompileResult r = service.CompilePass2(files, null, typeInfo, temp);
                        Console.WriteLine($"    (temp dll write {w.Elapsed.TotalMilliseconds:F1} ms, not included below)");
                        return r;
                    }
                default:
                    return service.CompilePass2(files, null, typeInfo, asmMode == "file" && Option(args, "--asm-path") != null ? asmPath : null);
            }
        }

        if (warmup == "full")
        {
            IReadOnlyList<string> all = service.AllXamlFiles;
            Report($"cold pass1, all {all.Count} files", service.CompilePass1(all));
            Report($"cold pass2, all {all.Count} files", RunPass2(all, true));
            Report($"warm pass1, all {all.Count} files", service.CompilePass1(all));
            Report($"warm pass2, all {all.Count} files", RunPass2(all, true));
        }

        // Stress: repeated full cycles, optionally forcing a GC before each compile, to flush out
        // anything that survives between compiles and shouldn't.
        int fullCycles = int.Parse(Option(args, "--full-cycles", "0"));
        bool forceGc = args.Contains("--gc");
        for (int n = 0; n < fullCycles; n++)
        {
            IReadOnlyList<string> all = service.AllXamlFiles;
            if (forceGc) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            XamlCompileResult p1 = service.CompilePass1(all);
            if (forceGc) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            XamlCompileResult p2 = RunPass2(all, true);
            Console.WriteLine($"full cycle {n}: pass1 {p1.Elapsed.TotalMilliseconds:F0} ms ok={p1.Success}, pass2 {p2.Elapsed.TotalMilliseconds:F0} ms ok={p2.Success}");
        }

        for (int n = 0; n < iterations; n++)
        {
            foreach (string page in pages)
            {
                Report($"[{n}] pass1 {page}", service.CompilePass1(new[] { page }));
                Report($"[{n}] pass2 {page}", RunPass2(new[] { page }, regenerateTypeInfo));
            }
        }

        // Every page in the project, one at a time (pass 1 + pass 2): the distribution a user would see.
        if (args.Contains("--each-page"))
        {
            var totals = new List<(double Ms, string Page)>();
            int failures = 0;
            foreach (string page in service.AllXamlFiles)
            {
                XamlCompileResult p1 = service.CompilePass1(new[] { page });
                XamlCompileResult p2 = RunPass2(new[] { page }, regenerateTypeInfo);
                if (!p1.Success || !p2.Success)
                {
                    failures++;
                }
                totals.Add((p1.Elapsed.TotalMilliseconds + p2.Elapsed.TotalMilliseconds, Path.GetRelativePath(Path.GetDirectoryName(inputs.ProjectPath), page)));
            }
            totals.Sort((a, b) => a.Ms.CompareTo(b.Ms));
            double P(double q) => totals[(int)Math.Min(totals.Count - 1, Math.Round(q * (totals.Count - 1)))].Ms;
            Console.WriteLine($"each page (pass1 + pass2), {totals.Count} pages, failures={failures}: min={totals[0].Ms:F0} p50={P(0.5):F0} p90={P(0.9):F0} p99={P(0.99):F0} max={totals[^1].Ms:F0} ms; slowest: {string.Join(", ", totals.Skip(totals.Count - 3).Select(t => $"{t.Page} {t.Ms:F0}"))}");
        }

        Console.WriteLine($"process total {processClock.Elapsed.TotalMilliseconds:F0} ms, working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB");

        // Can a build overwrite the project's assembly and references while the service is alive?
        if (args.Contains("--check-locks"))
        {
            var files = new List<string> { asmPath };
            files.AddRange(inputs.ReferenceAssemblies.Select(r => Path.GetFullPath(Path.Combine(projectDir, r.ItemSpec))));
            int locked = 0;
            foreach (string file in files.Where(File.Exists))
            {
                try
                {
                    using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException)
                {
                    locked++;
                    if (locked <= 3)
                    {
                        Console.WriteLine($"    locked: {file}");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Read-only files (for example in Program Files) can't be opened for write by anyone.
                }
            }
            Console.WriteLine($"lock check: {locked} of {files.Count} files held open by the service");
        }
        try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return 0;
    }

    // Marker pairs that bracket a phase. Per-page phases are summed over pages.
    private static readonly (string Name, string Start, string End)[] s_phases =
    {
        ("type universe", "perfXC_CreatingTypeUniverse", "perfXC_CreatedTypeUniverse"),
        ("schema context (load refs)", "perfXC_CreatingSchemaContext", "perfXC_CreatedSchemaContext"),
        ("  type name map", "perfXC_InitializeTypeNameMapStart", "perfXC_InitializeTypeNameMapEnd"),
        ("pages (all per-page work)", "perfXC_PageStart", "perfXC_PageDone"),
        ("  parse (load DOM)", "perfXC_PageLoadStart", "perfXC_PageLoadEnd"),
        ("  validate", "perfXC_PageValidateStart", "perfXC_PageValidateEnd"),
        ("  type info collect", "perfXC_PageTypeCollectStart", "perfXC_PageTypeCollectEnd"),
        ("  harvest", "perfXC_PageHarvestStart", "perfXC_PageHarvestEnd"),
        ("metadata providers/bindables", "perfXC_SearchIxmpAndBindableStart", "perfXC_SearchIxmpAndBindableEnd"),
        ("page code gen", "perfXC_PageCodeGenStart", "perfXC_PageCodeGenEnd"),
        ("edited XAML", "perfXC_PageEditStart", "perfXC_PageEditEnd"),
        ("write files", "perfXC_WriteFilesToDiskStart", "perfXC_WriteFilesToDiskEnd"),
        ("XamlTypeInfo gen", "perfXC_GenerateTypeInfoStart", "perfXC_GenerateTypeInfoEnd"),
        ("XamlTypeInfo write", "perfXC_WriteTypeinfoFilesToDiskStart", "perfXC_WriteTypeinfoFilesToDiskEnd"),
        ("XBF (genxbf)", "perfXC_GenerateXBFStart", "perfXC_GenerateXBFEnd"),
    };

    private static void Report(string label, XamlCompileResult r)
    {
        int errors = r.Diagnostics.Count(d => d.IsError);
        Console.WriteLine($"{label}: {r.Elapsed.TotalMilliseconds:F1} ms, success={r.Success}, errors={errors}, text={r.GeneratedText.Count}, xbf={r.GeneratedXbf.Count}");
        foreach (XamlCompileDiagnostic d in r.Diagnostics.Where(d => d.IsError).Take(5))
        {
            Console.WriteLine("    " + d);
        }

        double start = r.Markers.FirstOrDefault(m => m.Name.StartsWith("perfXC_StartPass", StringComparison.Ordinal))?.Milliseconds ?? 0;
        double end = r.Markers.LastOrDefault(m => m.Name.StartsWith("perfXC_EndPass", StringComparison.Ordinal))?.Milliseconds ?? 0;
        var parts = new List<string>();
        foreach (var phase in s_phases)
        {
            double sum = 0;
            int count = 0;
            var open = new Stack<double>();
            foreach (XamlCompileMarker m in r.Markers)
            {
                if (m.Name == phase.Start)
                {
                    open.Push(m.Milliseconds);
                }
                else if (m.Name == phase.End && open.Count > 0)
                {
                    sum += m.Milliseconds - open.Pop();
                    count++;
                }
            }
            if (count > 0 && sum >= 0.05)
            {
                parts.Add(count > 1 ? $"{phase.Name}={sum:F1} (x{count})" : $"{phase.Name}={sum:F1}");
            }
        }
        Console.WriteLine($"    DoExecute={end - start:F1}; " + string.Join("; ", parts));

        var segments = new List<string>();
        double previous = start;
        foreach (XamlCompileMarker m in r.Markers.Where(m => m.Name == "perfXC_Checkpoint"))
        {
            segments.Add($"{m.Info}={m.Milliseconds - previous:F1}");
            previous = m.Milliseconds;
        }
        if (segments.Count > 0)
        {
            Console.WriteLine($"    segments: {string.Join("; ", segments)}; outside DoExecute={r.Elapsed.TotalMilliseconds - (end - start):F1}; typeinfo stale={r.XamlTypeInfoMayBeStale}");
        }
    }

    private static int Emit(string[] args)
    {
        string rspPath = args[0];
        string projectDir = args[1];
        string outPath = args[2];
        bool runGenerators = !args.Contains("--no-generators");
        var replacements = new List<(string From, string To)>();
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--replace")
            {
                string[] pair = args[++i].Split('=', 2);
                replacements.Add((pair[0], pair[1]));
            }
        }

        Stopwatch w = Stopwatch.StartNew();
        string rsp = File.ReadAllText(rspPath).Trim();
        foreach (var (from, to) in replacements)
        {
            rsp = rsp.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }
        string[] cscArgs = CommandLineParser.SplitCommandLineIntoArguments(rsp, removeHashComments: false).ToArray();
        string sdkDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        CSharpCommandLineArguments parsed = CSharpCommandLineParser.Default.Parse(cscArgs, projectDir, sdkDir);
        foreach (Diagnostic d in parsed.Errors)
        {
            Console.WriteLine("rsp: " + d);
        }

        bool blankPass2Code = args.Contains("--blank-pass2");
        var trees = parsed.SourceFiles
            .AsParallel()
            .Select(f =>
            {
                // Pass 1 leaves the pass 2 files (.g.cs) empty for the intermediate compile;
                // --blank-pass2 reproduces that even if a later pass 2 has filled them in.
                bool blank = !File.Exists(f.Path) ||
                    (blankPass2Code && f.Path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
                     !f.Path.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase));
                string text = blank ? string.Empty : File.ReadAllText(f.Path);
                return CSharpSyntaxTree.ParseText(
                    Microsoft.CodeAnalysis.Text.SourceText.From(text, System.Text.Encoding.UTF8),
                    parsed.ParseOptions, f.Path);
            })
            .ToList();
        var references = parsed.MetadataReferences
            .Select(r => (MetadataReference)MetadataReference.CreateFromFile(Path.GetFullPath(Path.Combine(projectDir, r.Reference))))
            .ToList();
        CSharpCompilation compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(parsed.OutputFileName),
            trees, references, parsed.CompilationOptions);
        Console.WriteLine($"parse + create: {w.Elapsed.TotalMilliseconds:F0} ms ({trees.Count} files, {references.Count} references)");

        if (runGenerators)
        {
            w.Restart();
            var loader = new SimpleAnalyzerLoader();
            var generators = parsed.AnalyzerReferences
                .Select(a => new AnalyzerFileReference(Path.GetFullPath(Path.Combine(projectDir, a.FilePath)), loader))
                .SelectMany(a => a.GetGenerators(LanguageNames.CSharp))
                .ToList();
            var optionsProvider = new SimpleConfigOptionsProvider(parsed, projectDir);
            var additionalTexts = parsed.AdditionalFiles
                .Select(f => (AdditionalText)new FileAdditionalText(Path.GetFullPath(Path.Combine(projectDir, f.Path))))
                .ToImmutableArray();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, additionalTexts, parsed.ParseOptions, optionsProvider);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out ImmutableArray<Diagnostic> genDiags);
            compilation = (CSharpCompilation)updated;
            Console.WriteLine($"generators ({generators.Count}): {w.Elapsed.TotalMilliseconds:F0} ms, diagnostics={genDiags.Length}");
        }

        for (int pass = 0; pass < 3; pass++)
        {
            // The first pass binds the compilation; later passes show the cost once it is warm,
            // as in a hot reload host that already holds the compilation.
            w.Restart();
            using var full = new MemoryStream();
            EmitResult fullResult = compilation.Emit(full);
            double fullMs = w.Elapsed.TotalMilliseconds;

            w.Restart();
            using var metadata = new MemoryStream();
            EmitResult metadataResult = compilation.Emit(metadata, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
            double metadataMs = w.Elapsed.TotalMilliseconds;

            Console.WriteLine($"emit #{pass}: full={fullMs:F0} ms ({full.Length:N0} bytes, ok={fullResult.Success}), metadata-only={metadataMs:F0} ms ({metadata.Length:N0} bytes, ok={metadataResult.Success})");
            foreach (Diagnostic d in fullResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10))
            {
                Console.WriteLine("    " + d);
            }
            if (pass == 0)
            {
                File.WriteAllBytes(outPath, metadata.ToArray());
                File.WriteAllBytes(Path.ChangeExtension(outPath, ".full.dll"), full.ToArray());
            }
        }

        // A one-line edit, to show the cost on a compilation that has already been bound once.
        w.Restart();
        SyntaxTree first = compilation.SyntaxTrees.First(t => t.FilePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) || t.FilePath.EndsWith(".i.g.cs", StringComparison.OrdinalIgnoreCase));
        CSharpCompilation edited = compilation.ReplaceSyntaxTree(first, first.WithChangedText(first.GetText().Replace(0, 0, "// edit\r\n")));
        using (var m = new MemoryStream())
        {
            edited.Emit(m, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
        }
        Console.WriteLine($"metadata-only emit after editing one generated file: {w.Elapsed.TotalMilliseconds:F0} ms");
        return 0;
    }

    // The per-save path end to end, in one warm process:
    //   edit <build input.json> <outputPath> <rsp> <page.xaml> <find> <replace> <resultDir> [--rounds N]
    // Warms the service and a Roslyn compilation of the app (from the build's csc arguments, with
    // generators), then applies the edit to the XAML file and runs: pass 1 for the page, Roslyn
    // metadata-only emit with the new .g.i.cs, pass 2 with that image, and pass 2 with the image from
    // before the edit. Writes both pass 2 results under <resultDir> for diffing, then restores the file.
    private static int EditScenario(string[] args)
    {
        string buildInputJson = args[0], outputPath = args[1], rspPath = args[2], page = args[3];
        string find = args[4], replace = args[5], resultDir = args[6];
        int rounds = int.Parse(Option(args, "--rounds", "3"));
        CompilerInputs inputs = ReadInputs(buildInputJson);
        string projectDir = Path.GetDirectoryName(inputs.ProjectPath);
        string pagePath = Path.GetFullPath(Path.Combine(projectDir, page));
        string original = File.ReadAllText(pagePath);
        if (!original.Contains(find, StringComparison.Ordinal))
        {
            Console.WriteLine("edit anchor not found");
            return 1;
        }
        string edited = original.Replace(find, replace, StringComparison.Ordinal);

        // Optional code-behind edit applied together with the XAML edit.
        string csPath = Option(args, "--cs") is string cs ? Path.GetFullPath(Path.Combine(projectDir, cs)) : null;
        string csOriginal = csPath != null ? File.ReadAllText(csPath) : null;
        string csEdited = csPath != null ? csOriginal.Replace(Option(args, "--cs-find"), Option(args, "--cs-replace"), StringComparison.Ordinal) : null;
        if (csPath != null && csEdited == csOriginal)
        {
            Console.WriteLine("code-behind anchor not found");
            return 1;
        }

        using var service = new XamlCompilerService(inputs, outputPath);
        string serviceOutput = Path.GetFullPath(Path.Combine(projectDir, outputPath));
        string buildOutput = Path.GetFullPath(Path.Combine(projectDir, inputs.OutputPath));

        Stopwatch w = Stopwatch.StartNew();
        IReadOnlyList<string> all = service.AllXamlFiles;
        XamlCompileResult warm1 = service.CompilePass1(all);
        XamlCompileResult warm2 = service.CompilePass2(all);
        Console.WriteLine($"service warm-up (full pass 1 + pass 2): {w.ElapsedMilliseconds} ms ok={warm1.Success && warm2.Success}");

        // The app's compilation, as a hot reload host would hold it: generated files come from the
        // service's output folder. --no-roslyn skips this (native projects, or to time the compiler alone).
        bool useRoslyn = !args.Contains("--no-roslyn");
        CSharpCompilation compilation = null;
        byte[] imageBeforeEdit = null;
        if (useRoslyn)
        {
            w.Restart();
            compilation = CreateCompilation(rspPath, projectDir, buildOutput + "\\", serviceOutput + "\\");
            using (var m = new MemoryStream())
            {
                compilation.Emit(m, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
            }
            Console.WriteLine($"roslyn warm-up (parse, generators, first metadata emit): {w.ElapsedMilliseconds} ms");
            imageBeforeEdit = EmitMetadata(compilation, out _);
        }
        var previousText = new Dictionary<string, string>(warm1.GeneratedText.Concat(warm2.GeneratedText).GroupBy(p => p.Key).Select(g => g.Last()), StringComparer.OrdinalIgnoreCase);
        var previousXbf = new Dictionary<string, byte[]>(warm2.GeneratedXbf, StringComparer.OrdinalIgnoreCase);

        try
        {
            for (int round = 0; round < rounds; round++)
            {
                // Alternate edited/original so every round is a real change.
                bool applyEdit = round % 2 == 0;
                File.WriteAllText(pagePath, applyEdit ? edited : original);
                Console.WriteLine($"--- round {round}: {(applyEdit ? "apply edit" : "revert edit")}");

                Stopwatch total = Stopwatch.StartNew();
                XamlCompileResult p1 = service.CompilePass1(new[] { page });
                Report("  pass1", p1);

                w.Restart();
                var changedSources = new Dictionary<string, string>(p1.GeneratedText, StringComparer.OrdinalIgnoreCase);
                if (csPath != null)
                {
                    File.WriteAllText(csPath, applyEdit ? csEdited : csOriginal);
                    changedSources[csPath] = applyEdit ? csEdited : csOriginal;
                }
                byte[] image = null;
                if (useRoslyn)
                {
                    foreach (KeyValuePair<string, string> file in changedSources)
                    {
                        SyntaxTree old = compilation.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, file.Key, StringComparison.OrdinalIgnoreCase));
                        if (old != null)
                        {
                            compilation = compilation.ReplaceSyntaxTree(old, old.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(file.Value, System.Text.Encoding.UTF8)));
                        }
                    }
                    image = EmitMetadata(compilation, out double emitMs);
                    Console.WriteLine($"  roslyn: update trees + metadata-only emit {w.Elapsed.TotalMilliseconds:F0} ms (emit {emitMs:F0} ms, {image.Length:N0} bytes)");
                }

                XamlCompileResult p2 = service.CompilePass2(new[] { page }, image, regenerateTypeInfo: false);
                Report("  pass2 (new image)", p2);
                Console.WriteLine($"  per-save total (pass1 + {(useRoslyn ? "roslyn metadata emit + " : "")}pass2): {total.Elapsed.TotalMilliseconds:F0} ms");

                // Which generated files actually changed compared with the previous compile.
                var changed = new List<string>();
                foreach (var f in p1.GeneratedText.Concat(p2.GeneratedText))
                {
                    if (!previousText.TryGetValue(f.Key, out string before) || before != f.Value)
                    {
                        changed.Add(Path.GetFileName(f.Key));
                    }
                    previousText[f.Key] = f.Value;
                }
                foreach (var f in p2.GeneratedXbf)
                {
                    if (!previousXbf.TryGetValue(f.Key, out byte[] before) || !before.AsSpan().SequenceEqual(f.Value))
                    {
                        changed.Add(Path.GetFileName(f.Key));
                    }
                    previousXbf[f.Key] = f.Value;
                }
                Console.WriteLine($"  changed outputs: {(changed.Count == 0 ? "none" : string.Join(", ", changed))}");
                Save(p2, Path.Combine(resultDir, $"round{round}-new-image"), serviceOutput);
                if (p2.XamlTypeInfoMayBeStale)
                {
                    // The fallback: a full pass 2 that regenerates XamlTypeInfo exactly as a build does.
                    XamlCompileResult full = service.CompilePass2(service.AllXamlFiles, image, regenerateTypeInfo: true);
                    Console.WriteLine($"  XamlTypeInfo stale -> full pass2 with type info: {full.Elapsed.TotalMilliseconds:F0} ms ok={full.Success}");
                }

                // Same pass 2 against the image from before any edit: is a new image needed at all
                // for XAML-only edits? (Compiled into a scratch run, then the new-image result is restored.)
                if (useRoslyn && !args.Contains("--skip-old-image"))
                {
                    XamlCompileResult p2Old = service.CompilePass2(new[] { page }, imageBeforeEdit, regenerateTypeInfo: false);
                    Report("  pass2 (pre-edit image)", p2Old);
                    Save(p2Old, Path.Combine(resultDir, $"round{round}-old-image"), serviceOutput);
                    service.CompilePass2(new[] { page }, image, regenerateTypeInfo: false);
                }
                Save(p1, Path.Combine(resultDir, $"round{round}-pass1"), serviceOutput);
            }
        }
        finally
        {
            File.WriteAllText(pagePath, rounds % 2 == 1 ? edited : original);
            if (csPath != null)
            {
                File.WriteAllText(csPath, rounds % 2 == 1 ? csEdited : csOriginal);
            }
            Console.WriteLine($"left {page} {(rounds % 2 == 1 ? "edited" : "original")}");
        }
        return 0;
    }

    private static int TombstoneSelfTest(string generatedDir, string outDir)
    {
        Type members = typeof(XamlCompilerService).Assembly.GetType("Microsoft.UI.Xaml.Markup.Compiler.HotReloadRetiredMembers", throwOnError: true);
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var createInventory = members.GetMethod("CreateInventory", flags);
        var restore = members.GetMethod("Restore", flags);
        var removeKept = members.GetMethod("RemoveKeptMembers", flags);
        static IEnumerable<string> Keys(object inventory) =>
            ((System.Collections.IEnumerable)inventory).Cast<object>().Select(e => (string)e.GetType().GetProperty("Key").GetValue(e));

        int files = 0, entries = 0, restored = 0, failures = 0;
        Stopwatch w = Stopwatch.StartNew();
        foreach (string file in Directory.EnumerateFiles(generatedDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            string code = File.ReadAllText(file);
            object inventory = createInventory.Invoke(null, new object[] { code });
            List<string> keys = Keys(inventory).ToList();
            string unchanged = (string)restore.Invoke(null, new object[] { code, inventory });
            string stripped = (string)removeKept.Invoke(null, new object[] { code });
            string tombstoned = (string)restore.Invoke(null, new object[] { stripped, inventory });
            List<string> keysAfter = Keys(createInventory.Invoke(null, new object[] { tombstoned })).ToList();
            bool ok = unchanged == code && keys.SequenceEqual(keysAfter) && (keys.Count == 0 || tombstoned != stripped);
            if (!ok)
            {
                failures++;
                Console.WriteLine($"  FAIL {Path.GetRelativePath(generatedDir, file)}: no-op={unchanged == code}, keys {keys.Count} -> {keysAfter.Count}");
            }
            files++;
            entries += keys.Count;
            restored += tombstoned != stripped ? 1 : 0;
            string target = Path.Combine(outDir, Path.GetRelativePath(generatedDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, tombstoned, new System.Text.UTF8Encoding(true));
        }
        Console.WriteLine($"tombstone self-test: {files} files, {entries} inventoried members, {restored} files rebuilt from tombstones, {failures} failures, {w.ElapsedMilliseconds} ms");
        return failures == 0 ? 0 : 1;
    }
    // Copies what the compiler wrote to disk for this compile (exact bytes, as a build would leave them).
    private static void Save(XamlCompileResult r, string dir, string serviceOutput)
    {
        foreach (string file in r.GeneratedText.Keys.Concat(r.GeneratedXbf.Keys))
        {
            string target = Path.Combine(dir, Path.GetRelativePath(serviceOutput, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, overwrite: true);
        }
    }

    private static byte[] EmitMetadata(Compilation compilation, out double milliseconds)
    {
        Stopwatch w = Stopwatch.StartNew();
        using var m = new MemoryStream();
        EmitResult result = compilation.Emit(m, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
        milliseconds = w.Elapsed.TotalMilliseconds;
        if (!result.Success)
        {
            foreach (Diagnostic d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5))
            {
                Console.WriteLine("    emit: " + d);
            }
        }
        return m.ToArray();
    }

    private static CSharpCompilation CreateCompilation(string rspPath, string projectDir, string replaceFrom, string replaceTo)
    {
        string rsp = File.ReadAllText(rspPath).Trim().Replace(replaceFrom, replaceTo, StringComparison.OrdinalIgnoreCase);
        string[] cscArgs = CommandLineParser.SplitCommandLineIntoArguments(rsp, removeHashComments: false).ToArray();
        CSharpCommandLineArguments parsed = CSharpCommandLineParser.Default.Parse(cscArgs, projectDir, Path.GetDirectoryName(typeof(object).Assembly.Location));
        var trees = parsed.SourceFiles.AsParallel().AsOrdered()
            .Select(f => CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(File.Exists(f.Path) ? File.ReadAllText(f.Path) : string.Empty, System.Text.Encoding.UTF8),
                parsed.ParseOptions, f.Path))
            .ToList();
        var references = parsed.MetadataReferences
            .Select(r => (MetadataReference)MetadataReference.CreateFromFile(Path.GetFullPath(Path.Combine(projectDir, r.Reference))))
            .ToList();
        CSharpCompilation compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(parsed.OutputFileName), trees, references, parsed.CompilationOptions);
        var loader = new SimpleAnalyzerLoader();
        var generators = parsed.AnalyzerReferences
            .Select(a => new AnalyzerFileReference(Path.GetFullPath(Path.Combine(projectDir, a.FilePath)), loader))
            .SelectMany(a => a.GetGenerators(LanguageNames.CSharp))
            .ToList();
        var additionalTexts = parsed.AdditionalFiles
            .Select(f => (AdditionalText)new FileAdditionalText(Path.GetFullPath(Path.Combine(projectDir, f.Path))))
            .ToImmutableArray();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, additionalTexts, parsed.ParseOptions, new SimpleConfigOptionsProvider(parsed, projectDir));
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out _);
        return (CSharpCompilation)updated;
    }

    private sealed class FileAdditionalText : AdditionalText
    {
        public FileAdditionalText(string path) => Path = path;
        public override string Path { get; }
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(System.Threading.CancellationToken cancellationToken = default) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(Path), System.Text.Encoding.UTF8);
    }

    private sealed class SimpleAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public System.Reflection.Assembly LoadFromPath(string fullPath) => System.Reflection.Assembly.LoadFrom(fullPath);
    }

    // Hands generators the global analyzer config the build passed (build_property.* values).
    private sealed class SimpleConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly DictionaryOptions _global;

        public SimpleConfigOptionsProvider(CSharpCommandLineArguments parsed, string projectDir)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in parsed.AnalyzerConfigPaths)
            {
                foreach (string line in File.ReadAllLines(Path.Combine(projectDir, path)))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !line.TrimStart().StartsWith("[") && !line.TrimStart().StartsWith("#"))
                    {
                        values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            _global = new DictionaryOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;

        private sealed class DictionaryOptions : AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> _values;
            public DictionaryOptions(Dictionary<string, string> values) => _values = values;
            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value);
        }
    }
}
