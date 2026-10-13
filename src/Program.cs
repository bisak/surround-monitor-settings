using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

[assembly: AssemblyTitle("Monitor Control")]
[assembly: AssemblyProduct("Monitor Control")]
[assembly: AssemblyDescription("Control monitor settings over DDC/CI")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace MonitorControl
{
    static class Program
    {
        public const string ShowEventName = "MonitorControl.Show.7c1e2b";
        const string MutexName = "MonitorControl.SingleInstance.7c1e2b";

        [STAThread]
        static void Main(string[] args)
        {
            if (HasArg(args, "/diagnose")) { Diagnose(); return; }
            string snapshot = ArgValue(args, "/snapshot");
            Theme.HideSerials = HasArg(args, "/demo");
            int select;
            int.TryParse(ArgValue(args, "/select"), out select);

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created && snapshot == null)
                {
                    // Already running: ask that copy to show its window.
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); }
                    catch { }
                    return;
                }
                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, snapshot == null ? ShowEventName : null))
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += (s, e) => { Log.Write("Unhandled UI error: " + e.Exception); e.Handled = true; };
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
                    app.Resources = (ResourceDictionary)MainWindow.LoadXaml("Styles.xaml");

                    var window = new MainWindow(showEvent, snapshot, select);
                    if (HasArg(args, "/tray") || snapshot != null) new WindowInteropHelper(window).EnsureHandle();
                    else window.Show();
                    app.Run();
                }
            }
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (var a in args) if (a.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        // Writes a plain-text report of what the app can see, for troubleshooting.
        static void Diagnose()
        {
            var settings = new Settings();
            var sb = new StringBuilder();
            Log.Capture = sb;
            sb.AppendLine("Monitor Control diagnostics");
            sb.AppendLine("Time: " + DateTime.Now);
            sb.AppendLine("Windows: " + Environment.OSVersion + ", 64-bit process: " + Environment.Is64BitProcess);
            sb.AppendLine("I2C struct size: " + Marshal.SizeOf(typeof(Nvapi.I2CInfo)));
            sb.AppendLine();
            var sw = Stopwatch.StartNew();
            string source;
            var monitors = Scanner.Scan(out source);
            sb.AppendLine();
            sb.AppendLine(string.Format("Scan: {0} monitor(s) via {1} in {2} ms", monitors.Count, source, sw.ElapsedMilliseconds));
            foreach (var m in monitors)
            {
                sb.AppendLine(string.Format("Monitor {0}: {1}  serial {2}  ({3})", m.Number, m.Port.Model, m.Port.Serial, m.Port.Location));
                foreach (var f in Feature.All)
                    sb.AppendLine(string.Format("    {0,-14} {1}", f.Label,
                        m.Supports(f.Code) ? m.Value(f.Code) + " / " + m.Max(f.Code) : "not supported"));
                sb.AppendLine("    Capabilities: " + (m.Port.Capabilities() ?? "(no reply)"));
                m.Port.Dispose();
            }
            File.WriteAllText(Path.Combine(settings.Dir, "MonitorControl-diagnostics.txt"), sb.ToString());
        }
    }
}
