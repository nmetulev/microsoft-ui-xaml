// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LiveBindScopeProbe
{
    public sealed class TodoViewModel : INotifyPropertyChanged
    {
        private string _title = "title-0";
        private string _subtitle = "subtitle-0";

        public ObservableCollection<string> Items { get; } = new ObservableCollection<string> { "a", "b" };

        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; Raise(); } }
        }

        public string Subtitle
        {
            get => _subtitle;
            set { if (_subtitle != value) { _subtitle = value; Raise(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
