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
//   prepare <build input.json> <outputPath> <destDir>
//       Writes pass1.json/pass2.json for XamlCompiler.exe, redirected to <outputPath>
//       (relative to the project), so a full out-of-process build can be the byte-for-byte baseline.
//
//   bench <build input.json> <outputPath> --pages a.xaml[,b.xaml] [--iterations N]
//         [--typeinfo all|skip] [--asm file|image|tempdll] [--asm-path <dll>] [--warmup full|none]
//       Creates a warm service, then recompiles the given pages N times (pass 1 + pass 2) and
//       prints per-phase timings.
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
        switch (args[0])
        {
            case "prepare": return Prepare(args[1], args[2], args[3]);
            case "bench": return Bench(args.Skip(1).ToArray());
            case "emit": return Emit(args.Skip(1).ToArray());
            default:
                Console.WriteLine($"unknown command {args[0]}");
                return 1;
        }
    }

    private static CompilerInputs ReadInputs(string path)
    {
        using FileStream s = File.OpenRead(path);
        return JsonSerializer.Deserialize<CompilerInputs>(s);
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

        Console.WriteLine($"process total {processClock.Elapsed.TotalMilliseconds:F0} ms, working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB");
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
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parsed.ParseOptions, optionsProvider: optionsProvider);
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
        SyntaxTree first = compilation.SyntaxTrees.First(t => t.FilePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase));
        CSharpCompilation edited = compilation.ReplaceSyntaxTree(first, first.WithChangedText(first.GetText().Replace(0, 0, "// edit\r\n")));
        using (var m = new MemoryStream())
        {
            edited.Emit(m, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true));
        }
        Console.WriteLine($"metadata-only emit after editing one generated file: {w.Elapsed.TotalMilliseconds:F0} ms");
        return 0;
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
