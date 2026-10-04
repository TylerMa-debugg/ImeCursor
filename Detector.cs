// ImeCursor - input method detection (foreground app) and decision rules.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ImeCursor
{
    internal enum Mode { Keyboard, Native, Alpha, Caps, Unknown }

    /// <summary>One raw reading of the foreground input state.</summary>
    internal sealed class RawSample
    {
        public IntPtr Foreground;
        public uint Pid;
        public IntPtr ImeWnd;      // IME window that was queried (identifies the input thread)
        public uint Hkl;
        public int Open = -1;
        public int Conv = -1;
        public bool ImeLang;       // the language has an IME rule (not a plain keyboard)
        public bool QueryOk;
        public bool Bridged;       // query failed; last good reading of the same window re-used
        public bool Skipped;       // keyboard layout: IME not queried
        public bool Defer;         // query failed on a new window; a resulting "unknown" is not published yet
        public bool Hold;          // first reading of a newly focused window; its Chinese/English mode is not published yet
        public bool Caps;
        public bool OwnProcess;
        public bool Fullscreen;
        public Native.RECT FullscreenMonitor;
    }

    /// <summary>What the badge should show. Immutable once published.</summary>
    internal sealed class Decision
    {
        public Mode Mode;
        public int Lang;
        public bool IsIme;
        public string Name;        // short IME name ("微信") or keyboard label ("EN")
        public string Glyph;       // 中 / 英 / A / ? / EN
        public string BadgeText;
        public string TrayText;
        public Color Back;
        public string Tooltip;
        public bool Fullscreen;
        public Native.RECT FullscreenMonitor;
        public string Key;         // identity of the visual state

        public static string ModeName(Mode m)
        {
            switch (m)
            {
                case Mode.Native: return "NATIVE";
                case Mode.Alpha: return "ALPHA";
                case Mode.Caps: return "CAPS";
                case Mode.Unknown: return "UNKNOWN";
                default: return "KEYBOARD";
            }
        }
    }

    /// <summary>A text input processor (TSF input method) enabled by the user.</summary>
    internal sealed class TipInfo
    {
        public int Lang;
        public Guid Clsid;
        public string Description = "";
        public string DisplayName = "";

        public static readonly Guid WeTypeClsid = new Guid("86598FB9-66A2-463E-B9C2-AEB906D477AD");
        public static readonly Guid MsPinyinClsid = new Guid("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E");

        public bool IsWeType
        {
            get
            {
                return Clsid == WeTypeClsid || Has("WeType") || Has("微信");   // 微信
            }
        }

        public bool Has(string s)
        {
            return Description.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   DisplayName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string ShortName()
        {
            if (IsWeType) return "微信";                                                       // 微信
            if (Clsid == MsPinyinClsid || Has("Microsoft Pinyin") || Has("微软拼音")) return "微软"; // 微软
            if (Has("Sogou") || Has("搜狗")) return "搜狗";                           // 搜狗
            if (Has("QQ")) return "QQ";
            if (Has("Baidu") || Has("百度")) return "百度";                           // 百度
            if (Has("Microsoft Wubi")) return "五笔";                                        // 五笔
            if (Has("Microsoft Bopomofo")) return "注音";                                    // 注音
            if (Has("Microsoft IME")) return "MS";
            string d = Description.Length > 0 ? Description : DisplayName;
            d = d.Trim();
            if (d.Length == 0) return "IME";
            return d.Length <= 2 ? d : d.Substring(0, 2);
        }

        public string FullName()
        {
            if (DisplayName.Length > 0 && Description.Length > 0 && !DisplayName.Equals(Description, StringComparison.OrdinalIgnoreCase))
                return Description + " (" + DisplayName + ")";
            return Description.Length > 0 ? Description : DisplayName;
        }
    }

    /// <summary>Enabled input methods per language, read from the registry.</summary>
    internal sealed class InputMethodCatalog
    {
        private static readonly List<TipInfo> NoTips = new List<TipInfo>();
        private readonly Dictionary<int, List<TipInfo>> byLang = new Dictionary<int, List<TipInfo>>();
        private static readonly Regex TipValue = new Regex(
            @"^([0-9A-Fa-f]{4}):\{([0-9A-Fa-f\-]{36})\}\{([0-9A-Fa-f\-]{36})\}$", RegexOptions.CultureInvariant);

        public List<TipInfo> TipsFor(int lang)
        {
            List<TipInfo> l;
            if (byLang.TryGetValue(lang, out l)) return l;
            return NoTips;
        }

        public static InputMethodCatalog Load()
        {
            InputMethodCatalog c = new InputMethodCatalog();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 1) Windows 8+ language list: HKCU\Control Panel\International\User Profile\<tag>  "0804:{clsid}{profile}"
            try
            {
                using (RegistryKey up = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"))
                {
                    if (up != null)
                    {
                        foreach (string tag in up.GetSubKeyNames())
                        {
                            using (RegistryKey lk = up.OpenSubKey(tag))
                            {
                                if (lk == null) continue;
                                foreach (string vn in lk.GetValueNames())
                                {
                                    Match m = TipValue.Match(vn);
                                    if (!m.Success) continue;
                                    c.Add(seen, Convert.ToInt32(m.Groups[1].Value, 16), new Guid(m.Groups[2].Value), new Guid(m.Groups[3].Value));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("catalog(User Profile): " + ex.Message); }
            // 2) Fallback: CTF sort order (older systems / migrated profiles)
            try
            {
                using (RegistryKey ai = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\CTF\SortOrder\AssemblyItem"))
                {
                    if (ai != null)
                    {
                        foreach (string langKey in ai.GetSubKeyNames())
                        {
                            int lang = Config.ParseLang(langKey);
                            if (lang <= 0 || c.byLang.ContainsKey(lang)) continue;
                            using (RegistryKey cat = ai.OpenSubKey(langKey + @"\{34745C63-B2F0-4784-8B67-5E12C8701A31}"))
                            {
                                if (cat == null) continue;
                                foreach (string item in cat.GetSubKeyNames())
                                {
                                    using (RegistryKey it = cat.OpenSubKey(item))
                                    {
                                        if (it == null) continue;
                                        Guid clsid, prof;
                                        if (!TryGuid(it.GetValue("CLSID") as string, out clsid) || clsid == Guid.Empty) continue;
                                        if (!TryGuid(it.GetValue("Profile") as string, out prof)) continue;
                                        c.Add(seen, lang, clsid, prof);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("catalog(SortOrder): " + ex.Message); }
            return c;
        }

        private static bool TryGuid(string s, out Guid g)
        {
            g = Guid.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            try { g = new Guid(s); return true; }
            catch (FormatException) { return false; }
        }

        private void Add(HashSet<string> seen, int lang, Guid clsid, Guid profile)
        {
            string id = lang.ToString("X4") + clsid.ToString("B") + profile.ToString("B");
            if (!seen.Add(id)) return;
            TipInfo t = new TipInfo();
            t.Lang = lang;
            t.Clsid = clsid;
            string sub = @"SOFTWARE\Microsoft\CTF\TIP\" + clsid.ToString("B") + @"\LanguageProfile\0x" + lang.ToString("X8") + @"\" + profile.ToString("B");
            ReadDescription(Registry.LocalMachine, sub, t);
            if (t.Description.Length == 0) ReadDescription(Registry.CurrentUser, sub, t);
            List<TipInfo> l;
            if (!byLang.TryGetValue(lang, out l)) { l = new List<TipInfo>(); byLang[lang] = l; }
            l.Add(t);
        }

        private static void ReadDescription(RegistryKey root, string sub, TipInfo t)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    string d = k.GetValue("Description") as string;
                    if (!string.IsNullOrEmpty(d)) t.Description = d;
                    string dd = k.GetValue("Display Description") as string;
                    if (!string.IsNullOrEmpty(dd)) t.DisplayName = ResolveIndirect(dd);
                }
            }
            catch (Exception) { }
        }

        private static string ResolveIndirect(string s)
        {
            if (!s.StartsWith("@", StringComparison.Ordinal)) return s;
            try
            {
                StringBuilder sb = new StringBuilder(512);
                if (Native.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0) return sb.ToString();
            }
            catch (Exception) { }
            return "";
        }
    }

    /// <summary>Reads the foreground input state and turns it into a badge decision.</summary>
    internal sealed class Detector
    {
        private static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;
        private InputMethodCatalog catalog;
        private int catalogTick;
        private const uint QueryTimeoutMs = 50;

        public Detector()
        {
            ReloadCatalog();
        }

        public void ReloadCatalog()
        {
            catalog = InputMethodCatalog.Load();
            catalogTick = Environment.TickCount;
        }

        /// <summary>Refreshes the input-method list every 30 s (cheap registry reads).</summary>
        public void MaybeRefreshCatalog()
        {
            if (unchecked(Environment.TickCount - catalogTick) > 30000) ReloadCatalog();
        }

        // ------------------------------------------------------------------ sampling

        /// <summary>Rule actually applied for a language: null = plain keyboard, else open | open+conv | conv.</summary>
        public string ResolveRule(int lang, Config cfg)
        {
            string rule = cfg.RuleFor(lang);
            if (rule == "auto") rule = AutoRule(catalog.TipsFor(lang));
            return rule;
        }

        /// <summary>Reads the foreground input state. Only queries what the language's rule needs
        /// (nothing for keyboard layouts, open status only for "open") unless queryAll is set.</summary>
        public RawSample Sample(Config cfg, bool checkFullscreen, bool queryAll)
        {
            RawSample s = new RawSample();
            s.Caps = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;
            IntPtr fg = Native.GetForegroundWindow();
            s.Foreground = fg;
            if (fg == IntPtr.Zero) return s;

            uint pid;
            uint tid = Native.GetWindowThreadProcessId(fg, out pid);
            s.Pid = pid;
            if (pid == OwnPid) { s.OwnProcess = true; return s; }

            // UWP: the frame window belongs to ApplicationFrameHost; input happens in the CoreWindow child.
            IntPtr inputTop = fg;
            string fgClass = Native.ClassNameOf(fg);
            if (fgClass == "ApplicationFrameWindow")
            {
                IntPtr core = Native.FindWindowEx(fg, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero)
                {
                    uint cpid;
                    uint ctid = Native.GetWindowThreadProcessId(core, out cpid);
                    if (ctid != 0) { tid = ctid; inputTop = core; }
                }
            }
            // Classic console (conhost): GetWindowThreadProcessId reports the console client (cmd.exe), whose
            // thread has no keyboard layout (HKL 0) and no GUI info. The thread that owns the window's IME
            // window is conhost's real input thread, with the correct layout and open status.
            IntPtr hkl = Native.GetKeyboardLayout(tid);
            if (fgClass == "ConsoleWindowClass" || hkl == IntPtr.Zero)
            {
                IntPtr topIme = Native.ImmGetDefaultIMEWnd(fg);
                uint ipid;
                uint itid = topIme != IntPtr.Zero ? Native.GetWindowThreadProcessId(topIme, out ipid) : 0;
                if (itid != 0 && itid != tid) { tid = itid; hkl = Native.GetKeyboardLayout(tid); }
            }
            s.Hkl = unchecked((uint)hkl.ToInt64());

            Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            IntPtr focus = IntPtr.Zero;
            if (Native.GetGUIThreadInfo(tid, ref gti)) focus = gti.hwndFocus;
            if (focus == IntPtr.Zero) focus = inputTop;

            string rule = ResolveRule((int)(s.Hkl & 0xFFFF), cfg);
            s.ImeLang = rule != null;
            if (rule == null && !queryAll)
            {
                s.Skipped = true;   // keyboard layout: no IME query needed
                if (checkFullscreen) CheckFullscreen(fg, fgClass, s);
                return s;
            }
            bool needConv = queryAll || rule != "open";

            // Prefer the focus window: right after a focus change the top-level's IME window can report stale values.
            IntPtr ime = Native.ImmGetDefaultIMEWnd(focus);
            if (ime == IntPtr.Zero && focus != inputTop) ime = Native.ImmGetDefaultIMEWnd(inputTop);
            s.ImeWnd = ime;
            if (ime != IntPtr.Zero)
            {
                IntPtr r;
                if (Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, (IntPtr)Native.IMC_GETOPENSTATUS, IntPtr.Zero,
                        Native.SMTO_ABORTIFHUNG, QueryTimeoutMs, out r) != IntPtr.Zero)
                {
                    s.Open = unchecked((int)r.ToInt64());
                    if (!needConv) s.QueryOk = true;
                    else if (Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, (IntPtr)Native.IMC_GETCONVERSIONMODE, IntPtr.Zero,
                            Native.SMTO_ABORTIFHUNG, QueryTimeoutMs, out r) != IntPtr.Zero)
                    {
                        s.Conv = unchecked((int)r.ToInt64());
                        s.QueryOk = true;
                    }
                }
                // failure: timeout, hung, or UIPI (elevated target) -> mode unknown
            }

            if (checkFullscreen) CheckFullscreen(fg, fgClass, s);
            return s;
        }

        private static void CheckFullscreen(IntPtr fg, string cls, RawSample s)
        {
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return;
            // A maximized window with a caption/frame is larger than the work area by its resize border, so it
            // "covers" the monitor when the work area equals the monitor (auto-hide taskbar, monitor without a
            // taskbar). Real full-screen windows (F11 browsers, video players, borderless games) drop the caption.
            int style = Native.GetWindowLong(fg, Native.GWL_STYLE);
            if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return;
            if (Native.IsZoomed(fg) && (style & Native.WS_THICKFRAME) != 0) return;
            Native.RECT wr;
            if (!Native.GetWindowRect(fg, out wr)) return;
            Native.RECT mr = Native.MonitorRect(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST));
            if (wr.Left <= mr.Left && wr.Top <= mr.Top && wr.Right >= mr.Right && wr.Bottom >= mr.Bottom)
            {
                s.Fullscreen = true;
                s.FullscreenMonitor = mr;
            }
        }

        // ------------------------------------------------------------------ debounce

        private IntPtr goodFg;
        private uint goodHkl;
        private int goodOpen, goodConv;
        private int failStreak;
        private const int MaxBridgedFailures = 3;   // ~240 ms at the default PollMs

        // window switch confirmation: the input window whose IME reading is trusted, and the pending reading of a new one
        private IntPtr confFg, confIme;
        private IntPtr pendFg, pendIme;
        private uint pendHkl;
        private int pendOpen, pendConv, pendCount;
        private const int MaxHolds = 4;             // a window whose reading keeps changing is published anyway

        /// <summary>A single failed query (seen briefly while a window is being focused or busy) re-uses
        /// the last good reading of the same window instead of flickering "?". Persistent failures
        /// (elevated target, hung app) still become Unknown after a few polls.
        /// The first reading of a newly focused input window is often stale (a new window reports open=0 for
        /// about one poll before WeType activates), so after a window switch the Chinese/English mode is only
        /// published once two consecutive polls agree (Hold). Caps Lock and keyboard layouts are not held.</summary>
        public void Stabilize(RawSample s)
        {
            if (s.OwnProcess || s.Foreground == IntPtr.Zero) return;
            if (s.Skipped || !s.ImeLang)
            {
                if (s.Skipped) { failStreak = 0; goodFg = IntPtr.Zero; }
                confFg = s.Foreground; confIme = s.ImeWnd; pendFg = IntPtr.Zero; pendCount = 0;
                if (s.Skipped) return;
            }
            if (s.QueryOk)
            {
                goodFg = s.Foreground; goodHkl = s.Hkl; goodOpen = s.Open; goodConv = s.Conv;
                failStreak = 0;
                if (s.ImeLang && (s.Foreground != confFg || s.ImeWnd != confIme))
                {
                    bool sameWindow = pendFg == s.Foreground && pendIme == s.ImeWnd;
                    bool confirmed = sameWindow && pendHkl == s.Hkl && pendOpen == s.Open && pendConv == s.Conv;
                    if (confirmed || (sameWindow && pendCount >= MaxHolds))
                    {
                        confFg = s.Foreground; confIme = s.ImeWnd; pendFg = IntPtr.Zero; pendCount = 0;
                    }
                    else
                    {
                        if (!sameWindow) pendCount = 0;
                        pendFg = s.Foreground; pendIme = s.ImeWnd; pendHkl = s.Hkl; pendOpen = s.Open; pendConv = s.Conv;
                        pendCount++;
                        s.Hold = true;
                    }
                }
                return;
            }
            failStreak++;
            if (failStreak <= MaxBridgedFailures && s.Foreground == goodFg && s.Hkl == goodHkl && goodFg != IntPtr.Zero)
            {
                s.Open = goodOpen;
                s.Conv = goodConv;
                s.QueryOk = true;
                s.Bridged = true;
            }
            else if (failStreak <= MaxBridgedFailures)
            {
                s.Defer = true;   // new window/layout not answering yet: keep showing the last state briefly
            }
        }

        /// <summary>False when the decision is transient and should not replace the current badge: an "unknown"
        /// from a window that is not answering yet, or an unconfirmed Chinese/English mode of a new window.</summary>
        public static bool ShouldPublish(RawSample s, Decision d)
        {
            if (s.Defer && d.Mode == Mode.Unknown) return false;
            if (s.Hold && (d.Mode == Mode.Native || d.Mode == Mode.Alpha)) return false;
            return true;
        }

        // ------------------------------------------------------------------ decision

        // last decision, re-used while the inputs are unchanged (no allocations in the steady state)
        private Decision lastDecision;
        private Config lastCfg;
        private InputMethodCatalog lastCatalog;
        private uint lastHkl;
        private int lastOpen, lastConv;
        private bool lastOk, lastCaps, lastFs;
        private Native.RECT lastFsMon;

        public Decision Decide(RawSample s, Config cfg)
        {
            if (lastDecision != null && cfg == lastCfg && catalog == lastCatalog && s.Hkl == lastHkl &&
                s.Open == lastOpen && s.Conv == lastConv && s.QueryOk == lastOk && s.Caps == lastCaps &&
                s.Fullscreen == lastFs && s.FullscreenMonitor.Left == lastFsMon.Left && s.FullscreenMonitor.Top == lastFsMon.Top &&
                s.FullscreenMonitor.Right == lastFsMon.Right && s.FullscreenMonitor.Bottom == lastFsMon.Bottom)
                return lastDecision;
            Decision d = Compute(s, cfg);
            lastDecision = d; lastCfg = cfg; lastCatalog = catalog; lastHkl = s.Hkl; lastOpen = s.Open; lastConv = s.Conv;
            lastOk = s.QueryOk; lastCaps = s.Caps; lastFs = s.Fullscreen; lastFsMon = s.FullscreenMonitor;
            return d;
        }

        private Decision Compute(RawSample s, Config cfg)
        {
            Decision d = new Decision();
            int lang = (int)(s.Hkl & 0xFFFF);
            d.Lang = lang;
            d.Fullscreen = s.Fullscreen;
            d.FullscreenMonitor = s.FullscreenMonitor;
            if (lang == 0)
            {
                // no keyboard layout could be read for the foreground window: never show a bogus "0000" label
                d.Mode = s.Caps ? Mode.Caps : Mode.Unknown;
                d.Glyph = s.Caps ? cfg.TextCaps : cfg.TextUnknown;
                d.Back = s.Caps ? cfg.ColorCaps : cfg.ColorUnknown;
                d.Name = cfg.TextUnknown;
                d.BadgeText = d.Glyph;
                d.TrayText = d.Glyph;
                d.Tooltip = "Input method unknown";
                d.Key = d.BadgeText + "|" + d.Back.ToArgb().ToString("X8");
                return d;
            }
            string rule = cfg.RuleFor(lang);
            d.IsIme = rule != null;
            List<TipInfo> tips = catalog.TipsFor(lang);

            if (!d.IsIme)
            {
                d.Name = KeyboardLabel(lang, cfg);
                d.Mode = s.Caps ? Mode.Caps : Mode.Keyboard;
            }
            else
            {
                d.Name = ImeLabel(lang, tips, cfg);
                if (rule == "auto") rule = AutoRule(tips);
                if (s.Caps) d.Mode = Mode.Caps;                    // Caps Lock types uppercase English in every IME
                else if (!s.QueryOk) d.Mode = Mode.Unknown;
                else
                {
                    bool open = s.Open != 0;
                    bool native = (s.Conv & 1) != 0;              // IME_CMODE_NATIVE
                    bool chinese;
                    if (rule == "open") chinese = open;
                    else if (rule == "conv") chinese = native;
                    else chinese = open && native;                // open+conv
                    d.Mode = chinese ? Mode.Native : Mode.Alpha;
                }
            }

            switch (d.Mode)
            {
                case Mode.Native: d.Glyph = cfg.NativeTextFor(lang); d.Back = cfg.ColorChinese; break;
                case Mode.Alpha: d.Glyph = cfg.TextAlpha; d.Back = cfg.ColorEnglish; break;
                case Mode.Caps: d.Glyph = cfg.TextCaps; d.Back = cfg.ColorCaps; break;
                case Mode.Unknown: d.Glyph = cfg.TextUnknown; d.Back = cfg.ColorUnknown; break;
                default: d.Glyph = d.Name; d.Back = cfg.ColorKeyboard; break;
            }

            if (d.Mode == Mode.Keyboard) d.BadgeText = d.Name;
            else if (cfg.ShowImeName) d.BadgeText = d.Name + " " + d.Glyph;
            else d.BadgeText = d.Glyph;
            d.TrayText = d.Mode == Mode.Keyboard ? d.Name : d.Glyph;
            d.Tooltip = BuildTooltip(d, tips);
            d.Key = d.BadgeText + "|" + d.Back.ToArgb().ToString("X8");
            return d;
        }

        private static string AutoRule(List<TipInfo> tips)
        {
            if (tips.Count == 0) return "open+conv";
            foreach (TipInfo t in tips) if (!t.IsWeType) return "open+conv";
            return "open";
        }

        private static string KeyboardLabel(int lang, Config cfg)
        {
            string l;
            if (cfg.Labels.TryGetValue(lang, out l) && !l.Equals("auto", StringComparison.OrdinalIgnoreCase)) return l;
            if (lang == 0x0409) return "EN";
            try
            {
                string iso = new CultureInfo(lang).TwoLetterISOLanguageName;
                if (!string.IsNullOrEmpty(iso)) return iso.ToUpperInvariant();
            }
            catch (Exception) { }
            return lang.ToString("X4");
        }

        private static string ImeLabel(int lang, List<TipInfo> tips, Config cfg)
        {
            string l;
            if (cfg.Labels.TryGetValue(lang, out l) && !l.Equals("auto", StringComparison.OrdinalIgnoreCase)) return l;
            if (tips.Count == 1) return tips[0].ShortName();
            // none or several enabled: the active one cannot be identified from another process
            if (lang == 0x0411) return "日本";      // 日本
            if (lang == 0x0412) return "한국";      // 한국
            return "中文";                          // 中文
        }

        private static bool IsChineseLang(int lang)
        {
            return (lang & 0x3FF) == 0x04;   // LANG_CHINESE
        }

        private static string BuildTooltip(Decision d, List<TipInfo> tips)
        {
            string who;
            if (!d.IsIme)
            {
                string n;
                try { n = new CultureInfo(d.Lang).DisplayName; }
                catch (Exception) { n = d.Lang.ToString("X4"); }
                who = n + " keyboard";
            }
            else if (tips.Count == 1) who = tips[0].FullName();
            else who = d.Name + " IME";

            bool zh = IsChineseLang(d.Lang);
            string mode;
            switch (d.Mode)
            {
                case Mode.Native: mode = zh ? "中文模式" : "native input"; break;                // 中文模式
                case Mode.Alpha: mode = zh ? "英文模式" : "alphanumeric"; break;                  // 英文模式
                case Mode.Caps: mode = "大写锁定 (Caps Lock)"; break;                             // 大写锁定
                case Mode.Unknown: mode = "模式未知 (admin window?)"; break;                      // 模式未知
                default: mode = d.Name; break;
            }
            return who + " — " + mode;
        }
    }
}
