// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// The "newly generated binding scope", living in a side assembly that is compiled after the app and
// loaded at runtime. This is the shape a XAML compiler would emit for
//
//     <TextBlock x:Name="TitleText" Text="{x:Bind ViewModel.Title, Mode=OneWay}" />
//     <TextBlock x:Name="AltText"   Text="{x:Bind ViewModel.Subtitle, Mode=OneWay}" />
//     <ListView  x:Name="TodoList"  ItemsSource="{x:Bind ViewModel.Items, Mode=OneWay}" />
//
// on a page whose previously built XBF contained no bindings at all. It is strongly typed against
// the real element types and the real view model type, and it reproduces the cold-built semantics:
// a scope object produced from the root connection id, Connect for every id, a data root, listeners
// on the root and on the view model, and an initial update.
//
// Using a side assembly avoids needing the XAML compiler's new nested types to be inserted into the
// existing assembly by Edit and Continue. The tradeoff versus MetadataUpdater is discussed in the
// README next to this file.

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using LiveBindScopeProbe;
using LiveBindScopeProbe.Contracts;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace LiveBindScopeProbe.GeneratedScope
{
    // Connection ids, as a compiler would assign them for this document.
    internal static class Ids
    {
        public const int Root = 1;
        public const int TitleText = 2;
        public const int AltText = 3;
        public const int TodoList = 4;
    }

    public abstract class SubjectScopeConnectorBase : IComponentConnector, IXamlBindScopeManifest
    {
        private readonly string _expectedBaseTreeRevision;

        protected SubjectScopeConnectorBase(string expectedBaseTreeRevision)
        {
            _expectedBaseTreeRevision = expectedBaseTreeRevision ?? string.Empty;
        }

        public int RootConnectionId => Ids.Root;
        public int[] GetRequiredConnectionIds() => new[] { Ids.Root, Ids.TitleText, Ids.AltText, Ids.TodoList };
        public abstract string ScopeRevision { get; }
        public string ExpectedBaseTreeRevision => _expectedBaseTreeRevision;

        // Event-root Connect. The stock parser calls this for every id on the page itself; this
        // document has no event handlers, so there is nothing to wire.
        public void Connect(int connectionId, object target) { }

        public IComponentConnector GetBindingConnector(int connectionId, object target)
        {
            if (connectionId != Ids.Root) { return null; }
            if (!(target is SubjectPage page)) { return null; }
            return CreateScope(page);
        }

        protected virtual IComponentConnector CreateScope(SubjectPage page)
        {
            SubjectBindings bindings = CreateBindings();
            bindings.SetDataRoot(page);
            return bindings;
        }

        protected abstract SubjectBindings CreateBindings();
    }

    // Revision 1: TitleText shows Title, AltText shows Subtitle.
    public sealed class SubjectScopeConnectorV1 : SubjectScopeConnectorBase
    {
        public const string Revision = "scope@v1:title->Title,alt->Subtitle,list->Items";
        public SubjectScopeConnectorV1(string expectedBaseTreeRevision) : base(expectedBaseTreeRevision) { }
        public override string ScopeRevision => Revision;
        protected override SubjectBindings CreateBindings() => new SubjectBindings(swapPaths: false);
    }

    // Revision 2: the two paths are swapped. Used to prove that after a replace the old path stops
    // writing and the new one takes over.
    public sealed class SubjectScopeConnectorV2 : SubjectScopeConnectorBase
    {
        public const string Revision = "scope@v2:title->Subtitle,alt->Title,list->Items";
        public SubjectScopeConnectorV2(string expectedBaseTreeRevision) : base(expectedBaseTreeRevision) { }
        public override string ScopeRevision => Revision;
        protected override SubjectBindings CreateBindings() => new SubjectBindings(swapPaths: true);
    }

    // A connector whose scope deliberately omits IXamlBindScopeLifecycle. Negative control for
    // "a scope that can be connected but never initialized or stopped must be refused, not reported
    // as attached".
    public sealed class InertScopeConnector : SubjectScopeConnectorBase
    {
        public const string Revision = "scope@inert";
        public InertScopeConnector(string expectedBaseTreeRevision) : base(expectedBaseTreeRevision) { }
        public override string ScopeRevision => Revision;
        protected override SubjectBindings CreateBindings() => null;
        protected override IComponentConnector CreateScope(SubjectPage page) => new InertBindings();
    }

    internal sealed class InertBindings : IComponentConnector
    {
        public void Connect(int connectionId, object target) { }
        public IComponentConnector GetBindingConnector(int connectionId, object target) => null;
    }

    public sealed class SubjectBindings : IComponentConnector, IXamlBindScopeLifecycle
    {
        private readonly bool _swapPaths;

        private SubjectPage _root;
        private TextBlock _titleText;
        private TextBlock _altText;
        private ListView _todoList;

        private TodoViewModel _observedViewModel;
        private bool _initialized;

        internal SubjectBindings(bool swapPaths) { _swapPaths = swapPaths; }

        internal void SetDataRoot(SubjectPage root) { _root = root; }

        public void Connect(int connectionId, object target)
        {
            switch (connectionId)
            {
                case Ids.Root: _root = (SubjectPage)target; break;
                case Ids.TitleText: _titleText = (TextBlock)target; break;
                case Ids.AltText: _altText = (TextBlock)target; break;
                case Ids.TodoList: _todoList = (ListView)target; break;
            }
        }

        public IComponentConnector GetBindingConnector(int connectionId, object target) => null;

        // Stands in for the Loading callback a cold parse subscribes to inside GetBindingConnector.
        public void InitializeScope()
        {
            if (_initialized) { return; }
            _initialized = true;

            _root.PropertyChanged += OnRootPropertyChanged;
            ObserveViewModel(_root.ViewModel);
            Update();
        }

        public void DetachScope()
        {
            if (!_initialized) { return; }
            _initialized = false;

            _root.PropertyChanged -= OnRootPropertyChanged;
            ObserveViewModel(null);
        }

        private void OnRootPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SubjectPage.ViewModel))
            {
                ObserveViewModel(_root.ViewModel);
                Update();
            }
        }

        private void ObserveViewModel(TodoViewModel viewModel)
        {
            if (ReferenceEquals(_observedViewModel, viewModel)) { return; }

            if (_observedViewModel != null)
            {
                _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _observedViewModel.Items.CollectionChanged -= OnItemsChanged;
            }

            _observedViewModel = viewModel;

            if (_observedViewModel != null)
            {
                _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
                _observedViewModel.Items.CollectionChanged += OnItemsChanged;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e) => Update();

        private void OnItemsChanged(object sender, NotifyCollectionChangedEventArgs e) { /* ItemsSource is live */ }

        private void Update()
        {
            TodoViewModel vm = _observedViewModel;
            if (vm == null) { return; }

            if (_titleText != null) { _titleText.Text = _swapPaths ? vm.Subtitle : vm.Title; }
            if (_altText != null) { _altText.Text = _swapPaths ? vm.Title : vm.Subtitle; }
            if (_todoList != null && !ReferenceEquals(_todoList.ItemsSource, vm.Items)) { _todoList.ItemsSource = vm.Items; }
        }
    }

    // Entry point the probe reaches by reflection, so the app has no compile-time dependency on the
    // side assembly.
    public static class ScopeFactory
    {
        public static IComponentConnector CreateV1(string expectedBaseTreeRevision) => new SubjectScopeConnectorV1(expectedBaseTreeRevision);
        public static IComponentConnector CreateV2(string expectedBaseTreeRevision) => new SubjectScopeConnectorV2(expectedBaseTreeRevision);
        public static IComponentConnector CreateInert(string expectedBaseTreeRevision) => new InertScopeConnector(expectedBaseTreeRevision);
        public static string RevisionV1 => SubjectScopeConnectorV1.Revision;
        public static string RevisionV2 => SubjectScopeConnectorV2.Revision;

        // Used only by the "skip detach" negative control, which deliberately bypasses runtime
        // ownership to show what the contract is protecting against.
        public static IComponentConnector CreateBareScope(object page, bool swapPaths)
        {
            var bindings = new SubjectBindings(swapPaths);
            bindings.SetDataRoot((SubjectPage)page);
            return bindings;
        }

        public static void InitializeBareScope(IComponentConnector scope) => ((IXamlBindScopeLifecycle)scope).InitializeScope();
    }
}
