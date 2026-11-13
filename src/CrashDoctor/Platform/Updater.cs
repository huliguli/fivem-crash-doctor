using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using CrashDoctor.Core;

namespace CrashDoctor.Platform
{
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    public sealed class RateLimitedException : Exception
    {
        public DateTime ResetUtc { get; }
        public RateLimitedException(DateTime resetUtc) : base("GitHub-Anfragelimit erreicht") { ResetUtc = resetUtc; }
    }

    /// <summary>Woher Releases kommen: GitHub im Normalbetrieb, ein Ordner für Tests (--update-feed).</summary>
    public interface IUpdateFeed
    {
        /// <summary>JSON von releases/latest; null, wenn es (für Unangemeldete) kein Release gibt.</summary>
        string LatestReleaseJson();
        string DownloadText(ReleaseAsset asset, int maxBytes);
        void DownloadFile(ReleaseAsset asset, string target, Action<long> progress, CancellationToken ct);
    }

    /// <summary>GitHub-Releases über HTTPS. Weiterleitungen werden einzeln gegen die Host-Allowlist geprüft.</summary>
    public sealed class GitHubFeed : IUpdateFeed
    {
        public const string Owner = "huliguli";
        public const string Repo = "fivem-crash-doctor";
        public static string LatestUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases/latest";

        public string LatestReleaseJson()
        {
            using (var resp = Get(new Uri(LatestUrl), "application/vnd.github+json"))
            {
                var code = (int)resp.StatusCode;
                if (code == 404) return null;
                if ((code == 403 || code == 429) && resp.Headers["x-ratelimit-remaining"] == "0")
                {
                    var reset = long.TryParse(resp.Headers["x-ratelimit-reset"], out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : DateTime.UtcNow.AddHours(1);
                    throw new RateLimitedException(reset);
                }
                if (code != 200) throw new UpdateException($"GitHub antwortet mit {code}.");
                return ReadText(resp, 2 * 1024 * 1024);
            }
        }

        public string DownloadText(ReleaseAsset asset, int maxBytes)
        {
            using (var resp = Get(new Uri(asset.Url), "application/octet-stream"))
            {
                if ((int)resp.StatusCode != 200) throw new UpdateException($"Download von {asset.Name} fehlgeschlagen ({(int)resp.StatusCode}).");
                return ReadText(resp, maxBytes);
            }
        }

        public void DownloadFile(ReleaseAsset asset, string target, Action<long> progress, CancellationToken ct)
        {
            using (var resp = Get(new Uri(asset.Url), "application/octet-stream"))
            {
                if ((int)resp.StatusCode != 200) throw new UpdateException($"Download von {asset.Name} fehlgeschlagen ({(int)resp.StatusCode}).");
                using (var src = resp.GetResponseStream())
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    Copy(src, dst, UpdateLogic.MaxExeBytes, progress, ct);
            }
        }

        static HttpWebResponse Get(Uri url, string accept)
        {
            for (int hop = 0; hop < 6; hop++)
            {
                if (!UpdateLogic.IsAllowedHost(url)) throw new UpdateException($"Abgelehnt: {url.Host} ist kein GitHub-Server.");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.AllowAutoRedirect = false;
                req.UserAgent = "FiveMCrashDoctor/" + Updater.CurrentVersion;
                req.Accept = accept;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 30000;
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException ex) when (ex.Response is HttpWebResponse error) { resp = error; }
                var code = (int)resp.StatusCode;
                if (code >= 300 && code < 400)
                {
                    var location = resp.Headers["Location"];
                    resp.Dispose();
                    if (string.IsNullOrEmpty(location)) throw new UpdateException("Weiterleitung ohne Ziel.");
                    url = new Uri(url, location);
                    continue;
                }
                return resp;
            }
            throw new UpdateException("Zu viele Weiterleitungen.");
        }

        static string ReadText(HttpWebResponse resp, int maxBytes)
        {
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                Copy(s, ms, maxBytes, null, CancellationToken.None);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        internal static void Copy(Stream src, Stream dst, long maxBytes, Action<long> progress, CancellationToken ct)
        {
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += n;
                if (total > maxBytes) throw new UpdateException("Die Datei ist größer als erlaubt.");
                dst.Write(buf, 0, n);
                progress?.Invoke(total);
            }
        }
    }

    /// <summary>Releases aus einem Ordner (release.json plus die Dateien daneben). Nur für Tests über --update-feed.</summary>
    public sealed class FolderFeed : IUpdateFeed
    {
        readonly string _dir;
        public FolderFeed(string dir) { _dir = Path.GetFullPath(dir); }

        string Local(ReleaseAsset a) => Path.Combine(_dir, Path.GetFileName(a.Name ?? ""));

