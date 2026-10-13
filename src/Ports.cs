using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorControl
{
    static class Scanner
    {
        // Monitors on an NVIDIA GPU go through the NVIDIA driver, which reaches every monitor
        // even inside NVIDIA Surround. Monitors on any other adapter (AMD, Intel, a second
        // GPU) go through Windows' own DDC/CI API. Both run, so mixed setups work too.
        public static List<Monitor> Scan(out string source)
        {
            var nvidia = Probe(Nvapi.Enumerate());
            var windows = Probe(WinDdc.Enumerate(nvidia.Count > 0));
            var found = nvidia.Concat(windows).ToList();
            for (int i = 0; i < found.Count; i++) found[i].Number = i + 1;
            source = nvidia.Count > 0 && windows.Count > 0 ? "NVIDIA driver and Windows"
                   : nvidia.Count > 0 ? "NVIDIA driver"
                   : windows.Count > 0 ? "Windows DDC/CI" : "none";
            return found;
        }

        // Probes every port at once (each monitor is on its own bus), then keeps the original order.
        static List<Monitor> Probe(List<DdcPort> ports)
        {
            var result = new List<Monitor>();
            var candidates = ports.Select(p => new Monitor(p)).ToList();
            var answered = new bool[candidates.Count];
            var threads = new List<Thread>();
            for (int i = 0; i < candidates.Count; i++)
            {
                int index = i;
                var t = new Thread(() =>
                {
                    try { answered[index] = candidates[index].Probe(); }
                    catch (Exception ex) { Log.Write("Probe " + candidates[index].Port.Location + ": " + ex.Message); }
                }) { IsBackground = true };
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) t.Join();

            for (int i = 0; i < candidates.Count; i++)
            {
                var m = candidates[i];
                var port = m.Port;
                if (answered[i])
                {
                    m.Number = result.Count + 1;
                    result.Add(m);
                    var feats = new List<string>();
                    foreach (var code in m.SupportedCodes())
                        feats.Add(string.Format("{0:X2}={1}/{2}", code, m.Value(code), m.Max(code)));
                    Log.Write(string.Format("Monitor {0}: '{1}' serial '{2}' at {3}: {4}",
                        m.Number, port.Model, port.Serial, port.Location, string.Join(" ", feats)));
                }
                else
                {
                    Log.Write("No DDC/CI reply from " + port.Location);
                    port.Dispose();
                }
            }
            return result;
        }
    }

    static class Nvapi
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpusFn([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConnectedOutputsFn(IntPtr gpu, out uint mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConnectedDisplayIdsFn(IntPtr gpu, IntPtr ids, ref uint count, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DisplayIdToOutputFn(uint displayId, out IntPtr gpu, out uint outputId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int EdidFn(IntPtr gpu, uint outputId, IntPtr edid);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int I2CFn(IntPtr gpu, ref I2CInfo info);

        // NV_I2C_INFO_V3
        [StructLayout(LayoutKind.Sequential)]
        public struct I2CInfo
        {
            public uint version;
            public uint displayMask;
            public byte bIsDDCPort;
            public byte i2cDevAddress;
            public IntPtr pbI2cRegAddress;
            public uint regAddrSize;
            public IntPtr pbData;
            public uint cbSize;
            public uint i2cSpeed;
            public uint i2cSpeedKhz;
            public byte portId;
            public uint bIsPortIdSet;
        }

        public static readonly object Sync = new object();
        public static I2CFn I2CWrite, I2CRead;
        public static EdidFn GetEdid;

        static T Fn<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public static List<DdcPort> Enumerate()
        {
            var ports = new List<DdcPort>();
            try
            {
                var init = Fn<InitializeFn>(0x0150E828);
                int status = init == null ? -1 : init();
                if (status != 0) { Log.Write("NVAPI initialize failed: " + status); return ports; }

                I2CWrite = Fn<I2CFn>(0xE812EB07);
                I2CRead = Fn<I2CFn>(0x2FDE12C5);
                GetEdid = Fn<EdidFn>(0x37D32E69);
                var enumGpus = Fn<EnumGpusFn>(0xE5AC921F);
                if (I2CWrite == null || I2CRead == null || enumGpus == null) { Log.Write("NVAPI I2C not available"); return ports; }

                var gpus = new IntPtr[64];
                int count;
                if (enumGpus(gpus, out count) != 0) return ports;
                for (int g = 0; g < count; g++)
                    foreach (uint output in Outputs(gpus[g]))
                        ports.Add(new NvPort(gpus[g], output, g));
            }
            catch (DllNotFoundException) { Log.Write("No NVIDIA driver (nvapi64.dll) found"); }
            catch (Exception ex) { Log.Write("NVAPI enumerate: " + ex.Message); }
            return ports;
        }

        static List<uint> Outputs(IntPtr gpu)
        {
            var list = new List<uint>();
            uint mask;
            var connected = Fn<ConnectedOutputsFn>(0x1730BFC9);
            if (connected != null && connected(gpu, out mask) == 0 && mask != 0)
            {
                for (int b = 0; b < 32; b++) if ((mask & (1u << b)) != 0) list.Add(1u << b);
                return list;
            }

            // Newer API, in case a future driver drops the deprecated call above.
            var getIds = Fn<ConnectedDisplayIdsFn>(0x0078DBA2);
            var toOutput = Fn<DisplayIdToOutputFn>(0x112BA1A5);
            if (getIds == null || toOutput == null) return list;
            uint n = 0;
            if (getIds(gpu, IntPtr.Zero, ref n, 0) != 0 || n == 0) return list;
            const int size = 16;
            foreach (int ver in new[] { 3, 1 })
            {
                IntPtr buf = Marshal.AllocHGlobal(size * (int)n);
                try
                {
                    for (int i = 0; i < n * size; i++) Marshal.WriteByte(buf, i, 0);
                    for (int i = 0; i < n; i++) Marshal.WriteInt32(buf, i * size, size | (ver << 16));
                    uint got = n;
                    if (getIds(gpu, buf, ref got, 0) != 0) continue;
                    for (int i = 0; i < got; i++)
                    {
                        uint id = (uint)Marshal.ReadInt32(buf, i * size + 8);
                        IntPtr owner; uint outputId;
                        if (toOutput(id, out owner, out outputId) == 0 && outputId != 0 && !list.Contains(outputId))
                            list.Add(outputId);
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return list;
        }
    }

    sealed class NvPort : DdcPort
    {
        const byte Address = 0x6E;   // DDC/CI display address (8-bit)
        const byte Host = 0x51;      // host "register"
        readonly IntPtr gpu;
        readonly uint output;

        // Each monitor has its own DDC bus, so the lock is per port. The driver call itself
        // is serialized globally (Nvapi.Sync), but the protocol's mandatory pauses are not,
        // so three monitors wait in parallel instead of one after another.
        // A named mutex, so another copy of the app (e.g. /diagnose) can't interleave
        // transactions on the same bus. It also serializes threads within this process.
        Mutex bus;

        sealed class BusHold : IDisposable
        {
            readonly Mutex m; readonly bool held;
            public BusHold(Mutex m)
            {
                this.m = m;
                try { held = m.WaitOne(3000); }
                catch (AbandonedMutexException) { held = true; }
            }
            public void Dispose() { if (held) m.ReleaseMutex(); }
        }

        BusHold Lock() { return new BusHold(bus); }
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long readyAt;   // earliest time (clock ms) the monitor accepts its next command

        // Only sleep for whatever part of the required gap hasn't already passed.
        void WaitReady()
        {
            long ms = readyAt - clock.ElapsedMilliseconds;
            if (ms > 0) Thread.Sleep((int)ms);
        }

        void Hold(int ms) { readyAt = clock.ElapsedMilliseconds + Slow(ms); }

        // Adaptive timing: the MCCS minimums suit most monitors, but slow ones garble replies
        // when rushed. Each failure stretches this port's waits (up to 2.5x); a run of
        // successes eases them back. Same idea as ddcutil's dynamic sleep.
        double pace = 1.0;
        int streak;
        int Slow(int ms) { return (int)Math.Ceiling(ms * pace); }
        void Succeeded() { if (++streak >= 20 && pace > 1.0) { pace = Math.Max(1.0, pace - 0.1); streak = 0; } }
        void Stumbled()
        {
            streak = 0;
            if (pace < 2.5) { pace = Math.Min(2.5, pace + 0.5); Log.Write(string.Format("{0}: slowing DDC timing to {1:0.0}x", Location, pace)); }
        }

        public NvPort(IntPtr gpu, uint output, int gpuIndex)
        {
            this.gpu = gpu;
            this.output = output;
            Location = string.Format("nv{0}:{1:X}", gpuIndex, output);
            bus = new Mutex(false, "MonitorControl.Bus." + Location.Replace(":", "."));
            ReadEdid();
        }

        void ReadEdid()
        {
            if (Nvapi.GetEdid == null) return;
            // NV_EDID V3 / V2 / V1: version + 256 data bytes + trailing fields.
            foreach (var v in new[] { new[] { 272, 3 }, new[] { 264, 2 }, new[] { 260, 1 } })
            {
                IntPtr buf = Marshal.AllocHGlobal(v[0]);
                try
                {
                    for (int i = 0; i < v[0]; i++) Marshal.WriteByte(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, v[0] | (v[1] << 16));
                    if (Nvapi.GetEdid(gpu, output, buf) != 0) continue;
                    var e = new byte[128];
                    Marshal.Copy(IntPtr.Add(buf, 4), e, 0, 128);
                    Edid.Parse(e, out Model, out Serial, out ProductId);
                    return;
                }
                catch (Exception ex) { Log.Write("EDID " + Location + ": " + ex.Message); return; }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        int Transfer(byte[] data, bool read)
        {
            IntPtr reg = Marshal.AllocHGlobal(1);
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.WriteByte(reg, Host);
                Marshal.Copy(data, 0, buf, data.Length);
                var info = new Nvapi.I2CInfo();
                info.version = (uint)Marshal.SizeOf(typeof(Nvapi.I2CInfo)) | (3u << 16);
                info.displayMask = output;
                info.bIsDDCPort = 1;
                info.i2cDevAddress = Address;
                info.pbI2cRegAddress = read ? IntPtr.Zero : reg;
                info.regAddrSize = read ? 0u : 1u;
                info.pbData = buf;
                info.cbSize = (uint)data.Length;
                info.i2cSpeed = 0xFFFF;   // deprecated field; use i2cSpeedKhz
                info.i2cSpeedKhz = 0;     // driver default
                int status;
                lock (Nvapi.Sync) status = read ? Nvapi.I2CRead(gpu, ref info) : Nvapi.I2CWrite(gpu, ref info);
                if (read && status == 0) Marshal.Copy(buf, data, 0, data.Length);
                return status;
            }
            finally
            {
                Marshal.FreeHGlobal(reg);
                Marshal.FreeHGlobal(buf);
            }
        }

        public override void Dispose() { if (bus != null) { bus.Dispose(); bus = null; } }

        public override DdcResult Read(byte code, out int current, out int maximum)
        {
            current = maximum = 0;
            using (Lock())
            {
                WaitReady();
                byte[] req = { 0x82, 0x01, code, 0 };
                req[3] = (byte)(Address ^ Host ^ 0x82 ^ 0x01 ^ code);
                if (Transfer(req, false) != 0) { Stumbled(); Hold(50); return DdcResult.Failed; }
                Thread.Sleep(Slow(40));   // DDC/CI: the monitor needs 40 ms to prepare the reply
                byte[] rep = new byte[11];
                int status = Transfer(rep, true);
                Hold(20);
                if (status != 0) { Stumbled(); return DdcResult.Failed; }

                // Reply: 6E 88 02 rc code type maxH maxL curH curL checksum
                int o = rep[0] == Address ? 1 : 0;
                if (rep[o] != 0x88 || rep[o + 1] != 0x02 || rep[o + 3] != code) { Stumbled(); return DdcResult.Failed; }
                if (o == 1)
                {
                    byte sum = 0x50;
                    for (int i = 0; i < 10; i++) sum ^= rep[i];
                    if (sum != rep[10])
                    {
                        Log.Write("Bad checksum from " + Location + ": " + BitConverter.ToString(rep));
                        Stumbled();
                        return DdcResult.Failed;
                    }
                }
                Succeeded();
                if (rep[o + 2] != 0) return DdcResult.Unsupported;
                maximum = (rep[o + 5] << 8) | rep[o + 6];
                current = (rep[o + 7] << 8) | rep[o + 8];
                return DdcResult.Ok;
            }
        }

        public override bool Write(byte code, int value)
        {
            using (Lock())
            {
                WaitReady();
                byte hi = (byte)(value >> 8), lo = (byte)value;
                byte[] d = { 0x84, 0x03, code, hi, lo, 0 };
                d[5] = (byte)(Address ^ Host ^ 0x84 ^ 0x03 ^ code ^ hi ^ lo);
                bool ok = Transfer(d, false) == 0;
                if (!ok) Stumbled();
                Hold(50);   // DDC/CI: 50 ms after a set before the next command
                return ok;
            }
        }

        // MCCS capabilities string, read in chunks: request 83 F3 offH offL, reply 6E 80|len E3 offH offL data… chk.
        public override string Capabilities()
        {
            var sb = new System.Text.StringBuilder();
            int offset = 0;
            using (Lock())
            {
                WaitReady();
                for (int guard = 0; guard < 64; guard++)
                {
                    string chunk = null;
                    for (int attempt = 0; attempt < 4 && chunk == null; attempt++)
                    {
                        byte hi = (byte)(offset >> 8), lo = (byte)offset;
                        byte[] req = { 0x83, 0xF3, hi, lo, 0 };
                        req[4] = (byte)(Address ^ Host ^ 0x83 ^ 0xF3 ^ hi ^ lo);
                        if (Transfer(req, false) != 0) { Thread.Sleep(60); continue; }
                        Thread.Sleep(60);
                        byte[] rep = new byte[38];
                        if (Transfer(rep, true) != 0) { Thread.Sleep(60); continue; }
                        int o = rep[0] == Address ? 1 : 0;
                        int len = (rep[o] & 0x7F) - 3;
                        if ((rep[o] & 0x80) == 0 || rep[o + 1] != 0xE3 || len < 0 || len > 32 || ((rep[o + 2] << 8) | rep[o + 3]) != offset)
                        { Thread.Sleep(60); continue; }
                        var text = new System.Text.StringBuilder();
                        for (int i = 0; i < len; i++) if (rep[o + 4 + i] != 0) text.Append((char)rep[o + 4 + i]);
                        chunk = text.ToString();
                        if (len == 0) return sb.ToString();
                        offset += len;
                    }
                    if (chunk == null) return sb.Length > 0 ? sb.ToString() : null;
                    sb.Append(chunk);
                    Thread.Sleep(50);
                }
            }
            return sb.ToString();
        }
    }

    static class WinDdc
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public int l1, t1, r1, b1, l2, t2, r2, b2;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
        [DllImport("dxva2.dll")] internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
        [DllImport("dxva2.dll")] internal static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
        [DllImport("dxva2.dll")] internal static extern bool DestroyPhysicalMonitor(IntPtr h);
        [DllImport("dxva2.dll")] internal static extern bool GetCapabilitiesStringLength(IntPtr h, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, System.Text.StringBuilder caps, uint length);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        // The graphics adapter name behind a \\.\DISPLAYn device, e.g. "NVIDIA GeForce RTX 5070 Ti".
        static string AdapterOf(string deviceName)
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if (string.Equals(dd.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) return dd.DeviceString ?? "";
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            }
            return "";
        }

        // EDIDs of the monitors attached to a \\.\DISPLAYn device, read from the registry, so
        // monitors on this path also get a model name and a stable serial-based identity.
        static List<byte[]> MonitorEdids(string deviceName)
        {
            var list = new List<byte[]>();
            try
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                for (uint i = 0; EnumDisplayDevices(deviceName, i, ref dd, 1); i++)   // 1 = EDD_GET_DEVICE_INTERFACE_NAME
                {
                    // \\?\DISPLAY#SAM7489#5&1b944e99&0&UID4353#{guid} -> DISPLAY\SAM7489\5&1b944e99&0&UID4353
                    var parts = (dd.DeviceID ?? "").Split('#');
                    if (parts.Length >= 3)
                    {
                        string key = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + @"\" + parts[2] + @"\Device Parameters";
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                        {
                            var edid = k == null ? null : k.GetValue("EDID") as byte[];
                            if (edid != null) list.Add(edid);
                        }
                    }
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                }
            }
            catch (Exception ex) { Log.Write("EDID lookup " + deviceName + ": " + ex.Message); }
            return list;
        }

        // skipNvidia: leave out monitors on NVIDIA adapters (already reached through NVAPI).
        public static List<DdcPort> Enumerate(bool skipNvidia)
        {
            var ports = new List<DdcPort>();
            try
            {
                MonitorEnumProc cb = (hMon, hdc, rect, data) =>
                {
                    var info = new MONITORINFOEX();
                    info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    string device = GetMonitorInfo(hMon, ref info) ? info.szDevice : "display";
                    if (skipNvidia && AdapterOf(device).IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    uint n;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) || n == 0) return true;
                    var arr = new PHYSICAL_MONITOR[n];
                    if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
                    var edids = MonitorEdids(device);
                    for (int i = 0; i < n; i++)
                    {
                        var port = new WinPort(arr[i].hPhysicalMonitor, device.Replace("\\\\.\\", "") + "#" + (i + 1));
                        if (i < edids.Count) Edid.Parse(edids[i], out port.Model, out port.Serial, out port.ProductId);
                        ports.Add(port);
                    }
                    return true;
                };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
                GC.KeepAlive(cb);
            }
            catch (Exception ex) { Log.Write("Windows DDC enumerate: " + ex.Message); }
            return ports;
        }
    }

    sealed class WinPort : DdcPort
    {
        IntPtr handle;

        public WinPort(IntPtr handle, string location)
        {
            this.handle = handle;
            Location = "win:" + location;
        }

        public override DdcResult Read(byte code, out int current, out int maximum)
        {
            uint cur = 0, max = 0;
            bool ok = handle != IntPtr.Zero && WinDdc.GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out cur, out max);
            current = ok ? (int)cur : 0;
            maximum = ok ? (int)max : 0;
            return ok ? DdcResult.Ok : DdcResult.Failed;
        }

        public override bool Write(byte code, int value)
        {
            return handle != IntPtr.Zero && WinDdc.SetVCPFeature(handle, code, (uint)value);
        }

        public override string Capabilities()
        {
            uint length;
            if (handle == IntPtr.Zero || !WinDdc.GetCapabilitiesStringLength(handle, out length) || length == 0) return null;
            var sb = new System.Text.StringBuilder((int)length);
            return WinDdc.CapabilitiesRequestAndCapabilitiesReply(handle, sb, length) ? sb.ToString() : null;
        }

        public override void Dispose()
        {
            if (handle != IntPtr.Zero) { WinDdc.DestroyPhysicalMonitor(handle); handle = IntPtr.Zero; }
        }
    }
}
