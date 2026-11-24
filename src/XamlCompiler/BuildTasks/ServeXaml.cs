// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Markup.Compiler.MSBuildInterop;

namespace Microsoft.UI.Xaml.Markup.Compiler.Executable
{
    // XamlCompiler.exe --serve <input.json>: keeps the compiler loaded for one project and compiles pages on request.
    // One JSON request per line on stdin, one JSON response per line on stdout (docs\design-notes\xamlcompiler-hot-reload.md).
    internal static class ServeXaml
    {
        private sealed class Request
        {
            public int Id { get; set; }
            public int Pass { get; set; }
            public List<string> Files { get; set; }
            public byte[] LocalAssemblyImage { get; set; }
        }

        private sealed class Response
        {
            public int Id { get; set; }
            public bool Success { get; set; }
            public double ElapsedMs { get; set; }
            public Dictionary<string, string> Files { get; set; }
            public Dictionary<string, byte[]> Xbf { get; set; }
            public List<MSBuildLogEntry> Diagnostics { get; set; }
        }

        public static int Run(string inputJsonFile)
        {
            var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            CompilerInputs inputs;
            using (FileStream stream = File.OpenRead(inputJsonFile))
            {
                inputs = JsonSerializer.Deserialize<CompilerInputs>(stream);
            }

            string outputFolder = Path.Combine(Path.GetTempPath(), "XamlCompilerService", Process.GetCurrentProcess().Id.ToString());
            var service = new XamlCompilerService(inputs, outputFolder);
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            // Responses are the only output: anything else the compiler prints goes to stderr.
            Console.SetOut(Console.Error);
            try
            {
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    Response response;
                    int id = 0;
                    try
                    {
                        Request request = JsonSerializer.Deserialize<Request>(line, json);
                        id = request.Id;
                        XamlCompileResult result = service.Compile(request.Pass, request.Files, request.LocalAssemblyImage);
                        response = new Response
                        {
                            Id = request.Id,
                            Success = result.Success,
                            ElapsedMs = result.ElapsedMs,
                            Files = result.Files,
                            Xbf = result.Xbf,
                            Diagnostics = result.Diagnostics,
                        };
                    }
                    catch (Exception e)
                    {
                        response = new Response
                        {
                            Id = id,
                            Diagnostics = new List<MSBuildLogEntry> { new MSBuildLogEntry(MSBuildLogEntry.EntryType.Error, null, "WMC9999", null, null, 0, 0, 0, 0, e.ToString()) },
                        };
                    }
                    stdout.WriteLine(JsonSerializer.Serialize(response, json));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(outputFolder, recursive: true);
                }
                catch (IOException)
                {
                }
            }
            return 0;
        }
    }
}
