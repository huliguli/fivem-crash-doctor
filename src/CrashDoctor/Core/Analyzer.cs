using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CrashDoctor.Core
{
    /// <summary>Führt die Daten aus Dump, crashometry usw. in CrashFacts zusammen.</summary>
    public static class CrashMerger
    {
        public static void ApplyDump(MinidumpInfo dump, string fileName, CrashFacts f)
        {
            f.HasDump = true;
            f.DumpFileName = fileName;
            f.Sources.Add("Crash-Dump: " + fileName);
            foreach (var w in dump.Warnings) f.Warnings.Add(w);

            if (!string.IsNullOrEmpty(dump.Signature))
            {
                if (string.IsNullOrEmpty(f.Signature))
                {
                    f.Signature = dump.Signature;
                    f.FaultModule = dump.FaultModule?.Name;
                    f.FaultOffset = dump.FaultOffset;
                    f.FaultInUnloadedModule = dump.FaultInUnloaded;
                }
                if (string.IsNullOrEmpty(f.FaultModule) || f.FaultModule == dump.FaultModule?.Name) f.FaultModuleInfo = dump.FaultModule;
                else if (!SameSignature(f.Signature, dump.Signature))
                {
                    f.Warnings.Add($"Der Dump ({dump.Signature}) zeigt eine andere Absturzstelle als der Crash-Text ({f.Signature}). " +
                                   "Vermutlich gehören sie zu zwei verschiedenen Abstürzen – im Zweifel den neuesten Dump nehmen.");
                }
            }

            if (dump.ExceptionCode.HasValue && !f.ExceptionCode.HasValue)
            {
                f.ExceptionCode = dump.ExceptionCode;
                f.ExceptionAddress = dump.ExceptionAddress;
                f.ExceptionParameters = dump.ExceptionParameters;
            }

            f.CrashedProcess = f.CrashedProcess ?? dump.MainModuleName;
            if (!f.GameBuild.HasValue && dump.MainModuleName != null)
            {
                var m = Regex.Match(dump.MainModuleName, @"_b(\d{3,5})_");
                if (m.Success) f.GameBuild = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            f.LoadedModules.Clear();
            f.LoadedModules.AddRange(dump.Modules);
            f.UnloadedModules.Clear();
            f.UnloadedModules.AddRange(dump.UnloadedModules);
            foreach (var s in dump.StackModules) CrashTextParser.AddUnique(f.StackModules, s);
            foreach (var m in dump.StackModuleInfos) if (!f.StackModuleInfos.Contains(m)) f.StackModuleInfos.Add(m);

            // FiveMs absichtliche Notbremse: Ausführen-Fehler im Bereich Basis+0x1000..0x2000 des Spielprozesses
            // (MiniDump.cpp, isAccessDeath). Im Fenster steht dann „Early-exit trap“ oder „Game integrity check failed“.
            if (dump.ExceptionCode == 0xC0000005 && dump.ExceptionParameters.Length > 0 && dump.ExceptionParameters[0] == 8 &&
                dump.FaultModule != null && !dump.FaultInUnloaded && dump.Modules.Count > 0 && ReferenceEquals(dump.FaultModule, dump.Modules[0]) &&
                dump.FaultOffset >= 0x1000 && dump.FaultOffset < 0x2000)
            {
                f.EarlyExitTrap = true;
                f.EarlyExitFromDump = true;
            }
            f.Uptime = dump.Uptime;
            f.CrashTimeUtc = dump.TimestampUtc;
            if (dump.OsMajor > 0) f.DumpOsVersion = $"Windows {dump.OsMajor}.{dump.OsMinor} (Build {dump.OsBuild})";
            f.DumpCpuVendor = dump.CpuVendor;
            if (dump.ProcessorCount > 0) f.DumpProcessorCount = dump.ProcessorCount;
        }

        public static bool SameSignature(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        static string Normalize(string s) => Regex.Replace(s ?? "", @"\+0x", "+", RegexOptions.IgnoreCase).Trim();

        /// <summary>crashometry.json aus dem „Save information“-Zip: ein flaches JSON-Objekt aus Zeichenketten.</summary>
        public static void ApplyCrashometry(string json, CrashFacts f)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            foreach (Match m in Regex.Matches(json, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                var key = Unescape(m.Groups[1].Value);
                var value = Unescape(m.Groups[2].Value).Replace("~n~", "\n");
                if (key.StartsWith("crashometry_", StringComparison.OrdinalIgnoreCase)) key = key.Substring(12);
                // Kennungen des Spielers nicht übernehmen (z. B. RockstarId)
                if (key.IndexOf("id", StringComparison.OrdinalIgnoreCase) >= 0 && key.IndexOf("ide", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                f.Crashometry[key] = value;
            }
            if (f.Crashometry.TryGetValue("kill_network_msg", out var kill) && !f.Crashometry.ContainsKey("reload_game") &&
                string.IsNullOrEmpty(f.DisconnectMessage))
                f.DisconnectMessage = kill;
        }

        static string Unescape(string s)
        {
            try { return Regex.Unescape(s); } catch (ArgumentException) { return s; }
        }
    }

    /// <summary>Die Regeln: aus Fakten und Systembild werden Diagnosen mit Schritten und Fixes.</summary>
    public static class Analyzer
    {
        const string TestServerUrl = "https://cfx.re/join/y4lg95";

        public static AnalysisReport Analyze(CrashFacts f, SystemSnapshot s)
        {
            var report = new AnalysisReport { Facts = f, System = s ?? new SystemSnapshot() };
            var list = new List<Diagnosis>();
            s = report.System;

            var faultEntry = f.FaultModuleInfo != null ? ModuleCatalog.Lookup(f.FaultModuleInfo, s.FiveMAppPath, s.GtaPath) : ModuleCatalog.Lookup(f.FaultModule);
            bool isCrash = !string.IsNullOrEmpty(f.Signature) || f.ExceptionCode.HasValue || f.IntegrityCheckFailed || f.EarlyExitTrap
                           || f.ErrorCodes.Count > 0 || f.DirectXFatal || f.DirectXErrors.Count > 0;

            RuleDisconnect(f, list);
            RuleKnownCrashName(f, list);
            RuleIntegrity(f, list);
            RuleEarlyExit(f, s, list);
            RuleDirectX(f, s, list);
            RuleErrorCodes(f, s, list);
            RuleFaultModule(f, s, faultEntry, list);
            RuleRecurrence(f, list);
            RuleUnloaded(f, list);
            RuleStackAndLoadedModules(f, s, list);
            RuleExceptionCode(f, s, list);
            RuleCppException(f, list);
            RuleFatalMessage(f, list);
            RuleUptime(f, list);
            RuleLog(f, list);
            RuleGameLog(f, list);
            RuleSystem(f, s, list);
            if (isCrash && string.IsNullOrEmpty(f.DisconnectMessage)) RuleServerOrClient(f, list);
            if (f.HasAnyData && !list.Any(d => d.Severity >= Severity.Warning)) RuleFallback(f, list);

            // gleiche Kennung zusammenführen, dann ordnen
            foreach (var group in list.GroupBy(d => d.Id))
            {
                var first = group.First();
                foreach (var other in group.Skip(1))
                {
                    foreach (var w in other.MatchedBy) first.Because(w);
                    foreach (var st in other.Steps) if (!first.Steps.Contains(st)) first.Steps.Add(st);
                    first.Fix(other.FixIds.ToArray());
                    if (other.Confidence > first.Confidence) first.Confidence = other.Confidence;
                    if (other.Severity > first.Severity) first.Severity = other.Severity;
                }
                report.Diagnoses.Add(first);
            }
            var ordered = report.Diagnoses.Select((d, i) => new { d, i }).OrderByDescending(x => x.d.Rank).ThenBy(x => x.i).Select(x => x.d).ToList();
            report.Diagnoses.Clear();
            report.Diagnoses.AddRange(ordered);
            return report;
        }

        // ---------------------------------------------------------------- Regeln

        static void RuleDisconnect(CrashFacts f, List<Diagnosis> list)
        {
            if (string.IsNullOrEmpty(f.DisconnectMessage)) return;
            var d = New("disconnect", "Kein Absturz: die Verbindung zum Server wurde getrennt", Severity.Warning, Confidence.High, Evidence.Belegt,
                    "FiveM zeigt dieses Fenster („Disconnected“), wenn der Server dich getrennt hat oder die Verbindung abgerissen ist. Das Spiel selbst ist nicht abgestürzt.\n\nMeldung: " + Shorten(f.DisconnectMessage, 400))
                .Because("Fenstertitel „Disconnected“ bzw. kill_network_msg");
            d.SourceNote = "FiveM-Quelltext, MiniDump.cpp";
            d.Step("Lies die Meldung: Kick, Bann oder Wartung kommt vom Server – dann hilft nur das Server-Team.")
             .Step("Bei „timed out“ oder Verbindungsabbruch: Router neu starten, VPN aus, nach einer Weile erneut verbinden.")
             .Step("Passiert es nur auf einem Server, liegt es fast immer an diesem Server.");
            list.Add(d);
        }

        static void RuleKnownCrashName(CrashFacts f, List<Diagnosis> list)
        {
            if (string.IsNullOrEmpty(f.LegacyHash) || !KnownCrashes.Names.TryGetValue(f.LegacyHash, out var known)) return;
            var d = New("known-" + f.LegacyHash, $"Bekannter FiveM-Absturz „{f.LegacyHash}“", Severity.Critical, Confidence.High, Evidence.Belegt,
                known.Explanation).Because("Crash-Name „" + f.LegacyHash + "“ steht in der offiziellen Liste von Cfx.re");
            d.SourceNote = "Cfx.re, crash-data.json im FiveM-Quelltext";
            switch (known.Kind)
            {
                case KnownCrashes.Kind.ReloadRelated:
                    d.Step("Starte FiveM komplett neu, bevor du auf einen anderen Server gehst oder nach einem Disconnect wieder verbindest – nicht im laufenden Spiel wechseln.")
                     .Step("Leere einmal den FiveM-Cache.")
                     .Fix(FixIds.ClearCache);
                    break;
                case KnownCrashes.Kind.ServerAssets:
                    d.Step("Die Ursache liegt in Inhalten des Servers (Fahrzeuge, Skripte). Gib dem Server-Team den Crash-Namen, die Uhrzeit und was du gerade gemacht hast.")
                     .Step("Leere den FiveM-Cache, damit beschädigte Downloads neu geladen werden.")
                     .Step("Tritt es auf einem anderen Server nicht auf, kannst du auf deinem PC nichts weiter tun.")
                     .Fix(FixIds.ClearCache, FixIds.TestServer);
                    break;
                case KnownCrashes.Kind.Disconnect:
                    d.Step("Der Absturz passiert beim Trennen vom Server. Starte FiveM nach dem Trennen neu, statt direkt neu zu verbinden.")
                     .Step("Leere einmal den FiveM-Cache.")
                     .Fix(FixIds.ClearCache);
                    break;
                default:
                    d.Step("Das ist ein Fehler im Spiel, der vor allem durch Spielsituationen ausgelöst wird. Leere den Cache und halte FiveM aktuell (FiveM neu starten lädt Updates).")
                     .Step("Häuft es sich auf einem Server, gib dem Server-Team Crash-Name und Uhrzeit.")
                     .Fix(FixIds.ClearCache);
                    break;
            }
            list.Add(d);
        }

        static void RuleIntegrity(CrashFacts f, List<Diagnosis> list)
        {
            if (!f.IntegrityCheckFailed) return;
            var d = New("integrity", "Integritätsprüfung von FiveM fehlgeschlagen", Severity.Critical, Confidence.High, Evidence.Belegt,
                "FiveM hat das Spiel beendet, weil eine Integritätsprüfung fehlschlug. Laut FiveM liegt das oft an Änderungen am PC – typischerweise Mods, Programme, die sich ins Spiel einklinken, oder veränderte Spieldateien.")
                .Because("Meldung „Game integrity check failed“");
            d.SourceNote = "FiveM-Quelltext, MiniDump.cpp; Hilfeartikel aka.cfx.re/integrity-check-failed";
            d.Step("Deaktiviere Plugins im FiveM-Ordner und entferne Mods aus dem GTA-V-Ordner.")
             .Step("Lass die GTA-V-Dateien von Steam, Epic oder dem Rockstar Launcher prüfen.")
             .Step("Beende Programme, die sich in Spiele einklinken (Overlays, Cheat-/Trainer-Tools, Makro-Software).")
             .Step("Lies den offiziellen Hilfeartikel: https://aka.cfx.re/integrity-check-failed")
             .Fix(FixIds.DisablePlugins, FixIds.MoveGtaMods, FixIds.VerifyGame, FixIds.CloseOverlays);
            list.Add(d);
        }

        static void RuleEarlyExit(CrashFacts f, SystemSnapshot s, List<Diagnosis> list)
        {
            if (!f.EarlyExitTrap) return;
            var mods = f.LoadedModules
                .Select(m => new { m, e = ModuleCatalog.Lookup(m, s.FiveMAppPath, s.GtaPath) })
                .Where(x => x.e != null && x.e.IsConflictSoftware)
                .Select(x => $"{x.m.Name} ({x.e.Product})").Distinct().ToList();
            var d = New("early-exit", "FiveM hat das Spiel absichtlich beendet (Schutzfalle)", f.EarlyExitFromDump ? Severity.Critical : Severity.Warning,
                f.EarlyExitFromDump ? Confidence.High : Confidence.Medium, Evidence.Belegt,
                "Das ist kein gewöhnlicher Absturz: FiveM hat eine Schutzfalle ausgelöst („Early-exit trap“, je nach Version auch „Game integrity check failed“) und das Spiel beendet. " +
                "Laut FiveM liegt das oft an Änderungen am PC – Mods, Programme, die sich ins Spiel einklinken, oder veränderte Spieldateien. Den genauen Auslöser sieht nur Cfx über die Report-ID." +
                (mods.Count > 0 ? "\n\nIm Spielprozess steckten: " + string.Join(", ", mods.Take(8)) + "." : ""))
                .Because(f.EarlyExitFromDump ? "Dump: Ausführen-Fehler im Schutzbereich des Spielprozesses (" + f.Signature + ")" : "Meldung „early-exit trap“");
            d.SourceNote = "FiveM-Quelltext, MiniDump.cpp (isAccessDeath); Hilfeartikel aka.cfx.re/integrity-check-failed";
            d.SourceNote = "FiveM-Quelltext, MiniDump.cpp";
            d.Step("Notiere die Report-ID aus dem erweiterten Bereich des Fensters – ohne sie kann dir der Cfx-Support nicht helfen.")
             .Step("Entferne Tools, die sich in Prozesse einklinken (Trainer, Injektoren, Makro-Tools) und deaktiviere Plugins.")
             .Step("Installiere ausstehende Windows-Updates und starte den PC neu.")
             .Step("Lies den Hilfeartikel von Cfx: https://aka.cfx.re/integrity-check-failed")
             .Fix(FixIds.DisablePlugins, FixIds.MoveGtaMods, FixIds.VerifyGame, FixIds.SupportZip);
            if (!string.IsNullOrEmpty(f.ReportId)) d.Because("Report-ID vorhanden: " + f.ReportId);
            list.Add(d);
        }

        static void RuleDirectX(CrashFacts f, SystemSnapshot s, List<Diagnosis> list)
        {
            if (!string.IsNullOrEmpty(f.GraphicsModsInMessage))
            {
                var m = New("graphics-mods-message", "FiveM hat Grafik-Mods als mögliche Ursache gefunden", Severity.Critical, Confidence.High, Evidence.Belegt,
                    "FiveM prüft beim DirectX-Fehler den GTA-V-Ordner auf Grafik-Mods (d3d11.dll, dxgi.dll, d3d10.dll) und hat welche gefunden: " + f.GraphicsModsInMessage + ".")
                    .Because("Meldung „graphics mods were found in your GTA V installation“");
                m.SourceNote = "FiveM-Quelltext, RenderHooks.cpp";
                m.Step("Verschiebe die Mod-Dateien aus dem GTA-V-Ordner (das Tool legt eine Sicherung an).")
                 .Step("Willst du ReShade weiter nutzen, nimm eine aktuelle Version und lege sie in den FiveM-Ordner „plugins“ statt in den GTA-Ordner.")
                 .Fix(FixIds.MoveGtaMods);
                list.Add(m);
            }

            bool removed = f.DirectXErrors.Any(e => e.Contains("DEVICE_REMOVED") || e.Contains("DEVICE_HUNG") || e.Contains("DEVICE_RESET") || e.Contains("DRIVER_INTERNAL_ERROR"))
                           || (f.DeviceRemovedReason ?? "").IndexOf("HUNG", StringComparison.OrdinalIgnoreCase) >= 0;
            if (removed)
            {
                var reason = string.IsNullOrEmpty(f.DeviceRemovedReason) ? "" : $" Windows nennt als Grund {f.DeviceRemovedReason}.";
                var d = New("gpu-device-removed", "Die Grafikkarte ist ausgestiegen (DirectX „Device removed“)", Severity.Critical, Confidence.High, Evidence.Praxis,
                    "DirectX hat die Verbindung zur Grafikkarte verloren." + reason + " Typische Gründe: ein abgestürzter oder veralteter Grafiktreiber, Übertaktung oder Undervolting, Überhitzung oder ein Netzteil, das unter Last einbricht.")
                    .Because("DirectX-Fehler: " + string.Join(", ", f.DirectXErrors));
                d.SourceNote = "Meldungsformat aus FiveM-Quelltext (RenderHooks.cpp), Ursachen aus der Praxis";
                d.Step("Installiere den neuesten Grafiktreiber. Hilft das nicht: mit der Option „Neuinstallation“ bzw. „Werkseinstellungen“ sauber neu installieren.")
                 .Step("Nimm jede Übertaktung zurück (MSI Afterburner: „Reset“) – auch werkseitig übertaktete Karten testweise mit -100 MHz.")
                 .Step("Prüfe die GPU-Temperatur unter Last (über 85 °C ist kritisch) und reinige die Lüfter.")
                 .Step("Leere den Shader-Cache des Treibers.")
                 .Fix(FixIds.GpuDriver, FixIds.ClearShaderCache, FixIds.CloseOverlays);
                AddDriverAge(d, s);
                list.Add(d);
            }
            else if (f.DirectXErrors.Any(e => e.Contains("OUTOFMEMORY")))
            {
                var d = New("gpu-vram", "Der Grafikspeicher (VRAM) ist vollgelaufen", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                    "DirectX meldet, dass kein Grafikspeicher mehr frei ist. Server mit vielen Fahrzeugen, Kleidung und Karten brauchen deutlich mehr VRAM als das normale GTA V.")
                    .Because("DirectX-Fehler: " + string.Join(", ", f.DirectXErrors));
                d.Step("Stelle Texturqualität auf „Normal“ oder „Hoch“ und schalte MSAA aus.")
                 .Step("Schließe Programme mit Hardwarebeschleunigung (Browser, Discord) während des Spielens.")
                 .Fix(FixIds.ResetGraphics, FixIds.ClearCache);
                list.Add(d);
            }
            else if (f.DirectXFatal)
            {
                var d = New("directx-fatal", "DirectX-Fehler beim Rendern", Severity.Warning, Confidence.Medium, Evidence.Belegt,
                    "FiveM meldet einen nicht behebbaren DirectX-Fehler. Häufigste Ursachen sind der Grafiktreiber, Grafik-Mods und Overlays.")
                    .Because("Meldung „DirectX encountered an unrecoverable error“");
                d.Step("Grafiktreiber aktualisieren und den PC neu starten.")
                 .Step("Overlays (NVIDIA, Steam, Discord, RivaTuner) testweise abschalten.")
                 .Fix(FixIds.GpuDriver, FixIds.CloseOverlays, FixIds.MoveGtaMods);
                AddDriverAge(d, s);
                list.Add(d);
            }
        }

        static void RuleErrorCodes(CrashFacts f, SystemSnapshot s, List<Diagnosis> list)
        {
            foreach (var code in f.ErrorCodes)
            {
                if (code == "ERR_GFX_D3D_INIT")
                {
                    var d = New("err-d3d-init", "DirectX 11 konnte nicht starten (ERR_GFX_D3D_INIT)", Severity.Critical, Confidence.High, Evidence.Belegt,
                        "GTA V konnte die Grafikkarte nicht initialisieren. Laut FiveM-Quelltext hängt das stark mit dem NVIDIA-Overlay (ShadowPlay) zusammen; außerdem prüft FiveM bei diesem Fehler den GTA-Ordner auf Grafik-Mods. Daneben kommen veraltete Treiber und Übertaktung in Frage.")
                        .Because("Fehlercode " + code);
                    d.SourceNote = "FiveM-Quelltext: FileMapping.Win32.cpp (nvspcap), RenderHooks.cpp (Grafik-Mods)";
                    d.Step("Schalte das NVIDIA-Overlay bzw. Instant Replay aus (NVIDIA App → Einstellungen).")
                     .Step("Entferne d3d11.dll, dxgi.dll und d3d10.dll aus dem GTA-V-Ordner (ReShade/ENB).")
                     .Step("Aktualisiere den Grafiktreiber und nimm Übertaktung zurück.")
                     .Step("Setze die Grafikeinstellungen zurück – z. B. wenn eine zu hohe Auflösung oder ein falscher Monitor eingestellt ist.")
                     .Fix(FixIds.MoveGtaMods, FixIds.GpuDriver, FixIds.ResetGraphics, FixIds.CloseOverlays);
                    AddDriverAge(d, s);
                    list.Add(d);
                }
                else if (code.StartsWith("ERR_GFX_D3D_NO", StringComparison.Ordinal))
                {
                    var d = New("err-d3d-support", $"Grafikkarte unterstützt die nötige DirectX-Stufe nicht ({code})", Severity.Critical, Confidence.Medium, Evidence.Praxis,
                        "GTA V findet keine Grafikkarte mit passender DirectX-Unterstützung. Bei Laptops läuft das Spiel oft auf der schwachen Onboard-Grafik statt auf der richtigen Grafikkarte, oder der Treiber fehlt.")
                        .Because("Fehlercode " + code);
                    d.Step("Windows-Einstellungen → System → Anzeige → Grafik: für FiveM.exe und GTA5.exe „Hohe Leistung“ wählen.")
                     .Step("Installiere den Treiber deiner Grafikkarte direkt vom Hersteller.")
                     .Fix(FixIds.GpuDriver);
                    list.Add(d);
                }
                else if (code.StartsWith("ERR_GFX_", StringComparison.Ordinal))
                {
                    var d = New("err-gfx", $"Grafikfehler des Spiels ({code})", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Ein Grafikfehler von GTA V. Meist liegt es am Grafiktreiber, an Übertaktung oder an zu wenig Grafikspeicher bei Servern mit vielen Mods.")
                        .Because("Fehlercode " + code);
                    d.Step("Grafiktreiber aktualisieren, Übertaktung zurücknehmen.")
                     .Step("Grafikeinstellungen zurücksetzen und Texturqualität senken.")
                     .Step("Shader-Cache leeren.")
                     .Fix(FixIds.GpuDriver, FixIds.ResetGraphics, FixIds.ClearShaderCache);
                    AddDriverAge(d, s);
                    list.Add(d);
                }
                else if (code.StartsWith("ERR_MEM_", StringComparison.Ordinal))
                {
                    var d = New("err-mem", $"Dem Spiel ist der Speicher ausgegangen ({code})", Severity.Critical, Confidence.Medium, Evidence.Praxis,
                        "GTA V konnte keinen Speicher mehr reservieren. Auf FiveM-Servern mit vielen Add-on-Fahrzeugen, Kleidung und Karten ist das häufig – besonders mit wenig RAM oder ohne Auslagerungsdatei.")
                        .Because("Fehlercode " + code);
                    d.Step("Lass Windows die Auslagerungsdatei automatisch verwalten.")
                     .Step("Schließe speicherhungrige Programme (Browser mit vielen Tabs, Aufnahme-Software).")
                     .Step("Senke die Texturqualität und leere den FiveM-Cache.")
                     .Step("Passiert es nur auf einem Server: dem Server-Team melden, der Server streamt evtl. zu große Dateien.")
                     .Fix(FixIds.PagefileAuto, FixIds.ClearCache, FixIds.ResetGraphics);
                    list.Add(d);
                }
                else if (code.StartsWith("ERR_STR_", StringComparison.Ordinal))
                {
                    var d = New("err-streaming", $"Fehler beim Laden von Spielinhalten ({code})", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Das Spiel konnte Inhalte nicht laden. Oft ist der FiveM-Cache beschädigt oder der Server liefert fehlerhafte Dateien.")
                        .Because("Fehlercode " + code);
                    d.Step("FiveM-Cache leeren und neu verbinden.")
                     .Step("Nur auf einem Server? Dann dem Server-Team melden.")
                     .Fix(FixIds.ClearCache, FixIds.TestServer);
                    list.Add(d);
                }
                else if (code.StartsWith("ERR_FIL_", StringComparison.Ordinal))
                {
                    var d = New("err-files", $"Spieldateien beschädigt oder nicht lesbar ({code})", Severity.Critical, Confidence.Medium, Evidence.Praxis,
                        "GTA V konnte eine seiner Archivdateien nicht lesen. Meist sind Spieldateien beschädigt oder verändert (Mods im GTA-Ordner).")
                        .Because("Fehlercode " + code);
                    d.Step("Spieldateien in Steam, Epic oder dem Rockstar Launcher prüfen lassen.")
                     .Step("Mods aus dem GTA-V-Ordner entfernen.")
                     .Fix(FixIds.VerifyGame, FixIds.MoveGtaMods);
                    list.Add(d);
                }
                else if (code.StartsWith("ERR_AUD_", StringComparison.Ordinal))
                {
                    var d = New("err-audio", $"Audiofehler des Spiels ({code})", Severity.Warning, Confidence.Low, Evidence.Vermutung,
                        "Ein Fehler in der Audio-Ausgabe. Häufig hängen Audio-Zusatzprogramme (Nahimic, Sonic Studio) oder virtuelle Audiogeräte damit zusammen.")
                        .Because("Fehlercode " + code);
                    d.Step("Audio-Zusatzsoftware deaktivieren und ein normales Ausgabegerät als Standard wählen.");
                    list.Add(d);
                }
                else
                {
                    var d = New("err-other-" + code, $"Fehlercode des Spiels: {code}", Severity.Hint, Confidence.Low, Evidence.Vermutung,
                        "Zu diesem Code hat das Tool keine eigene Regel. Die Bereichskennung im Namen hilft beim Suchen (GFX = Grafik, MEM = Speicher, STR = Streaming, FIL = Dateien, NET = Netzwerk).")
                        .Because("Fehlercode " + code);
                    d.Step("Suche im Cfx-Forum (forum.cfx.re) nach dem genauen Code.").Fix(FixIds.ClearCache);
                    list.Add(d);
                }
            }
        }

        static void RuleFaultModule(CrashFacts f, SystemSnapshot s, ModuleEntry e, List<Diagnosis> list)
        {
            if (string.IsNullOrEmpty(f.FaultModule) || f.EarlyExitFromDump) return;
            string where = $"Absturzstelle: {f.Signature ?? f.FaultModule}";
            if (e == null)
            {
                var mod = f.FaultModuleInfo ?? f.LoadedModules.FirstOrDefault(m => m.Name == f.FaultModule) ?? f.UnloadedModules.FirstOrDefault(m => m.Name == f.FaultModule);
                bool thirdParty = mod != null && ModuleCatalog.IsThirdPartyPath(mod.Path, s.FiveMAppPath, s.GtaPath);
                var d = New("fault-unknown", $"Absturz in „{f.FaultModule}“", thirdParty ? Severity.Critical : Severity.Warning,
                        thirdParty ? Confidence.High : Confidence.Low, thirdParty ? Evidence.Praxis : Evidence.Vermutung,
                        thirdParty
                            ? $"Der Absturz passierte in einer DLL, die weder zu Windows noch zu FiveM oder GTA V gehört. Sie liegt unter:\n{mod.Path}\nDas Programm, zu dem sie gehört, hat sich ins Spiel eingeklinkt."
                            : "Dieses Modul kennt das Tool nicht. Ohne Dump lässt sich nicht sagen, zu welchem Programm es gehört.")
                    .Because(where);
                if (thirdParty)
                    d.Step("Finde über den Ordnerpfad heraus, zu welchem Programm die DLL gehört, und beende oder deinstalliere es testweise.")
                     .Step("Danach FiveM neu starten und prüfen, ob der Absturz weg ist.");
                else
                    d.Step("Suche nach dem DLL-Namen, um das zugehörige Programm zu finden.")
                     .Step("Lade zusätzlich den Crash-Dump (.dmp) ins Tool – dann sieht es den Pfad der DLL.");
                list.Add(d);
                return;
            }

            Diagnosis x;
            switch (e.Kind)
            {
                case ModuleKind.GpuNvidia:
                case ModuleKind.GpuAmd:
                case ModuleKind.GpuIntel:
                    var vendor = e.Kind == ModuleKind.GpuNvidia ? "NVIDIA" : e.Kind == ModuleKind.GpuAmd ? "AMD" : "Intel";
                    x = New("fault-gpu", $"Absturz im {vendor}-Grafiktreiber", Severity.Critical, Confidence.High, e.Evidence,
                        $"Das Spiel ist im Grafiktreiber abgestürzt ({f.FaultModule}). " +
                        (e.Evidence == Evidence.Belegt ? "FiveM benennt in diesem Fall selbst den Treiber als Ursache. " : "") +
                        "Meist hilft ein Treiber-Update; daneben kommen Übertaktung, Overlays und ein beschädigter Shader-Cache in Frage.")
                        .Because(where);
                    x.SourceNote = e.Evidence == Evidence.Belegt ? "FiveM-Quelltext, MiniDump.cpp (Schuldzuweisung im Dialog)" : null;
                    x.Step("Installiere den neuesten Grafiktreiber und starte den PC neu – so rät es auch FiveM.")
                     .Step("Hilft das nicht: Treiber sauber neu installieren (Option „Neuinstallation“/„Werkseinstellungen“).")
                     .Step("Nimm Übertaktung oder Undervolting der Grafikkarte zurück.")
                     .Step("Leere den Shader-Cache und schalte Overlays testweise ab.")
                     .Fix(FixIds.GpuDriver, FixIds.ClearShaderCache, FixIds.CloseOverlays);
                    AddDriverAge(x, s, vendor);
                    list.Add(x);
                    break;

                case ModuleKind.NvidiaFilter:
                    x = New("fault-nvppex", "NVIDIA-Spielfilter (Freestyle) bringen das Spiel zum Absturz", Severity.Critical, Confidence.High, Evidence.Belegt,
                        "Die NVIDIA-Spielfilter klinken sich in DirectX ein und vertragen sich nicht mit FiveM. FiveM blockiert die DLL inzwischen und nennt sie im Crash-Fenster.")
                        .Because(where);
                    x.SourceNote = "FiveM-Quelltext, MiniDump.cpp und FileMapping.Win32.cpp";
                    x.Step("Öffne die NVIDIA App → Einstellungen → Spielfilter und Fotomodus deaktivieren.")
                     .Step("FiveM neu starten.");
                    list.Add(x);
                    break;

                case ModuleKind.NvidiaCapture:
                    x = New("fault-nvcapture", "Das NVIDIA-Overlay (Aufnahme/ShadowPlay) ist beteiligt", Severity.Critical, Confidence.High, Evidence.Belegt,
                        "Die Aufnahme-DLL des NVIDIA-Overlays stand an der Absturzstelle. FiveM blockiert sie, weil sie stark mit dem Fehler ERR_GFX_D3D_INIT zusammenhängt.")
                        .Because(where);
                    x.SourceNote = "FiveM-Quelltext, FileMapping.Win32.cpp";
                    x.Step("NVIDIA App bzw. GeForce Experience → Einstellungen → In-Game-Overlay ausschalten.")
                     .Step("Instant Replay deaktivieren.");
                    list.Add(x);
                    break;

                case ModuleKind.Overlay:
                case ModuleKind.Recorder:
                case ModuleKind.Audio:
                case ModuleKind.VendorTool:
                    x = New("fault-" + e.Kind.ToString().ToLowerInvariant(), $"{e.Product} hat den Absturz ausgelöst", Severity.Critical, Confidence.High, e.Evidence,
                        $"Die DLL {f.FaultModule} gehört zu {e.Product}. Sie hängt sich in den Spielprozess ein und stand genau an der Absturzstelle." +
                        (string.IsNullOrEmpty(e.Note) ? "" : " " + e.Note))
                        .Because(where);
                    x.Step($"Beende {e.Product} vor dem Spielen oder schalte das Overlay/die Ingame-Funktion ab.")
                     .Step("Startet FiveM danach stabil, das Programm aktualisieren oder dauerhaft für FiveM ausschalten.");
                    if (e.Kind == ModuleKind.Audio) x.Step("Bei Nahimic/A-Volute: über „Apps & Features“ deinstallieren; der Dienst „Nahimic Service“ startet sonst immer wieder.");
                    x.Fix(FixIds.CloseOverlays);
                    list.Add(x);
                    break;

                case ModuleKind.Security:
                    x = New("fault-security", $"{e.Product} greift ins Spiel ein", Severity.Critical, Confidence.High, e.Evidence,
                        $"Die DLL {f.FaultModule} gehört zu {e.Product}, die sich in laufende Programme einklinkt." + (string.IsNullOrEmpty(e.Note) ? "" : " " + e.Note))
                        .Because(where);
                    x.Step("Lege für den FiveM-Ordner eine Ausnahme in der Sicherheitssoftware an oder schalte den Verhaltensschutz testweise ab.")
                     .Step("Bei Comodo empfiehlt FiveM die Deinstallation.");
                    list.Add(x);
                    break;

                case ModuleKind.AsiPlugin:
                    x = New("fault-asi", $"Ein Plugin ({f.FaultModule}) hat den Absturz verursacht", Severity.Critical, Confidence.High, Evidence.Belegt,
                        "Die Absturzstelle liegt in einer .asi-Datei. FiveM rät in diesem Fall selbst, die Datei aus dem Ordner „plugins“ zu entfernen.")
                        .Because(where);
                    x.SourceNote = "FiveM-Quelltext, MiniDump.cpp";
                    x.Step("Deaktiviere den Plugins-Ordner (das Tool benennt ihn nur um, nichts wird gelöscht).")
                     .Fix(FixIds.DisablePlugins);
                    list.Add(x);
                    break;

                case ModuleKind.GraphicsMod:
                    x = New("fault-graphicsmod", $"Grafik-Mod {e.Product} an der Absturzstelle", Severity.Critical, Confidence.High, Evidence.Praxis,
                        $"Der Absturz passierte in {f.FaultModule} ({e.Product}). Grafik-Mods hängen sich in DirectX und brechen nach FiveM- oder Spiel-Updates oft.")
                        .Because(where);
                    x.Step("Entferne die Mod aus dem GTA-V-Ordner und dem FiveM-Ordner „plugins“.")
                     .Fix(FixIds.MoveGtaMods, FixIds.DisablePlugins);
                    list.Add(x);
                    break;

                case ModuleKind.FiveMScripting:
                    x = New("fault-scripting", "Absturz in der Skriptlaufzeit (meist durch ein Server-Skript)", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        $"Der Absturz liegt in {f.FaultModule}, der Komponente, die die Lua-/JS-/C#-Skripte der Server ausführt. Häufig löst ein fehlerhaftes Skript des Servers das aus.")
                        .Because(where);
                    x.Step("Passiert es nur auf einem bestimmten Server? Dann gib dem Server-Team Crash-Signatur, Uhrzeit und was du gerade gemacht hast.")
                     .Step("Leere den FiveM-Cache.")
                     .Fix(FixIds.ClearCache, FixIds.TestServer);
                    list.Add(x);
                    break;

                case ModuleKind.FiveMStreaming:
                    x = New("fault-streaming", "Absturz beim Laden von Modellen und Texturen", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Der Absturz liegt im Streaming von FiveM – dort, wo Fahrzeuge, Kleidung und Karten des Servers geladen werden. Ursache sind oft fehlerhafte oder zu große Dateien des Servers, ein beschädigter Cache oder zu wenig Speicher.")
                        .Because(where);
                    x.Step("FiveM-Cache leeren und neu verbinden.")
                     .Step("Auslagerungsdatei automatisch verwalten lassen und Texturqualität senken.")
                     .Step("Tritt es immer an derselben Stelle auf (Garage, Kleidungsladen, Gebiet), dem Server-Team melden.")
                     .Fix(FixIds.ClearCache, FixIds.PagefileAuto, FixIds.TestServer);
                    list.Add(x);
                    break;

                case ModuleKind.FiveMNui:
                    x = New("fault-nui", "Absturz in der Spiel-Oberfläche (NUI/Chromium)", Severity.Warning, Confidence.Medium, e.Evidence,
                        "Abgestürzt ist der Chromium-Teil von FiveM, der die Menüs der Server anzeigt (Inventar, Handy, HUD). Auslöser sind meist eine fehlerhafte Server-Oberfläche oder der Grafiktreiber." +
                        (string.IsNullOrEmpty(f.CefConsoleTail) ? "" : "\n\nDas Crash-Zip enthält eine cef_console.txt – Auszug unter „Technische Details“."))
                        .Because(where);
                    x.SourceNote = "FiveM-Quelltext, MiniDump.cpp (cef_console.txt bei libcef-Abstürzen)";
                    x.Step("Leere den FiveM-Cache.")
                     .Step("Aktualisiere den Grafiktreiber.")
                     .Step("Stürzt es beim Öffnen eines bestimmten Menüs ab: dem Server-Team melden.")
                     .Fix(FixIds.ClearCache, FixIds.GpuDriver);
                    list.Add(x);
                    break;

                case ModuleKind.FiveMNet:
                    x = New("fault-net", "Absturz in der Netzwerk-Synchronisation", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Der Absturz liegt in dem Teil von FiveM, der Spieler, Fahrzeuge und Objekte mit dem Server abgleicht. Meist ist das ein Fehler im Spiel oder auf dem Server, nicht auf deinem PC.")
                        .Because(where);
                    x.Step("Starte FiveM vor jedem Serverwechsel komplett neu.")
                     .Step("Häuft es sich auf einem Server, dem Server-Team mit Uhrzeit melden.")
                     .Fix(FixIds.ClearCache, FixIds.TestServer);
                    list.Add(x);
                    break;

                case ModuleKind.FiveMVoice:
                    x = New("fault-voice", "Absturz im Sprachchat von FiveM", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Der Absturz liegt in der Sprachchat-Komponente. Häufig hängen Audiogeräte damit zusammen, die sich während des Spiels ändern (Headset ab- oder angesteckt), oder Audio-Zusatzsoftware.")
                        .Because(where);
                    x.Step("Wähle in Windows ein festes Standard-Aufnahme- und Wiedergabegerät; Headset vor dem Start anstecken.")
                     .Step("Audio-Zusatzsoftware (Nahimic, Sonic Studio, virtuelle Audiogeräte) testweise deaktivieren.");
                    list.Add(x);
                    break;

                case ModuleKind.FiveMProtection:
                    x = New("fault-adhesive", "Absturz in der Schutzkomponente von FiveM (adhesive.dll)", Severity.Warning, Confidence.Low, Evidence.Vermutung,
                        "adhesive.dll ist ein geschlossener Teil von FiveM. Ein Absturz dort lässt sich von außen kaum deuten; oft geht es um Konflikte mit Software, die sich in Prozesse einklinkt.")
                        .Because(where);
                    x.Step("Windows-Updates installieren, PC neu starten.")
                     .Step("Sicherheitssoftware von Drittanbietern und Tools, die sich in Prozesse einklinken, testweise abschalten.")
                     .Step("Mit Report-ID im Cfx-Forum melden.")
                     .Fix(FixIds.SupportZip);
                    list.Add(x);
                    break;

                case ModuleKind.RockstarLauncher:
                    x = New("fault-rockstar", "Absturz in der Rockstar-Anbindung (Social Club/Launcher)", Severity.Warning, Confidence.Low, Evidence.Vermutung,
                        "Der Absturz liegt in dem Teil, der GTA V mit dem Rockstar-Konto verbindet.")
                        .Because(where);
                    x.Step("Starte einmal den Rockstar Games Launcher (bzw. GTA V über Steam/Epic), melde dich an und lass Updates durchlaufen.")
                     .Step("Spieldateien prüfen lassen.")
                     .Fix(FixIds.VerifyGame);
                    list.Add(x);
                    break;

                case ModuleKind.Game:
                    x = New("fault-game", "Absturz im Spielcode von GTA V", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Die Absturzstelle liegt im Spiel selbst. Das sagt noch nicht, wer schuld ist: Häufig sind es Inhalte des Servers (Fahrzeuge, Karten, Kleidung), ein beschädigter Cache oder Mods.")
                        .Because(where);
                    x.Step("FiveM-Cache leeren.")
                     .Step("Starte FiveM vor jedem Serverwechsel komplett neu.")
                     .Step("Teste auf dem offiziellen Cfx-Testserver: läuft es dort stabil, liegt es am Server.")
                     .Step("Mods entfernen und Spieldateien prüfen lassen.")
                     .Fix(FixIds.ClearCache, FixIds.TestServer, FixIds.DisablePlugins, FixIds.VerifyGame);
                    if (!string.IsNullOrEmpty(f.LegacyHash) && !KnownCrashes.Names.ContainsKey(f.LegacyHash))
                        x.Step($"Suche im Cfx-Forum nach dem Crash-Namen „{f.LegacyHash}“ – gleiche Namen bedeuten dieselbe Absturzstelle (nur im selben Gamebuild vergleichbar).");
                    list.Add(x);
                    break;

                case ModuleKind.WindowsCore:
                    var third = ThirdPartyOnStack(f);
                    x = New("fault-windows", $"Absturz in einer Windows-Bibliothek ({f.FaultModule})", Severity.Hint, Confidence.Low, Evidence.Praxis,
                        "Windows-Bibliotheken sind fast nie selbst kaputt – sie sind nur die Stelle, an der ein Fehler aus einem anderen Teil auffällt (Speicherfehler, Ausnahme)." +
                        (third.Count > 0 ? " Auf dem Stack fand das Tool Fremdsoftware: " + string.Join(", ", third) + "." : ""))
                        .Because(where);
                    x.Step("FiveM-Cache leeren – ein Absturz in ntdll.dll kurz nach dem Spielstart verschwand in einem dokumentierten Fall genau dadurch.")
                     .Step("Overlays und Plugins testweise abschalten.")
                     .Step("Bleibt es: Windows-Systemdateien prüfen lassen.")
                     .Fix(FixIds.ClearCache, FixIds.CloseOverlays, FixIds.SystemFiles);
                    list.Add(x);
                    break;

                case ModuleKind.VcRuntime:
                    x = New("fault-vcruntime", "Absturz in der Microsoft-C++-Laufzeit", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        $"{f.FaultModule} ist Teil der Visual C++ Runtime. Oft hat sich das Programm dort selbst beendet (abort), seltener ist die Runtime beschädigt.")
                        .Because(where);
                    x.Step("Installiere die aktuelle Visual C++ Runtime (x64) von Microsoft neu.")
                     .Step("Danach PC neu starten.")
                     .Fix(FixIds.VcRedist, FixIds.SystemFiles);
                    list.Add(x);
                    break;

                case ModuleKind.DirectX:
                    var mod2 = f.FaultModuleInfo ?? f.LoadedModules.FirstOrDefault(m => m.Name == f.FaultModule);
                    if (mod2 != null && ModuleCatalog.IsThirdPartyPath(mod2.Path, s.FiveMAppPath, s.GtaPath))
                    {
                        x = New("fault-graphicsmod", $"Grafik-Mod an der Absturzstelle ({f.FaultModule})", Severity.Critical, Confidence.High, Evidence.Praxis,
                            $"{f.FaultModule} wurde nicht aus Windows geladen, sondern aus:\n{mod2.Path}\nDas ist fast immer ReShade, ENB oder ein ASI-Loader.")
                            .Because(where);
                        x.Step("Entferne die Datei aus dem Spiel- bzw. Plugin-Ordner.")
                         .Fix(FixIds.MoveGtaMods, FixIds.DisablePlugins);
                    }
                    else
                    {
                        x = New("fault-directx", $"Absturz in DirectX ({f.FaultModule})", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                            "Der Absturz liegt in DirectX. Fast immer steckt der Grafiktreiber, ein Overlay oder ein Grafik-Mod dahinter.")
                            .Because(where);
                        x.Step("Grafiktreiber aktualisieren.").Step("Overlays testweise abschalten.")
                         .Fix(FixIds.GpuDriver, FixIds.CloseOverlays, FixIds.MoveGtaMods);
                        AddDriverAge(x, s);
                    }
                    list.Add(x);
                    break;

                case ModuleKind.AudioSystem:
                    x = New("fault-audio", "Absturz in der Windows-Audioausgabe", Severity.Warning, Confidence.Low, Evidence.Praxis,
                        "Der Absturz liegt in der Audio-Schicht von Windows. Typisch sind Audiotreiber, Zusatzsoftware wie Nahimic und Geräte, die während des Spiels wechseln.")
                        .Because(where);
                    x.Step("Audiotreiber aktualisieren, Audio-Zusatzsoftware deaktivieren.")
                     .Step("Headset vor dem Start anstecken und als Standardgerät setzen.");
                    list.Add(x);
                    break;

                default:
                    x = New("fault-fivem", $"Absturz in einer FiveM-Komponente ({f.FaultModule})", Severity.Warning, Confidence.Low, Evidence.Praxis,
                        "Der Absturz liegt in FiveM selbst. Häufig hilft ein frischer Cache; FiveM aktualisiert sich bei jedem Start.")
                        .Because(where);
                    x.Step("FiveM-Cache leeren und FiveM neu starten.")
                     .Step("Mit Report-ID im Cfx-Forum melden, wenn es bleibt.")
                     .Fix(FixIds.ClearCache, FixIds.SupportZip);
                    list.Add(x);
                    break;
            }

            if (!string.IsNullOrEmpty(f.BlamedBy) && list.Count > 0)
                list[list.Count - 1].Because("FiveM selbst nennt: " + f.BlamedBy);
        }

        /// <summary>Mehrere Dumps mit derselben Absturzstelle machen die Diagnose zur Absturzstelle belastbarer.</summary>
        static void RuleRecurrence(CrashFacts f, List<Diagnosis> list)
        {
            if (f.OtherDumpCount == 0) return;
            var fault = list.FirstOrDefault(d => d.Id.StartsWith("fault-", StringComparison.Ordinal));
            if (f.OtherDumpsSameSignature > 0 && fault != null)
            {
                fault.Confidence = Confidence.High;
                fault.Because($"{f.OtherDumpsSameSignature} von {f.OtherDumpCount} weiteren Dumps zeigen dieselbe Absturzstelle");
            }
            else if (f.OtherDumpsSameSignature == 0)
            {
                list.Add(New("dumps-vary", "Die Abstürze passieren an verschiedenen Stellen", Severity.Hint, Confidence.Medium, Evidence.Vermutung,
                        $"Keiner der {f.OtherDumpCount} weiteren Dumps hat dieselbe Absturzstelle wie der neueste. Wechselnde Stellen sprechen eher für eine allgemeine Ursache: instabiler Arbeitsspeicher oder Übertaktung, zu wenig Speicher, beschädigter Cache oder Software, die sich einklinkt.")
                    .Because("Vergleich mehrerer Dumps")
                    .Step("Übertaktung (CPU, RAM-XMP/EXPO, GPU) testweise zurücknehmen.")
                    .Step("Auslagerungsdatei automatisch verwalten lassen, Cache leeren, Overlays beenden.")
                    .Fix(FixIds.PagefileAuto, FixIds.ClearCache, FixIds.CloseOverlays));
            }
        }

        static void RuleUnloaded(CrashFacts f, List<Diagnosis> list)
        {
            if (!f.FaultInUnloadedModule) return;
            var e = ModuleCatalog.Lookup(f.FaultModule);
            var d = New("fault-unloaded", $"Code einer bereits entladenen DLL wurde aufgerufen ({f.FaultModule})", Severity.Warning, Confidence.Medium, Evidence.Vermutung,
                "Die Absturzstelle liegt in einer DLL, die zu diesem Zeitpunkt schon wieder entladen war. Typisch für Programme, die sich kurz ins Spiel einklinken und wieder verschwinden (Overlays, Aufnahme-Tools)." +
                (e != null ? $" Die DLL gehört zu {e.Product}." : ""))
                .Because("Signatur mit „_unloaded“");
            d.Step("Overlays und Aufnahme-Tools vor dem Spielen beenden.").Fix(FixIds.CloseOverlays);
            list.Add(d);
        }

        static void RuleStackAndLoadedModules(CrashFacts f, SystemSnapshot s, List<Diagnosis> list)
        {
            var seen = new HashSet<string>();
            var stack = f.StackModuleInfos.Count > 0
                ? f.StackModuleInfos.Select(m => new { name = m.Name, mod = m }).ToList()
                : f.StackModules.Select(n => new { name = n, mod = (ModuleInfo)null }).ToList();
            foreach (var item in stack)
            {
                var name = item.name;
                if (item.mod != null ? ReferenceEquals(item.mod, f.FaultModuleInfo) : name == f.FaultModule) continue;
                var e = item.mod != null ? ModuleCatalog.Lookup(item.mod, s.FiveMAppPath, s.GtaPath) : ModuleCatalog.Lookup(name);
                if (e == null || !e.IsConflictSoftware || !seen.Add(e.Product)) continue;
                var d = New("stack-" + e.Product, $"{e.Product} war am Absturz beteiligt", Severity.Warning, Confidence.Medium, Evidence.Vermutung,
                    $"Auf dem Stack des abgestürzten Threads liegen Adressen aus {name} ({e.Product}). Das Programm lief also mitten in dem Ablauf, der abgestürzt ist." +
                    (string.IsNullOrEmpty(e.Note) ? "" : " " + e.Note))
                    .Because("Stack-Analyse des Dumps: " + name);
                var fix = FixFor(e, item.mod);
                d.Step(fix == FixIds.DisablePlugins ? $"Deaktiviere die Plugins ({name}) testweise und starte FiveM neu."
                     : fix == FixIds.MoveGtaMods ? $"Entferne {name} testweise aus dem Spielordner."
                     : $"Beende {e.Product} oder schalte dessen Ingame-Funktion ab und teste erneut.");
                d.Fix(fix);
                list.Add(d);
            }

            var groups = f.LoadedModules
                .Where(m => !ReferenceEquals(m, f.FaultModuleInfo))
                .Select(m => new { m, e = ModuleCatalog.Lookup(m, s.FiveMAppPath, s.GtaPath) })
                .Where(x => x.e != null && x.e.IsConflictSoftware && !seen.Contains(x.e.Product))
                .GroupBy(x => x.e.Product);
            foreach (var g in groups)
            {
                var e = g.First().e;
                var names = string.Join(", ", g.Select(x => x.m.Name).Distinct());
                var d = New("loaded-" + e.Product, $"{e.Product} war im Spiel geladen", Severity.Hint, Confidence.Low, e.Evidence,
                    $"{names} steckte{(g.Count() > 1 ? "n" : "")} im Spielprozess. Das muss nicht die Ursache sein, ist aber ein bekannter Störfaktor." +
                    (string.IsNullOrEmpty(e.Note) ? "" : " " + e.Note))
                    .Because("Modulliste des Dumps: " + names);
                d.Step($"Teste einmal ohne {e.Product}.");
                d.Fix(FixFor(e, g.First().m));
                list.Add(d);
            }
        }

        static string FixFor(ModuleEntry e, ModuleInfo m)
        {
            var inPlugins = m?.Path != null && m.Path.IndexOf("\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (e.Kind == ModuleKind.AsiPlugin || inPlugins) return FixIds.DisablePlugins;
            if (e.Kind == ModuleKind.GraphicsMod) return FixIds.MoveGtaMods;
            return FixIds.CloseOverlays;
        }

        static void RuleExceptionCode(CrashFacts f, SystemSnapshot s, List<Diagnosis> list)
        {
            if (!f.ExceptionCode.HasValue) return;
            uint code = f.ExceptionCode.Value;
            var why = "Ausnahmecode " + ExceptionCatalog.Describe(code);
            switch (code)
            {
                case 0xC0000374:
                    list.Add(New("ex-heap", "Speicherbeschädigung im Spiel (Heap Corruption)", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                            "Der Arbeitsspeicher des Spiels wurde beschädigt. Häufige Gründe: Mods und Programme, die sich einklinken, oder instabiler RAM (XMP/EXPO-Profil, Übertaktung).")
                        .Because(why)
                        .Step("Overlays und Plugins abschalten.")
                        .Step("Tritt es weiter auf: im BIOS das XMP/EXPO-Profil testweise ausschalten und die Windows-Speicherdiagnose laufen lassen (mdsched.exe).")
                        .Fix(FixIds.CloseOverlays, FixIds.DisablePlugins));
                    break;
                case 0xC000001D:
                case 0xC0000096:
                    list.Add(New("ex-illegal", "Der Prozessor konnte einen Befehl nicht ausführen", Severity.Warning, Confidence.Medium, Evidence.Vermutung,
                            "Das deutet auf eine instabile Übertaktung bzw. ein zu starkes Undervolting von CPU oder RAM hin – oder auf einen sehr alten Prozessor.")
                        .Because(why)
                        .Step("CPU- und RAM-Übertaktung bzw. Undervolting testweise zurücknehmen (BIOS-Standardwerte).")
                        .Step("BIOS des Mainboards aktualisieren."));
                    break;
                case 0xC0000006:
                    list.Add(New("ex-inpage", "Daten konnten nicht von der Festplatte gelesen werden", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                            "Windows konnte Daten nicht von der Festplatte nachladen. Typisch bei einem fehlerhaften oder fast vollen Laufwerk, bei Spielen auf externen Platten oder Problemen mit der Auslagerungsdatei.")
                        .Because(why)
                        .Step("Laufwerk prüfen: Rechtsklick auf das Laufwerk → Eigenschaften → Tools → Prüfen.")
                        .Step("Mindestens 20 GB auf dem Laufwerk von FiveM frei halten.")
                        .Fix(FixIds.PagefileAuto, FixIds.SystemFiles));
                    break;
                case 0xC00000FD:
                    list.Add(New("ex-stackoverflow", "Stapelüberlauf (endlose Wiederholung im Code)", Severity.Warning, Confidence.Low, Evidence.Vermutung,
                            "Ein Stapelüberlauf entsteht durch Code, der sich endlos selbst aufruft. Auf FiveM-Servern stecken oft fehlerhafte Skripte oder Assets dahinter.")
                        .Because(why)
                        .Step("Nur auf einem Server? Dann dem Server-Team melden.")
                        .Fix(FixIds.ClearCache, FixIds.TestServer));
                    break;
                case 0xC0000017:
                case 0xC000009A:
                case 0xE0000008:
                    list.Add(New("ex-oom", code == 0xE0000008 ? "Der FiveM-Oberfläche ist der Speicher ausgegangen" : "Windows hatte keinen Speicher mehr frei",
                            Severity.Critical, Confidence.Medium, code == 0xE0000008 ? Evidence.Belegt : Evidence.Praxis,
                            "Der Arbeitsspeicher war erschöpft. Mit wenig RAM oder ohne Auslagerungsdatei passiert das auf großen Servern schnell.")
                        .Because(why)
                        .Step("Auslagerungsdatei automatisch verwalten lassen.")
                        .Step("Speicherhungrige Programme schließen.")
                        .Fix(FixIds.PagefileAuto, FixIds.ClearCache));
                    break;
                case 0xC0000135:
                case 0xC0000142:
                case 0xC000007B:
                    list.Add(New("ex-dll", "Eine benötigte DLL fehlt oder ist beschädigt", Severity.Critical, Confidence.Medium, Evidence.Praxis,
                            "Windows konnte eine DLL nicht laden. Meist fehlt die Visual C++ Runtime oder sie ist beschädigt.")
                        .Because(why)
                        .Step("Visual C++ Runtime (x64) neu installieren.")
                        .Step("Systemdateien prüfen lassen.")
                        .Fix(FixIds.VcRedist, FixIds.SystemFiles));
                    break;
                default:
                    var d = New("ex-info", "Was technisch passiert ist", Severity.Info, Confidence.High, Evidence.Belegt, ExceptionCatalog.Describe(code) + ExceptionDetail(f))
                        .Because("Ausnahme aus dem Dump");
                    d.SourceNote = "Windows-Ausnahmecode";
                    list.Add(d);
                    break;
            }
        }

        static string ExceptionDetail(CrashFacts f)
        {
            if (f.ExceptionCode != 0xC0000005 || f.ExceptionParameters == null || f.ExceptionParameters.Length < 2) return "";
            var kind = f.ExceptionParameters[0] == 0 ? "lesen" : f.ExceptionParameters[0] == 1 ? "schreiben" : f.ExceptionParameters[0] == 8 ? "ausführen" : "zugreifen";
            var addr = f.ExceptionParameters[1];
            var text = $"\n\nDas Programm wollte an Adresse 0x{addr:X} {kind}.";
            if (addr < 0x10000) text += " Das ist eine Null-Adresse: ein Objekt, das es nicht (mehr) gibt – typisch für fehlende oder fehlerhafte Spielinhalte.";
            if (f.ExceptionParameters[0] == 8) text += " Ausführen an einer ungültigen Adresse deutet auf beschädigten Speicher oder fremde Hooks hin.";
            return text;
        }

        static void RuleCppException(CrashFacts f, List<Diagnosis> list)
        {
            if (string.IsNullOrEmpty(f.CppExceptionType)) return;
            bool oom = f.CppExceptionType.IndexOf("bad_alloc", StringComparison.OrdinalIgnoreCase) >= 0;
            var d = New("cpp-exception", oom ? "Speicher erschöpft (std::bad_alloc)" : $"Unbehandelte Ausnahme: {f.CppExceptionType}",
                    oom ? Severity.Critical : Severity.Warning, Confidence.Medium, Evidence.Belegt,
                    (oom ? "Das Programm konnte keinen Speicher mehr reservieren." : "FiveM ist an einer Ausnahme gescheitert, die nicht abgefangen wurde.") +
                    (string.IsNullOrEmpty(f.CppExceptionDetails) ? "" : "\n\nDetails: " + f.CppExceptionDetails))
                .Because("Meldung „unhandled exception (of type …)“");
            if (oom) d.Step("Auslagerungsdatei automatisch verwalten lassen und Programme schließen.").Fix(FixIds.PagefileAuto, FixIds.ClearCache);
            else d.Step("Suche im Cfx-Forum nach Typ und Details der Ausnahme.").Fix(FixIds.ClearCache, FixIds.SupportZip);
            list.Add(d);
        }

        static void RuleFatalMessage(CrashFacts f, List<Diagnosis> list)
        {
            if (string.IsNullOrEmpty(f.FatalErrorMessage) || f.DirectXFatal) return;
            var msg = f.FatalErrorMessage;
            var d = New("fatal-message", "FiveM meldet einen Fehler", Severity.Warning, Confidence.Medium, Evidence.Belegt,
                "FiveM hat das Spiel mit einer eigenen Meldung beendet:\n\n" + Shorten(msg, 500))
                .Because("Fenster „FiveM has encountered an error“");
            if (Regex.IsMatch(msg, @"game files|corrupt|Could not (find|open|load)", RegexOptions.IgnoreCase))
                d.Step("Spieldateien prüfen lassen.").Fix(FixIds.VerifyGame);
            if (Regex.IsMatch(msg, @"Steam|Epic|Rockstar|Social Club|ROS", RegexOptions.IgnoreCase))
                d.Step("Starte die Spieleplattform (Steam, Epic oder Rockstar Launcher) und melde dich dort an, bevor du FiveM startest.");
            if (Regex.IsMatch(msg, @"memory|allocate", RegexOptions.IgnoreCase))
                d.Step("Auslagerungsdatei automatisch verwalten lassen.").Fix(FixIds.PagefileAuto);
            if (d.Steps.Count == 0) d.Step("Lies die Meldung genau – oft steht die Lösung direkt darin. Sonst im Cfx-Forum nach dem ersten Satz suchen.");
            d.Fix(FixIds.ClearCache);
            list.Add(d);
        }

        static void RuleUptime(CrashFacts f, List<Diagnosis> list)
        {
            if (!f.Uptime.HasValue) return;
            var up = f.Uptime.Value;
            if (up.TotalSeconds < 90)
            {
                list.Add(New("uptime-early", $"Absturz schon {FormatDuration(up)} nach dem Start", Severity.Hint, Confidence.Medium, Evidence.Praxis,
                        "Ein Absturz so kurz nach dem Start deutet auf etwas hin, das beim Laden schiefgeht: beschädigter Cache, Plugins, Grafik-Mods oder Overlays.")
                    .Because("Prozessstart und Absturzzeit im Dump")
                    .Step("Cache leeren, Plugins deaktivieren, Overlays beenden – dann erneut starten.")
                    .Fix(FixIds.ClearCache, FixIds.DisablePlugins, FixIds.CloseOverlays));
            }
            else if (up.TotalHours >= 2)
            {
                list.Add(New("uptime-long", $"Absturz nach {FormatDuration(up)} Spielzeit", Severity.Hint, Confidence.Low, Evidence.Vermutung,
                        "Nach langer Spielzeit sammeln sich gestreamte Inhalte im Speicher. Stürzt es regelmäßig erst nach Stunden ab, ist Speicher der erste Verdacht.")
                    .Because("Prozessstart und Absturzzeit im Dump")
                    .Step("Auslagerungsdatei prüfen, Texturqualität senken, FiveM zwischendurch neu starten.")
                    .Fix(FixIds.PagefileAuto));
            }
        }

        static void RuleLog(CrashFacts f, List<Diagnosis> list)
        {
            if (f.Log == null) return;
            if (f.Log.ScriptErrors.Count > 0)
            {
                var d = New("log-script-errors", $"{f.Log.ScriptErrors.Count} Skriptfehler im Client-Log", Severity.Hint, Confidence.Low, Evidence.Praxis,
                        "Im Log stehen SCRIPT ERROR-Meldungen. Sie kommen von Ressourcen des Servers. Sie lassen das Spiel selten abstürzen, sind aber für das Server-Team wertvoll – besonders die letzte vor dem Absturz.\n\nLetzte Meldung:\n" +
                        Shorten(f.Log.ScriptErrors.Last(), 400))
                    .Because("Log: " + f.Log.SourceName);
                d.Step("Gib dem Server-Team die letzten Skriptfehler (Bericht kopieren).");
                list.Add(d);
            }
            if (f.Log.MentionsOutOfMemory)
            {
                list.Add(New("log-oom", "Das Log meldet Speichermangel", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Im Client-Log tauchen Hinweise auf erschöpften Speicher auf.")
                    .Because("Log: " + f.Log.SourceName)
                    .Step("Auslagerungsdatei automatisch verwalten lassen, Texturqualität senken.")
                    .Fix(FixIds.PagefileAuto, FixIds.ClearCache));
            }
        }

        /// <summary>Zustand des Spiels im Augenblick des Absturzes, aus der .gamelog neben dem Dump.</summary>
        static void RuleGameLog(CrashFacts f, List<Diagnosis> list)
        {
            var g = f.GameLog;
            if (g == null) return;
            var src = "Spielprotokoll: " + g.SourceName;

            var stream = Math.Max(g.StreamingVirtualRatio ?? 0, g.StreamingPhysicalRatio ?? 0);
            if (stream >= 0.9)
                list.Add(New("gamelog-streaming-full", "Der Streaming-Speicher des Spiels war voll", Severity.Warning, Confidence.Medium, Evidence.Vermutung,
                        $"Beim Absturz war der Speicher, in den GTA Modelle und Texturen lädt, zu {stream:P0} belegt (virtuell {g.StreamingVirtualUsedMb} von {g.StreamingVirtualAvailableMb} MB, physisch {g.StreamingPhysicalUsedMb} von {g.StreamingPhysicalAvailableMb} MB). Viele oder zu große Fahrzeuge, Kleidung und Karten des Servers füllen ihn.")
                    .Because(src)
                    .Step("Texturqualität im Spiel senken.")
                    .Step("Passiert es auf einem bestimmten Server oder an einem Ort mit vielen Mods: dem Server-Team melden.")
                    .Fix(FixIds.ClearCache, FixIds.ResetGraphics));

            if (g.PageFileFreeMb.HasValue && g.PageFileFreeMb.Value < 2048)
                list.Add(New("gamelog-commit-low", $"Beim Absturz waren nur noch {g.PageFileFreeMb} MB Auslagerungsspeicher frei", Severity.Critical, Confidence.Medium, Evidence.Praxis,
                        "Windows konnte dem Spiel kaum noch Speicher zusagen (RAM plus Auslagerungsdatei waren praktisch erschöpft). Dann scheitern Speicheranforderungen und das Spiel stürzt ab.")
                    .Because(src)
                    .Step("Auslagerungsdatei automatisch verwalten lassen und genug Platz auf dem Systemlaufwerk lassen.")
                    .Step("Speicherhungrige Programme beim Spielen schließen.")
                    .Fix(FixIds.PagefileAuto));
            else if (g.MemoryLoadPercent >= 92)
                list.Add(New("gamelog-memload", $"Arbeitsspeicher beim Absturz zu {g.MemoryLoadPercent} % belegt", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Der Arbeitsspeicher war fast voll. Das Spiel lagert dann aus und kann bei Speicherspitzen abstürzen.")
                    .Because(src)
                    .Step("Andere Programme schließen, Auslagerungsdatei automatisch verwalten lassen.")
                    .Fix(FixIds.PagefileAuto));

            if (g.FullPools.Count > 0 || g.FullStores.Count > 0)
            {
                var all = g.FullPools.Concat(g.FullStores).ToList();
                list.Add(New("gamelog-pool-full", "Interne Spiel-Speicherplätze waren voll", Severity.Warning, Confidence.Low, Evidence.Vermutung,
                        "Im Spielprotokoll sind Pools bzw. Asset-Speicher voll belegt: " + string.Join(", ", all.Take(6)) +
                        ". GTA hat für jede Objektart feste Obergrenzen; Server mit sehr vielen Objekten, Fahrzeugen oder Karten können sie sprengen.")
                    .Because(src)
                    .Step("Tritt es an einem bestimmten Ort oder bei einem Event auf: dem Server-Team mit diesen Pool-Namen melden."));
            }

            if (string.Equals(g.GameState, "Game Init", StringComparison.OrdinalIgnoreCase) && !(f.Uptime?.TotalSeconds < 90))
                list.Add(New("gamelog-init", "Absturz während das Spiel noch geladen hat", Severity.Hint, Confidence.Medium, Evidence.Praxis,
                        "Laut Spielprotokoll war GTA noch beim Starten („Game Init“). Abstürze beim Laden hängen oft an Plugins, Grafik-Mods, dem Cache oder beschädigten Spieldateien.")
                    .Because(src)
                    .Step("Plugins deaktivieren und den Cache leeren, dann erneut starten.")
                    .Fix(FixIds.DisablePlugins, FixIds.ClearCache, FixIds.VerifyGame));
        }

        static void RuleSystem(CrashFacts f, SystemSnapshot s, List<Diagnosis> list)
        {
            if (s == null || !s.Collected) return;
            double ramGb = s.TotalRamBytes / 1024.0 / 1024 / 1024;

            if (s.TotalRamBytes > 0 && ramGb < 7.5)
                list.Add(New("sys-ram-low", $"Nur {ramGb:0.#} GB Arbeitsspeicher", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                        "Für FiveM-Server mit vielen Mods sind 8 GB das absolute Minimum, 16 GB sind sinnvoll. Mit weniger stürzt das Spiel beim Nachladen leicht ab.")
                    .Because("Systemmessung").Step("Auslagerungsdatei automatisch verwalten lassen und beim Spielen alles andere schließen.").Fix(FixIds.PagefileAuto));
            else if (s.TotalRamBytes > 0 && ramGb < 15)
                list.Add(New("sys-ram-tight", $"{ramGb:0.#} GB Arbeitsspeicher sind knapp", Severity.Hint, Confidence.Low, Evidence.Praxis,
                        "Mit 8 GB läuft FiveM, auf großen Servern wird es aber eng. Browser, Discord und Aufnahme-Software teilen sich denselben Speicher.")
                    .Because("Systemmessung").Step("Beim Spielen Browser und andere große Programme schließen.").Fix(FixIds.PagefileAuto));

            if (s.PagefileAllocatedMb.HasValue && s.PagefileAllocatedMb.Value == 0 && s.PagefileAutomatic != true)
                list.Add(New("sys-no-pagefile", "Keine Auslagerungsdatei aktiv", Severity.Critical, Confidence.High, Evidence.Praxis,
                        "Ohne Auslagerungsdatei bricht das Spiel ab, sobald der Arbeitsspeicher voll ist – auch mit viel RAM. GTA V reserviert mehr Speicher, als es gleichzeitig nutzt.")
                    .Because("Systemmessung").Step("Lass Windows die Auslagerungsdatei automatisch verwalten (braucht Admin-Rechte und einen Neustart).").Fix(FixIds.PagefileAuto));
            else if (s.PagefileAutomatic == false && s.PagefileAllocatedMb.HasValue && s.PagefileAllocatedMb.Value < 8192 && ramGb < 32)
                list.Add(New("sys-small-pagefile", $"Auslagerungsdatei nur {s.PagefileAllocatedMb.Value / 1024.0:0.#} GB groß", Severity.Hint, Confidence.Medium, Evidence.Praxis,
                        "Eine fest eingestellte, kleine Auslagerungsdatei kann bei Speicherspitzen nicht mitwachsen.")
                    .Because("Systemmessung").Step("Auf „automatisch verwalten“ umstellen.").Fix(FixIds.PagefileAuto));

            foreach (var drive in s.Drives)
            {
                double freeGb = drive.FreeBytes / 1024.0 / 1024 / 1024;
                if (drive.TotalBytes <= 0) continue;
                if (freeGb < 10)
                    list.Add(New("sys-disk-" + drive.Root, $"Laufwerk {drive.Root} ({drive.Role}) fast voll: {freeGb:0.#} GB frei", Severity.Warning, Confidence.Medium, Evidence.Praxis,
                            "Mit wenig freiem Platz scheitern Cache-Downloads, Shader-Cache und die Auslagerungsdatei.")
                        .Because("Systemmessung").Step("Auf diesem Laufwerk mindestens 20 GB freimachen.").Fix(FixIds.ClearShaderCache));
            }

            foreach (var gpu in s.Gpus.Where(g => g.DriverDate.HasValue && g.Vendor != "Andere"))
            {
                var age = DateTime.UtcNow - gpu.DriverDate.Value;
                if (age.TotalDays > 240)
                    list.Add(New("sys-driver-old", $"Grafiktreiber ist {(int)(age.TotalDays / 30)} Monate alt", Severity.Hint, Confidence.Medium, Evidence.Praxis,
                            $"{gpu.Name}: Treiber {gpu.FriendlyDriverVersion ?? gpu.DriverVersion} vom {gpu.DriverDate.Value:dd.MM.yyyy}. Neue Treiber beheben regelmäßig Abstürze in GTA V und FiveM.")
                        .Because("Systemmessung").Step("Aktuellen Treiber vom Hersteller installieren.").Fix(FixIds.GpuDriver));
            }

            if (s.FiveMPlugins.Count > 0)
                list.Add(New("sys-plugins", $"{s.FiveMPlugins.Count} Datei(en) im FiveM-Ordner „plugins“", Severity.Hint, Confidence.Medium, Evidence.Belegt,
                        "Plugins (ReShade, ASI-Dateien) laufen mit im Spiel. FiveM nennt .asi-Plugins selbst als häufige Absturzursache.\n\n" + string.Join("\n", s.FiveMPlugins.Take(8)))
                    .Because("Ordner „plugins“").Step("Testweise deaktivieren – das Tool benennt den Ordner nur um.").Fix(FixIds.DisablePlugins));

            if (s.GtaGraphicsMods.Count > 0)
                list.Add(New("sys-gta-mods", "Mod-Dateien im GTA-V-Ordner", Severity.Warning, Confidence.Medium, Evidence.Belegt,
                        "Im Spielordner liegen Dateien, die nur Mods dort ablegen. FiveM prüft d3d11.dll, dxgi.dll und d3d10.dll bei DirectX-Fehlern selbst.\n\n" + string.Join("\n", s.GtaGraphicsMods.Take(10)))
                    .Because("GTA-V-Ordner").Step("In einen Sicherungsordner verschieben (rückgängig machbar).").Fix(FixIds.MoveGtaMods));

            var running = ProcessCatalog.Running(s).ToList();
            if (running.Count > 0)
            {
                var d = New("sys-overlays", "Programme laufen, die sich ins Spiel einklinken", Severity.Hint, Confidence.Low, Evidence.Praxis,
                        string.Join("\n", running.Select(r => $"• {r.Product}: {r.Hint}")))
                    .Because("laufende Prozesse");
                d.Step("Zum Testen beenden oder deren Overlay ausschalten.");
                if (running.Any(r => r.SafeToClose)) d.Fix(FixIds.CloseOverlays);
                list.Add(d);
            }

            if (!string.IsNullOrEmpty(s.UpdateChannel) && !s.UpdateChannel.Equals("production", StringComparison.OrdinalIgnoreCase) &&
                !s.UpdateChannel.Equals("prod", StringComparison.OrdinalIgnoreCase))
                list.Add(New("sys-channel", $"FiveM läuft auf dem Update-Kanal „{s.UpdateChannel}“", Severity.Hint, Confidence.Medium, Evidence.Belegt,
                        "Beta und Canary sind Testversionen von FiveM und können eigene Abstürze haben.")
                    .Because("CitizenFX.ini, [Game] UpdateChannel").Step("Auf den Release-Kanal zurückstellen.").Fix(FixIds.UpdateChannel));

            if (s.FiveMCacheBytes.HasValue && s.FiveMCacheBytes.Value > 30L * 1024 * 1024 * 1024)
                list.Add(New("sys-cache-big", $"FiveM-Cache ist {s.FiveMCacheBytes.Value / 1024.0 / 1024 / 1024:0} GB groß", Severity.Hint, Confidence.Low, Evidence.Praxis,
                        "Ein riesiger Cache ist nicht per se schlimm, enthält aber oft alte, beschädigte Downloads vieler Server.")
                    .Because("Ordnergröße").Step("Cache leeren – er wird beim nächsten Verbinden neu geladen.").Fix(FixIds.ClearCache));

            if (s.CrashDumpsLast7Days >= 5)
                list.Add(New("sys-many-crashes", $"{s.CrashDumpsLast7Days} Abstürze in den letzten 7 Tagen", Severity.Hint, Confidence.High, Evidence.Belegt,
                        "So viele Dumps deuten auf ein dauerhaftes Problem. Lade mehrere davon ins Tool: Wiederholt sich dieselbe Absturzstelle, ist die Ursache eindeutig.")
                    .Because("Ordner „crashes“").Step("Support-Paket erstellen und im Cfx-Forum oder beim Server-Team melden.").Fix(FixIds.SupportZip));
        }

        static void RuleServerOrClient(CrashFacts f, List<Diagnosis> list)
        {
            var d = New("server-or-client", "Server oder dein PC? So findest du es heraus", Severity.Info, Confidence.High, Evidence.Belegt,
                "Die FiveM-Doku rät: Läuft das Spiel auf dem offiziellen Cfx-Testserver ohne Absturz, liegt die Ursache recht oft beim Server, auf dem es abstürzt.")
                .Because("Allgemeiner Prüfschritt");
            d.SourceNote = "docs.fivem.net, Client issues";
            d.Step("Verbinde dich mit dem Cfx-Testserver (" + TestServerUrl + ") und spiele dort eine Weile.")
             .Step("Kein Absturz dort? Dann gib dem Server-Team Crash-Signatur" + (string.IsNullOrEmpty(f.LegacyHash) ? "" : ", Crash-Namen") + " und Uhrzeit.")
             .Step("Absturz auch dort? Dann liegt es an deinem PC – arbeite die Punkte oben ab.")
             .Fix(FixIds.TestServer);
            list.Add(d);
        }

        static void RuleFallback(CrashFacts f, List<Diagnosis> list)
        {
            var d = New("fallback", "Keine eindeutige Ursache erkennbar", Severity.Hint, Confidence.Low, Evidence.Praxis,
                "Aus den Daten lässt sich keine einzelne Ursache ableiten. Diese Reihenfolge löst die meisten FiveM-Abstürze, vom einfachsten Schritt an.")
                .Because("Keine starke Regel hat gegriffen");
            d.Step("FiveM-Cache leeren.")
             .Step("Grafiktreiber aktualisieren.")
             .Step("Overlays und Plugins abschalten.")
             .Step("Spieldateien prüfen lassen.")
             .Step("Bleibt es: Support-Paket erstellen und mit Report-ID im Cfx-Forum melden.");
            if (!f.HasDump) d.Step("Lade auch den Crash-Dump (.dmp) ins Tool – damit wird die Analyse deutlich genauer.");
            d.Fix(FixIds.ClearCache, FixIds.GpuDriver, FixIds.CloseOverlays, FixIds.VerifyGame, FixIds.SupportZip);
            list.Add(d);
        }

        // ---------------------------------------------------------------- Hilfen

        static Diagnosis New(string id, string title, Severity sev, Confidence conf, Evidence ev, string explanation) =>
            new Diagnosis { Id = id, Title = title, Severity = sev, Confidence = conf, Evidence = ev, Explanation = explanation };

        static void AddDriverAge(Diagnosis d, SystemSnapshot s, string vendor = null)
        {
            if (s == null) return;
            var gpu = vendor != null
                ? s.Gpus.FirstOrDefault(g => g.Vendor == vendor && g.DriverDate.HasValue)
                : s.Gpus.FirstOrDefault(g => g.Vendor != "Andere" && g.DriverDate.HasValue);
            if (gpu == null && vendor != null)
            {
                if (s.Collected && s.Gpus.Count > 0)
                    d.Because($"Auf diesem PC ist keine {vendor}-Grafikkarte eingebaut – stammt der Absturz von einem anderen PC?");
                return;
            }
            if (gpu == null) return;
            var months = (int)((DateTime.UtcNow - gpu.DriverDate.Value).TotalDays / 30);
            d.Because($"Dein Treiber: {gpu.FriendlyDriverVersion ?? gpu.DriverVersion} vom {gpu.DriverDate.Value:dd.MM.yyyy}" + (months >= 3 ? $" ({months} Monate alt)" : ""));
        }

        static List<string> ThirdPartyOnStack(CrashFacts f) =>
            (f.StackModuleInfos.Count > 0 ? f.StackModuleInfos.Select(m => ModuleCatalog.Lookup(m)) : f.StackModules.Select(ModuleCatalog.Lookup))
                .Where(e => e != null && e.IsConflictSoftware).Select(e => e.Product).Distinct().ToList();

        public static string FormatDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 120) return $"{(int)t.TotalSeconds} Sekunden";
            if (t.TotalMinutes < 120) return $"{(int)t.TotalMinutes} Minuten";
            return $"{t.TotalHours:0.#} Stunden".Replace('.', ',');
        }

        static string Shorten(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + " …";
    }
}
