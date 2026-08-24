// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;

namespace LiveBindScopeProbe.Model
{
    // A stand-in element. Two of these with the same TypeName are indistinguishable by cast, which is
    // the whole point of the identity control.
    public sealed class FakeElement
    {
        public FakeElement(string typeName, string name) { TypeName = typeName; Name = name; }
        public string TypeName { get; }
        public string Name { get; }
        public object Owner { get; set; }
        public string Written { get; set; }
        public int WriteCount { get; private set; }

        public void Write(string value) { Written = value; WriteCount++; }
        public override string ToString() => $"{TypeName}:{Name}#{GetHashCode():x}";
    }

    public sealed class FakeRoot
    {
        public readonly Dictionary<string, FakeElement> Names = new();
        public string BaseTreeRevision = string.Empty;
        public string Value = "v0";
        public event Action ValueChanged;

        public FakeElement Add(string typeName, string name)
        {
            var element = new FakeElement(typeName, name) { Owner = this };
            Names[name] = element;
            return element;
        }

        public void SetValue(string value) { Value = value; ValueChanged?.Invoke(); }

        // Subscribe/unsubscribe are what DetachScope has to undo.
        public void Subscribe(Action handler) => ValueChanged += handler;
        public void Unsubscribe(Action handler) => ValueChanged -= handler;
    }

    public sealed class FakeHost : IScopeHost
    {
        public object ResolveName(object root, string name)
            => root is FakeRoot r && r.Names.TryGetValue(name, out var e) ? e : null;

        public object GetNamescopeOwner(object target)
            => target is FakeElement e ? e.Owner : null;

        public string GetRuntimeTypeName(object target) => target switch
        {
            FakeElement e => e.TypeName,
            FakeRoot => "FakeRoot",
            _ => target?.GetType().FullName,
        };

        public string GetBaseTreeRevision(object root)
            => root is FakeRoot r ? r.BaseTreeRevision : string.Empty;
    }

    public sealed class FakeScope : IScope, IScopeLifecycle
    {
        private readonly bool _swap;
        private FakeRoot _root;
        private FakeElement _a;
        private FakeElement _b;
        private bool _initialized;

        public FakeScope(bool swap) { _swap = swap; }

        public int InitializeCount { get; private set; }
        public int DetachCount { get; private set; }
        public bool IsSubscribed { get; private set; }

        public void Connect(int connectionId, object target)
        {
            switch (connectionId)
            {
                case 1: _root = (FakeRoot)target; break;
                case 2: _a = (FakeElement)target; break;
                case 3: _b = (FakeElement)target; break;
            }
        }

        public void InitializeScope()
        {
            if (_initialized) { return; }
            _initialized = true;
            _root.Subscribe(OnChanged);
            IsSubscribed = true;
            InitializeCount++;
            OnChanged();
        }

        public void DetachScope()
        {
            if (!_initialized) { return; }
            _initialized = false;
            _root.Unsubscribe(OnChanged);
            IsSubscribed = false;
            DetachCount++;
        }

        private void OnChanged()
        {
            _a?.Write(_swap ? "B:" + _root.Value : "A:" + _root.Value);
            _b?.Write(_swap ? "A:" + _root.Value : "B:" + _root.Value);
        }
    }

    // A scope that can be connected but never initialized or stopped.
    public sealed class InertScope : IScope
    {
        public void Connect(int connectionId, object target) { }
    }

    public sealed class FakeConnector : IConnector, IScopeManifest
    {
        private readonly Func<object> _scopeFactory;

        public FakeConnector(string scopeRevision, string expectedBaseTreeRevision, Func<object> scopeFactory, int[] requiredIds = null)
        {
            ScopeRevision = scopeRevision;
            ExpectedBaseTreeRevision = expectedBaseTreeRevision;
            _scopeFactory = scopeFactory;
            RequiredConnectionIds = requiredIds ?? new[] { 1, 2, 3 };
        }

        public int RootConnectionId => 1;
        public int[] RequiredConnectionIds { get; }
        public string ScopeRevision { get; }
        public string ExpectedBaseTreeRevision { get; }
        public object LastProduced { get; private set; }

        public object GetBindingConnector(int connectionId, object root)
        {
            if (connectionId != RootConnectionId) { return null; }
            LastProduced = _scopeFactory();
            return LastProduced;
        }
    }

    // A connector that does not declare a manifest at all.
    public sealed class ManifestlessConnector : IConnector
    {
        public object GetBindingConnector(int connectionId, object root) => new FakeScope(false);
    }
}
