// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    // EnableXamlHotReloadCodegen: what a build leaves for the compiler service in <generated files folder>\XamlHotReload,
    // and what the service carries from one compile to the next.
    //
    //   <hash of the class name>.ids   each page's connection ID keys (see ConnectionIdKeys)
    //   typeinfo.txt                   the order of the types in XamlTypeInfo
    //   members.txt                    the order of the members in XamlTypeInfo (their accessors are numbered by it)
    //   code\<hash of the path>.cs     a copy of each generated C# file the build compiled into the app: the members the
    //                                  app has (see KeptMembers). The generated files themselves don't say that once
    //                                  another build has started: pass 1 empties <Page>.g.cs, and design-time builds
    //                                  (an editor's, into the same folder) rewrite them.
    //
    // A build only writes these files and never reads them, so its output doesn't depend on an earlier build. The compiler
    // service only reads them, and never writes to the build's folders, so a build can run while the service is running:
    // each file is replaced in one step, and read with sharing that allows that. When a build has rewritten a file, the
    // app was restarted from that build, so the service starts again from the file.
    internal sealed class XamlHotReloadHistory
    {
        public const string FolderName = "XamlHotReload";

        private readonly string _buildOutputFolder;
        private readonly string _folder;
        private readonly Dictionary<string, (DateTime BuildWrite, object Value)> _files =
            new Dictionary<string, (DateTime, object)>(StringComparer.OrdinalIgnoreCase);

        public XamlHotReloadHistory(string buildOutputFolder)
        {
            _buildOutputFolder = buildOutputFolder;
            _folder = FolderOf(buildOutputFolder);
        }

        public static string FolderOf(string outputFolder) => Path.Combine(outputFolder, FolderName);

        public static string CodeFolderOf(string outputFolder) => Path.Combine(FolderOf(outputFolder), "code");

        // The connection IDs given out for a class so far. A compile adds its new elements to the same dictionary.
        public Dictionary<string, int> ConnectionIds(string className)
        {
            return Get(ConnectionIdsPath(_folder, className), ReadConnectionIds);
        }

        // The type order of the XamlTypeInfo the app has.
        public IList<string> TypeOrder
        {
            get { return Get(TypeOrderPath(_folder), path => ReadLines(path)?.ToList() ?? new List<string>()); }
            set { Set(TypeOrderPath(_folder), value); }
        }

        // The member order of the XamlTypeInfo the app has.
        public IList<string> MemberOrder
        {
            get { return Get(MemberOrderPath(_folder), path => ReadLines(path)?.ToList() ?? new List<string>()); }
            set { Set(MemberOrderPath(_folder), value); }
        }

        // The text of a generated file as the app has it: the build's copy, then each compile's output.
        public string GeneratedText(string buildPath)
        {
            return Get(CodePath(_buildOutputFolder, buildPath), path => File.Exists(path) ? ReadAllText(path) : null);
        }

        public void SetGeneratedText(string buildPath, string text)
        {
            GeneratedText(buildPath);
            Set(CodePath(_buildOutputFolder, buildPath), text);
        }

        // Copies a generated file the build just wrote (or kept). Returns the copy's path.
        public static string WriteGeneratedText(string outputFolder, string buildPath)
        {
            string path = CodePath(outputFolder, buildPath);
            WriteIfChanged(path, ReadAllText(buildPath));
            return path;
        }

        public static void WriteConnectionIds(string folder, string className, IDictionary<string, int> ids)
        {
            WriteIfChanged(ConnectionIdsPath(folder, className),
                new[] { className }.Concat(ids.OrderBy(p => p.Value).Select(p => p.Value + "\t" + p.Key)));
        }

        public static void WriteTypeOrder(string folder, IEnumerable<string> typeNames)
        {
            WriteIfChanged(TypeOrderPath(folder), typeNames);
        }

        public static void WriteMemberOrder(string folder, IEnumerable<string> memberKeys)
        {
            WriteIfChanged(MemberOrderPath(folder), memberKeys);
        }

        // State from a compiler that recorded the type order but not the member order.
        public static bool LacksMemberOrder(string outputFolder)
        {
            string folder = FolderOf(outputFolder);
            return File.Exists(TypeOrderPath(folder)) && !File.Exists(MemberOrderPath(folder));
        }

        private static string ConnectionIdsPath(string folder, string className)
        {
            return Path.Combine(folder, Hash(className) + ".ids");
        }

        private static string CodePath(string outputFolder, string buildPath)
        {
            string relative = buildPath.Substring(outputFolder.Length).TrimStart('\\');
            return Path.Combine(CodeFolderOf(outputFolder), Hash(relative.ToUpperInvariant()) + ".cs");
        }

        // File names are hashed: class names and paths can be long, and the compiler runs where paths are limited to
        // 260 characters.
        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
        }

        private static string TypeOrderPath(string folder) => Path.Combine(folder, "typeinfo.txt");

        private static string MemberOrderPath(string folder) => Path.Combine(folder, "members.txt");

        private static Dictionary<string, int> ReadConnectionIds(string path)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in ReadLines(path)?.Skip(1) ?? Enumerable.Empty<string>())
            {
                int tab = line.IndexOf('\t');
                ids[line.Substring(tab + 1)] = int.Parse(line.Substring(0, tab), System.Globalization.CultureInfo.InvariantCulture);
            }
            return ids;
        }

        private T Get<T>(string path, Func<string, T> read)
        {
            DateTime buildWrite = File.GetLastWriteTimeUtc(path);
            if (!_files.TryGetValue(path, out var known) || known.BuildWrite != buildWrite)
            {
                known = (buildWrite, read(path));
                _files[path] = known;
            }
            return (T)known.Value;
        }

        private void Set(string path, object value)
        {
            _files[path] = (_files.TryGetValue(path, out var known) ? known.BuildWrite : File.GetLastWriteTimeUtc(path), value);
        }

        private static string[] ReadLines(string path)
        {
            return File.Exists(path) ? ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries) : null;
        }

        private static string ReadAllText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        // An unchanged file keeps its time, so the service doesn't start over when nothing changed.
        private static void WriteIfChanged(string path, IEnumerable<string> lines)
        {
            WriteIfChanged(path, string.Concat(lines.Select(l => l + "\n")));
        }

        private static void WriteIfChanged(string path, string text)
        {
            if (File.Exists(path) && ReadAllText(path) == text)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
