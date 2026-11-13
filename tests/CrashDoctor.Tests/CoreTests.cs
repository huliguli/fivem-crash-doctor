using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CrashDoctor.Core;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace CrashDoctor.Tests
{
    public class CrashTextParserTests
    {
        // Aufbau wie ein mit Strg+C kopierter TaskDialog; Texte aus MiniDump.cpp
        const string EnglishDialog = @"[Window Title]
Error GTA5_b3095.exe+16B5F3A

[Main Instruction]
GTA5_b3095.exe+16B5F3A

[Content]
An error at GTA5_b3095.exe+16B5F3A caused FiveM to stop working. A crash report is being uploaded to the FiveM developers.

Legacy crash hash: tennis-stairway-seven

[^] Hide information  [Save information
Stores a file with crash information that you should copy and upload when asking for help.]  [Close]

[Expanded Information]
Crash signature: GTA5_b3095.exe+16B5F3A
Report ID: 8d1c4b2e-1f00-4a2b-9c3d-0123456789ab [Copy text]
You can press Ctrl-C to copy this message and paste it elsewhere.";

        // Deutsche Übersetzung aus locales/de/LC_MESSAGES/cfx.po
        const string GermanDialog = @"[Fenstertitel]
Fehler nvwgf2umx.dll+5A1B2C

[Inhalt]
Ein Fehler bei nvwgf2umx.dll+5A1B2C hat verursacht, dass FiveM nicht mehr funktioniert. Ein Absturzbericht wird an die FiveM Entwickler hochgeladen.

[Erweiterte Informationen]
Absturzsignatur: nvwgf2umx.dll+5A1B2C
Meldung ID: ... [lädt hoch]
Sie können Strg-C drücken, um diese Nachricht zu kopieren und an einer anderen Stelle einzufügen.";

        [Fact]
        public void Englischer_Dialog_wird_vollstaendig_erkannt()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse(EnglishDialog, f);
            Assert.True(f.DialogRecognized);
            Assert.Equal("GTA5_b3095.exe+16B5F3A", f.Signature);
            Assert.Equal("gta5_b3095.exe", f.FaultModule);
            Assert.Equal(0x16B5F3AUL, f.FaultOffset);
            Assert.Equal("tennis-stairway-seven", f.LegacyHash);
            Assert.Equal("8d1c4b2e-1f00-4a2b-9c3d-0123456789ab", f.ReportId);
            Assert.Equal(3095, f.GameBuild);
        }

        [Fact]
        public void Deutscher_Dialog_ohne_ReportId_beim_Hochladen()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse(GermanDialog, f);
            Assert.True(f.DialogRecognized);
            Assert.True(f.DialogIsGerman);
            Assert.Equal("nvwgf2umx.dll", f.FaultModule);
            Assert.Null(f.ReportId); // „... [lädt hoch]“ ist keine ID
        }

        [Fact]
        public void Crashname_wird_auch_ohne_Beschriftung_erkannt()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("hab wieder den crash sweet-sad-july auf dem server", f);
            Assert.Equal("sweet-sad-july", f.LegacyHash);
        }

        [Fact]
        public void Beliebige_Wortkette_ist_kein_Crashname()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("das ist ein well-known-problem", f);
            Assert.Null(f.LegacyHash);
        }

        [Fact]
        public void Entladenes_Modul_und_Symbol_werden_zerlegt()
        {
            var a = new CrashFacts();
            CrashTextParser.ApplySignature("rtsshooks64.dll_unloaded+1A2B", a);
            Assert.Equal("rtsshooks64.dll", a.FaultModule);
            Assert.True(a.FaultInUnloadedModule);

            var b = new CrashFacts();
            CrashTextParser.ApplySignature("gta-streaming-five.dll!CfxCollection_AddStreamingFileByTag (0x3c)", b);
            Assert.Equal("gta-streaming-five.dll", b.FaultModule);
            Assert.Equal("CfxCollection_AddStreamingFileByTag", b.FaultSymbol);
            Assert.Equal(0x3CUL, b.FaultOffset);
        }

        [Fact]
        public void DirectX_Fehler_mit_Mods_und_Grund()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("DirectX encountered an unrecoverable error (R): DXGI_ERROR_DEVICE_REMOVED - The GPU device instance has been suspended.\n" +
                                  "GetDeviceRemovedReason returned DXGI_ERROR_DEVICE_HUNG - hung\n\nThe following graphics mods were found in your GTA V installation:\n- ReShade 5.9.2\n\nPlease contact", f);
            Assert.True(f.DirectXFatal);
            Assert.Contains("DXGI_ERROR_DEVICE_REMOVED", f.DirectXErrors);
            Assert.Equal("DXGI_ERROR_DEVICE_HUNG", f.DeviceRemovedReason);
            Assert.Equal("ReShade 5.9.2", f.GraphicsModsInMessage);
        }

        [Fact]
        public void Disconnect_ist_kein_Absturz()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("[Window Title]\nDisconnected\n\n[Main Instruction]\n\u041e\u0448\u0438\u0431\u043a\u0430 (Error)\n\n[Content]\nServer -> client connection timed out. Last seen 120 msec ago.\n", f);
            Assert.StartsWith("Server -> client connection timed out", f.DisconnectMessage);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("disconnect", r.Top.Id);
        }

        [Fact]
        public void Fehlercodes_des_Spiels()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("FiveM has encountered an error\nERR_GFX_D3D_INIT: Failed to create D3D device", f);
            Assert.Contains("ERR_GFX_D3D_INIT", f.ErrorCodes);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("err-d3d-init", r.Top.Id);
            Assert.Contains(FixIds.MoveGtaMods, r.Top.FixIds);
        }
    }

    public class LogParserTests
    {
        [Fact]
        public void Skriptfehler_und_Ende_des_Logs()
        {
            var log = string.Join("\n",
                "[    1000] [b3095_GTAProcess]             MainThrd/ Loading resource foo",
                "[    2000] [b3095_GTAProcess]             MainThrd/ ^1SCRIPT ERROR: @esx_x/client.lua:12: attempt to index a nil value^7",
                "[    2001] [b3095_GTAProcess]             MainThrd/ > fn (@esx_x/client.lua:12)",
                "[    3000] [b3095_GTAProcess]             MainThrd/ Connecting to cfx.re/join/abc123",
                "[    4000] [b3095_GTAProcess]             MainThrd/ Failed to load model blista2");
            var f = LogParser.Parse(log, "test.log");
            Assert.Single(f.ScriptErrors);
            Assert.DoesNotContain("^1", f.ScriptErrors[0]);
            Assert.Contains("cfx.re/join/abc123", f.Servers);
            Assert.Contains(f.ErrorLines, l => l.Contains("Failed to load model"));
            Assert.Equal(5, f.TailLines.Count);
            Assert.True(CrashTextParser.LooksLikeLog(log));
        }
    }

    public class MinidumpTests
    {
        static MinidumpBuilder GameCrash()
        {
            var b = new MinidumpBuilder();
            b.Modules.Add((@"C:\Users\Max\AppData\Local\FiveM\FiveM.app\data\cache\subprocess\FiveM_b3095_GTAProcess.exe", 0x7FF600000000, 0x4000000));
            b.Modules.Add((@"C:\Windows\System32\ntdll.dll", 0x7FFA00000000, 0x200000));
            b.Modules.Add((@"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64\nvwgf2umx.dll", 0x7FF900000000, 0x3000000));
            b.Modules.Add((@"C:\Program Files (x86)\RivaTuner Statistics Server\RTSSHooks64.dll", 0x7FF800000000, 0x100000));
            b.ExceptionAddress = 0x7FF900000000 + 0x5A1B2C;
            b.StackValues.AddRange(new ulong[]
            {
                0x1234, 0x7FF900000000 + 0x5A1000, 0x7FF900000000 + 0x5A1100, 0xDEADBEEF,
                0x7FF800000000 + 0x2345, 0x7FF600000000 + 0x16B5F3A, 0x7FF600000000 + 0x100 /* PE-Kopf, zählt nicht */
            });
            return b;
        }

        [Fact]
        public void Ausnahme_Modul_Offset_und_Laufzeit()
        {
            var info = MinidumpReader.Read(new MemoryStream(GameCrash().Build()));
            Assert.Empty(info.Warnings);
            Assert.Equal(0xC0000005u, info.ExceptionCode);
            Assert.Equal("nvwgf2umx.dll", info.FaultModule.Name);
            Assert.Equal(0x5A1B2CUL, info.FaultOffset);
            Assert.Equal("nvwgf2umx.dll+5A1B2C", info.Signature);
            Assert.Equal(TimeSpan.FromSeconds(12), info.Uptime);
            Assert.Equal("fivem_b3095_gtaprocess.exe", info.MainModuleName);
            Assert.Equal("1.2.3.4", info.Modules[0].FileVersion);
            Assert.Equal((ushort)9, info.ProcessorArchitecture);
            Assert.Equal(26100u, info.OsBuild);
            Assert.Equal("AuthenticAMD", info.CpuVendor);
            Assert.Equal(new[] { "nvwgf2umx.dll", "rtsshooks64.dll", "fivem_b3095_gtaprocess.exe" }, info.StackModules.ToArray());
        }

        [Fact]
        public void Modulliste_mit_Ausrichtungsbytes()
        {
            var b = GameCrash();
            b.PadModuleList = true;
            var info = MinidumpReader.Read(new MemoryStream(b.Build()));
            Assert.Equal(4, info.Modules.Count);
            Assert.Equal("ntdll.dll", info.Modules[1].Name);
        }

        [Fact]
        public void Spielprozess_wird_wie_bei_FiveM_umbenannt()
        {
            Assert.Equal("GTA5_b3095.exe+16B5F3A", MinidumpReader.BuildSignature("fivem_b3095_gtaprocess.exe", 0x16B5F3A, false));
            Assert.Equal("rtsshooks64.dll_unloaded+10", MinidumpReader.BuildSignature("rtsshooks64.dll", 0x10, true));
        }

        [Fact]
        public void Kaputte_Dateien_werfen_verstaendliche_Fehler()
        {
            Assert.Throws<InvalidDataException>(() => MinidumpReader.Read(new MemoryStream(new byte[64])));
            var truncated = GameCrash().Build().Take(200).ToArray();
            var info = MinidumpReader.Read(new MemoryStream(truncated)); // darf nicht abstürzen
            Assert.NotEmpty(info.Warnings);
        }

        [Fact]
        public void Echter_Dump_des_eigenen_Prozesses()
        {
            var path = Path.Combine(Path.GetTempPath(), "crashdoctor-selftest-" + Guid.NewGuid().ToString("N") + ".dmp");
            try
            {
                using (var p = Process.GetCurrentProcess())
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                {
                    Assert.True(MiniDumpWriteDump(p.Handle, (uint)p.Id, fs.SafeFileHandle, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero),
                        "MiniDumpWriteDump fehlgeschlagen: " + Marshal.GetLastWin32Error());
                }
                var info = MinidumpReader.Read(path);
                Assert.Empty(info.Warnings);
                Assert.Contains(info.Modules, m => m.Name == "ntdll.dll");
                Assert.True(info.Modules.Count > 10);
                Assert.True(info.OsBuild >= 10240);
                Assert.Null(info.ExceptionCode);
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }

        /// <summary>Schreibt einen Beispiel-Dump für Bildschirmfotos und Handtests, nur wenn CRASHDOCTOR_DEMO_DUMP gesetzt ist.</summary>
        [Fact]
        public void Beispiel_Dump_fuer_Handtests()
        {
            var target = Environment.GetEnvironmentVariable("CRASHDOCTOR_DEMO_DUMP");
            var bytes = GameCrash().Build();
            Assert.NotNull(MinidumpReader.Read(new MemoryStream(bytes)).Signature);
            if (string.IsNullOrEmpty(target)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllBytes(target, bytes);
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeFileHandle hFile, int dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
    }

    public class AnalyzerTests
    {
        [Fact]
        public void Treiberabsturz_aus_Dump_mit_Overlay_auf_dem_Stack()
        {
            var f = new CrashFacts();
            var dump = MinidumpReader.Read(new MemoryStream(new MinidumpTestData().Build()));
            CrashMerger.ApplyDump(dump, "test.dmp", f);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("fault-gpu", r.Top.Id);
            Assert.Contains(FixIds.GpuDriver, r.Top.FixIds);
            Assert.Contains(r.Diagnoses, d => d.Id == "stack-RivaTuner Statistics Server (MSI Afterburner)");
            Assert.Contains(r.Diagnoses, d => d.Id == "uptime-early");
            Assert.Contains(r.Diagnoses, d => d.Id == "server-or-client");
        }

        [Fact]
        public void Bekannter_Crashname_steht_oben()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("Crash signature: GTA5_b3095.exe+16B5F3A\nLegacy crash hash: sweet-sad-july", f);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("known-sweet-sad-july", r.Top.Id);
            Assert.Equal(Evidence.Belegt, r.Top.Evidence);
        }

        [Fact]
        public void Ohne_Daten_keine_Diagnose()
        {
            var r = Analyzer.Analyze(new CrashFacts(), new SystemSnapshot());
            Assert.Empty(r.Diagnoses);
        }

        [Fact]
        public void Fehlende_Auslagerungsdatei_wird_gemeldet()
        {
            var s = new SystemSnapshot { Collected = true, TotalRamBytes = 16UL << 30, PagefileAllocatedMb = 0, PagefileAutomatic = false };
            var f = new CrashFacts();
            CrashTextParser.Parse("ERR_MEM_EMBEDDEDALLOC_ALLOC", f);
            var r = Analyzer.Analyze(f, s);
            var d = r.Diagnoses.Single(x => x.Id == "sys-no-pagefile");
            Assert.Contains(FixIds.PagefileAuto, d.FixIds);
        }

        [Fact]
        public void Fremd_DLL_mit_Pfad_aus_dem_Dump()
        {
            var b = new MinidumpBuilder();
            b.Modules.Add((@"C:\FiveM\FiveM.app\FiveM_b3095_GTAProcess.exe", 0x140000000, 0x4000000));
            b.Modules.Add((@"C:\Program Files\FancyRGB\fancyhook64.dll", 0x7FF800000000, 0x100000));
            b.ExceptionAddress = 0x7FF800000000 + 0x42;
            var f = new CrashFacts();
            CrashMerger.ApplyDump(MinidumpReader.Read(new MemoryStream(b.Build())), "x.dmp", f);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("fault-unknown", r.Top.Id);
            Assert.Equal(Severity.Critical, r.Top.Severity);
            Assert.Contains("FancyRGB", r.Top.Explanation);
        }

        [Fact]
        public void Bericht_entfernt_den_Benutzernamen()
        {
            var f = new CrashFacts();
            var dump = MinidumpReader.Read(new MemoryStream(new MinidumpTestData().Build()));
            CrashMerger.ApplyDump(dump, "test.dmp", f);
            f.Warnings.Add(@"Pfad C:\Users\Max\AppData\Local\FiveM");
            var r = Analyzer.Analyze(f, null);
            var md = ReportWriter.Markdown(r, "1.0", "Max");
            Assert.DoesNotContain(@"\Users\Max", md);
            Assert.Contains(@"\Users\<Benutzer>", md);
            var dc = ReportWriter.Discord(r, "Max");
            Assert.True(dc.Length <= 1900);
            Assert.Contains("nvwgf2umx.dll+5A1B2C", dc);
        }

        [Fact]
        public void Notbremse_von_FiveM_wird_im_Dump_erkannt()
        {
            // Ausführen-Fehler bei Basis+0x1688 des Spielprozesses = isAccessDeath in MiniDump.cpp (Fall aus einem echten Dump)
            var b = new MinidumpBuilder();
            b.Modules.Add((@"D:\FiveM\FiveM.app\data\cache\subprocess\FiveM_b3570_GTAProcess.exe", 0x7FF6ACC70000, 0x5000000));
            b.Modules.Add((@"D:\FiveM\FiveM.app\plugins\NVE.asi", 0x7FF800000000, 0x100000));
            b.ExceptionAddress = 0x7FF6ACC71688;
            b.ExceptionParams = new ulong[] { 8, 0x7FF6ACC71688 };
            var f = new CrashFacts();
            CrashMerger.ApplyDump(MinidumpReader.Read(new MemoryStream(b.Build())), "x.dmp", f);
            Assert.True(f.EarlyExitTrap);
            Assert.Equal("GTA5_b3570.exe+1688", f.Signature);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("early-exit", r.Top.Id);
            Assert.Contains("nve.asi", r.Top.Explanation);
            Assert.DoesNotContain(r.Diagnoses, d => d.Id == "fault-game");
        }

        [Fact]
        public void Gleicher_Name_anderer_Pfad_ReShade_statt_DirectX()
        {
            var b = new MinidumpBuilder();
            b.Modules.Add((@"D:\FiveM\FiveM.app\data\cache\subprocess\FiveM_b3570_GTAProcess.exe", 0x140000000, 0x5000000));
            b.Modules.Add((@"C:\Windows\System32\dxgi.dll", 0x7FFA00000000, 0x100000));
            b.Modules.Add((@"D:\FiveM\FiveM.app\plugins\dxgi.dll", 0x7FF800000000, 0x200000));
            b.Modules.Add((@"D:\FiveM\FiveM.app\scripthookv.dll", 0x7FF700000000, 0x100000));
            b.Modules.Add((@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18\MpOAV.dll", 0x7FF710000000, 0x100000));
            b.ExceptionAddress = 0x7FF800000000 + 0x4242;
            var f = new CrashFacts();
            CrashMerger.ApplyDump(MinidumpReader.Read(new MemoryStream(b.Build())), "x.dmp", f);
            var r = Analyzer.Analyze(f, null);
            Assert.Equal("fault-graphicsmod", r.Top.Id);
            Assert.Contains(FixIds.DisablePlugins, r.Top.FixIds);
            var third = ReportWriter.ThirdPartyModules(f, null);
            Assert.Contains(third, t => t.StartsWith("dxgi.dll (ReShade"));
            Assert.DoesNotContain(third, t => t.StartsWith("mpoav.dll"));       // Windows Defender
            Assert.DoesNotContain(third, t => t.StartsWith("scripthookv.dll")); // FiveM-eigene Datei
        }

        [Fact]
        public void Spielprotokoll_gamelog()
        {
            const string log = "###### DEBUG INFO ######\n\n============ SYSTEM INFO ============\nGame State : Game Running\nStreaming Memory Occupancy :\n" +
                "  Physical : used(480Mb) - available(511Mb)\n  Virtual  : used(740Mb) - available(767Mb)\nSystem Memory :\n  Memory Load       : (        55%)\n" +
                "  Physical Total    : (     63129Mb)\n  Physical Free     : (     33880Mb)\n  PageFile Total    : (     82585Mb)\n  PageFile Free     : (      1200Mb)\n\n" +
                "============ MISC INFO ============\nVersion : 3570.0-dev_ng_Live\n\n============ POOL INFO ============\nPool, StorageSize, Size, Used, Free" +
                "CScriptEntityExtension, 0000020A1344EB60, 120, 10000, 0\nCGameScriptHandler, 0000020A1344EC20, 160, 750, 750\n\n" +
                "============ ASSET STORES INFO ============\nStore, Size, Used\nCutSceneStore, 1400, 663ScaleformStore, 852, 852DrawableStore, 235300, 86742\n" +
                "============ PACK INFO ============\n";
            var g = GameLogParser.Parse(log, "a.dmp.gamelog");
            Assert.Equal("Game Running", g.GameState);
            Assert.Equal(740, g.StreamingVirtualUsedMb);
            Assert.Equal(1200, g.PageFileFreeMb);
            Assert.Equal(new[] { "CGameScriptHandler (750/750)" }, g.FullPools.ToArray());
            Assert.Equal(new[] { "ScaleformStore (852/852)" }, g.FullStores.ToArray());
            var f = new CrashFacts { GameLog = g };
            var r = Analyzer.Analyze(f, null);
            Assert.Contains(r.Diagnoses, d => d.Id == "gamelog-streaming-full");
            Assert.Contains(r.Diagnoses, d => d.Id == "gamelog-commit-low");
            Assert.Contains(r.Diagnoses, d => d.Id == "gamelog-pool-full");
        }

        [Fact]
        public void Signaturvergleich_Text_gegen_Dump()
        {
            var f = new CrashFacts();
            CrashTextParser.Parse("Crash signature: GTA5_b3095.exe+111", f);
            CrashMerger.ApplyDump(MinidumpReader.Read(new MemoryStream(new MinidumpTestData().Build())), "a.dmp", f);
            Assert.Contains(f.Warnings, w => w.Contains("andere Absturzstelle"));
            Assert.Equal("GTA5_b3095.exe+111", f.Signature); // der Text hat Vorrang
        }

        sealed class MinidumpTestData
        {
            public byte[] Build()
            {
                var b = new MinidumpBuilder();
                b.Modules.Add((@"C:\Users\Max\AppData\Local\FiveM\FiveM.app\FiveM_b3095_GTAProcess.exe", 0x7FF600000000, 0x4000000));
                b.Modules.Add((@"C:\Windows\System32\DriverStore\FileRepository\nv\nvwgf2umx.dll", 0x7FF900000000, 0x3000000));
                b.Modules.Add((@"C:\Program Files (x86)\RivaTuner Statistics Server\RTSSHooks64.dll", 0x7FF800000000, 0x100000));
                b.ExceptionAddress = 0x7FF900000000 + 0x5A1B2C;
                b.StackValues.AddRange(new ulong[] { 0x7FF900000000 + 0x5A1000, 0x7FF800000000 + 0x2345 });
                return b.Build();
            }
        }
    }
}
