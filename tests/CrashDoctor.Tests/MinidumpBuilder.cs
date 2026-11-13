using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CrashDoctor.Tests
{
    /// <summary>
    /// Baut einen kleinen, aber formatgetreuen Minidump (minidumpapiset.h) für Tests:
    /// Systeminfo, Prozesszeiten, Module, ein Thread mit Stack, Kontext und Ausnahme.
    /// </summary>
    public sealed class MinidumpBuilder
    {
        public uint DumpTime = 1_763_000_000;
        public uint ProcessCreateTime = 1_762_999_988; // 12 s vor dem Absturz
        public uint ExceptionCode = 0xC0000005;
        public ulong ExceptionAddress;
        public ulong[] ExceptionParams = { 0, 0x10 };
        public ulong StackStart = 0x0000_00A0_0000_0000;
        public List<ulong> StackValues = new List<ulong>();
        public uint ThreadId = 42;
        public bool PadModuleList;
        public List<(string Path, ulong Base, uint Size)> Modules = new List<(string, ulong, uint)>();

        public byte[] Build()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            const int streams = 5;
            // Kopf
            w.Write(0x504D444Du); w.Write(0x0000A793u); w.Write((uint)streams); w.Write(32u); w.Write(0u); w.Write(DumpTime); w.Write(0ul);
            long dirPos = ms.Position;
            w.Write(new byte[streams * 12]);

            var dirs = new List<(uint type, uint size, uint rva)>();

            // Systeminfo (7)
            uint rva = (uint)ms.Position;
            w.Write((ushort)9); w.Write((ushort)25); w.Write((ushort)0x2100); w.Write((byte)16); w.Write((byte)1);
            w.Write(10u); w.Write(0u); w.Write(26100u); w.Write(2u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(Encoding.ASCII.GetBytes("AuthenticAMD")); w.Write(new byte[12]);
            dirs.Add((7, (uint)ms.Position - rva, rva));

            // MiscInfo (15)
            rva = (uint)ms.Position;
            w.Write(24u); w.Write(3u); w.Write(4242u); w.Write(ProcessCreateTime); w.Write(0u); w.Write(0u);
            dirs.Add((15, 24, rva));

            // Modulnamen, dann Modulliste (4)
            var nameRvas = new List<uint>();
            foreach (var m in Modules)
            {
                nameRvas.Add((uint)ms.Position);
                var bytes = Encoding.Unicode.GetBytes(m.Path);
                w.Write((uint)bytes.Length); w.Write(bytes); w.Write((ushort)0);
            }
            rva = (uint)ms.Position;
            w.Write((uint)Modules.Count);
            if (PadModuleList) w.Write(0u);
            for (int i = 0; i < Modules.Count; i++)
            {
                var m = Modules[i];
                w.Write(m.Base); w.Write(m.Size); w.Write(0u); w.Write(0u); w.Write(nameRvas[i]);
                // VS_FIXEDFILEINFO: Kennung, Strukturversion, Dateiversion 1.2.3.4, Rest 0
                w.Write(0xFEEF04BDu); w.Write(0x00010000u); w.Write((1u << 16) | 2u); w.Write((3u << 16) | 4u);
                w.Write(new byte[52 - 16]);
                w.Write(new byte[8]); w.Write(new byte[8]); w.Write(0ul); w.Write(0ul);
            }
            dirs.Add((4, (uint)ms.Position - rva, rva));

            // Stack-Speicher und Kontext
            uint stackRva = (uint)ms.Position;
            foreach (var v in StackValues) w.Write(v);
            uint stackSize = (uint)ms.Position - stackRva;
            uint ctxRva = (uint)ms.Position;
            var ctx = new byte[1232];
            BitConverter.GetBytes(StackStart).CopyTo(ctx, 0x98);      // Rsp
            BitConverter.GetBytes(ExceptionAddress).CopyTo(ctx, 0xF8); // Rip
            w.Write(ctx);

            // Threadliste (3)
            rva = (uint)ms.Position;
            w.Write(1u);
            w.Write(ThreadId); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0ul);
            w.Write(StackStart); w.Write(stackSize); w.Write(stackRva);
            w.Write((uint)ctx.Length); w.Write(ctxRva);
            dirs.Add((3, (uint)ms.Position - rva, rva));

            // Ausnahme (6)
            rva = (uint)ms.Position;
            w.Write(ThreadId); w.Write(0u);
            w.Write(ExceptionCode); w.Write(0u); w.Write(0ul); w.Write(ExceptionAddress);
            w.Write((uint)ExceptionParams.Length); w.Write(0u);
            for (int i = 0; i < 15; i++) w.Write(i < ExceptionParams.Length ? ExceptionParams[i] : 0ul);
            w.Write((uint)ctx.Length); w.Write(ctxRva);
            dirs.Add((6, (uint)ms.Position - rva, rva));

            ms.Position = dirPos;
            foreach (var d in dirs) { w.Write(d.type); w.Write(d.size); w.Write(d.rva); }
            w.Flush();
            return ms.ToArray();
        }
    }
}
