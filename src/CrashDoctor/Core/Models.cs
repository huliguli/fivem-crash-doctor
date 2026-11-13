using System;
using System.Collections.Generic;
using System.Linq;

// Kern-Schicht: reine Daten und Logik. Kein WPF, kein WMI, kein Prozessstart,
// keine Registry. Alles, was Windows misst, kommt als SystemSnapshot herein.
namespace CrashDoctor.Core
{
    /// <summary>Wie belastbar eine Aussage ist (angelehnt an die Belegklassen der FIVEM-LIBRARY).</summary>
    public enum Evidence
    {
        /// <summary>Steht im FiveM-Quelltext oder in der offiziellen Doku.</summary>
        Belegt,
        /// <summary>Verbreiteter Erfahrungswert aus der Community.</summary>
        Praxis,
        /// <summary>Eigener Schluss, nicht belegt.</summary>
        Vermutung
    }

    public enum Severity { Info = 0, Hint = 1, Warning = 2, Critical = 3 }

    public enum Confidence { Low = 1, Medium = 2, High = 3 }

    public sealed class ModuleInfo
    {
        public string Name { get; set; }          // Dateiname, klein geschrieben
        public string Path { get; set; }          // voller Pfad aus dem Dump
        public ulong Base { get; set; }
        public uint Size { get; set; }
        public string FileVersion { get; set; }
        public bool Unloaded { get; set; }

        public bool Contains(ulong address) => address >= Base && address < Base + Size;

        public override string ToString() => Name;
    }

    /// <summary>Was aus einem Client-Log (CitizenFX_log_*.log) herausgelesen wurde.</summary>
    public sealed class LogFacts
    {
        public string SourceName { get; set; }
        public int LineCount { get; set; }
        public List<string> ScriptErrors { get; } = new List<string>();
        public List<string> ErrorLines { get; } = new List<string>();
        public List<string> TailLines { get; } = new List<string>();
        public List<string> Servers { get; } = new List<string>();
        public bool MentionsOutOfMemory { get; set; }
    }

    /// <summary>
    /// Alle Fakten zu einem Absturz, zusammengeführt aus Crash-Text, Dump, Log und crashometry.
    /// </summary>
    public sealed class CrashFacts
    {
        // Identität des Absturzes
        public string Signature { get; set; }       // z. B. GTA5_b3095.exe+16B5F3A
        public string FaultModule { get; set; }     // klein: gta5_b3095.exe, nvwgf2umx.dll …
        public ulong? FaultOffset { get; set; }
        public string FaultSymbol { get; set; }
        public bool FaultInUnloadedModule { get; set; }
        public string LegacyHash { get; set; }      // drei Wörter, z. B. tennis-stairway-seven
        public string ReportId { get; set; }
        public int? GameBuild { get; set; }

        // Ausnahme
        public uint? ExceptionCode { get; set; }
        public ulong? ExceptionAddress { get; set; }
        public ulong[] ExceptionParameters { get; set; } = new ulong[0];
        public string CppExceptionType { get; set; }
        public string CppExceptionDetails { get; set; }

        // Meldungen aus dem Dialog
        public List<string> ErrorCodes { get; } = new List<string>();   // ERR_GFX_D3D_INIT …
        public List<string> DirectXErrors { get; } = new List<string>(); // DXGI_ERROR_DEVICE_REMOVED …
        public string DeviceRemovedReason { get; set; }
        public bool DirectXFatal { get; set; }
        public string GraphicsModsInMessage { get; set; }
        public bool IntegrityCheckFailed { get; set; }
        public bool EarlyExitTrap { get; set; }
        public string DisconnectMessage { get; set; }
        public string FatalErrorMessage { get; set; }
        public string BlamedBy { get; set; }
        public bool DialogRecognized { get; set; }
        public bool DialogIsGerman { get; set; }
        public List<string> StackTraceLines { get; } = new List<string>();

        // Aus dem Dump
        public bool HasDump { get; set; }
        public string DumpFileName { get; set; }
        public string CrashedProcess { get; set; }
        public List<ModuleInfo> LoadedModules { get; } = new List<ModuleInfo>();
        public List<ModuleInfo> UnloadedModules { get; } = new List<ModuleInfo>();
        public List<string> StackModules { get; } = new List<string>();
        public List<ModuleInfo> StackModuleInfos { get; } = new List<ModuleInfo>();
        public bool EarlyExitFromDump { get; set; }
        public ModuleInfo FaultModuleInfo { get; set; }
        public TimeSpan? Uptime { get; set; }
        public DateTime? CrashTimeUtc { get; set; }
        public string DumpOsVersion { get; set; }
        public string DumpCpuVendor { get; set; }
        public int? DumpProcessorCount { get; set; }
        public int OtherDumpCount { get; set; }              // weitere eingelesene Dumps
        public int OtherDumpsSameSignature { get; set; }     // davon mit derselben Absturzstelle

