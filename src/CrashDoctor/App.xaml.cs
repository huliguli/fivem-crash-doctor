using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CrashDoctor.Platform;

namespace CrashDoctor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args ?? new string[0];

            // Erhöhter Hilfsprozess: genau eine Reparatur ausführen, kein Fenster
            int fixIdx = Array.IndexOf(args, "--run-fix");
            if (fixIdx >= 0 && fixIdx + 1 < args.Length)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                int code;
                try { code = FixRunner.RunFromRequest(args[fixIdx + 1]); }
                catch (Exception ex) { AppLog.Write("Admin-Reparatur abgestürzt: " + ex); code = 9; }
                Shutdown(code);
                return;
            }

            // Ohne Fenster: FiveMCrashDoctor.exe --report bericht.md [--text crash.txt] [dump.dmp …]
            int reportIdx = Array.IndexOf(args, "--report");
            if (reportIdx >= 0 && reportIdx + 1 < args.Length)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(RunHeadlessReport(args, args[reportIdx + 1]));
                return;
            }

            // Ohne Fenster aktualisieren: FiveMCrashDoctor.exe --update [--update-feed <ordner>]
            if (args.Contains("--update"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(RunHeadlessUpdate(args));
                return;
            }

            AppLog.Start(args.Contains("--screenshot") ? "Bildschirmfoto" : "Oberfläche");
            try { Updater.CleanupLeftovers(Updater.CurrentExePath); } catch (Exception ex) { AppLog.Write("Aufräumen nach Update: " + ex.Message); }
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => AppLog.Write("Unbehandelter Fehler: " + ev.ExceptionObject);

            var window = new MainWindow(args);
            MainWindow = window;
            window.Show();
        }

        /// <summary>0 = aktualisiert, 1 = schon aktuell, 2 = Fehler.</summary>
        static int RunHeadlessUpdate(string[] args)
        {
            AppLog.Start("Update ohne Fenster");
            try
            {
                var feed = UpdateFeedFromArgs(args);
                var settings = AppSettings.Load();
                var result = Updater.Check(feed, settings, manual: true);
                settings.Save();
                AppLog.Write("Update-Prüfung: " + result.Status + " – " + result.Message);
                if (result.Status != UpdateStatus.Available) return result.Status == UpdateStatus.UpToDate ? 1 : 2;
                var exe = Updater.CurrentExePath;
                var verified = Updater.DownloadAndVerify(feed, result.Offer, exe, s => { }, System.Threading.CancellationToken.None);
                Updater.Swap(exe, verified);
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Write("Update fehlgeschlagen: " + ex.Message);
                return 2;
            }
        }

        public static IUpdateFeed UpdateFeedFromArgs(string[] args)
        {
            int i = Array.IndexOf(args, "--update-feed");
            return i >= 0 && i + 1 < args.Length ? (IUpdateFeed)new FolderFeed(args[i + 1]) : new GitHubFeed();
        }

        static int RunHeadlessReport(string[] args, string output)
        {
            AppLog.Start("Bericht ohne Fenster");
            try
            {
                string text = null;
                int textIdx = Array.IndexOf(args, "--text");
                if (textIdx >= 0 && textIdx + 1 < args.Length) text = System.IO.File.ReadAllText(args[textIdx + 1]);
                var skip = new System.Collections.Generic.HashSet<int> { Array.IndexOf(args, "--report") + 1 };
                if (textIdx >= 0) skip.Add(textIdx + 1);
                var files = args.Where((a, i) => !a.StartsWith("--") && !skip.Contains(i)).ToList();
                if (args.Contains("--latest"))
                    files.AddRange(InputLoader.FindLatestCrash(FiveMPaths.Detect(), out _));
                var paths = FiveMPaths.Detect();
                var snapshot = SystemCollector.Collect(paths);
                var facts = InputLoader.Load(text, files);
                var report = Core.Analyzer.Analyze(facts, snapshot);
                System.IO.File.WriteAllText(output, Core.ReportWriter.Markdown(report, AppLog.Version, Environment.UserName), new System.Text.UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Write("Bericht fehlgeschlagen: " + ex);
                try { System.IO.File.WriteAllText(output, "Fehler: " + ex.Message); } catch (Exception) { }
                return 1;
            }
        }

        void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Write("Fehler in der Oberfläche: " + e.Exception);
            MessageBox.Show(
                "Im Tool ist ein Fehler aufgetreten:\n\n" + e.Exception.Message +
                "\n\nDas Protokoll liegt unter:\n" + AppLog.Folder,
                "FiveM Crash Doctor", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
