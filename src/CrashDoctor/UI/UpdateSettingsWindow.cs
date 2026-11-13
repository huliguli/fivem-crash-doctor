using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CrashDoctor.Platform;

namespace CrashDoctor.UI
{
    /// <summary>Version, „Nach Updates suchen“ und die beiden Update-Schalter.</summary>
    public sealed class UpdateSettingsWindow : Window
    {
        UpdateSettingsWindow(AppSettings settings, Func<Task<string>> checkNow)
        {
            Title = "Updates";
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            Width = 500;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Res("SurfaceBrush");
            Foreground = Res("TextBrush");
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
            FontSize = 13;
            SourceInitialized += (s, e) => MainWindow.UseDarkTitleBar(this);

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = "FiveM Crash Doctor " + Updater.CurrentVersion, Style = (Style)Application.Current.Resources["H1"], FontSize = 18 });
            var last = settings.LastCheckUtc.HasValue ? "Zuletzt geprüft: " + settings.LastCheckUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "Noch nicht geprüft.";
            var status = new TextBlock { Text = last, Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 4, 0, 14) };
            root.Children.Add(status);

            var check = new CheckBox
            {
                Content = "Beim Start und stündlich nach Updates suchen", IsChecked = settings.UpdateCheck,
                Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 8)
            };
            var auto = new CheckBox
            {
                Content = "Updates beim Start automatisch installieren", IsChecked = settings.UpdateAutoInstall,
                Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 4)
            };
            check.Checked += (s, e) => { settings.UpdateCheck = true; auto.IsEnabled = true; };
            check.Unchecked += (s, e) => { settings.UpdateCheck = false; auto.IsEnabled = false; };
            auto.Checked += (s, e) => settings.UpdateAutoInstall = true;
            auto.Unchecked += (s, e) => settings.UpdateAutoInstall = false;
            auto.IsEnabled = settings.UpdateCheck;
            root.Children.Add(check);
            root.Children.Add(auto);
            root.Children.Add(new TextBlock
            {
                Text = "Das Tool fragt dafür nur api.github.com nach der neuesten Version – ohne Daten von dir. Installiert wird nur eine EXE mit passender Prüfsumme und der Signatur des Projekts.",
                Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 8, 0, 0)
            });
            if (!string.IsNullOrEmpty(settings.SkippedVersion))
            {
                var unskip = new Button { Content = $"Übersprungene Version {settings.SkippedVersion} wieder anbieten", Style = (Style)Application.Current.Resources["GhostButton"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 8, 0, 0) };
                unskip.Click += (s, e) => { settings.SkippedVersion = null; unskip.Visibility = Visibility.Collapsed; };
                root.Children.Add(unskip);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var search = new Button { Content = "Jetzt nach Updates suchen", Style = (Style)Application.Current.Resources["SecondaryButton"], MinWidth = 180 };
            search.Click += async (s, e) =>
            {
                search.IsEnabled = false;
                status.Text = "Suche …";
                try { status.Text = await checkNow(); }
                finally { search.IsEnabled = true; }
            };
            var close = new Button { Content = "Schließen", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true, IsCancel = true, MinWidth = 110, Margin = new Thickness(10, 0, 0, 0) };
            close.Click += (s, e) => Close();
            buttons.Children.Add(search);
            buttons.Children.Add(close);
            root.Children.Add(buttons);
            Content = root;
        }

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        public static void Show(AppSettings settings, Func<Task<string>> checkNow) =>
            new UpdateSettingsWindow(settings, checkNow).ShowDialog();
    }
}
