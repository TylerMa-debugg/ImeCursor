// ImeCursor - tray icon, detection thread and the pointer-following badge.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ImeCursor
{
    internal sealed class TrayApp : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string RunValue = "ImeCursor";
        internal const string NeutralTrayText = "输";        // 输 (input): app icon that never looks like a mode

        // ---- shared with the detection thread
        private volatile Config cfg;
        private volatile Decision latest;          // written by the detection thread, read on the UI thread
        private volatile bool reloadCatalog;
        private readonly Detector detector = new Detector();
        private readonly Thread detectThread;
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);

        // ---- text cursor (caret) tracking: written by the caret thread, read on the UI thread
        private const int CaretGraceMs = 500;      // keep the last caret this long when it briefly disappears (selection, redraw)
        private volatile CaretInfo caret;
        private readonly Thread caretThread;
        private readonly AutoResetEvent caretWake = new AutoResetEvent(false);
        private readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        // ---- UI objects
        private readonly Overlay overlay;
        private readonly System.Windows.Forms.Timer followTimer;
        private readonly NotifyIcon tray;
        private readonly ContextMenu menu;
        private readonly MenuItem miShow;
        private readonly MenuItem miAutostart;
        private IntPtr trayIconHandle;
        private Icon trayIcon;
        private string trayIconKey;
        private string trayTip;
        private bool trayDirty;

        // ---- pointer following (UI thread only)
        private const int IdleFollowMs = 40;          // timer rate after 1 s without movement, if no cursor events arrive
        private const int IdleFollowHookedMs = 250;   // ... once cursor WinEvents are known to arrive (they wake us on movement)
        private const double MinEventGapMs = 4.0;     // cursor events closer than this are left to the next event / timer tick
        private readonly Native.WinEventProc cursorHookProc;   // referenced so the delegate is not collected
        private IntPtr cursorHook;
        private bool cursorEventsSeen;
        private long lastFollowTs;
        private bool inFollow, followAgain;
        private bool enabled = true;
        private string shownKey;
        private int flashUntil;
        private bool flashing;
        private int noFlashUntil;
        private Native.RECT lastAnchor = new Native.RECT();   // pointer (as an empty rect) or caret rect of the last layout
        private bool lastAnchorCaret;
        private Native.POINT seenPt = new Native.POINT(int.MinValue, int.MinValue);
        private int lastMoveTick;
        private bool forceLayout = true;
        private int overlayX, overlayY;
        private string normalKey;                     // badge size without the flash enlargement, for the flip decision
        private int normalW, normalH;
        private int cursorBase = -1;                  // HKCU\Control Panel\Cursors\CursorBaseSize (logical px)
        private int cursorBaseTick;

        // ---- optional self-profiling (env IMECURSOR_PROFILE=1): logs time spent every 10 s
        private readonly bool profile = Environment.GetEnvironmentVariable("IMECURSOR_PROFILE") == "1";
        private long followTicks, followCount, detectTicks, detectCount, cursorEventCount;
        private long caretTicks, caretCount, caretEventCount;
        private int profileTick = Environment.TickCount;

        private bool exiting;
        private readonly Action onExit;

        public TrayApp(Config config, Action exitCallback)
        {
            cfg = config;
            onExit = exitCallback;
            foreach (string w in config.Warnings) Log.Write("config: " + w);

            // An elevated process does not receive "TaskbarCreated" from the non-elevated Explorer (UIPI);
            // without it the tray icon would be lost for good after an Explorer restart.
            if (IsElevated()) AllowTaskbarCreated();

            overlay = new Overlay();
            overlay.Wake = OnWake;

            miShow = new MenuItem("Show badge", OnToggleShow);
            miShow.Checked = true;
            miShow.DefaultItem = true;
            miAutostart = new MenuItem("Start with Windows", OnToggleAutostart);
            MenuItem miSettings = new MenuItem("Open settings", OnOpenSettings);
            MenuItem miReload = new MenuItem("Reload settings", OnReload);
            MenuItem miAdmin = new MenuItem(IsElevated() ? "Running as administrator" : "Restart as administrator...", OnRestartAdmin);
            miAdmin.Enabled = !IsElevated();
            MenuItem miExit = new MenuItem("Exit", OnExit);
            menu = new ContextMenu(new MenuItem[] {
                miShow, miAutostart, new MenuItem("-"), miSettings, miReload, new MenuItem("-"), miAdmin, new MenuItem("-"), miExit });
            menu.Popup += delegate { miAutostart.Checked = IsAutostartEnabled(); };

            tray = new NotifyIcon();
            tray.ContextMenu = menu;
            tray.Text = "ImeCursor";
            tray.DoubleClick += delegate { OnToggleShow(null, EventArgs.Empty); };
            SetTrayIcon(NeutralTrayText, cfg.ColorKeyboard);   // neutral app icon until the first detection
            tray.Visible = true;

            detectThread = new Thread(DetectLoop);
            detectThread.Name = "ImeCursor.Detect";
            detectThread.IsBackground = true;
            detectThread.Start();

            caretThread = new Thread(CaretLoop);
            caretThread.Name = "ImeCursor.Caret";
            caretThread.IsBackground = true;
            caretThread.SetApartmentState(ApartmentState.MTA);   // MSAA calls into other processes
            caretThread.Start();

            followTimer = new System.Windows.Forms.Timer();
            followTimer.Interval = cfg.FollowMs;
            followTimer.Tick += OnFollowTick;
            followTimer.Start();

            // Pointer movement wakes the UI thread immediately (out-of-context WinEvents are posted to this
            // thread's queue; they never delay the input itself). The timer remains as a fallback.
            cursorHookProc = OnCursorEvent;
            try
            {
                cursorHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero, cursorHookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            }
            catch (Exception ex) { Log.Write("cursor hook: " + ex.Message); }
            if (cursorHook == IntPtr.Zero) Log.Write("cursor hook not installed; following uses the timer only");
        }

        private static void AllowTaskbarCreated()
        {
            try
            {
                uint msg = Native.RegisterWindowMessage("TaskbarCreated");
                if (msg != 0 && !Native.ChangeWindowMessageFilter(msg, Native.MSGFLT_ADD))
                    Log.Write("ChangeWindowMessageFilter(TaskbarCreated) failed");
            }
            catch (Exception ex) { Log.Write("TaskbarCreated filter: " + ex.Message); }
        }

        // ------------------------------------------------------------------ detection (background thread)

        private void DetectLoop()
        {
            while (true)
            {
                Config c = cfg;
                long t0 = profile ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    if (reloadCatalog) { reloadCatalog = false; detector.ReloadCatalog(); }
                    else detector.MaybeRefreshCatalog();
                    RawSample s = detector.Sample(c, c.HideInFullscreen, false);
                    detector.Stabilize(s);
                    // own process in front (tray menu open) or no foreground window: keep the last state
                    if (!s.OwnProcess && s.Foreground != IntPtr.Zero)
                    {
                        Decision d = detector.Decide(s, c);   // same object while nothing changed
                        if (Detector.ShouldPublish(s, d) && !ReferenceEquals(d, latest))
                        {
                            latest = d;
                            overlay.PostWake();               // non-blocking; the UI thread shows it right away
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLimited("detect", ex.ToString());
                }
                if (profile) { Interlocked.Add(ref detectTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref detectCount); }
                if (stopEvent.WaitOne(c.PollMs)) break;
            }
        }

        // ------------------------------------------------------------------ caret (background thread)

        private const int CaretIdleMs = 250;       // poll rate once the caret has not moved for a second (MSAA queries cost CPU)

        private void CaretLoop()
        {
            int lastSeenTick = 0, lastChangeTick = 0;
            bool cheap = false;                        // last caret came from the cheap Win32 path
            WaitHandle[] waits = new WaitHandle[] { stopEvent, caretWake };
            while (true)
            {
                Config c = cfg;
                try
                {
                    if (!c.AnchorCaret)
                    {
                        if (caret != null) { caret = null; overlay.PostWake(); }
                    }
                    else
                    {
                        IntPtr fg = Native.GetForegroundWindow();
                        uint pid;
                        Native.GetWindowThreadProcessId(fg, out pid);
                        if (fg != IntPtr.Zero && pid != ownPid)          // own tray menu in front: keep the last caret
                        {
                            long t0 = profile ? Stopwatch.GetTimestamp() : 0;
                            CaretInfo ci = CaretLocator.Locate(fg);
                            if (profile) { Interlocked.Add(ref caretTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref caretCount); }
                            CaretInfo prev = caret;
                            int now = Environment.TickCount;
                            cheap = ci != null && ci.Source == "win32";
                            if (ci != null) lastSeenTick = now;
                            else if (prev != null && prev.Foreground == fg && unchecked(now - lastSeenTick) < CaretGraceMs)
                                ci = prev;                               // caret hidden for a moment (e.g. while selecting)
                            if (ci == null ? prev != null : !ci.SameAs(prev))
                            {
                                caret = ci;
                                lastChangeTick = now;
                                overlay.PostWake();
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.WriteLimited("caret", ex.ToString()); }
                // CaretMs while the caret moves (or when it is a cheap Win32 caret); slower when it is still or absent.
                // Caret-move WinEvents wake this thread immediately in either case.
                int wait = 500;
                if (c.AnchorCaret)
                    wait = cheap || unchecked(Environment.TickCount - lastChangeTick) < 1000 ? c.CaretMs : Math.Max(c.CaretMs, CaretIdleMs);
                if (WaitHandle.WaitAny(waits, wait) == 0) break;
            }
        }

        // ------------------------------------------------------------------ following (UI thread)

        private void OnFollowTick(object sender, EventArgs e)
        {
            RunFollow();
        }

        private void OnWake()
        {
            RunFollow();
        }

        private void OnCursorEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (exiting) return;
            if (idObject == Native.OBJID_CARET)                                 // text cursor moved: re-read it now
            {
                if (profile) caretEventCount++;
                caretWake.Set();
                return;
            }
            if (idObject != Native.OBJID_CURSOR) return;
            cursorEventsSeen = true;
            if (profile) cursorEventCount++;
            // coalesce bursts (high-rate mice): the next event or the FollowMs timer picks up the final position
            if ((Stopwatch.GetTimestamp() - lastFollowTs) * 1000.0 / Stopwatch.Frequency < MinEventGapMs) return;
            RunFollow();
        }

        private void RunFollow()
        {
            // WinEvents and the wake message can be dispatched while this thread waits inside a synchronous
            // call (e.g. Shell_NotifyIcon in UpdateTray): never nest Follow.
            if (exiting) return;
            if (inFollow) { followAgain = true; return; }   // run once more when the current pass ends
            long t0 = Stopwatch.GetTimestamp();
            lastFollowTs = t0;
            inFollow = true;
            try
            {
                Follow();
                if (followAgain) { followAgain = false; Follow(); }
            }
            catch (Exception ex) { Log.WriteLimited("follow", ex.ToString()); }
            finally { inFollow = false; followAgain = false; }
            if (profile)
            {
                followTicks += Stopwatch.GetTimestamp() - t0;
                followCount++;
                if (unchecked(Environment.TickCount - profileTick) > 10000)
                {
                    profileTick = Environment.TickCount;
                    Log.Write(string.Format("profile: follow {0} calls {1:0.0} ms total; cursor events {2}; timer {3} ms; detect {4} polls {5:0.0} ms total",
                        followCount, followTicks * 1000.0 / Stopwatch.Frequency, cursorEventCount, followTimer.Interval,
                        Interlocked.Read(ref detectCount), Interlocked.Read(ref detectTicks) * 1000.0 / Stopwatch.Frequency));
                    Log.Write(string.Format("profile: caret {0} queries {1:0.0} ms total; caret events {2}",
                        Interlocked.Read(ref caretCount), Interlocked.Read(ref caretTicks) * 1000.0 / Stopwatch.Frequency, caretEventCount));
                    Interlocked.Exchange(ref caretTicks, 0); Interlocked.Exchange(ref caretCount, 0); caretEventCount = 0;
                    followTicks = 0; followCount = 0; cursorEventCount = 0;
                    Interlocked.Exchange(ref detectTicks, 0); Interlocked.Exchange(ref detectCount, 0);
                }
            }
        }

        private void Follow()
        {
            Decision d = latest;
            FollowOverlay(cfg, d);
            // the tray (a synchronous call into Explorer) is updated only after the badge has been repainted
            if (trayDirty && d != null)
            {
                trayDirty = false;
                UpdateTray(d);
            }
        }

        private void FollowOverlay(Config c, Decision d)
        {
            int now = Environment.TickCount;
            if (d != null && d.Key != shownKey)
            {
                bool first = shownKey == null;
                shownKey = d.Key;
                trayDirty = true;
                if (c.FlashOnChange && !first && unchecked(now - noFlashUntil) >= 0) { flashUntil = now + c.FlashMs; flashing = true; }
                forceLayout = true;
            }
            if (flashing && unchecked(now - flashUntil) >= 0) { flashing = false; forceLayout = true; }

            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return;

            // adaptive timer: FollowMs while the pointer moves, a slower idle rate after 1 s without movement
            if (pt.X != seenPt.X || pt.Y != seenPt.Y)
            {
                seenPt = pt;
                lastMoveTick = now;
                if (followTimer.Interval != c.FollowMs) followTimer.Interval = c.FollowMs;
            }
            else if (unchecked(now - lastMoveTick) > 1000)
            {
                int idle = Math.Max(c.FollowMs, cursorEventsSeen ? IdleFollowHookedMs : IdleFollowMs);
                if (followTimer.Interval != idle) followTimer.Interval = idle;
            }

            // Anchor: the text cursor's rectangle, or the pointer (as an empty rectangle) when there is no caret.
            CaretInfo tc = c.AnchorCaret ? caret : null;
            bool useCaret = tc != null;
            Native.RECT anchor;
            if (useCaret) anchor = tc.Rect;
            else { anchor = new Native.RECT(); anchor.Left = anchor.Right = pt.X; anchor.Top = anchor.Bottom = pt.Y; }

            bool hide = !enabled || d == null;
            if (!hide && c.AnchorCaret && !useCaret && c.NoCaretHide) hide = true;
            if (!hide && c.HideInFullscreen && d.Fullscreen && d.FullscreenMonitor.Contains(anchor.Right, anchor.Bottom)) hide = true;
            if (!hide && !useCaret && c.HideWhenCursorHidden)
            {
                Native.CURSORINFO ci = new Native.CURSORINFO();
                ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.CURSORINFO));
                if (Native.GetCursorInfo(ref ci) && (ci.flags & Native.CURSOR_SHOWING) == 0) hide = true;
            }
            if (hide)
            {
                overlay.Hide();
                forceLayout = true;
                return;
            }

            if (!forceLayout && useCaret == lastAnchorCaret && anchor.Left == lastAnchor.Left && anchor.Top == lastAnchor.Top &&
                anchor.Right == lastAnchor.Right && anchor.Bottom == lastAnchor.Bottom && overlay.IsShown)
            {
                overlay.Place(overlayX, overlayY);   // cheap: only re-asserts topmost about once a second
                return;
            }
            lastAnchor = anchor;
            lastAnchorCaret = useCaret;
            forceLayout = false;

            IntPtr mon = Native.MonitorFromPoint(new Native.POINT(anchor.Right, anchor.Bottom), Native.MONITOR_DEFAULTTONEAREST);
            int dpi = Native.DpiOfMonitor(mon);
            Native.RECT mr = Native.MonitorRect(mon);
            float scale = dpi / 96f;
            float extra = flashing ? c.FlashScale : 1f;
            string baseKey = d.Key + "|" + dpi + "|" + c.FontName + "|" + c.FontSize + "|" + c.ColorText.ToArgb();
            string key = baseKey + "|" + (flashing ? "F" : "N");
            if (!overlay.SetContent(key, d.BadgeText, c.FontName, c.FontSize, scale * extra, d.Back, c.ColorText, c.Opacity)) return;
            int w = overlay.Width, h = overlay.Height;

            // The side of the pointer is decided with the normal badge size, so the flash enlargement
            // never throws the badge to the other side for its duration.
            if (!flashing) { normalKey = baseKey; normalW = w; normalH = h; }
            else if (normalKey != baseKey)
            {
                BadgeLayout nl = BadgeRenderer.Layout(d.BadgeText, c.FontName, c.FontSize, scale);
                normalKey = baseKey; normalW = nl.Width; normalH = nl.Height;
            }

            // Below-right of the anchor; near the right / bottom monitor edge, flip to the left of / above it.
            int ox, oy, gapX, gapY;
            if (useCaret)
            {
                CaretOffsets(c, dpi, out ox, out oy);
                gapX = ox; gapY = oy;
            }
            else
            {
                Offsets(c, dpi, out ox, out oy);
                gapX = Math.Max(2, ox / 3); gapY = Math.Max(2, oy / 4);
            }
            bool flipX = anchor.Right + ox + normalW > mr.Right;
            bool flipY = anchor.Bottom + oy + normalH > mr.Bottom;
            int nx = flipX ? anchor.Left - w - gapX : anchor.Right + ox;
            int ny = flipY ? anchor.Top - h - gapY : anchor.Bottom + oy;
            if (nx + w > mr.Right) nx = mr.Right - w;                      // enlarged (flash) badge: clamp
            if (ny + h > mr.Bottom) ny = mr.Bottom - h;
            if (nx < mr.Left) nx = mr.Left;
            if (ny < mr.Top) ny = mr.Top;
            overlayX = nx;
            overlayY = ny;
            overlay.Place(nx, ny);
        }

        private void Offsets(Config c, int dpi, out int ox, out int oy)
        {
            float s = dpi / 96f;
            if (c.OffsetX != Config.Auto && c.OffsetY != Config.Auto)
            {
                ox = (int)Math.Round(c.OffsetX * s);
                oy = (int)Math.Round(c.OffsetY * s);
                return;
            }
            float cursorPx = CursorPx(dpi);
            ox = c.OffsetX != Config.Auto ? (int)Math.Round(c.OffsetX * s) : (int)Math.Round(cursorPx * Config.AutoOffsetX);
            oy = c.OffsetY != Config.Auto ? (int)Math.Round(c.OffsetY * s) : (int)Math.Round(cursorPx * Config.AutoOffsetY);
        }

        /// <summary>Gap between the text cursor's bottom-right corner and the badge (auto = 2 logical px).</summary>
        private static void CaretOffsets(Config c, int dpi, out int ox, out int oy)
        {
            float s = dpi / 96f;
            ox = (int)Math.Round((c.OffsetX != Config.Auto ? c.OffsetX : 2) * s);
            oy = (int)Math.Round((c.OffsetY != Config.Auto ? c.OffsetY : 2) * s);
        }

        /// <summary>Displayed pointer size in physical px on a monitor with the given DPI:
        /// CursorBaseSize (32 = pointer size 1, 48 = size 2, ...) scaled by DPI/96. Windows draws the cursor
        /// image of that size (e.g. the 72 px image of a size-2 pointer at 150%). Re-read every 5 s.</summary>
        private float CursorPx(int dpi)
        {
            int now = Environment.TickCount;
            if (cursorBase < 0 || unchecked(now - cursorBaseTick) >= 5000)
            {
                cursorBaseTick = now;
                int v = 32;
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors"))
                    {
                        if (k != null)
                        {
                            object o = k.GetValue("CursorBaseSize");
                            if (o is int && (int)o >= 16 && (int)o <= 512) v = (int)o;
                        }
                    }
                }
                catch (Exception) { }
                if (cursorBase >= 0 && v != cursorBase) forceLayout = true;
                cursorBase = v;
            }
            return cursorBase * dpi / 96f;
        }

        // ------------------------------------------------------------------ tray

        private void UpdateTray(Decision d)
        {
            Config c = cfg;
            string tip = d.Tooltip + (enabled ? "" : " (badge hidden)");
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tip != trayTip) { tray.Text = tip; trayTip = tip; }
            if (c.TrayShowsState) SetTrayIcon(d.TrayText, d.Back);
        }

        private void SetTrayIcon(string text, Color back)
        {
            Config c = cfg;
            string key = text + "|" + back.ToArgb() + "|" + c.FontName + "|" + c.ColorText.ToArgb();
            if (key == trayIconKey) return;    // unchanged: no call into Explorer
            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;
            IntPtr h = BadgeRenderer.RenderTrayIcon(text, size, c.FontName, back, c.ColorText);
            Icon icon = Icon.FromHandle(h);
            tray.Icon = icon;                  // the shell keeps its own copy
            if (trayIcon != null) trayIcon.Dispose();
            if (trayIconHandle != IntPtr.Zero) Native.DestroyIcon(trayIconHandle);
            trayIcon = icon;
            trayIconHandle = h;
            trayIconKey = key;
        }

        private void OnToggleShow(object sender, EventArgs e)
        {
            enabled = !enabled;
            miShow.Checked = enabled;
            if (!enabled) overlay.Hide();
            forceLayout = true;
            Decision d = latest;
            if (d != null) UpdateTray(d);
        }

        // ------------------------------------------------------------------ autostart

        private static string ExePath()
        {
            return Application.ExecutablePath;
        }

        /// <summary>True when the Run value points to this exe and the user has not disabled it in
        /// Settings > Apps > Startup / Task Manager (StartupApproved, odd first byte = disabled).</summary>
        private static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RunValue) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    if (!string.Equals(v.Trim().Trim('"'), ExePath(), StringComparison.OrdinalIgnoreCase)) return false;
                }
                using (RegistryKey a = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    byte[] b = a != null ? a.GetValue(RunValue) as byte[] : null;
                    if (b != null && b.Length > 0 && (b[0] & 1) != 0) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Marks the Run entry as enabled in Settings > Apps > Startup (first byte 2 = enabled, as Task
        /// Manager writes it). Recent Windows 11 builds do not run a Run entry that has no approval value at all.</summary>
        private static void MarkStartupApproved()
        {
            using (RegistryKey a = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                a.SetValue(RunValue, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        private static void ClearStartupApproved()
        {
            using (RegistryKey a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            {
                if (a != null) a.DeleteValue(RunValue, false);
            }
        }

        /// <summary>The exe should not be registered for autostart from a folder that gets deleted.</summary>
        private static bool LooksTemporary(string path)
        {
            try
            {
                string temp = Path.GetFullPath(Path.GetTempPath());
                if (path.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) { }
            return path.IndexOf(@"\scratch-workspaces\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnToggleAutostart(object sender, EventArgs e)
        {
            try
            {
                if (IsAutostartEnabled())
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                        k.DeleteValue(RunValue, false);
                    ClearStartupApproved();
                }
                else
                {
                    string exe = ExePath();
                    if (LooksTemporary(exe))
                    {
                        DialogResult r = MessageBox.Show(
                            "ImeCursor is running from a temporary folder:\n" + Path.GetDirectoryName(exe) + "\n\n" +
                            "That folder may be deleted, and autostart would then silently stop working. " +
                            "Copy the program to a permanent folder first (run install.cmd, or copy ImeCursor.exe and " +
                            "ImeCursor.ini to e.g. %LOCALAPPDATA%\\Programs\\ImeCursor) and enable autostart from there.\n\n" +
                            "Enable autostart from this folder anyway?",
                            "ImeCursor", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (r != DialogResult.Yes) return;
                    }
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                        k.SetValue(RunValue, "\"" + exe + "\"", RegistryValueKind.String);
                    MarkStartupApproved();    // Explorer skips Run entries that are not approved (see MarkStartupApproved)
                }
            }
            catch (Exception ex)
            {
                Log.Write("autostart: " + ex.Message);
                MessageBox.Show("Could not change the autostart setting:\n" + ex.Message, "ImeCursor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            miAutostart.Checked = IsAutostartEnabled();
        }

        // ------------------------------------------------------------------ settings

        private void OnOpenSettings(object sender, EventArgs e)
        {
            try
            {
                string path = cfg.FilePath;
                if (!File.Exists(path)) Config.Load(path);   // writes the default file
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { Log.Write("open settings: " + ex.Message); }
        }

        private void OnReload(object sender, EventArgs e)
        {
            Config c = Config.Load(cfg.FilePath);
            foreach (string w in c.Warnings) Log.Write("config: " + w);
            if (c.ReadFailed)
            {
                // e.g. locked by an editor or sync client: keep the current settings instead of reverting to defaults
                tray.ShowBalloonTip(4000, "ImeCursor", "Could not read ImeCursor.ini - settings unchanged. See ImeCursor.log.", ToolTipIcon.Warning);
                return;
            }
            cfg = c;
            reloadCatalog = true;
            followTimer.Interval = c.FollowMs;
            shownKey = null;            // refresh tray + badge ...
            noFlashUntil = Environment.TickCount + 3 * c.PollMs + 100;   // ... without flashing when the detector recomputes with the new settings
            trayIconKey = null;
            overlay.Invalidate();
            forceLayout = true;
            if (!c.TrayShowsState) SetTrayIcon(NeutralTrayText, c.ColorKeyboard);
            if (c.Warnings.Count > 0)
                tray.ShowBalloonTip(4000, "ImeCursor", c.Warnings.Count + " setting(s) ignored or adjusted - see ImeCursor.log", ToolTipIcon.Warning);
        }

        // ------------------------------------------------------------------ elevation / exit

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        private void OnRestartAdmin(object sender, EventArgs e)
        {
            DialogResult r = MessageBox.Show(
                "Windows does not let a normal program read the input mode of windows that run as administrator " +
                "(e.g. an elevated terminal or Task Manager); the badge shows \"?\" there.\n\n" +
                "Restart ImeCursor as administrator?", "ImeCursor", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ExePath(), "--restarted");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) Log.Write("runas: " + ex.Message);   // 1223 = cancelled by user
                return;
            }
            ExitApp();
        }

        private void OnExit(object sender, EventArgs e)
        {
            ExitApp();
        }

        public void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            try
            {
                if (cursorHook != IntPtr.Zero) { Native.UnhookWinEvent(cursorHook); cursorHook = IntPtr.Zero; }
                followTimer.Stop();
                followTimer.Dispose();
                stopEvent.Set();
                detectThread.Join(1000);
                caretThread.Join(1000);
                overlay.Hide();
                overlay.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                if (trayIcon != null) trayIcon.Dispose();
                if (trayIconHandle != IntPtr.Zero) { Native.DestroyIcon(trayIconHandle); trayIconHandle = IntPtr.Zero; }
            }
            catch (Exception ex) { Log.Write("exit: " + ex); }
            if (onExit != null) onExit();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting) ExitApp();
            base.Dispose(disposing);
        }
    }
}
