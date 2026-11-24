// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using MSBuildInterop;

    // Keeps the XAML compiler loaded so that a hot reload tool can compile the pages that changed, with the same code and
    // the same results as the project's build. XamlCompiler.exe --serve runs one (docs\design-notes\xamlcompiler-hot-reload.md).
    //
    // It starts from the pass 2 inputs the build wrote (obj\...\input.json). Compiles write to the service's own folder,
    // never to the build's, and each result names a file by where the build puts it.
    internal sealed class XamlCompilerService
    {
        private readonly CompilerInputs _inputs;
        private readonly CompilerInputs _pass2;
        private readonly string _projectFolder;
        private readonly string _buildOutputFolder;
        private readonly string _outputFolder;
        private readonly SavedStateManager _savedState;
        private readonly XamlHotReloadHistory _history;

        // The types and members in the app's XamlTypeInfo. Null until a pass 2 has read every page.
        private HashSet<string> _typeInfo;

        // Why the inputs can't be compiled from, or null.
        private readonly string _inputsError;

        // outputFolder: where compiles write; it is emptied before each compile.
        public XamlCompilerService(CompilerInputs buildInputs, string outputFolder)
        {
            _inputs = buildInputs;
            // A design-time build (an editor's) writes its own pass 1 inputs to the same input.json.
            if (buildInputs.IsPass1 || buildInputs.CompileMode != "RealBuildPass2" || !(buildInputs.LocalAssembly?.Count > 0))
            {
                _inputsError = $"The inputs aren't those of a build's pass 2 (CompileMode '{buildInputs.CompileMode}'" +
                    (buildInputs.LocalAssembly?.Count > 0 ? "" : ", no LocalAssembly") + "): a design-time build may have " +
                    "rewritten input.json. Start the service from the input.json a build wrote, for example a copy taken when it finished.";
                return;
            }
            // What pass 2 gets and pass 1 doesn't.
            _pass2 = new CompilerInputs
            {
                LocalAssembly = buildInputs.LocalAssembly,
                SdkXamlPages = buildInputs.SdkXamlPages,
                DisableXbfGeneration = buildInputs.DisableXbfGeneration,
                DisableXbfLineInfo = buildInputs.DisableXbfLineInfo,
                ClIncludeFiles = buildInputs.ClIncludeFiles,
            };
            _inputs.XamlPages = _inputs.XamlPages ?? new List<MSBuildItem>();
            // A trimming roots file for .NET Native; a running app doesn't need it.
            _inputs.RootsLog = null;
            _projectFolder = Path.GetDirectoryName(Path.GetFullPath(buildInputs.ProjectPath));
            _buildOutputFolder = Path.GetFullPath(Path.Combine(_projectFolder, buildInputs.OutputPath));
            _outputFolder = Path.GetFullPath(outputFolder);
            // The build's saved state, kept in memory: the service never writes it.
            _savedState = SavedStateManager.Load(Path.Combine(_projectFolder, buildInputs.SavedStateFile ?? string.Empty));
            if (buildInputs.EnableXamlHotReloadCodegen && buildInputs.Language == ProgrammingLanguage.CSharp)
            {
                _history = new XamlHotReloadHistory(_buildOutputFolder);
            }
        }

        // Compiles the given XAML files (paths relative to the project, or full) the way the build's pass 1 or pass 2
        // would, and returns their generated files. A file the project doesn't list yet is added as a page.
        // localAssemblyImage (pass 2): the project's assembly, for example a metadata-only emit with C# the build hasn't
        // compiled yet; null reads the build's.
        public XamlCompileResult Compile(int pass, IEnumerable<string> xamlFiles, byte[] localAssemblyImage)
        {
            Stopwatch clock = Stopwatch.StartNew();
            var result = new XamlCompileResult();
            if (_inputsError != null)
            {
                result.Diagnostics.Add(new MSBuildLogEntry(MSBuildLogEntry.EntryType.Error, null, "WMC9999", null, null, 0, 0, 0, 0, _inputsError));
                return result;
            }
            var files = new HashSet<string>(xamlFiles.Select(f => Path.GetFullPath(Path.Combine(_projectFolder, f))), StringComparer.OrdinalIgnoreCase);
            foreach (string file in files.Where(f => !File.Exists(f)))
            {
                result.Diagnostics.Add(new MSBuildLogEntry(MSBuildLogEntry.EntryType.Error, null, "WMC9999", null, file, 0, 0, 0, 0, "The file doesn't exist."));
            }
            if (result.Diagnostics.Count > 0)
            {
                return result;
            }
            ISet<string> known = AllXamlFiles();
            foreach (string file in files.Where(f => !known.Contains(f)))
            {
                _inputs.XamlPages.Add(new MSBuildItem
                {
                    ItemSpec = Utilities.FileHelpers.GetRelativePath(_projectFolder, file),
                    FullPath = file,
                    DependentUpon = string.Empty,
                    MSBuild_Link = string.Empty,
                    MSBuild_TargetPath = string.Empty,
                    MSBuild_XamlResourceMapName = string.Empty,
                    MSBuild_XamlComponentResourceLocation = string.Empty,
                });
            }

            if (pass == 1)
            {
                result = Run(isPass1: true, files, localAssemblyImage: null, generateTypeInfo: false, out _);
            }
            else
            {
                // Only a compile of every page generates XamlTypeInfo as a build does: pages add types to it while their
                // code is generated. The first pass 2 does that, so the service knows what the app's XamlTypeInfo has.
                bool generateTypeInfo = _typeInfo == null;
                result = Run(isPass1: false, files, localAssemblyImage, generateTypeInfo, out HashSet<string> typeInfo);
                if (result.Success && !generateTypeInfo && !typeInfo.IsSubsetOf(_typeInfo))
                {
                    // The pages use a type or member the app's XamlTypeInfo doesn't have.
                    generateTypeInfo = true;
                    result = Run(isPass1: false, files, localAssemblyImage, generateTypeInfo, out typeInfo);
                }
                if (result.Success && generateTypeInfo)
                {
                    _typeInfo = typeInfo;
                }
            }
            result.ElapsedMs = clock.Elapsed.TotalMilliseconds;
            return result;
        }

        private XamlCompileResult Run(bool isPass1, ISet<string> files, byte[] localAssemblyImage, bool generateTypeInfo, out HashSet<string> typeInfo)
        {
            var result = new XamlCompileResult();
            typeInfo = null;

            // Only what this compile writes is in the folder afterwards.
            if (Directory.Exists(_outputFolder))
            {
                Directory.Delete(_outputFolder, recursive: true);
            }
            Directory.CreateDirectory(_outputFolder);

            // As the build runs pass 1 and pass 2 (see MarkupCompilePass1 and MarkupCompilePass2 in the targets).
            _inputs.IsPass1 = isPass1;
            _inputs.CompileMode = isPass1 ? "RealBuildPass1" : "RealBuildPass2";
            _inputs.LocalAssembly = isPass1 ? null : _pass2.LocalAssembly;
            _inputs.SdkXamlPages = isPass1 ? null : _pass2.SdkXamlPages;
            _inputs.DisableXbfGeneration = !isPass1 && _pass2.DisableXbfGeneration;
            _inputs.DisableXbfLineInfo = !isPass1 && _pass2.DisableXbfLineInfo;
            _inputs.ClIncludeFiles = isPass1 ? null : _pass2.ClIncludeFiles;
            _inputs.OutputPath = _outputFolder;

            var core = new CompileXamlInternal();
            string currentDirectory = Environment.CurrentDirectory;
            var outputs = new List<string>();
            try
            {
                // Some inputs are relative to the project, as in a build.
                Environment.CurrentDirectory = _projectFolder;
                core.Log = new Log(result.Diagnostics);
                core.TaskFileService = new BuildTaskFileService(_inputs.LanguageSourceExtension);
                core.PopulateFromCompilerInputs(_inputs);
                core.SaveState = _savedState;
                core.ServiceFiles = generateTypeInfo ? AllXamlFiles() : files;
                core.ServiceCollectsAllTypeInfo = generateTypeInfo;
                core.ServiceLocalAssemblyImage = localAssemblyImage;
                core.ServiceHistory = _history;
                result.Success = core.DoExecute() && !result.Diagnostics.Any(d => d.Type == MSBuildLogEntry.EntryType.Error);
                if (result.Success && !isPass1)
                {
                    typeInfo = core.CollectedTypeInfo();
                }

                // The requested files' outputs. Pass 1's are its code files: it also empties the pass 2 code files (so a
                // build runs pass 2) and, for C#, writes a placeholder XamlTypeInfo for the build's first C# compile.
                var prefixes = core.OutputPrefixes(files).ToList();
                outputs = Directory.EnumerateFiles(_outputFolder, "*", SearchOption.AllDirectories)
                    .Where(path => isPass1
                        ? path.EndsWith(core.Language.Pass1Extension, StringComparison.OrdinalIgnoreCase) && prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        : prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || (generateTypeInfo && Path.GetFileName(path).StartsWith(KnownStrings.XamlTypeInfo + ".", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            catch (Exception e)
            {
                result.Success = false;
                core.LogError_XamlInternalError(e, null);
            }
            finally
            {
                Environment.CurrentDirectory = currentDirectory;
            }

            foreach (string path in outputs)
            {
                string buildPath = Path.Combine(_buildOutputFolder, path.Substring(_outputFolder.Length).TrimStart('\\'));
                if (path.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase))
                {
                    result.Xbf[buildPath] = File.ReadAllBytes(path);
                    continue;
                }
                string text = File.ReadAllText(path);
                if (result.Success && _history != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    text = KeptMembers.AddRemovedMembers(_history.GeneratedText(buildPath), text);
                    _history.SetGeneratedText(buildPath, text);
                }
                result.Files[buildPath] = text;
            }
            return result;
        }

        private ISet<string> AllXamlFiles()
        {
            return new HashSet<string>((_inputs.XamlApplications ?? new List<MSBuildItem>()).Concat(_inputs.XamlPages)
                .Select(item => Path.GetFullPath(Path.Combine(_projectFolder, item.ItemSpec))), StringComparer.OrdinalIgnoreCase);
        }
        private sealed class Log : ILog
        {
            private readonly List<MSBuildLogEntry> _entries;

            public Log(List<MSBuildLogEntry> entries)
            {
                _entries = entries;
            }

            public void LogError(string subcategory, string errorCode, string helpKeyword, string file, int lineNumber, int columnNumber, int endLineNumber, int endColumnNumber, string message)
            {
                _entries.Add(new MSBuildLogEntry(MSBuildLogEntry.EntryType.Error, subcategory, errorCode, helpKeyword, file, lineNumber, columnNumber, endLineNumber, endColumnNumber, message));
            }

            public void LogWarning(string subcategory, string warningCode, string helpKeyword, string file, int lineNumber, int columnNumber, int endLineNumber, int endColumnNumber, string message)
            {
                _entries.Add(new MSBuildLogEntry(MSBuildLogEntry.EntryType.Warning, subcategory, warningCode, helpKeyword, file, lineNumber, columnNumber, endLineNumber, endColumnNumber, message));
            }

            public void LogDiagnosticMessage(string message)
            {
            }
        }
    }

    internal sealed class XamlCompileResult
    {
        public bool Success { get; set; }
        public double ElapsedMs { get; set; }

        // Generated text files and .xbf files, by where the build puts them.
        public Dictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> Xbf { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // Errors and warnings, as the build reports them.
        public List<MSBuildLogEntry> Diagnostics { get; } = new List<MSBuildLogEntry>();
    }
}
