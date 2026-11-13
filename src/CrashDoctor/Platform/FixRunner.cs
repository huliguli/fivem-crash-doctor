using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CrashDoctor.Core;

namespace CrashDoctor.Platform
{
    /// <summary>
    /// Führt Reparaturen aus. Braucht eine Reparatur Admin-Rechte, startet sich das Programm mit
    /// UAC-Abfrage selbst neu (--run-fix) und führt NUR diese eine Reparatur aus.
    /// </summary>
    public static class FixRunner
    {
        /// <summary>Nur diese Reparaturen darf der erhöhte Prozess ausführen.</summary>
        static readonly HashSet<string> ElevatedAllowed = new HashSet<string> { FixIds.PagefileAuto, FixIds.MoveGtaMods, FixIds.CloseOverlays };

        static string ExchangeDir => Path.Combine(Path.GetTempPath(), "FiveMCrashDoctor");

        public static async Task<FixResult> RunAsync(FixAction fix, FixEnv env)
        {
            if (fix.NeedsFiveMClosed && SystemCollector.IsFiveMRunning())
                return FixResult.Fail("FiveM läuft noch. Beende FiveM komplett (auch im Infobereich der Taskleiste) und versuche es erneut.");
            if (fix.NeedsAdmin(env) && !SystemCollector.IsElevated())
                return await RunElevatedAsync(fix, env).ConfigureAwait(false);
            return await Task.Run(() => RunSafe(fix, env)).ConfigureAwait(false);
        }

        static FixResult RunSafe(FixAction fix, FixEnv env)
        {
            AppLog.Write($"Reparatur {fix.Id} startet (Admin: {SystemCollector.IsElevated()})");
            try
            {
                var r = fix.Run(env);
                AppLog.Write($"Reparatur {fix.Id}: {(r.Ok ? "ok" : "fehlgeschlagen")} – {r.Message}");
                return r;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Write($"Reparatur {fix.Id}: kein Zugriff – {ex}");
                return FixResult.Fail("Kein Zugriff: " + ex.Message + " Starte das Tool als Administrator und versuche es erneut.");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Reparatur {fix.Id}: Fehler – {ex}");
                return FixResult.Fail("Fehlgeschlagen: " + ex.Message);
            }
        }

        static async Task<FixResult> RunElevatedAsync(FixAction fix, FixEnv env)
        {
            Directory.CreateDirectory(ExchangeDir);
            var id = Guid.NewGuid().ToString("N");
            var request = Path.Combine(ExchangeDir, $"req-{id}.txt");
            var result = Path.Combine(ExchangeDir, $"res-{id}.txt");
            File.WriteAllLines(request, new[]
            {
                "fix=" + fix.Id,
                "fivem=" + (env.Paths.AppDir ?? ""),
                "gta=" + (env.Paths.GtaDir ?? ""),
                "result=" + result
            });

            var exe = Process.GetCurrentProcess().MainModule.FileName;
            // Pfad immer in Anführungszeichen: Ordner mit Leerzeichen sonst kaputt (DESKTOP-APPS/GOTCHAS)
            var psi = new ProcessStartInfo(exe, $"--run-fix \"{request}\"") { UseShellExecute = true, Verb = "runas" };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return FixResult.Fail("Der Admin-Prozess konnte nicht gestartet werden.");
                    var exited = await Task.Run(() => p.WaitForExit(15 * 60 * 1000)).ConfigureAwait(false);
                    if (!exited) return FixResult.Fail("Die Reparatur mit Admin-Rechten antwortet nicht.");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return FixResult.Fail("Abgebrochen: Die Admin-Abfrage wurde abgelehnt.");
            }
            finally
            {
                TryDelete(request);
            }

            if (!File.Exists(result)) return FixResult.Fail("Die Reparatur mit Admin-Rechten hat kein Ergebnis geliefert. Details stehen im Protokoll.");
            var kv = ReadKv(result);
            TryDelete(result);
            kv.TryGetValue("message", out var msg);
            return new FixResult { Ok = kv.TryGetValue("ok", out var ok) && ok == "true", Message = (msg ?? "").Replace("\\n", "\n") };
        }

        /// <summary>Einstieg im erhöhten Prozess. Prüft die Anfrage, bevor sie irgendetwas tut.</summary>
        public static int RunFromRequest(string requestPath)
        {
            AppLog.Start("Admin-Reparatur");
            Dictionary<string, string> kv;
            try
            {
                var full = Path.GetFullPath(requestPath);
                if (!full.StartsWith(Path.GetFullPath(ExchangeDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    AppLog.Write("Anfrage außerhalb des Austauschordners abgelehnt: " + full);
                    return 2;
                }
                kv = ReadKv(full);
            }
            catch (Exception ex) { AppLog.Write("Anfrage nicht lesbar: " + ex); return 2; }

            kv.TryGetValue("fix", out var id);
            kv.TryGetValue("result", out var resultPath);
            if (string.IsNullOrEmpty(resultPath) || !Path.GetFullPath(resultPath).StartsWith(Path.GetFullPath(ExchangeDir), StringComparison.OrdinalIgnoreCase))
                return 2;

            FixResult r;
            var fix = FixRegistry.Get(id ?? "");
            if (fix == null || !ElevatedAllowed.Contains(fix.Id))
            {
                r = FixResult.Fail("Diese Reparatur darf nicht mit Admin-Rechten laufen.");
            }
            else
            {
                kv.TryGetValue("fivem", out var fivem);
                kv.TryGetValue("gta", out var gta);
                if (fix.Id == FixIds.MoveGtaMods && (string.IsNullOrEmpty(gta) || !File.Exists(Path.Combine(gta, "GTA5.exe"))))
                    r = FixResult.Fail("Der GTA-V-Ordner ist ungültig.");
                else
                    r = RunSafe(fix, new FixEnv { Paths = FiveMPaths.FromKnown(fivem, gta) });
            }
            try
            {
                File.WriteAllLines(resultPath, new[] { "ok=" + (r.Ok ? "true" : "false"), "message=" + (r.Message ?? "").Replace("\r", "").Replace("\n", "\\n") });
            }
            catch (Exception ex) { AppLog.Write("Ergebnis nicht schreibbar: " + ex); return 3; }
            return r.Ok ? 0 : 1;
        }

        /// <summary>Startet das ganze Programm mit Admin-Rechten neu; Eingaben werden über eine Datei mitgegeben.</summary>
        public static bool RestartElevated(string pastedText, IEnumerable<string> files)
        {
            Directory.CreateDirectory(ExchangeDir);
            var restore = Path.Combine(ExchangeDir, $"restore-{Guid.NewGuid():N}.txt");
            var lines = (files ?? Enumerable.Empty<string>()).Select(f => "file=" + f).ToList();
            lines.Add("text=" + (pastedText ?? "").Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n"));
            File.WriteAllLines(restore, lines);
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                Process.Start(new ProcessStartInfo(exe, $"--restore \"{restore}\"") { UseShellExecute = true, Verb = "runas" })?.Dispose();
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                TryDelete(restore);
                return false;
            }
        }

        public static void ReadRestore(string path, out string text, out List<string> files)
        {
            text = null;
            files = new List<string>();
            try
            {
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(Path.GetFullPath(ExchangeDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return;
                foreach (var line in File.ReadAllLines(full))
                {
                    if (line.StartsWith("file=")) files.Add(line.Substring(5));
                    else if (line.StartsWith("text=")) text = Unescape(line.Substring(5));
                }
                TryDelete(full);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Unescape(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    var n = s[++i];
                    sb.Append(n == 'n' ? '\n' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static Dictionary<string, string> ReadKv(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
            return d;
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
