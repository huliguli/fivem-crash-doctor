using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CrashDoctor.Core
{
    /// <summary>Ergebnis des Einlesens einer .dmp-Datei.</summary>
    public sealed class MinidumpInfo
    {
        public DateTime? TimestampUtc { get; set; }
        public uint? ExceptionCode { get; set; }
        public uint ExceptionFlags { get; set; }
        public ulong ExceptionAddress { get; set; }
        public ulong[] ExceptionParameters { get; set; } = new ulong[0];
        public uint ExceptionThreadId { get; set; }

        public List<ModuleInfo> Modules { get; } = new List<ModuleInfo>();
        public List<ModuleInfo> UnloadedModules { get; } = new List<ModuleInfo>();
        public ModuleInfo FaultModule { get; set; }
        public ulong FaultOffset { get; set; }
        public bool FaultInUnloaded { get; set; }
        public string Signature { get; set; }

        public ushort ProcessorArchitecture { get; set; }   // 9 = AMD64, 0 = x86
        public uint OsMajor { get; set; }
        public uint OsMinor { get; set; }
        public uint OsBuild { get; set; }
        public int ProcessorCount { get; set; }
        public string CpuVendor { get; set; }

        public uint? ProcessId { get; set; }
        public DateTime? ProcessCreateTimeUtc { get; set; }
        public ulong? InstructionPointer { get; set; }
        public ulong? StackPointer { get; set; }
        public List<string> StackModules { get; } = new List<string>();
        public List<ModuleInfo> StackModuleInfos { get; } = new List<ModuleInfo>();

        public List<string> Warnings { get; } = new List<string>();

        public string MainModuleName => Modules.Count > 0 ? Modules[0].Name : null;
        public TimeSpan? Uptime =>
            TimestampUtc.HasValue && ProcessCreateTimeUtc.HasValue && TimestampUtc >= ProcessCreateTimeUtc
                ? TimestampUtc - ProcessCreateTimeUtc : null;
    }

    /// <summary>
    /// Liest Windows-Minidumps (Format MINIDUMP_HEADER/…, dokumentiert in minidumpapiset.h)
    /// ohne dbghelp und ohne Symbole. Ausgewertet werden Ausnahme, Modulliste, entladene Module,
    /// Systeminfo, Prozesszeiten und – heuristisch – die Module auf dem Stack des abgestürzten Threads.
    /// </summary>
    public static class MinidumpReader
    {
        const uint MdmpSignature = 0x504D444D; // "MDMP"
        const int ModuleEntrySize = 108;
        const int ThreadEntrySize = 48;
        const int ThreadExEntrySize = 64;

        enum StreamType : uint
        {
            ThreadList = 3, ModuleList = 4, MemoryList = 5, Exception = 6, SystemInfo = 7,
            ThreadExList = 8, Memory64List = 9, UnloadedModuleList = 14, MiscInfo = 15
        }

        struct Dir { public uint Type; public uint Size; public uint Rva; }
        struct MemRange { public ulong Start; public ulong Size; public long FileOffset; }
        struct ThreadRec { public uint Id; public ulong StackStart; public uint StackSize; public uint StackRva; public uint CtxSize; public uint CtxRva; }

        public static MinidumpInfo Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Read(fs);
        }

        public static MinidumpInfo Read(Stream stream)
        {
            if (!stream.CanSeek) throw new ArgumentException("Stream muss seekbar sein.", nameof(stream));
            var r = new SafeReader(stream);
            if (stream.Length < 32) throw new InvalidDataException("Die Datei ist zu klein für einen Minidump.");

            r.Seek(0);
            if (r.U32() != MdmpSignature) throw new InvalidDataException("Das ist keine Minidump-Datei (Kennung „MDMP“ fehlt).");
            r.U32(); // Version
            uint streamCount = r.U32();
            uint dirRva = r.U32();
            r.U32(); // CheckSum
            uint timeDateStamp = r.U32();
            r.U64(); // Flags

            var info = new MinidumpInfo();
            if (timeDateStamp != 0) info.TimestampUtc = FromUnix(timeDateStamp);
            if (streamCount == 0 || streamCount > 1024) throw new InvalidDataException("Ungültige Anzahl an Datenströmen im Dump.");

            var dirs = new List<Dir>();
            for (uint i = 0; i < streamCount; i++)
            {
                r.Seek(dirRva + i * 12L);
                dirs.Add(new Dir { Type = r.U32(), Size = r.U32(), Rva = r.U32() });
            }

            Dir? Find(StreamType t)
            {
                foreach (var d in dirs) if (d.Type == (uint)t && d.Size > 0) return d;
                return null;
            }

            Try(info, "Systeminfo", () => { var d = Find(StreamType.SystemInfo); if (d.HasValue) ReadSystemInfo(r, d.Value, info); });
            Try(info, "Prozessinfo", () => { var d = Find(StreamType.MiscInfo); if (d.HasValue) ReadMiscInfo(r, d.Value, info); });
            Try(info, "Modulliste", () => { var d = Find(StreamType.ModuleList); if (d.HasValue) ReadModules(r, d.Value, info); });
            Try(info, "Entladene Module", () => { var d = Find(StreamType.UnloadedModuleList); if (d.HasValue) ReadUnloaded(r, d.Value, info); });

            var memory = new List<MemRange>();
            Try(info, "Speicherliste", () =>
            {
                var d = Find(StreamType.MemoryList); if (d.HasValue) ReadMemoryList(r, d.Value, memory);
                var d64 = Find(StreamType.Memory64List); if (d64.HasValue) ReadMemory64List(r, d64.Value, memory);
            });

            var threads = new List<ThreadRec>();
            Try(info, "Threadliste", () =>
            {
                var d = Find(StreamType.ThreadList);
                if (d.HasValue) ReadThreads(r, d.Value, threads, ThreadEntrySize);
                else { var dx = Find(StreamType.ThreadExList); if (dx.HasValue) ReadThreads(r, dx.Value, threads, ThreadExEntrySize); }
            });

            uint ctxSize = 0, ctxRva = 0;
            Try(info, "Ausnahme", () =>
            {
                var d = Find(StreamType.Exception);
                if (!d.HasValue) return;
                r.Seek(d.Value.Rva);
                info.ExceptionThreadId = r.U32();
                r.U32();
                info.ExceptionCode = r.U32();
                info.ExceptionFlags = r.U32();
                r.U64(); // verschachtelte Ausnahme
                info.ExceptionAddress = r.U64();
                uint n = Math.Min(r.U32(), 15u);
                r.U32();
                var p = new ulong[15];
                for (int i = 0; i < 15; i++) p[i] = r.U64();
                info.ExceptionParameters = p.Take((int)n).ToArray();
                ctxSize = r.U32();
                ctxRva = r.U32();
            });

            // Absturzstelle einem Modul zuordnen
            if (info.ExceptionCode.HasValue)
            {
                var addr = info.ExceptionAddress;
                var m = info.Modules.FirstOrDefault(x => x.Contains(addr));
                if (m == null)
                {
                    m = info.UnloadedModules.FirstOrDefault(x => x.Contains(addr));
                    if (m != null) info.FaultInUnloaded = true;
                }
                if (m != null)
                {
                    info.FaultModule = m;
                    info.FaultOffset = addr - m.Base;
                    info.Signature = BuildSignature(m.Name, info.FaultOffset, info.FaultInUnloaded);
                }
            }

            // Register aus dem Ausnahme-Kontext und Stack-Durchsuchung
            Try(info, "Stack", () =>
            {
                if (ctxRva != 0 && ctxSize != 0) ReadContext(r, ctxSize, ctxRva, info);
                ScanStack(r, info, threads, memory);
            });

            return info;
        }

        /// <summary>Baut die Signatur so, wie FiveM sie im Dialog zeigt (MiniDump.cpp: „%s+%X“, Spielprozess als GTA5_b&lt;build&gt;.exe).</summary>
        public static string BuildSignature(string moduleNameLower, ulong offset, bool unloaded)
        {
            var name = moduleNameLower ?? "?";
            if (!unloaded && (name.Contains("gtaprocess") || name.Contains("gameprocess")))
            {
                var b = Regex.Match(name, @"_b(\d{3,5})_");
                name = b.Success ? "GTA5_b" + b.Groups[1].Value + ".exe" : "GTA5.exe";
            }
            return name + (unloaded ? "_unloaded" : "") + "+" + offset.ToString("X");
        }

        static void ReadSystemInfo(SafeReader r, Dir d, MinidumpInfo info)
        {
            r.Seek(d.Rva);
            info.ProcessorArchitecture = r.U16();
            r.U16(); r.U16();
            info.ProcessorCount = r.U8();
            r.U8();
            info.OsMajor = r.U32();
            info.OsMinor = r.U32();
            info.OsBuild = r.U32();
            r.U32(); r.U32(); r.U16(); r.U16();
            if (d.Size >= 44)
            {
                var vendor = Encoding.ASCII.GetString(r.Bytes(12)).TrimEnd('\0');
                if (vendor.Length > 0 && vendor.All(c => c >= 0x20 && c < 0x7F)) info.CpuVendor = vendor;
            }
        }

        static void ReadMiscInfo(SafeReader r, Dir d, MinidumpInfo info)
        {
            if (d.Size < 24) return;
            r.Seek(d.Rva);
            r.U32(); // SizeOfInfo
            uint flags = r.U32();
            uint pid = r.U32();
            uint created = r.U32();
            if ((flags & 0x1) != 0) info.ProcessId = pid;
            if ((flags & 0x2) != 0 && created != 0) info.ProcessCreateTimeUtc = FromUnix(created);
        }

        static void ReadModules(SafeReader r, Dir d, MinidumpInfo info)
        {
            r.Seek(d.Rva);
            uint n = r.U32();
            if (n > 20000) throw new InvalidDataException("unplausible Modulanzahl");
            long start = d.Rva + 4L;
            if (d.Size == 8 + n * (long)ModuleEntrySize) start += 4; // 4 Byte Ausrichtung, kommt bei manchen Schreibern vor
            for (uint i = 0; i < n; i++)
            {
                r.Seek(start + i * (long)ModuleEntrySize);
                ulong baseAddr = r.U64();
                uint size = r.U32();
                r.U32(); r.U32();
                uint nameRva = r.U32();
                uint ffSig = r.U32();
                r.U32();
                uint verMs = r.U32();
                uint verLs = r.U32();
                var path = r.MinidumpString(nameRva) ?? "";
                info.Modules.Add(new ModuleInfo
                {
                    Base = baseAddr,
                    Size = size,
                    Path = path,
                    Name = FileName(path),
                    FileVersion = ffSig == 0xFEEF04BD ? $"{verMs >> 16}.{verMs & 0xFFFF}.{verLs >> 16}.{verLs & 0xFFFF}" : null
                });
            }
        }

        static void ReadUnloaded(SafeReader r, Dir d, MinidumpInfo info)
        {
            r.Seek(d.Rva);
            uint headerSize = r.U32();
            uint entrySize = r.U32();
            uint n = r.U32();
            if (entrySize < 24 || n > 20000) return;
            for (uint i = 0; i < n; i++)
            {
                r.Seek(d.Rva + headerSize + i * (long)entrySize);
                ulong baseAddr = r.U64();
                uint size = r.U32();
                r.U32(); r.U32();
                uint nameRva = r.U32();
                var path = r.MinidumpString(nameRva) ?? "";
                info.UnloadedModules.Add(new ModuleInfo { Base = baseAddr, Size = size, Path = path, Name = FileName(path), Unloaded = true });
            }
        }

        static void ReadMemoryList(SafeReader r, Dir d, List<MemRange> memory)
        {
            r.Seek(d.Rva);
            uint n = r.U32();
            if (n > 200000) return;
            long start = d.Rva + 4L;
            if (d.Size == 8 + n * 16L) start += 4;
            for (uint i = 0; i < n; i++)
            {
                r.Seek(start + i * 16L);
                ulong va = r.U64();
                uint size = r.U32();
                uint rva = r.U32();
                memory.Add(new MemRange { Start = va, Size = size, FileOffset = rva });
            }
        }

        static void ReadMemory64List(SafeReader r, Dir d, List<MemRange> memory)
        {
            r.Seek(d.Rva);
            ulong n = r.U64();
            ulong baseRva = r.U64();
            if (n > 2000000) return;
            ulong offset = baseRva;
            for (ulong i = 0; i < n; i++)
            {
                ulong va = r.U64();
                ulong size = r.U64();
                memory.Add(new MemRange { Start = va, Size = size, FileOffset = (long)offset });
                offset += size;
            }
        }

        static void ReadThreads(SafeReader r, Dir d, List<ThreadRec> threads, int entrySize)
        {
            r.Seek(d.Rva);
            uint n = r.U32();
            if (n > 100000) return;
            long start = d.Rva + 4L;
            if (d.Size == 8 + n * (long)entrySize) start += 4;
            for (uint i = 0; i < n; i++)
            {
                r.Seek(start + i * (long)entrySize);
                var t = new ThreadRec { Id = r.U32() };
                r.U32(); r.U32(); r.U32(); r.U64();
                t.StackStart = r.U64();
                t.StackSize = r.U32();
                t.StackRva = r.U32();
                t.CtxSize = r.U32();
                t.CtxRva = r.U32();
                threads.Add(t);
            }
        }

        static void ReadContext(SafeReader r, uint ctxSize, uint ctxRva, MinidumpInfo info)
        {
            if (info.ProcessorArchitecture == 9 && ctxSize >= 0x100) // AMD64: Rsp 0x98, Rip 0xF8
            {
                r.Seek(ctxRva + 0x98L); info.StackPointer = r.U64();
                r.Seek(ctxRva + 0xF8L); info.InstructionPointer = r.U64();
            }
            else if (info.ProcessorArchitecture == 0 && ctxSize >= 0xCC) // x86: Eip 0xB8, Esp 0xC4
            {
                r.Seek(ctxRva + 0xB8L); info.InstructionPointer = r.U32();
                r.Seek(ctxRva + 0xC4L); info.StackPointer = r.U32();
            }
        }

        /// <summary>
        /// Durchsucht den Stack des abgestürzten Threads nach Werten, die in ein geladenes Modul zeigen.
        /// Das sind meist Rücksprungadressen – aber nicht immer. Ergebnis ist deshalb ein Hinweis, kein Stacktrace.
        /// </summary>
        static void ScanStack(SafeReader r, MinidumpInfo info, List<ThreadRec> threads, List<MemRange> memory)
        {
            if (info.Modules.Count == 0 || threads.Count == 0) return;
            var thread = threads.FirstOrDefault(t => t.Id == info.ExceptionThreadId);
            if (thread.Id == 0 && thread.StackSize == 0) thread = threads[0];
            if (thread.StackSize == 0) return;

            long fileOffset = thread.StackRva;
            ulong stackStart = thread.StackStart;
            ulong stackSize = thread.StackSize;
            if (fileOffset == 0)
            {
                var range = memory.FirstOrDefault(m => thread.StackStart >= m.Start && thread.StackStart < m.Start + m.Size);
                if (range.Size == 0) return;
                fileOffset = range.FileOffset + (long)(thread.StackStart - range.Start);
            }

            ulong scanFrom = stackStart;
            if (info.StackPointer.HasValue && info.StackPointer.Value >= stackStart && info.StackPointer.Value < stackStart + stackSize)
                scanFrom = info.StackPointer.Value;

            int ptr = info.ProcessorArchitecture == 0 ? 4 : 8;
            long skip = (long)(scanFrom - stackStart);
            skip -= skip % ptr;
            int length = (int)Math.Min((long)stackSize - skip, 96 * 1024);
            if (length <= 0) return;

            byte[] data;
            try { r.Seek(fileOffset + skip); data = r.Bytes(length); }
            catch (EndOfStreamException) { return; }

            var sorted = info.Modules.Where(m => m.Size > 0).OrderBy(m => m.Base).ToArray();
            var bases = sorted.Select(m => m.Base).ToArray();
            string last = null;
            int hits = 0;
            for (int i = 0; i + ptr <= data.Length && hits < 200; i += ptr)
            {
                ulong v = ptr == 8 ? BitConverter.ToUInt64(data, i) : BitConverter.ToUInt32(data, i);
                if (v < 0x10000) continue;
                int idx = Array.BinarySearch(bases, v);
                if (idx < 0) idx = ~idx - 1;
                if (idx < 0) continue;
                var m = sorted[idx];
                if (!m.Contains(v) || v - m.Base < 0x1000) continue; // PE-Kopf ausnehmen
                hits++;
                if (m.Path == last) continue;
                last = m.Path;
                if (!info.StackModuleInfos.Contains(m))
                {
                    info.StackModuleInfos.Add(m);
                    if (!info.StackModules.Contains(m.Name)) info.StackModules.Add(m.Name);
                }
                if (info.StackModuleInfos.Count >= 16) break;
            }
        }

        static void Try(MinidumpInfo info, string what, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is IOException || ex is ArgumentOutOfRangeException)
            {
                info.Warnings.Add($"{what} im Dump nicht lesbar: {ex.Message}");
            }
        }

        static DateTime FromUnix(uint seconds) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

        static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return (i >= 0 ? path.Substring(i + 1) : path).ToLowerInvariant();
        }

        /// <summary>Lesen mit Grenzprüfung: ein kaputter Dump darf nie mehr als Datei-Länge anfordern.</summary>
        sealed class SafeReader
        {
            readonly Stream _s;
            readonly byte[] _buf = new byte[8];
            public SafeReader(Stream s) { _s = s; }

            public void Seek(long pos)
            {
                if (pos < 0 || pos > _s.Length) throw new EndOfStreamException($"Position {pos} liegt außerhalb der Datei.");
                _s.Position = pos;
            }

            void Fill(int n)
            {
                int read = 0;
                while (read < n)
                {
                    int k = _s.Read(_buf, read, n - read);
                    if (k <= 0) throw new EndOfStreamException("Unerwartetes Dateiende.");
                    read += k;
                }
            }

            public byte U8() { Fill(1); return _buf[0]; }
            public ushort U16() { Fill(2); return BitConverter.ToUInt16(_buf, 0); }
            public uint U32() { Fill(4); return BitConverter.ToUInt32(_buf, 0); }
            public ulong U64() { Fill(8); return BitConverter.ToUInt64(_buf, 0); }

            public byte[] Bytes(int n)
            {
                if (n < 0 || _s.Position + n > _s.Length) throw new EndOfStreamException("Bereich liegt außerhalb der Datei.");
                var b = new byte[n];
                int read = 0;
                while (read < n)
                {
                    int k = _s.Read(b, read, n - read);
                    if (k <= 0) throw new EndOfStreamException("Unerwartetes Dateiende.");
                    read += k;
                }
                return b;
            }

            public string MinidumpString(uint rva)
            {
                if (rva == 0) return null;
                Seek(rva);
                uint len = U32();
                if (len == 0 || len > 65534 || len % 2 != 0) return null;
                return Encoding.Unicode.GetString(Bytes((int)len));
            }
        }
    }
}
