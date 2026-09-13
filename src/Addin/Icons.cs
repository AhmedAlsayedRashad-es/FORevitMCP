using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FirstOption.RevitMcp.Addin
{
    /// <summary>Draws the ribbon icons at run time, so the add-in needs no image files.</summary>
    internal static class Icons
    {
        public static readonly Color Accent = Color.FromRgb(0x0F, 0x76, 0x6E);

        public static BitmapSource Make(string glyph, int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var radius = size * 0.2;
                dc.DrawRoundedRectangle(new SolidColorBrush(Accent), null, new Rect(0, 0, size, size), radius, radius);
                var text = size < 24 && glyph.Length > 1 ? glyph.Substring(0, 1) : glyph;
                var em = size * (text.Length > 2 ? 0.36 : text.Length == 2 ? 0.42 : 0.62);
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    em, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
