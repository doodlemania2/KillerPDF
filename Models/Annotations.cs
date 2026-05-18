using System.Windows;
using System.Windows.Media;

namespace TDPdf
{
    public enum EditTool { Select, Text, Highlight, Draw, Signature, Image, EditText, EditImage, Crop, Pan, Erase, Shape }

    public enum ShapeKind { Rectangle, Ellipse, Line }

    public enum ZoomFitMode { None, Width, Page }

    public abstract class PageAnnotation
    {
        public int PageIndex { get; set; }
    }

    /// <summary>
    /// Base class for placed/resizable annotations (signature, image).
    /// Carries the shared position, scale, and source-dimension properties used by the resize handle.
    /// </summary>
    public abstract class PlacedAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public double Scale { get; set; } = 0.5;
        public double SourceWidth { get; set; } = 400;
        public double SourceHeight { get; set; } = 150;
    }

    public class TextAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public string Content { get; set; } = "";
        public double FontSize { get; set; } = 14;
        public byte ColorR { get; set; } = 0;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }
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
    /// Transient crop rectangle used only as an on-canvas UI overlay while applying a crop.
    /// </summary>
    public class CropAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }
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
    /// Represents an edit to an existing PDF image: white-out original bounds, then optionally redraw.
    /// </summary>
    public class ImageEditAnnotation : PageAnnotation
    {
        public Rect OriginalBounds { get; set; }
        public Rect TargetBounds { get; set; }
        public string? OriginalImageData { get; set; }
        public string? ReplacementImagePath { get; set; }
        public bool IsDeleted { get; set; }
    }

    /// <summary>
    /// A signature placed on a PDF page: either ink strokes or an imported image.
    /// </summary>
    public class SignatureAnnotation : PlacedAnnotation
    {
        public List<List<Point>> Strokes { get; set; } = new();
        /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }

    /// <summary>
    /// A geometric shape annotation: rectangle, ellipse, or line.
    /// Stores Start/End endpoints so a Line preserves direction (NW→SE vs NE→SW).
    /// Bounds is the normalized rectangle spanning Start and End; used for hit-testing
    /// and rendering rectangles/ellipses.
    /// </summary>
    public class ShapeAnnotation : PageAnnotation
    {
        public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
        public Point Start { get; set; }
        public Point End { get; set; }

        public byte StrokeR { get; set; } = 255;
        public byte StrokeG { get; set; } = 0;
        public byte StrokeB { get; set; } = 0;
        public byte StrokeA { get; set; } = 255;

        public byte FillR { get; set; } = 255;
        public byte FillG { get; set; } = 255;
        public byte FillB { get; set; } = 0;
        public byte FillA { get; set; } = 80;

        public bool HasFill { get; set; }
        public double StrokeWidth { get; set; } = 2;

        public Color GetStrokeColor() => Color.FromArgb(StrokeA, StrokeR, StrokeG, StrokeB);
        public void SetStrokeColor(Color c) { StrokeR = c.R; StrokeG = c.G; StrokeB = c.B; StrokeA = c.A; }
        public Color GetFillColor() => Color.FromArgb(FillA, FillR, FillG, FillB);
        public void SetFillColor(Color c) { FillR = c.R; FillG = c.G; FillB = c.B; FillA = c.A; }

        public Rect Bounds
        {
            get
            {
                double x = System.Math.Min(Start.X, End.X);
                double y = System.Math.Min(Start.Y, End.Y);
                double w = System.Math.Abs(End.X - Start.X);
                double h = System.Math.Abs(End.Y - Start.Y);
                return new Rect(x, y, w, h);
            }
        }
    }

    /// <summary>
    /// An image placed on a PDF page as a resizable annotation.
    /// </summary>
    public class ImageAnnotation : PlacedAnnotation
    {
        /// <summary>Base-64 encoded image bytes (PNG, JPG, BMP, etc.).</summary>
        public string ImageData { get; set; } = "";
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
