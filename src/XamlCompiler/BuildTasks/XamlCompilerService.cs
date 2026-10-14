// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Markup.Compiler.MSBuildInterop;

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    /// <summary>
    /// Runs the XAML compiler in-process and keeps it warm between compiles, so a tool such as
    /// hot reload can recompile one changed page with the same code a build uses.
    ///
    /// Create it once per project from the inputs the build passed to CompileXaml (the pass 2
    /// input.json that the build writes next to the generated files). Each call then compiles
    /// only the XAML files it is given and returns the generated files.
    ///
    /// The compiler's type universe is process-wide, so use one service per process and don't
    /// run builds of other projects in the same process.
    /// </summary>
    public sealed class XamlCompilerService : IDisposable
    {
        private readonly CompilerInputs _pass2Inputs;
        private readonly string _outputPath;
        // Hot reload state (connection ID ledger, retired members) stays with the build's output, so the next build
        // continues the same history as the running app.
        private readonly string _buildOutputFolder;
        private readonly string _savedStateFile;
        private SavedStateManager _savedState;
        private HashSet<string> _typeInfoKeys;
        private bool _disposed;

        /// <param name="buildInputs">The pass 2 CompileXaml inputs captured from a build of the project.</param>
        /// <param name="outputPath">
        /// Where generated files go. Null writes to the build's own output folder; pass a separate
        /// folder to leave the build's incremental state untouched.
        /// </param>
        public XamlCompilerService(CompilerInputs buildInputs, string outputPath = null)
        {
            _pass2Inputs = buildInputs ?? throw new ArgumentNullException(nameof(buildInputs));
            ProjectFolder = Path.GetDirectoryName(buildInputs.ProjectPath);
            _outputPath = outputPath ?? buildInputs.OutputPath;
            _buildOutputFolder = Path.GetFullPath(Path.IsPathRooted(buildInputs.OutputPath) ? buildInputs.OutputPath : Path.Combine(ProjectFolder, buildInputs.OutputPath));
            string fullOutputPath = Path.IsPathRooted(_outputPath) ? _outputPath : Path.Combine(ProjectFolder, _outputPath);
            Directory.CreateDirectory(fullOutputPath);
            _savedStateFile = Path.Combine(fullOutputPath, "XamlSaveStateFile.xml");
            _savedState = SavedStateManager.Load(_savedStateFile);
        }

        public string ProjectFolder { get; }

        /// <summary>All XAML files in the project, as full paths.</summary>
        public IReadOnlyList<string> AllXamlFiles =>
            (_pass2Inputs.XamlApplications ?? new List<MSBuildItem>())
                .Concat(_pass2Inputs.XamlPages ?? new List<MSBuildItem>())
                .Select(i => FullPathOf(i))
                .ToList();

        /// <summary>
        /// Pass 1: generates the .g.i.cs (C#) or .g.h (C++) parts that the project's own code compiles against.
        /// Doesn't need the project's assembly.
        /// </summary>
        public XamlCompileResult CompilePass1(IEnumerable<string> xamlFiles)
        {
            return Compile(isPass1: true, xamlFiles, localAssemblyImage: null, regenerateTypeInfo: true);
        }

        /// <summary>
        /// Pass 2: generates the .g.cs/.g.hpp, the .xbf and (optionally) XamlTypeInfo.
        /// </summary>
        /// <param name="localAssemblyImage">
        /// The project's assembly as a PE image, for example emitted from the Roslyn compilation the
        /// caller already holds. Null reads the LocalAssembly file named by the build inputs.
        /// </param>
        /// <param name="regenerateTypeInfo">
        /// True reads every page for type information and regenerates XamlTypeInfo, exactly as a
        /// build does. False reads only the given pages and skips XamlTypeInfo, which is much faster
        /// but only correct when the edit didn't change which types the project's XAML uses.
        /// </param>
        /// <param name="localAssemblyPath">
        /// The project's assembly on disk, when it isn't the LocalAssembly the build used
        /// (for example a temporary file). Ignored when <paramref name="localAssemblyImage"/> is given.
        /// </param>
        public XamlCompileResult CompilePass2(IEnumerable<string> xamlFiles, byte[] localAssemblyImage = null, bool regenerateTypeInfo = true, string localAssemblyPath = null)
        {
            return Compile(isPass1: false, xamlFiles, localAssemblyImage, regenerateTypeInfo, localAssemblyPath);
        }

        private XamlCompileResult Compile(bool isPass1, IEnumerable<string> xamlFiles, byte[] localAssemblyImage, bool regenerateTypeInfo, string localAssemblyPath = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(XamlCompilerService));
            }

            var result = new XamlCompileResult();
            var log = new ServiceLog(result);
            string previousDirectory = Environment.CurrentDirectory;
            Stopwatch total = Stopwatch.StartNew();
            try
            {
                // The build runs the compiler from the project folder and some inputs are relative to it.
                Environment.CurrentDirectory = ProjectFolder;

                var core = new CompileXamlInternal();
                core.Log = log;
                core.TaskFileService = new BuildTaskFileService(_pass2Inputs.LanguageSourceExtension);
                core.PopulateFromCompilerInputs(CreateInputs(isPass1, localAssemblyPath));
                core.SaveState = _savedState;
                core.ServiceXamlFilesToCompile = new HashSet<string>(
                    xamlFiles.Select(f => Path.GetFullPath(Path.Combine(ProjectFolder, f))),
                    StringComparer.OrdinalIgnoreCase);
                core.ServiceCollectAllTypeInfo = regenerateTypeInfo;
                core.ServiceLocalAssemblyImage = isPass1 ? null : localAssemblyImage;
                core.ServiceWrittenFiles = result.GeneratedText;
                core.ServiceHotReloadStateFolder = _buildOutputFolder;

                result.Success = core.DoExecute();
                if (result.Success)
                {
                    core.UpdateSavedStateInMemory();
                    if (!isPass1)
                    {
                        HashSet<string> keys = core.GetCollectedTypeInfoKeys();
                        if (regenerateTypeInfo)
                        {
                            _typeInfoKeys = keys;
                        }
                        else
                        {
                            // Removals aren't detected: a type that is no longer used stays in XamlTypeInfo
                            // until the next full compile, which is harmless at run time.
                            result.XamlTypeInfoMayBeStale = _typeInfoKeys == null || !keys.IsSubsetOf(_typeInfoKeys);
                        }
                    }
                }
                foreach (string xbf in isPass1 ? Enumerable.Empty<string>() : core.GeneratedXbfFiles)
                {
                    string fullPath = Path.GetFullPath(xbf);
                    if (File.Exists(fullPath) && IsForRequestedFile(fullPath, core.ServiceXamlFilesToCompile))
                    {
                        result.GeneratedXbf[fullPath] = File.ReadAllBytes(fullPath);
                    }
                }
            }
            catch (Exception e)
            {
                result.Success = false;
                result.Diagnostics.Add(new XamlCompileDiagnostic(true, "WMC9999", null, 0, 0, e.ToString()));
            }
            finally
            {
                Environment.CurrentDirectory = previousDirectory;
                total.Stop();
                result.Elapsed = total.Elapsed;
            }
            return result;
        }

        // GeneratedXbfFiles lists every page's .xbf; return only the ones this compile produced.
        private static bool IsForRequestedFile(string xbfPath, ISet<string> requested)
        {
            string name = Path.GetFileNameWithoutExtension(xbfPath);
            return requested.Any(r => string.Equals(Path.GetFileNameWithoutExtension(r), name, StringComparison.OrdinalIgnoreCase));
        }

        private CompilerInputs CreateInputs(bool isPass1, string localAssemblyPath)
        {
            CompilerInputs i = _pass2Inputs;
            List<MSBuildItem> localAssembly = i.LocalAssembly;
            if (!isPass1 && localAssemblyPath != null)
            {
                localAssembly = new List<MSBuildItem> { new MSBuildItem { ItemSpec = localAssemblyPath, FullPath = Path.GetFullPath(localAssemblyPath) } };
            }
            var copy = new CompilerInputs
            {
                ProjectPath = i.ProjectPath,
                Language = i.Language,
                LanguageSourceExtension = i.LanguageSourceExtension,
                OutputPath = _outputPath,
                ReferenceAssemblies = i.ReferenceAssemblies,
                TargetPlatformMinVersion = i.TargetPlatformMinVersion,
                ReferenceAssemblyPaths = i.ReferenceAssemblyPaths,
                BuildConfiguration = i.BuildConfiguration,
                ForceSharedStateShutdown = false,
                DisableXbfGeneration = i.DisableXbfGeneration,
                DisableXbfLineInfo = i.DisableXbfLineInfo,
                EnableXBindDiagnostics = i.EnableXBindDiagnostics,
                ClIncludeFiles = i.ClIncludeFiles,
                CIncludeDirectories = i.CIncludeDirectories,
                XamlApplications = i.XamlApplications,
                XamlPages = i.XamlPages,
                LocalAssembly = isPass1 ? null : localAssembly,
                SdkXamlPages = isPass1 ? null : i.SdkXamlPages,
                ProjectName = i.ProjectName,
                IsPass1 = isPass1,
                RootNamespace = i.RootNamespace,
                OutputType = i.OutputType,
                PriIndexName = i.PriIndexName,
                CodeGenerationControlFlags = i.CodeGenerationControlFlags,
                FeatureControlFlags = i.FeatureControlFlags,
                XAMLFingerprint = i.XAMLFingerprint,
                UseVCMetaManaged = i.UseVCMetaManaged,
                FingerprintIgnorePaths = i.FingerprintIgnorePaths,
                VCInstallDir = i.VCInstallDir,
                VCInstallPath32 = i.VCInstallPath32,
                VCInstallPath64 = i.VCInstallPath64,
                WindowsSdkPath = i.WindowsSdkPath,
                CompileMode = isPass1 ? "RealBuildPass1" : "RealBuildPass2",
                SavedStateFile = _savedStateFile,
                RootsLog = isPass1 ? null : i.RootsLog,
                SuppressWarnings = i.SuppressWarnings,
                GenXbfPath = i.GenXbfPath,
                PrecompiledHeaderFile = i.PrecompiledHeaderFile,
                XamlResourceMapName = i.XamlResourceMapName,
                XamlComponentResourceLocation = i.XamlComponentResourceLocation,
                XamlPlatform = i.XamlPlatform,
                TargetFileName = i.TargetFileName,
                IgnoreSpecifiedTargetPlatformMinVersion = i.IgnoreSpecifiedTargetPlatformMinVersion,
                EnableHotReloadStableConnectionIds = i.EnableHotReloadStableConnectionIds,
                EnableXamlHotReload = i.EnableXamlHotReload,
                EnabledXamlOptionalChanges = i.EnabledXamlOptionalChanges,
                DisabledXamlOptionalChanges = i.DisabledXamlOptionalChanges,
            };
            return copy;
        }

        private string FullPathOf(MSBuildItem item)
        {
            return Path.GetFullPath(Path.Combine(ProjectFolder, item.ItemSpec));
        }

        /// <summary>Releases the warm type universe.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                new CompileXamlInternal().UnloadReferences();
            }
        }

        private sealed class ServiceLog : ILog
        {
            private const string MarkerPrefix = "Xaml Compiler Marker: ";
            private readonly XamlCompileResult _result;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public ServiceLog(XamlCompileResult result)
            {
                _result = result;
            }

            public void LogError(string subcategory, string errorCode, string helpKeyword, string file, int lineNumber, int columnNumber, int endLineNumber, int endColumnNumber, string message)
            {
                _result.Diagnostics.Add(new XamlCompileDiagnostic(true, errorCode, file, lineNumber, columnNumber, message));
            }

            public void LogWarning(string subcategory, string warningCode, string helpKeyword, string file, int lineNumber, int columnNumber, int endLineNumber, int endColumnNumber, string message)
            {
                _result.Diagnostics.Add(new XamlCompileDiagnostic(false, warningCode, file, lineNumber, columnNumber, message));
            }

            public void LogDiagnosticMessage(string message)
            {
                if (message == null || !message.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                {
                    return;
                }
                // "Xaml Compiler Marker: <time>:<ms> perfXC_Name[, info]" (the time may contain spaces)
                string rest = message.Substring(MarkerPrefix.Length);
                int nameStart = rest.IndexOf("perfXC_", StringComparison.Ordinal);
                string marker = nameStart >= 0 ? rest.Substring(nameStart) : rest;
                string info = null;
                int comma = marker.IndexOf(", ", StringComparison.Ordinal);
                if (comma >= 0)
                {
                    info = marker.Substring(comma + 2);
                    marker = marker.Substring(0, comma);
                }
                _result.Markers.Add(new XamlCompileMarker(marker, info, _clock.Elapsed.TotalMilliseconds));
            }
        }
    }

    public sealed class XamlCompileResult
    {
        public bool Success { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }

        /// <summary>
        /// Pass 2 with regenerateTypeInfo: false only. True when the compiled pages use a type or
        /// member that the last full compile didn't put in XamlTypeInfo, so the caller must run
        /// CompilePass2 with regenerateTypeInfo: true before the app can rely on it.
        /// </summary>
        public bool XamlTypeInfoMayBeStale { get; internal set; }

        /// <summary>Generated source and edited XAML, keyed by full output path.</summary>
        public IDictionary<string, string> GeneratedText { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Generated .xbf files for the requested pages, keyed by full output path.</summary>
        public IDictionary<string, byte[]> GeneratedXbf { get; } = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public IList<XamlCompileDiagnostic> Diagnostics { get; } = new List<XamlCompileDiagnostic>();

        /// <summary>The compiler's performance markers, with milliseconds since the compile started.</summary>
        public IList<XamlCompileMarker> Markers { get; } = new List<XamlCompileMarker>();
    }

    public sealed class XamlCompileDiagnostic
    {
        public XamlCompileDiagnostic(bool isError, string code, string file, int line, int column, string message)
        {
            IsError = isError;
            Code = code;
            File = file;
            Line = line;
            Column = column;
            Message = message;
        }

        public bool IsError { get; }
        public string Code { get; }
        public string File { get; }
        public int Line { get; }
        public int Column { get; }
        public string Message { get; }

        public override string ToString()
        {
            return $"{File}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
        }
    }

    public sealed class XamlCompileMarker
    {
        public XamlCompileMarker(string name, string info, double milliseconds)
        {
            Name = name;
            Info = info;
            Milliseconds = milliseconds;
        }

        public string Name { get; }
        public string Info { get; }
        public double Milliseconds { get; }
    }
}
