// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.Loader;

namespace XamlHotReloadEncHarness;

// Replays a hot reload journey through the XAML compiler service and the .NET SDK's edit-and-continue engine.
//
//   XamlHotReloadEncHarness <project.csproj> <journey.json> --compiler <XamlCompiler.exe>
//       [--mode off|stable|hot-reload] [--sdk <dotnet sdk dir>] [--extra] [--out <report.md>]
//
// The project must have been built once with 'dotnet build' (so obj holds the XAML compiler's input.json) and must
// record its compiler arguments in obj\...\csc.args (see the TodoBench Directory.Build.targets). Its folder must be a
// git repository: the journey edits the sources, and they are reset (git checkout + clean) before and after a run.
//
//   --mode off         today's Debug build: no stable connection IDs, no hot reload code generation
//   --mode stable      Prototype A stable connection IDs only (what the S1/S2 spike ran)
//   --mode hot-reload  EnableXamlHotReload: stable IDs plus the hot reload code generation (default)
//   --extra            extra steps after the journey (an x:Name element changing type)
//   --verify-build     after every XAML save, build the same sources and state with XamlCompiler.exe and compare
//                      every generated file with the service's (tombstones are the one intended difference)
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string sdk = Option(args, "--sdk") ?? DefaultSdkDirectory();
        RegisterSdkRoslyn(sdk);
        return await Harness.RunAsync(args, sdk);
    }

    internal static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string DefaultSdkDirectory()
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)!)!;
        string sdkRoot = Path.Combine(root, "..", "..", "sdk");
        return Directory.GetDirectories(sdkRoot)
            .Where(d => File.Exists(Path.Combine(d, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll")))
            .OrderBy(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out Version? v) ? v : new Version())
            .Last();
    }

    // The SDK's compiler (Roslyn\bincore) and dotnet watch's workspace and hot reload service.
    private static void RegisterSdkRoslyn(string sdk)
    {
        string bincore = Path.Combine(sdk, "Roslyn", "bincore");
        string watch = Path.GetDirectoryName(Directory.EnumerateFiles(Path.Combine(sdk, "DotnetTools", "dotnet-watch"),
            "Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll", SearchOption.AllDirectories).First())!;
        string[] directories = [bincore, watch];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string directory in directories)
            {
                string path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path))
                {
                    return context.LoadFromAssemblyPath(path);
                }
            }
            return null;
        };
    }
}
