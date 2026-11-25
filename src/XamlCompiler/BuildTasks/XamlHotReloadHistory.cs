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
    // and what the service has done since then.
    //
    // The build writes:
    //   build.txt                      a new ID for every build: the app is restarted from a build, so the service's
    //                                  session starts again from the build's state when it changes
    //   <hash of the class name>.ids   each page's connection ID keys (see ConnectionIdKeys)
    //   typeinfo.txt                   the order of the types in XamlTypeInfo
    //   members.txt                    the order of the members in XamlTypeInfo (their accessors are numbered by it)
    //   code\<hash of the path>.cs     a copy of each generated C# file the build compiled into the app: the members the
    //                                  app has (see KeptMembers). The generated files themselves don't say that once
    //                                  another build has started: pass 1 empties <Page>.g.cs, and design-time builds
    //                                  (an editor's, into the same folder) rewrite them.
    //
    // The service writes session\ after every compile: the same files as it has changed them (IDs given to new elements,
    // the generated code the app now has, the type and member order of the XamlTypeInfo it returned), plus known.txt (what
    // that XamlTypeInfo contains) and pages.txt (pages it added). session\build.txt and the first line of each file name the
    // build the session continues; a session of another build is deleted. A service
    // started again on the same build (after a crash, or by another tool) carries on where the previous one stopped.
    //
    // A build without the property leaves XamlHotReload\ alone: an app started from an earlier build with the property may
    // still be running, and the service continues its session. It deletes XamlHotReloadCodegen.txt (next to the folder),
    // which says the generated code is hot reload codegen, so the build after it regenerates every page.
    //
    // A build only writes its files and never reads them or session\, so its output doesn't depend on an earlier build or
    // session. The service never writes the build's files. Every file is replaced in one step, and read with sharing that
    // allows that, so a build can run while the service is running.
    internal sealed class XamlHotReloadHistory
    {
        public const string FolderName = "XamlHotReload";

        private readonly string _buildOutputFolder;
        private readonly string _folder;
        private readonly string _sessionFolder;
        private string _baseline;
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<string>> _changed = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);

        public XamlHotReloadHistory(string buildOutputFolder)
        {
            _buildOutputFolder = buildOutputFolder;
            _folder = FolderOf(buildOutputFolder);
            _sessionFolder = Path.Combine(_folder, "session");
        }

        public static string FolderOf(string outputFolder) => Path.Combine(outputFolder, FolderName);

        public static string CodeFolderOf(string outputFolder) => Path.Combine(FolderOf(outputFolder), "code");

        // Before a compile: if a build has run since the last one, the session starts again from that build.
        public void Refresh()
        {
            string baseline = ReadIfExists(Path.Combine(_folder, BaselineName)) ?? string.Empty;
            if (baseline == _baseline)
            {
                return;
            }
            _baseline = baseline;
            _values.Clear();
            _changed.Clear();
            // A session of an earlier build (this service's or one before it) is over.
            if (Directory.Exists(_sessionFolder) && ReadIfExists(Path.Combine(_sessionFolder, BaselineName)) != baseline)
            {
                try
                {
                    Directory.Delete(_sessionFolder, recursive: true);
                }
                catch (IOException)
                {
                    // Files of an earlier build are ignored anyway.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // After a compile: what it changed. A value that is still the build's needs no session file.
        public void Save()
        {
            foreach (KeyValuePair<string, Func<string>> change in _changed)
            {
                string path = Path.Combine(_sessionFolder, change.Key);
                string text = change.Value();
                if (!File.Exists(path) && text == ReadIfExists(Path.Combine(_folder, change.Key)))
                {
                    continue;
                }
                WriteIfChanged(Path.Combine(_sessionFolder, BaselineName), _baseline);
                WriteIfChanged(path, _baseline + "\n" + text);
            }
            _changed.Clear();
        }

        // The connection IDs given out for a class so far. A compile adds its new elements to the same dictionary.
        public Dictionary<string, int> ConnectionIds(string className)
        {
            string name = Hash(className) + ".ids";
            Dictionary<string, int> ids = Get(name, ParseConnectionIds);
            _changed[name] = () => FormatConnectionIds(className, ids);
            return ids;
        }

        // The type order of the XamlTypeInfo the app has.
        public IList<string> TypeOrder
        {
            get { return Get(TypeOrderName, text => ParseLines(text).ToList()); }
            set { Set(TypeOrderName, value, () => FormatLines(value)); }
        }

        // The member order of the XamlTypeInfo the app has.
        public IList<string> MemberOrder
        {
            get { return Get(MemberOrderName, text => ParseLines(text).ToList()); }
            set { Set(MemberOrderName, value, () => FormatLines(value)); }
        }

        // The types and members the XamlTypeInfo the app has contains (see XamlCompilerService), or null if the service
        // hasn't generated it.
        public HashSet<string> KnownTypeInfo
        {
            get { return Get("known.txt", text => text == null ? null : new HashSet<string>(ParseLines(text), StringComparer.Ordinal)); }
            set { Set("known.txt", value, () => FormatLines(value.OrderBy(k => k, StringComparer.Ordinal))); }
        }

        // The pages the service added (paths relative to the project).
        public IList<string> AddedPages
        {
            get { return Get("pages.txt", text => ParseLines(text).ToList()); }
            set { Set("pages.txt", value, () => FormatLines(value)); }
        }

        // The text of a generated file as the app has it: the build's copy, then each compile's output.
        public string GeneratedText(string buildPath)
        {
            return Get(CodeName(_buildOutputFolder, buildPath), text => text);
        }

        public void SetGeneratedText(string buildPath, string text)
        {
            Set(CodeName(_buildOutputFolder, buildPath), text, () => text);
        }
        // Copies a generated file the build just wrote (or kept). Returns the copy's path.
        public static string WriteGeneratedText(string outputFolder, string buildPath)
        {
            string path = Path.Combine(FolderOf(outputFolder), CodeName(outputFolder, buildPath));
            WriteIfChanged(path, ReadAllText(buildPath));
            return path;
        }

        public static void WriteConnectionIds(string folder, string className, IDictionary<string, int> ids)
        {
            WriteIfChanged(Path.Combine(folder, Hash(className) + ".ids"), FormatConnectionIds(className, ids));
        }

        public static void WriteTypeOrder(string folder, IEnumerable<string> typeNames)
        {
            WriteIfChanged(Path.Combine(folder, TypeOrderName), FormatLines(typeNames));
        }

        public static void WriteMemberOrder(string folder, IEnumerable<string> memberKeys)
        {
            WriteIfChanged(Path.Combine(folder, MemberOrderName), FormatLines(memberKeys));
        }

        // Last, once the build's other files are written.
        public static void WriteBaseline(string folder)
        {
            WriteIfChanged(Path.Combine(folder, BaselineName), Guid.NewGuid().ToString("N"));
        }

        // Whether the generated code in outputFolder (from the last build) is of the other kind than this build makes, so
        // every page is generated again: hot reload codegen without the property, plain codegen with it, or state from a
        // compiler that recorded less.
        public static bool DoesCodegenDiffer(string outputFolder, bool hotReloadCodegen)
        {
            if (!hotReloadCodegen)
            {
                return File.Exists(CodegenMarkerOf(outputFolder));
            }
            string folder = FolderOf(outputFolder);
            return !File.Exists(CodegenMarkerOf(outputFolder)) || !Directory.Exists(CodeFolderOf(outputFolder)) ||
                (File.Exists(Path.Combine(folder, TypeOrderName)) && !File.Exists(Path.Combine(folder, MemberOrderName)));
        }

        // At the end of a build. The marker sits next to XamlHotReload\, not in it: a build without the property never touches
        // that folder, since the app that the service is updating may have been started from an earlier build with it.
        public static void SetCodegen(string outputFolder, bool hotReloadCodegen)
        {
            string marker = CodegenMarkerOf(outputFolder);
            if (hotReloadCodegen)
            {
                WriteIfChanged(marker, "This folder's generated XAML code is hot reload codegen (EnableXamlHotReloadCodegen).\n");
            }
            else if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }

        private static string CodegenMarkerOf(string outputFolder) => Path.Combine(outputFolder, "XamlHotReloadCodegen.txt");

        private const string BaselineName = "build.txt";
        private const string TypeOrderName = "typeinfo.txt";
        private const string MemberOrderName = "members.txt";

        private static string CodeName(string outputFolder, string buildPath)
        {
            string relative = buildPath.Substring(outputFolder.Length).TrimStart('\\');
            return Path.Combine("code", Hash(relative.ToUpperInvariant()) + ".cs");
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

        // The session's value: what the service wrote for this build, or else the build's file.
        private T Get<T>(string name, Func<string, T> parse)
        {
            if (_baseline == null)
            {
                Refresh();
            }
            if (!_values.TryGetValue(name, out object value))
            {
                string session = ReadIfExists(Path.Combine(_sessionFolder, name));
                string prefix = _baseline + "\n";
                value = parse(session != null && session.StartsWith(prefix, StringComparison.Ordinal)
                    ? session.Substring(prefix.Length)
                    : ReadIfExists(Path.Combine(_folder, name)));
                _values[name] = value;
            }
            return (T)value;
        }

        private void Set(string name, object value, Func<string> format)
        {
            if (_baseline == null)
            {
                Refresh();
            }
            _values[name] = value;
            _changed[name] = format;
        }

        private static Dictionary<string, int> ParseConnectionIds(string text)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in ParseLines(text).Skip(1))
            {
                int tab = line.IndexOf('\t');
                ids[line.Substring(tab + 1)] = int.Parse(line.Substring(0, tab), System.Globalization.CultureInfo.InvariantCulture);
            }
            return ids;
        }

        private static string FormatConnectionIds(string className, IDictionary<string, int> ids)
        {
            return FormatLines(new[] { className }.Concat(ids.OrderBy(p => p.Value).Select(p => p.Value + "\t" + p.Key)));
        }

        private static IEnumerable<string> ParseLines(string text)
        {
            return text?.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Enumerable.Empty<string>();
        }

        private static string FormatLines(IEnumerable<string> lines)
        {
            return string.Concat(lines.Select(l => l + "\n"));
        }

        private static string ReadIfExists(string path)
        {
            try
            {
                return File.Exists(path) ? ReadAllText(path) : null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
        private static string ReadAllText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
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
