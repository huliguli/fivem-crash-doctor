using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CrashDoctor.Core;
using CrashDoctor.Platform;
using CrashDoctor.UI;

namespace CrashDoctor
{
    public partial class MainWindow : Window
    {
        readonly ObservableCollection<InputFileVm> _files = new ObservableCollection<InputFileVm>();
        readonly Dictionary<string, List<FixVm>> _fixVms = new Dictionary<string, List<FixVm>>();
        readonly string[] _args;
        readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        AnalysisReport _report;
        FiveMPaths _paths;
        SystemSnapshot _snapshot;
        string _fiveMOverride;
        bool _busy;

        // Auto-Update (Bauplan: DESKTOP-APPS-LIBRARY/AUTO-UPDATE.md)
        AppSettings _settings = new AppSettings();
        IUpdateFeed _feed;
        UpdateOffer _offer;
        bool _updateFlowActive;
        readonly HashSet<string> _dismissedThisSession = new HashSet<string>();
        readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };

        public MainWindow(string[] args)
        {
            _args = args ?? new string[0];
            InitializeComponent();
            FileList.ItemsSource = _files;
            _files.CollectionChanged += (s, e) => DropHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
            LoadSettings();
            Title = $"FiveM Crash Doctor {AppLog.Version}";
            VersionText.Text = "v" + Updater.CurrentVersion;
            _feed = App.UpdateFeedFromArgs(_args);
            _updateTimer.Tick += async (s, e) => await CheckForUpdateAsync(manual: false, startup: false);

            if (SystemCollector.IsElevated())
            {
                AdminText.Text = "Admin-Rechte aktiv";
                AdminText.Foreground = (Brush)FindResource("AccentBrush");
                AdminIcon.Foreground = (Brush)FindResource("AccentBrush");
                RestartAdminButton.Visibility = Visibility.Collapsed;
            }

            SourceInitialized += (s, e) => UseDarkTitleBar(this);
            DragEnter += (s, e) => SetDropHighlight(e.Data.GetDataPresent(DataFormats.FileDrop));
            DragLeave += (s, e) => SetDropHighlight(false);
            DragOver += OnDragOver;
            Drop += OnDrop;
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;

            if (HasArg("--screenshot"))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000; Top = 0;
                ShowActivated = false;
                ShowInTaskbar = false;
            }
        }

        // ------------------------------------------------------------------ Start

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var restore = ArgValue("--restore");
            if (restore != null)
            {
                FixRunner.ReadRestore(restore, out var text, out var files);
                if (!string.IsNullOrEmpty(text)) CrashText.Text = text;
                AddFiles(files);
            }

            // Dateien, die auf die EXE gezogen wurden, kommen als Argumente
            AddFiles(_args.Where(a => !a.StartsWith("--") && File.Exists(a)), quiet: true);

            if (HasArg("--demo")) CrashText.Text = DemoText;

            var shot = ArgValue("--screenshot");
            if (shot == null)
            {
                var from = ArgValue("--updated");
                if (from != null) ShowToast($"Aktualisiert auf Version {Updater.CurrentVersion} (vorher {from}).");
                _updateTimer.Start();
                _ = CheckForUpdateAsync(manual: false, startup: true);
            }
            else if (HasArg("--demo-update"))
            {
                ShowUpdateBanner(new UpdateOffer { Release = new ReleaseInfo { Version = new Version(1, 2, 0), Notes = "Neue Regeln für ERR_GFX-Fehler\nSchnellere Dump-Analyse" } });
            }

            if (shot != null)
            {
                var size = ArgValue("--size");
                if (size != null && size.Contains("x"))
                {
                    var p = size.Split('x');
                    Width = double.Parse(p[0]); Height = double.Parse(p[1]);
                }
                if (!HasArg("--empty")) await RunAnalysisAsync();
                var tab = ArgValue("--tab");
                if (tab != null && int.TryParse(tab, out var t)) Tabs.SelectedIndex = t;
                var expand = ArgValue("--expand");
                if (expand != null && DiagnosisList.ItemsSource is IEnumerable<DiagnosisVm> vms)
                    foreach (var vm in vms) vm.IsExpanded = expand == "all";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await Task.Delay(400);
                SaveScreenshot(shot);
                Application.Current.Shutdown();
            }
            else if (restore != null && (!string.IsNullOrWhiteSpace(CrashText.Text) || _files.Count > 0))
            {
                await RunAnalysisAsync();
            }
        }

        const string DemoText = @"[Window Title]
