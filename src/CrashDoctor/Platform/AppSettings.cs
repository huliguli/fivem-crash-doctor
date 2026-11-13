using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CrashDoctor.Platform
{
    /// <summary>Einstellungen unter %LOCALAPPDATA%\FiveMCrashDoctor\settings.ini (Schlüssel=Wert).</summary>
    public sealed class AppSettings
    {
        public string FiveMDir { get; set; }
        public bool UpdateCheck { get; set; } = true;          // beim Start und stündlich suchen
        public bool UpdateAutoInstall { get; set; }            // Opt-in, Vorgabe aus
        public string SkippedVersion { get; set; }
        public DateTime? RateLimitedUntilUtc { get; set; }
        public DateTime? LastCheckUtc { get; set; }

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveMCrashDoctor", "settings.ini");

        public static AppSettings Load(string path = null)
        {
            var s = new AppSettings();
            path = path ?? FilePath;
            try
            {
                if (!File.Exists(path)) return s;
                foreach (var line in File.ReadAllLines(path))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "fivem": s.FiveMDir = v.Length == 0 ? null : v; break;
                        case "update_check": s.UpdateCheck = v != "0"; break;
                        case "update_auto": s.UpdateAutoInstall = v == "1"; break;
                        case "update_skip": s.SkippedVersion = v.Length == 0 ? null : v; break;
                        case "update_ratelimit_until": s.RateLimitedUntilUtc = Date(v); break;
                        case "update_last_check": s.LastCheckUtc = Date(v); break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        public void Save(string path = null)
        {
            path = path ?? FilePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new List<string>
                {
                    "fivem=" + (FiveMDir ?? ""),
                    "update_check=" + (UpdateCheck ? "1" : "0"),
                    "update_auto=" + (UpdateAutoInstall ? "1" : "0"),
                    "update_skip=" + (SkippedVersion ?? ""),
                    "update_ratelimit_until=" + Iso(RateLimitedUntilUtc),
                    "update_last_check=" + Iso(LastCheckUtc)
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Iso(DateTime? d) => d.HasValue ? d.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : "";

        // RoundtripKind darf nicht mit AdjustToUniversal kombiniert werden (ArgumentException) – erst lesen, dann umrechnen
        static DateTime? Date(string v) =>
            DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : (DateTime?)null;
    }
}
