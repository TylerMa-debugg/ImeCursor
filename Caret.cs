// ImeCursor - locating the text cursor (caret) of the foreground app.
using System;
using System.Runtime.InteropServices;

namespace ImeCursor
{
    /// <summary>Caret rectangle in physical screen pixels (immutable; shared between threads).</summary>
    internal sealed class CaretInfo
    {
        public readonly IntPtr Foreground;
        public readonly Native.RECT Rect;
        public readonly string Source;     // "win32" or "msaa"

        public CaretInfo(IntPtr foreground, Native.RECT rect, string source)
        {
            Foreground = foreground;
            Rect = rect;
            Source = source;
        }

        public bool SameAs(CaretInfo o)
        {
            return o != null && o.Foreground == Foreground && o.Source == Source &&
                   o.Rect.Left == Rect.Left && o.Rect.Top == Rect.Top && o.Rect.Right == Rect.Right && o.Rect.Bottom == Rect.Bottom;
        }
    }

    /// <summary>
    /// Finds the caret of the foreground window's thread:
    /// 1. the Win32 system caret (GetGUIThreadInfo) - classic Win32, WinForms, WPF, Office, consoles;
    /// 2. the MSAA caret object (OBJID_CARET) of the focused window - Chrome, Edge, Electron apps (VS Code, ...),
    ///    which draw their own caret but expose it for accessibility.
    /// Returns null when no caret is found (no text field focused, or the app exposes no caret).
    /// Must run on a background (MTA) thread: the MSAA call goes to the other process and can be slow.
    /// </summary>
    internal static class CaretLocator
    {
        private static readonly IntPtr DpiPerMonitorV2 = new IntPtr(-4);

        public static CaretInfo Locate(IntPtr fg)
        {
            if (fg == IntPtr.Zero) return null;
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(fg, out pid);
            if (tid == 0) return null;

            Native.GUITHREADINFO gi = new Native.GUITHREADINFO();
            gi.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (!Native.GetGUIThreadInfo(tid, ref gi)) return null;

            Native.RECT r;
            if (gi.hwndCaret != IntPtr.Zero && gi.rcCaret.Bottom > gi.rcCaret.Top && Win32CaretToScreen(gi.hwndCaret, gi.rcCaret, out r) && Plausible(r))
                return new CaretInfo(fg, r, "win32");

            IntPtr focus = gi.hwndFocus != IntPtr.Zero ? gi.hwndFocus : fg;
            if (MsaaCaret(focus, out r) && Plausible(r))
                return new CaretInfo(fg, r, "msaa");
            return null;
        }

        /// <summary>rcCaret is in the caret window's client coordinates, in that window's own DPI space.
        /// Convert in its DPI context, then to physical pixels (a no-op for DPI-aware windows).</summary>
        private static bool Win32CaretToScreen(IntPtr hwnd, Native.RECT rc, out Native.RECT r)
        {
            r = new Native.RECT();
            Native.POINT a = new Native.POINT(rc.Left, rc.Top);
            Native.POINT b = new Native.POINT(Math.Max(rc.Right, rc.Left + 1), rc.Bottom);
            IntPtr old = IntPtr.Zero;
            bool switched = false;
            try
            {
                IntPtr ctx = Native.GetWindowDpiAwarenessContext(hwnd);
                if (ctx != IntPtr.Zero) { old = Native.SetThreadDpiAwarenessContext(ctx); switched = old != IntPtr.Zero; }
            }
            catch (EntryPointNotFoundException) { }
            bool ok = Native.ClientToScreen(hwnd, ref a) && Native.ClientToScreen(hwnd, ref b);
            if (switched) Native.SetThreadDpiAwarenessContext(old);
            if (!ok) return false;
            try
            {
                if (switched && old != IntPtr.Zero)
                {
                    Native.LogicalToPhysicalPointForPerMonitorDPI(hwnd, ref a);
                    Native.LogicalToPhysicalPointForPerMonitorDPI(hwnd, ref b);
                }
            }
            catch (EntryPointNotFoundException) { }
            r.Left = a.X; r.Top = a.Y; r.Right = b.X; r.Bottom = b.Y;
            return true;
        }

        private static bool MsaaCaret(IntPtr hwnd, out Native.RECT r)
        {
            r = new Native.RECT();
            object o = null;
            try
            {
                Guid iid = Native.IID_IAccessible;
                if (Native.AccessibleObjectFromWindow(hwnd, Native.OBJID_CARET, ref iid, out o) != 0 || o == null) return false;
                Accessibility.IAccessible acc = o as Accessibility.IAccessible;
                if (acc == null) return false;
                int x, y, w, h;
                acc.accLocation(out x, out y, out w, out h, 0);   // CHILDID_SELF
                if (h <= 0) return false;
                r.Left = x; r.Top = y; r.Right = x + Math.Max(w, 1); r.Bottom = y + h;
                return true;
            }
            catch (Exception) { return false; }       // no caret / app refused / app went away
            finally
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
        }

        /// <summary>A caret is a thin rectangle at most a few hundred px high that lies on a monitor.</summary>
        private static bool Plausible(Native.RECT r)
        {
            int h = r.Bottom - r.Top, w = r.Right - r.Left;
            if (h <= 0 || h > 600 || w < 0 || w > 600) return false;
            if (r.Left == 0 && r.Top == 0) return false;     // "no caret" reported as an empty origin rect
            return Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero;
        }
    }
}