Error nvwgf2umx.dll+5A1B2C

[Main Instruction]
nvwgf2umx.dll+5A1B2C

[Content]
An error at nvwgf2umx.dll+5A1B2C caused FiveM to stop working. A crash report is being uploaded to the FiveM developers.

[Expanded Information]
Crash signature: nvwgf2umx.dll+5A1B2C
Report ID: 3f9a1c2e-demo-0000-beispiel [Copy text]
You can press Ctrl-C to copy this message and paste it elsewhere.";

        // ------------------------------------------------------------------ Eingabe

        void CrashText_TextChanged(object sender, TextChangedEventArgs e) =>
            CrashTextPlaceholder.Visibility = string.IsNullOrEmpty(CrashText.Text) ? Visibility.Visible : Visibility.Collapsed;

        void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) { AddFiles(Clipboard.GetFileDropList().Cast<string>()); return; }
                if (Clipboard.ContainsText()) { CrashText.Text = Clipboard.GetText(); CrashText.Focus(); CrashText.CaretIndex = 0; }
                else ShowToast("Die Zwischenablage ist leer. Im Crash-Fenster zuerst Strg+C drücken.");
            }
            catch (COMException) { ShowToast("Die Zwischenablage ist gerade belegt. Bitte noch einmal versuchen."); }
        }

        void ClearText_Click(object sender, RoutedEventArgs e)
        {
            CrashText.Clear();
            _files.Clear();
            LatestInfo.Visibility = Visibility.Collapsed;
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        AddFiles(Clipboard.GetFileDropList().Cast<string>());
                        e.Handled = true;
                    }
                    else if (!CrashText.IsKeyboardFocusWithin && Clipboard.ContainsText())
                    {
                        CrashText.Text = Clipboard.GetText();
                        e.Handled = true;
                    }
                }
                catch (COMException) { }
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Analyze_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
        }

        void SetDropHighlight(bool on) =>
            DropZone.BorderBrush = (Brush)FindResource(on ? "AccentBrush" : "BorderBrush");

        void AddFiles(IEnumerable<string> paths, bool quiet = false)
        {
            var skipped = new List<string>();
            foreach (var raw in paths ?? Enumerable.Empty<string>())
            {
                var candidates = new List<string>();
                if (Directory.Exists(raw))
                {
                    candidates.AddRange(new DirectoryInfo(raw).GetFiles()
                        .Where(f => InputLoader.SupportedExtensions.Contains(f.Extension.ToLowerInvariant()))
                        .OrderByDescending(f => f.LastWriteTimeUtc).Take(5).Select(f => f.FullName));
                }
                else if (File.Exists(raw))
                {
                    if (InputLoader.SupportedExtensions.Contains(Path.GetExtension(raw).ToLowerInvariant())) candidates.Add(raw);
                    else skipped.Add(Path.GetFileName(raw));
                }
                foreach (var c in candidates)
                {
                    if (_files.Any(f => string.Equals(f.Path, c, StringComparison.OrdinalIgnoreCase))) continue;
                    InputFileVm vm = null;
                    vm = new InputFileVm { Path = c, Remove = new RelayCommand(_ => _files.Remove(vm)) };
                    _files.Add(vm);
                }
            }
            if (skipped.Count > 0 && !quiet) ShowToast("Nicht unterstützt: " + string.Join(", ", skipped) + " – erlaubt sind .dmp, .zip, .log und .txt.");
        }

        void LatestCrash_Click(object sender, RoutedEventArgs e)
        {
            var paths = FiveMPaths.Detect(_fiveMOverride);
            var found = InputLoader.FindLatestCrash(paths, out var desc);
            LatestInfo.Visibility = Visibility.Visible;
            if (found.Count == 0)
            {
                LatestInfo.Text = desc + (paths.Found ? "" : " Wähle den Ordner unter „System“ → „FiveM-Ordner wählen“.");
                return;
            }
            AddFiles(found);
            LatestInfo.Text = "Gefunden: " + desc;
        }

        void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var paths = _paths ?? FiveMPaths.Detect(_fiveMOverride);
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Crash-Dateien wählen",
                Multiselect = true,
                Filter = "Crash-Dateien (*.dmp, *.zip, *.log, *.txt)|*.dmp;*.zip;*.log;*.txt|Alle Dateien|*.*"
            };
            if (paths.Found && Directory.Exists(paths.CrashesDir)) dlg.InitialDirectory = paths.CrashesDir;
            if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
        }

        // ------------------------------------------------------------------ Analyse

        async void Analyze_Click(object sender, RoutedEventArgs e) => await RunAnalysisAsync();

        async Task RunAnalysisAsync()
        {
            if (_busy) return;
            _busy = true;
            AnalyzeButton.IsEnabled = false;
            AnalyzeText.Text = "Analysiere …";
            var text = CrashText.Text;
            var files = _files.Select(f => f.Path).ToList();
            var over = _fiveMOverride;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await Task.Run(() =>
                {
                    var paths = FiveMPaths.Detect(over);
                    var snap = SystemCollector.Collect(paths);
                    var facts = InputLoader.Load(text, files);
                    var rep = Analyzer.Analyze(facts, snap);
                    return Tuple.Create(paths, snap, rep);
                });
                _paths = result.Item1;
                _snapshot = result.Item2;
                _report = result.Item3;
                AppLog.Write($"Analyse in {sw.ElapsedMilliseconds} ms: {_report.Diagnoses.Count} Diagnosen, oben: {_report.Top?.Id ?? "-"}");
                ShowReport(!string.IsNullOrWhiteSpace(text));
            }
            catch (Exception ex)
            {
                AppLog.Write("Analyse fehlgeschlagen: " + ex);
                ConfirmWindow.Inform("Analyse fehlgeschlagen", new[] { ex.Message, "Details stehen im Tool-Protokoll: " + AppLog.Folder });
            }
            finally
            {
                _busy = false;
                AnalyzeButton.IsEnabled = true;
                AnalyzeText.Text = "Erneut analysieren";
            }
        }

        void ShowReport(bool hadText)
        {
            var r = _report;
            var f = r.Facts;
            EmptyState.Visibility = Visibility.Collapsed;
            ResultState.Visibility = Visibility.Visible;
            _fixVms.Clear();

            var top = r.Top;
            if (f.HasAnyData)
            {
                HeadlineLabel.Text = top?.Id == "disconnect" ? "ERGEBNIS" : "WAHRSCHEINLICHSTE URSACHE";
                Headline.Text = top?.Title ?? "Keine eindeutige Ursache gefunden";
                HeadlineSub.Text = top == null ? "" :
                    $"{ReportWriter.SeverityText(top.Severity)} · Wahrscheinlichkeit {ReportWriter.ConfidenceText(top.Confidence)} · {ReportWriter.EvidenceText(top.Evidence)}" +
                    (r.Diagnoses.Count > 1 ? $" · {r.Diagnoses.Count - 1} weitere Punkte unten" : "");
            }
            else
            {
                HeadlineLabel.Text = "SYSTEMCHECK";
                var issues = r.Diagnoses.Count(d => d.Severity >= Severity.Hint);
                Headline.Text = issues > 0 ? $"{issues} Auffälligkeit{(issues == 1 ? "" : "en")} auf deinem PC" : "Auf deinem PC ist nichts Auffälliges";
                HeadlineSub.Text = hadText
                    ? "Im eingefügten Text wurde kein FiveM-Absturz erkannt. Kopiere den Text direkt aus dem Crash-Fenster (Strg+C) oder lade den Dump."
                    : "Kein Crash-Text und kein Dump angegeben – geprüft wurde nur dein PC.";
            }

            FactChips.Children.Clear();
            AddChip("Signatur", f.Signature);
            AddChip("Crash-Name", f.LegacyHash);
            AddChip("Build", f.GameBuild?.ToString());
            AddChip("Report-ID", f.ReportId);
            if (f.ExceptionCode.HasValue)
                AddChip("Ausnahme", ExceptionCatalog.Codes.TryGetValue(f.ExceptionCode.Value, out var ex) ? ex.Name : $"0x{f.ExceptionCode.Value:X8}");
            if (f.Uptime.HasValue) AddChip("Lief", Analyzer.FormatDuration(f.Uptime.Value));
            if (f.OtherDumpCount > 0) AddChip("Dumps", $"{f.OtherDumpCount + 1} verglichen");

            var vms = r.Diagnoses.Select((d, i) => new DiagnosisVm(d, i, MakeFixVm)).ToList();
            DiagnosisList.ItemsSource = vms;
            NoDiagnosis.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DiagnosisTab.Header = $"Diagnose ({vms.Count})";
            Tabs.SelectedIndex = 0;

            BuildDetails();
            SystemList.ItemsSource = ReportWriter.SystemFields(_snapshot).Select(kv => new KeyValueVm { Key = kv.Key, Value = kv.Value }).ToList();
            CollectErrors.Visibility = _snapshot.CollectErrors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CollectErrors.Text = "Nicht messbar: " + string.Join("; ", _snapshot.CollectErrors);
            ReportText.Text = ReportWriter.Markdown(r, AppLog.Version, Environment.UserName);
        }

        void AddChip(string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var tb = new TextBlock { FontSize = 12 };
            tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = (Brush)FindResource("FaintBrush") });
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontFamily = (FontFamily)FindResource("MonoFont") });
            var chip = new Border
            {
                Background = (Brush)FindResource("Surface2Brush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 6), Child = tb,
                Cursor = Cursors.Hand, ToolTip = "Klicken zum Kopieren"
            };
            chip.MouseLeftButtonUp += (s, e) => { if (CopyText(value)) ShowToast($"{label} kopiert"); };
            FactChips.Children.Add(chip);
        }

        void BuildDetails()
        {
            var f = _report.Facts;
            DetailsPanel.Children.Clear();

            var crash = ReportWriter.CrashFields(f).Select(kv => new KeyValueVm { Key = kv.Key, Value = kv.Value }).ToList();
            if (f.Sources.Count > 0) crash.Insert(0, new KeyValueVm { Key = "Quellen", Value = string.Join("\n", f.Sources) });
            if (crash.Count > 0) AddSection("Absturz", KeyValues(crash));

            if (f.StackModules.Count > 0)
                AddSection("Module auf dem Stack", Mono(string.Join("  →  ", f.StackModules)),
                    "Adressen auf dem Stack des abgestürzten Threads, die in diese Module zeigen – in Reihenfolge des Auftretens. Ein Hinweis, kein exakter Stacktrace.");
            if (f.StackTraceLines.Count > 0) AddSection("Stacktrace aus dem Dialog", Mono(string.Join("\n", f.StackTraceLines)));

            var third = ReportWriter.ThirdPartyModules(f, _snapshot);
            if (third.Count > 0)
                AddSection("Fremdmodule im Spielprozess", Mono(string.Join("\n", third)),
                    "DLLs außerhalb von Windows, FiveM und GTA V. Hier stecken Overlays, Aufnahme-Tools und Mods.");
            if (f.LoadedModules.Count > 0)
                AddSection($"Alle geladenen Module ({f.LoadedModules.Count})",
                    Mono(string.Join("\n", f.LoadedModules.Select(m => $"{m.Name,-36} {m.FileVersion,-16} {ReportWriter.Anonymize(m.Path, Environment.UserName)}")), 220));

            if (f.Log != null)
            {
                if (f.Log.ScriptErrors.Count > 0) AddSection($"Skriptfehler im Log ({f.Log.ScriptErrors.Count})", Mono(string.Join("\n\n", f.Log.ScriptErrors), 220));
                if (f.Log.ErrorLines.Count > 0) AddSection("Fehlerzeilen im Log", Mono(string.Join("\n", f.Log.ErrorLines), 220));
                if (f.Log.TailLines.Count > 0) AddSection("Letzte Zeilen vor dem Absturz", Mono(string.Join("\n", f.Log.TailLines), 260));
            }
            if (f.Crashometry.Count > 0)
                AddSection("crashometry", KeyValues(f.Crashometry.Select(kv => new KeyValueVm { Key = kv.Key, Value = kv.Value }).ToList()));
            if (!string.IsNullOrEmpty(f.CefConsoleTail)) AddSection("cef_console.txt (Ende)", Mono(f.CefConsoleTail, 220));
            if (f.Warnings.Count > 0) AddSection("Hinweise beim Einlesen", Mono(string.Join("\n", f.Warnings)));

            if (DetailsPanel.Children.Count == 0)
                DetailsPanel.Children.Add(new TextBlock { Text = "Keine Absturzdaten – füge den Crash-Text ein oder lade einen Dump.", Style = (Style)FindResource("Muted"), Margin = new Thickness(4, 8, 0, 0) });
        }

        void AddSection(string title, UIElement content, string note = null)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H2") });
            if (note != null) panel.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
            if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(content);
            DetailsPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 10), Child = panel });
        }

        UIElement KeyValues(List<KeyValueVm> items) =>
            new ItemsControl { ItemsSource = items, ItemTemplate = (DataTemplate)FindResource("KeyValueTemplate") };

        UIElement Mono(string text, double maxHeight = double.PositiveInfinity) =>
            new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12,
                MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

        // ------------------------------------------------------------------ Reparaturen

        FixEnv CurrentEnv()
        {
            var env = new FixEnv
            {
                Paths = _paths ?? FiveMPaths.Detect(_fiveMOverride),
                Snapshot = _snapshot ?? new SystemSnapshot(),
                ReportMarkdown = ReportText.Text
            };
            env.InputFiles.AddRange(_files.Select(x => x.Path));
            return env;
        }

        FixVm MakeFixVm(string id)
        {
            var fix = FixRegistry.Get(id);
            if (fix == null) return null;
            var env = CurrentEnv();
            FixPreview preview;
            try { preview = fix.Preview(env); }
            catch (Exception ex) { AppLog.Write($"Vorschau {id}: {ex}"); preview = new FixPreview(); }
            var vm = new FixVm
            {
                Id = id,
                Label = fix.Label,
                NeedsAdmin = fix.NeedsAdmin(env) && !(_snapshot?.IsElevated ?? false),
                Tooltip = preview.CanRun ? string.Join("\n", preview.Lines.Take(4)) : preview.Reason
            };
            vm.Command = new RelayCommand(async p => await RunFixAsync((FixVm)p));
            if (!_fixVms.TryGetValue(id, out var list)) _fixVms[id] = list = new List<FixVm>();
            list.Add(vm);
            return vm;
        }

        async Task RunFixAsync(FixVm vm)
        {
            var fix = FixRegistry.Get(vm.Id);
            var env = CurrentEnv();
            var group = _fixVms.TryGetValue(vm.Id, out var g) ? g : new List<FixVm> { vm };

            FixPreview preview;
            try { preview = fix.Preview(env); }
            catch (Exception ex) { preview = new FixPreview { CanRun = false, Reason = "Vorschau fehlgeschlagen: " + ex.Message }; }
            if (!preview.CanRun)
            {
                foreach (var x in group) { x.Status = preview.Reason; x.StatusBrush = (Brush)FindResource("MutedBrush"); }
                return;
            }
            if (preview.Confirm && !ConfirmWindow.Ask(fix.Label, preview.Lines, preview.Undo, vm.NeedsAdmin)) return;

            foreach (var x in group) { x.IsBusy = true; x.Status = "Läuft …"; x.StatusBrush = (Brush)FindResource("MutedBrush"); }
            var result = await FixRunner.RunAsync(fix, env);
            foreach (var x in group)
            {
                x.IsBusy = false;
                x.Status = (result.Ok ? "Erledigt: " : "Nicht erledigt: ") + result.Message;
                x.StatusBrush = (Brush)FindResource(result.Ok ? "SuccessBrush" : "CriticalBrush");
            }
        }

        async void SupportZip_Click(object sender, RoutedEventArgs e)
        {
            var vm = MakeFixVm(FixIds.SupportZip);
            await RunFixAsync(vm);
            ShowToast(vm.Status);
        }

        // ------------------------------------------------------------------ Bericht, Ordner, Admin

        void CopyDiscord_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null) return;
            if (CopyText(ReportWriter.Discord(_report, Environment.UserName))) ShowToast("Kurzfassung kopiert – in Discord mit Strg+V einfügen.");
        }

        void CopyMarkdown_Click(object sender, RoutedEventArgs e)
        {
            if (CopyText(ReportText.Text)) ShowToast("Bericht kopiert (Markdown).");
        }

        bool CopyText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text ?? ""); return true; }
                catch (COMException) { System.Threading.Thread.Sleep(60); }
            }
            ShowToast("Die Zwischenablage ist belegt. Bitte erneut versuchen.");
            return false;
        }

        async void ChooseFiveM_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "FiveM.exe oder CitizenFX.ini im FiveM-Ordner wählen",
                Filter = "FiveM.exe oder CitizenFX.ini|FiveM.exe;CitizenFX.ini|Alle Dateien|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            var dir = Path.GetDirectoryName(dlg.FileName);
            if (dlg.FileName.EndsWith("FiveM.exe", StringComparison.OrdinalIgnoreCase)) dir = Path.Combine(dir ?? "", "FiveM.app");
            if (!Directory.Exists(dir)) { ShowToast("Dort liegt kein FiveM.app-Ordner."); return; }
            _fiveMOverride = dir;
            SaveSettings();
            ShowToast("FiveM-Ordner gespeichert.");
            if (_report != null) await RunAnalysisAsync();
        }

        void OpenCrashes_Click(object sender, RoutedEventArgs e)
        {
            var p = _paths ?? FiveMPaths.Detect(_fiveMOverride);
            if (p.Found && Directory.Exists(p.CrashesDir)) Process.Start("explorer.exe", $"\"{p.CrashesDir}\"")?.Dispose();
            else ShowToast("Crash-Ordner nicht gefunden.");
        }

        void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(AppLog.Folder);
            Process.Start("explorer.exe", $"\"{AppLog.Folder}\"")?.Dispose();
        }

        void RestartAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (FixRunner.RestartElevated(CrashText.Text, _files.Select(f => f.Path))) Application.Current.Shutdown();
            else ShowToast("Abgebrochen – das Tool läuft weiter ohne Admin-Rechte.");
        }

        void ShowToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        void LoadSettings()
        {
            _settings = AppSettings.Load();
            _fiveMOverride = _settings.FiveMDir;
        }

        void SaveSettings()
        {
            _settings.FiveMDir = _fiveMOverride;
            _settings.Save();
        }

        // ------------------------------------------------------------------ Auto-Update

        /// <summary>Prüft im Hintergrund. Beim Start mit „automatisch installieren“ läuft das Update ohne Rückfrage.</summary>
        async Task<UpdateCheckResult> CheckForUpdateAsync(bool manual, bool startup)
        {
            if (_updateFlowActive) return null;
            var feed = _feed;
            var settings = _settings;
            var result = await Task.Run(() => Updater.Check(feed, settings, manual));
            _settings.Save();
            if (result.Status != UpdateStatus.Available) return result;

            var version = result.Offer.Version.ToString();
            if (!manual && _dismissedThisSession.Contains(version)) return result;
            if (startup && _settings.UpdateAutoInstall && !_busy && !Updater.IsDevBuild(Updater.CurrentExePath))
            {
                ShowUpdateBanner(result.Offer);
                await InstallUpdateAsync();
            }
            else
            {
                ShowUpdateBanner(result.Offer);
            }
            return result;
        }

        void ShowUpdateBanner(UpdateOffer offer)
        {
            _offer = offer;
            UpdateTitle.Text = $"Update auf Version {offer.Version} verfügbar";
            UpdateNotes.Text = UpdateLogic.NotesPreview(offer.Release.Notes, 1);
            UpdateNotes.Foreground = (Brush)FindResource("MutedBrush");
            UpdateNowButton.IsEnabled = true;
            UpdateLaterButton.IsEnabled = UpdateSkipButton.IsEnabled = true;
            UpdateBanner.Visibility = Visibility.Visible;
        }

        async Task InstallUpdateAsync()
        {
            if (_offer == null || _updateFlowActive) return;
            var exe = Updater.CurrentExePath;
            if (Updater.IsDevBuild(exe))
            {
                UpdateNotes.Text = "Entwickler-Build: Hier tauscht sich das Tool nicht selbst aus. Lade das Update über „Was ist neu?“.";
                return;
            }
            if (_busy)
            {
                UpdateNotes.Text = "Bitte warte, bis die Analyse fertig ist.";
                return;
            }
            _updateFlowActive = true;
            UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = UpdateSkipButton.IsEnabled = false;
            var offer = _offer;
            var feed = _feed;
            try
            {
                var verified = await Task.Run(() => Updater.DownloadAndVerify(feed, offer, exe,
                    msg => Dispatcher.BeginInvoke(new Action(() => UpdateNotes.Text = msg)), System.Threading.CancellationToken.None));
                UpdateNotes.Text = "Starte neu …";
                Updater.Swap(exe, verified);
                Updater.Relaunch(exe, Updater.CurrentVersion);
                Application.Current.Shutdown();
            }
            catch (Exception ex) when (ex is UpdateException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Net.WebException)
            {
                AppLog.Write("Update fehlgeschlagen: " + ex);
                UpdateNotes.Text = "Update fehlgeschlagen: " + ex.Message;
                UpdateNotes.Foreground = (Brush)FindResource("CriticalBrush");
                UpdateNowButton.IsEnabled = UpdateLaterButton.IsEnabled = UpdateSkipButton.IsEnabled = true;
            }
            finally
            {
                _updateFlowActive = false;
            }
        }

        async void UpdateNow_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

        void UpdateLater_Click(object sender, RoutedEventArgs e)
        {
            if (_offer != null) _dismissedThisSession.Add(_offer.Version.ToString());
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        void UpdateSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_offer != null) { _settings.SkippedVersion = _offer.Version.ToString(); _settings.Save(); }
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        void UpdatePage_Click(object sender, RoutedEventArgs e)
        {
            var url = _offer?.Release?.PageUrl;
            if (string.IsNullOrEmpty(url) || !UpdateLogic.IsAllowedHost(new Uri(url))) url = GitHubFeed.ReleasesPage;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }

        void UpdateSettings_Click(object sender, RoutedEventArgs e)
        {
            UpdateSettingsWindow.Show(_settings, async () =>
            {
                var r = await CheckForUpdateAsync(manual: true, startup: false);
                return r?.Message ?? "Ein Update läuft bereits.";
            });
            _settings.Save();
            if (_settings.UpdateCheck) _updateTimer.Start(); else _updateTimer.Stop();
        }

        bool HasArg(string name) => _args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        string ArgValue(string name)
        {
            var i = Array.FindIndex(_args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
        }

        void SaveScreenshot(string path)
        {
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle((Brush)FindResource("BgBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        // ------------------------------------------------------------------ dunkle Titelleiste (Windows 10 20H1+ / 11)

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void UseDarkTitleBar(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int caption = 0x0014100E; // #0E1014 als COLORREF (BGR), nur Windows 11
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
