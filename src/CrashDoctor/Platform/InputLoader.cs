using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CrashDoctor.Core;

namespace CrashDoctor.Platform
{
    /// <summary>Liest eingefügten Text und Dateien (.dmp, CfxCrashDump-Zip, Logs) in CrashFacts ein.</summary>
    public static class InputLoader
    {
        public static readonly string[] SupportedExtensions = { ".dmp", ".zip", ".log", ".txt", ".json", ".gamelog" };
        const long MaxInMemoryDump = 512L * 1024 * 1024;
        const long MaxTextFile = 32L * 1024 * 1024;

        sealed class DumpCandidate
        {
            public MinidumpInfo Info; public string Name; public DateTime Sort; public GameLogFacts GameLog;
        }

        public static CrashFacts Load(string pastedText, IEnumerable<string> files)
        {
            var f = new CrashFacts();
            if (!string.IsNullOrWhiteSpace(pastedText))
            {
                f.Sources.Add("eingefügter Text");
                CrashTextParser.Parse(pastedText, f);
            }

            var dumps = new List<DumpCandidate>();
            foreach (var path in files ?? Enumerable.Empty<string>())
            {
                try { LoadFile(path, f, dumps); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is NotSupportedException)
                {
                    f.Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    AppLog.Write($"Datei {path} nicht lesbar: {ex}");
                }
            }

            if (dumps.Count > 0)
            {
                var ordered = dumps.OrderByDescending(d => d.Sort).ToList();
                var primary = ordered[0];
                CrashMerger.ApplyDump(primary.Info, primary.Name, f);
                if (primary.GameLog != null) { f.GameLog = primary.GameLog; f.Sources.Add("Spielprotokoll: " + primary.GameLog.SourceName); }
                f.OtherDumpCount = ordered.Count - 1;
                f.OtherDumpsSameSignature = ordered.Skip(1).Count(d =>
                    !string.IsNullOrEmpty(d.Info.Signature) && CrashMerger.SameSignature(d.Info.Signature, primary.Info.Signature));
                foreach (var d in ordered.Skip(1)) f.Sources.Add("weiterer Dump: " + d.Name);
            }

            // Gamebuild notfalls aus der Version im Spielprotokoll („3570.0-dev_ng_Live“)
            if (!f.GameBuild.HasValue && f.GameLog?.Version != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(f.GameLog.Version, @"^(\d{3,5})\.");
                if (m.Success) f.GameBuild = int.Parse(m.Groups[1].Value);
            }
            return f;
        }

        static void LoadFile(string path, CrashFacts f, List<DumpCandidate> dumps)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var name = Path.GetFileName(path);
            switch (ext)
            {
                case ".dmp":
                    var candidate = new DumpCandidate { Info = MinidumpReader.Read(path), Name = name, Sort = File.GetLastWriteTimeUtc(path) };
                    // FiveM legt neben viele Dumps eine „<id>.dmp.gamelog“ mit dem Spielzustand
                    var sibling = path + ".gamelog";
                    if (File.Exists(sibling)) candidate.GameLog = GameLogParser.Parse(ReadText(sibling), Path.GetFileName(sibling));
                    dumps.Add(candidate);
                    break;
                case ".zip":
                    LoadZip(path, f, dumps);
                    break;
                case ".json":
                    if (name.IndexOf("crashometry", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        CrashMerger.ApplyCrashometry(ReadText(path), f);
                        f.Sources.Add(name);
                    }
                    break;
                default:
                    var text = ReadText(path);
                    ApplyText(text, name, f);
                    break;
            }
        }

