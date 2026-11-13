using System;
using System.IO;
using System.Reflection;

namespace CrashDoctor.Platform
{
    /// <summary>Kleines Protokoll unter %LOCALAPPDATA%\FiveMCrashDoctor\logs – begrenzt auf zwei Dateien zu je 1 MB.</summary>
    public static class AppLog
    {
        static readonly object Gate = new object();
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveMCrashDoctor", "logs");
        static string FilePath => Path.Combine(Folder, "app.log");

        public static string Version =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static void Start(string mode)
        {
            Write($"=== FiveM Crash Doctor {Version} · {Environment.OSVersion.VersionString} · .NET {Environment.Version} · 64 Bit: {Environment.Is64BitProcess} · Modus: {mode}");
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