        // Aus Log, .gamelog und crashometry
        public LogFacts Log { get; set; }
        public GameLogFacts GameLog { get; set; }
        public Dictionary<string, string> Crashometry { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string CefConsoleTail { get; set; }

        // Herkunft und Probleme beim Einlesen
        public List<string> Sources { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public bool HasAnyData =>
            !string.IsNullOrEmpty(Signature) || HasDump || Log != null || GameLog != null || ErrorCodes.Count > 0 ||
            DirectXErrors.Count > 0 || !string.IsNullOrEmpty(LegacyHash) || IntegrityCheckFailed ||
            EarlyExitTrap || !string.IsNullOrEmpty(DisconnectMessage) || !string.IsNullOrEmpty(FatalErrorMessage) ||
            ExceptionCode.HasValue || !string.IsNullOrEmpty(CppExceptionType);

        public bool HasModuleLoaded(string fileNameLower) =>
            LoadedModules.Any(m => m.Name == fileNameLower);
    }

    public sealed class GpuInfo
    {
        public string Name { get; set; }
        public string Vendor { get; set; }          // NVIDIA, AMD, Intel, Andere
        public string DriverVersion { get; set; }   // Windows-Schreibweise 32.0.15.6094
        public string FriendlyDriverVersion { get; set; } // NVIDIA: 560.94
        public DateTime? DriverDate { get; set; }
    }

    public sealed class DriveSpace
    {
        public string Root { get; set; }
        public string Role { get; set; }   // "System", "FiveM", "GTA V"
        public long FreeBytes { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// Messwerte des PCs. Wird von der Windows-Schicht gefüllt; der Kern deutet nur Zahlen und Kennungen.
    /// </summary>
    public sealed class SystemSnapshot
    {
        public bool Collected { get; set; }
        public string OsName { get; set; }
        public int OsBuild { get; set; }
        public ulong TotalRamBytes { get; set; }
        public ulong AvailableRamBytes { get; set; }
        public bool? PagefileAutomatic { get; set; }
        public long? PagefileAllocatedMb { get; set; }   // Summe aller Auslagerungsdateien
        public List<GpuInfo> Gpus { get; } = new List<GpuInfo>();
        public List<DriveSpace> Drives { get; } = new List<DriveSpace>();
        public List<string> RunningProcesses { get; } = new List<string>(); // klein, ohne .exe
        public bool IsElevated { get; set; }

        // FiveM und GTA V
        public bool FiveMFound { get; set; }
        public string FiveMAppPath { get; set; }
        public string GtaPath { get; set; }
        public string GtaPlatform { get; set; }          // Steam, Epic, Rockstar, unbekannt
        public bool FiveMRunning { get; set; }
        public List<string> FiveMPlugins { get; } = new List<string>();
        public List<string> GtaGraphicsMods { get; } = new List<string>();
        public long? FiveMCacheBytes { get; set; }
        public string UpdateChannel { get; set; }
        public int CrashDumpCount { get; set; }
        public int CrashDumpsLast7Days { get; set; }

        public List<string> CollectErrors { get; } = new List<string>();

        public bool IsProcessRunning(string nameLowerWithoutExe) => RunningProcesses.Contains(nameLowerWithoutExe);
    }

    /// <summary>Eine mögliche Ursache mit Erklärung, Schritten und automatischen Fixes.</summary>
    public sealed class Diagnosis
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Explanation { get; set; }
        public Severity Severity { get; set; }
        public Confidence Confidence { get; set; }
        public Evidence Evidence { get; set; }
        public string SourceNote { get; set; }
        public List<string> Steps { get; } = new List<string>();
        public List<string> FixIds { get; } = new List<string>();
        public List<string> MatchedBy { get; } = new List<string>();

        /// <summary>Ordnungszahl: zuerst nach Schwere (Hauptverdacht vor Hinweis), innerhalb davon nach Wahrscheinlichkeit.</summary>
        public int Rank => (int)Severity * 10 + (int)Confidence * 3;

        public Diagnosis Step(string text) { Steps.Add(text); return this; }
        public Diagnosis Fix(params string[] ids) { foreach (var id in ids) if (!FixIds.Contains(id)) FixIds.Add(id); return this; }
        public Diagnosis Because(string why) { if (!MatchedBy.Contains(why)) MatchedBy.Add(why); return this; }
    }

    public sealed class AnalysisReport
    {
        public CrashFacts Facts { get; set; }
        public SystemSnapshot System { get; set; }
        public List<Diagnosis> Diagnoses { get; } = new List<Diagnosis>();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public Diagnosis Top => Diagnoses.FirstOrDefault();
    }
}
