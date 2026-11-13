using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CrashDoctor.Core;
using CrashDoctor.Platform;
using Xunit;

namespace CrashDoctor.Tests
{
    /// <summary>
    /// Prüft Pfaderkennung, Einlesen und Reparaturen gegen eine nachgebaute FiveM-Installation im Temp-Ordner.
    /// Reparaturen, die echte Benutzer- oder Systemdaten anfassen (Grafikeinstellungen, Shader-Cache,
    /// Prozesse beenden, Auslagerungsdatei), werden hier bewusst NICHT ausgeführt.
    /// </summary>
    public sealed class FakeInstall : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "cdtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public string App => Path.Combine(Root, "FiveM", "FiveM.app");
        public string Gta => Path.Combine(Root, "Grand Theft Auto V");

        public FakeInstall()
        {
            Directory.CreateDirectory(Path.Combine(App, "data", "cache", "subprocess"));
            File.WriteAllText(Path.Combine(App, "data", "cache", "subprocess", "x.bin"), new string('x', 1000));
            Directory.CreateDirectory(Path.Combine(App, "data", "server-cache"));
            File.WriteAllText(Path.Combine(App, "data", "server-cache", "a.rpf"), "abc");
            Directory.CreateDirectory(Path.Combine(App, "data", "game-storage"));
            File.WriteAllText(Path.Combine(App, "data", "game-storage", "keep.rpf"), "keep");
            Directory.CreateDirectory(Path.Combine(App, "plugins"));
            File.WriteAllText(Path.Combine(App, "plugins", "dxgi.dll"), "reshade");
            Directory.CreateDirectory(Path.Combine(App, "crashes"));
            Directory.CreateDirectory(Path.Combine(App, "logs"));
            Directory.CreateDirectory(Gta);
            File.WriteAllText(Path.Combine(Gta, "GTA5.exe"), "");
            File.WriteAllText(Path.Combine(Gta, "d3d11.dll"), "enb");
            File.WriteAllText(Path.Combine(Gta, "trainer.asi"), "asi");
            File.WriteAllText(Path.Combine(Gta, "x64a.rpf"), "game");
            File.WriteAllText(Path.Combine(App, "CitizenFX.ini"), "[Game]\r\nIVPath=" + Gta + "\r\nUpdateChannel=canary\r\n");
        }

