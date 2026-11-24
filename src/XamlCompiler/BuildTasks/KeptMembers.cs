// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;

    // EnableXamlHotReloadCodegen, compiler service: members the running app has, but the new generated code doesn't.
    //
    // .NET hot reload can't remove a field or a type from a running app, and Roslyn fails when an update removes a method
    // that an earlier update added. So when a compile stops generating a member, the member is kept, and does nothing:
    //
    //   field   its declaration without an initializer
    //   method  its declaration with an empty body, or "return default;"; out parameters are set
    //   type    its declaration, with all of its members kept that way
    //
    // The new code wins: a member it declares (by name) isn't kept. Kept members come from the file's previous text, which
    // holds the members kept before, so a member stays kept until the app restarts from a build.
    internal static class KeptMembers
    {
        public static string AddRemovedMembers(string previous, string current)
        {
            if (string.IsNullOrWhiteSpace(previous))
            {
                return current;
            }
            Declaration before = Declaration.Parse(previous);
            Declaration after = Declaration.Parse(current);

            var insertions = new List<(int At, string Text)>();
            AddRemoved(before, after, insertions);
            var removedNamespaces = before.Members.Where(m => m.Kind == Kind.Namespace && after.Find(m) == null).ToList();
            if (removedNamespaces.Count > 0)
            {
                insertions.Add((current.Length, string.Concat(removedNamespaces.Select(n => Kept(n, string.Empty)))));
            }

            var text = new StringBuilder(current);
            foreach (var insertion in insertions.OrderByDescending(i => i.At))
            {
                text.Insert(insertion.At, insertion.Text);
            }
            return text.ToString();
        }

        private static void AddRemoved(Declaration before, Declaration after, List<(int, string)> insertions)
        {
            var kept = new StringBuilder();
            foreach (Declaration member in before.Members)
            {
                Declaration now = after.Find(member);
                if (now != null)
                {
                    if (member.Kind == Kind.Namespace || member.Kind == Kind.Type)
                    {
                        AddRemoved(member, now, insertions);
                    }
                }
                else if (before.Kind != Kind.File && !member.Name.Contains('.'))
                {
                    // Explicit interface implementations aren't kept: the type may no longer implement the interface.
                    kept.Append(Kept(member, after.Indent + "    "));
                }
            }
            if (kept.Length > 0)
            {
                insertions.Add((after.LineStartOfClose,
                    "#pragma warning disable\r\n" +
                    after.Indent + "    // Kept by XAML hot reload: the running app has these members, and the markup no longer generates them.\r\n" +
                    kept +
                    "#pragma warning restore\r\n"));
            }
        }

        private static string Kept(Declaration d, string indent)
        {
            var text = new StringBuilder();
            switch (d.Kind)
            {
                case Kind.Namespace:
                case Kind.Type:
                    text.Append(Lines(d.Header, indent)).Append(indent).Append("{\r\n");
                    foreach (Declaration member in d.Members)
                    {
                        text.Append(Kept(member, indent + "    "));
                    }
                    return text.Append(indent).Append("}\r\n").ToString();

                case Kind.Field:
                    // Without its initializer: a static one would add a static constructor (Roslyn can't update one of a type
                    // an earlier update added).
                    return Lines(d.Header, indent).TrimEnd() + ";\r\n";

                case Kind.Method:
                    if (d.Body == null)
                    {
                        return Lines(d.Header, indent).TrimEnd() + ";\r\n";
                    }
                    text.Append(Lines(d.Header, indent)).Append(indent).Append("{\r\n");
                    foreach (string parameter in d.OutParameters)
                    {
                        // IDataTemplateComponent.ProcessBindings: -1 means there are no more phases.
                        text.Append(indent).Append("    ").Append(parameter).Append(parameter == "nextPhase" ? " = -1;\r\n" : " = default;\r\n");
                    }
                    if (!d.ReturnsVoid)
                    {
                        text.Append(indent).Append("    return default;\r\n");
                    }
                    return text.Append(indent).Append("}\r\n").ToString();

                case Kind.Property:
                    text.Append(Lines(d.Header, indent)).Append(indent).Append("{ ");
                    foreach (string accessor in d.Accessors)
                    {
                        text.Append(accessor).Append(accessor == "get" ? " { return default; } " : " { } ");
                    }
                    return text.Append("}\r\n").ToString();

                default:
                    return Lines(d.Text, indent);
            }
        }

        // The declaration's lines, indented for where it goes.
        private static string Lines(string text, string indent)
        {
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            return string.Concat(lines.Select(l => indent + l + "\r\n"));
        }

        private enum Kind { File, Namespace, Type, Field, Method, Property, Other }

        // A namespace, type or member of generated C#, found by a scanner that knows only what declarations look like.
        private sealed class Declaration
        {
            private static readonly Regex NamespaceKeyword = new Regex(@"\bnamespace\s+([\w.]+)");
            private static readonly Regex TypeKeyword = new Regex(@"\b(class|struct|interface|enum|record)\s+(\w+)");
            private static readonly Regex MethodName = new Regex(@"([\w.]+)\s*(<[^()]*>)?\s*$");
            private static readonly Regex LastName = new Regex(@"(\w+)\s*(\[[^\]]*\])?\s*$");

            public Kind Kind;
            public string Name = string.Empty;
            public string Header = string.Empty;
            public string Text = string.Empty;
            public string Body;
            public bool ReturnsVoid;
            public List<string> OutParameters = new List<string>();
            public List<string> Accessors = new List<string>();
            public List<Declaration> Members = new List<Declaration>();
            public int LineStartOfClose;
            public string Indent = string.Empty;

            public Declaration Find(Declaration other) =>
                Members.FirstOrDefault(m => m.Name == other.Name && (m.Kind == Kind.Namespace) == (other.Kind == Kind.Namespace));

            public static Declaration Parse(string text)
            {
                var file = new Declaration { Kind = Kind.File };
                ParseMembers(new Scanner(text), file);
                return file;
            }

            private static void ParseMembers(Scanner s, Declaration container)
            {
                while (true)
                {
                    s.SkipTrivia();
                    if (s.AtEnd || s.Current == '}')
                    {
                        container.LineStartOfClose = s.LineStart();
                        container.Indent = s.Text.Substring(container.LineStartOfClose, s.Position - container.LineStartOfClose);
                        return;
                    }
                    Declaration member = ParseMember(s, container.Name);
                    if (member != null)
                    {
                        container.Members.Add(member);
                    }
                }
            }

            private static Declaration ParseMember(Scanner s, string containerName)
            {
                int start = s.Position;
                char end = s.ScanToDeclarationEnd();
                var d = new Declaration { Header = s.Text.Substring(start, s.Position - start) };
                string signature = StripAttributes(d.Header);
                int paren = signature.IndexOf('(');
                string beforeParen = paren < 0 ? signature : signature.Substring(0, paren);
                Match match;

                if (end == '}' || end == '\0')
                {
                    return null;
                }
                if (end == '{' && (match = NamespaceKeyword.Match(beforeParen)).Success)
                {
                    d.Kind = Kind.Namespace;
                    d.Name = match.Groups[1].Value;
                    d.Header = "namespace " + d.Name;
                    s.Advance();
                    ParseMembers(s, d);
                    s.Advance();
                }
                else if (end == '{' && (match = TypeKeyword.Match(beforeParen)).Success)
                {
                    d.Name = match.Groups[2].Value;
                    // An enum or interface has no code to make inert, so it is kept as it is.
                    d.Kind = match.Groups[1].Value == "enum" || match.Groups[1].Value == "interface" ? Kind.Other : Kind.Type;
                    if (d.Kind == Kind.Type)
                    {
                        s.Advance();
                        ParseMembers(s, d);
                        s.Advance();
                    }
                    else
                    {
                        s.SkipBalanced();
                    }
                }
                else if (paren >= 0 && end != '=' && (match = MethodName.Match(beforeParen)).Success)
                {
                    // Operators and delegates are kept as they are.
                    d.Kind = Regex.IsMatch(beforeParen, @"\b(operator|delegate)\b") ? Kind.Other : Kind.Method;
                    d.Name = match.Groups[1].Value;
                    d.ReturnsVoid = Regex.IsMatch(beforeParen.Substring(0, match.Index), @"\bvoid\b") || d.Name == containerName;
                    d.OutParameters = OutParameterNames(signature.Substring(paren));
                    d.Body = end == '{' ? s.SkipBalanced() : end == '>' ? s.SkipToSemicolon() : null;
                }
                else if (end == '{' || end == '>')
                {
                    d.Kind = Kind.Property;
                    d.Name = LastName.Match(signature).Groups[1].Value;
                    d.Body = end == '{' ? s.SkipBalanced() : s.SkipToSemicolon();
                    d.Accessors = end == '>' ? new List<string> { "get" } :
                        Regex.Matches(d.Body, @"\b(get|set|init|add|remove)\b").Cast<Match>().Select(m => m.Value).Distinct().ToList();
                    if (end == '{' && d.Body.IndexOf('{', 1) < 0)
                    {
                        // An auto-property is kept as it is.
                        d.Kind = Kind.Other;
                    }
                }
                else if (Regex.IsMatch(signature, @"^\s*(using|extern)\b"))
                {
                    s.SkipToSemicolon();
                    s.Advance();
                    return null;
                }
                else
                {
                    d.Kind = Regex.IsMatch(signature, @"\bconst\b") ? Kind.Other : Kind.Field;
                    d.Name = LastName.Match(signature).Groups[1].Value;
                    if (end == '=')
                    {
                        s.SkipToSemicolon();
                    }
                }
                d.Text = s.Text.Substring(start, s.Position - start);
                if (s.Current == ';')
                {
                    s.Advance();
                    d.Text += ";";
                }
                return d;
            }

            private static string StripAttributes(string header)
            {
                var s = new Scanner(header);
                while (true)
                {
                    s.SkipTrivia();
                    if (s.AtEnd || s.Current != '[')
                    {
                        return header.Substring(s.Position);
                    }
                    s.SkipBalanced();
                }
            }

            // parameterList starts at the '(' of the parameter list.
            private static List<string> OutParameterNames(string parameterList)
            {
                var names = new List<string>();
                int depth = 0, start = 1;
                for (int i = 0; i < parameterList.Length; i++)
                {
                    char c = parameterList[i];
                    depth += "(<[".IndexOf(c) >= 0 ? 1 : ")>]".IndexOf(c) >= 0 ? -1 : 0;
                    if ((c == ',' && depth == 1) || depth == 0)
                    {
                        string parameter = StripAttributes(parameterList.Substring(start, i - start)).Trim();
                        if (parameter.StartsWith("out ", StringComparison.Ordinal))
                        {
                            names.Add(LastName.Match(parameter).Groups[1].Value);
                        }
                        start = i + 1;
                    }
                    if (depth == 0)
                    {
                        break;
                    }
                }
                return names;
            }
        }
        // Walks C# text, skipping comments, preprocessor lines and literals.
        private sealed class Scanner
        {
            public Scanner(string text)
            {
                Text = text;
            }

            public string Text { get; }
            public int Position { get; private set; }
            public bool AtEnd => Position >= Text.Length;
            public char Current => AtEnd ? '\0' : Text[Position];
            private char Next => Position + 1 < Text.Length ? Text[Position + 1] : '\0';

            public void Advance()
            {
                Position++;
            }

            public int LineStart()
            {
                int i = Position;
                while (i > 0 && Text[i - 1] != '\n')
                {
                    i--;
                }
                return i;
            }

            public void SkipTrivia()
            {
                while (!AtEnd)
                {
                    if (char.IsWhiteSpace(Current))
                    {
                        Position++;
                    }
                    else if (Current == '/' && Next == '/' || Current == '#' && Text.Substring(LineStart(), Position - LineStart()).Trim().Length == 0)
                    {
                        SkipLine();
                    }
                    else if (Current == '/' && Next == '*')
                    {
                        int close = Text.IndexOf("*/", Position + 2, StringComparison.Ordinal);
                        Position = close < 0 ? Text.Length : close + 2;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            // Stops at the '{' of a body, the ';' of a declaration, the '=' of an initializer, or the "=>" of an expression
            // body (returns '>', at the '=').
            public char ScanToDeclarationEnd()
            {
                int depth = 0;
                while (!AtEnd)
                {
                    if (SkipCommentOrLiteral())
                    {
                        continue;
                    }
                    char c = Current;
                    if (c == '(' || c == '[')
                    {
                        depth++;
                    }
                    else if (c == ')' || c == ']')
                    {
                        depth--;
                    }
                    else if (depth == 0 && (c == '{' || c == ';' || c == '}'))
                    {
                        return c;
                    }
                    else if (depth == 0 && c == '=' && Next != '=' && "=!<>".IndexOf(Text[Position - 1]) < 0)
                    {
                        return Next == '>' ? '>' : '=';
                    }
                    Position++;
                }
                return '\0';
            }

            // From a '{', '(' or '[' to after its match; returns the text.
            public string SkipBalanced()
            {
                int start = Position, depth = 0;
                while (!AtEnd)
                {
                    if (SkipCommentOrLiteral())
                    {
                        continue;
                    }
                    char c = Current;
                    depth += c == '{' || c == '(' || c == '[' ? 1 : c == '}' || c == ')' || c == ']' ? -1 : 0;
                    Position++;
                    if (depth == 0)
                    {
                        break;
                    }
                }
                return Text.Substring(start, Position - start);
            }

            public string SkipToSemicolon()
            {
                int start = Position;
                while (!AtEnd && Current != ';')
                {
                    if (SkipCommentOrLiteral())
                    {
                        continue;
                    }
                    if (Current == '{' || Current == '(' || Current == '[')
                    {
                        SkipBalanced();
                    }
                    else
                    {
                        Position++;
                    }
                }
                return Text.Substring(start, Position - start);
            }

            private void SkipLine()
            {
                int newline = Text.IndexOf('\n', Position);
                Position = newline < 0 ? Text.Length : newline + 1;
            }

            private bool SkipCommentOrLiteral()
            {
                if (Current == '/' && (Next == '/' || Next == '*'))
                {
                    SkipTrivia();
                    return true;
                }
                if (Current == '@' && Next == '"')
                {
                    Position += 2;
                    while (!AtEnd && !(Current == '"' && Next != '"'))
                    {
                        Position += Current == '"' ? 2 : 1;
                    }
                    Position++;
                    return true;
                }
                if (Current == '"' || Current == '\'')
                {
                    char quote = Current;
                    Position++;
                    while (!AtEnd && Current != quote)
                    {
                        Position += Current == '\\' ? 2 : 1;
                    }
                    Position++;
                    return true;
                }
                return false;
            }
        }
    }
}
