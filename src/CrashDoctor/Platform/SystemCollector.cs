using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using CrashDoctor.Core;
using Microsoft.Win32;

namespace CrashDoctor.Platform
{
    /// <summary>Misst den PC und füllt das SystemSnapshot. Jede Quelle liefert Daten oder einen Eintrag in CollectErrors.</summary>
    public static class SystemCollector
    {
        public static SystemSnapshot Collect(FiveMPaths paths, CancellationToken ct = default(CancellationToken))
        {
            var s = new SystemSnapshot { Collected = true, IsElevated = IsElevated() };
            Step(s, "Windows-Version", () => ReadOs(s));
            Step(s, "Arbeitsspeicher", () => ReadMemory(s));
            Step(s, "Auslagerungsdatei", () => ReadPagefile(s));
            Step(s, "Grafikkarte", () => ReadGpus(s));
            Step(s, "Prozesse", () => ReadProcesses(s));
            ct.ThrowIfCancellationRequested();

            s.FiveMFound = paths.Found;
            s.FiveMAppPath = paths.AppDir;
            s.GtaPath = paths.GtaDir;
            s.GtaPlatform = paths.GtaPlatform;
            Step(s, "Laufwerke", () => ReadDrives(s, paths));
            if (paths.Found)
            {
                Step(s, "FiveM-Plugins", () => s.FiveMPlugins.AddRange(ListPlugins(paths.PluginsDir)));
                Step(s, "Update-Kanal", () => s.UpdateChannel = FiveMPaths.IniRead(paths.CitizenFxIni, "Game", "UpdateChannel"));
                Step(s, "Crash-Dumps", () =>
                {
                    if (!Directory.Exists(paths.CrashesDir)) return;
                    var dumps = new DirectoryInfo(paths.CrashesDir).GetFiles("*.dmp");
                    s.CrashDumpCount = dumps.Length;
                    s.CrashDumpsLast7Days = dumps.Count(d => d.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7));
                });
                Step(s, "Cache-Größe", () => s.FiveMCacheBytes = paths.CacheDirs.Sum(d => DirectorySize(d, TimeSpan.FromSeconds(4))));
            }
            if (!string.IsNullOrEmpty(paths.GtaDir))
                Step(s, "GTA-V-Ordner", () => s.GtaGraphicsMods.AddRange(ListGtaMods(paths.GtaDir)));
            return s;
        }

        public static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        /// <summary>FiveM.exe und seine Kindprozesse (FiveM_b3095_GTAProcess, FiveM_ChromeBrowser …) – nicht dieses Tool.</summary>
        public static bool IsFiveMProcessName(string name) =>
            !string.IsNullOrEmpty(name) &&
            (name.Equals("FiveM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FiveM_", StringComparison.OrdinalIgnoreCase));

        public static bool IsFiveMRunning() =>
            Process.GetProcesses().Any(p =>
            {
                try { return IsFiveMProcessName(p.ProcessName); }
                catch (InvalidOperationException) { return false; }
                finally { p.Dispose(); }
            });

        static void Step(SystemSnapshot s, string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { s.CollectErrors.Add($"{what}: {ex.GetType().Name} – {ex.Message}"); AppLog.Write($"Messung {what} fehlgeschlagen: {ex}"); }
        }

