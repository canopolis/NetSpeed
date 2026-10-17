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
                Diag.Line("error", "", "", "", "", "", "", "", "", "", "", "", (ex?.GetType().Name ?? "") + ": " + (ex?.Message ?? ""));
            }
            catch { }
        }
    }

    // Diagnostic log: one CSV line per ping burst and per speed test.
    // %LOCALAPPDATA%\NetSpeed\netspeed_log.csv  (rotates at 5 MB, previous kept as netspeed_log_old.csv)
    static class Diag
    {
        static readonly object gate = new object();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetSpeed");
        static readonly string FilePath = Path.Combine(Dir, "netspeed_log.csv");
        const string Header = "time,type,online,mode,raw_ms,burst_median_ms,ping_avg_ms,jitter_ms,normal_ms,status,reason,down_mbps,up_mbps,details";
        static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

        public static string N(double v) => v.ToString("0.0", IC);

        static string Q(string v)
        {
            if (v == null) return "";
            return v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        // Copies all log files into a new dated folder on the Desktop and returns that folder.
        public static string CopyToDesktop()
        {
            lock (gate)
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string target = Path.Combine(desk, "NetSpeed logs " + DateTime.Now.ToString("yyyy-MM-dd HHmm", IC));
                Directory.CreateDirectory(target);
                int copied = 0;
                foreach (var name in new[] { "netspeed_log.csv", "netspeed_log_old.csv", "error.log" })
                {
                    string src = Path.Combine(Dir, name);
                    if (File.Exists(src)) { File.Copy(src, Path.Combine(target, name), true); copied++; }
                }
                if (copied == 0) File.WriteAllText(Path.Combine(target, "no logs yet.txt"), "NetSpeed has not written any log yet.");
                return target;
            }
        }

        public static void Line(params string[] fields)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 5000000)
                    {
                        string old = Path.Combine(Dir, "netspeed_log_old.csv");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    bool isNew = !File.Exists(FilePath);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC) + "," + string.Join(",", fields.Select(Q));
                    File.AppendAllText(FilePath, (isNew ? Header + Environment.NewLine : "") + line + Environment.NewLine);
                }
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
        public double PingMs, JitterMs, Baseline, BurstMedian;
        public string Raw = "", Mode = "";
    }

    class SpeedResult
    {
        public bool Ok;
        public double DownMbps, UpMbps;
        public long Bytes;
        public DateTime When;
        public string Detail = "";
    }

    class Network
    {
        const string Base = "https://speed.cloudflare.com";
        const string PingHost = "1.1.1.1";
        const int MinB = 200000, MaxB = 5000000;    // a single test never moves more than 5 MB each way
        const double TargetSeconds = 0.2;           // measured part of each transfer
        readonly HttpClient http;
        readonly Random rnd = new Random();
        readonly List<double> histP = new List<double>(), histJ = new List<double>();
        bool httpMode;
        DateTime httpSince;
        int burstFails, offline;
        public double LastPing = 30;
        string lastDetail = "";
        public double KnownDown, KnownUp;          // last results, used to size the next test without a probe
        public double Baseline;                    // this connection's normal ping (learned)
        readonly List<double> longHist = new List<double>();
        const double UpTargetSeconds = 0.35;       // upload measured a bit longer for accuracy

        public Network()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetSpeed/3.6");
            http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        }

        string R() => rnd.Next().ToString("x");

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

        // Burst of 4 network pings 0.1 s apart: ping = middle value, jitter = variation inside the burst.
        List<double> IcmpBurst()
        {
            var got = new List<double>();
            using (var p = new Ping())
            {
                for (int i = 0; i < 4; i++)
                {
                    try
                    {
                        var reply = p.Send(PingHost, 1000);
                        DataUse.Add(120);
                        if (reply.Status == IPStatus.Success) got.Add(Math.Max(1, reply.RoundtripTime));
                    }
                    catch { }
                    if (i < 3) Thread.Sleep(100);
                }
            }
            return got;
        }

        // Web fallback, only used when the network blocks normal pings. First request only warms the connection.
        List<double> HttpBurst()
        {
            var got = new List<double>();
            for (int i = 0; i < 4; i++)
            {
                try { double ms = HttpPing(); if (i > 0) got.Add(ms); } catch { }
                if (i < 3) Thread.Sleep(100);
            }
            return got;
        }

        public PingState Sample()
        {
            List<double> got;
            if (!httpMode)
            {
                got = IcmpBurst();
                if (got.Count == 0)
                {
                    burstFails++;
                    if (burstFails >= 3)
                    {
                        var h = HttpBurst();
                        if (h.Count > 0) { httpMode = true; httpSince = DateTime.Now; got = h; }
                    }
                }
                else burstFails = 0;
            }
            else
            {
                got = null;
                if ((DateTime.Now - httpSince).TotalMinutes >= 5)   // check if normal pings work again
                {
                    httpSince = DateTime.Now;
                    var test = IcmpBurst();
                    if (test.Count > 0) { httpMode = false; burstFails = 0; got = test; }
                }
                if (got == null) got = HttpBurst();
            }

            var st = new PingState();
            st.Mode = httpMode ? "web" : "icmp";
            st.Raw = got.Count == 0 ? "no reply" : string.Join(" ", got.Select(x => x.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            if (got.Count == 0)
            {
                offline++;
                st.Online = offline < 2;     // two failed bursts in a row (about 20 s) = offline
                st.PingMs = LastPing;
                st.JitterMs = histJ.Count > 0 ? histJ.Average() : 0;
                st.Baseline = Baseline;
                return st;
            }
            offline = 0;

            var sorted = got.OrderBy(x => x).ToList();
            double med = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
                       : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            double jit = 0;
            if (got.Count >= 2)
            {
                for (int i = 1; i < got.Count; i++) jit += Math.Abs(got[i] - got[i - 1]);
                jit /= got.Count - 1;
            }

            // Show the average of the last 3 bursts (30 s) so one stray value doesn't flip the colour.
            histP.Add(med); histJ.Add(jit);
            while (histP.Count > 3) histP.RemoveAt(0);
            while (histJ.Count > 3) histJ.RemoveAt(0);
            LastPing = histP.Average();

            // Normal ping = low value seen over the last hour (20th percentile of burst medians).
            longHist.Add(med);
            while (longHist.Count > 360) longHist.RemoveAt(0);
            if (longHist.Count >= 6)
            {
                var ls = longHist.OrderBy(x => x).ToList();
                Baseline = ls[(int)(ls.Count * 0.2)];
                if (longHist.Count % 30 == 0)
                    Settings.Set("BaselinePing", Baseline.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (Baseline <= 0) Baseline = med;
            st.Baseline = Baseline;
            st.BurstMedian = med;
            st.Online = true;
            st.PingMs = LastPing;
            st.JitterMs = histJ.Average();
            return st;
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
                    lastDetail = "down req " + size + " B, got " + moved + " B, timed " + after + " B in " + (sec * 1000).ToString("0") + " ms";
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
            double eff = Math.Max(ms - LastPing / 2, ms * 0.7);
            lastDetail = "up " + size + " B in " + ms.ToString("0") + " ms (ping " + LastPing.ToString("0") + ", counted " + eff.ToString("0") + " ms)";
            return size * 8 / (eff / 1000) / 1e6;
        }

        static int SizeFor(double mbps, double seconds = TargetSeconds)
        {
            if (mbps <= 0) return 1000000;
            double b = mbps * 1e6 / 8 * seconds;
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
                var det = new List<string>();
                double downEst;
                if (KnownDown > 0) downEst = KnownDown;
                else { downEst = Download(MinB, out moved); det.Add("probe " + lastDetail); }
                double down = Download(SizeFor(downEst), out moved);
                det.Add(lastDetail);
                if (down <= 0) down = downEst;
                double upEst;
                if (KnownUp > 0) upEst = KnownUp;
                else { upEst = Upload(MinB); det.Add("probe " + lastDetail); }
                double up = Upload(SizeFor(upEst, UpTargetSeconds));
                det.Add(lastDetail);
                r.Detail = string.Join(" | ", det);
                if (down > 0) KnownDown = down;
                if (up > 0) KnownUp = up;
                r.DownMbps = down;
                r.UpMbps = up;
                r.Ok = down > 0 && up > 0;
            }
            catch (Exception ex) { Log.Write(ex); r.Ok = false; r.Detail = "failed: " + ex.Message; }
            r.Bytes = Math.Max(0, DataUse.ThisMonth - before);
            return r;
        }
    }

    class TrayApp : ApplicationContext
    {
        static readonly Color Green = Color.FromArgb(27, 110, 32), Orange = Color.FromArgb(190, 85, 0), Red = Color.FromArgb(183, 28, 28);

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
            double bp;
            if (double.TryParse(Settings.Get("BaselinePing", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out bp) && bp > 0)
                net.Baseline = bp;

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
            var openLog = new ToolStripMenuItem("Logs");
            var openFolder = new ToolStripMenuItem("Open log folder");
            openFolder.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(Diag.Dir); Process.Start("explorer.exe", "\"" + Diag.Dir + "\""); }
                catch (Exception ex) { Log.Write(ex); }
            };
            var openCsv = new ToolStripMenuItem("Open log file");
            openCsv.Click += (s, e) =>
            {
                try
                {
                    string f = Path.Combine(Diag.Dir, "netspeed_log.csv");
                    if (File.Exists(f)) Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
                    else tray.ShowBalloonTip(4000, "NetSpeed", "No log yet.", ToolTipIcon.Info);
                }
                catch (Exception ex) { Log.Write(ex); }
            };
            var copyLogs = new ToolStripMenuItem("Copy logs to Desktop");
            copyLogs.Click += (s, e) =>
            {
                try
                {
                    string target = Diag.CopyToDesktop();
                    Process.Start("explorer.exe", "\"" + target + "\"");
                    tray.ShowBalloonTip(5000, "NetSpeed", "Logs copied to Desktop:\n" + Path.GetFileName(target), ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Log.Write(ex);
                    tray.ShowBalloonTip(5000, "NetSpeed", "Could not copy logs: " + ex.Message, ToolTipIcon.Warning);
                }
            };
            openLog.DropDownItems.AddRange(new ToolStripItem[] { openCsv, openFolder, copyLogs });

            menu.Items.AddRange(new ToolStripItem[] {
                lineSpeed, linePing, lineStatus, new ToolStripSeparator(),
                testNow, intervalItem, lineData, new ToolStripSeparator(),
                startItem, floatItem, unitsItem, openLog, new ToolStripSeparator(), exit });
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

            Diag.Line("start", "", "", "", "", "", "", "", "", "", "", "", "NetSpeed " + Application.ProductVersion + " on " + Environment.OSVersion.VersionString);
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
                    try { p = net.Sample(); } catch (Exception ex) { Log.Write(ex); p = new PingState(); }
                    Post(() =>
                    {
                        lastPing = p; Refresh();
                        int l = Level();
                        Diag.Line("ping", p.Online ? "yes" : "no", p.Mode, p.Raw, Diag.N(p.BurstMedian), Diag.N(p.PingMs),
                                  Diag.N(p.JitterMs), Diag.N(Normal()), LevelName(l), Reason(), "", "", "");
                    });
                }
                Thread.Sleep(10000);
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
                    Diag.Line("speedtest", r.Ok ? "yes" : "no", "", "", "", "", "", "", r.Ok ? "ok" : "failed", "",
                              r.Ok ? Diag.N(r.DownMbps) : "", r.Ok ? Diag.N(r.UpMbps) : "",
                              r.Detail + " | total " + r.Bytes + " B");
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

        double Normal() => lastPing.Baseline > 0 ? lastPing.Baseline : lastPing.PingMs;
        double Above() => Math.Max(0, lastPing.PingMs - Normal());

        // Green: ping within 30 ms of this connection's normal and jitter under 10 ms.
        // Orange: ping 30-60 ms above normal or jitter 10-30 ms. Red: more than that, or offline.
        int Level()
        {
            if (!lastPing.Online) return 2;
            if (Above() > 60 || lastPing.JitterMs > 30) return 2;
            if (Above() > 30 || lastPing.JitterMs >= 10) return 1;
            return 0;
        }

        string LevelName(int l) => l == 0 ? "Good" : l == 1 ? "Fair" : "Poor";

        string Reason()
        {
            if (!lastPing.Online) return "offline";
            if (Above() > 60) return "ping " + Above().ToString("0") + " ms above normal";
            if (lastPing.JitterMs > 30) return "high jitter";
            if (Above() > 30) return "ping " + Above().ToString("0") + " ms above normal";
            if (lastPing.JitterMs >= 10) return "jitter above 10 ms";
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
            ? "Ping " + lastPing.PingMs.ToString("0") + " ms (normal " + Normal().ToString("0") + ") · Jitter " + lastPing.JitterMs.ToString("0") + " ms"
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
                using (var bg = new SolidBrush(Color.FromArgb(250, 250, 250)))
                using (var border = new Pen(bgColor, Math.Max(1f, s / 16f)))
                using (var path = Rounded(new RectangleF(0.5f, 0.5f, s - 1.5f, s - 1.5f), s * 0.18f))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }
                DrawFit(g, top, bgColor, new RectangleF(0, 0, s, s / 2f), s);
                DrawFit(g, bottom, bgColor, new RectangleF(0, s / 2f, s, s / 2f), s);
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
        readonly Font small = new Font("Segoe UI", 8f, FontStyle.Bold);

        public FloatBox(ContextMenuStrip menu)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;               // set once only
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(252, 252, 252);
            Opacity = 0.96;
            DoubleBuffered = true;
            ContextMenuStrip = menu;
            AutoScaleMode = AutoScaleMode.None;

            using (var g = CreateGraphics())
            {
                var w = Math.Max(g.MeasureString("↓ 999.99  ↑ 999.99 MB/s  (00:00)", font).Width,
                                 g.MeasureString("Ping 999 ms (normal 999) · Jitter 99 ms", font).Width);
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
            using (var w = new SolidBrush(Color.FromArgb(25, 25, 25)))
            using (var lt = new SolidBrush(Color.FromArgb(60, 64, 72)))
            using (var edge = new Pen(Color.FromArgb(190, 190, 190)))
            {
                g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                g.FillRectangle(bar, 0, 0, 5, ClientSize.Height);
                g.DrawString(line1, font, w, 9, 3);
                g.DrawString(line2, font, lt, 9, 3 + lh);
                g.DrawString(line3, small, bar, 9, 4 + lh * 2);
            }
        }
    }
}
