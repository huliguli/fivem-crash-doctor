using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CrashDoctor.UI
{
    /// <summary>Dunkle Rückfrage vor einer Reparatur: zeigt genau, was passiert, und wie man es rückgängig macht.</summary>
    public sealed class ConfirmWindow : Window
    {
        ConfirmWindow(string title, IEnumerable<string> lines, string undo, bool admin, string okText)
        {
            Title = title;
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            Width = 560;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Res("SurfaceBrush");
            Foreground = Res("TextBrush");
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
            FontSize = 13;
            SourceInitialized += (s, e) => MainWindow.UseDarkTitleBar(this);

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H1"], FontSize = 18 });

            var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var l in lines)
                list.Children.Add(new TextBlock { Text = l, Style = (Style)Application.Current.Resources["Body"], Margin = new Thickness(0, 0, 0, 6) });
            root.Children.Add(list);

            if (!string.IsNullOrEmpty(undo))
            {
                var box = new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0) };
                box.Child = new TextBlock { Text = "Rückgängig: " + undo, Style = (Style)Application.Current.Resources["Muted"] };
                root.Children.Add(box);
            }
            if (admin)
                root.Children.Add(new TextBlock
                {
                    Text = "Windows fragt gleich nach Admin-Rechten. Mit diesen Rechten führt das Tool nur diese eine Reparatur aus.",
                    Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 12, 0, 0)
                });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = "Abbrechen", Style = (Style)Application.Current.Resources["SecondaryButton"], IsCancel = true, MinWidth = 110 };
            var ok = new Button { Content = okText, Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true, MinWidth = 140, Margin = new Thickness(10, 0, 0, 0) };
            ok.Click += (s, e) => { DialogResult = true; };
            if (okText != "OK") buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            Content = root;
        }

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        public static bool Ask(string title, IEnumerable<string> lines, string undo, bool admin, string okText = "Ausführen")
        {
            var w = new ConfirmWindow(title, lines, undo, admin, okText);
            return w.ShowDialog() == true;
        }

        public static void Inform(string title, IEnumerable<string> lines)
        {
            var w = new ConfirmWindow(title, lines, null, false, "OK");
            w.ShowDialog();
        }
    }
}