        static void ApplyText(string text, string name, CrashFacts f)
        {
            if (GameLogParser.LooksLikeGameLog(text))
            {
                if (f.GameLog == null) { f.GameLog = GameLogParser.Parse(text, name); f.Sources.Add("Spielprotokoll: " + name); }
                return;
            }
            if (name.IndexOf("cef_console", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                f.CefConsoleTail = Tail(text, 30);
                f.Sources.Add(name);
                return;
            }
            if (CrashTextParser.LooksLikeLog(text) || name.StartsWith("CitizenFX_log", StringComparison.OrdinalIgnoreCase))
            {
                f.Log = LogParser.Parse(text, name);
                f.Sources.Add("Client-Log: " + name);
                // Fehlercodes stehen manchmal nur im Log
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\bERR_[A-Z0-9_]{3,}\b"))
                    CrashTextParser.AddUnique(f.ErrorCodes, m.Value);
            }
            else
            {
                CrashTextParser.Parse(text, f);
                f.Sources.Add(name);
            }
        }

        static void LoadZip(string path, CrashFacts f, List<DumpCandidate> dumps)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                f.Sources.Add("Crash-Zip: " + Path.GetFileName(path));
                foreach (var entry in zip.Entries)
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext == ".dmp")
                    {
                        if (entry.Length > MaxInMemoryDump)
                        {
                            f.Warnings.Add($"{entry.Name} ist ein vollständiger Speicherabzug ({entry.Length / 1048576} MB) und wird übersprungen. Für die Analyse reicht der normale Minidump.");
                            continue;
                        }
                        var ms = new MemoryStream((int)Math.Max(0, entry.Length));
                        using (var s = entry.Open()) s.CopyTo(ms);
                        ms.Position = 0;
                        dumps.Add(new DumpCandidate { Info = MinidumpReader.Read(ms), Name = entry.Name, Sort = entry.LastWriteTime.UtcDateTime });
                    }
                    else if (ext == ".log" || ext == ".txt" || ext == ".json" || ext == ".gamelog")
                    {
                        if (entry.Length > MaxTextFile) continue;
                        string text;
                        using (var s = entry.Open())
                        using (var r = new StreamReader(s, Encoding.UTF8, true)) text = r.ReadToEnd();
                        if (entry.Name.IndexOf("crashometry", StringComparison.OrdinalIgnoreCase) >= 0) CrashMerger.ApplyCrashometry(text, f);
                        else if (ext != ".json") ApplyText(text, entry.Name, f);
                    }
                }
            }
        }

        static string ReadText(string path)
        {
            var len = new FileInfo(path).Length;
            if (len > MaxTextFile)
            {
                // nur das Ende lesen – dort steht der Absturz
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(-MaxTextFile, SeekOrigin.End);
                    using (var r = new StreamReader(fs, Encoding.UTF8, true)) return r.ReadToEnd();
                }
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs, Encoding.UTF8, true)) return r.ReadToEnd();
        }

        static string Tail(string text, int lines)
        {
            var all = (text ?? "").Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }

        /// <summary>
        /// Sucht den neuesten Absturz: neuestes „Save information“-Zip in %TEMP% (enthält Dump und Log)
        /// oder sonst den neuesten Dump aus FiveM.app\crashes plus das passende Client-Log.
        /// </summary>
        public static List<string> FindLatestCrash(FiveMPaths paths, out string description)
        {
            var result = new List<string>();
            FileInfo newestDump = null, newestZip = null;
            if (paths.Found && Directory.Exists(paths.CrashesDir))
                newestDump = new DirectoryInfo(paths.CrashesDir).GetFiles("*.dmp").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            try
            {
                newestZip = new DirectoryInfo(Path.GetTempPath()).GetFiles("CfxCrashDump_*.zip").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (newestZip != null && (newestDump == null || newestZip.LastWriteTimeUtc >= newestDump.LastWriteTimeUtc.AddMinutes(-10)))
            {
                result.Add(newestZip.FullName);
                description = $"Crash-Zip vom {newestZip.LastWriteTime:dd.MM.yyyy HH:mm}";
                return result;
            }
            if (newestDump == null)
            {
                description = paths.Found ? "Im Ordner „crashes“ liegt kein Dump." : "FiveM wurde nicht gefunden.";
                return result;
            }

            result.Add(newestDump.FullName);
            description = $"Dump vom {newestDump.LastWriteTime:dd.MM.yyyy HH:mm}";
            if (Directory.Exists(paths.LogsDir))
            {
                var log = new DirectoryInfo(paths.LogsDir).GetFiles("CitizenFX_log_*.log")
                    .Where(l => l.LastWriteTimeUtc <= newestDump.LastWriteTimeUtc.AddMinutes(5))
                    .OrderByDescending(l => l.LastWriteTimeUtc).FirstOrDefault();
                if (log != null && newestDump.LastWriteTimeUtc - log.LastWriteTimeUtc < TimeSpan.FromHours(12))
                {
                    result.Add(log.FullName);
                    description += " + Client-Log";
                }
            }
            return result;
        }
    }
}
