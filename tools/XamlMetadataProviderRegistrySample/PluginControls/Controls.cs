// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PluginControls
{
    // A plugin control: a labelled, counted badge built in code (no template of its own, so it needs no
    // ms-appx resources from the plugin).
    public sealed class Badge : ContentControl
    {
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(Badge), new PropertyMetadata(string.Empty, (d, e) => ((Badge)d).Refresh()));

        public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
            nameof(Count), typeof(int), typeof(Badge), new PropertyMetadata(0, (d, e) => ((Badge)d).Refresh()));

        private readonly TextBlock _text = new TextBlock();

        public Badge()
        {
            Content = _text;
            Refresh();
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public int Count
        {
            get => (int)GetValue(CountProperty);
            set => SetValue(CountProperty, value);
        }

        public string RenderedText => _text.Text;

        private void Refresh() => _text.Text = $"{Label} ({Count})";
    }

    // A plugin panel whose children come from a property: <p:StarRating Stars="3"/>.
    public sealed class StarRating : StackPanel
    {
        public static readonly DependencyProperty StarsProperty = DependencyProperty.Register(
            nameof(Stars), typeof(int), typeof(StarRating), new PropertyMetadata(0, (d, e) => ((StarRating)d).Refresh()));

        public StarRating()
        {
            Orientation = Orientation.Horizontal;
        }

        public int Stars
        {
            get => (int)GetValue(StarsProperty);
            set => SetValue(StarsProperty, value);
        }

        private void Refresh()
        {
            Children.Clear();
            for (int i = 0; i < Stars; i++)
            {
                Children.Add(new TextBlock { Text = "*" });
            }
        }
    }
}
