using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CrashDoctor.Platform
{
    /// <summary>
    /// Pfade von FiveM und GTA V. Aufbau nachgelesen im FiveM-Quelltext:
    /// CitizenFX.ini liegt in FiveM.app ([Game] IVPath = GTA-Ordner, UpdateChannel),
    /// Einstellungen und fivem.cfg liegen in %APPDATA%\CitizenFX.
    /// </summary>
    public sealed class FiveMPaths
    {
        public string AppDir { get; private set; }            // …\FiveM\FiveM.app
        public string GtaDir { get; private set; }
        public string GtaPlatform { get; private set; }

        public bool Found => AppDir != null && Directory.Exists(AppDir);
        public string CrashesDir => Combine(AppDir, "crashes");
        public string LogsDir => Combine(AppDir, "logs");
        public string PluginsDir => Combine(AppDir, "plugins");
        public string DataDir => Combine(AppDir, "data");
        public string CitizenFxIni => Combine(AppDir, "CitizenFX.ini");
        public IEnumerable<string> CacheDirs => new[] { "cache", "server-cache", "server-cache-priv" }.Select(n => Combine(DataDir, n));

        public static string RoamingCitizenFx => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CitizenFX");
        public static string FiveMSettingsXml => Path.Combine(RoamingCitizenFx, "gta5_settings.xml");
        public static string GtaSettingsXml => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games", "GTA V", "settings.xml");

        public static FiveMPaths Detect(string preferredAppDir = null)
        {
            var p = new FiveMPaths();
            foreach (var candidate in Candidates(preferredAppDir))
            {
                if (candidate != null && Directory.Exists(candidate)) { p.AppDir = candidate; break; }
            }
            p.ResolveGta();
            return p;
        }

        public static FiveMPaths FromKnown(string appDir, string gtaDir)
        {
            var p = new FiveMPaths { AppDir = string.IsNullOrEmpty(appDir) ? null : appDir };
            if (!string.IsNullOrEmpty(gtaDir)) { p.GtaDir = gtaDir; p.GtaPlatform = PlatformOf(gtaDir); }
            else p.ResolveGta();
            return p;
        }

        static IEnumerable<string> Candidates(string preferred)
        {
            if (!string.IsNullOrEmpty(preferred))
            {
                yield return preferred;
                yield return Path.Combine(preferred, "FiveM.app");
            }
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.app");

            // fivem://-Protokoll: HKCU\Software\Classes\fivem\shell\open\command → Pfad zu FiveM.exe
            string cmd = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\fivem\shell\open\command"))
                    cmd = k?.GetValue(null) as string;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            var exe = ExtractExe(cmd);
            if (exe != null) yield return Path.Combine(Path.GetDirectoryName(exe) ?? "", "FiveM.app");

            // läuft FiveM gerade? Dann kennt der Prozess seinen Pfad
            foreach (var running in RunningFiveMExes()) yield return Path.Combine(Path.GetDirectoryName(running) ?? "", "FiveM.app");

            // Verknüpfungen im Startmenü und auf dem Desktop
            foreach (var target in ShortcutTargets()) yield return Path.Combine(Path.GetDirectoryName(target) ?? "", "FiveM.app");

            // übliche eigene Ordner auf allen festen Laufwerken (z. B. D:\FiveM)
            foreach (var drive in SafeDrives())
                foreach (var sub in new[] { "FiveM", @"Games\FiveM", @"Spiele\FiveM", @"Program Files\FiveM" })
                    yield return Path.Combine(drive, sub, "FiveM.app");
        }

        static IEnumerable<string> RunningFiveMExes()
        {
            var list = new List<string>();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("FiveM"))
            {
                try { list.Add(p.MainModule.FileName); }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                finally { p.Dispose(); }
            }
            return list;
        }

        static IEnumerable<string> ShortcutTargets()
        {
            var places = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            var result = new List<string>();
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return result;
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                foreach (var place in places.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)))
                {
                    IEnumerable<string> links;
                    try { links = Directory.EnumerateFiles(place, "FiveM*.lnk", SearchOption.AllDirectories).Take(10).ToList(); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                    foreach (var link in links)
                    {
                        object sc = null;
                        try
                        {
                            sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                            var target = sc?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                            if (!string.IsNullOrEmpty(target) && target.EndsWith("FiveM.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                                result.Add(target);
                        }
                        catch (Exception ex) when (ex is COMException || ex is System.Reflection.TargetInvocationException || ex is MissingMethodException) { }
                        finally { if (sc != null && Marshal.IsComObject(sc)) Marshal.FinalReleaseComObject(sc); }
                    }
                }
            }
            catch (COMException) { }
            finally { if (shell != null) Marshal.FinalReleaseComObject(shell); }
            return result;
        }

        static IEnumerable<string> SafeDrives()
        {
            try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
            catch (IOException) { return Enumerable.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        static string ExtractExe(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim();
            string path;
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                path = end > 1 ? command.Substring(1, end - 1) : null;
            }
            else
            {
                int end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = end > 0 ? command.Substring(0, end + 4) : null;
            }
            return path != null && File.Exists(path) ? path : null;
        }

        void ResolveGta()
        {
            var fromIni = IniRead(CitizenFxIni, "Game", "IVPath");
            foreach (var dir in new[] { fromIni, RegistryGtaDir(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V", "InstallFolder"),
                                        RegistryGtaDir(@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV", "InstallFolderSteam"),
                                        RegistryGtaDir(@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV", "InstallFolderEpic") })
            {
                if (IsGtaDir(dir)) { GtaDir = dir.TrimEnd('\\'); GtaPlatform = PlatformOf(GtaDir); return; }
            }
        }

        static bool IsGtaDir(string dir)
        {
            try { return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "GTA5.exe")); }
            catch (ArgumentException) { return false; }
        }

        static string RegistryGtaDir(string key, string value)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(key.Replace(@"WOW6432Node\", "")) ?? hklm.OpenSubKey(key))
                {
                    var v = k?.GetValue(value) as string;
                    if (string.IsNullOrWhiteSpace(v)) return null;
                    v = v.Trim('"');
                    if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = Path.GetDirectoryName(v);
                    // Steam schreibt teils „…\Grand Theft Auto V\GTAV“
                    if (v != null && !IsGtaDir(v) && IsGtaDir(Path.GetDirectoryName(v))) v = Path.GetDirectoryName(v);
                    return v;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is IOException)
            {
                return null;
            }
        }

        public static string PlatformOf(string gtaDir)
        {
            if (string.IsNullOrEmpty(gtaDir)) return null;
            var d = gtaDir.ToLowerInvariant();
            if (d.Contains("steamapps")) return "Steam";
            if (d.Contains("epic games")) return "Epic";
            return "Rockstar";
        }

        static string Combine(string a, string b) => a == null ? null : Path.Combine(a, b);

        // ------------------------------------------------------------ INI über die Windows-API (wie FiveM selbst)

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder ret, int size, string file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        public static string IniRead(string file, string section, string key)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            var sb = new StringBuilder(1024);
            GetPrivateProfileString(section, key, "", sb, sb.Capacity, file);
            var v = sb.ToString().Trim();
            return v.Length == 0 ? null : v;
        }

        public static bool IniWrite(string file, string section, string key, string value) =>
            WritePrivateProfileString(section, key, value, file);
    }
}
