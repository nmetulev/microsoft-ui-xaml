// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Microsoft.UI.Xaml.Markup.Compiler
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Xaml;
    using XamlDom;

    // EnableXamlHotReloadCodegen: connection IDs that don't change when other markup in the page changes.
    //
    // Generated fields and Bindings classes are named after connection IDs, and .NET hot reload can't change the type of
    // a field. A build numbers elements in document order, as it always has, and records a key for each one. The compiler
    // service then gives an element whose key it has seen the same ID, and a new element the next unused ID, so inserting
    // an element doesn't renumber the others, and an ID is never given to a different element.
    //
    // An element's key: the key of the template it is in, its type, the markup that shapes its generated code (x:Name,
    // x: directives, events and x:Bind), and how many elements before it in the same template have all of that too.
    internal sealed class ConnectionIdKeys
    {
        private readonly bool _keepRecordedIds;
        private readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<XamlDomObject, string> _keys = new Dictionary<XamlDomObject, string>();
        private int _last;

        // keepRecordedIds: false for a build (number in document order, record the keys), true for the compiler service
        // (Ids holds what the build and earlier compiles recorded, and new keys are added to it).
        public ConnectionIdKeys(Dictionary<string, int> ids, bool keepRecordedIds)
        {
            Ids = ids;
            _keepRecordedIds = keepRecordedIds;
            _last = ids.Count == 0 ? 0 : ids.Values.Max();
        }

        public Dictionary<string, int> Ids { get; }

        public int GetConnectionId(XamlDomObject domObject, int nextInDocumentOrder)
        {
            string key = KeyOf(domObject);
            // The same element can be collected twice (a template, as an element and as a scope root).
            for (int n = 2; !_used.Add(key); n++)
            {
                key = KeyOf(domObject) + "~" + n;
            }

            if (!_keepRecordedIds)
            {
                Ids[key] = nextInDocumentOrder;
                return nextInDocumentOrder;
            }
            if (!Ids.TryGetValue(key, out int id))
            {
                id = ++_last;
                Ids[key] = id;
            }
            return id;
        }

        private string KeyOf(XamlDomObject domObject)
        {
            if (!_keys.TryGetValue(domObject, out string key))
            {
                XamlDomObject root = domObject;
                while (root.Parent?.Parent != null)
                {
                    root = root.Parent.Parent;
                }
                AddKeys(root, string.Empty, new Dictionary<string, int>(StringComparer.Ordinal));
                key = _keys[domObject];
            }
            return key;
        }

        private void AddKeys(XamlDomObject domObject, string scope, Dictionary<string, int> seenInScope)
        {
            string local = (domObject.Type + Signature(domObject)).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            seenInScope.TryGetValue(local, out int before);
            seenInScope[local] = before + 1;
            string key = scope + "/" + local + (before > 0 ? "@" + before : string.Empty);
            _keys[domObject] = key;

            bool isTemplate = domObject.Type.IsDerivedFromFrameworkTemplate();
            foreach (XamlDomMember member in domObject.MemberNodes)
            {
                foreach (XamlDomObject child in member.Items.OfType<XamlDomObject>())
                {
                    if (isTemplate)
                    {
                        AddKeys(child, key, new Dictionary<string, int>(StringComparer.Ordinal));
                    }
                    else
                    {
                        AddKeys(child, scope, seenInScope);
                    }
                }
            }
        }

        private static string Signature(XamlDomObject domObject)
        {
            XamlMember nameProperty = domObject.Type.GetAliasedProperty(XamlLanguage.Name);
            var parts = new List<string>();
            foreach (XamlDomMember member in domObject.MemberNodes)
            {
                bool isTemplateTargetType = member.Member.Name == "TargetType" && domObject.Type.IsDerivedFromFrameworkTemplate();
                bool hasOnlyText = member.Items.All(i => i is XamlDomValue);
                if (member.Member.IsEvent || member.Items.Any(i => i is XamlDomObject o && DomHelper.IsBindExtension(o)) || isTemplateTargetType ||
                    ((member.Member.IsDirective || member.Member == nameProperty) && hasOnlyText))
                {
                    parts.Add(member.Member.Name + "=" + Text(member));
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return parts.Count == 0 ? string.Empty : "[" + string.Join(";", parts) + "]";
        }

        private static string Text(XamlDomMember member)
        {
            return string.Join(",", member.Items.Select(item =>
                item is XamlDomValue value ? Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) :
                item is XamlDomObject obj ? obj.Type.Name + "(" + string.Join(";", obj.MemberNodes.Select(m => m.Member.Name + "=" + Text(m))) + ")" :
                string.Empty));
        }
    }
}
