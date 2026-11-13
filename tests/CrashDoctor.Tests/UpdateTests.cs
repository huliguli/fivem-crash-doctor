using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using CrashDoctor.Core;
using CrashDoctor.Platform;
using Xunit;

namespace CrashDoctor.Tests
{
    public class UpdateLogicTests
    {
        const string ReleaseJson = @"{
  ""tag_name"": ""v1.2.0"", ""name"": ""FiveM Crash Doctor 1.2.0"", ""draft"": false, ""prerelease"": false,
  ""html_url"": ""https://github.com/huliguli/fivem-crash-doctor/releases/tag/v1.2.0"",
  ""body"": ""Erste Zeile\n\n- **Neu:** Punkt zwei\n- Punkt drei"",
  ""assets"": [
    { ""name"": ""FiveMCrashDoctor.exe"", ""size"": 320512, ""browser_download_url"": ""https://github.com/huliguli/fivem-crash-doctor/releases/download/v1.2.0/FiveMCrashDoctor.exe"" },
    { ""name"": ""FiveMCrashDoctor.exe.sha256"", ""size"": 86, ""browser_download_url"": ""https://github.com/huliguli/fivem-crash-doctor/releases/download/v1.2.0/FiveMCrashDoctor.exe.sha256"" }
  ]
}";

        [Fact]
        public void Versionen_numerisch_vergleichen()
        {
            Assert.Equal(new Version(1, 2, 0), UpdateLogic.ParseVersion("v1.2"));
            Assert.Equal(new Version(1, 10, 3), UpdateLogic.ParseVersion("1.10.3"));
            Assert.Null(UpdateLogic.ParseVersion("v1.2-beta"));
            Assert.True(UpdateLogic.IsNewer(new Version(1, 10, 0), new Version(1, 9, 9)));   // als Text wäre 1.10 < 1.9
            Assert.False(UpdateLogic.IsNewer(new Version(1, 1, 0), new Version(1, 1, 0, 0))); // Assembly-Version mit vier Stellen
        }

        [Theory]
        [InlineData("https://github.com/x", true)]
        [InlineData("https://api.github.com/repos", true)]
        [InlineData("https://objects.githubusercontent.com/a", true)]
        [InlineData("https://release-assets.githubusercontent.com/a", true)]
        [InlineData("https://evilgithub.com/a", false)]
        [InlineData("https://github.com.evil.example/a", false)]
        [InlineData("https://githubusercontent.com/a", false)]
        [InlineData("https://githubusercontent.com.evil.example/a", false)]
        [InlineData("http://github.com/a", false)]
        [InlineData("https://github.com:8443/a", false)]
        public void Nur_GitHub_ueber_HTTPS(string url, bool allowed) =>
            Assert.Equal(allowed, UpdateLogic.IsAllowedHost(new Uri(url)));

        [Fact]
        public void Release_wird_gelesen_und_angeboten()
        {
            var r = UpdateLogic.ParseRelease(ReleaseJson);
            Assert.Equal(new Version(1, 2, 0), r.Version);
            Assert.Equal(2, r.Assets.Count);
            var offer = UpdateLogic.Evaluate(r, new Version(1, 1, 0), null);
            Assert.NotNull(offer);
            Assert.Equal(320512, offer.Exe.Size);
            Assert.Equal("Erste Zeile\nNeu: Punkt zwei", UpdateLogic.NotesPreview(r.Notes, 2));
        }

        [Fact]
        public void Kein_Angebot_bei_alt_uebersprungen_Entwurf_oder_ohne_Dateien()
        {
            var r = UpdateLogic.ParseRelease(ReleaseJson);
            Assert.Null(UpdateLogic.Evaluate(r, new Version(1, 2, 0), null));     // gleich alt
            Assert.Null(UpdateLogic.Evaluate(r, new Version(1, 1, 0), "1.2.0"));  // übersprungen
            r.Prerelease = true;
            Assert.Null(UpdateLogic.Evaluate(r, new Version(1, 1, 0), null));     // zurückgerufen (--prerelease)
            r.Prerelease = false;
            r.Assets.RemoveAll(a => a.Name.EndsWith(".sha256"));
            Assert.Null(UpdateLogic.Evaluate(r, new Version(1, 1, 0), null));     // Dateien noch nicht vollständig
            Assert.Null(UpdateLogic.ParseRelease("kein json"));
        }

        [Fact]
        public void Pruefsumme_lesen()
        {
            var h = new string('a', 64);
            Assert.Equal(h, UpdateLogic.ParseChecksum(h.ToUpperInvariant() + "  FiveMCrashDoctor.exe", "FiveMCrashDoctor.exe"));
            Assert.Equal(h, UpdateLogic.ParseChecksum(h, "FiveMCrashDoctor.exe"));
            Assert.Null(UpdateLogic.ParseChecksum(h + "  andere.exe", "FiveMCrashDoctor.exe"));
            Assert.Null(UpdateLogic.ParseChecksum("zu kurz", "FiveMCrashDoctor.exe"));
        }

        [Fact]
        public void Einstellungen_ueberleben_Speichern_und_Laden()
        {
            var path = Path.Combine(Path.GetTempPath(), "cd-settings-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var reset = new DateTime(2026, 11, 13, 18, 0, 0, DateTimeKind.Utc);
                new AppSettings { FiveMDir = @"D:\FiveM\FiveM.app", UpdateCheck = false, UpdateAutoInstall = true, SkippedVersion = "1.2.0", RateLimitedUntilUtc = reset }.Save(path);
                var s = AppSettings.Load(path);
                Assert.Equal(@"D:\FiveM\FiveM.app", s.FiveMDir);
                Assert.False(s.UpdateCheck);
                Assert.True(s.UpdateAutoInstall);
                Assert.Equal("1.2.0", s.SkippedVersion);
                Assert.Equal(reset, s.RateLimitedUntilUtc);
            }
            finally { File.Delete(path); }
        }
    }

