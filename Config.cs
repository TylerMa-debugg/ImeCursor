// ImeCursor - settings: tiny tolerant UTF-8 INI parser + defaults.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace ImeCursor
{
    internal sealed class Config
    {
        public const int Auto = int.MinValue;

        /// <summary>Auto offset = these factors x the displayed pointer size (CursorBaseSize x DPI/96): just below-right
        /// of the arrow's ink (about 5 logical px clear of it), measured on the Windows arrow images.</summary>
        public const float AutoOffsetX = 0.36f;
        public const float AutoOffsetY = 0.50f;

        // Defaults: keep the field initializers below, DefaultIni (the file written when it is missing) and the
        // README settings table in sync.

        // [General]
        public bool ShowImeName = true;
        public int PollMs = 80;
        public int FollowMs = 15;
        public byte Opacity = 235;
        public string FontName = "Microsoft YaHei UI";
        public float FontSize = 9.5f;
        public bool AnchorCaret = true;       // Anchor=caret (text cursor) | mouse (pointer)
        public bool NoCaretHide = false;      // NoCaret=mouse (follow the pointer) | hide, when no text cursor is found
        public int CaretMs = 50;
        public int OffsetX = Auto;            // logical px or Auto
        public int OffsetY = Auto;
        public bool HideInFullscreen = true;
        public bool HideWhenCursorHidden = false;
        public bool FlashOnChange = true;
        public int FlashMs = 700;
        public float FlashScale = 1.4f;
        public bool TrayShowsState = true;

        // [Colors]
        public Color ColorChinese = Color.FromArgb(0xE5, 0x48, 0x4D);
        public Color ColorEnglish = Color.FromArgb(0x2F, 0x80, 0xED);
        public Color ColorKeyboard = Color.FromArgb(0x6B, 0x72, 0x80);
        public Color ColorCaps = Color.FromArgb(0xD9, 0x77, 0x06);
        public Color ColorUnknown = Color.FromArgb(0x6B, 0x72, 0x80);
        public Color ColorText = Color.FromArgb(0xFF, 0xFF, 0xFF);

        // [Text]
        public string TextNative = "中";   // 中
        public string TextAlpha = "英";    // 英
        public string TextCaps = "A";
        public string TextUnknown = "?";
        public Dictionary<int, string> NativeByLang = new Dictionary<int, string>();

        // [Rules] lang -> auto|open|open+conv|conv|keyboard ; [Labels] lang -> auto|text
        public Dictionary<int, string> Rules = new Dictionary<int, string>();
        public Dictionary<int, string> Labels = new Dictionary<int, string>();

        public string FilePath;
        public List<string> Warnings = new List<string>();
        public bool ReadFailed;               // the file exists but could not be read: these are defaults, not the user's settings

        /// <summary>Languages that use an IME by default (rule "auto").</summary>
        public static readonly int[] DefaultImeLanguages = new int[] { 0x0804, 0x0404, 0x0C04, 0x1004, 0x1404, 0x0411, 0x0412 };

        public Config()
        {
            foreach (int l in DefaultImeLanguages) Rules[l] = "auto";
            NativeByLang[0x0411] = "あ";   // あ
            NativeByLang[0x0412] = "한";   // 한
        }

        /// <summary>Rule for a language, or null if the language is a plain keyboard.</summary>
        public string RuleFor(int lang)
        {
            string r;
            if (Rules.TryGetValue(lang, out r))
                return r == "keyboard" ? null : r;
            return null;
        }

        public string NativeTextFor(int lang)
        {
            string s;
            if (NativeByLang.TryGetValue(lang, out s) && !string.IsNullOrEmpty(s)) return s;
            return TextNative;
        }

        // ------------------------------------------------------------------ loading

        public static string DefaultPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImeCursor.ini");
        }

        /// <summary>Loads settings; never throws. Writes the default INI when missing.</summary>
        public static Config Load(string path)
        {
            Config c = new Config();
            c.FilePath = path;
            try
            {
                if (!File.Exists(path))
                {
                    try { File.WriteAllText(path, DefaultIni, new UTF8Encoding(true)); }
                    catch (Exception ex) { c.Warnings.Add("cannot write default ini: " + ex.Message); }
                    return c;
                }
                byte[] bytes = File.ReadAllBytes(path);
                c.Parse(c.Decode(bytes));
            }
            catch (Exception ex)
            {
                c.ReadFailed = true;
                c.Warnings.Add("cannot read ini: " + ex.Message);
            }
            return c;
        }

        /// <summary>UTF-8 (with or without BOM) or UTF-16 with BOM. Bytes that are not valid UTF-8 (a file saved
        /// as ANSI, e.g. GBK) are decoded with the system code page instead, with a warning.</summary>
        private string Decode(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException)
            {
                Encoding ansi = Encoding.Default;
                Warnings.Add("file is not UTF-8; read as " + ansi.WebName + " - please save it as UTF-8");
                return ansi.GetString(b);
            }
        }

        private void Parse(string text)
        {
            string section = "";
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int e = line.IndexOf(']');
                    section = (e > 0 ? line.Substring(1, e - 1) : line.Substring(1)).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) { Warnings.Add("line " + (i + 1) + ": ignored"); continue; }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                int sc = val.IndexOf(" ;", StringComparison.Ordinal);      // inline comment
                if (sc >= 0) val = val.Substring(0, sc).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"') val = val.Substring(1, val.Length - 2);
                try { Apply(section, key, val, i + 1); }
                catch (Exception ex) { Warnings.Add("line " + (i + 1) + ": " + ex.Message); }
            }
        }

        private void Apply(string section, string key, string val, int lineNo)
        {
            string k = key.ToLowerInvariant();
            if (section == "general")
            {
                switch (k)
                {
                    case "showimename": ShowImeName = ParseBool(val, ShowImeName, lineNo); break;
                    case "pollms": PollMs = ParseInt(val, PollMs, 20, 2000, lineNo); break;
                    case "followms": FollowMs = ParseInt(val, FollowMs, 5, 200, lineNo); break;
                    case "opacity": Opacity = (byte)ParseInt(val, Opacity, 30, 255, lineNo); break;
                    case "fontname": if (val.Length > 0) FontName = val; break;
                    case "fontsize": FontSize = ParseFloat(val, FontSize, 5f, 40f, lineNo); break;
                    case "anchor":
                        if (val.Equals("caret", StringComparison.OrdinalIgnoreCase)) AnchorCaret = true;
                        else if (val.Equals("mouse", StringComparison.OrdinalIgnoreCase)) AnchorCaret = false;
                        else Warnings.Add("line " + lineNo + ": bad anchor " + val + " (caret or mouse)");
                        break;
                    case "nocaret":
                        if (val.Equals("mouse", StringComparison.OrdinalIgnoreCase)) NoCaretHide = false;
                        else if (val.Equals("hide", StringComparison.OrdinalIgnoreCase)) NoCaretHide = true;
                        else Warnings.Add("line " + lineNo + ": bad NoCaret " + val + " (mouse or hide)");
                        break;
                    case "caretms": CaretMs = ParseInt(val, CaretMs, 15, 500, lineNo); break;
                    case "offsetx": OffsetX = ParseOffset(val, OffsetX, lineNo); break;
                    case "offsety": OffsetY = ParseOffset(val, OffsetY, lineNo); break;
                    case "hideinfullscreen": HideInFullscreen = ParseBool(val, HideInFullscreen, lineNo); break;
                    case "hidewhencursorhidden": HideWhenCursorHidden = ParseBool(val, HideWhenCursorHidden, lineNo); break;
                    case "flashonchange": FlashOnChange = ParseBool(val, FlashOnChange, lineNo); break;
                    case "flashms": FlashMs = ParseInt(val, FlashMs, 100, 5000, lineNo); break;
                    case "flashscale": FlashScale = ParseFloat(val, FlashScale, 1f, 3f, lineNo); break;
                    case "trayshowsstate": TrayShowsState = ParseBool(val, TrayShowsState, lineNo); break;
                    default: Warnings.Add("line " + lineNo + ": unknown key " + key); break;
                }
            }
            else if (section == "colors")
            {
                switch (k)
                {
                    case "chinese": ColorChinese = ParseColor(val, ColorChinese, lineNo); break;
                    case "english": ColorEnglish = ParseColor(val, ColorEnglish, lineNo); break;
                    case "keyboard": ColorKeyboard = ParseColor(val, ColorKeyboard, lineNo); break;
                    case "caps": ColorCaps = ParseColor(val, ColorCaps, lineNo); break;
                    case "unknown": ColorUnknown = ParseColor(val, ColorUnknown, lineNo); break;
                    case "text": ColorText = ParseColor(val, ColorText, lineNo); break;
                    default: Warnings.Add("line " + lineNo + ": unknown key " + key); break;
                }
            }
            else if (section == "text")
            {
                if (val.Length == 0 || val.Length > 8) { Warnings.Add("line " + lineNo + ": bad text"); return; }
                if (k.StartsWith("native.", StringComparison.Ordinal))
                {
                    int lang = ParseLang(k.Substring(7));
                    if (lang > 0) NativeByLang[lang] = val; else Warnings.Add("line " + lineNo + ": bad language " + key);
                    return;
                }
                switch (k)
                {
                    case "native": TextNative = val; break;
                    case "alpha": TextAlpha = val; break;
                    case "caps": TextCaps = val; break;
                    case "unknown": TextUnknown = val; break;
                    default: Warnings.Add("line " + lineNo + ": unknown key " + key); break;
                }
            }
            else if (section == "rules")
            {
                int lang = ParseLang(key);
                string v = val.ToLowerInvariant().Replace(" ", "");
                if (lang <= 0) { Warnings.Add("line " + lineNo + ": bad language " + key); return; }
                if (v == "auto" || v == "open" || v == "open+conv" || v == "conv" || v == "keyboard") Rules[lang] = v;
                else Warnings.Add("line " + lineNo + ": bad rule " + val);
            }
            else if (section == "labels")
            {
                int lang = ParseLang(key);
                if (lang <= 0) { Warnings.Add("line " + lineNo + ": bad language " + key); return; }
                if (val.Length == 0 || val.Length > 12) { Warnings.Add("line " + lineNo + ": bad label"); return; }
                Labels[lang] = val;
            }
            else
            {
                Warnings.Add("line " + lineNo + ": unknown section [" + section + "]");
            }
        }

        // ------------------------------------------------------------------ value parsers (tolerant)

        private bool ParseBool(string v, bool def, int line)
        {
            string s = v.ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            Warnings.Add("line " + line + ": bad boolean " + v);
            return def;
        }

        private int ParseInt(string v, int def, int min, int max, int line)
        {
            int r;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
            {
                int cl = Math.Max(min, Math.Min(max, r));
                if (cl != r) Warnings.Add("line " + line + ": " + v + " out of range " + min + ".." + max + ", using " + cl);
                return cl;
            }
            Warnings.Add("line " + line + ": bad number " + v);
            return def;
        }

        private float ParseFloat(string v, float def, float min, float max, int line)
        {
            float r;
            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) && !float.IsNaN(r))
            {
                float cl = Math.Max(min, Math.Min(max, r));
                if (cl != r)
                    Warnings.Add("line " + line + ": " + v + " out of range " + min.ToString(CultureInfo.InvariantCulture) + ".." +
                        max.ToString(CultureInfo.InvariantCulture) + ", using " + cl.ToString(CultureInfo.InvariantCulture));
                return cl;
            }
            Warnings.Add("line " + line + ": bad number " + v);
            return def;
        }

        private int ParseOffset(string v, int def, int line)
        {
            if (v.Equals("auto", StringComparison.OrdinalIgnoreCase) || v.Length == 0) return Auto;
            return ParseInt(v, def == Auto ? 0 : def, -400, 400, line);
        }

        private Color ParseColor(string v, Color def, int line)
        {
            string s = v.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            uint u;
            if ((s.Length == 6 || s.Length == 8) && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u))
            {
                if (s.Length == 6) u |= 0xFF000000u;
                return Color.FromArgb(unchecked((int)u));
            }
            Warnings.Add("line " + line + ": bad colour " + v);
            return def;
        }

        /// <summary>"0804", "0x0804", "804", "0x00000804" -> 0x0804; 0 on failure.</summary>
        public static int ParseLang(string s)
        {
            string t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            int r;
            if (t.Length > 0 && t.Length <= 8 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r))
            {
                r &= 0xFFFF;
                return r;
            }
            return 0;
        }

        // ------------------------------------------------------------------ default file

        public const string DefaultIni =
