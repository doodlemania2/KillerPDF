using System.Windows;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    internal sealed class PdfContentEditor
    {
        private readonly Dictionary<string, Dictionary<int, ParsedPageContent>> _pageCache = new();

        public void ClearCache() => _pageCache.Clear();

        public TextRunHit? FindTextRunAt(string pdfPath, int pageIndex, Point canvasPoint, int renderWidth, int renderHeight)
        {
            var parsed = GetParsedPage(pdfPath, pageIndex, renderWidth, renderHeight);
            if (parsed.TextRuns.Count == 0) return null;

            var direct = parsed.TextRuns
                .Where(r => r.CanvasBounds.Contains(canvasPoint))
                .OrderBy(r => r.CanvasBounds.Width * r.CanvasBounds.Height)
                .FirstOrDefault();
            if (direct is not null) return direct;

            return parsed.TextRuns
                .Where(r => canvasPoint.Y >= r.CanvasBounds.Top - 3 && canvasPoint.Y <= r.CanvasBounds.Bottom + 3)
                .OrderBy(r => Math.Abs((r.CanvasBounds.Left + r.CanvasBounds.Right) / 2 - canvasPoint.X))
                .FirstOrDefault();
        }

        public ImageHit? FindImageAt(string pdfPath, int pageIndex, Point canvasPoint, int renderWidth, int renderHeight)
        {
            var parsed = GetParsedPage(pdfPath, pageIndex, renderWidth, renderHeight);
            return parsed.Images
                .Where(i => i.CanvasBounds.Contains(canvasPoint))
                .OrderBy(i => i.CanvasBounds.Width * i.CanvasBounds.Height)
                .FirstOrDefault();
        }

        private ParsedPageContent GetParsedPage(string pdfPath, int pageIndex, int renderWidth, int renderHeight)
        {
            string cacheKey = $"{pdfPath}|{renderWidth}x{renderHeight}";
            if (_pageCache.TryGetValue(cacheKey, out var pages) &&
                pages.TryGetValue(pageIndex, out var cached))
                return cached;

            pages ??= new Dictionary<int, ParsedPageContent>();
            _pageCache[cacheKey] = pages;

            using var pigDoc = PdfPigDoc.Open(pdfPath);
            if (pageIndex < 0 || pageIndex >= pigDoc.NumberOfPages)
            {
                var empty = new ParsedPageContent();
                pages[pageIndex] = empty;
                return empty;
            }

            var page = pigDoc.GetPage(pageIndex + 1);
            double sx = renderWidth / page.Width;
            double sy = renderHeight / page.Height;

            var textRuns = page.GetWords()
                .GroupBy(w => Math.Round((renderHeight - (w.BoundingBox.Top * sy)) / 4.0))
                .Select(g =>
                {
                    var words = g.OrderBy(w => w.BoundingBox.Left).ToList();
                    double left = words.Min(w => w.BoundingBox.Left) * sx;
                    double top = renderHeight - (words.Max(w => w.BoundingBox.Top) * sy);
                    double right = words.Max(w => w.BoundingBox.Right) * sx;
                    double bottom = renderHeight - (words.Min(w => w.BoundingBox.Bottom) * sy);
                    string text = string.Join(" ", words.Select(w => w.Text));
                    double fontSize = Math.Max((bottom - top) * 0.75, 10);
                    string fontName = CleanFontName(words.FirstOrDefault()?.FontName);

                    var firstLetter = words.SelectMany(w => w.Letters).FirstOrDefault();
                    if (firstLetter is not null)
                    {
                        fontSize = Math.Max(firstLetter.FontSize * sy, 10);
                        fontName = CleanFontName(firstLetter.FontName);
                    }

                    return new TextRunHit
                    {
                        Text = text,
                        CanvasBounds = new Rect(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1)),
                        Position = new Point(left, top),
                        FontSize = fontSize,
                        FontName = fontName
                    };
                })
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .ToList();

            var images = page.GetImages()
                .Select(img =>
                {
                    var box = img.BoundingBox;
                    double left = box.Left * sx;
                    double top = renderHeight - (box.Top * sy);
                    double width = (box.Right - box.Left) * sx;
                    double height = (box.Top - box.Bottom) * sy;
                    return new ImageHit { CanvasBounds = new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1)) };
                })
                .Where(i => i.CanvasBounds.Width > 1 && i.CanvasBounds.Height > 1)
                .ToList();

            var parsed = new ParsedPageContent { TextRuns = textRuns, Images = images };
            pages[pageIndex] = parsed;
            return parsed;
        }

        private static string CleanFontName(string? rawFont)
        {
            if (string.IsNullOrWhiteSpace(rawFont)) return "Segoe UI";

            var font = rawFont;
            int plus = font.IndexOf('+');
            if (plus >= 0 && plus < font.Length - 1)
                font = font.Substring(plus + 1);

            font = font.Replace(",Bold", "")
                       .Replace(",Italic", "")
                       .Replace("-Bold", "")
                       .Replace("-Italic", "")
                       .Replace("-Roman", "")
                       .Replace("-Regular", "");

            return string.IsNullOrWhiteSpace(font) ? "Segoe UI" : font;
        }

        private sealed class ParsedPageContent
        {
            public List<TextRunHit> TextRuns { get; set; } = new();
            public List<ImageHit> Images { get; set; } = new();
        }
    }

    internal sealed class TextRunHit
    {
        public string Text { get; set; } = "";
        public Rect CanvasBounds { get; set; }
        public Point Position { get; set; }
        public double FontSize { get; set; } = 14;
        public string FontName { get; set; } = "Segoe UI";
    }

    internal sealed class ImageHit
    {
        public Rect CanvasBounds { get; set; }
    }
}
