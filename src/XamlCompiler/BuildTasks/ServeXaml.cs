// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Markup.Compiler.MSBuildInterop;

namespace Microsoft.UI.Xaml.Markup.Compiler.Executable
{
    // XamlCompiler.exe --serve <pass 2 input.json> [<output folder>]
    //
    // Keeps a XamlCompilerService warm for a host that can't (or doesn't want to) load the compiler
    // assembly itself, such as native tooling. One JSON request per line on stdin, one JSON response
    // per line on stdout. An empty line or {"Shutdown":true} ends the process.
    // {"Op":"reload","InputJson":"<new input.json>"} takes new inputs (new references) without losing the warm compiler;
    // {"Op":"reload"} picks up referenced files rebuilt on disk.
    internal sealed class ServeRequest
    {
        public int Id { get; set; }
        public int Pass { get; set; }
        public List<string> Files { get; set; }
        public bool RegenerateTypeInfo { get; set; }
        // The project's assembly (or .winmd) as base64, for example a fresh in-memory compile.
        public string LocalAssemblyImage { get; set; }
        public string LocalAssemblyPath { get; set; }
        public bool Shutdown { get; set; }
        // "reload": take a new input.json (InputJson) or, without one, pick up referenced files rebuilt on disk.
        // Anything else (or null): compile.
        public string Op { get; set; }
        public string InputJson { get; set; }
    }

    internal sealed class ServeDiagnostic
    {
        public bool IsError { get; set; }
        public string Code { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Message { get; set; }
    }

    internal sealed class ServeResponse
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        public double ElapsedMs { get; set; }
        public bool XamlTypeInfoMayBeStale { get; set; }
        public Dictionary<string, string> GeneratedText { get; set; }
        // Base64.
        public Dictionary<string, string> GeneratedXbf { get; set; }
        public List<ServeDiagnostic> Diagnostics { get; set; }
        // Reload responses only.
        public List<string> Added { get; set; }
        public List<string> Removed { get; set; }
        public List<string> Released { get; set; }
        public bool FullReset { get; set; }
        public bool KeptLocalAssembly { get; set; }
    }

    internal static class ServeXaml
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: XamlCompiler.exe --serve <pass 2 input.json> [<output folder>]");
                return 1;
            }

            CompilerInputs inputs;
            using (FileStream stream = File.OpenRead(args[1]))
            {
                inputs = JsonSerializer.Deserialize<CompilerInputs>(stream);
            }

            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using (var service = new XamlCompilerService(inputs, args.Length > 2 ? args[2] : null))
            {
                string line;
                while (!string.IsNullOrWhiteSpace(line = stdin.ReadLine()))
                {
                    ServeRequest request = JsonSerializer.Deserialize<ServeRequest>(line);
                    if (request.Shutdown)
                    {
                        break;
                    }

                    if (string.Equals(request.Op, "reload", StringComparison.OrdinalIgnoreCase))
                    {
                        var reply = new ServeResponse { Id = request.Id, Diagnostics = new List<ServeDiagnostic>() };
                        try
                        {
                            XamlReloadResult reload;
                            if (request.InputJson != null)
                            {
                                using (FileStream stream = File.OpenRead(request.InputJson))
                                {
                                    reload = service.ReloadInputs(JsonSerializer.Deserialize<CompilerInputs>(stream));
                                }
                            }
                            else
                            {
                                reload = service.RefreshReferences();
                            }
                            reply.Success = true;
                            reply.ElapsedMs = reload.Elapsed.TotalMilliseconds;
                            reply.Added = reload.Added;
                            reply.Removed = reload.Removed;
                            reply.Released = reload.Released;
                            reply.FullReset = reload.FullReset;
                            reply.KeptLocalAssembly = reload.KeptLocalAssembly;
                        }
                        catch (Exception e)
                        {
                            reply.Diagnostics.Add(new ServeDiagnostic { IsError = true, Code = "WMC9999", Message = e.ToString() });
                        }
                        stdout.WriteLine(JsonSerializer.Serialize(reply));
                        continue;
                    }

                    IEnumerable<string> files = request.Files ?? (IEnumerable<string>)service.AllXamlFiles;
                    XamlCompileResult result = request.Pass == 1
                        ? service.CompilePass1(files)
                        : service.CompilePass2(
                            files,
                            request.LocalAssemblyImage != null ? Convert.FromBase64String(request.LocalAssemblyImage) : null,
                            request.RegenerateTypeInfo,
                            request.LocalAssemblyPath);

                    var response = new ServeResponse
                    {
                        Id = request.Id,
                        Success = result.Success,
                        ElapsedMs = result.Elapsed.TotalMilliseconds,
                        XamlTypeInfoMayBeStale = result.XamlTypeInfoMayBeStale,
                        GeneratedText = new Dictionary<string, string>(result.GeneratedText),
                        GeneratedXbf = result.GeneratedXbf.ToDictionary(p => p.Key, p => Convert.ToBase64String(p.Value)),
                        Diagnostics = result.Diagnostics.Select(d => new ServeDiagnostic
                        {
                            IsError = d.IsError,
                            Code = d.Code,
                            File = d.File,
                            Line = d.Line,
                            Column = d.Column,
                            Message = d.Message,
                        }).ToList(),
                    };
                    stdout.WriteLine(JsonSerializer.Serialize(response));
                }
            }
            return 0;
        }
    }
}