    /// <summary>
    /// Echte Authenticode-Prüfung mit einem Wegwerf-Zertifikat (nur im Speicher und als Temp-PFX, nie im Zertifikatsspeicher).
    /// </summary>
    public sealed class SignedFileFixture : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "cd-sign-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public readonly string Signed;
        public readonly string Unsigned;
        public readonly string Thumbprint;
        public readonly Version FileVersion;

        public SignedFileFixture()
        {
            Directory.CreateDirectory(Dir);
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                var req = new CertificateRequest("CN=CrashDoctor Testsignatur", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));
                using (var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7)))
                {
                    Thumbprint = cert.Thumbprint;
                    File.WriteAllBytes(Path.Combine(Dir, "test.pfx"), cert.Export(X509ContentType.Pfx, "test"));
                }
            }

            // eine echte PE-Datei mit bekannter Dateiversion: die Test-DLL selbst
            var source = Assembly.GetExecutingAssembly().Location;
            FileVersion = UpdateLogic.ParseVersion(FileVersionInfo.GetVersionInfo(source).FileVersion);
            Unsigned = Path.Combine(Dir, "unsigned.dll");
            Signed = Path.Combine(Dir, "signed.dll");
            File.Copy(source, Unsigned);
            File.Copy(source, Signed);

            // Pfade über Umgebungsvariablen, nie in den Befehl einsetzen (DESKTOP-APPS/CODE-SIGNING)
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($env:CD_PFX, 'test'); " +
                "$r = Set-AuthenticodeSignature -FilePath $env:CD_FILE -Certificate $c -HashAlgorithm SHA256; if ($r.Status -eq 'NotSigned' -or $r.Status -eq 'HashMismatch') { exit 3 }\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.EnvironmentVariables["CD_PFX"] = Path.Combine(Dir, "test.pfx");
            psi.EnvironmentVariables["CD_FILE"] = Signed;
            foreach (var key in psi.EnvironmentVariables.Keys.Cast<string>().Where(k => k.Equals("PSModulePath", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.EnvironmentVariables.Remove(key);
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                if (p.ExitCode != 0) throw new InvalidOperationException("Signieren im Test fehlgeschlagen: " + err);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public class UpdaterTests : IClassFixture<SignedFileFixture>
    {
        readonly SignedFileFixture _f;
        public UpdaterTests(SignedFileFixture f) { _f = f; }

        [Fact]
        public void Passende_Signatur_wird_akzeptiert()
        {
            Assert.True(Updater.VerifyAuthenticode(_f.Signed, new[] { _f.Thumbprint }, out var thumb, out var error), error);
            Assert.Equal(_f.Thumbprint, thumb);
        }

        [Fact]
        public void Fremdes_Zertifikat_wird_abgelehnt()
        {
            Assert.False(Updater.VerifyAuthenticode(_f.Signed, Updater.TrustedThumbprints, out _, out var error));
            Assert.Contains("fremdes Zertifikat", error);
        }

        [Fact]
        public void Unsignierte_Datei_wird_abgelehnt()
        {
            Assert.False(Updater.VerifyAuthenticode(_f.Unsigned, new[] { _f.Thumbprint }, out _, out _));
        }

        [Fact]
        public void Manipulierte_Datei_wird_abgelehnt()
        {
            var tampered = Path.Combine(_f.Dir, "tampered-" + Guid.NewGuid().ToString("N") + ".dll");
            var bytes = File.ReadAllBytes(_f.Signed);
            bytes[bytes.Length / 3] ^= 0xFF; // mitten im Code, außerhalb der Signatur
            File.WriteAllBytes(tampered, bytes);
            Assert.False(Updater.VerifyAuthenticode(tampered, new[] { _f.Thumbprint }, out _, out var error));
            Assert.Contains("WinVerifyTrust", error);
        }

        UpdateOffer Feed(string dir, string exeSource, string checksumOverride = null)
        {
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, UpdateLogic.ExeAssetName);
            File.Copy(exeSource, exe, true);
            string hash;
            using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(File.ReadAllBytes(exe)).Select(b => b.ToString("x2")));
            File.WriteAllText(Path.Combine(dir, UpdateLogic.ChecksumAssetName), (checksumOverride ?? hash) + "  " + UpdateLogic.ExeAssetName);
            var size = new FileInfo(exe).Length;
            var v = _f.FileVersion;
            File.WriteAllText(Path.Combine(dir, "release.json"),
                $"{{\"tag_name\":\"v{v}\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
                $"{{\"name\":\"{UpdateLogic.ExeAssetName}\",\"size\":{size},\"browser_download_url\":\"{UpdateLogic.ExeAssetName}\"}}," +
                $"{{\"name\":\"{UpdateLogic.ChecksumAssetName}\",\"size\":90,\"browser_download_url\":\"{UpdateLogic.ChecksumAssetName}\"}}]}}");
            var release = UpdateLogic.ParseRelease(new FolderFeed(dir).LatestReleaseJson());
            return UpdateLogic.Evaluate(release, new Version(0, 0, 1), null);
        }

        [Fact]
        public void Ganzer_Ablauf_herunterladen_pruefen_austauschen_aufraeumen()
        {
            var feedDir = Path.Combine(_f.Dir, "feed-ok");
            var offer = Feed(feedDir, _f.Signed);
            Assert.NotNull(offer);

            var installDir = Path.Combine(_f.Dir, "install-ok");
            Directory.CreateDirectory(installDir);
            var current = Path.Combine(installDir, "FiveMCrashDoctor.exe");
            File.WriteAllText(current, "alte Fassung");

            var verified = Updater.DownloadAndVerify(new FolderFeed(feedDir), offer, current, s => { }, CancellationToken.None, new[] { _f.Thumbprint });
            Assert.Equal(current + ".download", verified);
            Updater.Swap(current, verified);
            Assert.Equal(File.ReadAllBytes(_f.Signed), File.ReadAllBytes(current));
            Assert.Equal("alte Fassung", File.ReadAllText(current + ".old"));
            Updater.CleanupLeftovers(current);
            Assert.False(File.Exists(current + ".old"));
        }

        [Fact]
        public void Falsche_Pruefsumme_verwirft_den_Download()
        {
            var feedDir = Path.Combine(_f.Dir, "feed-badsum");
            var offer = Feed(feedDir, _f.Signed, checksumOverride: new string('0', 64));
            var installDir = Path.Combine(_f.Dir, "install-badsum");
            Directory.CreateDirectory(installDir);
            var current = Path.Combine(installDir, "FiveMCrashDoctor.exe");
            File.WriteAllText(current, "alt");
            var ex = Assert.Throws<UpdateException>(() =>
                Updater.DownloadAndVerify(new FolderFeed(feedDir), offer, current, s => { }, CancellationToken.None, new[] { _f.Thumbprint }));
            Assert.Contains("Prüfsumme", ex.Message);
            Assert.False(File.Exists(current + ".download"));
            Assert.Equal("alt", File.ReadAllText(current));
        }

        [Fact]
        public void Unsigniertes_Update_wird_nie_installiert()
        {
            var feedDir = Path.Combine(_f.Dir, "feed-unsigned");
            var offer = Feed(feedDir, _f.Unsigned);
            var installDir = Path.Combine(_f.Dir, "install-unsigned");
            Directory.CreateDirectory(installDir);
            var current = Path.Combine(installDir, "FiveMCrashDoctor.exe");
            File.WriteAllText(current, "alt");
            var ex = Assert.Throws<UpdateException>(() =>
                Updater.DownloadAndVerify(new FolderFeed(feedDir), offer, current, s => { }, CancellationToken.None, new[] { _f.Thumbprint }));
            Assert.Contains("Signatur", ex.Message);
            Assert.Equal("alt", File.ReadAllText(current));
        }

        [Fact]
        public void Pruefung_wirft_nie_und_meldet_fehlendes_Release()
        {
            var empty = Path.Combine(_f.Dir, "feed-empty");
            Directory.CreateDirectory(empty);
            var s = new AppSettings();
            Assert.Equal(UpdateStatus.NoRelease, Updater.Check(new FolderFeed(empty), s, manual: true).Status);
            Assert.Equal(UpdateStatus.Disabled, Updater.Check(new FolderFeed(empty), new AppSettings { UpdateCheck = false }, manual: false).Status);
            var broken = Path.Combine(_f.Dir, "feed-broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "release.json"), "<html>Wartung</html>");
            Assert.Equal(UpdateStatus.Unreachable, Updater.Check(new FolderFeed(broken), s, manual: true).Status);
        }

        [Fact]
        public void Build_Ordner_tauscht_sich_nicht_selbst_aus()
        {
            Assert.True(Updater.IsDevBuild(@"C:\repo\src\CrashDoctor\bin\Release\net48\FiveMCrashDoctor.exe"));
            Assert.False(Updater.IsDevBuild(@"C:\Users\Max\Downloads\FiveMCrashDoctor.exe"));
        }
    }
}
