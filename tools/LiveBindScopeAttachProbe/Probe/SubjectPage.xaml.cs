// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace LiveBindScopeProbe
{
    public sealed partial class SubjectPage : Page, INotifyPropertyChanged
    {
        private TodoViewModel _viewModel = new TodoViewModel();

        public SubjectPage()
        {
            InitializeComponent();
        }

        public TodoViewModel ViewModel
        {
            get => _viewModel;
            set { _viewModel = value; Raise(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
