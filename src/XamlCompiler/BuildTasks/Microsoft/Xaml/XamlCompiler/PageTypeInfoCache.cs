// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Xaml;
    using DirectUI;

    // Compiler service, hot reload builds (C#) only.
    //
    // Regenerating XamlTypeInfo reads every page for type information: parse, validate and collect, about 1.2 s on WinUI
    // Gallery. What a page contributes depends on its markup text and on the types it names, so the service records, per
    // page, the calls the type collection made into XamlSchemaCodeInfo (by type and member name) and replays them on later
    // compiles while the page's text is unchanged. Hot reload builds sort the type tables, so the order of the calls
    // doesn't change the output.
    //
    // A replay re-resolves every type and member against the current compile, so it reflects the newest project assembly.
    // It falls back to collecting the page again when anything doesn't resolve, or when a project type the record names
    // changed shape (base type or declared members): a style setter's property, for example, is found through the type
    // hierarchy. Reloading references clears the cache.
    internal enum TypeInfoOp
    {
        TypeAndProperties,
        Type,
        ReturnTypeStub,
        Member,
    }

    internal sealed class TypeInfoTypeRef
    {
        public string Assembly;
        public string FullName;
        public TypeInfoTypeRef[] TypeArguments;
        public TypeInfoTypeRef Element;
        public int ArrayRank;

        public static TypeInfoTypeRef TryCreate(Type type)
        {
            if (type == null)
            {
                return null;
            }
            if (type.IsArray)
            {
                TypeInfoTypeRef element = TryCreate(type.GetElementType());
                return element == null ? null : new TypeInfoTypeRef { Element = element, ArrayRank = type.GetArrayRank() };
            }
            if (type.IsGenericParameter || type.IsByRef || type.IsPointer)
            {
                return null;
            }
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                TypeInfoTypeRef definition = TryCreate(type.GetGenericTypeDefinition());
                TypeInfoTypeRef[] arguments = type.GetGenericArguments().Select(TryCreate).ToArray();
                if (definition == null || arguments.Any(a => a == null))
                {
                    return null;
                }
                definition.TypeArguments = arguments;
                return definition;
            }
            if (type.FullName == null || type.Assembly == null)
            {
                return null;
            }
            return new TypeInfoTypeRef { Assembly = type.Assembly.GetName().Name, FullName = type.FullName };
        }

        public Type Resolve(IDictionary<string, Assembly> assemblies)
        {
            if (Element != null)
            {
                Type element = Element.Resolve(assemblies);
                return element == null ? null : (ArrayRank == 1 ? element.MakeArrayType() : element.MakeArrayType(ArrayRank));
            }
            Assembly assembly;
            if (!assemblies.TryGetValue(Assembly, out assembly))
            {
                return null;
            }
            Type type = assembly.GetType(FullName, throwOnError: false);
            if (type == null || TypeArguments == null)
            {
                return type;
            }
            Type[] arguments = new Type[TypeArguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                arguments[i] = TypeArguments[i].Resolve(assemblies);
                if (arguments[i] == null)
                {
                    return null;
                }
            }
            return type.MakeGenericType(arguments);
        }

        public override string ToString()
        {
            if (Element != null)
            {
                return Element + "[" + ArrayRank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            }
            return "[" + Assembly + "]" + FullName + (TypeArguments == null ? "" : "<" + string.Join(",", TypeArguments.Select(a => a.ToString())) + ">");
        }

        public IEnumerable<TypeInfoTypeRef> SelfAndParts()
        {
            yield return this;
            foreach (TypeInfoTypeRef part in (TypeArguments ?? new TypeInfoTypeRef[0]).Concat(Element != null ? new[] { Element } : new TypeInfoTypeRef[0]))
            {
                foreach (TypeInfoTypeRef nested in part.SelfAndParts())
                {
                    yield return nested;
                }
            }
        }
    }

    internal sealed class TypeInfoRecord
    {
        public TypeInfoOp Op;
        public TypeInfoTypeRef Type;
        public string MemberName;
        public bool IsAttachable;
        public bool IsStub;
        private string _key;

        // Identity of the call: replaying the same call twice in one compile changes nothing.
        public string Key
        {
            get { return _key ?? (_key = ((int)Op).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Type + "|" + MemberName + "|" + IsAttachable + "|" + IsStub); }
        }
    }

    // Set on XamlSchemaCodeInfo while a page's type information is collected.
    internal sealed class TypeInfoRecorder
    {
        public readonly List<TypeInfoRecord> Records = new List<TypeInfoRecord>();
        public bool Failed;

        public void Record(TypeInfoOp op, XamlType type, XamlMember member, bool isStub)
        {
            if (member != null && member.IsDirective)
            {
                // AddMember ignores directives.
                return;
            }
            XamlType owner = member != null ? member.DeclaringType : type;
            TypeInfoTypeRef typeRef = owner == null ? null : TypeInfoTypeRef.TryCreate(owner.UnderlyingType);
            if (typeRef == null)
            {
                Failed = true;
                return;
            }
            Records.Add(new TypeInfoRecord
            {
                Op = op,
                Type = typeRef,
                MemberName = member?.Name,
                IsAttachable = member != null && member.IsAttachable,
                IsStub = isStub,
            });
        }
    }

    internal sealed class PageTypeInfoEntry
    {
        public string ContentHash;
        public List<TypeInfoRecord> Records;
        // Project types the records name, by full name, with their shape when recorded.
        public Dictionary<string, string> LocalTypeShapes;
    }

    internal static class PageTypeInfoCache
    {
        private static readonly Dictionary<string, PageTypeInfoEntry> s_entries = new Dictionary<string, PageTypeInfoEntry>(StringComparer.OrdinalIgnoreCase);

        // Per compile, reported in the compiler's markers.
        public static readonly System.Diagnostics.Stopwatch ShapeTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch ResolveTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch ApplyTime = new System.Diagnostics.Stopwatch();
        public static int AppliedRecords;

        public static void Clear()
        {
            s_entries.Clear();
        }

        private static readonly Dictionary<string, KeyValuePair<string, string>> s_hashes = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);

        // The file's content hash, read again only when its size or write time changed.
        public static string HashFile(string path)
        {
            var info = new System.IO.FileInfo(path);
            string stamp = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            KeyValuePair<string, string> cached;
            if (s_hashes.TryGetValue(path, out cached) && cached.Key == stamp)
            {
                return cached.Value;
            }
            using (SHA256 sha = SHA256.Create())
            {
                string hash = Convert.ToBase64String(sha.ComputeHash(System.IO.File.ReadAllBytes(path)));
                s_hashes[path] = new KeyValuePair<string, string>(stamp, hash);
                return hash;
            }
        }

        public static void Store(string xamlPath, string contentHash, TypeInfoRecorder recorder, Assembly localAssembly, Dictionary<Type, string> shapeCache)
        {
            if (recorder.Failed)
            {
                s_entries.Remove(xamlPath);
                return;
            }
            string localName = localAssembly?.GetName().Name;
            var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
            var assemblies = localAssembly == null ? null : new Dictionary<string, Assembly> { { localName, localAssembly } };
            foreach (TypeInfoTypeRef part in recorder.Records.SelectMany(r => r.Type.SelfAndParts()).Where(p => p.Assembly != null && p.Assembly == localName))
            {
                if (!shapes.ContainsKey(part.FullName))
                {
                    Type type = localAssembly.GetType(part.FullName, throwOnError: false);
                    if (type == null)
                    {
                        s_entries.Remove(xamlPath);
                        return;
                    }
                    shapes.Add(part.FullName, Shape(type, shapeCache));
                }
            }
            s_entries[xamlPath] = new PageTypeInfoEntry { ContentHash = contentHash, Records = recorder.Records, LocalTypeShapes = shapes };
        }

        // Resolves every record against the current compile and applies them to schemaInfo; false (and nothing applied)
        // when the page must be collected again.
        public static bool TryReplay(string xamlPath, string contentHash, XamlSchemaCodeInfo schemaInfo, XamlSchemaContext schemaContext,
            IDictionary<string, Assembly> assemblies, Assembly localAssembly, Dictionary<Type, string> shapeCache, HashSet<string> appliedKeys)
        {
            PageTypeInfoEntry entry;
            if (!s_entries.TryGetValue(xamlPath, out entry) || entry.ContentHash != contentHash)
            {
                return false;
            }
            ShapeTime.Start();
            try
            {
                foreach (KeyValuePair<string, string> shape in entry.LocalTypeShapes)
                {
                    Type type = localAssembly?.GetType(shape.Key, throwOnError: false);
                    if (type == null || Shape(type, shapeCache) != shape.Value)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                ShapeTime.Stop();
            }
            ResolveTime.Start();

            var resolved = new List<Action>(entry.Records.Count);
            var keys = new List<string>();
            foreach (TypeInfoRecord record in entry.Records)
            {
                // The schema calls are idempotent and hot reload builds sort the tables: a call another page already
                // replayed in this compile is skipped.
                if (appliedKeys.Contains(record.Key))
                {
                    continue;
                }
                keys.Add(record.Key);
                Type type = record.Type.Resolve(assemblies);
                XamlType xamlType = type == null ? null : schemaContext.GetXamlType(type);
                if (xamlType == null)
                {
                    ResolveTime.Stop();
                    return false;
                }
                TypeInfoRecord r = record;
                switch (record.Op)
                {
                    case TypeInfoOp.TypeAndProperties:
                        resolved.Add(() => schemaInfo.AddTypeAndProperties(xamlType));
                        break;
                    case TypeInfoOp.Type:
                        resolved.Add(() => schemaInfo.AddType(xamlType, r.IsStub));
                        break;
                    case TypeInfoOp.ReturnTypeStub:
                        resolved.Add(() => schemaInfo.AddReturnTypeStub(xamlType));
                        break;
                    case TypeInfoOp.Member:
                        XamlMember member = record.IsAttachable ? xamlType.GetAttachableMember(record.MemberName) : xamlType.GetMember(record.MemberName);
                        if (member == null)
                        {
                            ResolveTime.Stop();
                            return false;
                        }
                        resolved.Add(() => schemaInfo.AddMember(null, member, r.IsStub));
                        break;
                }
            }
            ResolveTime.Stop();
            ApplyTime.Start();
            foreach (Action action in resolved)
            {
                action();
            }
            ApplyTime.Stop();
            AppliedRecords += resolved.Count;
            appliedKeys.UnionWith(keys);
            return true;
        }

        private static string Shape(Type type, Dictionary<Type, string> cache)
        {
            string shape;
            if (cache.TryGetValue(type, out shape))
            {
                return shape;
            }
            var text = new StringBuilder();
            text.Append(type.BaseType?.AssemblyQualifiedName).Append('\n');
            foreach (string member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.MemberType + " " + m.ToString())
                .OrderBy(m => m, StringComparer.Ordinal))
            {
                text.Append(member).Append('\n');
            }
            shape = text.ToString();
            cache[type] = shape;
            return shape;
        }
    }
}
