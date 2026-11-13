using System;
using System.Collections.Generic;
using System.Linq;

namespace CrashDoctor.Core
{
    /// <summary>Kennungen der automatischen Reparaturen (Umsetzung in Platform/Fixes.cs).</summary>
    public static class FixIds
    {
        public const string ClearCache = "clear-cache";
        public const string DisablePlugins = "disable-plugins";
        public const string MoveGtaMods = "move-gta-mods";
        public const string ResetGraphics = "reset-graphics";
        public const string ClearShaderCache = "clear-shader-cache";
        public const string CloseOverlays = "close-overlays";
        public const string VerifyGame = "verify-game";
        public const string PagefileAuto = "pagefile-auto";
        public const string SystemFiles = "system-files";
        public const string GpuDriver = "gpu-driver";
        public const string VcRedist = "vcredist";
        public const string TestServer = "cfx-test-server";
        public const string UpdateChannel = "update-channel";
        public const string SupportZip = "support-zip";
    }

    public enum ModuleKind
    {
        Unknown, GpuNvidia, GpuAmd, GpuIntel, NvidiaFilter, NvidiaCapture, Overlay, Recorder, Audio, Security,
        VendorTool, AsiPlugin, GraphicsMod, FiveMScripting, FiveMStreaming, FiveMNui, FiveMNet, FiveMVoice,
        FiveMProtection, FiveMCore, RockstarLauncher, Game, WindowsCore, VcRuntime, DirectX, AudioSystem
    }

    public sealed class ModuleEntry
    {
        public string Pattern;      // klein geschrieben
        public MatchMode Mode;
        public ModuleKind Kind;
        public string Product;
        public Evidence Evidence;
        public string Note;

        public enum MatchMode { Exact, Prefix, Contains, Suffix }

        public bool Matches(string nameLower)
        {
            if (string.IsNullOrEmpty(nameLower)) return false;
            switch (Mode)
            {
                case MatchMode.Exact: return nameLower == Pattern;
                case MatchMode.Prefix: return nameLower.StartsWith(Pattern, StringComparison.Ordinal);
                case MatchMode.Suffix: return nameLower.EndsWith(Pattern, StringComparison.Ordinal);
                default: return nameLower.IndexOf(Pattern, StringComparison.Ordinal) >= 0;
            }
        }

        /// <summary>Fremdsoftware, die sich ins Spiel einklinkt und als Störenfried bekannt ist.</summary>
        public bool IsConflictSoftware =>
            Kind == ModuleKind.Overlay || Kind == ModuleKind.Recorder || Kind == ModuleKind.Audio || Kind == ModuleKind.Security ||
            Kind == ModuleKind.VendorTool || Kind == ModuleKind.AsiPlugin || Kind == ModuleKind.GraphicsMod ||
            Kind == ModuleKind.NvidiaFilter || Kind == ModuleKind.NvidiaCapture;
    }

    /// <summary>
    /// Bekannte Module. Einträge mit Evidence.Belegt stammen aus dem FiveM-Quelltext:
    /// MiniDump.cpp (Schuldzuweisung im Dialog) und FileMapping.Win32.cpp (Liste der DLLs, die FiveM blockiert).
    /// </summary>
    public static class ModuleCatalog
    {
        static ModuleEntry E(string pattern, ModuleEntry.MatchMode mode, ModuleKind kind, string product, Evidence ev, string note = null) =>
            new ModuleEntry { Pattern = pattern, Mode = mode, Kind = kind, Product = product, Evidence = ev, Note = note };

        const ModuleEntry.MatchMode X = ModuleEntry.MatchMode.Exact, P = ModuleEntry.MatchMode.Prefix,
            C = ModuleEntry.MatchMode.Contains, S = ModuleEntry.MatchMode.Suffix;

