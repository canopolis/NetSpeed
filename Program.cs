using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeed
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "NetSpeed_SingleInstance_Canopolis", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    static class Settings
    {
        const string Key = @"Software\NetSpeed";
        public static string Get(string name, string def)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return (k?.GetValue(name) as string) ?? def;
        }
        public static void Set(string name, string value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
                k.SetValue(name, value);
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "NetSpeed";
        public static bool Enabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(Name) != null; }
        }
        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
            }
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem header, startItem, floatItem, unitsItem, adapterItem;
        readonly FloatBox box;
        readonly Stopwatch clock = Stopwatch.StartNew();
        Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
        double lastT;
        Icon current;
        string units, adapter;
        string lastIconText = "";

        public TrayApp()
        {
            units = Settings.Get("Units", "Mbps");
            adapter = Settings.Get("Adapter", "");
            if (Startup.Enabled) Startup.Set(true); // keep path correct if the exe was moved

            header = new ToolStripMenuItem("Measuring…") { Enabled = false };
            startItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Startup.Enabled };
            startItem.CheckedChanged += (s, e) => Startup.Set(startItem.Checked);
            floatItem = new ToolStripMenuItem("Show floating box") { CheckOnClick = true, Checked = Settings.Get("Float", "0") == "1" };
            floatItem.CheckedChanged += (s, e) => { Settings.Set("Float", floatItem.Checked ? "1" : "0"); ShowBox(); };
            unitsItem = new ToolStripMenuItem("Units");
            foreach (var u in new[] { "Mbps", "MB/s" })
            {
                var it = new ToolStripMenuItem(u) { Checked = u == units };
                it.Click += (s, e) => { units = u; Settings.Set("Units", u); foreach (ToolStripMenuItem x in unitsItem.DropDownItems) x.Checked = x.Text == u; };
                unitsItem.DropDownItems.Add(it);
            }
            adapterItem = new ToolStripMenuItem("Network adapter");
            adapterItem.DropDownItems.Add("…");
            adapterItem.DropDownOpening += (s, e) => BuildAdapterMenu();
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => Quit();

            menu.Items.AddRange(new ToolStripItem[] { header, new ToolStripSeparator(), startItem, floatItem, unitsItem, adapterItem, new ToolStripSeparator(), exit });
            menu.Opening += (s, e) => startItem.Checked = Startup.Enabled;

            tray.ContextMenuStrip = menu;
            tray.Text = "NetSpeed";
            tray.Icon = LoadAppIcon();
            tray.Visible = true;
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                    mi?.Invoke(tray, null);
                }
            };

            box = new FloatBox(menu);
            ShowBox();

            Sample(true);
            timer.Tick += (s, e) => Sample(false);
            timer.Start();

            if (Settings.Get("FirstRun", "1") == "1")
            {
                Settings.Set("FirstRun", "0");
                tray.ShowBalloonTip(10000, "NetSpeed is running",
                    "Your live speed shows on this icon. If you can't see it, click the ^ arrow by the clock and drag the icon onto the taskbar.",
                    ToolTipIcon.Info);
            }
        }

        static Icon LoadAppIcon()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("netspeed.ico"))
                return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
        }

        void ShowBox()
        {
            if (floatItem.Checked) { if (!box.Visible) box.Show(); }
            else if (box.Visible) box.Hide();
        }

        static bool Countable(NetworkInterface n)
        {
            if (n.OperationalStatus != OperationalStatus.Up) return false;
            if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;
            string d = (n.Description + " " + n.Name).ToLowerInvariant();
            return !(d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("virtual") || d.Contains("vmware") || d.Contains("pseudo") || d.Contains("loopback"));
        }

        IEnumerable<NetworkInterface> Selected()
        {
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return Enumerable.Empty<NetworkInterface>(); }
            if (adapter != "")
            {
                var one = all.Where(n => n.Id == adapter).ToArray();
                if (one.Length > 0) return one;
            }
            return all.Where(Countable);
        }

        void BuildAdapterMenu()
        {
            adapterItem.DropDownItems.Clear();
            var allItem = new ToolStripMenuItem("All active adapters") { Checked = adapter == "" };
            allItem.Click += (s, e) => SetAdapter("");
            adapterItem.DropDownItems.Add(allItem);
            adapterItem.DropDownItems.Add(new ToolStripSeparator());
            NetworkInterface[] list;
            try { list = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray(); }
            catch { list = new NetworkInterface[0]; }
            foreach (var n in list)
            {
                string id = n.Id;
                var it = new ToolStripMenuItem(n.Name + "  (" + n.Description + ")") { Checked = adapter == id };
                it.Click += (s, e) => SetAdapter(id);
                adapterItem.DropDownItems.Add(it);
            }
        }

        void SetAdapter(string id)
        {
            adapter = id;
            Settings.Set("Adapter", id);
            prev.Clear();
            Sample(true);
        }

        void Sample(bool baselineOnly)
        {
            double t = clock.Elapsed.TotalSeconds;
            double secs = Math.Max(0.2, t - lastT);
            lastT = t;
            long drx = 0, dtx = 0;
            var cur = new Dictionary<string, long[]>();
            foreach (var n in Selected())
            {
                long rx, tx;
                try { var st = n.GetIPStatistics(); rx = st.BytesReceived; tx = st.BytesSent; }
                catch { continue; }
                cur[n.Id] = new[] { rx, tx };
                long[] p;
                if (prev.TryGetValue(n.Id, out p))
                {
                    drx += Math.Max(0, rx - p[0]);
                    dtx += Math.Max(0, tx - p[1]);
                }
            }
            prev = cur;
            if (baselineOnly) return;
            Show(drx / secs, dtx / secs);
        }

        double Conv(double bytesPerSec) => units == "MB/s" ? bytesPerSec / 1048576.0 : bytesPerSec * 8 / 1e6;

        static string Full(double v) => v < 10 ? v.ToString("0.00") : v < 100 ? v.ToString("0.0") : v.ToString("0");

        static string Short(double v)
        {
            if (v < 0.05) return "0";
            if (v < 10) return v.ToString("0.0");
            if (v < 1000) return Math.Round(v).ToString("0");
            return (v / 1000).ToString(v < 10000 ? "0.0" : "0") + "k";
        }

        void Show(double down, double up)
        {
            double d = Conv(down), u = Conv(up);
            string text = "↓ " + Full(d) + "  ↑ " + Full(u) + " " + units;
            header.Text = text;
            string tip = "Download " + Full(d) + " " + units + "\nUpload " + Full(u) + " " + units;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            box.SetValues("↓ " + Full(d) + " " + units, "↑ " + Full(u) + " " + units);

            string a = Short(d), b = Short(u);
            if (a + "|" + b == lastIconText) return;
            lastIconText = a + "|" + b;
            var old = current;
            current = MakeIcon(a, b);
            tray.Icon = current;
            old?.Dispose();
        }

        static FontFamily pickedFamily;
        static FontFamily Family()
        {
            if (pickedFamily != null) return pickedFamily;
            foreach (var name in new[] { "Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI" })
            {
                try { var f = new FontFamily(name); pickedFamily = f; return f; } catch { }
            }
            pickedFamily = FontFamily.GenericSansSerif;
            return pickedFamily;
        }

        static Icon MakeIcon(string top, string bottom)
        {
            int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(s, s))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var bg = new SolidBrush(Color.FromArgb(235, 18, 24, 38)))
                using (var path = Rounded(new RectangleF(0, 0, s, s), s * 0.18f))
                    g.FillPath(bg, path);
                DrawFit(g, top, Color.FromArgb(105, 240, 174), new RectangleF(0, 0, s, s / 2f), s);
                DrawFit(g, bottom, Color.FromArgb(140, 185, 255), new RectangleF(0, s / 2f, s, s / 2f), s);
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        static void DrawFit(Graphics g, string text, Color c, RectangleF r, int s)
        {
            float size = r.Height * 1.05f;
            Font f = null;
            SizeF m = SizeF.Empty;
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                for (; size > 5; size -= 0.5f)
                {
                    f?.Dispose();
                    f = new Font(Family(), size, FontStyle.Bold, GraphicsUnit.Pixel);
                    m = g.MeasureString(text, f, 1000, fmt);
                    if (m.Width <= s - 1 && m.Height <= r.Height + 2) break;
                }
                using (var br = new SolidBrush(c))
                    g.DrawString(text, f, br, r.X + (s - m.Width) / 2f, r.Y + (r.Height - m.Height) / 2f, fmt);
            }
            f?.Dispose();
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Quit()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            box.Close();
            ExitThread();
        }
    }

    class FloatBox : Form
    {
        string line1 = "↓ …", line2 = "↑ …";
        readonly Font font = new Font("Segoe UI", 9f, FontStyle.Bold);

        public FloatBox(ContextMenuStrip menu)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(22, 26, 36);
            Opacity = 0.92;
            DoubleBuffered = true;
            ContextMenuStrip = menu;
            AutoScaleMode = AutoScaleMode.None;

            using (var g = CreateGraphics())
            {
                var m = g.MeasureString("↑ 999.9 MB/s", font);
                ClientSize = new Size((int)m.Width + 14, (int)(m.Height * 2) + 8);
            }

            Point p;
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (TryParse(Settings.Get("BoxPos", ""), out p) && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(p)))
                Location = p;
            else
                Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 8);

            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, 0xA1, 2, 0); // drag the window
                    Settings.Set("BoxPos", Location.X + "," + Location.Y);
                }
            };
        }

        static bool TryParse(string s, out Point p)
        {
            p = Point.Empty;
            var parts = s.Split(',');
            int x, y;
            if (parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y)) { p = new Point(x, y); return true; }
            return false;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } // tool window, no activate
        }

        protected override bool ShowWithoutActivation => true;

        public void SetValues(string a, string b)
        {
            line1 = a; line2 = b;
            if (Visible) { TopMost = true; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float h = ClientSize.Height / 2f;
            using (var g1 = new SolidBrush(Color.FromArgb(105, 240, 174)))
            using (var g2 = new SolidBrush(Color.FromArgb(140, 185, 255)))
            {
                e.Graphics.DrawString(line1, font, g1, 6, 3);
                e.Graphics.DrawString(line2, font, g2, 6, h);
            }
        }
    }
}