        public string LatestReleaseJson()
        {
            var p = Path.Combine(_dir, "release.json");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        public string DownloadText(ReleaseAsset asset, int maxBytes)
        {
            var p = Local(asset);
            if (new FileInfo(p).Length > maxBytes) throw new UpdateException("Die Datei ist größer als erlaubt.");
            return File.ReadAllText(p);
        }

        public void DownloadFile(ReleaseAsset asset, string target, Action<long> progress, CancellationToken ct)
        {
            using (var src = File.OpenRead(Local(asset)))
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                GitHubFeed.Copy(src, dst, UpdateLogic.MaxExeBytes, progress, ct);
        }
    }

    public enum UpdateStatus { UpToDate, Available, Disabled, RateLimited, NoRelease, Unreachable }

    public sealed class UpdateCheckResult
    {
        public UpdateStatus Status { get; set; }
        public UpdateOffer Offer { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Selbst-Update der EXE nach DESKTOP-APPS-LIBRARY/AUTO-UPDATE.md: prüft SHA-256 aus dem Release UND die
    /// Authenticode-Signatur gegen den gepinnten Thumbprint (fail closed), prüft die Dateiversion und tauscht
    /// dann die laufende EXE per Umbenennen aus (unter Windows darf eine laufende EXE umbenannt werden).
    /// </summary>
    public static class Updater
    {
        /// <summary>Thumbprints der eigenen Code-Signing-Zertifikate. Beim Zertifikatswechsel den neuen ZUSÄTZLICH eintragen.</summary>
        public static readonly string[] TrustedThumbprints = { "5F6C9BDF7AEDE817D9AC25F6216D907395B02B3D" };

        public static Version CurrentVersion => UpdateLogic.Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        public static string CurrentExePath
        {
            get { using (var p = Process.GetCurrentProcess()) return p.MainModule.FileName; }
        }

        /// <summary>Aus einem Build-Ordner (bin\Debug, bin\Release) tauscht sich das Tool nie selbst aus.</summary>
        public static bool IsDevBuild(string exePath) =>
            Environment.GetEnvironmentVariable("CRASHDOCTOR_ALLOW_DEV_UPDATE") != "1" &&
            (exePath ?? "").IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Wirft nie: offline, Limit, kaputte Antwort ⇒ Status statt Ausnahme.</summary>
        public static UpdateCheckResult Check(IUpdateFeed feed, AppSettings settings, bool manual)
        {
            if (!manual && !settings.UpdateCheck) return new UpdateCheckResult { Status = UpdateStatus.Disabled };
            if (settings.RateLimitedUntilUtc.HasValue && settings.RateLimitedUntilUtc > DateTime.UtcNow)
                return new UpdateCheckResult { Status = UpdateStatus.RateLimited, Message = $"GitHub-Anfragelimit erreicht – nächster Versuch ab {settings.RateLimitedUntilUtc.Value.ToLocalTime():HH:mm} Uhr." };
            try
            {
                var json = feed.LatestReleaseJson();
                settings.LastCheckUtc = DateTime.UtcNow;
                if (json == null) return new UpdateCheckResult { Status = UpdateStatus.NoRelease, Message = "Kein Release gefunden." };
                var release = UpdateLogic.ParseRelease(json);
                if (release == null) return new UpdateCheckResult { Status = UpdateStatus.Unreachable, Message = "Die Antwort von GitHub war nicht lesbar." };
                var offer = UpdateLogic.Evaluate(release, CurrentVersion, manual ? null : settings.SkippedVersion);
                if (offer == null) return new UpdateCheckResult { Status = UpdateStatus.UpToDate, Message = $"Du hast die neueste Version ({CurrentVersion})." };
                AppLog.Write($"Update gefunden: {offer.Version} (installiert {CurrentVersion})");
                return new UpdateCheckResult { Status = UpdateStatus.Available, Offer = offer, Message = $"Version {offer.Version} ist verfügbar." };
            }
            catch (RateLimitedException ex)
            {
                settings.RateLimitedUntilUtc = ex.ResetUtc;
                return new UpdateCheckResult { Status = UpdateStatus.RateLimited, Message = "GitHub-Anfragelimit erreicht – später erneut." };
            }
            catch (Exception ex)
            {
                AppLog.Write("Update-Prüfung fehlgeschlagen: " + ex.Message);
                return new UpdateCheckResult { Status = UpdateStatus.Unreachable, Message = "Update-Server nicht erreichbar." };
            }
        }

        /// <summary>Lädt die neue EXE neben die laufende und prüft sie. Gibt den Pfad der geprüften Datei zurück.</summary>
        public static string DownloadAndVerify(IUpdateFeed feed, UpdateOffer offer, string exePath, Action<string> status, CancellationToken ct,
            IEnumerable<string> pins = null)
        {
            pins = pins ?? TrustedThumbprints;
            var target = exePath + ".download";
            TryDelete(target);
            try
            {
                using (File.Create(target)) { }
            }
            catch (UnauthorizedAccessException)
            {
                throw new UpdateException("Im Ordner der EXE darf das Tool nicht schreiben. Lade das Update über die Release-Seite herunter.");
            }

            try
            {
                status?.Invoke("Prüfsumme wird geladen …");
                var expected = UpdateLogic.ParseChecksum(feed.DownloadText(offer.Checksum, 4096), UpdateLogic.ExeAssetName);
                if (expected == null) throw new UpdateException("Die Prüfsumme im Release ist nicht lesbar.");

                var size = offer.Exe.Size;
                feed.DownloadFile(offer.Exe, target, done => status?.Invoke($"Lade herunter … {Math.Min(100, done * 100 / Math.Max(1, size))} %"), ct);
                if (new FileInfo(target).Length != size) throw new UpdateException("Der Download ist unvollständig.");

                status?.Invoke("Prüfe Datei …");
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(target))
                    actual = string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
                if (actual != expected) throw new UpdateException("Die Prüfsumme stimmt nicht. Das Update wurde verworfen.");

                if (!VerifyAuthenticode(target, pins, out var thumb, out var why))
                    throw new UpdateException("Die Signatur des Updates ist ungültig (" + why + "). Das Update wurde verworfen.");

                var fileVersion = UpdateLogic.ParseVersion(FileVersionInfo.GetVersionInfo(target).FileVersion);
                if (fileVersion == null || !UpdateLogic.Normalize(fileVersion).Equals(UpdateLogic.Normalize(offer.Version)))
                    throw new UpdateException($"Die heruntergeladene Datei hat Version {fileVersion}, erwartet war {offer.Version}.");

                AppLog.Write($"Update {offer.Version} geprüft: SHA-256 ok, Signatur {thumb}");
                return target;
            }
            catch
            {
                TryDelete(target);
                throw;
            }
        }

        /// <summary>Laufende EXE → .old, neue EXE an ihren Platz. Schlägt der zweite Schritt fehl, wird zurückgetauscht.</summary>
        public static void Swap(string currentExe, string verifiedNew)
        {
            var old = currentExe + ".old";
            TryDelete(old);
            if (File.Exists(old)) old = currentExe + ".old-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            File.Move(currentExe, old);
            try
            {
                File.Move(verifiedNew, currentExe);
            }
            catch
            {
                File.Move(old, currentExe);
                throw;
            }
            AppLog.Write($"EXE ausgetauscht, alte Fassung als {Path.GetFileName(old)}");
        }

        public static void Relaunch(string exePath, Version from)
        {
            Process.Start(new ProcessStartInfo(exePath, "--updated " + from) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) })?.Dispose();
        }

