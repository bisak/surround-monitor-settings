using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MonitorControl
{
    static class Theme
    {
        // Screenshot mode for docs: hides monitor serial numbers.
        public static bool HideSerials;

        public static Brush Res(string key) { return (Brush)Application.Current.Resources[key]; }
        public static FontFamily Font(string key) { return (FontFamily)Application.Current.Resources[key]; }
        public static Style Style(string key) { return (Style)Application.Current.Resources[key]; }

        public static TextBlock Text(string text, double size, string brush)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Res(brush), FontFamily = Font("Sans"), VerticalAlignment = VerticalAlignment.Center };
        }

        public static TextBlock Serif(string text, double size)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Res("Ink"), FontFamily = Font(size >= 30 ? "Serif" : "SerifSmall") };
        }

        // A physical-looking key cap for shortcuts.
        public static Border Kbd(string key)
        {
            return new Border
            {
                Background = Res("Canvas"),
                BorderBrush = Res("Border"),
                BorderThickness = new Thickness(1, 1, 1, 2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key, FontFamily = Font("Mono"), FontSize = 11, Foreground = Res("Text") }
            };
        }

        public static StackPanel Keys(params string[] keys)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var k in keys) p.Children.Add(Kbd(k));
            return p;
        }

    }

    // One physical screen: a panel whose shade follows the monitor's real brightness.
    sealed class ScreenTile : StackPanel
    {
        public readonly Monitor Monitor;
        readonly Border ring, panel;
        readonly TextBlock value, name, meta;

        public ScreenTile(Monitor monitor)
        {
            Monitor = monitor;
            Margin = new Thickness(10, 0, 10, 0);
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;

            ring = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Padding = new Thickness(4), BorderBrush = Brushes.Transparent };
            panel = new Border { Width = 204, Height = 115, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)) };
            value = new TextBlock { FontFamily = Theme.Font("Serif"), FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Child = value;
            ring.Child = panel;
            var stand = new Border { Width = 46, Height = 6, CornerRadius = new CornerRadius(0, 0, 3, 3), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), Margin = new Thickness(0, -2, 0, 0) };

            var row = new Grid { Margin = new Thickness(6, 10, 6, 0) };
            name = new TextBlock { FontSize = 14, FontFamily = Theme.Font("Sans") };
            meta = new TextBlock { FontSize = 11, FontFamily = Theme.Font("Mono"), Foreground = Theme.Res("Faint"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            row.Children.Add(meta);
            Children.Add(ring);
            Children.Add(stand);
            Children.Add(row);
        }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        public void Update(bool selected)
        {
            int max = Monitor.Max(0x10);
            double b = max > 0 ? Monitor.Value(0x10) / (double)max : 0.6;
            // Near-black when dim, soft white when bright.
            var fill = Mix(Color.FromRgb(0x26, 0x26, 0x26), Color.FromRgb(0xF2, 0xF1, 0xEC), 0.08 + 0.92 * b);
            panel.Background = new SolidColorBrush(fill);
            value.Text = Monitor.Supports(0x10) ? Math.Round(b * 100) + "%" : "";
            value.Foreground = new SolidColorBrush(b > 0.45 ? Color.FromRgb(0x19, 0x19, 0x19) : Color.FromRgb(0xF2, 0xF2, 0xF0));
            ring.BorderBrush = selected ? Theme.Res("Ink") : Brushes.Transparent;
            name.Text = Monitor.Name;
            name.Foreground = Theme.Res(selected ? "Ink" : "Muted");
            name.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            string serial = Monitor.Port.Serial;
            meta.Text = serial.Length > 0 && !Theme.HideSerials ? "SN " + serial.Substring(Math.Max(0, serial.Length - 4)) : "";
        }
    }

    // Hotkey feedback near the bottom of the screen. Never takes focus or clicks.
    sealed class Osd : Window
    {
        readonly TextBlock label, big;
        readonly Border bar, fill;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };

        public Osd()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -10000;

            label = Theme.Text("", 13, "Muted");
            big = Theme.Serif("", 34);
            fill = new Border { Background = Theme.Res("Ink"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            bar = new Border { Width = 220, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Res("TrackBg"), Margin = new Thickness(0, 10, 0, 0), Child = fill };
            var stack = new StackPanel { MinWidth = 220 };
            stack.Children.Add(label);
            stack.Children.Add(big);
            stack.Children.Add(bar);
            Content = new Border
            {
                Background = Theme.Res("Card"),
                BorderBrush = Theme.Res("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 14, 22, 18),
                Margin = new Thickness(20),
                Effect = new DropShadowEffect { ShadowDepth = 4, BlurRadius = 24, Opacity = 0.45 },
                Child = stack
            };
            timer.Tick += (s, e) => { timer.Stop(); Hide(); };
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000 | 0x20);   // TOOLWINDOW | NOACTIVATE | TRANSPARENT
            };
        }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);

        public void ShowLevel(string title, string text, double fraction)
        {
            label.Text = title;
            big.Text = text;
            bar.Visibility = fraction < 0 ? Visibility.Collapsed : Visibility.Visible;
            fill.Width = Math.Max(4, 220 * Math.Max(0, Math.Min(1, fraction)));
            if (!IsVisible) Show();
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Bottom - ActualHeight - 60;
            Topmost = true;
            timer.Stop();
            timer.Start();
        }
    }

    static class Dialog
    {
        // Returns null when cancelled. For a confirmation (input == null) returns "" on OK.
        public static string Show(Window owner, string title, string message, string input, string okText)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
            };
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var stack = new StackPanel { Width = 380 };
            stack.Children.Add(Theme.Serif(title, 24));
            if (!string.IsNullOrEmpty(message))
                stack.Children.Add(new TextBlock { Text = message, FontFamily = Theme.Font("Sans"), FontSize = 14, Foreground = Theme.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 22 });
            TextBox box = null;
            if (input != null)
            {
                box = new TextBox { Text = input, Style = Theme.Style("Field"), Margin = new Thickness(0, 16, 0, 0), MaxLength = 40 };
                stack.Children.Add(box);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            var cancel = new Button { Content = "Cancel", Style = Theme.Style("Ghost"), IsCancel = true, MinWidth = 84 };
            var ok = new Button { Content = okText, Style = Theme.Style("Primary"), IsDefault = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (s, e) => { w.DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);

            var card = new Border
            {
                Background = Theme.Res("Card"),
                BorderBrush = Theme.Res("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(28, 24, 28, 24),
                Margin = new Thickness(24),
                Effect = new DropShadowEffect { ShadowDepth = 6, BlurRadius = 32, Opacity = 0.5 },
                Child = stack
            };
            card.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch (InvalidOperationException) { } };
            w.Content = card;
            w.Loaded += (s, e) =>
            {
                if (box != null) { box.Focus(); box.SelectAll(); } else ok.Focus();
            };
            if (w.ShowDialog() != true) return null;
            return box == null ? "" : box.Text.Trim();
        }
    }

    // System tray icon with a light menu matching the app.
    sealed class Tray : IDisposable
    {
        readonly Forms.NotifyIcon icon;
        readonly Forms.ToolStripMenuItem presets;
        static readonly Drawing.Font MenuFont = new Drawing.Font("Segoe UI", 9.5F);

        public Tray(Action open, Action toggle, Action<int> brightness, Action exit)
        {
            var menu = new Forms.ContextMenuStrip { Renderer = new PaperMenuRenderer(), ShowImageMargin = false, Font = MenuFont, Padding = new Forms.Padding(2, 4, 2, 4) };
            var openItem = Item("Open Monitor Control", (s, e) => open());
            openItem.Font = new Drawing.Font("Segoe UI Semibold", 9.5F);
            menu.Items.Add(openItem);
            presets = Item("Presets", null);
            menu.Items.Add(presets);
            var bright = Item("Brightness", null);
            foreach (int p in new[] { 0, 10, 25, 50, 75, 100 })
            {
                int pct = p;
                bright.DropDownItems.Add(Item(pct + "%", (s, e) => brightness(pct)));
            }
            Style(bright);
            menu.Items.Add(bright);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("Exit", (s, e) => exit()));

            icon = new Forms.NotifyIcon { Icon = Icons.TrayIcon(), Text = "Monitor Control", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) toggle(); };
        }

        static Forms.ToolStripMenuItem Item(string text, EventHandler click)
        {
            var item = new Forms.ToolStripMenuItem(text, null, click);
            item.Padding = new Forms.Padding(6, 5, 6, 5);
            return item;
        }

        static void Style(Forms.ToolStripMenuItem item)
        {
            var dd = item.DropDown as Forms.ToolStripDropDownMenu;
            if (dd != null) { dd.ShowImageMargin = false; dd.Renderer = new PaperMenuRenderer(); dd.Font = MenuFont; }
        }

        public void SetPresets(IList<string> names, Action<string> apply)
        {
            presets.DropDownItems.Clear();
            foreach (var n in names)
            {
                string name = n;
                presets.DropDownItems.Add(Item(name, (s, e) => apply(name)));
            }
            Style(presets);
            presets.Enabled = names.Count > 0;
        }

        public void Balloon(string title, string text)
        {
            icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }

    sealed class PaperMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public PaperMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Drawing.Color.FromArgb(214, 214, 211);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Drawing.Color.FromArgb(143, 143, 139);
            base.OnRenderArrow(e);
        }

        sealed class Colors : Forms.ProfessionalColorTable
        {
            static readonly Drawing.Color Bg = Drawing.Color.FromArgb(33, 33, 33);
            static readonly Drawing.Color Hi = Drawing.Color.FromArgb(48, 48, 48);
            static readonly Drawing.Color Line = Drawing.Color.FromArgb(54, 54, 54);
            public override Drawing.Color ToolStripDropDownBackground { get { return Bg; } }
            public override Drawing.Color ImageMarginGradientBegin { get { return Bg; } }
            public override Drawing.Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Drawing.Color ImageMarginGradientEnd { get { return Bg; } }
            public override Drawing.Color MenuBorder { get { return Line; } }
            public override Drawing.Color MenuItemBorder { get { return Hi; } }
            public override Drawing.Color MenuItemSelected { get { return Hi; } }
            public override Drawing.Color MenuItemSelectedGradientBegin { get { return Hi; } }
            public override Drawing.Color MenuItemSelectedGradientEnd { get { return Hi; } }
            public override Drawing.Color MenuItemPressedGradientBegin { get { return Hi; } }
            public override Drawing.Color MenuItemPressedGradientEnd { get { return Hi; } }
            public override Drawing.Color SeparatorDark { get { return Line; } }
            public override Drawing.Color SeparatorLight { get { return Bg; } }
        }
    }

    static class Icons
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        // A charcoal screen with a paper-white half: brightness, drawn as plainly as possible.
        public static Drawing.Bitmap Draw(int size)
        {
            var bmp = new Drawing.Bitmap(size, size);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                float s = size / 32f;
                using (var ink = new Drawing.SolidBrush(Drawing.Color.FromArgb(58, 58, 58)))
                using (var paper = new Drawing.SolidBrush(Drawing.Color.FromArgb(242, 241, 236)))
                using (var stand = new Drawing.SolidBrush(Drawing.Color.FromArgb(180, 178, 173)))
                {
                    g.FillRectangle(ink, 1 * s, 4 * s, 30 * s, 20 * s);
                    g.FillRectangle(paper, 3 * s, 6 * s, 13 * s, 16 * s);
                    g.FillRectangle(stand, 12 * s, 24 * s, 8 * s, 3 * s);
                    g.FillRectangle(stand, 8 * s, 27 * s, 16 * s, 2 * s);
                }
            }
            return bmp;
        }

        public static Drawing.Icon TrayIcon()
        {
            using (var bmp = Draw(32))
            {
                IntPtr h = bmp.GetHicon();
                try { return (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        public static ImageSource WindowIcon()
        {
            using (var bmp = Draw(64))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
    }
}
