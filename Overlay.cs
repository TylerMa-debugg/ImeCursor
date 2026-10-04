// ImeCursor - the click-through, never-activating, per-pixel-alpha badge window.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace ImeCursor
{
    internal sealed class Overlay : NativeWindow, IDisposable
    {
        private IntPtr memDC;
        private IntPtr hbmp;
        private IntPtr oldBmp;
        private int width;
        private int height;
        private string renderedKey;
        private bool contentDirty;
        private bool visible;
        private int x = int.MinValue;
        private int y = int.MinValue;
        private byte opacity = 235;
        private int lastTopmostTick;
        private bool disposed;
        private IntPtr hwnd;                 // copy of Handle, safe to read from the detection thread
        private int wakePending;
        private const int WM_WAKE = Native.WM_APP + 1;

        /// <summary>Runs on the UI thread when the detection thread calls PostWake (a new state is ready).</summary>
        public Action Wake;

        public Overlay()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "ImeCursorBadge";
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                         Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.X = -32000;
            cp.Y = -32000;
            cp.Width = 1;
            cp.Height = 1;
            CreateHandle(cp);
            hwnd = Handle;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        public int Width { get { return width; } }
        public int Height { get { return height; } }
        public bool IsShown { get { return visible; } }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_NCHITTEST: m.Result = (IntPtr)Native.HTTRANSPARENT; return;
                case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return;
                case Native.WM_DPICHANGED: m.Result = IntPtr.Zero; return;   // we size ourselves
                case WM_WAKE:
                    System.Threading.Interlocked.Exchange(ref wakePending, 0);
                    if (Wake != null) Wake();
                    m.Result = IntPtr.Zero;
                    return;
            }
            base.WndProc(ref m);
        }

        /// <summary>Thread-safe and non-blocking: asks the UI thread to run Wake soon (coalesced).</summary>
        public void PostWake()
        {
            IntPtr h = hwnd;
            if (h == IntPtr.Zero) return;
            if (System.Threading.Interlocked.Exchange(ref wakePending, 1) == 0)
            {
                if (!Native.PostMessage(h, WM_WAKE, IntPtr.Zero, IntPtr.Zero)) System.Threading.Interlocked.Exchange(ref wakePending, 0);
            }
        }

        /// <summary>Re-renders the bitmap if key changed. Returns false if rendering failed.</summary>
        public bool SetContent(string key, string text, string fontName, float fontPt, float scale, Color back, Color fore, byte alpha)
        {
            if (key == renderedKey && hbmp != IntPtr.Zero && alpha == opacity) return true;
            BadgeLayout L;
            IntPtr nb = BadgeRenderer.RenderDib(text, fontName, fontPt, scale, back, fore, out L);
            if (nb == IntPtr.Zero) return false;
            IntPtr prev = Native.SelectObject(memDC, nb);
            if (oldBmp == IntPtr.Zero) oldBmp = prev;            // the DC's original 1x1 stock bitmap
            if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);   // previous badge (now deselected)
            hbmp = nb;
            width = L.Width;
            height = L.Height;
            renderedKey = key;
            opacity = alpha;
            contentDirty = true;
            return true;
        }

        /// <summary>Moves (and if needed repaints) the badge; shows it without activation.</summary>
        public void Place(int nx, int ny)
        {
            if (hbmp == IntPtr.Zero) return;
            bool moved = nx != x || ny != y;
            int now = Environment.TickCount;
            if (contentDirty)
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                Native.POINT dst = new Native.POINT(nx, ny);
                Native.SIZE sz = new Native.SIZE(width, height);
                Native.POINT src = new Native.POINT(0, 0);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.BlendOp = Native.AC_SRC_OVER;
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = opacity;
                bf.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDC, ref src, 0, ref bf, Native.ULW_ALPHA);
                Native.ReleaseDC(IntPtr.Zero, screen);
                contentDirty = false;
                x = nx; y = ny;
                moved = true;   // re-assert topmost below
            }
            uint show = 0;
            if (!visible)
            {
                show = Native.SWP_SHOWWINDOW;   // show and move in the same call: never visible at the old position
                visible = true;
                moved = true;
            }
            if (moved || unchecked(now - lastTopmostTick) > 1000)
            {
                // move + re-assert topmost (+ show) in one call, never activating
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | show);
                x = nx; y = ny;
                lastTopmostTick = now;
            }
        }

        public void Hide()
        {
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            visible = false;
        }

        /// <summary>Forces a repaint with the next SetContent (e.g. after settings reload).</summary>
        public void Invalidate()
        {
            renderedKey = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (memDC != IntPtr.Zero)
            {
                if (oldBmp != IntPtr.Zero) Native.SelectObject(memDC, oldBmp);
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero;
            }
            if (hbmp != IntPtr.Zero) { Native.DeleteObject(hbmp); hbmp = IntPtr.Zero; }
            hwnd = IntPtr.Zero;
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
