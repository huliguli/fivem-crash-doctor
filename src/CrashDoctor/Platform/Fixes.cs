using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Text;
using CrashDoctor.Core;

namespace CrashDoctor.Platform
{
    /// <summary>Alles, was eine Reparatur wissen muss. Wird für die erhöhte Ausführung in eine Anfrage-Datei geschrieben.</summary>
    public sealed class FixEnv
    {
        public FiveMPaths Paths { get; set; }
        public SystemSnapshot Snapshot { get; set; } = new SystemSnapshot();
        public string ReportMarkdown { get; set; }
        public List<string> InputFiles { get; } = new List<string>();
        public string UserName { get; set; } = Environment.UserName;
    }

    public sealed class FixPreview
    {
        public bool CanRun = true;
        public string Reason;                       // warum nicht
        public List<string> Lines = new List<string>();
        public string Undo;                         // wie rückgängig machen, null = nicht nötig/möglich
        public bool Confirm = true;                 // Rückfrage nötig?
    }

    public sealed class FixResult
    {
        public bool Ok;
        public string Message;
        public static FixResult Success(string m) => new FixResult { Ok = true, Message = m };
        public static FixResult Fail(string m) => new FixResult { Ok = false, Message = m };
    }

    public abstract class FixAction
    {
        public abstract string Id { get; }
        public abstract string Label { get; }
        public virtual bool NeedsFiveMClosed => false;
        public virtual bool NeedsAdmin(FixEnv env) => false;
        public abstract FixPreview Preview(FixEnv env);
        public abstract FixResult Run(FixEnv env);

        protected static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes / 1048576.0:0} MB";
        protected static string Stamp => DateTime.Now.ToString("yyyyMMdd-HHmmss");

