using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CrashDoctor.Core
{
    /// <summary>Werte aus der „.dmp.gamelog“, die FiveM neben den Dump legt (GTA-eigener Debug-Block „###### DEBUG INFO ######“).</summary>
    public sealed class GameLogFacts
    {
        public string SourceName { get; set; }
        public string GameState { get; set; }            // z. B. „Game Init“, „Game Running“
        public string Version { get; set; }              // z. B. „3570.0-dev_ng_Live“
        public int? StreamingPhysicalUsedMb { get; set; }
        public int? StreamingPhysicalAvailableMb { get; set; }
        public int? StreamingVirtualUsedMb { get; set; }
        public int? StreamingVirtualAvailableMb { get; set; }
        public int? MemoryLoadPercent { get; set; }
        public long? PhysicalTotalMb { get; set; }
        public long? PhysicalFreeMb { get; set; }
        public long? PageFileTotalMb { get; set; }
        public long? PageFileFreeMb { get; set; }
        public List<string> FullPools { get; } = new List<string>();   // „Name (benutzt/Größe)“
        public List<string> FullStores { get; } = new List<string>();

        public double? StreamingVirtualRatio =>
            StreamingVirtualUsedMb.HasValue && StreamingVirtualAvailableMb > 0 ? (double)StreamingVirtualUsedMb / StreamingVirtualAvailableMb : (double?)null;
        public double? StreamingPhysicalRatio =>
            StreamingPhysicalUsedMb.HasValue && StreamingPhysicalAvailableMb > 0 ? (double)StreamingPhysicalUsedMb / StreamingPhysicalAvailableMb : (double?)null;
    }

    public static class GameLogParser
    {
        static readonly Regex StateRx = new Regex(@"^Game State\s*:\s*(?<v>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex VersionRx = new Regex(@"^Version\s*:\s*(?<v>\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex StreamPhysRx = new Regex(@"Physical\s*:\s*used\((?<u>\d+)Mb\)\s*-\s*available\((?<a>\d+)Mb\)", RegexOptions.Compiled);
        static readonly Regex StreamVirtRx = new Regex(@"Virtual\s*:\s*used\((?<u>\d+)Mb\)\s*-\s*available\((?<a>\d+)Mb\)", RegexOptions.Compiled);
        static readonly Regex MemRx = new Regex(@"^\s*(?<k>Memory Load|Physical Total|Physical Free|PageFile Total|PageFile Free)\s*:\s*\(\s*(?<v>\d+)(?:%|Mb)\)", RegexOptions.Multiline | RegexOptions.Compiled);
        // Pool-Zeile: Name, Adresse, Elementgröße, Größe, belegt
        static readonly Regex PoolRx = new Regex(@"^(?<n>[A-Za-z_][\w:<>]*), [0-9A-Fa-f]{8,16}, (?<s>\d+), (?<size>\d+), (?<used>\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        // Asset-Stores stehen ohne Zeilenumbruch hintereinander: „CutSceneStore, 1400, 663ScaleformStore, 852, 808…“
        static readonly Regex StoreRx = new Regex(@"(?<n>[A-Z][A-Za-z]*?(?:Store|Scripts)), (?<size>\d+), (?<used>\d+)", RegexOptions.Compiled);

        /// <summary>Winzige feste Speicher wie „TextStore (1/1)“ sind in jedem Protokoll voll – erst ab dieser Größe zählt „voll“ (an echten Protokollen geprüft).</summary>
        const int MinPoolSize = 16;

        public static bool LooksLikeGameLog(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf("###### DEBUG INFO ######", StringComparison.Ordinal) >= 0;

        public static GameLogFacts Parse(string text, string sourceName)
        {
            var g = new GameLogFacts { SourceName = sourceName };
            if (string.IsNullOrEmpty(text)) return g;
            text = text.Replace("\r\n", "\n");

            var m = StateRx.Match(text);
            if (m.Success) g.GameState = m.Groups["v"].Value.Trim();
            m = VersionRx.Match(text);
            if (m.Success) g.Version = m.Groups["v"].Value.Trim();

            m = StreamPhysRx.Match(text);
            if (m.Success) { g.StreamingPhysicalUsedMb = Int(m, "u"); g.StreamingPhysicalAvailableMb = Int(m, "a"); }
            m = StreamVirtRx.Match(text);
            if (m.Success) { g.StreamingVirtualUsedMb = Int(m, "u"); g.StreamingVirtualAvailableMb = Int(m, "a"); }

            foreach (Match mm in MemRx.Matches(text))
            {
                var v = long.Parse(mm.Groups["v"].Value, CultureInfo.InvariantCulture);
                switch (mm.Groups["k"].Value)
                {
                    case "Memory Load": g.MemoryLoadPercent = (int)v; break;
                    case "Physical Total": g.PhysicalTotalMb = v; break;
                    case "Physical Free": g.PhysicalFreeMb = v; break;
                    case "PageFile Total": g.PageFileTotalMb = v; break;
                    case "PageFile Free": g.PageFileFreeMb = v; break;
                }
            }

            var poolSection = Section(text, "POOL INFO");
            if (poolSection != null)
                foreach (Match pm in PoolRx.Matches(poolSection))
                {
                    var size = long.Parse(pm.Groups["size"].Value, CultureInfo.InvariantCulture);
                    var used = long.Parse(pm.Groups["used"].Value, CultureInfo.InvariantCulture);
                    var entry = $"{pm.Groups["n"].Value} ({used}/{size})";
                    if (size >= MinPoolSize && used >= size && !g.FullPools.Contains(entry)) g.FullPools.Add(entry);
                }

            var storeSection = Section(text, "ASSET STORES INFO");
            if (storeSection != null)
                foreach (Match sm in StoreRx.Matches(storeSection))
                {
                    var size = long.Parse(sm.Groups["size"].Value, CultureInfo.InvariantCulture);
                    var used = long.Parse(sm.Groups["used"].Value, CultureInfo.InvariantCulture);
                    var entry = $"{sm.Groups["n"].Value} ({used}/{size})";
                    if (size >= MinPoolSize && used >= size && !g.FullStores.Contains(entry)) g.FullStores.Add(entry);
                }
            return g;
        }

        static string Section(string text, string name)
        {
            var start = text.IndexOf("============ " + name + " ============", StringComparison.Ordinal);
            if (start < 0) return null;
            var next = text.IndexOf("\n============ ", start + name.Length + 26, StringComparison.Ordinal);
            return next < 0 ? text.Substring(start) : text.Substring(start, next - start);
        }

        static int Int(Match m, string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
    }
}