        /// <summary>Reste früherer Updates entfernen (die alte EXE kann sich nicht selbst löschen, solange sie läuft).</summary>
        public static void CleanupLeftovers(string exePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(exePath);
                var name = Path.GetFileName(exePath);
                foreach (var f in Directory.GetFiles(dir, name + ".old*").Concat(Directory.GetFiles(dir, name + ".download")))
                    TryDelete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------------------------------------------------------------- Authenticode

        const uint CertEUntrustedRoot = 0x800B0109; // selbstsigniert: Kette endet in einer nicht vertrauten Wurzel – erwartet

        /// <summary>
        /// Prüft die Signatur kryptografisch (WinVerifyTrust: Hash der Datei gegen die Signatur) und den Unterzeichner
        /// gegen die gepinnten Thumbprints. Ein nicht vertrautes Stammzertifikat ist bei selbstsignierten Zertifikaten
        /// normal und wird akzeptiert; eine veränderte Datei (TRUST_E_BAD_DIGEST) oder fehlende Signatur nie.
        /// </summary>
        public static bool VerifyAuthenticode(string path, IEnumerable<string> pins, out string thumbprint, out string error)
        {
            thumbprint = null;
            uint result = WinVerify(path);
            if (result != 0 && result != CertEUntrustedRoot)
            {
                error = $"WinVerifyTrust 0x{result:X8}";
                return false;
            }
            try
            {
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    thumbprint = cert.Thumbprint;
            }
            catch (CryptographicException)
            {
                error = "nicht signiert";
                return false;
            }
            var t = thumbprint;
            if (!pins.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase)))
            {
                error = "fremdes Zertifikat " + thumbprint;
                return false;
            }
            error = null;
            return true;
        }

        static uint WinVerify(string path)
        {
            var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = path };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            var pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,              // WTD_UI_NONE
                    fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                    dwUnionChoice = 1,           // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,           // WTD_STATEACTION_IGNORE
                    dwProvFlags = 0x00000010 | 0x00001000 // REVOCATION_CHECK_NONE | CACHE_ONLY_URL_RETRIEVAL (kein Netz)
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(data, pData, false);
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, pData);
            }
            finally
            {
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pFile);
            }
        }

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
