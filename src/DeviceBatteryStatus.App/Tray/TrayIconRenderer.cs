using System.Globalization;
using System.Runtime.InteropServices;
using DeviceBatteryStatus.Core.Presentation;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Imaging = System.Drawing.Imaging;
using Text = System.Drawing.Text;

namespace DeviceBatteryStatus.App.Tray;

/// <summary>Creates owned icon copies; callers dispose them when replacing a tray image.</summary>
internal static class TrayIconRenderer
{
    // Visual reference only: EZ2.4G 5db7a8b1, my_devices.py make_battery_icon uses a circular
    // indicator. This filled circle and centered percentage are independently rendered in C#;
    // no Python/GDI implementation or assets were copied from the unlicensed reference.
    internal static Drawing.Icon Render(int width, int height, int? percentage, BatteryColor color)
    {
        var measured = percentage is >= 0 and <= 100 && color != BatteryColor.Neutral;
        var text = measured ? percentage!.Value.ToString(CultureInfo.InvariantCulture) : "–";
        var background = measured ? color switch
        {
            BatteryColor.Green => Drawing.Color.FromArgb(0x44, 0xD6, 0x2C),
            BatteryColor.Yellow => Drawing.Color.FromArgb(0xFF, 0xD5, 0x4A),
            BatteryColor.Red => Drawing.Color.FromArgb(0xFF, 0x5C, 0x5C),
            _ => Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A)
        } : Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A);
        var foreground = measured ? Drawing.Color.FromArgb(0x08, 0x08, 0x08)
            : Drawing.Color.FromArgb(0xE8, 0xE8, 0xE8);
        const int supersample = 4;
        using var rendered = new Drawing.Bitmap(width * supersample, height * supersample, Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(rendered))
        {
            graphics.Clear(Drawing.Color.Transparent);
            graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            graphics.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
            graphics.TextRenderingHint = Text.TextRenderingHint.AntiAliasGridFit;
            var diameter = Math.Min(rendered.Width, rendered.Height) - supersample;
            var circle = new Drawing.RectangleF((rendered.Width - diameter) / 2f,
                (rendered.Height - diameter) / 2f, diameter, diameter);
            using var circleBrush = new Drawing.SolidBrush(background);
            using var textBrush = new Drawing.SolidBrush(foreground);
            graphics.FillEllipse(circleBrush, circle);
            using var format = new Drawing.StringFormat(Drawing.StringFormat.GenericTypographic)
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
                FormatFlags = Drawing.StringFormatFlags.NoWrap
            };
            var fontSize = diameter * (text.Length >= 3 ? 0.48f : 0.64f);
            // Fit three digits inside the circle rather than dropping a digit or rendering "99+".
            while (fontSize > diameter * 0.30f)
            {
                using var candidate = new Drawing.Font("Segoe UI", fontSize, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
                var extent = graphics.MeasureString(text, candidate, int.MaxValue, format);
                if (extent.Width <= diameter * 0.82f && extent.Height <= diameter * 0.90f) break;
                fontSize -= supersample * 0.25f;
            }
            using var font = new Drawing.Font("Segoe UI", fontSize, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            var textBounds = circle;
            textBounds.Y -= diameter * 0.025f;
            graphics.DrawString(text, font, textBrush, textBounds, format);
        }

        using var bitmap = new Drawing.Bitmap(width, height, Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(rendered, new Drawing.Rectangle(0, 0, width, height));
        }
        var nativeIcon = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(nativeIcon);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(nativeIcon); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
