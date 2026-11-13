using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CrashDoctor.Core;
using CrashDoctor.Platform;

namespace CrashDoctor.UI
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action<object> _run;
        readonly Func<object, bool> _can;
        public RelayCommand(Action<object> run, Func<object, bool> can = null) { _run = run; _can = can; }
        public event EventHandler CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public bool CanExecute(object p) => _can == null || _can(p);
        public void Execute(object p) => _run(p);
    }

    static class Brushes2
    {
        public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
    }

    public sealed class StepVm
    {
        public string Number { get; set; }
        public string Text { get; set; }
    }

    public sealed class FixVm : Observable
    {
        string _status;
        Brush _statusBrush;
        bool _busy;

        public string Id { get; set; }
        public string Label { get; set; }
        public bool NeedsAdmin { get; set; }
        public Visibility AdminVisibility => NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
        public string Tooltip { get; set; }
        public ICommand Command { get; set; }

        public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusVisibility)); } }
        public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
        public Visibility StatusVisibility => string.IsNullOrEmpty(Status) ? Visibility.Collapsed : Visibility.Visible;
        public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(IsIdle)); } }
        public bool IsIdle => !_busy;
    }

    public sealed class DiagnosisVm : Observable
    {
        bool _expanded;

        public DiagnosisVm(Diagnosis d, int index, Func<string, FixVm> fixFactory)
        {
            Model = d;
            Index = index;
            Title = d.Title;
            Explanation = d.Explanation;
            SeverityText = ReportWriter.SeverityText(d.Severity);
            ConfidenceText = "Wahrscheinlichkeit " + ReportWriter.ConfidenceText(d.Confidence);
            EvidenceText = ReportWriter.EvidenceText(d.Evidence);
            EvidenceTooltip = d.Evidence == Evidence.Belegt
                ? "Steht im FiveM-Quelltext oder der offiziellen Doku." + (string.IsNullOrEmpty(d.SourceNote) ? "" : " Quelle: " + d.SourceNote)
                : d.Evidence == Evidence.Praxis
                    ? "Verbreiteter Erfahrungswert aus der FiveM-Community, nicht offiziell belegt."
                    : "Schlussfolgerung des Tools. Vor größeren Schritten selbst prüfen.";
            var key = d.Severity == Severity.Critical ? "Critical" : d.Severity == Severity.Warning ? "Warning" : d.Severity == Severity.Hint ? "Hint" : "Info";
            SeverityBrush = Brushes2.Get(key + "Brush");
            SeveritySoftBrush = Brushes2.Get(key + "SoftBrush");
            Steps = d.Steps.Select((s, i) => new StepVm { Number = (i + 1) + ".", Text = s }).ToList();
            Fixes = d.FixIds.Select(fixFactory).Where(f => f != null).ToList();
            MatchedText = d.MatchedBy.Count == 0 ? null : "Erkannt an: " + string.Join(" · ", d.MatchedBy);
            _expanded = d.Severity >= Severity.Warning || index < 2;
            Toggle = new RelayCommand(_ => IsExpanded = !IsExpanded);
        }

        public Diagnosis Model { get; }
        public int Index { get; }
        public string Title { get; }
        public string Explanation { get; }
        public string SeverityText { get; }
        public string ConfidenceText { get; }
        public string EvidenceText { get; }
        public string EvidenceTooltip { get; }
        public Brush SeverityBrush { get; }
        public Brush SeveritySoftBrush { get; }
        public List<StepVm> Steps { get; }
        public List<FixVm> Fixes { get; }
        public string MatchedText { get; }
        public Visibility MatchedVisibility => MatchedText == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StepsVisibility => Steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FixesVisibility => Fixes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        public ICommand Toggle { get; }

        public bool IsExpanded
        {
            get => _expanded;
            set { if (Set(ref _expanded, value)) { Raise(nameof(BodyVisibility)); Raise(nameof(Chevron)); } }
        }
        public Visibility BodyVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
        public string Chevron => _expanded ? "" : "";
    }

    public sealed class KeyValueVm
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public sealed class InputFileVm
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Info
        {
            get
            {
                try
                {
                    var fi = new FileInfo(Path);
                    var kind = fi.Extension.ToLowerInvariant() == ".dmp" ? "Crash-Dump" : fi.Extension.ToLowerInvariant() == ".zip" ? "Crash-Zip" : "Text/Log";
                    var size = fi.Length >= 1048576 ? $"{fi.Length / 1048576.0:0.0} MB" : $"{Math.Max(1, fi.Length / 1024)} KB";
                    return $"{kind} · {size} · {fi.LastWriteTime:dd.MM.yyyy HH:mm}";
                }
                catch (Exception) { return ""; }
            }
        }
        public ICommand Remove { get; set; }
    }
}