        protected static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
    }

    public static class FixRegistry
    {
        public static readonly List<FixAction> All = new List<FixAction>
        {
            new ClearCacheFix(), new DisablePluginsFix(), new MoveGtaModsFix(), new ResetGraphicsFix(), new ClearShaderCacheFix(),
            new CloseOverlaysFix(), new VerifyGameFix(), new PagefileAutoFix(), new SystemFilesFix(), new GpuDriverFix(),
            new VcRedistFix(), new TestServerFix(), new UpdateChannelFix(), new SupportZipFix()
        };

        public static FixAction Get(string id) => All.FirstOrDefault(f => f.Id == id);
    }

    // ===================================================================== Reparaturen

    sealed class ClearCacheFix : FixAction
    {
        public override string Id => FixIds.ClearCache;
        public override string Label => "FiveM-Cache leeren";
        public override bool NeedsFiveMClosed => true;

        public override FixPreview Preview(FixEnv env)
        {
            var p = new FixPreview();
            if (!env.Paths.Found) return new FixPreview { CanRun = false, Reason = "FiveM wurde nicht gefunden. Wähle den FiveM-Ordner unter „System“." };
            var dirs = env.Paths.CacheDirs.Where(Directory.Exists).ToList();
            if (dirs.Count == 0) return new FixPreview { CanRun = false, Reason = "Der Cache ist bereits leer." };
            foreach (var d in dirs) p.Lines.Add($"Löscht {d} ({Gb(SystemCollector.DirectorySize(d, TimeSpan.FromSeconds(3)))})");
            p.Lines.Add("Bleibt erhalten: data\\game-storage (heruntergeladene Spieldateien) und deine Einstellungen.");
            p.Undo = "Nicht nötig: FiveM lädt den Cache beim nächsten Verbinden neu. Der erste Start dauert dann länger.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var errors = 0;
            foreach (var d in env.Paths.CacheDirs.Where(Directory.Exists))
                errors += DeleteTree(d);
            return errors == 0
                ? FixResult.Success("Cache geleert. Starte FiveM neu und verbinde dich mit dem Server.")
                : FixResult.Fail($"Cache größtenteils geleert, {errors} Datei(en) waren gesperrt. Ist FiveM wirklich komplett beendet?");
        }

        internal static int DeleteTree(string dir)
        {
            int errors = 0;
            foreach (var f in SafeEnum(() => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                catch (IOException) { errors++; }
                catch (UnauthorizedAccessException) { errors++; }
            }
            foreach (var d in SafeEnum(() => Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)).OrderByDescending(x => x.Length))
            {
                try { Directory.Delete(d, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(dir, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return errors;
        }

        static IEnumerable<string> SafeEnum(Func<IEnumerable<string>> f)
        {
            try { return f().ToList(); }
            catch (IOException) { return Enumerable.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }
    }

    sealed class DisablePluginsFix : FixAction
    {
        public override string Id => FixIds.DisablePlugins;
        public override string Label => "Plugins deaktivieren";
        public override bool NeedsFiveMClosed => true;

        public override FixPreview Preview(FixEnv env)
        {
            if (!env.Paths.Found) return new FixPreview { CanRun = false, Reason = "FiveM wurde nicht gefunden." };
            var items = SystemCollector.ListPlugins(env.Paths.PluginsDir);
            if (items.Count == 0) return new FixPreview { CanRun = false, Reason = "Der Ordner „plugins“ ist leer oder existiert nicht – es gibt nichts zu deaktivieren." };
            var p = new FixPreview();
            p.Lines.Add($"Benennt {env.Paths.PluginsDir} um in „plugins.deaktiviert-…“. Nichts wird gelöscht.");
            p.Lines.AddRange(items.Take(12).Select(i => "• " + i));
            p.Undo = "Ordner wieder in „plugins“ umbenennen.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var target = env.Paths.PluginsDir + ".deaktiviert-" + Stamp;
            Directory.Move(env.Paths.PluginsDir, target);
            Directory.CreateDirectory(env.Paths.PluginsDir);
            return FixResult.Success($"Plugins deaktiviert. Sie liegen jetzt in {target}.");
        }
    }

    sealed class MoveGtaModsFix : FixAction
    {
        public override string Id => FixIds.MoveGtaMods;
        public override string Label => "Mods aus GTA-Ordner sichern";

        public override bool NeedsAdmin(FixEnv env) => !string.IsNullOrEmpty(env.Paths.GtaDir) && !CanWrite(env.Paths.GtaDir);

        public override FixPreview Preview(FixEnv env)
        {
            if (string.IsNullOrEmpty(env.Paths.GtaDir)) return new FixPreview { CanRun = false, Reason = "Der GTA-V-Ordner wurde nicht gefunden (CitizenFX.ini, Eintrag IVPath)." };
            var mods = SystemCollector.ListGtaMods(env.Paths.GtaDir);
            if (mods.Count == 0) return new FixPreview { CanRun = false, Reason = "Im GTA-V-Ordner liegen keine bekannten Mod-Dateien." };
            var p = new FixPreview();
            p.Lines.Add($"Verschiebt aus {env.Paths.GtaDir} in den Unterordner „_CrashDoctor-Sicherung-…“:");
            p.Lines.AddRange(mods.Select(m => "• " + m));
            p.Undo = "Dateien aus dem Sicherungsordner zurück in den GTA-V-Ordner verschieben.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var gta = env.Paths.GtaDir;
            var backup = Path.Combine(gta, "_CrashDoctor-Sicherung-" + Stamp);
            Directory.CreateDirectory(backup);
            int moved = 0;
            foreach (var m in SystemCollector.ListGtaMods(gta))
            {
                var src = Path.Combine(gta, m.TrimEnd('\\'));
                var dst = Path.Combine(backup, m.TrimEnd('\\'));
                if (m.EndsWith("\\")) Directory.Move(src, dst); else File.Move(src, dst);
                moved++;
            }
            return FixResult.Success($"{moved} Mod-Datei(en) gesichert nach {backup}.");
        }

        static bool CanWrite(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".crashdoctor-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }
    }

    sealed class ResetGraphicsFix : FixAction
    {
        public override string Id => FixIds.ResetGraphics;
        public override string Label => "Grafikeinstellungen zurücksetzen";
        public override bool NeedsFiveMClosed => true;

        static IEnumerable<string> Files => new[] { FiveMPaths.FiveMSettingsXml, FiveMPaths.GtaSettingsXml }.Where(File.Exists);

        public override FixPreview Preview(FixEnv env)
        {
            var files = Files.ToList();
            if (files.Count == 0) return new FixPreview { CanRun = false, Reason = "Keine Einstellungsdatei gefunden – das Spiel nutzt bereits die Standardwerte." };
            var p = new FixPreview();
            foreach (var f in files) p.Lines.Add($"Benennt {f} um in „….bak-…“");
            p.Lines.Add("Beim nächsten Start erzeugt das Spiel neue Standard-Einstellungen.");
            p.Undo = "Die .bak-Datei wieder in settings.xml bzw. gta5_settings.xml umbenennen.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var done = new List<string>();
            foreach (var f in Files.ToList())
            {
                var bak = f + ".bak-" + Stamp;
                File.Move(f, bak);
                done.Add(Path.GetFileName(f));
            }
            return FixResult.Success("Zurückgesetzt: " + string.Join(", ", done) + ". Stelle Auflösung und Bildschirm im Spiel neu ein.");
        }
    }

    sealed class ClearShaderCacheFix : FixAction
    {
        public override string Id => FixIds.ClearShaderCache;
        public override string Label => "Shader-Cache leeren";
        public override bool NeedsFiveMClosed => true;

        static IEnumerable<string> Dirs
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return new[]
                {
                    Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
                    Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"), Path.Combine(local, "AMD", "GLCache"),
                    Path.Combine(local, "D3DSCache")
                }.Where(Directory.Exists);
            }
        }

        public override FixPreview Preview(FixEnv env)
        {
            var dirs = Dirs.ToList();
            if (dirs.Count == 0) return new FixPreview { CanRun = false, Reason = "Kein Shader-Cache gefunden." };
            var p = new FixPreview();
            foreach (var d in dirs) p.Lines.Add($"Leert {d} ({Gb(SystemCollector.DirectorySize(d, TimeSpan.FromSeconds(2)))})");
            p.Lines.Add("Gesperrte Dateien (von laufenden Spielen) werden übersprungen.");
            p.Undo = "Nicht nötig: der Treiber baut den Cache neu auf. Die ersten Minuten im Spiel können ruckeln.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            int locked = 0;
            foreach (var d in Dirs.ToList())
            {
                locked += ClearCacheFix.DeleteTree(d);
                Directory.CreateDirectory(d);
            }
            return FixResult.Success(locked == 0 ? "Shader-Cache geleert." : $"Shader-Cache geleert, {locked} gesperrte Datei(en) übersprungen.");
        }
    }

    sealed class CloseOverlaysFix : FixAction
    {
        public override string Id => FixIds.CloseOverlays;
        public override string Label => "Overlay-Programme beenden";

        public override FixPreview Preview(FixEnv env)
        {
            var running = RunningEntries();
            var p = new FixPreview();
            var closable = running.Where(r => r.SafeToClose).ToList();
            foreach (var r in closable) p.Lines.Add($"Beendet {r.Product}");
            foreach (var r in running.Where(r => !r.SafeToClose)) p.Lines.Add($"Nicht automatisch: {r.Product} – {r.Hint}");
            p.Lines.Add("Overlays, die du selbst abschalten musst:");
            p.Lines.Add("• Discord: Einstellungen → Spiel-Overlay → aus");
            p.Lines.Add("• Steam: Einstellungen → Im Spiel → Steam-Overlay aus");
            p.Lines.Add("• NVIDIA: Alt+Z → Einstellungen → In-Game-Overlay aus");
            if (closable.Count == 0)
            {
                p.CanRun = false;
                p.Reason = "Gerade läuft keines der Programme, die das Tool beenden kann. Schalte die Overlays oben von Hand ab.";
            }
            p.Undo = "Programme einfach wieder starten.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var closed = new List<string>();
            var failed = new List<string>();
            foreach (var e in RunningEntries().Where(r => r.SafeToClose))
            {
                foreach (var p in Process.GetProcesses().Where(x => SafeName(x) == e.Process))
                {
                    try
                    {
                        if (!p.CloseMainWindow() || !p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); }
                        if (!closed.Contains(e.Product)) closed.Add(e.Product);
                    }
                    catch (Win32Exception) { failed.Add(e.Product); }
                    catch (InvalidOperationException) { }
                    finally { p.Dispose(); }
                }
            }
            var msg = closed.Count > 0 ? "Beendet: " + string.Join(", ", closed) + "." : "Nichts beendet.";
            if (failed.Count > 0) msg += " Nicht möglich (läuft mit Admin-Rechten): " + string.Join(", ", failed.Distinct()) + ".";
            return new FixResult { Ok = failed.Count == 0, Message = msg };
        }

        static List<ProcessCatalog.Entry> RunningEntries()
        {
            var names = new HashSet<string>(Process.GetProcesses().Select(SafeName));
            return ProcessCatalog.Entries.Where(e => names.Contains(e.Process)).ToList();
        }

        static string SafeName(Process p)
        {
            try { return p.ProcessName.ToLowerInvariant(); } catch (InvalidOperationException) { return ""; }
        }
    }

    sealed class VerifyGameFix : FixAction
    {
        public override string Id => FixIds.VerifyGame;
        public override string Label => "Spieldateien prüfen";

        public override FixPreview Preview(FixEnv env)
        {
            var p = new FixPreview { Confirm = false };
            switch (env.Paths.GtaPlatform)
            {
                case "Steam": p.Lines.Add("Startet in Steam die Prüfung von GTA V (steam://validate/271590)."); break;
                case "Epic": p.Lines.Add("Epic Games Launcher → Bibliothek → GTA V → ⋯ → Verwalten → Überprüfen."); break;
                default: p.Lines.Add("Rockstar Games Launcher → Einstellungen → Meine installierten Spiele → GTA V → Integrität überprüfen."); break;
            }
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            if (env.Paths.GtaPlatform == "Steam")
            {
                OpenUrl("steam://validate/271590");
                return FixResult.Success("Steam prüft jetzt die Spieldateien. Warte, bis die Prüfung fertig ist, bevor du FiveM startest.");
            }
            return FixResult.Success(Preview(env).Lines.First());
        }
    }

    sealed class PagefileAutoFix : FixAction
    {
        public override string Id => FixIds.PagefileAuto;
        public override string Label => "Auslagerungsdatei automatisch";
        public override bool NeedsAdmin(FixEnv env) => true;

        public override FixPreview Preview(FixEnv env)
        {
            if (env.Snapshot.PagefileAutomatic == true)
                return new FixPreview { CanRun = false, Reason = "Windows verwaltet die Auslagerungsdatei bereits automatisch." };
            var p = new FixPreview();
            p.Lines.Add("Stellt die Windows-Einstellung „Auslagerungsdateigröße für alle Laufwerke automatisch verwalten“ ein.");
            p.Lines.Add("Braucht Admin-Rechte (Windows fragt nach) und wirkt erst nach einem Neustart.");
            p.Undo = "Systemsteuerung → System → Erweiterte Systemeinstellungen → Leistung → Erweitert → Virtueller Arbeitsspeicher.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { EnablePrivileges = true });
            scope.Connect();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_ComputerSystem")))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject cs in results)
                    using (cs)
                    {
                        cs["AutomaticManagedPagefile"] = true;
                        cs.Put();
                    }
            }
            return FixResult.Success("Die Auslagerungsdatei wird jetzt automatisch verwaltet. Starte den PC neu, damit es wirkt.");
        }
    }

    sealed class SystemFilesFix : FixAction
    {
        public override string Id => FixIds.SystemFiles;
        public override string Label => "Windows-Dateien reparieren";

        public override FixPreview Preview(FixEnv env)
        {
            var p = new FixPreview();
            p.Lines.Add("Öffnet ein Admin-Konsolenfenster und führt nacheinander aus:");
            p.Lines.Add("• DISM /Online /Cleanup-Image /RestoreHealth (repariert den Windows-Komponentenspeicher, braucht Internet)");
            p.Lines.Add("• sfc /scannow (prüft und ersetzt beschädigte Systemdateien)");
            p.Lines.Add("Dauert 10 bis 30 Minuten. Das Fenster nicht schließen.");
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/k \"DISM /Online /Cleanup-Image /RestoreHealth & sfc /scannow & echo. & echo Fertig. Fenster kann geschlossen werden.\"")
                { UseShellExecute = true, Verb = "runas" };
                Process.Start(psi)?.Dispose();
                return FixResult.Success("Die Reparatur läuft im Konsolenfenster. Danach den PC neu starten.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return FixResult.Fail("Abgebrochen: die Admin-Abfrage wurde abgelehnt.");
            }
        }
    }

    sealed class GpuDriverFix : FixAction
    {
        public override string Id => FixIds.GpuDriver;
        public override string Label => "Grafiktreiber holen";

        static string Url(string vendor)
        {
            switch (vendor)
            {
                case "NVIDIA": return "https://www.nvidia.com/de-de/drivers/";
                case "AMD": return "https://www.amd.com/de/support/download/drivers.html";
                case "Intel": return "https://www.intel.de/content/www/de/de/support/detect.html";
                default: return null;
            }
        }

        public override FixPreview Preview(FixEnv env)
        {
            var gpu = env.Snapshot.Gpus.FirstOrDefault(g => Url(g.Vendor) != null);
            var p = new FixPreview { Confirm = false };
            if (gpu == null)
            {
                p.Lines.Add("Öffnet die Übersicht der Treiberseiten (Grafikkarte nicht erkannt).");
                return p;
            }
            p.Lines.Add($"Öffnet die offizielle Treiberseite von {gpu.Vendor} für {gpu.Name}.");
            p.Lines.Add($"Installiert: {gpu.FriendlyDriverVersion ?? gpu.DriverVersion}" + (gpu.DriverDate.HasValue ? $" vom {gpu.DriverDate.Value:dd.MM.yyyy}" : ""));
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var gpu = env.Snapshot.Gpus.FirstOrDefault(g => Url(g.Vendor) != null);
            var url = gpu != null ? Url(gpu.Vendor) : "https://www.nvidia.com/de-de/drivers/";
            OpenUrl(url);
            return FixResult.Success("Treiberseite geöffnet. Nach der Installation den PC neu starten.");
        }
    }

    sealed class VcRedistFix : FixAction
    {
        public override string Id => FixIds.VcRedist;
        public override string Label => "Visual C++ Runtime holen";

        public override FixPreview Preview(FixEnv env)
        {
            var p = new FixPreview { Confirm = false };
            p.Lines.Add("Lädt vc_redist.x64.exe direkt von Microsoft (aka.ms/vs/17/release/vc_redist.x64.exe) im Browser herunter.");
            p.Lines.Add("Danach die Datei ausführen und „Reparieren“ bzw. „Installieren“ wählen.");
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            OpenUrl("https://aka.ms/vs/17/release/vc_redist.x64.exe");
            return FixResult.Success("Download gestartet. Datei ausführen, dann den PC neu starten.");
        }
    }

    sealed class TestServerFix : FixAction
    {
        public override string Id => FixIds.TestServer;
        public override string Label => "Cfx-Testserver öffnen";

        public override FixPreview Preview(FixEnv env)
        {
            var p = new FixPreview { Confirm = false };
            p.Lines.Add("Öffnet https://cfx.re/join/y4lg95 – den offiziellen Testserver von Cfx.re ohne eigene Mods.");
            p.Lines.Add("Stürzt es dort nicht ab, liegt die Ursache wahrscheinlich am anderen Server.");
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            OpenUrl("https://cfx.re/join/y4lg95");
            return FixResult.Success("Testserver-Seite geöffnet. Dort auf „Connect“ klicken.");
        }
    }

    sealed class UpdateChannelFix : FixAction
    {
        public override string Id => FixIds.UpdateChannel;
        public override string Label => "Release-Kanal einstellen";
        public override bool NeedsFiveMClosed => true;

        public override FixPreview Preview(FixEnv env)
        {
            if (!env.Paths.Found || !File.Exists(env.Paths.CitizenFxIni)) return new FixPreview { CanRun = false, Reason = "CitizenFX.ini nicht gefunden." };
            var current = FiveMPaths.IniRead(env.Paths.CitizenFxIni, "Game", "UpdateChannel") ?? "production";
            if (current == "production" || current == "prod") return new FixPreview { CanRun = false, Reason = "FiveM nutzt bereits den Release-Kanal." };
            var p = new FixPreview();
            p.Lines.Add($"Setzt in {env.Paths.CitizenFxIni} unter [Game] UpdateChannel von „{current}“ auf „production“.");
            p.Lines.Add("Beim nächsten Start lädt FiveM die normale Version.");
            p.Undo = $"In den FiveM-Einstellungen wieder „{current}“ wählen.";
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            return FiveMPaths.IniWrite(env.Paths.CitizenFxIni, "Game", "UpdateChannel", "production")
                ? FixResult.Success("Update-Kanal steht auf „production“. FiveM neu starten.")
                : FixResult.Fail("Konnte CitizenFX.ini nicht schreiben.");
        }
    }

    sealed class SupportZipFix : FixAction
    {
        public override string Id => FixIds.SupportZip;
        public override string Label => "Support-Paket erstellen";

        public override FixPreview Preview(FixEnv env)
        {
            var p = new FixPreview();
            p.Lines.Add("Legt auf dem Desktop eine ZIP-Datei an mit:");
            p.Lines.Add("• bericht.md – diese Analyse, ohne deinen Windows-Benutzernamen");
            foreach (var f in env.InputFiles.Where(File.Exists).Take(6)) p.Lines.Add("• " + Path.GetFileName(f));
            p.Lines.Add("Achtung: Dumps und Logs können Server- und Spielernamen und Teile des Spielspeichers enthalten. Teile das Paket nur mit Leuten, denen du vertraust (Server-Team, Cfx-Forum).");
            return p;
        }

        public override FixResult Run(FixEnv env)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var target = Path.Combine(desktop, $"FiveM-Crash-Support-{Stamp}.zip");
            using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                var report = zip.CreateEntry("bericht.md");
                using (var w = new StreamWriter(report.Open(), new UTF8Encoding(false)))
                    w.Write(env.ReportMarkdown ?? "");
                foreach (var f in env.InputFiles.Where(File.Exists).Take(6))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".log" || ext == ".txt")
                    {
                        var entry = zip.CreateEntry(Path.GetFileName(f));
                        string text;
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                        using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                            w.Write(ReportWriter.Anonymize(text, env.UserName));
                    }
                    else
                    {
                        zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
                    }
                }
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true })?.Dispose();
            return FixResult.Success("Support-Paket erstellt: " + target);
        }
    }
}
