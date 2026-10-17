using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        static readonly object gate = new object();
        public static string Get(string name, string def)
        {
            lock (gate)
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return (k?.GetValue(name) as string) ?? def;
        }
        public static void Set(string name, string value)
        {
            lock (gate)
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

    // Counts every byte NetSpeed itself sends/receives, per calendar month.
    static class DataUse
    {
        static readonly object gate = new object();
        static long bytes = -1;
        static string month = "";

        static void Load()
        {
            string m = DateTime.Now.ToString("yyyy-MM");
            if (bytes >= 0 && month == m) return;
            month = m;
            long b;
            bytes = Settings.Get("DataMonth", "") == m && long.TryParse(Settings.Get("DataBytes", "0"), out b) ? b : 0;
        }

        public static void Add(long b)
        {
            lock (gate)
            {
                Load();
                bytes += b;
                Settings.Set("DataMonth", month);
                Settings.Set("DataBytes", bytes.ToString());
            }
        }

        public static long ThisMonth
        {
            get { lock (gate) { Load(); return bytes; } }
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    }

    class PingState
    {
        public bool Online;
        public double PingMs, JitterMs;
    }

    class SpeedResult
    {
        public bool Ok;
        public double DownMbps, UpMbps;
        public long Bytes;
        public DateTime When;
    }

    class Network
    {
        const string Base = "https://speed.cloudflare.com";
        const string PingHost = "1.1.1.1";
        const int MinB = 200000, MaxB = 5000000;    // a single test never moves more than 5 MB each way
        const double TargetSeconds = 0.2;           // measured part of each transfer
        readonly HttpClient http;
        readonly Random rnd = new Random();
        readonly List<double> recent = new List<double>();
        bool icmpBlocked;
        int fails;
        public double LastPing = 30;
        public double KnownDown, KnownUp;          // last results, used to size the next test without a probe

        public Network()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetSpeed/3.2");
            http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        }

        string R() => rnd.Next().ToString("x");

        double IcmpPing()
        {
            using (var p = new Ping())
            {
                var reply = p.Send(PingHost, 2000);
                DataUse.Add(120);
                if (reply.Status != IPStatus.Success) throw new Exception("ping " + reply.Status);
                return Math.Max(1, reply.RoundtripTime);
            }
        }

        double HttpPing()
        {
            var sw = Stopwatch.StartNew();
            using (var resp = http.GetAsync(Base + "/__down?bytes=0&r=" + R()).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                sw.Stop();
            }
            DataUse.Add(1500);
            return sw.Elapsed.TotalMilliseconds;
        }

        public PingState PingOnce()
        {
            var st = new PingState();
            double ms = -1;
            if (!icmpBlocked)
            {
                try { ms = IcmpPing(); }
                catch
                {
                    // ICMP failed: check over the web. If the web works, ICMP is blocked on this network.
                    try { ms = HttpPing(); icmpBlocked = true; } catch { ms = -1; }
                }
            }
            else
            {
                try { ms = HttpPing(); } catch { ms = -1; }
            }

            if (ms < 0)
            {
                fails++;
                st.Online = fails < 2;            // one lost packet is not "offline"
                st.PingMs = LastPing;
                st.JitterMs = Jitter();
                return st;
            }
            fails = 0;
            LastPing = ms;
            recent.Add(ms);
            while (recent.Count > 10) recent.RemoveAt(0);
            st.Online = true;
            st.PingMs = ms;
            st.JitterMs = Jitter();
            return st;
        }

        double Jitter()
        {
            if (recent.Count < 2) return 0;
            double sum = 0;
            for (int i = 1; i < recent.Count; i++) sum += Math.Abs(recent[i] - recent[i - 1]);
            return sum / (recent.Count - 1);
        }

        // Download: the clock starts when the first data arrives, so ping does not lower the result.
        double Download(int size, out long moved)
        {
            moved = 0;
            using (var resp = http.GetAsync(Base + "/__down?bytes=" + size + "&r=" + R(), HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                {
                    var buf = new byte[65536];
                    int n;
                    long after = 0;
                    Stopwatch sw = null;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        moved += n;
                        if (sw == null) sw = Stopwatch.StartNew();
                        else after += n;
                    }
                    DataUse.Add(moved);
                    double sec = sw == null ? 0 : sw.Elapsed.TotalSeconds;
                    if (after <= 0 || sec <= 0.001) return -1;
                    return after * 8 / sec / 1e6;
                }
            }
        }

        // Upload: total time minus the measured ping (the server's first byte can't be seen from the PC).
        double Upload(int size)
        {
            var body = new byte[size];
            var sw = Stopwatch.StartNew();
            using (var content = new ByteArrayContent(body))
            using (var resp = http.PostAsync(Base + "/__up?r=" + R(), content).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            sw.Stop();
            DataUse.Add(size);
            double ms = sw.Elapsed.TotalMilliseconds;
            double eff = Math.Max(ms - LastPing, ms * 0.5);
            return size * 8 / (eff / 1000) / 1e6;
        }

        static int SizeFor(double mbps)
        {
            if (mbps <= 0) return 1000000;
            double b = mbps * 1e6 / 8 * TargetSeconds;
            return (int)Math.Min(MaxB, Math.Max(MinB, b));
        }

        public SpeedResult SpeedTest()
        {
            var r = new SpeedResult { When = DateTime.Now };
            long before = DataUse.ThisMonth;
            try
            {
                long moved;
                // Size each test from the last result; a small probe is only needed the very first time.
                double downEst = KnownDown > 0 ? KnownDown : Download(MinB, out moved);
                double down = Download(SizeFor(downEst), out moved);
                if (down <= 0) down = downEst;
                double upEst = KnownUp > 0 ? KnownUp : Upload(MinB);
                double up = Upload(SizeFor(upEst));
                if (down > 0) KnownDown = down;
                if (up > 0) KnownUp = up;
                r.DownMbps = down;
                r.UpMbps = up;
                r.Ok = down > 0 && up > 0;
            }
            catch (Exception ex) { Log.Write(ex); r.Ok = false; }
            r.Bytes = Math.Max(0, DataUse.ThisMonth - before);
            return r;
        }
    }

    class TrayApp : ApplicationContext
    {
        static readonly Color Green = Color.FromArgb(46, 125, 50), Orange = Color.FromArgb(230, 100, 0), Red = Color.FromArgb(198, 40, 40);

        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem lineSpeed, linePing, lineStatus, testNow, intervalItem, lineData, startItem, floatItem, unitsItem;
        readonly FloatBox box;
        readonly Network net = new Network();
        readonly System.Windows.Forms.Timer scheduler = new System.Windows.Forms.Timer { Interval = 30000 };
        readonly DateTime started = DateTime.Now;
        volatile bool running = true, testing;
        string units;
        int intervalMin;
        SpeedResult lastSpeed;
        PingState lastPing = new PingState { Online = true };
        Icon current;
        string lastIconKey = "";
        bool iconPending;

        public TrayApp()
        {
            units = Settings.Get("Units", "Mbps");
            int iv; intervalMin = int.TryParse(Settings.Get("IntervalMin", "0"), out iv) ? iv : 0;   // automatic tests Off by default
            if (Startup.Enabled) Startup.Set(true);
            lastSpeed = LoadLastSpeed();
            if (lastSpeed != null && lastSpeed.Ok) { net.KnownDown = lastSpeed.DownMbps; net.KnownUp = lastSpeed.UpMbps; }

            lineSpeed = new ToolStripMenuItem("Speed: not measured yet") { Enabled = false };
            linePing = new ToolStripMenuItem("Ping: measuring…") { Enabled = false };
            lineStatus = new ToolStripMenuItem("") { Enabled = false };
            testNow = new ToolStripMenuItem("Test speed now");
            testNow.Click += (s, e) => StartTest();
            intervalItem = new ToolStripMenuItem("Automatic speed test");
            foreach (var m in new[] { 60, 30, 15, 0 })
            {
                int min = m;
                var it = new ToolStripMenuItem("") { Tag = min, Checked = min == intervalMin };
                it.Click += (s, e) =>
                {
                    intervalMin = min; Settings.Set("IntervalMin", min.ToString());
                    foreach (ToolStripMenuItem x in intervalItem.DropDownItems) x.Checked = (int)x.Tag == min;
                };
                intervalItem.DropDownItems.Add(it);
            }
            lineData = new ToolStripMenuItem("") { Enabled = false };
            startItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Startup.Enabled };
            startItem.CheckedChanged += (s, e) => Startup.Set(startItem.Checked);
            floatItem = new ToolStripMenuItem("Show floating box") { CheckOnClick = true, Checked = Settings.Get("Float", "0") == "1" };
            floatItem.CheckedChanged += (s, e) => { Settings.Set("Float", floatItem.Checked ? "1" : "0"); ShowBox(); };
            unitsItem = new ToolStripMenuItem("Units");
            foreach (var u in new[] { "Mbps", "MB/s" })
            {
                string unit = u;
                var it = new ToolStripMenuItem(unit) { Checked = unit == units };
                it.Click += (s, e) =>
                {
                    units = unit; Settings.Set("Units", unit);
                    foreach (ToolStripMenuItem x in unitsItem.DropDownItems) x.Checked = x.Text == unit;
                    lastIconKey = ""; Refresh();
                };
                unitsItem.DropDownItems.Add(it);
            }
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => Quit();

            menu.Items.AddRange(new ToolStripItem[] {
                lineSpeed, linePing, lineStatus, new ToolStripSeparator(),
                testNow, intervalItem, lineData, new ToolStripSeparator(),
                startItem, floatItem, unitsItem, new ToolStripSeparator(), exit });
            menu.Opening += (s, e) => { startItem.Checked = Startup.Enabled; UpdateMenuTexts(); };
            menu.Closed += (s, e) => { if (iconPending) { iconPending = false; UpdateIcon(); } };

            tray.ContextMenuStrip = menu;
            tray.Text = "NetSpeed";
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
            var handle = box.Handle; // lets the background threads hand results to the screen
            ShowBox();

            if (Settings.Get("FirstRun", "1") == "1")
            {
                Settings.Set("FirstRun", "0");
                tray.ShowBalloonTip(10000, "NetSpeed is running",
                    "Colour = connection quality, bottom number = live ping. Click the icon and choose Test speed now to measure your speed. If you can't see the icon, click the ^ arrow by the clock and drag it onto the taskbar.",
                    ToolTipIcon.Info);
            }

            new Thread(PingLoop) { IsBackground = true, Name = "NetSpeed ping" }.Start();
            scheduler.Tick += (s, e) => CheckSchedule();
            scheduler.Start();
            Refresh();
        }

        // ---------- background work ----------

        void PingLoop()
        {
            while (running)
            {
                if (!testing)   // a running speed test would distort ping
                {
                    PingState p;
                    try { p = net.PingOnce(); } catch (Exception ex) { Log.Write(ex); p = new PingState(); }
                    Post(() => { lastPing = p; Refresh(); });
                }
                Thread.Sleep(5000);
            }
        }

        void CheckSchedule()
        {
            if (intervalMin <= 0 || testing) return;
            if ((DateTime.Now - started).TotalSeconds < 60) return;   // let the PC settle after start-up
            DateTime last = lastSpeed != null ? lastSpeed.When : DateTime.MinValue;
            if (DateTime.Now - last >= TimeSpan.FromMinutes(intervalMin)) StartTest();
        }

        void StartTest()
        {
            if (testing) return;
            if (!lastPing.Online) { lineStatus.Text = "Offline - speed test skipped"; return; }
            testing = true;
            Refresh();
            new Thread(() =>
            {
                SpeedResult r;
                try { r = net.SpeedTest(); } catch (Exception ex) { Log.Write(ex); r = new SpeedResult { When = DateTime.Now }; }
                Post(() =>
                {
                    testing = false;
                    if (r.Ok) { lastSpeed = r; SaveLastSpeed(r); }
                    else if (lastSpeed == null || DateTime.Now - lastSpeed.When > TimeSpan.FromMinutes(intervalMin > 0 ? intervalMin : 60))
                        lastSpeed = new SpeedResult { Ok = false, When = DateTime.Now }; // wait a full interval before retrying
                    Refresh();
                });
            }) { IsBackground = true, Name = "NetSpeed speed test" }.Start();
        }

        void Post(Action a)
        {
            if (!running) return;
            try { box.BeginInvoke(a); } catch { }
        }

        static SpeedResult LoadLastSpeed()
        {
            try
            {
                var p = Settings.Get("LastSpeed", "").Split('|');
                if (p.Length != 3) return null;
                return new SpeedResult
                {
                    Ok = true,
                    When = new DateTime(long.Parse(p[0])),
                    DownMbps = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    UpMbps = double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)
                };
            }
            catch { return null; }
        }

        static void SaveLastSpeed(SpeedResult r)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Settings.Set("LastSpeed", r.When.Ticks + "|" + r.DownMbps.ToString(ci) + "|" + r.UpMbps.ToString(ci));
            if (r.Bytes > 0) Settings.Set("LastTestBytes", r.Bytes.ToString());
        }

        // ---------- display ----------

        int Level()
        {
            if (!lastPing.Online) return 2;
            if (lastPing.PingMs > 100 || lastPing.JitterMs > 10) return 2;
            if (lastPing.PingMs >= 50 || lastPing.JitterMs >= 5) return 1;
            return 0;
        }

        string LevelName(int l) => l == 0 ? "Good" : l == 1 ? "Fair" : "Poor";

        string Reason()
        {
            if (!lastPing.Online) return "offline";
            if (lastPing.PingMs > 100) return "high ping";
            if (lastPing.JitterMs > 10) return "high jitter";
            if (lastPing.PingMs >= 50) return "ping above 50 ms";
            if (lastPing.JitterMs >= 5) return "jitter above 5 ms";
            return "ready for calls and games";
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

        string SpeedText()
        {
            if (testing) return "Speed: testing…";
            if (lastSpeed == null || !lastSpeed.Ok) return "Speed: not measured yet";
            return "↓ " + Full(Conv(lastSpeed.DownMbps)) + "  ↑ " + Full(Conv(lastSpeed.UpMbps)) + " " + units + "  (" + lastSpeed.When.ToString("HH:mm") + ")";
        }

        string PingText() => lastPing.Online
            ? "Ping " + lastPing.PingMs.ToString("0") + " ms · Jitter " + lastPing.JitterMs.ToString("0") + " ms"
            : "Offline";

        void UpdateMenuTexts()
        {
            lineSpeed.Text = SpeedText();
            linePing.Text = PingText();
            lineStatus.Text = LevelName(Level()) + " — " + Reason();
            testNow.Text = testing ? "Testing speed…" : "Test speed now";
            testNow.Enabled = !testing;
            lineData.Text = "NetSpeed data this month: " + (DataUse.ThisMonth / 1e6).ToString("0") + " MB";

            long perTest;
            bool known = long.TryParse(Settings.Get("LastTestBytes", ""), out perTest) && perTest > 0;
            foreach (ToolStripMenuItem it in intervalItem.DropDownItems)
            {
                int min = (int)it.Tag;
                string name = min == 0 ? "Off" : min == 60 ? "Every 1 hour" : "Every " + min + " min";
                if (min > 0 && known)
                {
                    double gb = perTest * (30.0 * 24 * 60 / min) / 1e9;
                    name += "  (≈ " + gb.ToString("0.0") + " GB/month at your speed)";
                }
                it.Text = name;
            }
        }

        void Refresh()
        {
            if (!running) return;
            int lvl = Level();
            UpdateMenuTexts();
            string tip = LevelName(lvl) + " · " + PingText() + "\n" + SpeedText().Replace("  (", " (");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            box.SetValues(SpeedText(), PingText(), LevelName(lvl) + " — " + Reason(), lvl == 0 ? Green : lvl == 1 ? Orange : Red);
            if (menu.Visible) { iconPending = true; return; }   // never touch the icon while the menu is open
            UpdateIcon();
        }

        void UpdateIcon()
        {
            int lvl = Level();
            string top = testing ? "…" : (lastSpeed != null && lastSpeed.Ok ? Short(Conv(lastSpeed.DownMbps)) : "--");
            string bottom = lastPing.Online ? Math.Min(999, Math.Round(lastPing.PingMs)).ToString("0") : "--";
            string key = top + "|" + bottom + "|" + lvl;
            if (key == lastIconKey) return;
            lastIconKey = key;
            var old = current;
            current = MakeIcon(top, bottom, lvl == 0 ? Green : lvl == 1 ? Orange : Red);
            tray.Icon = current;
            old?.Dispose();
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

        static Icon MakeIcon(string top, string bottom, Color bgColor)
        {
            int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(s, s))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var bg = new SolidBrush(bgColor))
                using (var path = Rounded(new RectangleF(0, 0, s, s), s * 0.18f))
                    g.FillPath(bg, path);
                DrawFit(g, top, Color.White, new RectangleF(0, 0, s, s / 2f), s);
                DrawFit(g, bottom, Color.White, new RectangleF(0, s / 2f, s, s / 2f), s);
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
            scheduler.Stop();
            tray.Visible = false;
            tray.Dispose();
            box.Close();
            ExitThread();
        }
    }

    class FloatBox : Form
    {
        string line1 = "Speed: …", line2 = "Ping: …", line3 = "";
        Color statusColor = Color.Gray;
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
                var w = g.MeasureString("↓ 999.99  ↑ 999.99 MB/s  (00:00)", font).Width;
                var h = g.MeasureString("Ag", font).Height;
                var h3 = g.MeasureString("Ag", small).Height;
                ClientSize = new Size((int)w + 18, (int)(h * 2 + h3) + 8);
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
                    Native.SendMessage(Handle, 0xA1, 2, 0);
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
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }
        }

        protected override bool ShowWithoutActivation => true;

        public void SetValues(string a, string b, string c, Color col)
        {
            line1 = a; line2 = b; line3 = c; statusColor = col;
            if (Visible) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float lh = g.MeasureString("Ag", font).Height;
            using (var bar = new SolidBrush(statusColor))
            using (var w = new SolidBrush(Color.White))
            using (var lt = new SolidBrush(Color.FromArgb(200, 210, 225)))
            {
                g.FillRectangle(bar, 0, 0, 5, ClientSize.Height);
                g.DrawString(line1, font, w, 9, 3);
                g.DrawString(line2, font, lt, 9, 3 + lh);
                g.DrawString(line3, small, bar, 9, 4 + lh * 2);
            }
        }
    }
}