"; ImeCursor settings (UTF-8). Lines starting with ; or # are comments.\r\n" +
"; After editing: tray menu > Reload settings (or restart ImeCursor).\r\n" +
"; Invalid values are ignored (the default is used); out-of-range numbers are clamped\r\n" +
"; to the allowed range. Both are reported in ImeCursor.log.\r\n" +
"\r\n" +
"[General]\r\n" +
"; 1 = badge shows the input method name too (\"微信 中\"), 0 = only the mode (\"中\")\r\n" +
"ShowImeName=1\r\n" +
"; How often the input mode is queried, in milliseconds (20..2000)\r\n" +
"PollMs=80\r\n" +
"; How often the badge follows the pointer, in milliseconds (5..200)\r\n" +
"FollowMs=15\r\n" +
"; Badge opacity 30..255\r\n" +
"Opacity=235\r\n" +
"FontName=Microsoft YaHei UI\r\n" +
"; Font size in points at 100% scaling (scaled by the DPI of the monitor under the pointer)\r\n" +
"FontSize=9.5\r\n" +
"; Where the badge sits: caret = bottom-right of the text cursor, mouse = bottom-right of the mouse pointer\r\n" +
"Anchor=caret\r\n" +
"; With Anchor=caret, when no text cursor can be found (desktop, no text field focused):\r\n" +
"; mouse = show the badge at the mouse pointer instead, hide = hide the badge\r\n" +
"NoCaret=mouse\r\n" +
"; How often the text cursor position is read, in milliseconds (15..500); caret movement also wakes it\r\n" +
"CaretMs=50\r\n" +
"; Badge offset in logical pixels, or auto. Caret: from the text cursor's bottom-right corner\r\n" +
"; (auto = 2 px). Mouse: from the pointer hotspot (auto = 0.36 x / 0.50 x the pointer size,\r\n" +
"; so the badge sits just below-right of the arrow)\r\n" +
"OffsetX=auto\r\n" +
"OffsetY=auto\r\n" +
"; Hide the badge while a full-screen app (game, video, F11 browser) is in front\r\n" +
"HideInFullscreen=1\r\n" +
"; Hide the badge while the mouse pointer itself is hidden\r\n" +
"HideWhenCursorHidden=0\r\n" +
"; Briefly enlarge the badge when the mode changes\r\n" +
"FlashOnChange=1\r\n" +
"FlashMs=700\r\n" +
"FlashScale=1.4\r\n" +
"; Tray icon shows the current mode (中/英/A/EN) in the state colour\r\n" +
"TrayShowsState=1\r\n" +
"\r\n" +
"[Colors]\r\n" +
"; #RRGGBB\r\n" +
"Chinese=#E5484D\r\n" +
"English=#2F80ED\r\n" +
"Keyboard=#6B7280\r\n" +
"Caps=#D97706\r\n" +
"Unknown=#6B7280\r\n" +
"Text=#FFFFFF\r\n" +
"\r\n" +
"[Text]\r\n" +
"; Mode glyphs. Native.<lang> overrides the native glyph for one language.\r\n" +
"Native=中\r\n" +
"Alpha=英\r\n" +
"Caps=A\r\n" +
"Unknown=?\r\n" +
"Native.0411=あ\r\n" +
"Native.0412=한\r\n" +
"\r\n" +
"[Rules]\r\n" +
"; How Chinese/English mode is decided, per language (hex LANGID):\r\n" +
";   auto      = 'open' if the language's input method is WeType, else 'open+conv'\r\n" +
";   open      = Chinese when the IME is open (WeType)\r\n" +
";   open+conv = Chinese when open AND the conversion mode is native (Microsoft Pinyin, most IMEs)\r\n" +
";   conv      = Chinese when the conversion mode is native\r\n" +
";   keyboard  = treat as a plain keyboard layout (no mode)\r\n" +
"; Other languages are plain keyboard layouts. To turn one of the IME languages below into a\r\n" +
"; plain keyboard, set it to keyboard (deleting its line keeps auto).\r\n" +
"0804=auto\r\n" +
"0404=auto\r\n" +
"0C04=auto\r\n" +
"1004=auto\r\n" +
"1404=auto\r\n" +
"0411=auto\r\n" +
"0412=auto\r\n" +
"\r\n" +
"[Labels]\r\n" +
"; Short input-method name shown before the mode, per language (hex LANGID).\r\n" +
"; auto = from the enabled input method: WeType -> 微信, Microsoft Pinyin -> 微软,\r\n" +
";        Sogou -> 搜狗, QQ -> QQ, Baidu -> 百度; several enabled for one language -> 中文.\r\n" +
"; Keyboard layouts default to the language code (0409 -> EN).\r\n" +
"0804=auto\r\n" +
";0409=EN\r\n";
    }
}
