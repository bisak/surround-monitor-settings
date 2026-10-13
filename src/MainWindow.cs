using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace MonitorControl
{
    sealed class MainWindow : Window
    {
        const int WM_HOTKEY = 0x0312;
        const int HK_BRIGHTER = 1, HK_DIMMER = 2, HK_PRESET = 100;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        const uint VK_PRIOR = 0x21, VK_NEXT = 0x22;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MonitorControl";

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly Settings settings = new Settings();
        readonly DdcWorker worker = new DdcWorker();
        readonly EventWaitHandle showEvent;
        readonly string snapshotPath;
        readonly int snapshotSelect;
        readonly Osd osd = new Osd();
        readonly DispatcherTimer rescanTimer = new DispatcherTimer();
        readonly Tray tray;

        List<Monitor> monitors = new List<Monitor>();
        readonly HashSet<Monitor> selected = new HashSet<Monitor>();
        readonly List<ScreenTile> tiles = new List<ScreenTile>();
        bool scanning, exiting, building, initialized;
        Slider dragging;   // the slider being dragged, so nothing rebuilds under the pointer
        IntPtr hwnd;

        readonly Grid root;
        readonly StackPanel wall, adjustLeft, adjustRight, presetList, hotkeyHint;
        readonly TextBlock heading, subHeading, status, wallEmpty, stepValue, subtitle, linkText;
        readonly ToggleButton allToggle;
        readonly CheckBox startupBox, trayBox;
        readonly Button copyButton, resetColorButton;
        readonly Border linkBadge;

        public static object LoadXaml(string name)
        {
            using (var s = typeof(MainWindow).Assembly.GetManifestResourceStream("MonitorControl." + name))
                return XamlReader.Load(s);
        }

        public MainWindow(EventWaitHandle showEvent, string snapshotPath, int snapshotSelect)
        {
            this.showEvent = showEvent;
            this.snapshotPath = snapshotPath;
            this.snapshotSelect = snapshotSelect;
            Log.PathName = IOPath.Combine(Settings.LogDir, "MonitorControl.log");
            Log.Write("Starting " + typeof(MainWindow).Assembly.GetName().Version + ", settings in " + settings.Dir);

            Title = "Monitor Control";
            Width = 1280; Height = 840; MinWidth = 1180; MinHeight = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Res("Canvas");
            Icon = Icons.WindowIcon();

            root = (Grid)LoadXaml("Layout.xaml");
            Content = root;
            wall = Find<StackPanel>("Wall");
            wallEmpty = Find<TextBlock>("WallEmpty");
            adjustLeft = Find<StackPanel>("AdjustLeft");
            adjustRight = Find<StackPanel>("AdjustRight");
            presetList = Find<StackPanel>("PresetList");
            hotkeyHint = Find<StackPanel>("HotkeyHint");
            heading = Find<TextBlock>("Heading");
            subHeading = Find<TextBlock>("SubHeading");
            subtitle = Find<TextBlock>("Subtitle");
            status = Find<TextBlock>("Status");
            stepValue = Find<TextBlock>("StepValue");
            allToggle = Find<ToggleButton>("AllToggle");
            startupBox = Find<CheckBox>("StartupBox");
            trayBox = Find<CheckBox>("TrayBox");
            copyButton = Find<Button>("CopyButton");
            resetColorButton = Find<Button>("ResetColorButton");
            linkBadge = Find<Border>("LinkBadge");
            linkText = Find<TextBlock>("LinkText");

            allToggle.Click += (s, e) => SelectAll();
            Find<Button>("IdentifyButton").Click += (s, e) => Identify(Targets());
            Find<Button>("RescanButton").Click += (s, e) => Rescan();
            Find<Button>("ReadButton").Click += (s, e) => RefreshValues();
            copyButton.Click += (s, e) => CopyToOthers();
            resetColorButton.Click += (s, e) => ResetColor();
            Find<Button>("SavePresetButton").Click += (s, e) => SaveNewPreset();
            Find<Button>("StepDown").Click += (s, e) => ChangeStep(-5);
            Find<Button>("StepUp").Click += (s, e) => ChangeStep(5);

            startupBox.IsChecked = StartupEnabled();
            startupBox.Click += (s, e) => SetStartup(startupBox.IsChecked == true);
            trayBox.IsChecked = settings.GetBool("closetotray", true);
            trayBox.Click += (s, e) => { settings.Set("closetotray", trayBox.IsChecked == true); settings.Save(); };
            stepValue.Text = Step() + "%";
            hotkeyHint.Children.Add(Theme.Text("Brightness", 12, "Muted"));
            hotkeyHint.Children.Add(Theme.Keys("Ctrl", "Alt", "PgUp", "PgDn"));
            ((FrameworkElement)hotkeyHint.Children[1]).Margin = new Thickness(6, 0, 0, 0);

            if (snapshotPath == null)
                tray = new Tray(ShowWindow, ToggleWindow, SetAllBrightness, ExitApp);

            EnsureDefaultPresets();
            RefreshPresets();
            BuildWall();

            rescanTimer.Tick += (s, e) => { rescanTimer.Stop(); Rescan(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            SourceInitialized += OnSourceInitialized;

            if (snapshotPath != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000; Top = 0;
                ShowInTaskbar = false;
                ShowActivated = false;
            }
            else
            {
                var t = new Thread(WaitForShowRequests) { IsBackground = true };
                t.Start();
            }
        }

        T Find<T>(string name) where T : class { return root.FindName(name) as T; }

        // ---------- window lifecycle ----------

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            PaperTitleBar();
            RegisterHotkeys();
            if (!initialized)
            {
                initialized = true;
                if (StartupEnabled()) SetStartup(true);   // keep the path current if the exe moved
                Rescan();
            }
        }

        void PaperTitleBar()
        {
            try
            {
                int on = 1, caption = 0x00191919, text = 0x00F0F2F2, border = 0x00303030;   // COLORREF = 0x00BBGGRR
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            }
            catch { }
        }

        void WaitForShowRequests()
        {
            while (true)
            {
                try { showEvent.WaitOne(); }
                catch { return; }
                Ui(ShowWindow);
            }
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            RefreshTiles();
            BuildAdjust();
        }

        void ToggleWindow()
        {
            if (IsVisible && WindowState != WindowState.Minimized) Hide();
            else ShowWindow();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!exiting && tray != null && trayBox.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
                if (!settings.GetBool("traytipshown", false))
                {
                    tray.Balloon("Monitor Control is still running", "Hotkeys keep working. Right-click the tray icon to exit.");
                    settings.Set("traytipshown", true);
                    settings.Save();
                }
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            UnregisterHotkeys();
            if (tray != null) tray.Dispose();
            foreach (var m in monitors) if (m.Lane != null) m.Lane.Stop();
            worker.Dispose();
            osd.Close();
            Log.Write("Exit");
            base.OnClosed(e);
            Application.Current.Shutdown();
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        void Ui(Action a)
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(a);
        }

        void SetStatus(string text) { status.Text = text; }

        void SetLink(string text, string bg, string fg)
        {
            linkText.Text = text;
            linkBadge.Background = Theme.Res(bg);
            linkText.Foreground = Theme.Res(fg);
        }

        void OnDisplayChanged(object sender, EventArgs e) { Ui(() => RestartRescanTimer(3)); }

        void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) Ui(() => RestartRescanTimer(6));
        }

        void RestartRescanTimer(int seconds)
        {
            rescanTimer.Stop();
            rescanTimer.Interval = TimeSpan.FromSeconds(seconds);
            rescanTimer.Start();
        }

        // ---------- monitors ----------

        void Rescan()
        {
            if (scanning) return;
            scanning = true;
            SetStatus("Looking for your screens…");
            SetLink("SCANNING", "YellowBg", "YellowText");
            subtitle.Text = "Reading each monitor's settings. This takes a few seconds.";
            var old = monitors;
            monitors = new List<Monitor>();
            selected.Clear();
            BuildWall();
            worker.Run(() =>
            {
                var found = new List<Monitor>();
                string source = "none";
                try
                {
                    foreach (var m in old) { if (m.Lane != null) m.Lane.Dispose(); m.Port.Dispose(); }
                    found = Scanner.Scan(out source);
                }
                catch (Exception ex) { Log.Write("Scan failed: " + ex); }
                Ui(() => OnScanned(found, source));
            });
        }

        void OnScanned(List<Monitor> found, string source)
        {
            scanning = false;
            foreach (var m in found)
            {
                m.Name = settings.Get("name." + m.Key, DefaultName(m, found.Count));
                m.Lane = new Lane(m);
                m.Lane.WriteFailed += (mon, code, value) => Ui(() => OnWriteFailed(mon, code));
            }
            var order = settings.Get("order", "").Split('|').ToList();
            monitors = found.OrderBy(m => { int i = order.IndexOf(m.Key); return i < 0 ? 100 + m.Number : i; }).ToList();
            selected.Clear();
            foreach (var m in monitors) selected.Add(m);
            if (snapshotSelect > 0 && snapshotSelect <= monitors.Count)
            {
                selected.Clear();
                selected.Add(monitors[snapshotSelect - 1]);
            }
            BuildWall();

            string via = source == "NVIDIA driver" ? "the NVIDIA driver" : source == "NVIDIA driver and Windows" ? "the NVIDIA driver and Windows" : "Windows";
            if (found.Count == 0)
            {
                SetLink("NO SCREENS", "RedBg", "RedText");
                subtitle.Text = "No monitor answered. Turn on DDC/CI in each monitor's own menu, then choose Rescan.";
            }
            else
            {
                SetLink("CONNECTED", "GreenBg", "GreenText");
                string model = found.Select(m => m.Port.Model).Distinct().Count() == 1 && found[0].Port.Model.Length > 0 ? " " + found[0].Port.Model : "";
                subtitle.Text = string.Format("{0}{1} screen{2}, connected through {3}.", Count(found.Count), model, found.Count == 1 ? "" : "s", via);
            }
            SetStatus("");
            if (snapshotPath != null && Theme.HideSerials) ShowSampleValues();
            if (snapshotPath != null) TakeSnapshot();
        }

        static string Count(int n)
        {
            string[] words = { "No", "One", "Two", "Three", "Four", "Five", "Six" };
            return n < words.Length ? words[n] : n.ToString();
        }

        static string DefaultName(Monitor m, int count)
        {
            if (count == 3) return new[] { "Left", "Center", "Right" }[m.Number - 1];
            if (count == 2) return new[] { "Left", "Right" }[m.Number - 1];
            return m.DefaultName;
        }

        // Docs screenshots (/snapshot ... /demo): typical daytime values instead of whatever the
        // screens happen to be set to. Only the display cache changes; nothing is sent to monitors.
        void ShowSampleValues()
        {
            var sample = new Dictionary<byte, int> { { 0x10, 60 }, { 0x12, 80 }, { 0x87, 50 }, { 0x62, 40 } };
            foreach (var m in monitors)
            {
                foreach (var kv in sample)
                    if (m.Supports(kv.Key)) m.SetCached(kv.Key, (int)Math.Round(kv.Value * m.Max(kv.Key) / 100.0));
                var tone = m.Choices(Feature.ByCode(0x14)).FirstOrDefault(c => c.Value == "Standard");
                if (tone.Value != null) m.SetCached(0x14, tone.Key);
            }
            RefreshTiles();
            BuildAdjust();
        }

        void TakeSnapshot()
        {
            Show();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                root.UpdateLayout();
                var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var f = File.Create(snapshotPath)) enc.Save(f);
                ExitApp();
            }));
        }

        List<Monitor> Targets() { return monitors.Where(selected.Contains).ToList(); }

        void BuildWall()
        {
            wall.Children.Clear();
            tiles.Clear();
            foreach (var m in monitors)
            {
                var tile = new ScreenTile(m);
                var mon = m;
                tile.MouseLeftButtonUp += (s, e) => OnTileClick(mon);
                tile.ContextMenu = TileMenu(mon);
                tiles.Add(tile);
                wall.Children.Add(tile);
            }
            wallEmpty.Visibility = monitors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            wallEmpty.Text = scanning ? "Looking for your screens…" : "No screens found. Turn on DDC/CI in each monitor's menu, then choose Rescan.";
            RefreshTiles();
            BuildAdjust();
        }

        ContextMenu TileMenu(Monitor m)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Adjust only this screen", () => { selected.Clear(); selected.Add(m); OnSelectionChanged(); }));
            menu.Items.Add(MenuItem("Rename…", () => Rename(m)));
            menu.Items.Add(MenuItem("Identify", () => Identify(new List<Monitor> { m })));
            menu.Items.Add(MenuItem("Move left", () => MoveScreen(m, -1)));
            menu.Items.Add(MenuItem("Move right", () => MoveScreen(m, 1)));
            return menu;
        }

        static MenuItem MenuItem(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => click();
            return item;
        }

        void OnTileClick(Monitor m)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            {
                if (selected.Contains(m)) { if (selected.Count > 1) selected.Remove(m); }
                else selected.Add(m);
            }
            else
            {
                selected.Clear();
                selected.Add(m);
            }
            OnSelectionChanged();
        }

        void SelectAll()
        {
            foreach (var m in monitors) selected.Add(m);
            OnSelectionChanged();
        }

        void OnSelectionChanged()
        {
            RefreshTiles();
            BuildAdjust();
        }

        void RefreshTiles()
        {
            foreach (var t in tiles) t.Update(selected.Contains(t.Monitor));
            allToggle.IsChecked = monitors.Count > 0 && selected.Count == monitors.Count;
            allToggle.Visibility = monitors.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        }

        void MoveScreen(Monitor m, int delta)
        {
            int i = monitors.IndexOf(m), j = i + delta;
            if (i < 0 || j < 0 || j >= monitors.Count) return;
            monitors[i] = monitors[j];
            monitors[j] = m;
            settings.Set("order", string.Join("|", monitors.Select(x => x.Key)));
            settings.Save();
            BuildWall();
        }

        void Rename(Monitor m)
        {
            string name = Dialog.Show(this, "Rename screen", "What should this screen be called?", m.Name, "Rename");
            if (string.IsNullOrEmpty(name)) return;
            m.Name = name;
            settings.Set("name." + m.Key, name);
            settings.Save();
            RefreshTiles();
            BuildAdjust();
        }

        // ---------- the adjust panels ----------

        void BuildAdjust()
        {
            if (dragging != null) return;
            building = true;
            adjustLeft.Children.Clear();
            adjustRight.Children.Clear();
            var targets = Targets();

            if (targets.Count == 0)
            {
                heading.Text = scanning ? "Connecting" : "No screens";
                subHeading.Text = scanning ? "Reading each monitor's settings" : "Turn on DDC/CI, then Rescan";
                copyButton.Visibility = Visibility.Collapsed;
                resetColorButton.Visibility = Visibility.Collapsed;
                building = false;
                return;
            }

            bool all = targets.Count == monitors.Count && monitors.Count > 1;
            if (targets.Count == 1)
            {
                var m = targets[0];
                heading.Text = m.Name;
                subHeading.Text = (m.Port.Model.Length > 0 ? m.Port.Model : "Monitor") + (m.Port.Serial.Length > 0 && !Theme.HideSerials ? "  SN " + m.Port.Serial : "");
            }
            else
            {
                heading.Text = all ? "All screens" : JoinNames(targets);
                subHeading.Text = "Changes apply to " + (all ? "every screen" : targets.Count + " screens");
            }
            copyButton.Visibility = targets.Count == 1 && monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            resetColorButton.Visibility = targets.Any(m => m.CanResetColor) ? Visibility.Visible : Visibility.Collapsed;

            AddBrightness(targets);
            AddSlider(adjustLeft, 0x12, "Contrast", "Ink", targets);
            AddSlider(adjustLeft, 0x87, "Sharpness", "Ink", targets);
            AddSlider(adjustLeft, 0x62, "Volume", "Ink", targets);

            adjustRight.Children.Add(CardTitle("Color"));
            AddChoice(0xDC, "Picture mode", targets);
            AddChoice(0x14, "Color tone", targets);
            if (targets.Count == 1) AddChoice(0x60, "Input", targets);
            building = false;
        }

        static TextBlock CardTitle(string text)
        {
            var t = Theme.Serif(text, 24);
            t.Margin = new Thickness(0, 0, 0, 16);
            return t;
        }

        static string JoinNames(List<Monitor> list)
        {
            if (list.Count == 2) return list[0].Name + " and " + list[1].Name;
            return string.Join(", ", list.Take(list.Count - 1).Select(m => m.Name)) + " and " + list[list.Count - 1].Name;
        }

        static string Pct(int v, int max) { return max == 0 ? "" : ((int)Math.Round(v * 100.0 / max)).ToString() + "%"; }

        // A slider that follows the pointer from the first press: click anywhere on the
        // rail and drag in one motion. The wheel steps by one; arrow keys work as usual.
        Slider MakeSlider(Monitor reference, byte code, Brush fill, List<Monitor> targets, Action<int> onValue)
        {
            var f = Feature.ByCode(code);
            int max = reference.Max(code);
            var slider = new Slider
            {
                Style = Theme.Style("Slide"),
                Minimum = 0,
                Maximum = max,
                Value = Math.Max(0, Math.Min(max, reference.Value(code))),
                LargeChange = Math.Max(1, max / 10),
                Foreground = fill,
            };
            AutomationName(slider, f.Label);
            slider.ValueChanged += (s, e) =>
            {
                int v = (int)Math.Round(e.NewValue);
                onValue(v);
                if (building) return;
                Apply(f, v, max, targets);
                RefreshTiles();
            };
            Action<MouseEventArgs> follow = e =>
            {
                var track = slider.Template.FindName("PART_Track", slider) as Track;
                if (track == null) return;
                double v = track.ValueFromPoint(e.GetPosition(track));
                slider.Value = Math.Max(0, Math.Min(max, Math.Round(v)));
            };
            slider.PreviewMouseLeftButtonDown += (s, e) =>
            {
                slider.Focus();
                if (slider.CaptureMouse()) dragging = slider;   // only block rebuilds while we really own the pointer
                follow(e);
                e.Handled = true;
            };
            slider.PreviewMouseMove += (s, e) => { if (dragging == slider && e.LeftButton == MouseButtonState.Pressed) follow(e); };
            slider.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (dragging != slider) return;
                dragging = null;
                slider.ReleaseMouseCapture();
                e.Handled = true;
            };
            slider.LostMouseCapture += (s, e) => { if (dragging == slider) dragging = null; };
            slider.PreviewMouseWheel += (s, e) =>
            {
                slider.Value = Math.Max(0, Math.Min(max, slider.Value + (e.Delta > 0 ? 1 : -1)));
                e.Handled = true;
            };
            return slider;
        }

        void AddBrightness(List<Monitor> targets)
        {
            var reference = targets.FirstOrDefault(m => m.Supports(0x10));
            if (reference == null) return;
            int max = reference.Max(0x10);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Theme.Text("Brightness", 14, "Text");
            label.VerticalAlignment = VerticalAlignment.Bottom;
            label.Margin = new Thickness(0, 0, 0, 8);
            var value = Theme.Serif(Pct(reference.Value(0x10), max), 52);
            value.Margin = new Thickness(0, -10, 0, -6);
            Grid.SetColumn(value, 1);
            top.Children.Add(label);
            top.Children.Add(value);
            row.Children.Add(top);
            row.Children.Add(MakeSlider(reference, 0x10, Theme.Res("Ink"), targets, v => value.Text = Pct(v, max)));
            adjustLeft.Children.Add(row);
        }

        void AddSlider(StackPanel host, byte code, string label, string color, List<Monitor> targets)
        {
            var reference = targets.FirstOrDefault(m => m.Supports(code));
            if (reference == null) return;
            int max = reference.Max(code);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var value = Theme.Text(Pct(reference.Value(code), max), 13, "Text");
            value.FontFamily = Theme.Font("Mono");
            Grid.SetColumn(value, 1);
            top.Children.Add(Theme.Text(label, 13, "Muted"));
            top.Children.Add(value);
            row.Children.Add(top);
            row.Children.Add(MakeSlider(reference, code, Theme.Res(color), targets, v => value.Text = Pct(v, max)));
            host.Children.Add(row);
        }

        static void AutomationName(DependencyObject d, string name)
        {
            System.Windows.Automation.AutomationProperties.SetName(d, name);
        }

        // A segmented control with one segment per value the monitor actually accepts.
        void AddChoice(byte code, string label, List<Monitor> targets)
        {
            var f = Feature.ByCode(code);
            var reference = targets.FirstOrDefault(m => m.Supports(code) && m.Choices(f).Count > 0);
            if (reference == null) return;
            var choices = reference.Choices(f);
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(Theme.Text(label, 13, "Muted"));
            var well = new Border { Background = Theme.Res("Well"), CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var row = new WrapPanel();
            well.Child = row;
            var segs = new List<ToggleButton>();
            Action sync = () => { foreach (var sg in segs) sg.IsChecked = (int)sg.Tag == reference.Value(code); };
            foreach (var kv in choices)
            {
                int v = kv.Key;
                string name = kv.Value;
                var seg = new ToggleButton { Content = name, Style = Theme.Style("Seg"), Tag = v };
                AutomationName(seg, label + " " + name);
                seg.Click += (s, e) =>
                {
                    if (v == reference.Value(code)) { sync(); return; }
                    if (f.Kind == FeatureKind.Input)
                    {
                        sync();
                        string ok = Dialog.Show(this, "Switch to " + name + "?",
                            "If nothing is plugged into " + name + ", " + reference.Name + " goes dark and this app can't switch it back. " +
                            "Use the monitor's own button to return.", null, "Switch input");
                        if (ok == null) return;
                    }
                    Apply(f, v, reference.Max(code), targets);
                    sync();
                    RefreshTiles();
                };
                segs.Add(seg);
                row.Children.Add(seg);
            }
            sync();
            panel.Children.Add(well);
            adjustRight.Children.Add(panel);
        }

        void Apply(Feature f, int value, int refMax, List<Monitor> targets)
        {
            foreach (var m in targets)
            {
                if (!m.Supports(f.Code)) continue;
                int v;
                if (f.Kind == FeatureKind.Slider) v = Scale(value, refMax, m.Max(f.Code));
                else if (m.Choices(f).Any(c => c.Key == value)) v = value;
                else continue;   // never send a value this monitor doesn't accept
                m.SetCached(f.Code, v);
                m.Lane.Write(f.Code, v, m.CanVerify(f), true);
            }
        }

        void ResetColor()
        {
            var targets = Targets().Where(m => m.CanResetColor).ToList();
            if (targets.Count == 0) return;
            if (Dialog.Show(this, "Reset color?", "Restores the factory white balance and color tone on " +
                (targets.Count == 1 ? targets[0].Name : targets.Count + " screens") + ".", null, "Reset color") == null) return;
            SetStatus("Restoring factory color…");
            var ignored = new List<string>();
            Lane.ForEach(targets, m => { if (!m.ResetColor()) lock (ignored) ignored.Add(m.Name); },
                () => Ui(() =>
                {
                    RefreshTiles();
                    BuildAdjust();
                    SetStatus(ignored.Count == 0 ? "Factory color restored."
                        : string.Join(", ", ignored) + " didn't respond to the color reset. Use the monitor's own menu.");
                }));
        }

        void OnWriteFailed(Monitor m, byte code)
        {
            var f = Feature.ByCode(code);
            SetStatus(string.Format("{0} didn't accept the {1} change. Its current picture mode may lock it.",
                m.Name, f == null ? "setting" : f.Label.ToLowerInvariant()));
            RefreshTiles();
            if (IsVisible) BuildAdjust();
        }

        void RefreshValues()
        {
            var list = monitors;
            SetStatus("Reading the current settings from your screens…");
            Lane.ForEach(list, m => m.Refresh(),
                () => Ui(() => { RefreshTiles(); BuildAdjust(); SetStatus("Up to date with your screens."); }));
        }

        void CopyToOthers()
        {
            var targets = Targets();
            if (targets.Count != 1) return;
            var src = targets[0];
            foreach (var f in Feature.All.Where(x => x.InPresets && src.Supports(x.Code)))
                foreach (var m in monitors.Where(x => x != src && x.Supports(f.Code)))
                {
                    int v = f.Kind == FeatureKind.Slider ? Scale(src.Value(f.Code), src.Max(f.Code), m.Max(f.Code)) : src.Value(f.Code);
                    if (f.Kind != FeatureKind.Slider && !m.Choices(f).Any(c => c.Key == v)) continue;
                    m.SetCached(f.Code, v);
                    m.Lane.Write(f.Code, v, m.CanVerify(f), false);
                }
            RefreshTiles();
            Lane.ForEach(monitors, null, () => Ui(() => SetStatus("Copied " + src.Name + "'s settings to the other screens.")));
        }

        void Identify(List<Monitor> targets)
        {
            // One screen at a time so it's clear which is which.
            var queue = new Queue<Monitor>(targets.Where(m => m.Supports(0x10) && m.Lane != null));
            Action next = null;
            next = () =>
            {
                if (queue.Count == 0) { SetStatus("Right-click a screen to rename it or move it to match your desk."); return; }
                var m = queue.Dequeue();
                SetStatus("Blinking " + m.Name + "…");
                Lane.ForEach(new[] { m }, mon =>
                {
                    int cur = mon.Value(0x10), mx = mon.Max(0x10);
                    int high = Math.Max(cur, (int)Math.Round(mx * 0.4));
                    for (int i = 0; i < 3; i++)
                    {
                        mon.Port.Write(0x10, 0); Thread.Sleep(250);
                        mon.Port.Write(0x10, high); Thread.Sleep(250);
                    }
                    mon.WriteVerified(0x10, cur, true);
                }, () => Ui(next));
            };
            next();
        }

        void SetAllBrightness(int pct)
        {
            Monitor first = null;
            foreach (var m in monitors.Where(x => x.Supports(0x10)))
            {
                int v = (int)Math.Round(pct * m.Max(0x10) / 100.0);
                m.SetCached(0x10, v);
                m.Lane.Write(0x10, v, true, true);
                if (first == null) first = m;
            }
            if (first == null) osd.ShowLevel("Brightness", "No screens found", -1);
            else osd.ShowLevel("Brightness", pct + "%", pct / 100.0);
            AfterExternalChange();
        }

        void StepBrightness(int direction)
        {
            int step = Step();
            Monitor first = null;
            foreach (var m in monitors.Where(x => x.Supports(0x10)))
            {
                int mx = m.Max(0x10);
                int delta = Math.Max(1, (int)Math.Round(mx * step / 100.0));
                int v = Math.Max(0, Math.Min(mx, m.Value(0x10) + direction * delta));
                m.SetCached(0x10, v);
                m.Lane.Write(0x10, v, true, true);
                if (first == null) first = m;
            }
            if (first == null) { osd.ShowLevel("Brightness", scanning ? "Still looking…" : "No screens found", -1); return; }
            double frac = first.Value(0x10) / (double)first.Max(0x10);
            osd.ShowLevel("Brightness", (int)Math.Round(frac * 100) + "%", frac);
            AfterExternalChange();
        }

        void AfterExternalChange()
        {
            RefreshTiles();
            if (IsVisible) BuildAdjust();
        }

        int Step() { return Math.Max(1, Math.Min(50, settings.GetInt("step", 10))); }

        void ChangeStep(int delta)
        {
            int v = Math.Max(5, Math.Min(50, Step() + delta));
            settings.Set("step", v);
            settings.Save();
            stepValue.Text = v + "%";
        }

        // ---------- presets ----------
        // Stored as preset.<name>.<monitor key or *>.<feature>=value.
        // Sliders are stored in percent so presets work across different monitors.

        List<string> PresetNames()
        {
            return settings.Get("presets", "").Split('|').Where(n => n.Length > 0).ToList();
        }

        void SavePresetNames(List<string> names)
        {
            settings.Set("presets", string.Join("|", names));
            settings.Save();
            RefreshPresets();
            RegisterHotkeys();
        }

        void EnsureDefaultPresets()
        {
            if (settings.Has("presets")) return;
            var defaults = new[] { new { Name = "Bright", Pct = 70 }, new { Name = "Normal", Pct = 40 }, new { Name = "Dim", Pct = 10 }, new { Name = "Night", Pct = 0 } };
            foreach (var d in defaults) settings.Set("preset." + d.Name + ".*.brightness", d.Pct);
            settings.Set("presets", string.Join("|", defaults.Select(d => d.Name)));
            settings.Save();
        }

        void RefreshPresets()
        {
            var names = PresetNames();
            presetList.Children.Clear();
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                int index = i;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new TextBlock { Text = name, FontSize = 14, Foreground = Theme.Res("Text"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                if (i < 9)
                {
                    var keys = Theme.Keys("Ctrl", "Alt", (i + 1).ToString());
                    Grid.SetColumn(keys, 1);
                    grid.Children.Add(keys);
                }
                var row = new Button { Style = Theme.Style("Row"), Content = grid };
                AutomationName(row, "Apply preset " + name);
                row.Click += (s, e) => ApplyPreset(name);
                var menu = new ContextMenu();
                menu.Items.Add(MenuItem("Apply", () => ApplyPreset(name)));
                menu.Items.Add(MenuItem("Update with current settings", () => UpdatePreset(name)));
                menu.Items.Add(MenuItem("Rename…", () => RenamePreset(name)));
                menu.Items.Add(MenuItem("Move up", () => MovePreset(index, -1)));
                menu.Items.Add(MenuItem("Move down", () => MovePreset(index, 1)));
                menu.Items.Add(MenuItem("Delete", () => DeletePreset(name)));
                row.ContextMenu = menu;
                if (i > 0) presetList.Children.Add(new Rectangle { Height = 1, Fill = Theme.Res("Border"), Margin = new Thickness(10, 0, 10, 0) });
                presetList.Children.Add(row);
            }
            if (names.Count == 0)
                presetList.Children.Add(new TextBlock { Text = "Set your screens the way you like, then save them here.", Foreground = Theme.Res("Muted"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6) });
            else
                presetList.Children.Add(new TextBlock { Text = "Right-click a preset to update, rename or reorder it.", Foreground = Theme.Res("Faint"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 10, 10, 0) });
            if (tray != null) tray.SetPresets(names, ApplyPreset);
        }

        void ApplyPreset(string name)
        {
            if (monitors.Count == 0) { osd.ShowLevel("Preset", "No screens found", -1); return; }
            string p = "preset." + name + ".";
            foreach (var f in Feature.All.Where(x => x.InPresets))
                foreach (var m in monitors.Where(x => x.Supports(f.Code)))
                {
                    string s = settings.Get(p + m.Key + "." + f.Key, null) ?? settings.Get(p + "*." + f.Key, null);
                    int stored;
                    if (s == null || !int.TryParse(s, out stored)) continue;
                    int mx = m.Max(f.Code);
                    int v = Math.Max(0, Math.Min(mx, f.Kind == FeatureKind.Slider ? (int)Math.Round(stored * mx / 100.0) : stored));
                    if (f.Kind != FeatureKind.Slider && !m.Choices(f).Any(c => c.Key == v)) continue;
                    m.SetCached(f.Code, v);
                    m.Lane.Write(f.Code, v, m.CanVerify(f), false);
                }
            osd.ShowLevel("Preset", name, -1);
            SetStatus("Applying " + name + "…");
            AfterExternalChange();
            Lane.ForEach(monitors, null, () => Ui(() => { AfterExternalChange(); SetStatus("Applied " + name + "."); }));
        }

        void StorePreset(string name)
        {
            string p = "preset." + name + ".";
            settings.RemovePrefix(p);
            foreach (var m in monitors)
                foreach (var f in Feature.All.Where(x => x.InPresets && m.Supports(x.Code)))
                {
                    int v = m.Value(f.Code), mx = m.Max(f.Code);
                    int stored = f.Kind == FeatureKind.Slider ? (int)Math.Round(v * 100.0 / mx) : v;
                    settings.Set(p + m.Key + "." + f.Key, stored);
                    if (m == monitors[0]) settings.Set(p + "*." + f.Key, stored);
                }
        }

        // Re-reads the screens first so the preset matches what's really showing,
        // including changes made with the monitors' own buttons.
        void SnapshotInto(string name, bool isNew)
        {
            var list = monitors;
            SetStatus("Reading the current settings from your screens…");
            Lane.ForEach(list, m => m.Refresh(), () =>
            {
                Ui(() =>
                {
                    StorePreset(name);
                    var names = PresetNames();
                    if (isNew && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                    SavePresetNames(names);
                    AfterExternalChange();
                    SetStatus("Saved " + name + ".");
                });
            });
        }

        static string CleanName(string name)
        {
            return name == null ? null : new string(name.Where(c => c != '|' && c != '.' && c != '=').ToArray()).Trim();
        }

        void SaveNewPreset()
        {
            if (monitors.Count == 0) { SetStatus("Connect a screen first, then save a preset."); return; }
            string name = CleanName(Dialog.Show(this, "Save as preset", "Saves the current settings of every screen.", "Evening", "Save preset"));
            if (string.IsNullOrEmpty(name)) return;
            if (PresetNames().Contains(name, StringComparer.OrdinalIgnoreCase) &&
                Dialog.Show(this, "Replace " + name + "?", "A preset with this name already exists.", null, "Replace") == null)
                return;
            SnapshotInto(name, true);
        }

        void UpdatePreset(string name)
        {
            if (monitors.Count == 0) return;
            if (Dialog.Show(this, "Update " + name + "?", "Replaces it with the current settings of every screen.", null, "Update") == null) return;
            SnapshotInto(name, false);
        }

        void RenamePreset(string name)
        {
            string newName = CleanName(Dialog.Show(this, "Rename preset", null, name, "Rename"));
            if (string.IsNullOrEmpty(newName) || newName == name) return;
            var names = PresetNames();
            if (names.Contains(newName, StringComparer.OrdinalIgnoreCase)) { SetStatus("There's already a preset called " + newName + "."); return; }
            string from = "preset." + name + ".", to = "preset." + newName + ".";
            foreach (var k in settings.KeysWithPrefix(from)) settings.Set(to + k.Substring(from.Length), settings.Get(k, ""));
            settings.RemovePrefix(from);
            names[names.IndexOf(name)] = newName;
            SavePresetNames(names);
        }

        void DeletePreset(string name)
        {
            if (Dialog.Show(this, "Delete " + name + "?", null, null, "Delete") == null) return;
            settings.RemovePrefix("preset." + name + ".");
            var names = PresetNames();
            names.Remove(name);
            SavePresetNames(names);
        }

        void MovePreset(int i, int delta)
        {
            var names = PresetNames();
            int j = i + delta;
            if (i < 0 || j < 0 || j >= names.Count) return;
            var tmp = names[i]; names[i] = names[j]; names[j] = tmp;
            SavePresetNames(names);
        }

        // ---------- hotkeys ----------

        void UnregisterHotkeys()
        {
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, HK_BRIGHTER);
            UnregisterHotKey(hwnd, HK_DIMMER);
            for (int i = 0; i < 9; i++) UnregisterHotKey(hwnd, HK_PRESET + i);
        }

        void RegisterHotkeys()
        {
            if (hwnd == IntPtr.Zero || snapshotPath != null) return;
            UnregisterHotkeys();
            var taken = new List<string>();
            if (!RegisterHotKey(hwnd, HK_BRIGHTER, MOD_CONTROL | MOD_ALT, VK_PRIOR)) taken.Add("Ctrl+Alt+PgUp");
            if (!RegisterHotKey(hwnd, HK_DIMMER, MOD_CONTROL | MOD_ALT, VK_NEXT)) taken.Add("Ctrl+Alt+PgDn");
            int count = Math.Min(9, PresetNames().Count);
            for (int i = 0; i < count; i++)
                if (!RegisterHotKey(hwnd, HK_PRESET + i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)('1' + i)))
                    taken.Add("Ctrl+Alt+" + (i + 1));
            if (taken.Count > 0)
            {
                Log.Write("Hotkeys in use by another app: " + string.Join(", ", taken));
                SetStatus("Another app is already using " + string.Join(", ", taken) + ".");
            }
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            handled = true;
            int id = wParam.ToInt32();
            if (id == HK_BRIGHTER) StepBrightness(1);
            else if (id == HK_DIMMER) StepBrightness(-1);
            else if (id >= HK_PRESET && id < HK_PRESET + 9)
            {
                var names = PresetNames();
                if (id - HK_PRESET < names.Count) ApplyPreset(names[id - HK_PRESET]);
            }
            return IntPtr.Zero;
        }

        // ---------- start with Windows ----------

        static bool StartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" /tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Startup setting: " + ex.Message);
                SetStatus("Couldn't change the start-with-Windows setting.");
            }
        }

        static int Scale(int v, int from, int to) { return from == to || from == 0 ? v : (int)Math.Round(v * (double)to / from); }
    }
}
