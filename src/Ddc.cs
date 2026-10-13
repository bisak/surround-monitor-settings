using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace MonitorControl
{
    enum FeatureKind { Slider, Choice, Input }

    sealed class Feature
    {
        public readonly byte Code;
        public readonly string Key;
        public readonly string Label;
        public readonly FeatureKind Kind;
        public readonly bool InPresets;

        Feature(byte code, string key, string label, FeatureKind kind, bool inPresets)
        {
            Code = code; Key = key; Label = label; Kind = kind; InPresets = inPresets;
        }

        // Order matters: presets apply color tone first, because changing it can
        // reset the monitor's other color settings.
        // Picture mode (VCP DC) is deliberately absent: Samsung's G5 answers reads
        // for it but doesn't declare it, and writing it leaves the OSD in odd states.
        public static readonly Feature[] All =
        {
            new Feature(0x14, "colortone", "Color tone", FeatureKind.Choice, true),
            new Feature(0x10, "brightness", "Brightness", FeatureKind.Slider, true),
            new Feature(0x12, "contrast", "Contrast", FeatureKind.Slider, true),
            new Feature(0x87, "sharpness", "Sharpness", FeatureKind.Slider, true),
            new Feature(0x16, "red", "Red", FeatureKind.Slider, true),
            new Feature(0x18, "green", "Green", FeatureKind.Slider, true),
            new Feature(0x1A, "blue", "Blue", FeatureKind.Slider, true),
            new Feature(0x62, "volume", "Volume", FeatureKind.Slider, false),
            new Feature(0x60, "input", "Input source", FeatureKind.Input, false),
        };

        public static Feature ByCode(byte code)
        {
            foreach (var f in All) if (f.Code == code) return f;
            return null;
        }

        // Standard MCCS value names.
        public static readonly Dictionary<int, string> InputNames = new Dictionary<int, string>
        {
            { 0x01, "VGA 1" }, { 0x02, "VGA 2" }, { 0x03, "DVI 1" }, { 0x04, "DVI 2" },
            { 0x0F, "DisplayPort 1" }, { 0x10, "DisplayPort 2" }, { 0x11, "HDMI 1" }, { 0x12, "HDMI 2" },
            { 0x1B, "USB-C" },
        };

        public static readonly Dictionary<int, string> ColorPresetNames = new Dictionary<int, string>
        {
            { 0x01, "sRGB" }, { 0x02, "Native" }, { 0x03, "4000K" }, { 0x04, "5000K" }, { 0x05, "6500K" },
            { 0x06, "7500K" }, { 0x07, "8200K" }, { 0x08, "9300K" }, { 0x09, "10000K" }, { 0x0A, "11500K" },
            { 0x0B, "User 1" }, { 0x0C, "User 2" }, { 0x0D, "User 3" },
        };
    }

    // Monitor-specific knowledge where the monitor's own capabilities string is wrong.
    static class Profiles
    {
        // Samsung Odyssey G5 (S27/S32CG55, firmware model LEMON23): declares color preset
        // values 05 08 0B 0C, but ignores them. It actually takes a position 0-4 and reads
        // it back. The factory default reads 1, and 4 looks identical to Standard while the
        // RGB gains sit at 50/50/50, so the order is the manual's list with Custom last.
        public static List<KeyValuePair<int, string>> ColorTone(Monitor m)
        {
            if (m.Port.Model.IndexOf("Odyssey G5", StringComparison.OrdinalIgnoreCase) >= 0 && m.Max(0x14) == 4)
                return new List<KeyValuePair<int, string>>
                {
                    new KeyValuePair<int, string>(0, "Cool"),
                    new KeyValuePair<int, string>(1, "Standard"),
                    new KeyValuePair<int, string>(2, "Warm 1"),
                    new KeyValuePair<int, string>(3, "Warm 2"),
                    new KeyValuePair<int, string>(4, "Custom"),
                };
            return null;
        }
    }

    static class Capabilities
    {
        // Parses the vcp(...) section of an MCCS capabilities string into
        // code -> declared values (null when the code lists no values).
        public static Dictionary<int, List<int>> ParseVcp(string caps)
        {
            if (string.IsNullOrEmpty(caps)) return null;
            int i = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var result = new Dictionary<int, List<int>>();
            var token = new StringBuilder();
            List<int> values = null;
            int depth = 1, lastCode = -1;
            for (i += 4; i < caps.Length && depth > 0; i++)
            {
                char c = caps[i];
                if (Uri.IsHexDigit(c)) { token.Append(c); continue; }
                if (token.Length > 0)
                {
                    int v = Convert.ToInt32(token.ToString(), 16);
                    if (depth == 1) { lastCode = v; result[v] = null; }
                    else if (depth == 2 && values != null) values.Add(v);
                    token.Clear();
                }
                if (c == '(')
                {
                    depth++;
                    if (depth == 2 && lastCode >= 0) { values = new List<int>(); result[lastCode] = values; }
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 1) values = null;
                }
            }
            return result.Count > 0 ? result : null;
        }
    }

    enum DdcResult { Ok, Unsupported, Failed }

    // One physical monitor's DDC/CI channel.
    abstract class DdcPort : IDisposable
    {
        public string Model = "";
        public string Serial = "";
        public string Location = "";
        public abstract DdcResult Read(byte code, out int current, out int maximum);
        public abstract bool Write(byte code, int value);
        public virtual string Capabilities() { return null; }
        public virtual void Dispose() { }
    }

    sealed class Monitor
    {
        public readonly DdcPort Port;
        public Lane Lane;   // this monitor's own I/O thread, created after the scan
        public int Number;
        public string Name = "";
        readonly Dictionary<byte, int> maxima = new Dictionary<byte, int>();
        readonly Dictionary<byte, int> values = new Dictionary<byte, int>();

        // Codes the monitor declares in its capabilities string (null if it gave none).
        public Dictionary<int, List<int>> Declared;

        public Monitor(DdcPort port) { Port = port; }

        public bool Declares(byte code) { return Declared == null || Declared.ContainsKey(code); }

        // Valid choices for a Choice/Input feature, from a known profile or the declared list.
        public List<KeyValuePair<int, string>> Choices(Feature f)
        {
            if (f.Code == 0x14)
            {
                var profile = Profiles.ColorTone(this);
                if (profile != null) return profile;
            }
            List<int> declared;
            if (Declared == null || !Declared.TryGetValue(f.Code, out declared) || declared == null || declared.Count == 0)
                return f.Code == 0x60
                    ? Feature.InputNames.Where(kv => kv.Key >= 0x0F).ToList()
                    : new List<KeyValuePair<int, string>>();
            var names = f.Code == 0x60 ? Feature.InputNames : Feature.ColorPresetNames;
            return declared.Select(v => new KeyValuePair<int, string>(v,
                names.ContainsKey(v) ? names[v] : "Value " + v.ToString("X2"))).ToList();
        }

        // Whether reading the value back after a write confirms it took.
        public bool CanVerify(Feature f)
        {
            if (f.Kind == FeatureKind.Input) return false;
            if (f.Code == 0x14) return Profiles.ColorTone(this) != null;
            return true;
        }

        // Stable identity across restarts and cable swaps when the EDID has a serial.
        public string Key { get { return Port.Serial.Length > 0 ? Port.Serial : Port.Location; } }

        public string DefaultName
        {
            get { return (Port.Model.Length > 0 ? Port.Model : "Monitor") + " #" + Number; }
        }

        public bool Supports(byte code) { lock (values) return maxima.ContainsKey(code); }
        public int Max(byte code) { lock (values) { int m; return maxima.TryGetValue(code, out m) ? m : 0; } }
        public int Value(byte code) { lock (values) { int v; return values.TryGetValue(code, out v) ? v : 0; } }
        public void SetCached(byte code, int v) { lock (values) values[code] = v; }

        public List<byte> SupportedCodes()
        {
            lock (values) return new List<byte>(maxima.Keys);
        }

        // Everything below does bus I/O: call from the worker thread only.

        public bool Probe()
        {
            bool any = false;
            Declared = Capabilities.ParseVcp(Port.Capabilities());
            foreach (var f in Feature.All)
            {
                if (!Declares(f.Code)) continue;
                int cur, max;
                if (ReadRetry(f.Code, out cur, out max, 2) && max > 0)
                {
                    lock (values) { maxima[f.Code] = max; values[f.Code] = cur; }
                    any = true;
                }
            }
            return any;
        }

        public void Refresh()
        {
            foreach (var code in SupportedCodes())
            {
                int cur, max;
                if (ReadRetry(code, out cur, out max, 3)) SetCached(code, cur);
            }
        }

        public bool ReadRetry(byte code, out int cur, out int max, int attempts)
        {
            cur = max = 0;
            for (int i = 0; i < attempts; i++)
            {
                var r = Port.Read(code, out cur, out max);
                if (r == DdcResult.Ok) return true;
                if (r == DdcResult.Unsupported) return false;
                Thread.Sleep(60 * (i + 1));
            }
            return false;
        }

        public bool WriteVerified(byte code, int value, bool verify)
        {
            for (int i = 0; i < 3; i++)
            {
                if (Port.Write(code, value))
                {
                    if (!verify) { SetCached(code, value); return true; }
                    int cur, max;   // the port itself waits out the post-write gap
                    if (Port.Read(code, out cur, out max) == DdcResult.Ok && cur == value)
                    {
                        SetCached(code, value);
                        return true;
                    }
                }
                Thread.Sleep(80 * (i + 1));
            }
            // Re-sync the cache with what the monitor actually has.
            int actual, mx;
            if (verify && ReadRetry(code, out actual, out mx, 2)) SetCached(code, actual);
            return false;
        }
    }

    static class Edid
    {
        public static void Parse(byte[] e, out string model, out string serial)
        {
            model = ""; serial = "";
            if (e == null || e.Length < 128 || e[0] != 0 || e[1] != 0xFF || e[7] != 0) return;
            for (int i = 54; i <= 108; i += 18)
            {
                if (e[i] != 0 || e[i + 1] != 0) continue;
                string text = Text(e, i + 5);
                if (e[i + 3] == 0xFC) model = text;
                else if (e[i + 3] == 0xFF) serial = text;
            }
            if (serial.Length == 0)
            {
                uint num = (uint)(e[12] | (e[13] << 8) | (e[14] << 16) | (e[15] << 24));
                if (num != 0) serial = string.Format("{0:X2}{1:X2}-{2}", e[8], e[9], num);
            }
        }

        static string Text(byte[] e, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + 13 && i < e.Length; i++)
            {
                if (e[i] == 0x0A || e[i] == 0) break;
                if (e[i] >= 0x20 && e[i] < 0x7F) sb.Append((char)e[i]);
            }
            return sb.ToString().Trim();
        }
    }
}
