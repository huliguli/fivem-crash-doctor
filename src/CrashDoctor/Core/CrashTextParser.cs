using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CrashDoctor.Core
{
    /// <summary>
    /// Liest den Text, den man im FiveM-Crash-Fenster mit Strg+C kopiert.
    /// Formate nachgelesen in citizenfx/fivem, code/client/launcher/MiniDump.cpp
    /// und der deutschen Übersetzung data/client/citizen/locales/de/LC_MESSAGES/cfx.po.
    /// </summary>
    public static class CrashTextParser
    {
        const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        const RegexOptions OptI = Opt | RegexOptions.IgnoreCase;

        // modul+OFFSET (so schreibt FiveM die Signatur), auch modul_unloaded+OFFSET
        static readonly Regex OffsetSigRx = new Regex(
            @"(?<mod>[A-Za-z0-9_\-\.]+?\.(?:dll|exe|asi|sys|drv))(?<unl>_unloaded)?\+(?:0x)?(?<off>[0-9A-Fa-f]{1,16})\b", OptI);

        // modul!symbol, optional mit (0xOFFSET) oder +0xOFFSET
        static readonly Regex SymbolSigRx = new Regex(
            @"(?<mod>[A-Za-z0-9_\-\.]+?\.(?:dll|exe))!(?<sym>[A-Za-z_~][\w:~<>`'\-\.$@]*)(?:\s*\(0x(?<off>[0-9A-Fa-f]+)\)|\+0x(?<off2>[0-9A-Fa-f]+))?", OptI);

        static readonly Regex CrashSignatureLineRx = new Regex(@"(?:Crash signature|Absturzsignatur)\s*:\s*(?<sig>[^\r\n]+)", OptI);
        static readonly Regex ErrorAtRx = new Regex(@"(?:An error at|Ein Fehler bei)\s+(?<sig>\S+)", OptI);
        static readonly Regex LegacyHashRx = new Regex(@"Legacy crash hash\s*:\s*(?<h>[a-z]+-[a-z]+-[a-z]+)", OptI);
        static readonly Regex ReportIdRx = new Regex(@"(?:Report ID|Meldung ID)\s*:\s*(?<id>[0-9A-Za-z][0-9A-Za-z\-]{5,})", OptI);
        static readonly Regex BuildRx = new Regex(@"(?:GTA5|FiveM)_b(?<b>\d{3,5})", OptI);
        static readonly Regex ErrCodeRx = new Regex(@"\bERR_[A-Z0-9_]{3,}\b", Opt);
        static readonly Regex DxErrorRx = new Regex(@"\b(?:DXGI_ERROR_[A-Z_]+|D3DERR_[A-Z_]+|D3D11_ERROR_[A-Z_]+)\b", Opt);
        static readonly Regex RemovedReasonRx = new Regex(@"GetDeviceRemovedReason returned\s+(?<r>[A-Za-z0-9_]+)", OptI);
        static readonly Regex ExTypeRx = new Regex(@"unhandled exception \(of type (?<t>[^)\r\n]+)\)", OptI);
        static readonly Regex ExDetailsRx = new Regex(@"Exception details\s*:\s*(?<d>[^\r\n]+)", OptI);
        static readonly Regex BlameRx = new Regex(@"FiveM crashed due to (?<b>[^\r\n]+?)\.?\s*(?:\r|\n|$)", OptI);
        static readonly Regex ExCodeRx = new Regex(@"(?:exception code|ExceptionCode|Ausnahmecode)\s*[:=]?\s*0x(?<c>[0-9A-Fa-f]{8})\b", OptI);
        static readonly Regex TripleWordRx = new Regex(@"\b(?<h>[a-z]+-[a-z]+-[a-z]+)\b", Opt);
        static readonly Regex SectionRx = new Regex(@"^\[(?<name>[^\]\r\n]{2,40})\]\s*$", Opt | RegexOptions.Multiline);
        static readonly Regex LogLineRx = new Regex(@"^\[\s*\d+\]\s", Opt | RegexOptions.Multiline);
        static readonly Regex GraphicsModListRx = new Regex(
            @"graphics mods were found in your GTA V installation:\s*(?<list>(?:\s*-\s*[^\r\n]+)+)", OptI);

        /// <summary>
        /// Wortliste der „Legacy crash hashes“ (humanhash, 256 Wörter), wörtlich aus
        /// citizenfx/fivem code/client/launcher/CrashWordList.h. Damit lassen sich Crash-Namen
        /// auch dann sicher erkennen, wenn nur die drei Wörter irgendwo im Text stehen.
        /// </summary>
        public static readonly HashSet<string> CrashWords = new HashSet<string>(new[]
        {
            "ack","alabama","alanine","alaska","alpha","angel","apart","april","arizona","arkansas","artist","asparagus","aspen","august","autumn",
            "avocado","bacon","bakerloo","batman","beer","berlin","beryllium","black","blossom","blue","bluebird","bravo","bulldog","burger",
            "butter","california","carbon","cardinal","carolina","carpet","cat","ceiling","charlie","chicken","coffee","cola","cold","colorado",
            "comet","connecticut","crazy","cup","dakota","december","delaware","delta","diet","don","double","early","earth","east","echo",
            "edward","eight","eighteen","eleven","emma","enemy","equal","failed","fanta","fifteen","fillet","finch","fish","five","fix",
            "floor","florida","football","four","fourteen","foxtrot","freddie","friend","fruit","gee","georgia","glucose","golf","green","grey",
            "hamper","happy","harry","hawaii","helium","high","hot","hotel","hydrogen","idaho","illinois","india","indigo","ink","iowa",
            "island","item","jersey","jig","johnny","juliet","july","jupiter","kansas","kentucky","kilo","king","kitten","lactose","lake","lamp",
            "lemon","leopard","lima","lion","lithium","london","louisiana","low","magazine","magnesium","maine","mango","march","mars",
            "maryland","massachusetts","may","mexico","michigan","mike","minnesota","mirror","mississippi","missouri","mobile","mockingbird",
            "monkey","montana","moon","mountain","muppet","music","nebraska","neptune","network","nevada","nine","nineteen","nitrogen","north",
            "november","nuts","october","ohio","oklahoma","one","orange","oranges","oregon","oscar","oven","oxygen","papa","paris","pasta",
            "pennsylvania","pip","pizza","pluto","potato","princess","purple","quebec","queen","quiet","red","river","robert","robin","romeo",
            "rugby","sad","salami","saturn","september","seven","seventeen","shade","sierra","single","sink","six","sixteen","skylark","snake",
            "social","sodium","solar","south","spaghetti","speaker","spring","stairway","steak","stream","summer","sweet","table","tango","ten",
            "tennessee","tennis","texas","thirteen","three","timing","triple","twelve","twenty","two","uncle","undress","uniform","uranus","utah",
            "vegan","venus","vermont","victor","video","violet","virginia","washington","west","whiskey","white","william","winner","winter",
            "wisconsin","wolfram","wyoming","xray","yankee","yellow","zebra","zulu"
        }, StringComparer.Ordinal);

        public static bool LooksLikeLog(string text) =>
            !string.IsNullOrEmpty(text) && LogLineRx.Matches(text).Count >= 3;

        public static void Parse(string text, CrashFacts facts)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Replace("\r\n", "\n");

            var sections = SplitSections(text);
            string Section(string a, string b = null)
            {
                foreach (var key in new[] { a, b })
                    if (key != null && sections.TryGetValue(key, out var v)) return v;
                return null;
            }

            string windowTitle = Section("window title", "fenstertitel");
            string mainInstruction = Section("main instruction", "hauptanweisung");
            string content = Section("content", "inhalt") ?? text;

            if (ErrorAtRx.IsMatch(text) || CrashSignatureLineRx.IsMatch(text) || LegacyHashRx.IsMatch(text) ||
                text.IndexOf("caused FiveM to stop working", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("nicht mehr funktioniert", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                facts.DialogRecognized = true;
            }

            if (text.IndexOf("Absturzsignatur", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Ein Fehler bei", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Meldung ID", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                facts.DialogIsGerman = true;
            }

            // 1. Signatur: „Crash signature:“ ist die genaueste Stelle, dann „An error at“, dann der Fenstertitel.
            string sig = MatchGroup(CrashSignatureLineRx, text, "sig")?.Trim();
            if (string.IsNullOrEmpty(sig)) sig = MatchGroup(ErrorAtRx, text, "sig")?.Trim();
            if (string.IsNullOrEmpty(sig) && mainInstruction != null && (OffsetSigRx.IsMatch(mainInstruction) || SymbolSigRx.IsMatch(mainInstruction)))
                sig = mainInstruction.Trim();
            if (string.IsNullOrEmpty(sig) && windowTitle != null)
            {
                var t = windowTitle.Trim();
                if (t.StartsWith("Error ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(6);
                else if (t.StartsWith("Fehler ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7);
                if (OffsetSigRx.IsMatch(t) || SymbolSigRx.IsMatch(t)) sig = t;
            }
            if (!string.IsNullOrEmpty(sig))
            {
                sig = sig.TrimEnd('.', ' ');
                ApplySignature(sig, facts);
            }
            else
            {
                // Kein Dialog-Format: irgendein modul+offset im Text (z. B. aus Discord kopiert)
                var any = OffsetSigRx.Match(text);
                if (any.Success) ApplySignature(any.Value, facts);
            }

            // 2. Crash-Name
            var legacy = MatchGroup(LegacyHashRx, text, "h");
            if (!string.IsNullOrEmpty(legacy)) facts.LegacyHash = legacy.ToLowerInvariant();
            else if (string.IsNullOrEmpty(facts.LegacyHash))
            {
                foreach (Match m in TripleWordRx.Matches(text.ToLowerInvariant()))
                {
                    var parts = m.Groups["h"].Value.Split('-');
                    if (parts.Length == 3 && parts.All(CrashWords.Contains)) { facts.LegacyHash = m.Groups["h"].Value; break; }
                }
            }

            // 3. Report-ID (nicht „... [uploading]“)
            var rid = MatchGroup(ReportIdRx, text, "id");
            if (!string.IsNullOrEmpty(rid)) facts.ReportId = rid;

            // 4. Gamebuild
            if (!facts.GameBuild.HasValue)
            {
                var b = MatchGroup(BuildRx, text, "b");
                if (int.TryParse(b, out var build)) facts.GameBuild = build;
            }

            // 5. Fehlercodes des Spiels und von DirectX
            foreach (Match m in ErrCodeRx.Matches(text)) AddUnique(facts.ErrorCodes, m.Value);
            foreach (Match m in DxErrorRx.Matches(text)) AddUnique(facts.DirectXErrors, m.Value);
            var reason = MatchGroup(RemovedReasonRx, text, "r");
            if (!string.IsNullOrEmpty(reason)) facts.DeviceRemovedReason = reason;
            if (text.IndexOf("DirectX encountered an unrecoverable error", StringComparison.OrdinalIgnoreCase) >= 0)
                facts.DirectXFatal = true;
            var mods = GraphicsModListRx.Match(text);
            if (mods.Success)
            {
                var list = mods.Groups["list"].Value.Split('\n').Select(l => l.Trim().TrimStart('-').Trim()).Where(l => l.Length > 0);
                facts.GraphicsModsInMessage = string.Join(", ", list);
            }
            else if (Regex.IsMatch(text, @"unrecoverable error \((R\+E|R|E|M)\)", Opt))
            {
                var tag = Regex.Match(text, @"unrecoverable error \((?<t>R\+E|R|E|M)\)", Opt).Groups["t"].Value;
                facts.GraphicsModsInMessage = tag == "R+E" ? "ReShade, ENBSeries" : tag == "R" ? "ReShade" : tag == "E" ? "ENBSeries" : "Grafik-Mod";
            }

            // 6. Sonderfälle des Dialogs
            if (text.IndexOf("integrity check failed", StringComparison.OrdinalIgnoreCase) >= 0)
                facts.IntegrityCheckFailed = true;
            if (text.IndexOf("early-exit trap", StringComparison.OrdinalIgnoreCase) >= 0)
                facts.EarlyExitTrap = true;

            var blame = MatchGroup(BlameRx, text, "b");
            if (!string.IsNullOrEmpty(blame)) facts.BlamedBy = blame.Trim();

            var exType = MatchGroup(ExTypeRx, text, "t");
            if (!string.IsNullOrEmpty(exType)) facts.CppExceptionType = exType.Trim();
            var exDetails = MatchGroup(ExDetailsRx, text, "d");
            if (!string.IsNullOrEmpty(exDetails)) facts.CppExceptionDetails = exDetails.Trim();

            if (!facts.ExceptionCode.HasValue)
            {
                var c = MatchGroup(ExCodeRx, text, "c");
                if (uint.TryParse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) facts.ExceptionCode = code;
                else
                {
                    foreach (var kv in ExceptionCatalog.SymbolicNames)
                        if (text.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) { facts.ExceptionCode = kv.Value; break; }
                }
            }

            // Getrennt statt abgestürzt (FiveM zeigt dann „Disconnected“)
            if (windowTitle != null && windowTitle.Trim().Equals("Disconnected", StringComparison.OrdinalIgnoreCase))
                facts.DisconnectMessage = (content ?? "").Trim();
            else if (text.Contains("Ошибка (Error)"))
                facts.DisconnectMessage = ExtractAfter(text, "Ошибка (Error)");

            // Fataler Fehler mit eigener Meldung („FiveM has encountered an error“)
            if (string.IsNullOrEmpty(facts.FatalErrorMessage) &&
                text.IndexOf("has encountered an error", StringComparison.OrdinalIgnoreCase) >= 0 && string.IsNullOrEmpty(facts.BlamedBy))
            {
                var msg = (content ?? text).Trim();
                if (msg.Length > 600) msg = msg.Substring(0, 600) + " …";
                facts.FatalErrorMessage = msg;
            }

            // Stacktrace (nur bei symbolischen Abstürzen vorhanden)
            var stIdx = text.IndexOf("Stack trace:", StringComparison.OrdinalIgnoreCase);
            if (stIdx >= 0)
            {
                foreach (var line in text.Substring(stIdx + 12).Split('\n'))
                {
                    var l = line.Trim();
                    if (l.Length == 0) { if (facts.StackTraceLines.Count > 0) break; continue; }
                    if (l.StartsWith("[")) break;
                    facts.StackTraceLines.Add(l);
                    var sm = SymbolSigRx.Match(l);
                    var om = OffsetSigRx.Match(l);
                    var mod = sm.Success ? sm.Groups["mod"].Value : om.Success ? om.Groups["mod"].Value : null;
                    if (mod != null) AddUnique(facts.StackModules, mod.ToLowerInvariant());
                    if (facts.StackTraceLines.Count >= 40) break;
                }
            }

            if (LooksLikeLog(text))
            {
                var log = LogParser.Parse(text, "eingefügter Text");
                if (facts.Log == null) facts.Log = log;
            }
        }

        /// <summary>Zerlegt eine Signatur wie „GTA5_b3095.exe+16B5F3A“ oder „gta-core-five.dll!rage::foo (0x12)“.</summary>
        public static void ApplySignature(string sig, CrashFacts facts)
        {
            if (string.IsNullOrWhiteSpace(sig)) return;
            sig = sig.Trim();
            var om = OffsetSigRx.Match(sig);
            var sm = SymbolSigRx.Match(sig);
            if (om.Success && (!sm.Success || om.Index <= sm.Index))
            {
                facts.Signature = om.Value;
                facts.FaultModule = om.Groups["mod"].Value.ToLowerInvariant();
                facts.FaultInUnloadedModule = om.Groups["unl"].Success;
                if (ulong.TryParse(om.Groups["off"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var off)) facts.FaultOffset = off;
            }
            else if (sm.Success)
            {
                facts.Signature = sm.Value;
                facts.FaultModule = sm.Groups["mod"].Value.ToLowerInvariant();
                facts.FaultSymbol = sm.Groups["sym"].Value;
                var offText = sm.Groups["off"].Success ? sm.Groups["off"].Value : sm.Groups["off2"].Value;
                if (ulong.TryParse(offText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var off)) facts.FaultOffset = off;
            }
            else
            {
                facts.Signature = sig.Length > 160 ? sig.Substring(0, 160) : sig;
            }

            var b = MatchGroup(BuildRx, sig, "b");
            if (int.TryParse(b, out var build)) facts.GameBuild = build;
        }

        static Dictionary<string, string> SplitSections(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matches = SectionRx.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int start = m.Index + m.Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                var name = m.Groups["name"].Value.Trim().ToLowerInvariant();
                if (!result.ContainsKey(name)) result[name] = text.Substring(start, end - start).Trim();
            }
            return result;
        }

        static string ExtractAfter(string text, string marker)
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            var rest = text.Substring(i + marker.Length);
            var c = rest.IndexOf("[Content]", StringComparison.OrdinalIgnoreCase);
            if (c >= 0) rest = rest.Substring(c + 9);
            var cut = rest.IndexOf("\n[", StringComparison.Ordinal);
            if (cut >= 0) rest = rest.Substring(0, cut);
            return rest.Trim();
        }

        static string MatchGroup(Regex rx, string text, string group)
        {
            var m = rx.Match(text);
            return m.Success ? m.Groups[group].Value : null;
        }

        internal static void AddUnique(List<string> list, string value)
        {
            if (!string.IsNullOrEmpty(value) && !list.Contains(value)) list.Add(value);
        }
    }

    /// <summary>Liest Client-Logs (CitizenFX_log_*.log). Heuristisch: zieht Fehlerzeilen und das Ende heraus.</summary>
    public static class LogParser
    {
        static readonly Regex ColorRx = new Regex(@"\^[0-9]", RegexOptions.Compiled);
        static readonly Regex ErrorWordRx = new Regex(@"\b(error|failed|failure|exception|fatal|crash(?:ed)?|couldn't|could not|unable to)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex NoiseRx = new Regex(@"(0 errors|no error|error_pickup|ErrorHandler|crashometry|errorCode=0\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex ServerRx = new Regex(@"cfx\.re/join/[a-z0-9]{4,8}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex OomRx = new Regex(@"out of memory|ERR_MEM_|bad_alloc|ran out of memory", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static LogFacts Parse(string text, string sourceName)
        {
            var facts = new LogFacts { SourceName = sourceName };
            if (string.IsNullOrEmpty(text)) return facts;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            facts.LineCount = lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = Clean(lines[i]);
                if (line.Length == 0) continue;

                if (line.IndexOf("SCRIPT ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var block = line;
                    for (int k = 1; k <= 2 && i + k < lines.Length; k++)
                    {
                        var next = Clean(lines[i + k]);
                        if (next.Length == 0 || next.IndexOf("SCRIPT ERROR", StringComparison.OrdinalIgnoreCase) >= 0) break;
                        block += "\n   " + next;
                    }
                    Keep(facts.ScriptErrors, block, 25);
                }
                else if (ErrorWordRx.IsMatch(line) && !NoiseRx.IsMatch(line))
                {
                    Keep(facts.ErrorLines, line, 30);
                }

                foreach (Match m in ServerRx.Matches(line)) CrashTextParser.AddUnique(facts.Servers, m.Value.ToLowerInvariant());
                if (OomRx.IsMatch(line)) facts.MentionsOutOfMemory = true;
            }

            foreach (var raw in lines.Reverse())
            {
                var line = Clean(raw);
                if (line.Length == 0) continue;
                facts.TailLines.Insert(0, line);
                if (facts.TailLines.Count >= 40) break;
            }
            return facts;
        }

        static string Clean(string line)
        {
            line = ColorRx.Replace(line ?? "", "").TrimEnd();
            if (line.Length > 400) line = line.Substring(0, 400) + " …";
            return line;
        }

        // behält die letzten N Einträge (das Ende des Logs liegt am nächsten am Absturz)
        static void Keep(List<string> list, string value, int max)
        {
            list.Remove(value);
            list.Add(value);
            if (list.Count > max) list.RemoveAt(0);
        }
    }
}