        public static readonly List<ModuleEntry> Entries = new List<ModuleEntry>
        {
            // Grafiktreiber
            E("nvwgf2", P, ModuleKind.GpuNvidia, "NVIDIA-Grafiktreiber", Evidence.Belegt, "FiveM nennt bei „nvwgf“ selbst den NVIDIA-Treiber als Ursache."),
            E("nvldumd", P, ModuleKind.GpuNvidia, "NVIDIA-Grafiktreiber", Evidence.Praxis),
            E("nvd3dum", P, ModuleKind.GpuNvidia, "NVIDIA-Grafiktreiber", Evidence.Praxis),
            E("nvumdshim", P, ModuleKind.GpuNvidia, "NVIDIA-Grafiktreiber", Evidence.Praxis),
            E("nvoglv", P, ModuleKind.GpuNvidia, "NVIDIA-Grafiktreiber (OpenGL)", Evidence.Praxis),
            E("nvgpucomp", P, ModuleKind.GpuNvidia, "NVIDIA-Shader-Compiler", Evidence.Praxis),
            E("nvppex", P, ModuleKind.NvidiaFilter, "NVIDIA-Spielfilter (Freestyle)", Evidence.Belegt, "FiveM blockiert die DLL und nennt sie im Crash-Dialog."),
            E("nvspcap", P, ModuleKind.NvidiaCapture, "NVIDIA-Overlay / ShadowPlay (Aufnahme)", Evidence.Belegt, "Laut FiveM-Quelltext stark korreliert mit ERR_GFX_D3D_INIT."),
            E("atidxx", P, ModuleKind.GpuAmd, "AMD-Grafiktreiber", Evidence.Belegt, "FiveM nennt bei „atidxx“ selbst den AMD-Treiber als Ursache."),
            E("amdxx", P, ModuleKind.GpuAmd, "AMD-Grafiktreiber", Evidence.Belegt, "FiveM nennt bei „amdxx“ selbst den AMD-Treiber als Ursache."),
            E("atiumd", P, ModuleKind.GpuAmd, "AMD-Grafiktreiber", Evidence.Praxis),
            E("atiuxp", P, ModuleKind.GpuAmd, "AMD-Grafiktreiber", Evidence.Praxis),
            E("aticfx", P, ModuleKind.GpuAmd, "AMD-Grafiktreiber", Evidence.Praxis),
            E("amdxc", P, ModuleKind.GpuAmd, "AMD-Grafiktreiber", Evidence.Praxis),
            E("amdihk", P, ModuleKind.GpuAmd, "AMD-Software (Hotkeys/Overlay)", Evidence.Praxis),
            E("igd10iumd", P, ModuleKind.GpuIntel, "Intel-Grafiktreiber", Evidence.Praxis),
            E("igdumdim", P, ModuleKind.GpuIntel, "Intel-Grafiktreiber", Evidence.Praxis),
            E("igd11dxva", P, ModuleKind.GpuIntel, "Intel-Grafiktreiber", Evidence.Praxis),
            E("igc64", X, ModuleKind.GpuIntel, "Intel-Shader-Compiler", Evidence.Praxis),
            E("igdgmm", P, ModuleKind.GpuIntel, "Intel-Grafiktreiber", Evidence.Praxis),

            // Overlays und Aufnahme
            E("gameoverlayrenderer", P, ModuleKind.Overlay, "Steam-Overlay", Evidence.Praxis),
            E("discordhook", P, ModuleKind.Overlay, "Discord-Overlay", Evidence.Belegt, "FiveM blockiert die DLL, weil sie Eingaben stört."),
            E("rtsshooks", P, ModuleKind.Overlay, "RivaTuner Statistics Server (MSI Afterburner)", Evidence.Praxis),
            E("obs-graphics-hook", P, ModuleKind.Recorder, "OBS-Spielaufnahme", Evidence.Praxis),
            E("graphics-hook64", X, ModuleKind.Recorder, "OBS-Spielaufnahme", Evidence.Praxis),
            E("ow-graphics", P, ModuleKind.Overlay, "Overwolf", Evidence.Praxis),
            E("owclient", P, ModuleKind.Overlay, "Overwolf", Evidence.Praxis),
            E("twitchnativeoverlay", P, ModuleKind.Overlay, "Twitch-Overlay", Evidence.Praxis),
            E("eosovh-win", P, ModuleKind.Overlay, "Epic-Games-Overlay", Evidence.Praxis),
            E("bdcam", P, ModuleKind.Recorder, "Bandicam", Evidence.Belegt, "FiveM blockiert die DLL („apparently crashes NUI“)."),
            E("fraps", P, ModuleKind.Recorder, "Fraps", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("playclawhook", P, ModuleKind.Recorder, "PlayClaw", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("mirillisaction", P, ModuleKind.Recorder, "Mirillis Action!", Evidence.Praxis),

            // Audio-Zusatzsoftware
            E("a-volute", C, ModuleKind.Audio, "Nahimic / A-Volute (Audio-Software von MSI, ASUS, Dell u. a.)", Evidence.Belegt, "FiveM blockiert A-Volute-DLLs."),
            E("audiodevprops", C, ModuleKind.Audio, "Nahimic / A-Volute (Audio-Software)", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("nahimic", P, ModuleKind.Audio, "Nahimic (Audio-Software)", Evidence.Praxis),

            // Sicherheitssoftware
            E("guard64", X, ModuleKind.Security, "Comodo Internet Security", Evidence.Belegt, "FiveM nennt Comodo im Crash-Dialog und rät zur Deinstallation."),
            E("guard64.dll", X, ModuleKind.Security, "Comodo Internet Security", Evidence.Belegt, "FiveM nennt Comodo im Crash-Dialog und rät zur Deinstallation."),
            E("avghook", P, ModuleKind.Security, "AVG-Virenschutz", Evidence.Belegt, "FiveM blockiert avghooka.dll."),
            E("aswhook", P, ModuleKind.Security, "Avast-Virenschutz", Evidence.Praxis),
            E("atcuf", P, ModuleKind.Security, "Bitdefender", Evidence.Praxis),

            // Hersteller-Werkzeuge aus der FiveM-Sperrliste
            E("gm_gametooldll", P, ModuleKind.VendorTool, "Lenovo Nerve Center / Artery", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("omen_camera", P, ModuleKind.VendorTool, "HP OMEN Gaming Hub", Evidence.Belegt, "FiveM blockiert die DLL („crashes everyone that has this software“)."),
            E("hmipcore", P, ModuleKind.VendorTool, "HideMyIP", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("crashhandler64.dll", X, ModuleKind.VendorTool, "Steam-Crash-Handler", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("am64-34121", P, ModuleKind.VendorTool, "Ad Muncher", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("ezfrd64", P, ModuleKind.VendorTool, "USB-Vibrationstreiber (Gamepad)", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("edsdk.dll", X, ModuleKind.VendorTool, "Canon-Kamera-Software", Evidence.Belegt, "FiveM blockiert die DLL."),
            E("vulkan-1.dll", X, ModuleKind.VendorTool, "Vulkan-Loader", Evidence.Belegt, "FiveM blockiert ihn, weil er viele Vulkan-Treiber abstürzen lässt."),

            // Mods
            E(".asi", S, ModuleKind.AsiPlugin, "ASI-Plugin", Evidence.Belegt, "FiveM nennt .asi-Dateien im Crash-Dialog und rät, sie aus „plugins“ zu entfernen."),
            E("reshade", P, ModuleKind.GraphicsMod, "ReShade", Evidence.Praxis),
            E(".addon64", S, ModuleKind.GraphicsMod, "ReShade-Addon", Evidence.Praxis),
            E(".addon", S, ModuleKind.GraphicsMod, "ReShade-Addon", Evidence.Praxis),
            // scripthookv.dll NICHT eintragen: FiveM liefert in FiveM.app eine eigene Kompatibilitäts-DLL mit diesem Namen
            E("enbhelper", P, ModuleKind.GraphicsMod, "ENBSeries", Evidence.Praxis),

            // FiveM-Komponenten
            E("citizen-scripting-", P, ModuleKind.FiveMScripting, "FiveM-Skriptlaufzeit (Lua/JS/C#)", Evidence.Praxis),
            E("gta-streaming", P, ModuleKind.FiveMStreaming, "FiveM-Streaming (Laden von Modellen und Texturen)", Evidence.Praxis),
            E("libcef.dll", X, ModuleKind.FiveMNui, "Chromium (FiveM-Oberfläche, NUI)", Evidence.Belegt, "Bei libcef-Abstürzen legt FiveM cef_console.txt ins Crash-Zip."),
            E("nui-", P, ModuleKind.FiveMNui, "FiveM-Oberfläche (NUI)", Evidence.Praxis),
            E("chrome_elf.dll", X, ModuleKind.FiveMNui, "Chromium (FiveM-Oberfläche, NUI)", Evidence.Praxis),
            E("gta-net", P, ModuleKind.FiveMNet, "FiveM-Netzwerk-Synchronisation", Evidence.Praxis),
            E("voip-mumble", P, ModuleKind.FiveMVoice, "FiveM-Sprachchat (VoIP)", Evidence.Praxis),
            E("adhesive.dll", X, ModuleKind.FiveMProtection, "FiveM-Schutzkomponente (adhesive)", Evidence.Vermutung),
            E("ros-patches", P, ModuleKind.RockstarLauncher, "Rockstar-Anbindung (Social Club / Launcher)", Evidence.Vermutung),
            E("socialclub", P, ModuleKind.RockstarLauncher, "Rockstar Social Club", Evidence.Vermutung),
            E("citizen-", P, ModuleKind.FiveMCore, "FiveM-Kern", Evidence.Praxis),
            E("gta-core", P, ModuleKind.FiveMCore, "FiveM-Kern (Spielanbindung)", Evidence.Praxis),
            E("gta-game", P, ModuleKind.FiveMCore, "FiveM-Kern (Spielanbindung)", Evidence.Praxis),
            E("rage-", P, ModuleKind.FiveMCore, "FiveM-Kern (RAGE-Anbindung)", Evidence.Praxis),
            E("conhost", P, ModuleKind.FiveMCore, "FiveM-Konsole", Evidence.Praxis),
            E("glue.dll", X, ModuleKind.FiveMCore, "FiveM-Kern (glue)", Evidence.Praxis),
            E("fivem.exe", X, ModuleKind.FiveMCore, "FiveM-Launcher", Evidence.Praxis),

            // Spiel
            E("gtaprocess", C, ModuleKind.Game, "GTA V (Spielcode)", Evidence.Belegt),
            E("gameprocess", C, ModuleKind.Game, "GTA V (Spielcode)", Evidence.Belegt),
            E("gta5_b", P, ModuleKind.Game, "GTA V (Spielcode)", Evidence.Belegt),
            E("gta5.exe", X, ModuleKind.Game, "GTA V (Spielcode)", Evidence.Belegt),

            // Windows
            E("ntdll.dll", X, ModuleKind.WindowsCore, "Windows-Kernbibliothek (ntdll)", Evidence.Praxis),
            E("kernelbase.dll", X, ModuleKind.WindowsCore, "Windows-Kernbibliothek (KernelBase)", Evidence.Praxis),
            E("kernel32.dll", X, ModuleKind.WindowsCore, "Windows-Kernbibliothek (kernel32)", Evidence.Praxis),
            E("user32.dll", X, ModuleKind.WindowsCore, "Windows-Fensterverwaltung", Evidence.Praxis),
            E("win32u.dll", X, ModuleKind.WindowsCore, "Windows-Fensterverwaltung", Evidence.Praxis),
            E("combase.dll", X, ModuleKind.WindowsCore, "Windows-COM", Evidence.Praxis),
            E("ucrtbase.dll", X, ModuleKind.VcRuntime, "Microsoft C-Laufzeit", Evidence.Praxis),
            E("vcruntime140", P, ModuleKind.VcRuntime, "Microsoft Visual C++ Runtime", Evidence.Praxis),
            E("msvcp140", P, ModuleKind.VcRuntime, "Microsoft Visual C++ Runtime", Evidence.Praxis),
            E("d3d11.dll", X, ModuleKind.DirectX, "DirectX 11", Evidence.Praxis),
            E("dxgi.dll", X, ModuleKind.DirectX, "DirectX (DXGI)", Evidence.Praxis),
            E("d3d10warp.dll", X, ModuleKind.DirectX, "DirectX (Software-Rendering)", Evidence.Praxis),
            E("d3dcompiler_47.dll", X, ModuleKind.DirectX, "DirectX-Shader-Compiler", Evidence.Praxis),
            E("xaudio2", P, ModuleKind.AudioSystem, "Windows-Audio (XAudio2)", Evidence.Praxis),
            E("audioses.dll", X, ModuleKind.AudioSystem, "Windows-Audio", Evidence.Praxis),
            E("mmdevapi.dll", X, ModuleKind.AudioSystem, "Windows-Audiogeräte", Evidence.Praxis),
        };

        public static ModuleEntry Lookup(string nameLower)
        {
            if (string.IsNullOrEmpty(nameLower)) return null;
            return Entries.FirstOrDefault(e => e.Matches(nameLower));
        }

        static readonly ModuleEntry PluginEntry = new ModuleEntry
        {
            Pattern = "", Mode = ModuleEntry.MatchMode.Contains, Kind = ModuleKind.AsiPlugin, Product = "Plugin aus dem FiveM-Ordner „plugins“",
            Evidence = Evidence.Belegt, Note = "Alles im Ordner „plugins“ lädt FiveM mit ins Spiel."
        };

        static readonly ModuleEntry GtaModEntry = new ModuleEntry
        {
            Pattern = "", Mode = ModuleEntry.MatchMode.Contains, Kind = ModuleKind.GraphicsMod, Product = "Mod aus dem GTA-V-Ordner",
            Evidence = Evidence.Belegt, Note = "FiveM prüft d3d11.dll, dxgi.dll und d3d10.dll im GTA-Ordner bei DirectX-Fehlern selbst."
        };

        /// <summary>
        /// Einordnung mit Pfad: Eine dxgi.dll aus System32 ist DirectX, dieselbe Datei aus FiveM.app\plugins ist ReShade.
        /// </summary>
        public static ModuleEntry Lookup(ModuleInfo m, string fiveMAppPath = null, string gtaPath = null)
        {
            if (m == null) return null;
            var p = (m.Path ?? "").Replace('/', '\\').ToLowerInvariant();
            var byName = Lookup(m.Name);
            if (p.Contains("\\plugins\\") && (p.Contains("\\fivem.app\\") || (fiveMAppPath != null && p.StartsWith(fiveMAppPath.ToLowerInvariant()))))
            {
                if (byName != null && (byName.Kind == ModuleKind.GraphicsMod || byName.Kind == ModuleKind.AsiPlugin)) return byName;
                if (m.Name == "dxgi.dll" || m.Name == "d3d11.dll") return new ModuleEntry { Kind = ModuleKind.GraphicsMod, Product = "ReShade (" + m.Name + " im Ordner „plugins“)", Evidence = Evidence.Praxis, Note = PluginEntry.Note };
                return PluginEntry;
            }
            if (GtaModFileNames.Contains(m.Name) && p.Length > 0 && !p.Contains("\\windows\\") && IsThirdPartyPath(m.Path, fiveMAppPath, gtaPath))
                return GtaModEntry;
            return byName;
        }

        /// <summary>Liegt die DLL außerhalb von Windows, FiveM und GTA V? Dann gehört sie zu einem Fremdprogramm.</summary>
        public static bool IsThirdPartyPath(string path, string fiveMAppPath, string gtaPath)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.Replace('/', '\\').ToLowerInvariant();
            if (p.Contains("\\windows\\")) return false;
            if (p.Contains("\\windows defender\\") || p.Contains("\\common files\\microsoft shared\\")) return false;
            if (p.Contains("\\fivem.app\\") && !p.Contains("\\fivem.app\\plugins\\")) return false;
            if (!string.IsNullOrEmpty(fiveMAppPath) && p.StartsWith(fiveMAppPath.ToLowerInvariant().TrimEnd('\\') + "\\") &&
                !p.Contains("\\plugins\\")) return false;
            if (p.Contains("\\fivem\\") && !p.Contains("\\plugins\\")) return false;
            if (p.Contains("\\grand theft auto v\\") || p.Contains("\\gtav\\") || p.Contains("\\gta v\\"))
            {
                // im Spielordner sind nur die Spiel-DLLs normal; d3d11/dxgi/dinput8 dort sind Mods
                var file = p.Substring(p.LastIndexOf('\\') + 1);
                return GtaModFileNames.Contains(file) || file.EndsWith(".asi");
            }
            if (!string.IsNullOrEmpty(gtaPath) && p.StartsWith(gtaPath.ToLowerInvariant().TrimEnd('\\') + "\\"))
            {
                var file = p.Substring(p.LastIndexOf('\\') + 1);
                return GtaModFileNames.Contains(file) || file.EndsWith(".asi");
            }
            return true;
        }

        /// <summary>Dateien, die im GTA-V-Ordner nur durch Mods landen (FiveM prüft d3d11/dxgi/d3d10 selbst, RenderHooks.cpp).</summary>
        public static readonly HashSet<string> GtaModFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "d3d11.dll", "dxgi.dll", "d3d10.dll", "d3d9.dll", "dinput8.dll", "scripthookv.dll", "scripthookvdotnet.asi",
            "openiv.asi", "enbseries.ini", "enblocal.ini", "reshade.ini", "dsound.dll", "version.dll", "winmm.dll"
        };
    }

    /// <summary>Programme, die als laufender Prozess auffallen (Name klein, ohne .exe).</summary>
    public static class ProcessCatalog
    {
        public sealed class Entry
        {
            public string Process; public string Product; public bool SafeToClose; public string Hint;
        }

        public static readonly List<Entry> Entries = new List<Entry>
        {
            new Entry { Process = "rtss", Product = "RivaTuner Statistics Server", SafeToClose = true, Hint = "Overlay, klinkt sich in DirectX ein." },
            new Entry { Process = "msiafterburner", Product = "MSI Afterburner", SafeToClose = true, Hint = "Übertaktung der Grafikkarte? Beenden setzt den Takt nicht zurück – im Programm erst „Reset“ wählen." },
            new Entry { Process = "nahimicsvc64", Product = "Nahimic (Audio)", SafeToClose = false, Hint = "Von FiveM als Absturzursache blockiert; deinstallieren oder den Dienst deaktivieren." },
            new Entry { Process = "nahimicsvc32", Product = "Nahimic (Audio)", SafeToClose = false, Hint = "Von FiveM als Absturzursache blockiert; deinstallieren oder den Dienst deaktivieren." },
            new Entry { Process = "obs64", Product = "OBS Studio", SafeToClose = false, Hint = "Spielaufnahme hängt sich ins Spiel; zum Test „Fensteraufnahme“ statt „Spielaufnahme“ nutzen." },
            new Entry { Process = "overwolf", Product = "Overwolf", SafeToClose = true, Hint = "Overlay im Spiel." },
            new Entry { Process = "bdcam", Product = "Bandicam", SafeToClose = true, Hint = "Von FiveM blockiert, weil es die Oberfläche abstürzen lässt." },
            new Entry { Process = "fraps", Product = "Fraps", SafeToClose = true, Hint = "Von FiveM blockiert." },
            new Entry { Process = "playclaw", Product = "PlayClaw", SafeToClose = true, Hint = "Von FiveM blockiert." },
            new Entry { Process = "medal", Product = "Medal.tv", SafeToClose = true, Hint = "Aufnahme-Overlay." },
            new Entry { Process = "nvidia overlay", Product = "NVIDIA-Overlay", SafeToClose = false, Hint = "In der NVIDIA App unter Einstellungen das In-Game-Overlay ausschalten." },
            new Entry { Process = "nvidia share", Product = "GeForce-Experience-Overlay", SafeToClose = false, Hint = "In GeForce Experience das In-Game-Overlay ausschalten." },
        };

        public static IEnumerable<Entry> Running(SystemSnapshot s) =>
            s == null ? Enumerable.Empty<Entry>() : Entries.Where(e => s.IsProcessRunning(e.Process));
    }

    /// <summary>Ausnahmecodes von Windows mit einer verständlichen Bedeutung.</summary>
    public static class ExceptionCatalog
    {
        public sealed class Entry { public string Name; public string Meaning; }

        public static readonly Dictionary<uint, Entry> Codes = new Dictionary<uint, Entry>
        {
            [0xC0000005] = new Entry { Name = "EXCEPTION_ACCESS_VIOLATION", Meaning = "Speicherzugriffsfehler: das Programm hat auf Speicher zugegriffen, der ihm nicht gehört." },
            [0xC0000006] = new Entry { Name = "STATUS_IN_PAGE_ERROR", Meaning = "Daten konnten nicht von der Festplatte nachgeladen werden (Datenträger oder Auslagerungsdatei)." },
            [0xC0000017] = new Entry { Name = "STATUS_NO_MEMORY", Meaning = "Kein Speicher mehr frei." },
            [0xC000001D] = new Entry { Name = "STATUS_ILLEGAL_INSTRUCTION", Meaning = "Der Prozessor hat einen Befehl nicht ausführen können." },
            [0xC0000094] = new Entry { Name = "STATUS_INTEGER_DIVIDE_BY_ZERO", Meaning = "Division durch null." },
            [0xC0000096] = new Entry { Name = "STATUS_PRIVILEGED_INSTRUCTION", Meaning = "Unerlaubter Prozessorbefehl." },
            [0xC000009A] = new Entry { Name = "STATUS_INSUFFICIENT_RESOURCES", Meaning = "Windows hatte nicht genug Systemressourcen." },
            [0xC00000FD] = new Entry { Name = "STATUS_STACK_OVERFLOW", Meaning = "Stapelüberlauf, meist durch endlose Wiederholung im Code." },
            [0xC0000135] = new Entry { Name = "STATUS_DLL_NOT_FOUND", Meaning = "Eine benötigte DLL fehlt." },
            [0xC0000142] = new Entry { Name = "STATUS_DLL_INIT_FAILED", Meaning = "Eine DLL konnte nicht starten." },
            [0xC000007B] = new Entry { Name = "STATUS_INVALID_IMAGE_FORMAT", Meaning = "Eine DLL ist beschädigt oder hat die falsche Bitbreite (32/64 Bit)." },
            [0xC0000374] = new Entry { Name = "STATUS_HEAP_CORRUPTION", Meaning = "Der Arbeitsspeicher des Programms wurde beschädigt." },
            [0xC0000409] = new Entry { Name = "STATUS_STACK_BUFFER_OVERRUN", Meaning = "Das Programm hat sich selbst beendet, weil es eine Beschädigung oder einen schweren Fehler erkannt hat (Fail-Fast)." },
            [0xC0000420] = new Entry { Name = "STATUS_ASSERTION_FAILURE", Meaning = "Eine interne Prüfung ist fehlgeschlagen." },
            [0xE06D7363] = new Entry { Name = "C++-Ausnahme", Meaning = "Eine C++-Ausnahme wurde nicht abgefangen." },
            [0xE0000008] = new Entry { Name = "Chromium: kein Speicher", Meaning = "Die FiveM-Oberfläche (Chromium) hatte keinen Speicher mehr." },
            [0x80000003] = new Entry { Name = "EXCEPTION_BREAKPOINT", Meaning = "Absichtlicher Halt im Code (Prüfung/Assert)." },
            [0x40000015] = new Entry { Name = "STATUS_FATAL_APP_EXIT", Meaning = "Das Programm hat sich mit einem fatalen Fehler beendet." },
        };

        public static readonly Dictionary<string, uint> SymbolicNames = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXCEPTION_ACCESS_VIOLATION"] = 0xC0000005,
            ["ACCESS_VIOLATION"] = 0xC0000005,
            ["STATUS_IN_PAGE_ERROR"] = 0xC0000006,
            ["STATUS_HEAP_CORRUPTION"] = 0xC0000374,
            ["STATUS_STACK_BUFFER_OVERRUN"] = 0xC0000409,
            ["STATUS_STACK_OVERFLOW"] = 0xC00000FD,
            ["STATUS_ILLEGAL_INSTRUCTION"] = 0xC000001D,
        };

        public static string Describe(uint code)
        {
            return Codes.TryGetValue(code, out var e)
                ? $"0x{code:X8} ({e.Name}) – {e.Meaning}"
                : $"0x{code:X8}";
        }
    }

    /// <summary>
    /// Crash-Namen mit Erklärung von Cfx.re: citizenfx/fivem data/client/citizen/crash-data.json (gelesen 2026-11-13),
    /// hier sinngemäß übersetzt.
    /// </summary>
    public static class KnownCrashes
    {
        public enum Kind { ReloadRelated, ServerAssets, Disconnect, Engine }

        public sealed class Entry { public string Explanation; public Kind Kind; }

        public static readonly Dictionary<string, Entry> Names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["october-michigan-lithium"] = new Entry { Kind = Kind.Engine, Explanation = "Der Speicherverwalter des Spiels ist auf eine schwere Beschädigung gestoßen. Laut Cfx passiert das oft, wenn ein Ped stirbt; einen weiteren Zusammenhang kennt Cfx nicht." },
            ["asparagus-king-thirteen"] = new Entry { Kind = Kind.ReloadRelated, Explanation = "Absturz beim Klonen von Netzwerk-Objekten (eine fehlende Null-Prüfung im Spielcode von Rockstar). Tritt laut Cfx nur nach einem Neuladen des Spiels auf." },
            ["lithium-washington-whiskey"] = new Entry { Kind = Kind.ReloadRelated, Explanation = "Unbekannter Absturz beim Klonen von Netzwerk-Objekten. Tritt laut Cfx nur nach einem Neuladen des Spiels auf." },
            ["tennis-stairway-seven"] = new Entry { Kind = Kind.ServerAssets, Explanation = "Absturz im Fahrzeug-Sound. Laut Cfx verursacht durch fehlerhafte Cloth-Daten in einem geladenen Auto-Mod." },
            ["kansas-winner-double"] = new Entry { Kind = Kind.ReloadRelated, Explanation = "Absturz im Ped-Task-Code (rage::gameScriptHandler wurde nicht sauber zurückgesetzt). Tritt laut Cfx nur nach einem Neuladen des Spiels auf." },
            ["spring-montana-seventeen"] = new Entry { Kind = Kind.ReloadRelated, Explanation = "Absturz im Ped-Task-Code (rage::gameScriptHandler wurde nicht sauber zurückgesetzt). Tritt laut Cfx nur nach einem Neuladen des Spiels auf." },
            ["chicken-harry-undress"] = new Entry { Kind = Kind.ServerAssets, Explanation = "Unbekannter Absturz bei Fahrzeug-Gadgets. Laut Cfx möglicherweise durch Auto-Mods." },
            ["winner-florida-minnesota"] = new Entry { Kind = Kind.Disconnect, Explanation = "Absturz beim Entladen von Add-on-Modellen, nachdem die Verbindung zum Server getrennt wurde." },
            ["fanta-robin-burger"] = new Entry { Kind = Kind.Disconnect, Explanation = "Unbekannter Absturz bei der Objekt-Replikation, während die Verbindung zum Server getrennt wurde." },
            ["sweet-sad-july"] = new Entry { Kind = Kind.ServerAssets, Explanation = "Ein ungültiger Verweis auf ein Textur-Wörterbuch (TXD). Laut Cfx ein Skriptfehler – also ein Fehler in einer Ressource des Servers." },
            ["salami-emma-purple"] = new Entry { Kind = Kind.ReloadRelated, Explanation = "Unbekannter Absturz beim Streamen von Modellinformationen. Tritt laut Cfx nur nach einem Neuladen des Spiels auf." },
        };
    }
}
