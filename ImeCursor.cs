// ImeCursor - shows the active input method and Chinese/English mode next to the mouse pointer.
// Entry point, logging, single instance, diagnostic modes (--probe, --render, --bench).
// C# 5 / .NET Framework 4.x; build with build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ImeCursor
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private const long MaxBytes = 512 * 1024;

        public static string PathName
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImeCursor.log"); }
        }

        public static void Write(string msg)
        {
            try
            {
                lock (Sync)
                {
                    string p = PathName;
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = p + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch (Exception) { /* logging must never crash the app */ }
        }

        private static readonly Dictionary<string, int> RepeatCounts = new Dictionary<string, int>();
        private const int RepeatFull = 20;

        /// <summary>For errors that can repeat on every tick: the first 20 of a category are logged in full,
        /// then only every 1000th (with the count), so the log keeps the first (root-cause) entries.</summary>
        public static void WriteLimited(string category, string msg)
        {
            int n;
            lock (Sync)
            {
                RepeatCounts.TryGetValue(category, out n);
                n++;
                RepeatCounts[category] = n;
            }
            if (n <= RepeatFull) Write(category + ": " + msg);
            else if (n == RepeatFull + 1) Write(category + ": further errors of this kind are suppressed (every 1000th is logged)");
            else if (n % 1000 == 0) Write(category + " (occurrence " + n + "): " + msg);
        }
    }

    internal static class Program
    {
        private const string MutexName = @"Local\ImeCursor.Singleton";

        [STAThread]
        private static int Main(string[] args)
        {
            Native.EnableDpiAwareness();   // before any window is created
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.WriteLimited("UI exception", e.Exception.ToString()); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Write("fatal: " + e.ExceptionObject);
            };

            try
            {
                string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                if (mode == "--probe") return Diagnostics.Probe(args);
                if (mode == "--render") return Diagnostics.Render(args);
                if (mode == "--bench") return Diagnostics.Bench(args);
                if (mode == "--caret") return Diagnostics.Caret(args);
                return RunTray(mode == "--restarted");
            }
            catch (Exception ex)
            {
                Log.Write("fatal: " + ex);
                return 1;
            }
        }

        private static int RunTray(bool restarted)
        {
            bool createdNew;
            Mutex mutex;
            try { mutex = new Mutex(true, MutexName, out createdNew); }
            catch (UnauthorizedAccessException) { return 0; }   // owned by an elevated instance
            if (!createdNew)
            {
                bool got = false;
                if (restarted)
                {
                    // the previous instance is exiting (restart as administrator): wait for it
                    try { got = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { got = true; }
                }
                if (!got) { mutex.Dispose(); return 0; }   // already running: exit silently
            }

            bool released = false;
            Action release = delegate
            {
                if (released) return;
                released = true;
                try { mutex.ReleaseMutex(); } catch (Exception) { }
                mutex.Dispose();
            };
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Config cfg = Config.Load(Config.DefaultPath());
                using (TrayApp app = new TrayApp(cfg, release))
                {
                    Application.ApplicationExit += delegate { app.ExitApp(); };
                    Application.Run(app);
                }
            }
            finally
            {
                release();
            }
            return 0;
        }
    }

    internal static class Diagnostics
    {
        // ------------------------------------------------------------------ --probe <logfile> <seconds>

        public static int Probe(string[] args)
        {
            if (args.Length < 3) return 2;
            string logPath = args[1];
            int seconds;
            if (!int.TryParse(args[2], out seconds)) seconds = 60;
            // optional 4th arg "app": query exactly like the tray app (conv skipped where the rule ignores it)
            bool queryAll = !(args.Length > 3 && args[3].Equals("app", StringComparison.OrdinalIgnoreCase));
            Config cfg = Config.Load(Config.DefaultPath());
            Detector det = new Detector();
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            string last = null;
            Stopwatch sw = Stopwatch.StartNew();
            using (StreamWriter w = new StreamWriter(logPath, true, new UTF8Encoding(false)))
            {
                w.AutoFlush = true;
                w.WriteLine(Stamp() + " PROBE start pollMs=" + cfg.PollMs + " queryAll=" + queryAll + " warnings=" + cfg.Warnings.Count);
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    try
                    {
                        RawSample s = det.Sample(cfg, true, queryAll);
                        det.Stabilize(s);
                        string line;
                        if (s.OwnProcess || s.Foreground == IntPtr.Zero)
                            line = "proc=" + (s.OwnProcess ? "(self)" : "(none)") + " keep-last";
                        else
                        {
                            Decision d = det.Decide(s, cfg);
                            if (!Detector.ShouldPublish(s, d)) { Thread.Sleep(cfg.PollMs); continue; }
                            line = "proc=" + ProcName(names, s.Pid) +
                                   " hkl=" + s.Hkl.ToString("X8") +
                                   " open=" + (s.QueryOk ? s.Open.ToString() : "-") +
                                   " conv=" + (s.QueryOk ? s.Conv.ToString() : "-") +
                                   (s.Bridged ? " (bridged)" : "") +
                                   " caps=" + (s.Caps ? 1 : 0) +
                                   " state=" + Decision.ModeName(d.Mode) +
                                   " fs=" + (d.Fullscreen ? 1 : 0) +
                                   " badge=[" + d.BadgeText + "]";
                        }
                        if (line != last)
                        {
                            w.WriteLine(Stamp() + " DET " + line);
                            last = line;
                        }
                    }
                    catch (Exception ex) { w.WriteLine(Stamp() + " ERR " + ex.Message); }
                    Thread.Sleep(cfg.PollMs);
                }
                w.WriteLine(Stamp() + " PROBE end");
            }
            return 0;
        }

        // ------------------------------------------------------------------ --caret <logfile> <seconds>

        /// <summary>Logs every change of the foreground app's caret rectangle (physical px) and where it came from.</summary>
        public static int Caret(string[] args)
        {
            if (args.Length < 3) return 2;
            int seconds;
            if (!int.TryParse(args[2], out seconds)) seconds = 60;
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            string last = null;
            Stopwatch sw = Stopwatch.StartNew();
            using (StreamWriter w = new StreamWriter(args[1], true, new UTF8Encoding(false)))
            {
                w.AutoFlush = true;
                w.WriteLine(Stamp() + " CARET start");
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    try
                    {
                        IntPtr fg = Native.GetForegroundWindow();
                        uint pid;
                        Native.GetWindowThreadProcessId(fg, out pid);
                        long t0 = Stopwatch.GetTimestamp();
                        CaretInfo ci = CaretLocator.Locate(fg);
                        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        string line = "proc=" + ProcName(names, pid) + " cls=" + Native.ClassNameOf(fg) + " " +
                            (ci == null ? "caret=none" : string.Format("caret={0},{1}-{2},{3} src={4}", ci.Rect.Left, ci.Rect.Top, ci.Rect.Right, ci.Rect.Bottom, ci.Source));
                        if (line != last) { w.WriteLine(Stamp() + " " + line + string.Format(" ({0:0.0} ms)", ms)); last = line; }
                    }
                    catch (Exception ex) { w.WriteLine(Stamp() + " ERR " + ex.Message); }
                    Thread.Sleep(30);
                }
                w.WriteLine(Stamp() + " CARET end");
            }
            return 0;
        }

        private static string Stamp() { return DateTime.Now.ToString("HH:mm:ss.fff"); }

        private static string ProcName(Dictionary<uint, string> cache, uint pid)
        {
            string n;
            if (cache.TryGetValue(pid, out n)) return n;
            try { using (Process p = Process.GetProcessById((int)pid)) n = p.ProcessName; }
            catch (Exception) { n = "pid" + pid; }
            cache[pid] = n;
            return n;
        }

        // ------------------------------------------------------------------ --render <out.png>

        public static int Render(string[] args)
        {
            string outPath = args.Length > 1 ? args[1] : "render.png";
            Config cfg = Config.Load(Config.DefaultPath());
            string zh = "中", en = "英", wx = "微信";
            // label, text, colour
            object[][] rows = new object[][] {
                new object[] { "EN (US keyboard)",   "EN",            cfg.ColorKeyboard },
                new object[] { "EN + Caps",          "EN A",          cfg.ColorCaps },
                new object[] { "WeType Chinese",     wx + " " + zh,   cfg.ColorChinese },
                new object[] { "WeType English",     wx + " " + en,   cfg.ColorEnglish },
                new object[] { "WeType + Caps",      wx + " A",       cfg.ColorCaps },
                new object[] { "WeType unknown",     wx + " ?",       cfg.ColorUnknown },
                new object[] { "ShowImeName=0: EN",  "EN",            cfg.ColorKeyboard },
                new object[] { "ShowImeName=0: Caps","A",             cfg.ColorCaps },
                new object[] { "ShowImeName=0: zh",  zh,              cfg.ColorChinese },
                new object[] { "ShowImeName=0: en",  en,              cfg.ColorEnglish },
                new object[] { "ShowImeName=0: ?",   "?",             cfg.ColorUnknown },
                new object[] { "FLASH x" + cfg.FlashScale.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), wx + " " + zh, cfg.ColorChinese },
            };
            int[] dpis = new int[] { 96, 144, 192 };
            int labelW = 190, cellW = 230, headerH = 34, rowH = 74;
            int W = labelW + cellW * dpis.Length * 2;
            const int bottomH = 160;   // tray icons + 4x zoom sample (124 px tall at 150%)
            int H = headerH + rowH * rows.Length + bottomH;
            Color light = Color.FromArgb(0xF3, 0xF4, 0xF6), dark = Color.FromArgb(0x1E, 0x1E, 0x1E);
            using (Bitmap canvas = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(canvas))
            using (Font lf = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font hf = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (ImageAttributes ia = new ImageAttributes())
            {
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = cfg.Opacity / 255f;   // same as SourceConstantAlpha
                ia.SetColorMatrix(cm);
                for (int di = 0; di < dpis.Length; di++)
                {
                    for (int bg = 0; bg < 2; bg++)
                    {
                        int x0 = labelW + (di * 2 + bg) * cellW;
                        using (SolidBrush b = new SolidBrush(bg == 0 ? light : dark))
                            g.FillRectangle(b, x0, 0, cellW, H - bottomH);
                        using (SolidBrush tb = new SolidBrush(bg == 0 ? Color.Black : Color.White))
                            g.DrawString((dpis[di] * 100 / 96) + "% " + (bg == 0 ? "light" : "dark"), hf, tb, x0 + 8, 9);
                    }
                }
                for (int r = 0; r < rows.Length; r++)
                {
                    int y0 = headerH + r * rowH;
                    g.DrawString((string)rows[r][0], lf, Brushes.Black, 8, y0 + rowH / 2 - 8);
                    bool flash = r == rows.Length - 1;
                    for (int di = 0; di < dpis.Length; di++)
                    {
                        for (int bg = 0; bg < 2; bg++)
                        {
                            int x0 = labelW + (di * 2 + bg) * cellW;
                            float scale = dpis[di] / 96f * (flash ? cfg.FlashScale : 1f);
                            using (Bitmap bmp = BadgeRenderer.RenderBitmap((string)rows[r][1], cfg.FontName, cfg.FontSize, scale, (Color)rows[r][2], cfg.ColorText))
                            {
                                int by = y0 + (rowH - bmp.Height) / 2;
                                g.DrawImage(bmp, new Rectangle(x0 + 14, by, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                            }
                        }
                    }
                }
                // tray icons at 16/20/24/32 px
                int ty = H - bottomH + 10;
                g.DrawString("Tray icons 16/20/24/32 px\n(last: neutral app icon)", lf, Brushes.Black, 8, ty + 10);
                string[] trayTexts = new string[] { "EN", "A", zh, en, "?", TrayApp.NeutralTrayText };
                Color[] trayCols = new Color[] { cfg.ColorKeyboard, cfg.ColorCaps, cfg.ColorChinese, cfg.ColorEnglish, cfg.ColorUnknown, cfg.ColorKeyboard };
                int[] sizes = new int[] { 16, 20, 24, 32 };
                int tx = labelW;
                foreach (int sz in sizes)
                {
                    for (int i = 0; i < trayTexts.Length; i++)
                    {
                        IntPtr h = BadgeRenderer.RenderTrayIcon(trayTexts[i], sz, cfg.FontName, trayCols[i], cfg.ColorText);
                        try
                        {
                            using (Icon ic = Icon.FromHandle(h))
                            using (Bitmap ib = ic.ToBitmap())
                                g.DrawImage(ib, tx, ty + 6, sz, sz);
                        }
                        finally { Native.DestroyIcon(h); }
                        tx += sz + 8;
                    }
                    tx += 24;
                }
                // 4x zoom of the 150% WeType Chinese badge for pixel inspection
                using (Bitmap bmp = BadgeRenderer.RenderBitmap(wx + " " + zh, cfg.FontName, cfg.FontSize, 1.5f, cfg.ColorChinese, cfg.ColorText))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    int zx = tx + 10, zy = ty - 6;
                    g.DrawString("150% x4", lf, Brushes.Black, zx, zy - 14 < 0 ? 0 : zy - 2);
                    g.DrawImage(bmp, new Rectangle(zx + 52, zy, bmp.Width * 4, bmp.Height * 4), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                }
                canvas.Save(outPath, ImageFormat.Png);
            }
            return 0;
        }

        // ------------------------------------------------------------------ --bench <seconds> [outfile]

        public static int Bench(string[] args)
        {
            int seconds;
            if (args.Length < 2 || !int.TryParse(args[1], out seconds)) seconds = 20;
            string outFile = args.Length > 2 ? args[2] : null;
            StringBuilder report = new StringBuilder();
            Config cfg = Config.Load(Config.DefaultPath());
            string zh = "中", en = "英", wx = "微信";
            string[] texts = new string[] { "EN", "EN A", wx + " " + zh, wx + " " + en, wx + " A", wx + " ?", zh, en };
            Color[] cols = new Color[] { cfg.ColorKeyboard, cfg.ColorCaps, cfg.ColorChinese, cfg.ColorEnglish, cfg.ColorCaps, cfg.ColorUnknown, cfg.ColorChinese, cfg.ColorEnglish };

            Process me = Process.GetCurrentProcess();
            report.AppendLine(Metrics("before-window", me));
            Overlay ov = new Overlay();
            Native.POINT cp;
            Native.GetCursorPos(out cp);
            IntPtr mon = Native.MonitorFromPoint(cp, Native.MONITOR_DEFAULTTONEAREST);
            Native.RECT mr = Native.MonitorRect(mon);
            int dpi = Native.DpiOfMonitor(mon);
            int cx = (mr.Left + mr.Right) / 2, cy = (mr.Top + mr.Bottom) / 2;
            int frames = 0, i = 0;
            string warm = null;
            Stopwatch sw = Stopwatch.StartNew();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 30;
            t.Tick += delegate
            {
                int k = i % texts.Length;
                bool flash = (i / texts.Length) % 3 == 0;
                float scale = dpi / 96f * (flash ? cfg.FlashScale : 1f);
                ov.SetContent(texts[k] + flash + dpi, texts[k], cfg.FontName, cfg.FontSize, scale, cols[k], cfg.ColorText, cfg.Opacity);
                double a = i * 0.15;
                ov.Place(cx + (int)(160 * Math.Cos(a)), cy + (int)(90 * Math.Sin(a)));
                i++;
                frames++;
                if (warm == null && sw.ElapsedMilliseconds > 1500) warm = Metrics("after-warmup", me);
                if (sw.Elapsed.TotalSeconds >= seconds) Application.ExitThread();
            };
            t.Start();
            Application.Run();
            t.Stop();
            t.Dispose();
            report.AppendLine(warm ?? "after-warmup n/a");
            report.AppendLine(Metrics("end", me));
            ov.Dispose();
            report.AppendLine(Metrics("after-dispose", me));
            report.AppendLine("frames=" + frames + " seconds=" + seconds + " dpi=" + dpi);
            string text = report.ToString();
            if (outFile != null) File.WriteAllText(outFile, text, Encoding.UTF8);
            try
            {
                Native.AttachConsole(-1);
                Console.Out.Write(text);
                Console.Out.Flush();
            }
            catch (Exception) { }
            return 0;
        }

        private static string Metrics(string label, Process me)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            me.Refresh();
            return string.Format("{0,-14} gdi={1} user={2} handles={3} ws={4:0.0}MB private={5:0.0}MB cpu={6:0}ms",
                label,
                Native.GetGuiResources(me.Handle, Native.GR_GDIOBJECTS),
                Native.GetGuiResources(me.Handle, Native.GR_USEROBJECTS),
                me.HandleCount,
                me.WorkingSet64 / 1048576.0,
                me.PrivateMemorySize64 / 1048576.0,
                me.TotalProcessorTime.TotalMilliseconds);
        }
    }
}
