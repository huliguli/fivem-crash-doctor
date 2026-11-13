using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CrashDoctor.Core
{
    public sealed class ReleaseAsset
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
    }

    public sealed class ReleaseInfo
    {
        public string Tag { get; set; }
        public Version Version { get; set; }
        public string Title { get; set; }
        public string Notes { get; set; }
        public string PageUrl { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public List<ReleaseAsset> Assets { get; } = new List<ReleaseAsset>();

        public ReleaseAsset Asset(string name) =>
            Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ein Update, das installiert werden kann.</summary>
    public sealed class UpdateOffer
    {
        public ReleaseInfo Release { get; set; }
        public ReleaseAsset Exe { get; set; }
        public ReleaseAsset Checksum { get; set; }
        public Version Version => Release.Version;
    }

    /// <summary>
    /// Regeln des Updaters, frei von Netz und Dateisystem (Bauplan: DESKTOP-APPS-LIBRARY/AUTO-UPDATE.md):
    /// numerischer Versionsvergleich, exakte Host-Allowlist, nur vollständige, veröffentlichte Releases.
    /// </summary>
    public static class UpdateLogic
    {
        public const string ExeAssetName = "FiveMCrashDoctor.exe";
        public const string ChecksumAssetName = ExeAssetName + ".sha256";
        public const long MaxExeBytes = 50L * 1024 * 1024;

        /// <summary>„v1.2.3“ oder „1.2.3“ → 1.2.3 (immer drei Stellen); sonst null.</summary>
        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text.Trim(), @"^v?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?:\.(\d{1,5}))?$", RegexOptions.CultureInvariant);
            if (!m.Success) return null;
            int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            return new Version(Part(1), Part(2), Part(3));
        }

        public static Version Normalize(Version v) => v == null ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        public static bool IsNewer(Version candidate, Version current) =>
            candidate != null && current != null && Normalize(candidate).CompareTo(Normalize(current)) > 0;

        /// <summary>
        /// Nur HTTPS zu GitHub: github.com, api.github.com und echte Unterdomänen von githubusercontent.com
        /// (dort liegen die Release-Dateien nach der Weiterleitung). Kein EndsWith("github.com") – evilgithub.com!
        /// </summary>
        public static bool IsAllowedHost(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
            var host = uri.IdnHost.ToLowerInvariant();
            if (host == "github.com" || host == "api.github.com") return true;
            return host.EndsWith(".githubusercontent.com", StringComparison.Ordinal) && host.Length > ".githubusercontent.com".Length;
        }

        public static ReleaseInfo ParseRelease(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            Dictionary<string, object> root;
            try { root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            if (root == null) return null;

            var r = new ReleaseInfo
            {
                Tag = Str(root, "tag_name"),
                Title = Str(root, "name"),
                Notes = Str(root, "body"),
                PageUrl = Str(root, "html_url"),
                Draft = Bool(root, "draft"),
                Prerelease = Bool(root, "prerelease")
            };
            r.Version = ParseVersion(r.Tag);
            if (root.TryGetValue("assets", out var assets) && assets is IEnumerable list)
            {
                foreach (var item in list)
                {
                    if (!(item is Dictionary<string, object> a)) continue;
                    long size = 0;
                    if (a.TryGetValue("size", out var s) && s != null) long.TryParse(Convert.ToString(s, CultureInfo.InvariantCulture), out size);
                    r.Assets.Add(new ReleaseAsset { Name = Str(a, "name"), Url = Str(a, "browser_download_url"), Size = size });
                }
            }
            return r;
        }

        /// <summary>Liefert ein Angebot nur für ein veröffentlichtes, neueres Release mit EXE und Prüfsumme.</summary>
        public static UpdateOffer Evaluate(ReleaseInfo release, Version current, string skippedVersion)
        {
            if (release == null || release.Draft || release.Prerelease || release.Version == null) return null;
            if (!IsNewer(release.Version, current)) return null;
            var skipped = ParseVersion(skippedVersion);
            if (skipped != null && Normalize(skipped).Equals(Normalize(release.Version))) return null;
            var exe = release.Asset(ExeAssetName);
            var sha = release.Asset(ChecksumAssetName);
            // Release ohne eigene Dateien = „noch kein Update“, kein Fehler (Assets kommen evtl. gerade erst)
            if (exe == null || sha == null || exe.Size <= 0 || exe.Size > MaxExeBytes) return null;
            return new UpdateOffer { Release = release, Exe = exe, Checksum = sha };
        }

        /// <summary>„&lt;64 hex&gt;  FiveMCrashDoctor.exe“ oder nur der Hash → Hash in Kleinbuchstaben; sonst null.</summary>
        public static string ParseChecksum(string text, string fileName)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^(?<h>[0-9A-Fa-f]{64})(?:\s+\*?(?<n>\S.*))?$");
                if (!m.Success) continue;
                if (m.Groups["n"].Success && !string.Equals(m.Groups["n"].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                return m.Groups["h"].Value.ToLowerInvariant();
            }
            return null;
        }

        /// <summary>Die ersten Zeilen der Release-Notizen, ohne Markdown-Zeichen, für das Banner.</summary>
        public static string NotesPreview(string notes, int maxLines = 3)
        {
            if (string.IsNullOrWhiteSpace(notes)) return "";
            var lines = notes.Replace("\r", "").Split('\n')
                .Select(l => Regex.Replace(l, @"^\s*(#+|[-*]|\d+\.)\s*", "").Replace("**", "").Replace("`", "").Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("Download", StringComparison.OrdinalIgnoreCase))
                .Take(maxLines);
            return string.Join("\n", lines);
        }

        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
        static bool Bool(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is bool b && b;
    }
}
