// ImeCursor - draws the pill badge and the tray icon (GDI+, premultiplied ARGB).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ImeCursor
{
    /// <summary>Pixel geometry of one badge.</summary>
    internal struct BadgeLayout
    {
        public int Width;
        public int Height;
        public float EmPx;
        public float TextX;
        public float TextY;
        public float Border;
    }

    internal static class BadgeRenderer
    {
        /// <summary>Computes the badge size. scale = dpi/96 * extra (flash).</summary>
        public static BadgeLayout Layout(string text, string fontName, float fontPt, float scale)
        {
            BadgeLayout L = new BadgeLayout();
            float em = fontPt * scale * 96f / 72f;
            L.EmPx = em;
            using (Font f = MakeFont(fontName, em))
            using (Bitmap b = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(b))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                SizeF sz = g.MeasureString(text, f, new PointF(0, 0), sf);
                int h = (int)Math.Round(em * 1.62f);
                if (h < 12) h = 12;
                int padX = (int)Math.Round(em * 0.62f);
                int w = (int)Math.Ceiling(sz.Width) + 2 * padX;
                if (w < h + (int)Math.Round(em * 0.25f)) w = h + (int)Math.Round(em * 0.25f);
                L.Width = w;
                L.Height = h;
                L.TextX = (float)Math.Round((w - sz.Width) / 2f);
                // Visual centre of CJK ideographs and Latin capitals sits ~0.36 em above the baseline.
                float ascent = em * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);
                float baseline = (float)Math.Round(h / 2f + em * 0.36f);
                L.TextY = baseline - ascent;
                L.Border = Math.Max(1f, (float)Math.Round(scale * 0.75f * 2f) / 2f);
            }
            return L;
        }

        private static Font MakeFont(string fontName, float emPx)
        {
            try { return new Font(fontName, emPx, FontStyle.Bold, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return new Font(FontFamily.GenericSansSerif, emPx, FontStyle.Bold, GraphicsUnit.Pixel); }
        }

        /// <summary>Draws the badge with its top-left at (0,0) of g. The surface must be cleared to transparent.</summary>
        public static void Draw(Graphics g, BadgeLayout L, string text, string fontName, Color back, Color fore)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Color bg = Color.FromArgb(255, back.R, back.G, back.B);
            using (GraphicsPath p = Pill(0, 0, L.Width, L.Height))
            using (SolidBrush b = new SolidBrush(bg))
                g.FillPath(b, p);

            // subtle top highlight for depth
            using (GraphicsPath p = Pill(0, 0, L.Width, L.Height))
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(0, 0, L.Width, L.Height),
                       Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
            {
                g.FillPath(lg, p);
            }

            // soft darker 1px border
            float bw = L.Border;
            using (GraphicsPath p = Pill(bw / 2f, bw / 2f, L.Width - bw, L.Height - bw))
            using (Pen pen = new Pen(Color.FromArgb(150, Darken(bg, 0.45f)), bw))
                g.DrawPath(pen, p);

            using (Font f = MakeFont(fontName, L.EmPx))
            using (SolidBrush tb = new SolidBrush(fore))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                g.DrawString(text, f, tb, L.TextX, L.TextY, sf);
            }
        }

        private static GraphicsPath Pill(float x, float y, float w, float h)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(w, h);
            p.AddArc(x, y, d, d, 90, 180);
            p.AddArc(x + w - d, y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        private static Color Darken(Color c, float k)
        {
            return Color.FromArgb(c.A, (int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k)));
        }

        /// <summary>Renders a badge into a new premultiplied bitmap (for --render).</summary>
        public static Bitmap RenderBitmap(string text, string fontName, float fontPt, float scale, Color back, Color fore)
        {
            BadgeLayout L = Layout(text, fontName, fontPt, scale);
            Bitmap bmp = new Bitmap(L.Width, L.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, L, text, fontName, back, fore);
            }
            return bmp;
        }

        /// <summary>Renders a badge into a new top-down 32bpp DIB section (premultiplied, for UpdateLayeredWindow).
        /// Caller owns the returned HBITMAP and must DeleteObject it.</summary>
        public static IntPtr RenderDib(string text, string fontName, float fontPt, float scale, Color back, Color fore, out BadgeLayout layout)
        {
            BadgeLayout L = Layout(text, fontName, fontPt, scale);
            layout = L;
            Native.BITMAPINFOHEADER bih = new Native.BITMAPINFOHEADER();
            bih.biSize = 40;
            bih.biWidth = L.Width;
            bih.biHeight = -L.Height;    // top-down
            bih.biPlanes = 1;
            bih.biBitCount = 32;
            bih.biCompression = 0;       // BI_RGB
            IntPtr bits;
            IntPtr hbmp = Native.CreateDIBSection(IntPtr.Zero, ref bih, 0, out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                return IntPtr.Zero;
            }
            try
            {
                using (Bitmap wrap = new Bitmap(L.Width, L.Height, L.Width * 4, PixelFormat.Format32bppPArgb, bits))
                using (Graphics g = Graphics.FromImage(wrap))
                {
                    g.Clear(Color.Transparent);
                    Draw(g, L, text, fontName, back, fore);
                    g.Flush(FlushIntention.Sync);
                }
            }
            catch
            {
                Native.DeleteObject(hbmp);
                throw;
            }
            return hbmp;
        }

        /// <summary>Tray icon: rounded square in the state colour with the short mode text. Caller must DestroyIcon.</summary>
        public static IntPtr RenderTrayIcon(string text, int size, string fontName, Color back, Color fore)
        {
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float r = size * 0.22f;
                    using (GraphicsPath p = RoundRect(0, 0, size, size, r))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, back.R, back.G, back.B)))
                        g.FillPath(b, p);

                    string t = string.IsNullOrEmpty(text) ? "?" : text;
                    if (t.Length > 2) t = t.Substring(0, 2);
                    // fit text: start large, shrink until it fits with a small margin
                    float em = size * (t.Length == 1 ? 0.78f : 0.56f);
                    using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        sf.FormatFlags |= StringFormatFlags.NoWrap;
                        for (int i = 0; i < 12; i++)
                        {
                            using (Font f = MakeFont(fontName, em))
                            {
                                SizeF sz = g.MeasureString(t, f, new PointF(0, 0), sf);
                                if (sz.Width <= size * 0.92f || em < 6f)
                                {
                                    float ascent = em * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);
                                    float baseline = (float)Math.Round(size / 2f + em * 0.36f);
                                    using (SolidBrush tb = new SolidBrush(fore))
                                        g.DrawString(t, f, tb, (float)Math.Round((size - sz.Width) / 2f), baseline - ascent, sf);
                                    break;
                                }
                            }
                            em *= 0.9f;
                        }
                    }
                }
                return bmp.GetHicon();
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
