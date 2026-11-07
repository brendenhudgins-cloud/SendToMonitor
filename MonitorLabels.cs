using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SendToMonitor;

/// <summary>
/// While a menu is open, shows each monitor's number and name in the middle of that monitor (like
/// Windows' "Identify"), so menu entries are easy to match to screens. The labels are per-pixel-alpha
/// layered windows: click-through, never activated, not in Alt+Tab or the taskbar, and drawn at
/// each monitor's own DPI.
/// </summary>
internal sealed class MonitorLabels : IDisposable
{
    private readonly List<LabelWindow> _windows = new();

    private MonitorLabels() { }

    /// <summary>Creates and shows a label on every monitor. Dispose to remove them.</summary>
    public static MonitorLabels Show(IEnumerable<MonitorDesc> monitors)
    {
        MonitorLabels labels = Create(monitors);
        labels.Reveal();
        return labels;
    }

    /// <summary>Creates the (still hidden) label windows.</summary>
    internal static MonitorLabels Create(IEnumerable<MonitorDesc> monitors)
    {
        var labels = new MonitorLabels();
        foreach (MonitorDesc monitor in monitors)
        {
            try
            {
                labels._windows.Add(new LabelWindow(monitor));
            }
            catch (Exception ex)
            {
                Log.Error("Creating monitor label", ex);
            }
        }
        return labels;
    }

    internal IReadOnlyList<IntPtr> Handles => _windows.Select(w => w.Handle).ToList();

    internal void Reveal()
    {
        foreach (LabelWindow window in _windows) Native.ShowWindow(window.Handle, SW_SHOWNOACTIVATE);
    }

    public void Dispose()
    {
        foreach (LabelWindow window in _windows) window.DestroyHandle();
        _windows.Clear();
    }

    /// <summary>The label image for <paramref name="monitor"/>: a big number over the name, at the monitor's DPI.</summary>
    internal static Bitmap Render(MonitorDesc monitor)
    {
        float scale = monitor.Dpi / 96f;
        float S(float logical) => logical * scale;

        using var numberFont = new Font("Segoe UI Semibold", S(72), FontStyle.Regular, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", S(22), FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        string number = monitor.Number.ToString();
        string name = monitor.Name;

        SizeF numberSize, nameSize;
        using (var measure = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(measure))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            numberSize = g.MeasureString(number, numberFont, int.MaxValue, format);
            nameSize = g.MeasureString(name, nameFont, int.MaxValue, format);
        }

        float padX = S(32), padTop = S(12), gap = S(2), padBottom = S(22);
        // Keep the label a sensible size on small or portrait monitors.
        float maxContentWidth = Math.Max(S(120), monitor.WorkArea.Width * 0.8f - 2 * padX);
        float contentWidth = Math.Min(maxContentWidth, Math.Max(Math.Max(numberSize.Width, nameSize.Width), S(96)));
        int width = (int)Math.Ceiling(contentWidth + 2 * padX);
        int height = (int)Math.Ceiling(padTop + numberSize.Height + gap + nameSize.Height + padBottom);

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // ClearType needs an opaque background
            g.Clear(Color.Transparent);

            float radius = S(14), inset = S(0.5f);
            using (GraphicsPath box = RoundedRect(new RectangleF(inset, inset, width - 2 * inset, height - 2 * inset), radius))
            using (var fill = new SolidBrush(Color.FromArgb(232, 30, 30, 30)))
            using (var border = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, S(1))))
            {
                g.FillPath(fill, box);
                g.DrawPath(border, box);
            }

            using var white = new SolidBrush(Color.White);
            using var light = new SolidBrush(Color.FromArgb(225, 225, 225));
            g.DrawString(number, numberFont, white, new RectangleF(padX, padTop, contentWidth, numberSize.Height), format);
            g.DrawString(name, nameFont, light, new RectangleF(padX, padTop + numberSize.Height + gap, contentWidth, nameSize.Height), format);
        }
        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class LabelWindow : NativeWindow
    {
        public LabelWindow(MonitorDesc monitor)
        {
            using Bitmap bitmap = Render(monitor);
            RECT area = monitor.WorkArea;
            int x = area.Left + (area.Width - bitmap.Width) / 2;
            int y = area.Top + (area.Height - bitmap.Height) / 2;

            // Created directly on its monitor, so Windows never sends it a DPI change.
            CreateHandle(new CreateParams
            {
                Caption = "SendToMonitor monitor label",
                X = x,
                Y = y,
                Width = bitmap.Width,
                Height = bitmap.Height,
                Style = unchecked((int)Native.WS_POPUP),
                ExStyle = (int)(WS_EX_LAYERED | WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST),
            });

            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr hbitmap = bitmap.GetHbitmap(Color.FromArgb(0)); // 32-bit premultiplied, alpha kept
            IntPtr old = SelectObject(memory, hbitmap);
            try
            {
                var position = new POINT(x, y);
                var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
                var source = new POINT(0, 0);
                var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                if (!UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, ULW_ALPHA))
                    throw new System.ComponentModel.Win32Exception();
            }
            catch
            {
                DestroyHandle();
                throw;
            }
            finally
            {
                SelectObject(memory, old);
                Native.DeleteObject(hbitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCHITTEST:
                    m.Result = (IntPtr)Native.HTTRANSPARENT;
                    return;
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
            }
            base.WndProc(ref m);
        }
    }

    private const long WS_EX_TOPMOST = 0x00000008L, WS_EX_TRANSPARENT = 0x00000020L,
                       WS_EX_LAYERED = 0x00080000L, WS_EX_NOACTIVATE = 0x08000000L;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    private const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
}
