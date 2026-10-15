// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Text;

    // Hot reload builds (EnableXamlHotReload, C#) only.
    //
    // .NET hot reload can't delete a field or a type (ENC0033). When a XAML edit removes an element, the generated code
    // loses that element's connection field, maybe a nested Bindings class, a function-binding delegate or the fields
    // that go with it. So the compiler service keeps every field and nested type that the generated code of a class has
    // declared since the last build: whatever a new compile no longer declares is put back as a tombstone. A field keeps
    // its declaration without its initializer; a type keeps its header, fields and nested types, and its method and
    // accessor bodies become inert: they do nothing and return default (ProcessBindings: -1, no more phases). Objects
    // created before the update still call them: a list recycles or reuses old containers through their template's
    // retired Bindings class (IDataTemplateComponent.Recycle/ProcessBindings), and data sources raise events into
    // handlers those objects subscribed.
    //
    // Methods are kept the same way (with an inert body) when no member of the same name is left. .NET hot reload can
    // delete a method, but Roslyn fails (NullReferenceException in DefinitionMap.GetPreviousMethodHandle) when the method
    // was added by an earlier update of the same session, for example an x:Load element's UpdateUnloadedElement.
    //
    // The inventory of declared members lives with the connection ID ledger (in the build's output folder), one file
    // per class and generated file. A build is a new baseline: it records what it generated and puts nothing back.
    // A live member with the same name always wins over a tombstone (a changed field type is a real rude edit).
    internal static class HotReloadRetiredMembers
    {
        internal static string Apply(string stateFolder, string className, string generatedFileExtension, string generatedCode, bool restoreRetiredMembers)
        {
            string inventoryPath = GetInventoryPath(stateFolder, className, generatedFileExtension);
            string result = generatedCode;
            if (restoreRetiredMembers && File.Exists(inventoryPath))
            {
                HotReloadMemberInventory previous = HotReloadJsonFile.Read<HotReloadMemberInventory>(inventoryPath);
                if (previous != null && previous.SchemaVersion == HotReloadMemberInventory.CurrentSchemaVersion && previous.Members != null)
                {
                    result = Restore(generatedCode, previous.Members);
                }
            }
            HotReloadJsonFile.Write(inventoryPath, new HotReloadMemberInventory
            {
                SchemaVersion = HotReloadMemberInventory.CurrentSchemaVersion,
                ClassName = className,
                Members = CreateInventory(result),
            });
            return result;
        }

        internal static string GetInventoryPath(string stateFolder, string className, string generatedFileExtension)
        {
            return Path.Combine(HotReloadStatePaths.GetFolder(stateFolder),
                HotReloadConnectionIdLedgerSession.CreateFileStem(className) + generatedFileExtension + HotReloadStatePaths.MembersExtension);
        }

        // For tests: the code without any of the members an inventory keeps (every field and nested type of every type),
        // so that restoring them from the inventory exercises every tombstone.
        internal static string RemoveKeptMembers(string code)
        {
            var ranges = new List<(int Start, int End)>();
            CollectKeptRanges(code, CSharpDeclarationScanner.Scan(code), ranges);
            var result = new StringBuilder(code.Length);
            int copied = 0;
            foreach ((int start, int end) in ranges.OrderBy(r => r.Start))
            {
                result.Append(code, copied, start - copied);
                copied = end;
            }
            result.Append(code, copied, code.Length - copied);
            return result.ToString();
        }

        private static void CollectKeptRanges(string code, CSharpDeclaration container, List<(int Start, int End)> ranges)
        {
            foreach (CSharpDeclaration member in container.Members)
            {
                if (container.Kind == CSharpDeclarationKind.Type && !container.HasConditionalDirectives &&
                    (member.Kind == CSharpDeclarationKind.Field || member.Kind == CSharpDeclarationKind.Type ||
                     (IsRetirableMethod(container, member) && CreateMethodTombstone(code, member, out _) != null)))
                {
                    ranges.Add((member.Start, member.End));
                }
                else if (member.Kind == CSharpDeclarationKind.Type || member.Kind == CSharpDeclarationKind.Namespace)
                {
                    CollectKeptRanges(code, member, ranges);
                }
            }
        }

        // An ordinary method with a body: what .NET hot reload would have to delete when the markup no longer generates it.
        // Roslyn can't delete a method that an earlier update of the same session added (NullReferenceException in
        // DefinitionMap.GetPreviousMethodHandle), so a removed method is kept with an inert body instead. Not kept:
        // constructors, operators, explicit interface implementations (the type may no longer implement the interface),
        // partial, extern and abstract methods, interface members and generic methods (Name is null for those).
        private static bool IsRetirableMethod(CSharpDeclaration container, CSharpDeclaration member)
        {
            if (member.Kind != CSharpDeclarationKind.Body || container.Kind != CSharpDeclarationKind.Type || container.IsInterface ||
                member.Name == null || !member.Name.EndsWith("()", StringComparison.Ordinal) ||
                (member.BodyOpen < 0 && member.ArrowStart < 0))
            {
                return false;
            }
            string name = MethodName(member);
            string typeName = container.Name;
            int arity = typeName.IndexOf('`');
            return name != (arity >= 0 ? typeName.Substring(0, arity) : typeName);
        }

        private static string MethodName(CSharpDeclaration member)
        {
            return member.Name.Substring(0, member.Name.Length - 2);
        }

        private static readonly HashSet<string> s_notRetirable = new HashSet<string>(StringComparer.Ordinal)
        {
            "partial", "extern", "abstract", "operator", "implicit", "explicit", "delegate",
        };

        // Partial methods whose defining declaration pass 1 emits for every page, so an implementation part (in the .g.cs)
        // can always be kept. An implementation that is dropped leaves the method unimplemented, and Roslyn then deletes it
        // from metadata: UnloadObject is implemented only while the page has x:Load elements.
        private static readonly HashSet<string> s_alwaysDeclaredPartialMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnloadObject",
        };

        private static string MethodHeader(string code, CSharpDeclaration member)
        {
            return code.Substring(member.Start, (member.BodyOpen >= 0 ? member.BodyOpen : member.ArrowStart) - member.Start);
        }

        // Null when the header isn't one a tombstone can keep (see IsRetirableMethod).
        private static string CreateMethodTombstone(string code, CSharpDeclaration member, out string parameters)
        {
            parameters = null;
            string header = MethodHeader(code, member);
            string core = StripAttributes(header);
            int open = core.IndexOf('(');
            if (open < 0)
            {
                return null;
            }
            string beforeParameters = core.Substring(0, open).TrimEnd();
            string[] words = beforeParameters.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool alwaysDeclaredPartial = words.Contains("partial") && s_alwaysDeclaredPartialMethods.Contains(MethodName(member));
            if (words.Any(w => s_notRetirable.Contains(w) && !(w == "partial" && alwaysDeclaredPartial)) ||
                beforeParameters.EndsWith("." + MethodName(member), StringComparison.Ordinal))
            {
                return null;
            }
            int close = MatchingParenthesis(core, open);
            parameters = System.Text.RegularExpressions.Regex.Replace(core.Substring(open + 1, Math.Max(0, close - open - 1)), @"\s+", " ").Trim();
            // An inert body has no await: drop 'async' (its 'return default;' would not compile in an async Task method).
            string kept = System.Text.RegularExpressions.Regex.Replace(header, @"\basync\s+", string.Empty);
            return kept.TrimEnd() + " " + InertBody(kept);
        }

        internal static List<HotReloadMemberEntry> CreateInventory(string code)
        {
            var entries = new Dictionary<string, HotReloadMemberEntry>(StringComparer.Ordinal);
            CSharpDeclaration root = CSharpDeclarationScanner.Scan(code);
            CollectEntries(code, root, entries);
            return entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        }

        internal static string Restore(string code, IEnumerable<HotReloadMemberEntry> previousMembers)
        {
            CSharpDeclaration root = CSharpDeclarationScanner.Scan(code);
            var containers = new Dictionary<string, CSharpDeclaration>(StringComparer.Ordinal);
            var memberKeys = new HashSet<string>(StringComparer.Ordinal);
            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            IndexDeclarations(root, containers, memberKeys, memberNames);

            // Insert before the closing brace of the container's last declaration, in key order.
            var insertions = new SortedDictionary<int, List<HotReloadMemberEntry>>();
            foreach (HotReloadMemberEntry entry in previousMembers.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (memberKeys.Contains(entry.Key) ||
                    !containers.TryGetValue(entry.Container, out CSharpDeclaration container) ||
                    memberNames.Contains(entry.Container + "/" + entry.Name))
                {
                    continue;
                }
                if (!insertions.TryGetValue(container.BodyClose, out List<HotReloadMemberEntry> list))
                {
                    list = new List<HotReloadMemberEntry>();
                    insertions.Add(container.BodyClose, list);
                }
                list.Add(entry);
            }
            if (insertions.Count == 0)
            {
                return code;
            }

            var result = new StringBuilder(code.Length + 1024);
            int copied = 0;
            foreach (KeyValuePair<int, List<HotReloadMemberEntry>> insertion in insertions)
            {
                int lineStart = code.LastIndexOf('\n', insertion.Key) + 1;
                string closingIndent = code.Substring(lineStart, insertion.Key - lineStart);
                if (closingIndent.Trim().Length != 0)
                {
                    // The closing brace isn't alone on its line; insert right before it.
                    lineStart = insertion.Key;
                    closingIndent = string.Empty;
                }
                string indent = closingIndent + "    ";
                result.Append(code, copied, lineStart - copied);
                result.Append("#pragma warning disable\r\n");
                result.Append(indent).Append("// Hot reload: removed from the markup since the last build. Kept so that applying the change doesn't delete it.\r\n");
                foreach (HotReloadMemberEntry entry in insertion.Value)
                {
                    result.Append(indent).Append(entry.Text).Append("\r\n");
                }
                result.Append("#pragma warning restore\r\n");
                copied = lineStart;
            }
            result.Append(code, copied, code.Length - copied);
            return result.ToString();
        }

        private static void IndexDeclarations(CSharpDeclaration container, Dictionary<string, CSharpDeclaration> containers, HashSet<string> memberKeys, HashSet<string> memberNames)
        {
            if (container.Path != null)
            {
                // A partial type can be declared more than once in a file; the last declaration takes the tombstones.
                containers[container.Path] = container;
            }
            foreach (CSharpDeclaration member in container.Members)
            {
                if (container.Kind == CSharpDeclarationKind.Type && member.Name != null)
                {
                    memberNames.Add(container.Path + "/" + member.Name.TrimEnd('(', ')'));
                    if (member.Kind == CSharpDeclarationKind.Type || member.Kind == CSharpDeclarationKind.Field)
                    {
                        memberKeys.Add(GetKey(container, member));
                    }
                }
                if (member.Kind == CSharpDeclarationKind.Type || member.Kind == CSharpDeclarationKind.Namespace)
                {
                    IndexDeclarations(member, containers, memberKeys, memberNames);
                }
            }
        }

        private static void CollectEntries(string code, CSharpDeclaration container, Dictionary<string, HotReloadMemberEntry> entries)
        {
            foreach (CSharpDeclaration member in container.Members)
            {
                // Only members of types are kept: a page's top-level class doesn't go away without its markup.
                if (container.Kind == CSharpDeclarationKind.Type &&
                    (member.Kind == CSharpDeclarationKind.Field || member.Kind == CSharpDeclarationKind.Type) &&
                    !container.HasConditionalDirectives)
                {
                    string key = GetKey(container, member);
                    if (!entries.ContainsKey(key))
                    {
                        entries.Add(key, new HotReloadMemberEntry
                        {
                            Key = key,
                            Container = container.Path,
                            Name = member.Name,
                            Kind = member.Kind == CSharpDeclarationKind.Type ? "type" : "field",
                            Text = member.Kind == CSharpDeclarationKind.Type
                                ? CreateTypeShell(code, member)
                                : CreateFieldTombstone(code, member),
                        });
                    }
                }
                else if (container.Kind == CSharpDeclarationKind.Type && !container.HasConditionalDirectives && IsRetirableMethod(container, member))
                {
                    string text = CreateMethodTombstone(code, member, out string parameters);
                    string key = container.Path + "/M:" + MethodName(member) + "(" + parameters + ")";
                    if (text != null && !entries.ContainsKey(key))
                    {
                        entries.Add(key, new HotReloadMemberEntry
                        {
                            Key = key,
                            Container = container.Path,
                            Name = MethodName(member),
                            Kind = "method",
                            Text = text,
                        });
                    }
                }
                if (member.Kind == CSharpDeclarationKind.Type || member.Kind == CSharpDeclarationKind.Namespace)
                {
                    CollectEntries(code, member, entries);
                }
            }
        }

        private static string GetKey(CSharpDeclaration container, CSharpDeclaration member)
        {
            return container.Path + "/" + (member.Kind == CSharpDeclarationKind.Type ? "T:" : "F:") + member.Name;
        }

        private static string CreateFieldTombstone(string code, CSharpDeclaration field)
        {
            if (field.InitializerStart < 0 || field.IsConst)
            {
                return code.Substring(field.Start, field.End - field.Start);
            }
            return code.Substring(field.Start, field.InitializerStart - field.Start).TrimEnd() + ";";
        }

        // The type as it was declared, with its method and accessor bodies replaced and its field initializers dropped.
        private static string CreateTypeShell(string code, CSharpDeclaration type)
        {
            if (type.BodyOpen < 0 || type.HasConditionalDirectives || type.IsEnum)
            {
                return code.Substring(type.Start, type.End - type.Start);
            }
            var shell = new StringBuilder();
            int copied = type.Start;
            foreach (CSharpDeclaration member in type.Members)
            {
                AppendMemberShell(code, member, shell, ref copied);
            }
            shell.Append(code, copied, type.End - copied);
            return shell.ToString();
        }

        // The body of a retired member: it does nothing, assigns its out parameters and returns default.
        // ProcessBindings (IDataTemplateComponent / IDataTemplateExtension) reports no more phases.
        internal static string InertBody(string header)
        {
            string core = StripAttributes(header).Trim();
            string accessor = AccessorKeyword(core);
            if (accessor != null)
            {
                return accessor == "get" ? "{ return default; }" : "{ }";
            }
            int open = core.IndexOf('(');
            if (open < 0)
            {
                return "{ return default; }";
            }
            string beforeParameters = core.Substring(0, open).TrimEnd();
            string name = LastIdentifier(StripGenericArguments(beforeParameters));
            string beforeName = beforeParameters.Substring(0, beforeParameters.LastIndexOf(name, StringComparison.Ordinal)).Trim();
            string lastToken = beforeName.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            bool isVoid = lastToken == null || lastToken == "void" || s_modifiers.Contains(lastToken);

            var body = new StringBuilder("{ ");
            int close = MatchingParenthesis(core, open);
            foreach (string parameter in SplitTopLevel(core.Substring(open + 1, Math.Max(0, close - open - 1))))
            {
                string[] tokens = parameter.Split(new[] { '=' }, 2)[0].Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && tokens.Contains("out"))
                {
                    string parameterName = tokens[tokens.Length - 1].TrimStart('@');
                    body.Append(parameterName).Append(parameterName == "nextPhase" ? " = -1; " : " = default; ");
                }
            }
            if (!isVoid)
            {
                body.Append(name == "ProcessBindings" && lastToken == "int" ? "return -1; " : "return default; ");
            }
            return body.Append('}').ToString();
        }

        private static readonly HashSet<string> s_modifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "static", "override", "virtual", "sealed", "new", "async",
            "extern", "unsafe", "partial", "readonly", "abstract",
        };

        private static bool IsMethodLike(string header)
        {
            return StripAttributes(header).IndexOf('(') >= 0;
        }

        private static bool IsAccessor(string header)
        {
            return AccessorKeyword(StripAttributes(header).Trim()) != null;
        }

        private static string AccessorKeyword(string core)
        {
            string last = core.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return last == "get" || last == "set" || last == "init" || last == "add" || last == "remove" ? last : null;
        }

        private static string StripAttributes(string header)
        {
            var result = new StringBuilder();
            int depth = 0;
            foreach (char c in header)
            {
                if (c == '[' && depth == 0 && result.ToString().Trim().Length == 0)
                {
                    depth = 1;
                    continue;
                }
                if (depth > 0)
                {
                    depth += c == '[' ? 1 : c == ']' ? -1 : 0;
                    continue;
                }
                result.Append(c);
            }
            return result.ToString();
        }

        private static string StripGenericArguments(string text)
        {
            text = text.TrimEnd();
            if (!text.EndsWith(">", StringComparison.Ordinal))
            {
                return text;
            }
            int depth = 0;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                depth += text[i] == '>' ? 1 : text[i] == '<' ? -1 : 0;
                if (depth == 0)
                {
                    return text.Substring(0, i).TrimEnd();
                }
            }
            return text;
        }

        private static string LastIdentifier(string text)
        {
            int end = text.Length;
            int start = end;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '@'))
            {
                start--;
            }
            return text.Substring(start, end - start);
        }

        private static int MatchingParenthesis(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
                if (depth == 0)
                {
                    return i;
                }
            }
            return text.Length;
        }

        private static IEnumerable<string> SplitTopLevel(string parameters)
        {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                char c = parameters[i];
                depth += c == '<' || c == '(' || c == '[' ? 1 : c == '>' || c == ')' || c == ']' ? -1 : 0;
                if (c == ',' && depth == 0)
                {
                    yield return parameters.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (parameters.Trim().Length > 0)
            {
                yield return parameters.Substring(start);
            }
        }

        private static void AppendMemberShell(string code, CSharpDeclaration member, StringBuilder shell, ref int copied)
        {
            switch (member.Kind)
            {
                case CSharpDeclarationKind.Type:
                    shell.Append(code, copied, member.Start - copied);
                    shell.Append(CreateTypeShell(code, member));
                    copied = member.End;
                    break;
                case CSharpDeclarationKind.Field:
                    shell.Append(code, copied, member.Start - copied);
                    shell.Append(CreateFieldTombstone(code, member));
                    copied = member.End;
                    break;
                case CSharpDeclarationKind.Accessors:
                    foreach (CSharpDeclaration accessor in member.Members)
                    {
                        AppendMemberShell(code, accessor, shell, ref copied);
                    }
                    if (member.InitializerStart >= 0)
                    {
                        // '{ get; set; } = value;' -> '{ get; set; }'
                        shell.Append(code, copied, member.BodyClose + 1 - copied);
                        copied = member.End;
                    }
                    break;
                case CSharpDeclarationKind.Body:
                    if (member.BodyOpen >= 0)
                    {
                        string header = code.Substring(member.Start, member.BodyOpen - member.Start);
                        shell.Append(code, copied, member.BodyOpen - copied);
                        shell.Append(InertBody(header));
                        copied = member.BodyClose + 1;
                    }
                    else if (member.ArrowStart >= 0)
                    {
                        string header = code.Substring(member.Start, member.ArrowStart - member.Start);
                        shell.Append(code, copied, member.ArrowStart - copied);
                        // 'int P => x;' stays an expression body; a method's becomes a block.
                        shell.Append(IsMethodLike(header) || IsAccessor(header) ? InertBody(header) : "=> default;");
                        copied = member.End;
                    }
                    break;
            }
        }
    }

    internal enum CSharpDeclarationKind
    {
        Root,
        Namespace,
        Type,
        Field,
        // A property, indexer or event with an accessor list (Members are the accessors).
        Accessors,
        // A method, constructor, operator, accessor or expression-bodied member; or anything else that ends with ';'.
        Body,
        Other,
    }

    internal sealed class CSharpDeclaration
    {
        internal CSharpDeclarationKind Kind;
        internal string Name;
        internal string Path;
        internal int Start;
        internal int End;
        internal int BodyOpen = -1;
        internal int BodyClose = -1;
        internal int InitializerStart = -1;
        internal int ArrowStart = -1;
        internal bool IsConst;
        internal bool IsEnum;
        internal bool IsInterface;
        internal bool HasConditionalDirectives;
        internal List<CSharpDeclaration> Members = new List<CSharpDeclaration>();
    }

    // A declaration-level scanner for the C# the XAML compiler generates: it finds namespaces, types, fields and
    // member bodies, and never looks inside a body. It skips comments, strings (regular, verbatim, interpolated),
    // character literals and preprocessor lines.
    internal static class CSharpDeclarationScanner
    {
        private static readonly HashSet<string> s_typeKeywords = new HashSet<string>(StringComparer.Ordinal) { "class", "struct", "interface", "enum", "record" };

        internal static CSharpDeclaration Scan(string code)
        {
            var root = new CSharpDeclaration { Kind = CSharpDeclarationKind.Root, Path = null, Start = 0, End = code.Length };
            ScanMembers(code, 0, code.Length, root, "", accessorList: false);
            return root;
        }

        private static void ScanMembers(string code, int from, int to, CSharpDeclaration container, string path, bool accessorList)
        {
            int i = from;
            while (true)
            {
                i = SkipTrivia(code, i, to, container);
                if (i >= to)
                {
                    return;
                }
                CSharpDeclaration member = ReadMember(code, i, to, container);
                i = member.End;
                Classify(code, member, path, accessorList);
                container.Members.Add(member);
                if (member.Kind == CSharpDeclarationKind.Namespace || member.Kind == CSharpDeclarationKind.Type)
                {
                    if (member.BodyOpen >= 0 && !member.IsEnum)
                    {
                        ScanMembers(code, member.BodyOpen + 1, member.BodyClose, member, member.Path, accessorList: false);
                    }
                }
                else if (member.Kind == CSharpDeclarationKind.Accessors)
                {
                    ScanMembers(code, member.BodyOpen + 1, member.BodyClose, member, path, accessorList: true);
                }
            }
        }

        // One member: up to a top-level ';', or a top-level block (plus a property initializer after it).
        private static CSharpDeclaration ReadMember(string code, int start, int to, CSharpDeclaration container)
        {
            var member = new CSharpDeclaration { Start = start, End = to };
            int depth = 0;
            int i = start;
            while (i < to)
            {
                int skipped = SkipLiteralOrComment(code, i, to);
                if (skipped > i)
                {
                    i = skipped;
                    continue;
                }
                if (IsDirectiveStart(code, i))
                {
                    if (IsConditionalDirective(code, i))
                    {
                        container.HasConditionalDirectives = true;
                    }
                    i = LineEnd(code, i, to);
                    continue;
                }
                char c = code[i];
                if (c == '(' || c == '[')
                {
                    depth++;
                }
                else if (c == ')' || c == ']')
                {
                    depth--;
                }
                else if (depth == 0)
                {
                    if (c == '=' && member.InitializerStart < 0 && member.ArrowStart < 0 && !IsComparison(code, i))
                    {
                        if (i + 1 < to && code[i + 1] == '>')
                        {
                            member.ArrowStart = i;
                        }
                        else
                        {
                            member.InitializerStart = i;
                        }
                    }
                    else if (c == ';')
                    {
                        member.End = i + 1;
                        return member;
                    }
                    else if (c == '{')
                    {
                        int close = MatchBrace(code, i, to);
                        if (member.InitializerStart >= 0 || member.ArrowStart >= 0)
                        {
                            // A brace inside an initializer or expression body (a lambda, an object initializer).
                            i = close + 1;
                            continue;
                        }
                        member.BodyOpen = i;
                        member.BodyClose = close;
                        member.End = close + 1;
                        int next = SkipTrivia(code, close + 1, to, container);
                        if (next < to && code[next] == '=' && !IsComparison(code, next))
                        {
                            // '{ get; set; } = value;'
                            member.InitializerStart = next;
                            int end = next;
                            while (end < to && code[end] != ';')
                            {
                                int s = SkipLiteralOrComment(code, end, to);
                                end = s > end ? s : (code[end] == '{' ? MatchBrace(code, end, to) + 1 : end + 1);
                            }
                            member.End = Math.Min(to, end + 1);
                        }
                        else if (next < to && code[next] == ';')
                        {
                            member.End = next + 1;
                        }
                        return member;
                    }
                }
                i++;
            }
            return member;
        }

        private static void Classify(string code, CSharpDeclaration member, string path, bool accessorList)
        {
            int headerEnd = member.BodyOpen >= 0 ? member.BodyOpen
                : member.InitializerStart >= 0 ? member.InitializerStart
                : member.ArrowStart >= 0 ? member.ArrowStart
                : member.End;
            List<Token> tokens = Tokenize(code, member.Start, headerEnd);
            int firstParen = tokens.FindIndex(t => t.Text == "(");

            if (accessorList)
            {
                member.Kind = CSharpDeclarationKind.Body;
                return;
            }

            int keyword = tokens.FindIndex(t => t.Text == "namespace");
            if (keyword >= 0 && keyword + 1 < tokens.Count)
            {
                member.Kind = CSharpDeclarationKind.Namespace;
                var name = new StringBuilder();
                for (int t = keyword + 1; t < tokens.Count && (tokens[t].IsWord || tokens[t].Text == "."); t++)
                {
                    name.Append(tokens[t].Text);
                }
                member.Name = name.ToString();
                member.Path = (path.Length == 0 ? "" : path + "/") + "N:" + member.Name;
                return;
            }

            keyword = tokens.FindIndex(t => t.IsWord && s_typeKeywords.Contains(t.Text));
            if (keyword >= 0 && (firstParen < 0 || keyword < firstParen) && keyword + 1 < tokens.Count && tokens[keyword + 1].IsWord)
            {
                member.Kind = CSharpDeclarationKind.Type;
                member.IsEnum = tokens[keyword].Text == "enum";
                member.IsInterface = tokens[keyword].Text == "interface";
                member.Name = tokens[keyword + 1].Text + Arity(tokens, keyword + 2);
                member.Path = path + "/" + member.Name;
                return;
            }

            int delegateKeyword = tokens.FindIndex(t => t.Text == "delegate");
            if (delegateKeyword >= 0 && firstParen > delegateKeyword && member.BodyOpen < 0)
            {
                int nameIndex = firstParen - 1;
                if (tokens[nameIndex].Text == ">")
                {
                    int angle = 1;
                    while (nameIndex > 0 && angle > 0)
                    {
                        nameIndex--;
                        angle += tokens[nameIndex].Text == ">" ? 1 : tokens[nameIndex].Text == "<" ? -1 : 0;
                    }
                    nameIndex--;
                }
                if (nameIndex > delegateKeyword && tokens[nameIndex].IsWord)
                {
                    member.Kind = CSharpDeclarationKind.Type;
                    member.Name = tokens[nameIndex].Text + Arity(tokens, nameIndex + 1);
                    member.Path = path + "/" + member.Name;
                    return;
                }
            }

            if (member.BodyOpen < 0 && member.ArrowStart < 0 && firstParen < 0 && code[member.End - 1] == ';' &&
                !tokens.Any(t => t.Text == "event" || t.Text == "operator" || t.Text == "using" || t.Text == "delegate" || t.Text == "this"))
            {
                // A field: '[attributes] modifiers Type name [= value];' with a single declarator.
                int angle = 0;
                bool singleDeclarator = true;
                foreach (Token t in tokens)
                {
                    angle += t.Text == "<" ? 1 : t.Text == ">" ? -1 : 0;
                    singleDeclarator &= !(t.Text == "," && angle == 0);
                }
                Token last = tokens.LastOrDefault(t => t.Text != ";");
                if (singleDeclarator && last.IsWord && tokens.Count(t => t.IsWord) >= 2)
                {
                    member.Kind = CSharpDeclarationKind.Field;
                    member.Name = last.Text;
                    member.IsConst = tokens.Any(t => t.Text == "const");
                    return;
                }
            }

            if (member.BodyOpen >= 0 && firstParen < 0)
            {
                // A property, indexer or event with an accessor list.
                member.Kind = CSharpDeclarationKind.Accessors;
                Token name = tokens.LastOrDefault(t => t.IsWord);
                member.Name = tokens.Any(t => t.Text == "this") ? "this[]" : name.Text;
                return;
            }

            member.Kind = CSharpDeclarationKind.Body;
            if (firstParen > 0 && tokens[firstParen - 1].IsWord)
            {
                member.Name = tokens[firstParen - 1].Text + "()";
            }
            else if (member.ArrowStart >= 0)
            {
                Token name = tokens.LastOrDefault(t => t.IsWord);
                member.Name = name.Text;
            }
        }

        private static string Arity(List<Token> tokens, int index)
        {
            if (index >= tokens.Count || tokens[index].Text != "<")
            {
                return string.Empty;
            }
            int depth = 0;
            int arity = 1;
            for (int t = index; t < tokens.Count; t++)
            {
                depth += tokens[t].Text == "<" ? 1 : tokens[t].Text == ">" ? -1 : 0;
                if (depth == 1 && tokens[t].Text == ",")
                {
                    arity++;
                }
                if (depth == 0)
                {
                    break;
                }
            }
            return "`" + arity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private struct Token
        {
            internal string Text;
            internal bool IsWord;
        }

        // Words and punctuation of a declaration header, without attributes, comments and literals.
        private static List<Token> Tokenize(string code, int start, int end)
        {
            var tokens = new List<Token>();
            int i = start;
            int attributeDepth = 0;
            bool atHead = true;
            while (i < end)
            {
                int skipped = SkipLiteralOrComment(code, i, end);
                if (skipped > i)
                {
                    i = skipped;
                    continue;
                }
                if (IsDirectiveStart(code, i))
                {
                    i = LineEnd(code, i, end);
                    continue;
                }
                char c = code[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (atHead && c == '[')
                {
                    attributeDepth++;
                    i++;
                    continue;
                }
                if (attributeDepth > 0)
                {
                    attributeDepth += c == '[' ? 1 : c == ']' ? -1 : 0;
                    i++;
                    continue;
                }
                atHead = false;
                if (char.IsLetterOrDigit(c) || c == '_' || c == '@')
                {
                    int s = i;
                    while (i < end && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '@'))
                    {
                        i++;
                    }
                    tokens.Add(new Token { Text = code.Substring(s, i - s).TrimStart('@'), IsWord = true });
                    continue;
                }
                tokens.Add(new Token { Text = c.ToString() });
                i++;
            }
            return tokens;
        }

        private static bool IsComparison(string code, int i)
        {
            char previous = i > 0 ? code[i - 1] : ' ';
            char next = i + 1 < code.Length ? code[i + 1] : ' ';
            return next == '=' || previous == '=' || previous == '!' || previous == '<' || previous == '>';
        }

        private static int SkipTrivia(string code, int i, int to, CSharpDeclaration container)
        {
            while (i < to)
            {
                if (char.IsWhiteSpace(code[i]))
                {
                    i++;
                }
                else if (IsDirectiveStart(code, i))
                {
                    if (IsConditionalDirective(code, i))
                    {
                        container.HasConditionalDirectives = true;
                    }
                    i = LineEnd(code, i, to);
                }
                else if (code[i] == '/' && i + 1 < to && (code[i + 1] == '/' || code[i + 1] == '*'))
                {
                    i = SkipLiteralOrComment(code, i, to);
                }
                else
                {
                    break;
                }
            }
            return i;
        }

        private static bool IsDirectiveStart(string code, int i)
        {
            if (code[i] != '#')
            {
                return false;
            }
            for (int p = i - 1; p >= 0 && code[p] != '\n'; p--)
            {
                if (!char.IsWhiteSpace(code[p]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsConditionalDirective(string code, int i)
        {
            int s = i + 1;
            while (s < code.Length && (code[s] == ' ' || code[s] == '\t'))
            {
                s++;
            }
            return string.CompareOrdinal(code, s, "if", 0, 2) == 0 || string.CompareOrdinal(code, s, "el", 0, 2) == 0 ||
                string.CompareOrdinal(code, s, "endif", 0, 5) == 0;
        }

        private static int LineEnd(string code, int i, int to)
        {
            int n = code.IndexOf('\n', i, to - i);
            return n < 0 ? to : n + 1;
        }

        private static int MatchBrace(string code, int open, int to)
        {
            int depth = 0;
            int i = open;
            while (i < to)
            {
                int skipped = SkipLiteralOrComment(code, i, to);
                if (skipped > i)
                {
                    i = skipped;
                    continue;
                }
                if (IsDirectiveStart(code, i))
                {
                    i = LineEnd(code, i, to);
                    continue;
                }
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    return i;
                }
                i++;
            }
            throw new InvalidDataException("Unbalanced braces in generated code.");
        }

        // The index after a comment or literal that starts at i, or i if there isn't one.
        private static int SkipLiteralOrComment(string code, int i, int to)
        {
            char c = code[i];
            char next = i + 1 < to ? code[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                int n = code.IndexOf('\n', i, to - i);
                return n < 0 ? to : n;
            }
            if (c == '/' && next == '*')
            {
                int n = code.IndexOf("*/", i + 2, to - i - 2, StringComparison.Ordinal);
                return n < 0 ? to : n + 2;
            }
            if (c == '\'')
            {
                int p = i + 1;
                while (p < to && code[p] != '\'')
                {
                    p += code[p] == '\\' ? 2 : 1;
                }
                return Math.Min(to, p + 1);
            }
            bool verbatim = false;
            bool interpolated = false;
            int q = i;
            while (q < to && (code[q] == '@' || code[q] == '$') && q - i < 2)
            {
                verbatim |= code[q] == '@';
                interpolated |= code[q] == '$';
                q++;
            }
            if (q >= to || code[q] != '"' || (q > i && !verbatim && !interpolated))
            {
                return i;
            }
            int p2 = q + 1;
            while (p2 < to)
            {
                char ch = code[p2];
                if (ch == '\\' && !verbatim)
                {
                    p2 += 2;
                    continue;
                }
                if (ch == '"')
                {
                    if (verbatim && p2 + 1 < to && code[p2 + 1] == '"')
                    {
                        p2 += 2;
                        continue;
                    }
                    return p2 + 1;
                }
                if (interpolated && ch == '{')
                {
                    if (p2 + 1 < to && code[p2 + 1] == '{')
                    {
                        p2 += 2;
                        continue;
                    }
                    int depth = 1;
                    p2++;
                    while (p2 < to && depth > 0)
                    {
                        int s = SkipLiteralOrComment(code, p2, to);
                        if (s > p2)
                        {
                            p2 = s;
                            continue;
                        }
                        depth += code[p2] == '{' ? 1 : code[p2] == '}' ? -1 : 0;
                        p2++;
                    }
                    continue;
                }
                p2++;
            }
            return to;
        }
    }

    [DataContract]
    internal sealed class HotReloadMemberInventory
    {
        internal const int CurrentSchemaVersion = 1;

        [DataMember(Name = "schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "class", Order = 1)]
        public string ClassName { get; set; }

        [DataMember(Name = "members", Order = 2)]
        public List<HotReloadMemberEntry> Members { get; set; }
    }

    [DataContract]
    internal sealed class HotReloadMemberEntry
    {
        [DataMember(Name = "key", Order = 0)]
        public string Key { get; set; }

        [DataMember(Name = "container", Order = 1)]
        public string Container { get; set; }

        [DataMember(Name = "name", Order = 2)]
        public string Name { get; set; }

        [DataMember(Name = "kind", Order = 3)]
        public string Kind { get; set; }

        [DataMember(Name = "text", Order = 4)]
        public string Text { get; set; }
    }
}
