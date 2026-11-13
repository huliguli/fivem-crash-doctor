using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CrashDoctor.Core
{
    /// <summary>Bericht als Markdown (Forum/GitHub) und als Kurzfassung für Discord. Entfernt den Windows-Benutzernamen.</summary>
    public static class ReportWriter
    {
        public static string ConfidenceText(Confidence c) =>
            c == Confidence.High ? "hoch" : c == Confidence.Medium ? "mittel" : "niedrig";

        public static string EvidenceText(Evidence e) =>
            e == Evidence.Belegt ? "belegt" : e == Evidence.Praxis ? "Erfahrungswert" : "Vermutung";

        public static string SeverityText(Severity s) =>
            s == Severity.Critical ? "Hauptverdacht" : s == Severity.Warning ? "Wahrscheinlich" : s == Severity.Hint ? "Hinweis" : "Info";

        public static string Anonymize(string text, string userName)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Regex.Replace(text, @"(?i)([A-Z]:\\Users\\)[^\\\r\n""]+", "$1<Benutzer>");
            text = Regex.Replace(text, @"(?i)([A-Z]:/Users/)[^/\r\n""]+", "$1<Benutzer>");
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
                text = Regex.Replace(text, @"(?i)(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", "<Benutzer>");
            return text;
        }

        public static IEnumerable<KeyValuePair<string, string>> CrashFields(CrashFacts f)
        {
            if (!string.IsNullOrEmpty(f.Signature)) yield return Kv("Crash-Signatur", f.Signature);
            if (!string.IsNullOrEmpty(f.FaultSymbol)) yield return Kv("Funktion", f.FaultSymbol);
            if (!string.IsNullOrEmpty(f.LegacyHash)) yield return Kv("Crash-Name", f.LegacyHash);
            if (!string.IsNullOrEmpty(f.ReportId)) yield return Kv("Report-ID", f.ReportId);
            if (f.GameBuild.HasValue) yield return Kv("Gamebuild", f.GameBuild.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(f.CrashedProcess)) yield return Kv("Abgestürzter Prozess", f.CrashedProcess);
            if (f.ExceptionCode.HasValue) yield return Kv("Ausnahme", ExceptionCatalog.Describe(f.ExceptionCode.Value));
            if (f.ErrorCodes.Count > 0) yield return Kv("Fehlercodes", string.Join(", ", f.ErrorCodes));
            if (f.DirectXErrors.Count > 0) yield return Kv("DirectX", string.Join(", ", f.DirectXErrors) + (string.IsNullOrEmpty(f.DeviceRemovedReason) ? "" : " (Grund: " + f.DeviceRemovedReason + ")"));
            if (f.Uptime.HasValue) yield return Kv("Zeit seit Spielstart", Analyzer.FormatDuration(f.Uptime.Value));
            if (f.CrashTimeUtc.HasValue) yield return Kv("Absturzzeit", f.CrashTimeUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(f.BlamedBy)) yield return Kv("FiveM nennt als Ursache", f.BlamedBy);
            if (!string.IsNullOrEmpty(f.DumpOsVersion)) yield return Kv("Windows laut Dump", f.DumpOsVersion);
            var g = f.GameLog;
            if (g != null)
            {
                if (!string.IsNullOrEmpty(g.GameState)) yield return Kv("Spielzustand", g.GameState);
                if (g.StreamingVirtualAvailableMb > 0)
                    yield return Kv("Streaming-Speicher", $"virtuell {g.StreamingVirtualUsedMb} von {g.StreamingVirtualAvailableMb} MB, physisch {g.StreamingPhysicalUsedMb} von {g.StreamingPhysicalAvailableMb} MB");
                if (g.MemoryLoadPercent.HasValue)
                    yield return Kv("RAM beim Absturz", $"{g.MemoryLoadPercent} % belegt, {g.PhysicalFreeMb} MB frei");
                if (g.PageFileFreeMb.HasValue) yield return Kv("Zusagbarer Speicher frei", $"{g.PageFileFreeMb} MB von {g.PageFileTotalMb} MB");
                if (g.FullPools.Count + g.FullStores.Count > 0) yield return Kv("Volle Pools", string.Join(", ", g.FullPools.Concat(g.FullStores)));
            }
        }

        public static IEnumerable<KeyValuePair<string, string>> SystemFields(SystemSnapshot s)
        {
            if (s == null || !s.Collected) yield break;
            if (!string.IsNullOrEmpty(s.OsName)) yield return Kv("Windows", s.OsName + (s.OsBuild > 0 ? $" (Build {s.OsBuild})" : ""));
            if (s.TotalRamBytes > 0) yield return Kv("Arbeitsspeicher", $"{s.TotalRamBytes / 1073741824.0:0.#} GB, frei {s.AvailableRamBytes / 1073741824.0:0.#} GB");
            if (s.PagefileAllocatedMb.HasValue || s.PagefileAutomatic.HasValue)
                yield return Kv("Auslagerungsdatei", (s.PagefileAutomatic == true ? "automatisch" : s.PagefileAllocatedMb == 0 ? "aus" : "fest eingestellt") +
                                                     (s.PagefileAllocatedMb > 0 ? $", {s.PagefileAllocatedMb.Value / 1024.0:0.#} GB" : ""));
            foreach (var g in s.Gpus)
                yield return Kv("Grafik", $"{g.Name} – Treiber {g.FriendlyDriverVersion ?? g.DriverVersion}" + (g.DriverDate.HasValue ? $" vom {g.DriverDate.Value:dd.MM.yyyy}" : ""));
            foreach (var d in s.Drives)
                yield return Kv("Laufwerk " + d.Root, $"{d.Role}: {d.FreeBytes / 1073741824.0:0.#} GB frei von {d.TotalBytes / 1073741824.0:0} GB");
            yield return Kv("FiveM", s.FiveMFound ? s.FiveMAppPath : "nicht gefunden");
            if (!string.IsNullOrEmpty(s.GtaPath)) yield return Kv("GTA V", s.GtaPath + (string.IsNullOrEmpty(s.GtaPlatform) ? "" : $" ({s.GtaPlatform})"));
            if (!string.IsNullOrEmpty(s.UpdateChannel)) yield return Kv("Update-Kanal", s.UpdateChannel);
            if (s.FiveMCacheBytes.HasValue) yield return Kv("FiveM-Cache", $"{s.FiveMCacheBytes.Value / 1073741824.0:0.0} GB");
            if (s.CrashDumpCount > 0) yield return Kv("Crash-Dumps", $"{s.CrashDumpCount} gesamt, {s.CrashDumpsLast7Days} in den letzten 7 Tagen");
            yield return Kv("Plugins", s.FiveMPlugins.Count == 0 ? "keine" : string.Join(", ", s.FiveMPlugins));
            if (s.GtaGraphicsMods.Count > 0) yield return Kv("Mods im GTA-Ordner", string.Join(", ", s.GtaGraphicsMods));
        }

        public static List<string> ThirdPartyModules(CrashFacts f, SystemSnapshot s) =>
            f.LoadedModules.Where(m => ModuleCatalog.IsThirdPartyPath(m.Path, s?.FiveMAppPath, s?.GtaPath))
                .Select(m =>
                {
                    var e = ModuleCatalog.Lookup(m, s?.FiveMAppPath, s?.GtaPath);
                    return e != null ? $"{m.Name} ({e.Product})" : m.Name;
                }).Distinct().ToList();

        public static string Markdown(AnalysisReport r, string appVersion, string userName)
        {
            var f = r.Facts;
            var sb = new StringBuilder();
            sb.AppendLine("# FiveM-Crash-Analyse");
            sb.AppendLine();
            sb.AppendLine($"Erstellt am {DateTime.Now:dd.MM.yyyy HH:mm} mit FiveM Crash Doctor {appVersion}. Quellen: {(f.Sources.Count == 0 ? "–" : string.Join(", ", f.Sources))}.");
            sb.AppendLine();

            var crash = CrashFields(f).ToList();
            if (crash.Count > 0)
            {
                sb.AppendLine("## Absturz");
                sb.AppendLine();
                sb.AppendLine("| Feld | Wert |");
                sb.AppendLine("| --- | --- |");
                foreach (var kv in crash) sb.AppendLine($"| {kv.Key} | `{Md(kv.Value)}` |");
                sb.AppendLine();
            }

            sb.AppendLine("## Mögliche Ursachen");
            sb.AppendLine();
            if (r.Diagnoses.Count == 0) sb.AppendLine("Keine erkannt.");
            int i = 1;
            foreach (var d in r.Diagnoses)
            {
                sb.AppendLine($"### {i++}. {d.Title}");
                sb.AppendLine();
                sb.AppendLine($"*{SeverityText(d.Severity)} · Wahrscheinlichkeit {ConfidenceText(d.Confidence)} · {EvidenceText(d.Evidence)}*" +
                              (string.IsNullOrEmpty(d.SourceNote) ? "" : $" · Quelle: {d.SourceNote}"));
                sb.AppendLine();
                sb.AppendLine(d.Explanation);
                sb.AppendLine();
                int n = 1;
                foreach (var st in d.Steps) sb.AppendLine($"{n++}. {st}");
                if (d.MatchedBy.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Erkannt an: " + string.Join("; ", d.MatchedBy));
                }
                sb.AppendLine();
            }

            var sys = SystemFields(r.System).ToList();
            if (sys.Count > 0)
            {
                sb.AppendLine("## System");
                sb.AppendLine();
                foreach (var kv in sys) sb.AppendLine($"- **{kv.Key}:** {kv.Value}");
                sb.AppendLine();
            }

            if (f.StackModules.Count > 0)
            {
                sb.AppendLine("## Module auf dem Stack (heuristisch)");
                sb.AppendLine();
                sb.AppendLine("`" + string.Join("` → `", f.StackModules) + "`");
                sb.AppendLine();
            }

            var third = ThirdPartyModules(f, r.System);
            if (third.Count > 0)
            {
                sb.AppendLine("## Fremdmodule im Spielprozess");
                sb.AppendLine();
                foreach (var t in third) sb.AppendLine("- " + t);
                sb.AppendLine();
            }

            if (f.Log != null && (f.Log.ScriptErrors.Count > 0 || f.Log.ErrorLines.Count > 0))
            {
                sb.AppendLine("## Auszug aus dem Client-Log");
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var l in f.Log.ScriptErrors.Skip(Math.Max(0, f.Log.ScriptErrors.Count - 5))) sb.AppendLine(l);
                foreach (var l in f.Log.ErrorLines.Skip(Math.Max(0, f.Log.ErrorLines.Count - 10))) sb.AppendLine(l);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (f.Warnings.Count > 0)
            {
                sb.AppendLine("## Hinweise beim Einlesen");
                sb.AppendLine();
                foreach (var w in f.Warnings) sb.AppendLine("- " + w);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("Inoffizielles Werkzeug, nicht mit Cfx.re oder Rockstar Games verbunden.");
            return Anonymize(sb.ToString(), userName);
        }

        public static string Discord(AnalysisReport r, string userName)
        {
            var f = r.Facts;
            var sb = new StringBuilder();
            sb.Append("**FiveM-Crash**");
            var ids = new List<string>();
            if (!string.IsNullOrEmpty(f.Signature)) ids.Add($"`{f.Signature}`");
            if (!string.IsNullOrEmpty(f.LegacyHash)) ids.Add($"`{f.LegacyHash}`");
            if (f.GameBuild.HasValue) ids.Add("Build " + f.GameBuild.Value);
            if (!string.IsNullOrEmpty(f.ReportId)) ids.Add("Report-ID `" + f.ReportId + "`");
            if (ids.Count > 0) sb.Append(" · ").Append(string.Join(" · ", ids));
            sb.AppendLine();
            if (f.ExceptionCode.HasValue && ExceptionCatalog.Codes.TryGetValue(f.ExceptionCode.Value, out var ex))
                sb.AppendLine($"Ausnahme: `0x{f.ExceptionCode.Value:X8}` {ex.Name}" + (f.Uptime.HasValue ? $" · {Analyzer.FormatDuration(f.Uptime.Value)} nach Start" : ""));

            var main = r.Diagnoses.Where(d => d.Severity >= Severity.Warning).Take(3).ToList();
            if (main.Count > 0)
            {
                sb.AppendLine($"**Wahrscheinlich:** {main[0].Title} ({ConfidenceText(main[0].Confidence)})");
                if (main.Count > 1) sb.AppendLine("**Außerdem:** " + string.Join("; ", main.Skip(1).Select(d => d.Title)));
            }
            var s = r.System;
            if (s != null && s.Collected)
            {
                var parts = new List<string>();
                var gpu = s.Gpus.FirstOrDefault(g => g.Vendor != "Andere") ?? s.Gpus.FirstOrDefault();
                if (gpu != null) parts.Add($"{gpu.Name} ({gpu.FriendlyDriverVersion ?? gpu.DriverVersion})");
                if (s.TotalRamBytes > 0) parts.Add($"{s.TotalRamBytes / 1073741824.0:0} GB RAM");
                if (s.PagefileAllocatedMb == 0 && s.PagefileAutomatic != true) parts.Add("ohne Auslagerungsdatei");
                if (s.FiveMPlugins.Count > 0) parts.Add($"{s.FiveMPlugins.Count} Plugins");
                if (parts.Count > 0) sb.AppendLine("**System:** " + string.Join(" · ", parts));
            }
            if (f.StackModules.Count > 0) sb.AppendLine("Stack: `" + string.Join(" → ", f.StackModules.Take(8)) + "`");
            var third = ThirdPartyModules(f, s);
            if (third.Count > 0) sb.AppendLine("Fremdmodule: " + string.Join(", ", third.Take(8)));
            if (f.Log != null && f.Log.ScriptErrors.Count > 0)
                sb.AppendLine("Letzter Skriptfehler: `" + Oneline(f.Log.ScriptErrors.Last(), 180) + "`");
            var text = Anonymize(sb.ToString().TrimEnd(), userName);
            return text.Length > 1900 ? text.Substring(0, 1890) + " …" : text;
        }

        static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);
        static string Md(string s) => (s ?? "").Replace("|", "\\|").Replace("`", "'").Replace("\n", " ");
        static string Oneline(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("`", "'");
            return s.Length > max ? s.Substring(0, max) + " …" : s;
        }
    }
}
