using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
                // Never let an error close the program: log it instead.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write(e.ExceptionObject as Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    static class Log
    {
        public static void Write(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetSpeed");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
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

    class Result
    {
        public bool Ok;
        public double DownMbps, UpMbps, PingMs, JitterMs;
    }

    // Same method as realtimespeed.com: small 100 KB - 1 MB transfers to the nearest
    // Cloudflare edge node, a new cycle every 3-5 seconds.
    class Meter
    {
        const string Base = "https://speed.cloudflare.com";
        const int MinB = 100000, MaxB = 1000000;
        readonly HttpClient http;
        readonly Random rnd = new Random();
        int dlBytes = 250000, ulBytes = 200000;
        double lastPing = -1;

        public Meter()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetSpeed/2.0");
            http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        }

        string R() => rnd.Next().ToString("x");

        double TimedGet(string url, out long bytes)
        {
            var sw = Stopwatch.StartNew();
            var data = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            sw.Stop();
            bytes = data.LongLength;
            return sw.Elapsed.TotalMilliseconds;
        }

        static int Adapt(int b, double ms) => Math.Min(MaxB, Math.Max(MinB, (int)(b * (600.0 / Math.Max(ms, 1)))));

        public Result Measure()
        {
            var r = new Result();
            try
            {
                long dummy;
                double best = double.MaxValue;
                for (int i = 0; i < 3; i++)
                    best = Math.Min(best, TimedGet(Base + "/__down?bytes=0&r=" + R(), out dummy));
                r.PingMs = best;
                r.JitterMs = lastPing < 0 ? 0 : Math.Abs(best - lastPing);
                lastPing = best;

                long got;
                double dms = TimedGet(Base + "/__down?bytes=" + (dlBytes + rnd.Next(999)) + "&r=" + R(), out got);
                r.DownMbps = got * 8 / (dms / 1000) / 1e6;
                dlBytes = Adapt(dlBytes, dms);

                var body = new byte[ulBytes];
                var sw = Stopwatch.StartNew();
                using (var content = new ByteArrayContent(body))
                using (var resp = http.PostAsync(Base + "/__up?r=" + R(), content).GetAwaiter().GetResult())
                    resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                sw.Stop();
                double ums = sw.Elapsed.TotalMilliseconds;
                r.UpMbps = ulBytes * 8 / (ums / 1000) / 1e6;
                ulBytes = Adapt(ulBytes, ums);
                r.Ok = true;
            }
            catch { r.Ok = false; lastPing = -1; }
            return r;
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem header, header2, startItem, floatItem, unitsItem;
        readonly FloatBox box;
        readonly Meter meter = new Meter();
        readonly Random rnd = new Random();
        volatile bool running = true;
        Icon current;
        string units;
        string lastIconText = "";
        Result pendingIcon;

        public TrayApp()
        {
            units = Settings.Get("Units", "Mbps");
            if (Startup.Enabled) Startup.Set(true); // keep path correct if the exe was moved

            header = new ToolStripMenuItem("Measuring…") { Enabled = false };
            header2 = new ToolStripMenuItem("") { Enabled = false };
            startItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Startup.Enabled };
            startItem.CheckedChanged += (s, e) => Startup.Set(startItem.Checked);
            floatItem = new ToolStripMenuItem("Show floating box") { CheckOnClick = true, Checked = Settings.Get("Float", "0") == "1" };
            floatItem.CheckedChanged += (s, e) => { Settings.Set("Float", floatItem.Checked ? "1" : "0"); ShowBox(); };
            unitsItem = new ToolStripMenuItem("Units");
            foreach (var u in new[] { "Mbps", "MB/s" })
            {
                var it = new ToolStripMenuItem(u) { Checked = u == units };
                it.Click += (s, e) =>
                {
                    units = u; Settings.Set("Units", u);
                    foreach (ToolStripMenuItem x in unitsItem.DropDownItems) x.Checked = x.Text == u;
                    lastIconText = "";
                };
                unitsItem.DropDownItems.Add(it);
            }
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => Quit();

            menu.Items.AddRange(new ToolStripItem[] { header, header2, new ToolStripSeparator(), startItem, floatItem, unitsItem, new ToolStripSeparator(), exit });
            menu.Opening += (s, e) => startItem.Checked = Startup.Enabled;
            menu.Closed += (s, e) => { if (pendingIcon != null) { var p = pendingIcon; pendingIcon = null; UpdateIcon(p); } };

            tray.ContextMenuStrip = menu;   // right click opens it (built-in)
            tray.Text = "NetSpeed - measuring…";
            tray.Icon = LoadAppIcon();
            tray.Visible = true;
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                try
                {
                    var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                    mi?.Invoke(tray, null);
                }
                catch (Exception ex) { Log.Write(ex); }
            };

            box = new FloatBox(menu);
            var forceHandle = box.Handle; // needed to receive results from the measuring thread
            ShowBox();

            if (Settings.Get("FirstRun", "1") == "1")
            {
                Settings.Set("FirstRun", "0");
                tray.ShowBalloonTip(10000, "NetSpeed is running",
                    "Your live speed shows on this icon. If you can't see it, click the ^ arrow by the clock and drag the icon onto the taskbar.",
                    ToolTipIcon.Info);
            }

            var t = new Thread(Loop) { IsBackground = true, Name = "NetSpeed meter" };
            t.Start();
        }

        void Loop()
        {
            while (running)
            {
                var sw = Stopwatch.StartNew();
                Result r;
                try { r = meter.Measure(); } catch (Exception ex) { Log.Write(ex); r = new Result(); }
                if (!running) break;
                try { box.BeginInvoke((Action)(() => ShowResult(r))); } catch { }
                int wait = 3000 + rnd.Next(2000) - (int)sw.ElapsedMilliseconds; // new cycle every 3-5 s
                if (wait > 0) Thread.Sleep(wait);
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

        double Conv(double mbps) => units == "MB/s" ? mbps * 1e6 / 8 / 1048576.0 : mbps;

        static string Full(double v) => v < 10 ? v.ToString("0.00") : v < 100 ? v.ToString("0.0") : v.ToString("0");

        static string Short(double v)
        {
            if (v < 0.05) return "0";
            if (v < 10) return v.ToString("0.0");
            if (v < 1000) return Math.Round(v).ToString("0");
            return (v / 1000).ToString(v < 10000 ? "0.0" : "0") + "k";
        }

        void ShowResult(Result r)
        {
            if (!running) return;
            if (!r.Ok)
            {
                header.Text = "Offline - no connection";
                header2.Text = "";
                tray.Text = "NetSpeed - offline";
                box.SetValues("Offline", "", "", true);
            }
            else
            {
                double d = Conv(r.DownMbps), u = Conv(r.UpMbps);
                header.Text = "↓ " + Full(d) + "   ↑ " + Full(u) + " " + units;
                header2.Text = "Ping " + r.PingMs.ToString("0") + " ms   Jitter " + r.JitterMs.ToString("0") + " ms";
                string tip = "↓ " + Full(d) + " ↑ " + Full(u) + " " + units + "\nPing " + r.PingMs.ToString("0") + " ms  Jitter " + r.JitterMs.ToString("0") + " ms";
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                box.SetValues("↓ " + Full(d) + " " + units, "↑ " + Full(u) + " " + units,
                              "Ping " + r.PingMs.ToString("0") + " ms · Jitter " + r.JitterMs.ToString("0"), false);
            }
            // Don't touch the tray icon while the menu is open, so the menu never closes by itself.
            if (menu.Visible) { pendingIcon = r; return; }
            UpdateIcon(r);
        }

        void UpdateIcon(Result r)
        {
            string a = r.Ok ? Short(Conv(r.DownMbps)) : "--";
            string b = r.Ok ? Short(Conv(r.UpMbps)) : "--";
            if (a + "|" + b + "|" + r.Ok == lastIconText) return;
            lastIconText = a + "|" + b + "|" + r.Ok;
            var old = current;
            current = MakeIcon(a, b, r.Ok);
            tray.Icon = current;
            old?.Dispose();
        }

        static FontFamily pickedFamily;
        static FontFamily Family()
        {
            if (pickedFamily != null) return pickedFamily;
            foreach (var name in new[] { "Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI" })
            {
                try { pickedFamily = new FontFamily(name); return pickedFamily; } catch { }
            }
            pickedFamily = FontFamily.GenericSansSerif;
            return pickedFamily;
        }

        static Icon MakeIcon(string top, string bottom, bool ok)
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
                var c1 = ok ? Color.FromArgb(105, 240, 174) : Color.FromArgb(255, 82, 82);
                var c2 = ok ? Color.FromArgb(140, 185, 255) : Color.FromArgb(255, 82, 82);
                DrawFit(g, top, c1, new RectangleF(0, 0, s, s / 2f), s);
                DrawFit(g, bottom, c2, new RectangleF(0, s / 2f, s, s / 2f), s);
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
            running = false;
            tray.Visible = false;
            tray.Dispose();
            box.Close();
            ExitThread();
        }
    }

    class FloatBox : Form
    {
        string line1 = "↓ …", line2 = "↑ …", line3 = "";
        bool offline;
        readonly Font font = new Font("Segoe UI", 9f, FontStyle.Bold);
        readonly Font small = new Font("Segoe UI", 8f, FontStyle.Regular);

        public FloatBox(ContextMenuStrip menu)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;               // set once only
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(22, 26, 36);
            Opacity = 0.92;
            DoubleBuffered = true;
            ContextMenuStrip = menu;
            AutoScaleMode = AutoScaleMode.None;

            using (var g = CreateGraphics())
            {
                var m = g.MeasureString("Ping 999 ms · Jitter 999", font);
                var h = g.MeasureString("Ag", font).Height;
                var h3 = g.MeasureString("Ag", small).Height;
                ClientSize = new Size((int)m.Width + 14, (int)(h * 2 + h3) + 8);
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
                    Native.SendMessage(Handle, 0xA1, 2, 0); // drag the box
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
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } // tool window, never takes focus
        }

        protected override bool ShowWithoutActivation => true;

        public void SetValues(string a, string b, string c, bool isOffline)
        {
            line1 = a; line2 = b; line3 = c; offline = isOffline;
            if (Visible) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float lh = g.MeasureString("Ag", font).Height;
            using (var red = new SolidBrush(Color.FromArgb(255, 82, 82)))
            using (var g1 = new SolidBrush(Color.FromArgb(105, 240, 174)))
            using (var g2 = new SolidBrush(Color.FromArgb(140, 185, 255)))
            using (var g3 = new SolidBrush(Color.FromArgb(255, 204, 128)))
            {
                if (offline) { g.DrawString(line1, font, red, 6, 4); return; }
                g.DrawString(line1, font, g1, 6, 3);
                g.DrawString(line2, font, g2, 6, 3 + lh);
                g.DrawString(line3, small, g3, 6, 4 + lh * 2);
            }
        }
    }
}
