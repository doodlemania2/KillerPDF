using System.Windows;
using System.Windows.Media;

namespace KillerPDF
{
    public enum EditTool { Select, Text, Highlight, Draw, Signature }

    public abstract class PageAnnotation
    {
        public int PageIndex { get; set; }
    }

    public class TextAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public string Content { get; set; } = "";
        public double FontSize { get; set; } = 14;
    }

    public class InkAnnotation : PageAnnotation
    {
        public List<Point> Points { get; set; } = new();
        public double StrokeWidth { get; set; } = 2;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }
    }

    public class HighlightAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 255;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 80;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }
    }

    /// <summary>
    /// Represents an edit to existing PDF text: whites out original bounds, draws replacement.
    /// </summary>
    public class TextEditAnnotation : PageAnnotation
    {
        public Rect OriginalBounds { get; set; }
        public Point Position { get; set; }
        public string NewContent { get; set; } = "";
        public string OriginalContent { get; set; } = "";
        public double FontSize { get; set; } = 14;
        public string FontName { get; set; } = "Segoe UI";
    }

    /// <summary>
    /// A signature placed on a PDF page: either ink strokes or an imported image.
    /// </summary>
    public class SignatureAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public double Scale { get; set; } = 0.5;
        public List<List<Point>> Strokes { get; set; } = new();
        public double SourceWidth { get; set; } = 400;
        public double SourceHeight { get; set; } = 150;
        /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }

    /// <summary>
    /// A point that can be serialized to JSON (WPF Point doesn't serialize well).
    /// </summary>
    public class SerializablePoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// A saved signature stored in the user's AppData for reuse.
    /// </summary>
    public class SavedSignature
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Signature";
        public List<List<SerializablePoint>> Strokes { get; set; } = new();
        public double CanvasWidth { get; set; } = 400;
        public double CanvasHeight { get; set; } = 150;
        /// <summary>Base-64 encoded PNG for imported image signatures. Null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }
}