        public FiveMPaths Paths => FiveMPaths.Detect(App);

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public class PlatformTests
    {
        [Fact]
        public void Pfade_aus_CitizenFX_ini()
        {
            using (var fake = new FakeInstall())
            {
                var p = fake.Paths;
                Assert.True(p.Found);
                Assert.Equal(fake.Gta, p.GtaDir);
                Assert.Equal("Rockstar", p.GtaPlatform);
                Assert.Equal("canary", FiveMPaths.IniRead(p.CitizenFxIni, "Game", "UpdateChannel"));
                Assert.Equal(new[] { "dxgi.dll" }, SystemCollector.ListPlugins(p.PluginsDir).ToArray());
                var mods = SystemCollector.ListGtaMods(p.GtaDir);
                Assert.Contains("d3d11.dll", mods);
                Assert.Contains("trainer.asi", mods);
                Assert.DoesNotContain("x64a.rpf", mods);
            }
        }

        [Fact]
        public void Systembild_der_Schein_Installation()
        {
            using (var fake = new FakeInstall())
            {
                var s = SystemCollector.Collect(fake.Paths);
                Assert.True(s.FiveMFound);
                Assert.Equal("canary", s.UpdateChannel);
                Assert.True(s.TotalRamBytes > 0);
                Assert.True(s.FiveMCacheBytes > 1000);
                var r = Analyzer.Analyze(new CrashFacts(), s);
                Assert.Contains(r.Diagnoses, d => d.Id == "sys-channel");
                Assert.Contains(r.Diagnoses, d => d.Id == "sys-plugins");
                Assert.Contains(r.Diagnoses, d => d.Id == "sys-gta-mods");
            }
        }

        [Fact]
        public void Neuester_Crash_mit_passendem_Log()
        {
            using (var fake = new FakeInstall())
            {
                var p = fake.Paths;
                var oldDump = Path.Combine(p.CrashesDir, "old.dmp");
                var newDump = Path.Combine(p.CrashesDir, "new.dmp");
                File.WriteAllBytes(oldDump, Dump());
                File.WriteAllBytes(newDump, Dump());
                File.SetLastWriteTimeUtc(oldDump, DateTime.UtcNow.AddDays(-2));
                var log = Path.Combine(p.LogsDir, "CitizenFX_log_2026-11-13T120000.log");
                File.WriteAllText(log, "[    1] [b3095_GTAProcess]  MainThrd/ hello\n[    2] [b3095_GTAProcess]  MainThrd/ ^1SCRIPT ERROR: boom^7\n[    3] x");
                File.SetLastWriteTimeUtc(log, File.GetLastWriteTimeUtc(newDump).AddSeconds(-30));

                var files = InputLoader.FindLatestCrash(p, out var desc);
                // ein CfxCrashDump-Zip im echten Temp-Ordner kann das Ergebnis vorwegnehmen – dann gilt der Test nur für den Ordnerfall
                if (files.Count == 1 && files[0].EndsWith(".zip")) return;
                Assert.Equal(new[] { newDump, log }, files.ToArray());
                Assert.Contains("Client-Log", desc);

                var f = InputLoader.Load(null, files.Concat(new[] { oldDump }));
                Assert.Equal("nvwgf2umx.dll+5A1B2C", f.Signature);
                Assert.Equal(1, f.OtherDumpCount);
                Assert.Equal(1, f.OtherDumpsSameSignature);
                Assert.Single(f.Log.ScriptErrors);
                var r = Analyzer.Analyze(f, null);
                Assert.Contains(r.Top.MatchedBy, m => m.Contains("1 von 1 weiteren Dumps"));
            }
        }

        [Fact]
        public void Save_information_Zip_wird_vollstaendig_gelesen()
        {
            using (var fake = new FakeInstall())
            {
                var zipPath = Path.Combine(fake.Root, "CfxCrashDump_2026_11_13_12_00_00.zip");
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    var e = zip.CreateEntry("dump.dmp");
                    using (var s = e.Open()) { var b = Dump(); s.Write(b, 0, b.Length); }
                    var l = zip.CreateEntry("CitizenFX_log_2026-11-13T115500.log");
                    using (var w = new StreamWriter(l.Open(), Encoding.UTF8)) w.Write("[  1] a\n[  2] b\n[  3] Failed to load model foo\n");
                    var c = zip.CreateEntry("crashometry.json");
                    using (var w = new StreamWriter(c.Open(), Encoding.UTF8))
                        w.Write("{\"crashometry_last_server\":\"cfx.re/join/abc123\",\"crashometry_RockstarId\":\"123456\",\"crashometry_gpu\":\"RTX\"}");
                }
                var f = InputLoader.Load(null, new[] { zipPath });
                Assert.True(f.HasDump);
                Assert.Equal("nvwgf2umx.dll+5A1B2C", f.Signature);
                Assert.NotNull(f.Log);
                Assert.Equal("cfx.re/join/abc123", f.Crashometry["last_server"]);
                Assert.False(f.Crashometry.ContainsKey("RockstarId")); // Kennung des Spielers wird verworfen
            }
        }

        [Fact]
        public void Cache_leeren_behaelt_game_storage()
        {
            using (var fake = new FakeInstall())
            {
                var env = new FixEnv { Paths = fake.Paths };
                var fix = FixRegistry.Get(FixIds.ClearCache);
                var preview = fix.Preview(env);
                Assert.True(preview.CanRun);
                Assert.Contains(preview.Lines, l => l.Contains("server-cache"));
                var r = fix.Run(env);
                Assert.True(r.Ok, r.Message);
                Assert.False(Directory.Exists(Path.Combine(fake.App, "data", "cache")));
                Assert.False(Directory.Exists(Path.Combine(fake.App, "data", "server-cache")));
                Assert.True(File.Exists(Path.Combine(fake.App, "data", "game-storage", "keep.rpf")));
                Assert.False(fix.Preview(env).CanRun); // zweiter Lauf: nichts mehr zu tun
            }
        }