        static void ReadOs(SystemSnapshot s)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k == null) return;
                var name = k.GetValue("ProductName") as string ?? "Windows";
                int.TryParse(k.GetValue("CurrentBuild") as string, out var build);
                // ProductName sagt auch auf Windows 11 „Windows 10“; die Buildnummer entscheidet
                if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                var display = k.GetValue("DisplayVersion") as string;
                s.OsName = string.IsNullOrEmpty(display) ? name : $"{name} {display}";
                s.OsBuild = build;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        static void ReadMemory(SystemSnapshot s)
        {
            var m = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(m)) throw new InvalidOperationException("GlobalMemoryStatusEx fehlgeschlagen");
            s.TotalRamBytes = m.TotalPhys;
            s.AvailableRamBytes = m.AvailPhys;
        }

        static IEnumerable<ManagementObject> Wmi(string query)
        {
            var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(8), ReturnImmediately = true, Rewindable = false };
            using (var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(query), options))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject o in results)
                    using (o) yield return o;
            }
        }

        static void ReadPagefile(SystemSnapshot s)
        {
            foreach (var cs in Wmi("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"))
                s.PagefileAutomatic = cs["AutomaticManagedPagefile"] as bool?;
            long total = 0;
            foreach (var pf in Wmi("SELECT AllocatedBaseSize FROM Win32_PageFileUsage"))
                total += Convert.ToInt64(pf["AllocatedBaseSize"] ?? 0);
            s.PagefileAllocatedMb = total;
        }

        static void ReadGpus(SystemSnapshot s)
        {
            foreach (var vc in Wmi("SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterCompatibility FROM Win32_VideoController"))
            {
                var name = (vc["Name"] as string ?? "").Trim();
                var pnp = (vc["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                var compat = (vc["AdapterCompatibility"] as string ?? "").ToUpperInvariant();
                var vendor = pnp.Contains("VEN_10DE") || compat.Contains("NVIDIA") ? "NVIDIA"
                           : pnp.Contains("VEN_1002") || compat.Contains("ADVANCED MICRO") || compat.Contains("AMD") ? "AMD"
                           : pnp.Contains("VEN_8086") || compat.Contains("INTEL") ? "Intel" : "Andere";
                var gpu = new GpuInfo { Name = name, Vendor = vendor, DriverVersion = vc["DriverVersion"] as string };
                var date = vc["DriverDate"] as string;
                if (!string.IsNullOrEmpty(date))
                {
                    try { gpu.DriverDate = ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime(); }
                    catch (ArgumentOutOfRangeException) { }
                }
                if (vendor == "NVIDIA") gpu.FriendlyDriverVersion = NvidiaVersion(gpu.DriverVersion);
                s.Gpus.Add(gpu);
            }
            // dedizierte Karte zuerst; „AMD Radeon(TM) Graphics“ ohne Modellnummer ist die Prozessorgrafik
            var ordered = s.Gpus.OrderBy(g =>
                g.Vendor == "NVIDIA" ? 0 :
                g.Vendor == "AMD" && !IsIntegratedAmd(g.Name) ? 0 :
                g.Vendor == "AMD" || g.Vendor == "Intel" ? 1 : 2).ToList();
            s.Gpus.Clear();
            s.Gpus.AddRange(ordered);
        }

        static bool IsIntegratedAmd(string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"^AMD Radeon(\(TM\))? (Graphics|Vega \d+ Graphics|\d{3}M)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Windows-Treiberversion 32.0.15.6094 → NVIDIA-Version 560.94 (letzte Ziffer des dritten Teils + vierter Teil).</summary>
        public static string NvidiaVersion(string windowsVersion)
        {
            var parts = (windowsVersion ?? "").Split('.');
            if (parts.Length != 4 || parts[2].Length == 0 || !int.TryParse(parts[3], out var last)) return null;
            var digits = parts[2].Substring(parts[2].Length - 1) + last.ToString("0000");
            return digits.Substring(0, 3) + "." + digits.Substring(3);
        }

        static void ReadProcesses(SystemSnapshot s)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName.ToLowerInvariant();
                    if (!s.RunningProcesses.Contains(n)) s.RunningProcesses.Add(n);
                    if (IsFiveMProcessName(n)) s.FiveMRunning = true;
                }
                catch (InvalidOperationException) { }
                finally { p.Dispose(); }
            }
        }

        static void ReadDrives(SystemSnapshot s, FiveMPaths paths)
        {
            var roles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, string role)
            {
                if (string.IsNullOrEmpty(path)) return;
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return;
                if (!roles.TryGetValue(root, out var list)) roles[root] = list = new List<string>();
                if (!list.Contains(role)) list.Add(role);
            }
            Add(Environment.SystemDirectory, "System");
            Add(paths.AppDir, "FiveM");
            Add(paths.GtaDir, "GTA V");
            foreach (var kv in roles)
            {
                var di = new DriveInfo(kv.Key);
                if (!di.IsReady) continue;
                s.Drives.Add(new DriveSpace { Root = kv.Key.TrimEnd('\\'), Role = string.Join(", ", kv.Value), FreeBytes = di.AvailableFreeSpace, TotalBytes = di.TotalSize });
            }
        }

        public static List<string> ListPlugins(string pluginsDir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(pluginsDir) || !Directory.Exists(pluginsDir)) return list;
            foreach (var f in Directory.EnumerateFileSystemEntries(pluginsDir))
                list.Add(Path.GetFileName(f) + (Directory.Exists(f) ? "\\" : ""));
            return list;
        }

        public static List<string> ListGtaMods(string gtaDir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(gtaDir) || !Directory.Exists(gtaDir)) return list;
            foreach (var f in Directory.EnumerateFiles(gtaDir))
            {
                var name = Path.GetFileName(f);
                if (ModuleCatalog.GtaModFileNames.Contains(name) || name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)) list.Add(name);
            }
            foreach (var folder in new[] { "reshade-shaders", "enbseries" })
                if (Directory.Exists(Path.Combine(gtaDir, folder))) list.Add(folder + "\\");
            return list;
        }

        public static long DirectorySize(string dir, TimeSpan budget)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            var sw = Stopwatch.StartNew();
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && sw.Elapsed < budget)
            {
                var d = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(d);
                    foreach (var f in di.EnumerateFiles()) total += f.Length;
                    foreach (var sub in di.EnumerateDirectories())
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub.FullName);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return total;
        }
    }
}
