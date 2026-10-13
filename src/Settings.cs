using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MonitorControl
{
    // Plain key=value file. Lives next to the exe when that folder is writable
    // (portable), otherwise in %APPDATA%\MonitorControl.
    sealed class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly string Dir;
        readonly string file;

        public Settings()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (IsWritable(exeDir)) Dir = exeDir;
            else
            {
                Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorControl");
                Directory.CreateDirectory(Dir);
            }
            file = Path.Combine(Dir, "MonitorControl.ini");
            if (!Load(file)) Load(file + ".bak");
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        bool Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                values.Clear();
                foreach (var line in lines)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                return values.Count > 0;
            }
            catch (Exception ex) { Log.Write("Settings load " + path + ": " + ex.Message); return false; }
        }

        public void Save()
        {
            try
            {
                var lines = new List<string> { "# Monitor Control settings" };
                foreach (var k in values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    lines.Add(k + "=" + values[k]);
                string tmp = file + ".tmp";
                File.WriteAllLines(tmp, lines, Encoding.UTF8);
                if (File.Exists(file)) File.Copy(file, file + ".bak", true);
                File.Copy(tmp, file, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { Log.Write("Settings save: " + ex.Message); }
        }

        public bool Has(string key) { return values.ContainsKey(key); }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key, ""), out v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string v = Get(key, null);
            return v == null ? fallback : v == "1";
        }

        public void Set(string key, string value) { values[key] = value.Replace("\r", "").Replace("\n", ""); }
        public void Set(string key, int value) { values[key] = value.ToString(); }
        public void Set(string key, bool value) { values[key] = value ? "1" : "0"; }

        public void RemovePrefix(string prefix)
        {
            foreach (var k in values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                values.Remove(k);
        }

        public List<string> KeysWithPrefix(string prefix)
        {
            return values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static string PathName;
        public static StringBuilder Capture;

        public static void Write(string message)
        {
            lock (gate)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
                if (Capture != null) Capture.AppendLine(line);
                if (PathName == null) return;
                try
                {
                    var fi = new FileInfo(PathName);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        File.Copy(PathName, PathName + ".old", true);
                        File.Delete(PathName);
                    }
                    File.AppendAllText(PathName, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