        [Fact]
        public void Plugins_werden_nur_umbenannt()
        {
            using (var fake = new FakeInstall())
            {
                var env = new FixEnv { Paths = fake.Paths };
                var r = FixRegistry.Get(FixIds.DisablePlugins).Run(env);
                Assert.True(r.Ok, r.Message);
                Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fake.App, "plugins")));
                var moved = Directory.GetDirectories(fake.App, "plugins.deaktiviert-*").Single();
                Assert.True(File.Exists(Path.Combine(moved, "dxgi.dll")));
            }
        }

        [Fact]
        public void Mods_aus_dem_GTA_Ordner_gesichert()
        {
            using (var fake = new FakeInstall())
            {
                var env = new FixEnv { Paths = fake.Paths };
                var fix = FixRegistry.Get(FixIds.MoveGtaMods);
                Assert.False(fix.NeedsAdmin(env)); // Temp-Ordner ist beschreibbar
                var r = fix.Run(env);
                Assert.True(r.Ok, r.Message);
                Assert.False(File.Exists(Path.Combine(fake.Gta, "d3d11.dll")));
                Assert.True(File.Exists(Path.Combine(fake.Gta, "x64a.rpf")));
                var backup = Directory.GetDirectories(fake.Gta, "_CrashDoctor-Sicherung-*").Single();
                Assert.True(File.Exists(Path.Combine(backup, "trainer.asi")));
            }
        }

        [Fact]
        public void Update_Kanal_zurueck_auf_production()
        {
            using (var fake = new FakeInstall())
            {
                var env = new FixEnv { Paths = fake.Paths };
                var r = FixRegistry.Get(FixIds.UpdateChannel).Run(env);
                Assert.True(r.Ok, r.Message);
                Assert.Equal("production", FiveMPaths.IniRead(env.Paths.CitizenFxIni, "Game", "UpdateChannel"));
                Assert.Equal(fake.Gta, FiveMPaths.IniRead(env.Paths.CitizenFxIni, "Game", "IVPath")); // Rest der Datei bleibt
            }
        }

        [Fact]
        public void Admin_Anfrage_ausserhalb_des_Austauschordners_wird_abgelehnt()
        {
            var evil = Path.Combine(Path.GetTempPath(), "evil-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(evil, "fix=move-gta-mods\nresult=" + evil + ".res");
            try { Assert.Equal(2, FixRunner.RunFromRequest(evil)); }
            finally { File.Delete(evil); }
        }

        [Fact]
        public void Das_Tool_selbst_gilt_nicht_als_laufendes_FiveM()
        {
            Assert.False(SystemCollector.IsFiveMProcessName("FiveMCrashDoctor"));
            Assert.True(SystemCollector.IsFiveMProcessName("FiveM"));
            Assert.True(SystemCollector.IsFiveMProcessName("FiveM_b3095_GTAProcess"));
            Assert.True(SystemCollector.IsFiveMProcessName("FiveM_ChromeBrowser"));
        }

        [Fact]
        public void Nvidia_Version_aus_Windows_Treiberversion()
        {
            Assert.Equal("560.94", SystemCollector.NvidiaVersion("32.0.15.6094"));
            Assert.Equal("621.14", SystemCollector.NvidiaVersion("32.0.16.2114"));
            Assert.Null(SystemCollector.NvidiaVersion("kaputt"));
        }

        static byte[] Dump()
        {
            var b = new MinidumpBuilder();
            b.Modules.Add((@"C:\FiveM\FiveM.app\FiveM_b3095_GTAProcess.exe", 0x7FF600000000, 0x4000000));
            b.Modules.Add((@"C:\Windows\System32\DriverStore\nvwgf2umx.dll", 0x7FF900000000, 0x3000000));
            b.ExceptionAddress = 0x7FF900000000 + 0x5A1B2C;
            return b.Build();
        }
    }
}
