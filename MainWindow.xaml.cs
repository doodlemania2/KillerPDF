using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using KillerPDF.Services;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand ZoomInRoutedCommand = new("Zoom In", "ZoomIn", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomOutRoutedCommand = new("Zoom Out", "ZoomOut", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomResetRoutedCommand = new("Reset Zoom", "ZoomReset", typeof(MainWindow));

        public ZoomViewModel Zoom { get; } = new();

        private readonly PdfDocumentService _pdfDocumentService = new();
        private CancellationTokenSource? _openCancellationTokenSource;
        private CancellationTokenSource? _renderCancellationTokenSource;
        private int _busyDepth;
        private bool _isFileOperationBusy;
        private PdfDocument? _doc;
        private string? _currentFile;
        private Point _dragStartPoint;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        private readonly Dictionary<int, List<PageAnnotation>> _annotations = new();
        private readonly Dictionary<int, (int w, int h)> _renderDims = new();
        private readonly Dictionary<(int pageIndex, int dpiX), RenderedPage> _renderCache = new();
        private double _currentDpiScale = 1.0;
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private InkAnnotation? _activeInk;
        private CropAnnotation? _activeCrop;
        private TextBox? _activeTextBox;
        private PageAnnotation? _selectedAnnotation;
        private Border? _selectionBorder;
        private Rectangle? _imageResizeHandle;
        private bool _isResizingImage;
        private ImageEditAnnotation? _resizingImageEdit;
        private Point _imageResizeStart;
        private Rect _imageResizeOriginalBounds;
        private readonly PdfContentEditor _contentEditor = new();

        // Draw/Highlight settings
        private Color _drawColor = Colors.Red;
        private double _drawWidth = 3;
        private byte _drawOpacity = 255;
        private Color _highlightColor = Color.FromArgb(80, 255, 255, 0);
        private Border? _drawSettingsBar;

        // Text selection
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
        private string? _selectedText;

        // Search
        private Border? _searchBar;
        private TextBox? _searchBox;
        private TextBlock? _searchStatus;
        private readonly List<Rect> _searchHighlights = new();

        // Signatures
        private List<SavedSignature> _savedSignatures = new();
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        private Border? _cropPopup;
        private CheckBox? _cropApplyAllCheck;
        private static readonly string SignatureDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string SignatureFile = System.IO.Path.Combine(SignatureDir, "signatures.json");
        private static readonly SolidColorBrush SignatureBorderBrush = FrozenSolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        private static readonly SolidColorBrush DialogCloseNormalBrush = FrozenSolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly SolidColorBrush DialogCloseHoverBrush = FrozenSolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

        // Manual element refs (XAML codegen doesn't resolve these)
        private Canvas _annotationCanvas = null!;
        private Grid _pageContentGrid = null!;
        private Button _toolSelectBtn = null!;
        private Button _toolTextBtn = null!;
        private Button _toolEditTextBtn = null!;
        private Button _toolEditImageBtn = null!;
        private Button _toolHighlightBtn = null!;
        private Button _toolDrawBtn = null!;
        private Button _toolSignatureBtn = null!;
        private Button _toolCropBtn = null!;
        private Button _saveAsBtnRef = null!;
        private Button _closeFileBtnRef = null!;
        private ComboBox _zoomBox = null!;

        // Dirty / unsaved-change tracking
        private bool _isDirty = false;

        // Whole-document search results (PDF-space rects per page)
        private readonly Dictionary<int, List<(double left, double bottom, double right, double top)>> _allSearchRects = new();
        private List<int> _searchResultPages = new();
        private int _searchPageCursor = -1;

        public MainWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            _annotationCanvas = (Canvas)FindName("AnnotationCanvas")!;
            _pageContentGrid = (Grid)FindName("PageContentGrid")!;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolEditTextBtn = (Button)FindName("ToolEditTextBtn")!;
            _toolEditImageBtn = (Button)FindName("ToolEditImageBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolDrawBtn = (Button)FindName("ToolDrawBtn")!;
            _toolSignatureBtn = (Button)FindName("ToolSignatureBtn")!;
            _toolCropBtn = (Button)FindName("ToolCropBtn")!;
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            Zoom.SetZoomLevel(Properties.Settings.Default.LastZoomLevel);
            Zoom.PropertyChanged += Zoom_PropertyChanged;
            CommandBindings.Add(new CommandBinding(ZoomInRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.In)));
            CommandBindings.Add(new CommandBinding(ZoomOutRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.Out)));
            CommandBindings.Add(new CommandBinding(ZoomResetRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.Reset)));
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            SourceInitialized += MainWindow_SourceInitialized;
            DpiChanged += (_, _) => ApplyZoom();

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            Loaded += async (_, _) =>
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && System.IO.File.Exists(args[1]))
                    await OpenFileAsync(args[1]);
            };
        }

        private static SolidColorBrush FrozenSolidColorBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private bool ShouldIgnoreGlobalShortcut() => _activeTextBox is not null && _activeTextBox.IsFocused;

        private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            Open_Click(sender, e);
        }

        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            SaveAs_Click(sender, e);
        }

        private void PrintCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            Print_Click(sender, e);
        }

        private void FindCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            ToggleSearchBar();
        }

        private void DropZone_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                Open_Click(sender, e);
                e.Handled = true;
            }
        }

        // ============================================================
        // Maximize-respects-taskbar fix (WindowStyle=None needs WM_GETMINMAXINFO)
        // ============================================================

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            UpdateCurrentDpiScale();
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_DPICHANGED = 0x02E0;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            else if (msg == WM_DPICHANGED)
            {
                WmDpiChanged(hwnd, wParam, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void WmDpiChanged(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
        {
            var rect = Marshal.PtrToStructure<RECT>(lParam);
            SetWindowPos(hwnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
                SWP_NOZORDER | SWP_NOACTIVATE);

            int dpiX = wParam.ToInt32() & 0xFFFF;
            _currentDpiScale = dpiX > 0 ? dpiX / 96.0 : GetCurrentDpiScaleFromVisual();
            InvalidateRenderCache();
            RerenderCurrentPage();
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private sealed class RenderedPage
        {
            public RenderedPage(BitmapSource bitmap, double displayWidth, double displayHeight, int pixelWidth, int pixelHeight)
            {
                Bitmap = bitmap;
                DisplayWidth = displayWidth;
                DisplayHeight = displayHeight;
                PixelWidth = pixelWidth;
                PixelHeight = pixelHeight;
            }

            public BitmapSource Bitmap { get; }
            public double DisplayWidth { get; }
            public double DisplayHeight { get; }
            public int PixelWidth { get; }
            public int PixelHeight { get; }
        }

        // ============================================================
        // Window chrome
        // ============================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(sender, e);
                return;
            }
            // Delegate drag to Windows via WM_NCLBUTTONDOWN(HTCAPTION).
            // This gives native restore-from-maximized-and-drag behavior:
            // if the window is maximized, Windows restores it and follows the cursor
            // exactly as a native title bar would.
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    "You have unsaved changes. Close KillerPDF without saving?",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            base.OnClosing(e);
        }

        // ============================================================
        // Context menu
        // ============================================================

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            menu.Items.Add(MakeMenuItem("_Copy Text", (s, e) => CopySelectedText(), "Ctrl+C", "Copy selected text to the clipboard"));
            menu.Items.Add(MakeMenuItem("_Print", (s, e) => Print_Click(s!, e), "Ctrl+P", "Print the current PDF"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("_Select Tool", (s, e) => SetTool(EditTool.Select), null, "Switch to the select tool"));
            menu.Items.Add(MakeMenuItem("_Text Tool", (s, e) => SetTool(EditTool.Text), null, "Switch to the text tool"));
            menu.Items.Add(MakeMenuItem("Edit Existing Text", (s, e) => SetTool(EditTool.EditText), null, "Switch to the existing text edit tool"));
            menu.Items.Add(MakeMenuItem("Edit Existing Image", (s, e) => SetTool(EditTool.EditImage), null, "Switch to the existing image edit tool"));
            menu.Items.Add(MakeMenuItem("_Highlight Tool", (s, e) => SetTool(EditTool.Highlight), null, "Switch to the highlight tool"));
            menu.Items.Add(MakeMenuItem("_Draw Tool", (s, e) => SetTool(EditTool.Draw), null, "Switch to the draw tool"));
            menu.Items.Add(MakeMenuItem("_Crop Tool", (s, e) => SetTool(EditTool.Crop), null, "Switch to the crop tool"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("De_lete Selected", (s, e) => DeleteSelected(), "Delete", "Delete the selected annotation"));
            menu.Items.Add(MakeMenuItem("_Undo Last", (s, e) => Undo_Click(s!, e), "Ctrl+Z", "Undo the last annotation change"));
            menu.Items.Add(MakeMenuItem("Cle_ar Page Annotations", (s, e) => ClearAnnotations_Click(s!, e), null, "Clear all annotations on this page"));

            _annotationCanvas.ContextMenu = menu;
        }

        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null, string? helpText = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            var automationName = header.Replace("_", string.Empty);
            AutomationProperties.SetName(item, automationName);
            AutomationProperties.SetHelpText(item, helpText ?? automationName);
            return item;
        }

        // ============================================================
        // File operations
        // ============================================================

        private async Task OpenFileAsync(string path)
        {
            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _openCancellationTokenSource.Token;

            SetFileOperationBusy(true, $"Opening {System.IO.Path.GetFileName(path)}...");
            try
            {
                var result = await OpenFileCoreAsync(path, null, cancellationToken);
                await FinishOpenFileAsync(result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Open canceled");
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                SetFileOperationBusy(false);
                string? pw = PromptForPassword(path);
                if (pw is null)
                {
                    SetStatus("Open canceled");
                    return;
                }
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SetStatus("Open canceled");
                        return;
                    }
                    SetFileOperationBusy(true, $"Opening {System.IO.Path.GetFileName(path)}...");
                    _openCancellationTokenSource?.Dispose();
                    _openCancellationTokenSource = new CancellationTokenSource();
                    var retryCancellationToken = _openCancellationTokenSource.Token;
                    var result = await OpenFileCoreAsync(path, pw, retryCancellationToken);
                    await FinishOpenFileAsync(result, retryCancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SetStatus("Open canceled");
                }
                catch (Exception ex2)
                {
                    SetFileOperationBusy(false);
                    KillerDialog.Show(this, $"Failed to open PDF:\n{ex2.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                SetFileOperationBusy(false);
                KillerDialog.Show(this, $"Failed to open PDF:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async Task<PdfOpenResult> OpenFileCoreAsync(string path, string? password, CancellationToken cancellationToken)
        {
            var result = await _pdfDocumentService.OpenAsync(path, password, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private async Task FinishOpenFileAsync(PdfOpenResult result, CancellationToken cancellationToken)
        {
            bool assignedDocument = false;
            try
            {
                int pageCount = result.Document.PageCount;
                var thumbnails = await _pdfDocumentService.RenderThumbnailsAsync(result.WorkingPath, pageCount, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = result.Document;
                assignedDocument = true;
                _currentFile = result.WorkingPath;
                FileNameLabel.Text = System.IO.Path.GetFileName(result.DisplayPath);
                _annotations.Clear();
                InvalidateRenderCache();
                _contentEditor.ClearCache();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
                ClearSelection();
                RefreshPageList(thumbnails);
                DropZone.Visibility = Visibility.Collapsed;
                PagePreviewPanel.Visibility = Visibility.Visible;
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
                MarkDirty(false);
                if (_doc.PageCount > 0) PageList.SelectedIndex = 0;
                var readOnlySuffix = result.OpenedReadOnly ? " (read-only - owner restrictions)" : string.Empty;
                SetStatus($"Opened {System.IO.Path.GetFileName(result.DisplayPath)}{readOnlySuffix} - {_doc.PageCount} page(s)");
            }
            catch
            {
                if (!assignedDocument) result.Document.Close();
                throw;
            }
        }

        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        private string? PromptForPassword(string filename)
        {
            string? result = null;
            var win = new Window
            {
                Title = "Password Required",
                Width = 360,
                Height = 165,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = FrozenSolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            };
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"\"{System.IO.Path.GetFileName(filename)}\" is password protected.",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var pwBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(pwBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Open", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "Cancel", Width = 76 };
            okBtn.Click += (s, ev) => { result = pwBox.Password; win.DialogResult = true; };
            cancelBtn.Click += (s, ev) => { win.DialogResult = false; };
            pwBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { result = pwBox.Password; win.DialogResult = true; } };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            sp.Children.Add(btnRow);
            win.Content = sp;
            return win.ShowDialog() == true ? result : null;
        }

        private void RefreshPageList(IReadOnlyList<BitmapSource?>? thumbnails = null)
        {
            PageList.Items.Clear();
            if (_doc is null) return;

            for (int i = 0; i < _doc.PageCount; i++)
            {
                BitmapSource? thumb = thumbnails is not null && i < thumbnails.Count ? thumbnails[i] : null;
                var img = new Image
                {
                    Source = thumb,
                    Width = 140,
                    Height = thumb is not null ? 140.0 * thumb.PixelHeight / thumb.PixelWidth : 100,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 0, 2)
                };

                var label = new TextBlock
                {
                    Text = $"Page {i + 1}",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                if (thumb is not null)
                {
                    var border = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = FrozenSolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                        BorderThickness = new Thickness(1),
                        Child = img
                    };
                    panel.Children.Add(border);
                }
                else
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"Page {i + 1}",
                        Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 20)
                    });
                }
                panel.Children.Add(label);
                PageList.Items.Add(panel);
            }
        }

        private void UpdateCurrentDpiScale()
        {
            _currentDpiScale = GetCurrentDpiScaleFromVisual();
        }

        private double GetCurrentDpiScaleFromVisual()
        {
            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            return transform.M11 > 0 ? transform.M11 : 1.0;
        }

        private int GetCurrentDpiX()
        {
            return Math.Max(1, (int)Math.Round(_currentDpiScale * Zoom.ZoomLevel * 96.0));
        }

        private void InvalidateRenderCache()
        {
            _renderCache.Clear();
            _renderDims.Clear();
        }

        private void RerenderCurrentPage()
        {
            int pageIndex = PageList.SelectedIndex;
            if (pageIndex < 0 || _doc is null) return;

            RenderPage(pageIndex);
            ApplyZoom();
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                && _allSearchRects.Count > 0)
            {
                HighlightSearchResultsOnCurrentPage();
            }
        }

        private async void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            var currentFile = _currentFile;
            _renderCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Dispose();
            _renderCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _renderCancellationTokenSource.Token;
            try
            {
                int dpiX = GetCurrentDpiX();
                SetBusy(true, $"Rendering page {pageIndex + 1}...");
                if (!_renderCache.TryGetValue((pageIndex, dpiX), out var renderedPage))
                {
                    var result = await _pdfDocumentService.RenderPageAsync(currentFile, pageIndex, dpiX, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (result.Bitmap is null || result.Width <= 0 || result.Height <= 0)
                    {
                        PageImage.Source = null;
                        SetStatus($"Page {pageIndex + 1} - could not render");
                        return;
                    }

                    double renderScale = dpiX / 96.0;
                    renderedPage = new RenderedPage(result.Bitmap, result.Width / renderScale, result.Height / renderScale, result.Width, result.Height);
                    _renderCache[(pageIndex, dpiX)] = renderedPage;
                }

                if (_doc is null) return;

                _renderDims[pageIndex] = ((int)Math.Round(renderedPage.DisplayWidth), (int)Math.Round(renderedPage.DisplayHeight));
                PageImage.Source = renderedPage.Bitmap;
                PageImage.Width = renderedPage.DisplayWidth;
                PageImage.Height = renderedPage.DisplayHeight;
                _annotationCanvas.Width = renderedPage.DisplayWidth;
                _annotationCanvas.Height = renderedPage.DisplayHeight;
                ClearSelection();
                RenderAllAnnotations(pageIndex);
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount} - {Zoom.DisplayText}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PageImage.Source = null;
                SetStatus($"Render error: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void SetBusy(bool isBusy, string? status = null)
        {
            _busyDepth = isBusy ? _busyDepth + 1 : Math.Max(0, _busyDepth - 1);
            Mouse.OverrideCursor = _busyDepth > 0 ? Cursors.Wait : null;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        private void SetFileOperationBusy(bool isBusy, string? status = null)
        {
            if (_isFileOperationBusy == isBusy)
            {
                if (!string.IsNullOrEmpty(status)) SetStatus(status);
                return;
            }

            _isFileOperationBusy = isBusy;
            IsEnabled = !isBusy;
            SetBusy(isBusy, status);
        }

        // ============================================================
        // Tool selection
        // ============================================================

        private void SetTool(EditTool tool)
        {
            CommitActiveTextBox();
            ClearTextSelection();
            _currentTool = tool;

            var map = new (Button btn, EditTool t)[]
            {
                (_toolSelectBtn, EditTool.Select),
                (_toolTextBtn, EditTool.Text),
                (_toolEditTextBtn, EditTool.EditText),
                (_toolEditImageBtn, EditTool.EditImage),
                (_toolHighlightBtn, EditTool.Highlight),
                (_toolDrawBtn, EditTool.Draw),
                (_toolSignatureBtn, EditTool.Signature),
                (_toolCropBtn, EditTool.Crop)
            };
            var green = (SolidColorBrush)FindResource("AccentGreen");
            var greenDim = (SolidColorBrush)FindResource("AccentGreenDim");
            var text = (SolidColorBrush)FindResource("TextPrimary");

            foreach (var (btn, t) in map)
            {
                btn.Background = t == tool ? greenDim : Brushes.Transparent;
                btn.Foreground = t == tool ? green : text;
            }

            _annotationCanvas.Cursor = tool switch
            {
                EditTool.Text => Cursors.IBeam,
                EditTool.EditText => Cursors.IBeam,
                EditTool.EditImage => Cursors.Hand,
                EditTool.Highlight => Cursors.Cross,
                EditTool.Draw => Cursors.Pen,
                EditTool.Signature => Cursors.Hand,
                EditTool.Crop => Cursors.Cross,
                _ => Cursors.Arrow
            };

            // Show/hide draw settings bar
            if (tool == EditTool.Draw || tool == EditTool.Highlight)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }

            if (tool != EditTool.Crop)
            {
                HideCropPopup();
                ClearCropSelection();
            }
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolEditText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.EditText);
        private void ToolEditImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.EditImage);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolCrop_Click(object sender, RoutedEventArgs e)
        {
            SetTool(EditTool.Crop);
            ShowCropPopup();
        }
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }

        // ============================================================
        // Draw/Highlight settings bar
        // ============================================================

        private static readonly Color[] SwatchColors = new[]
        {
            Colors.Red, Colors.SaddleBrown, Colors.Orange, Colors.Gold,
            Colors.LimeGreen, Colors.DodgerBlue, Colors.MediumPurple,
            Colors.DeepPink, Colors.White, Colors.Black
        };

        private void ShowDrawSettings(EditTool tool)
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Color label
            panel.Children.Add(new TextBlock
            {
                Text = "Color:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches
            var activeColor = tool == EditTool.Draw ? _drawColor : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            foreach (var color in SwatchColors)
            {
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = FrozenSolidColorBrush(color),
                    BorderBrush = color == activeColor
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : FrozenSolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(color == activeColor ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
                swatch.MouseLeftButtonDown += (s, e) =>
                {
                    var c = (Color)((Border)s!).Tag;
                    if (tool == EditTool.Draw)
                        _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                    else
                        _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                    ShowDrawSettings(tool); // refresh selection
                };
                panel.Children.Add(swatch);
            }

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Size slider (draw only)
            if (tool == EditTool.Draw)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Size:",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
                var sizeSlider = new Slider
                {
                    Minimum = 1, Maximum = 20, Value = _drawWidth,
                    Width = 80, VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = 1, IsSnapToTickEnabled = true
                };
                sizeSlider.ValueChanged += (s, e) => _drawWidth = e.NewValue;
                panel.Children.Add(sizeSlider);

                var sizeLabel = new TextBlock
                {
                    Text = $"{_drawWidth:F0}px",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                };
                sizeSlider.ValueChanged += (s, e) => sizeLabel.Text = $"{e.NewValue:F0}px";
                panel.Children.Add(sizeLabel);

                // Separator
                panel.Children.Add(new Rectangle
                {
                    Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                    Margin = new Thickness(8, 2, 8, 2)
                });
            }

            // Opacity slider
            panel.Children.Add(new TextBlock
            {
                Text = "Opacity:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            byte currentOpacity = tool == EditTool.Draw ? _drawOpacity : _highlightColor.A;
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = currentOpacity,
                Width = 80, VerticalAlignment = VerticalAlignment.Center
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                if (tool == EditTool.Draw)
                {
                    _drawOpacity = a;
                    _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B);
                }
                else
                {
                    _highlightColor = Color.FromArgb(a, _highlightColor.R, _highlightColor.G, _highlightColor.B);
                }
            };
            panel.Children.Add(opacitySlider);
            panel.Children.Add(opacityLabel);

            _drawSettingsBar = new Border
            {
                Background = FrozenSolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_drawSettingsBar, 100);
                previewArea.Children.Add(_drawSettingsBar);
            }
        }

        private void HideDrawSettings()
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }
        }

        // ============================================================
        // Crop settings bar
        // ============================================================

        private void ShowCropPopup()
        {
            if (_cropPopup is not null)
            {
                _cropPopup.Visibility = Visibility.Visible;
                return;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = "Drag a crop rectangle",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            _cropApplyAllCheck = new CheckBox
            {
                Content = "Apply to all pages",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            panel.Children.Add(_cropApplyAllCheck);

            var applyBtn = new Button
            {
                Content = "Apply crop",
                Style = (Style)FindResource("ToolbarButtonAccent"),
                ToolTip = "Apply the selected crop rectangle"
            };
            applyBtn.Click += ApplyCrop_Click;
            panel.Children.Add(applyBtn);

            var resetBtn = new Button
            {
                Content = "Reset",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Clear the current crop rectangle"
            };
            resetBtn.Click += (s, e) => ClearCropSelection();
            panel.Children.Add(resetBtn);

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Cancel cropping"
            };
            cancelBtn.Click += (s, e) => SetTool(EditTool.Select);
            panel.Children.Add(cancelBtn);

            _cropPopup = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_cropPopup, 101);
                previewArea.Children.Add(_cropPopup);
            }
        }

        private void HideCropPopup()
        {
            if (_cropPopup is not null)
                _cropPopup.Visibility = Visibility.Collapsed;
        }

        // ============================================================
        // Signatures
        // ============================================================

        private void LoadSignatures()
        {
            try
            {
                if (File.Exists(SignatureFile))
                {
                    var json = File.ReadAllText(SignatureFile);
                    _savedSignatures = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? new();
                }
            }
            catch { _savedSignatures = new(); }
        }

        private void PersistSignatures()
        {
            try
            {
                Directory.CreateDirectory(SignatureDir);
                var json = JsonSerializer.Serialize(_savedSignatures, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SignatureFile, json);
            }
            catch { /* best effort */ }
        }

        private void ShowSignaturePopup()
        {
            HideSignaturePopup();

            var stack = new StackPanel { Margin = new Thickness(4) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Signatures",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 6)
            });

            // Saved signatures
            if (_savedSignatures.Count > 0)
            {
                var scroll = new ScrollViewer
                {
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                var listPanel = new StackPanel();

                foreach (var sig in _savedSignatures)
                {
                    var sigCopy = sig; // capture for lambda
                    var item = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = SignatureBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(4, 2, 4, 2),
                        Padding = new Thickness(4),
                        Cursor = Cursors.Hand,
                        Height = 60,
                        Width = 220
                    };

                    // Render mini signature preview
                    if (sigCopy.ImageData is not null)
                    {
                        try
                        {
                            var imgBytes = Convert.FromBase64String(sigCopy.ImageData);
                            var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                            using (var imageStream = new System.IO.MemoryStream(imgBytes))
                            {
                                bmpImg.BeginInit();
                                bmpImg.StreamSource = imageStream;
                                bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bmpImg.EndInit();
                            }
                            if (bmpImg.CanFreeze) bmpImg.Freeze();
                            item.Child = new System.Windows.Controls.Image
                            {
                                Source = bmpImg,
                                Width = 210, Height = 50,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                        }
                        catch { item.Child = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
                    }
                    else
                    {
                        var canvas = new Canvas
                        {
                            Width = 210, Height = 50,
                            Background = Brushes.Transparent,
                            IsHitTestVisible = false
                        };
                        RenderSignaturePreview(canvas, sigCopy, 210, 50);
                        item.Child = canvas;
                    }

                    item.MouseLeftButtonDown += (s, e) =>
                    {
                        _pendingSignature = sigCopy;
                        HideSignaturePopup();
                        _annotationCanvas.Cursor = Cursors.Cross;
                        SetStatus("Click on the page to place your signature");
                    };
                    item.MouseEnter += (s, e) =>
                        ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                    item.MouseLeave += (s, e) =>
                        ((Border)s!).BorderBrush = SignatureBorderBrush;

                    // Wrap in grid with delete button
                    var itemGrid = new Grid();
                    itemGrid.Children.Add(item);

                    var delBtn = new Button
                    {
                        Content = "\ue711",
                        FontSize = 10,
                        Width = 18, Height = 18,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0, 2, 0),
                        Background = FrozenSolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                        Foreground = (SolidColorBrush)FindResource("DangerRed"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("ToolbarButton")
                    };
                    delBtn.Click += (s, e) =>
                    {
                        _savedSignatures.Remove(sigCopy);
                        PersistSignatures();
                        ShowSignaturePopup(); // refresh
                    };
                    itemGrid.Children.Add(delBtn);
                    listPanel.Children.Add(itemGrid);
                }
                scroll.Content = listPanel;
                stack.Children.Add(scroll);
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "No saved signatures",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 4, 4, 8),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            // Separator
            stack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(4, 4, 4, 4)
            });

            // Create Signature button
            var createBtn = new Button
            {
                Content = "Create Signature",
                Style = (Style)FindResource("DarkButton"),
                Background = (SolidColorBrush)FindResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            createBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                OpenSignatureCreator();
            };
            stack.Children.Add(createBtn);

            // Import image button
            var importBtn = new Button
            {
                Content = "Import Image",
                Style = (Style)FindResource("DarkButton"),
                Background = FrozenSolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x2e)),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 2, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            importBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                ImportImageSignature();
            };
            stack.Children.Add(importBtn);

            _signaturePopup = new Border
            {
                Background = FrozenSolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 80, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 12, Opacity = 0.5, ShadowDepth = 4
                }
            };

            var previewGrid = PagePreviewPanel.Parent as Grid;
            if (previewGrid is not null)
            {
                Panel.SetZIndex(_signaturePopup, 200);
                previewGrid.Children.Add(_signaturePopup);
            }
        }

        private void HideSignaturePopup()
        {
            if (_signaturePopup is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_signaturePopup);
                _signaturePopup = null;
            }
        }

        private void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double scaleX = targetW / sig.CanvasWidth;
            double scaleY = targetH / sig.CanvasHeight;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sig.CanvasWidth * scale) / 2;
            double offsetY = (targetH - sig.CanvasHeight * scale) / 2;

            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                foreach (var pt in stroke)
                    poly.Points.Add(new Point(pt.X * scale + offsetX, pt.Y * scale + offsetY));
                canvas.Children.Add(poly);
            }
        }

        private void OpenSignatureCreator()
        {
            var win = new Window
            {
                Title = "Create Signature",
                Width = 460, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            // Outer chrome
            var outerChrome = new Border
            {
                Background      = FrozenSolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush     = FrozenSolidColorBrush(Color.FromRgb(0x22, 0x54, 0x3d)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };
            var rootStack = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = FrozenSolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                Padding      = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text       = "Create Signature",
                Foreground = FrozenSolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content         = "",
                FontFamily      = new FontFamily("Segoe MDL2 Assets"),
                FontSize        = 10,
                Width           = 28, Height = 28,
                Background      = System.Windows.Media.Brushes.Transparent,
                Foreground      = DialogCloseNormalBrush,
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeWinBtn.MouseEnter += (_, _2) => closeWinBtn.Foreground = DialogCloseHoverBrush;
            closeWinBtn.MouseLeave += (_, _2) => closeWinBtn.Foreground = DialogCloseNormalBrush;
            closeWinBtn.Click += (_, _2) => win.Close();
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = new CornerRadius(4),
                Height = 170
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;

            // Placeholder text
            var placeholder = new TextBlock
            {
                Text = "Draw your signature here",
                Foreground = FrozenSolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            drawCanvas.Children.Add(placeholder);

            // Drawing state
            var strokes = new List<List<Point>>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = new List<Point>();
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                currentPoly.Points.Add(pos);
                drawCanvas.Children.Add(currentPoly);
                drawCanvas.CaptureMouse();
            };

            drawCanvas.MouseMove += (s, e) =>
            {
                if (currentStroke is null || currentPoly is null) return;
                var pos = e.GetPosition(drawCanvas);
                pos.X = Math.Max(0, Math.Min(drawCanvas.ActualWidth, pos.X));
                pos.Y = Math.Max(0, Math.Min(drawCanvas.ActualHeight, pos.Y));
                currentStroke.Add(pos);
                currentPoly.Points.Add(pos);
            };

            drawCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (currentStroke is not null && currentStroke.Count > 1)
                    strokes.Add(currentStroke);
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };

            var clearBtn = new Button
            {
                Content = "Clear",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = FrozenSolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = FrozenSolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderBrush = FrozenSolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = new Button
            {
                Content = "Save Signature",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = FrozenSolidColorBrush(Color.FromRgb(0x22, 0x54, 0x3d)),
                Foreground = FrozenSolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                BorderBrush = FrozenSolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    KillerDialog.Show(this, "Draw a signature first.", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"Signature {_savedSignatures.Count + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _savedSignatures.Add(saved);
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Signature saved - click on the page to place it");

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            outerChrome.Child = rootStack;
            win.Content = outerChrome;
            win.ShowDialog();
        }

        private void ImportImageSignature()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Import Signature Image"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                var saved = new SavedSignature
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _savedSignatures.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Image loaded - click on the page to place it");
                ShowSignaturePopup(); // refresh to show the new entry
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Failed to import image:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            var sig = _pendingSignature;
            double scale = 0.5;

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                SourceWidth = sig.CanvasWidth,
                SourceHeight = sig.CanvasHeight,
                ImageData = sig.ImageData
            };

            // Drawn signature — convert serializable points to WPF points
            if (sig.ImageData is null)
            {
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add(stroke.Select(p => new Point(p.X, p.Y)).ToList());
            }

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);
            SetStatus("Signature placed - click again to place another, or switch tools");
        }

        // ============================================================
        // Canvas interaction
        // ============================================================

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            var pos = e.GetPosition(_annotationCanvas);
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            if (_currentTool == EditTool.EditImage && _imageResizeHandle is not null &&
                e.OriginalSource == _imageResizeHandle && _selectedAnnotation is ImageEditAnnotation selectedImage)
            {
                _isResizingImage = true;
                _resizingImageEdit = selectedImage;
                _imageResizeStart = pos;
                _imageResizeOriginalBounds = selectedImage.TargetBounds;
                _annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            switch (_currentTool)
            {
                case EditTool.Select:
                    ClearSelection();
                    ClearTextSelection();
                    if (e.ClickCount == 2)
                    {
                        // Double-click: edit existing PDF text at this position
                        EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Single click: start a drag selection for text
                        _isSelecting = true;
                        _selectStart = pos;
                        _selectRect = new Rectangle
                        {
                            Fill = FrozenSolidColorBrush(Color.FromArgb(40, 74, 130, 255)),
                            Stroke = FrozenSolidColorBrush(Color.FromArgb(120, 74, 130, 255)),
                            StrokeThickness = 1,
                            Width = 0, Height = 0,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(_selectRect, pos.X);
                        Canvas.SetTop(_selectRect, pos.Y);
                        _annotationCanvas.Children.Add(_selectRect);
                        _annotationCanvas.CaptureMouse();
                        e.Handled = true;
                    }
                    break;

                case EditTool.Text:
                    CommitActiveTextBox();
                    PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditText:
                    CommitActiveTextBox();
                    EditTextAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditImage:
                    CommitActiveTextBox();
                    EditImageAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    var rect = new Rectangle
                    {
                        Fill = FrozenSolidColorBrush(_highlightColor),
                        Width = 0, Height = 0
                    };
                    Canvas.SetLeft(rect, pos.X);
                    Canvas.SetTop(rect, pos.Y);
                    _annotationCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _annotationCanvas.CaptureMouse();
                    break;

                case EditTool.Crop:
                    ClearSelection();
                    ClearCropSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    var cropRect = new Rectangle
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(35, 74, 222, 128)),
                        Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 6, 3 },
                        Width = 0,
                        Height = 0,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(cropRect, pos.X);
                    Canvas.SetTop(cropRect, pos.Y);
                    _annotationCanvas.Children.Add(cropRect);
                    _activePreview = cropRect;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;

                case EditTool.Draw:
                    ClearSelection();
                    _isDrawing = true;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    var poly = new Polyline
                    {
                        Stroke = FrozenSolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    poly.Points.Add(pos);
                    _annotationCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _annotationCanvas.CaptureMouse();
                    break;

                case EditTool.Signature:
                    if (_pendingSignature is not null)
                    {
                        PlaceSignature(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowSignaturePopup();
                    }
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_annotationCanvas);
            pos.X = Math.Max(0, Math.Min(_annotationCanvas.ActualWidth, pos.X));
            pos.Y = Math.Max(0, Math.Min(_annotationCanvas.ActualHeight, pos.Y));

            // Text selection drag
            if (_isSelecting && _selectRect is not null)
            {
                Canvas.SetLeft(_selectRect, Math.Min(pos.X, _selectStart.X));
                Canvas.SetTop(_selectRect, Math.Min(pos.Y, _selectStart.Y));
                _selectRect.Width = Math.Abs(pos.X - _selectStart.X);
                _selectRect.Height = Math.Abs(pos.Y - _selectStart.Y);
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                ResizeImageEditPreview(pos);
                return;
            }

            if (!_isDrawing || _activePreview is null) return;

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Crop when _activePreview is Rectangle:
                    var rect = (Rectangle)_activePreview;
                    Canvas.SetLeft(rect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(rect, Math.Min(pos.Y, _drawStart.Y));
                    rect.Width = Math.Abs(pos.X - _drawStart.X);
                    rect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Draw when _activePreview is Polyline poly && _activeInk is not null:
                    _activeInk.Points.Add(pos);
                    poly.Points.Add(pos);
                    break;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;

            // Handle text selection release
            if (_isSelecting)
            {
                _isSelecting = false;
                _annotationCanvas.ReleaseMouseCapture();
                var pos = e.GetPosition(_annotationCanvas);
                double dragW = Math.Abs(pos.X - _selectStart.X);
                double dragH = Math.Abs(pos.Y - _selectStart.Y);

                if (dragW < 5 && dragH < 5)
                {
                    // Tiny drag = single click -> try annotation selection
                    ClearTextSelection();
                    if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                    {
                        for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                            {
                                SelectAnnotation(_annotations[pageIdx][i], bounds);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Real drag -> extract text from rectangle
                    var selectBounds = new Rect(
                        Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y),
                        dragW, dragH);
                    ExtractTextFromRegion(pageIdx, selectBounds);
                }
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                _isResizingImage = false;
                _annotationCanvas.ReleaseMouseCapture();
                RenderAllAnnotations(_resizingImageEdit.PageIndex);
                SelectAnnotation(_resizingImageEdit, _resizingImageEdit.TargetBounds);
                MarkDirty();
                SetStatus("Image resize committed - save to apply white-out + overdraw");
                _resizingImageEdit = null;
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _annotationCanvas.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle rect:
                    if (rect.Width > 3 && rect.Height > 3)
                    {
                        var ha = new HighlightAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ha.SetColor(_highlightColor);
                        AddAnnotation(ha);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rect);
                    }
                    break;

                case EditTool.Crop when _activePreview is Rectangle rect:
                    if (rect.Width > 5 && rect.Height > 5)
                    {
                        _activeCrop = new CropAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ShowCropPopup();
                        SetStatus("Crop rectangle selected - choose Apply crop, Reset, or Cancel");
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rect);
                        _activePreview = null;
                        _activeCrop = null;
                    }
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_activeInk.Points.Count > 2)
                    {
                        AddAnnotation(_activeInk);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;
            }
            _activePreview = null;
        }

        private void ClearCropSelection()
        {
            bool hasCropSelection = _activeCrop is not null || _currentTool == EditTool.Crop;
            if (!hasCropSelection) return;

            if (_activePreview is Rectangle rect)
                _annotationCanvas.Children.Remove(rect);

            if (_activePreview is Rectangle)
                _activePreview = null;
            _activeCrop = null;
            if (_currentTool == EditTool.Crop)
                SetStatus("Crop cleared - drag a new crop rectangle");
        }

        private async void ApplyCrop_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null)
            {
                KillerDialog.Show(this, "Open a PDF first.");
                return;
            }

            if (_activeCrop is null)
            {
                KillerDialog.Show(this, "Drag a crop rectangle first.", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int pageIdx = _activeCrop.PageIndex;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount || !_renderDims.ContainsKey(pageIdx))
            {
                KillerDialog.Show(this, "The selected crop page is no longer available.", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                ClearCropSelection();
                return;
            }

            string sourcePath = _currentFile;
            int selectedIdx = PageList.SelectedIndex;
            bool applyToAll = _cropApplyAllCheck?.IsChecked == true;

            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _openCancellationTokenSource.Token;

            SetFileOperationBusy(true, applyToAll ? "Applying crop to all pages..." : $"Applying crop to page {pageIdx + 1}...");
            try
            {
                CommitActiveTextBox();
                var cropRect = CanvasRectToPdfCropRect(pageIdx, _activeCrop.Bounds);
                _doc.Close();
                _doc = null;

                string croppedPath = await Task.Run(() => CropService.Apply(sourcePath, pageIdx, cropRect, applyToAll), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await OpenFileCoreAsync(croppedPath, null, cancellationToken);
                await FinishOpenFileAsync(result, cancellationToken);
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                ClearCropSelection();
                MarkDirty();
                SetStatus(applyToAll ? "Crop applied to all pages" : $"Crop applied to page {pageIdx + 1}");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Crop canceled");
            }
            catch (Exception ex)
            {
                try
                {
                    if (_doc is null && System.IO.File.Exists(sourcePath))
                    {
                        var restoreResult = await OpenFileCoreAsync(sourcePath, null, CancellationToken.None);
                        await FinishOpenFileAsync(restoreResult, CancellationToken.None);
                        if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                            PageList.SelectedIndex = selectedIdx;
                    }
                }
                catch { }
                SetFileOperationBusy(false);
                KillerDialog.Show(this, $"Crop failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private Rect CanvasRectToPdfCropRect(int pageIdx, Rect canvasBounds)
        {
            var (renderW, renderH) = _renderDims[pageIdx];
            var page = _doc!.Pages[pageIdx];
            var box = GetVisiblePageBox(page);
            double boxLeft = Math.Min(box.X1, box.X2);
            double boxRight = Math.Max(box.X1, box.X2);
            double boxBottom = Math.Min(box.Y1, box.Y2);
            double boxTop = Math.Max(box.Y1, box.Y2);
            double sx = (boxRight - boxLeft) / renderW;
            double sy = (boxTop - boxBottom) / renderH;

            double left = boxLeft + canvasBounds.Left * sx;
            double right = boxLeft + canvasBounds.Right * sx;
            double top = boxTop - canvasBounds.Top * sy;
            double bottom = boxTop - canvasBounds.Bottom * sy;
            return new Rect(left, bottom, right - left, top - bottom);
        }

        private static PdfRectangle GetVisiblePageBox(PdfPage page)
        {
            var crop = page.CropBox;
            if (Math.Abs(crop.X2 - crop.X1) > 0.1 && Math.Abs(crop.Y2 - crop.Y1) > 0.1)
                return crop;
            return page.MediaBox;
        }

        // ============================================================
        // Selection
        // ============================================================

        private bool HitTestAnnotation(PageAnnotation annot, Point pos, out Rect bounds)
        {
            switch (annot)
            {
                case HighlightAnnotation ha:
                    bounds = ha.Bounds;
                    return bounds.Contains(pos);

                case TextAnnotation ta:
                    var ft = new FormattedText(ta.Content,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), ta.FontSize, Brushes.Black,
                        VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip);
                    bounds = new Rect(ta.Position.X, ta.Position.Y, ft.Width + 8, ft.Height + 8);
                    return bounds.Contains(pos);

                case InkAnnotation ia when ia.Points.Count > 0:
                    bool near = ia.Points.Any(p =>
                        Math.Sqrt((p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y)) < 15);
                    if (near)
                    {
                        double minX = ia.Points.Min(p => p.X);
                        double minY = ia.Points.Min(p => p.Y);
                        double maxX = ia.Points.Max(p => p.X);
                        double maxY = ia.Points.Max(p => p.Y);
                        bounds = new Rect(minX, minY, Math.Max(maxX - minX, 4), Math.Max(maxY - minY, 4));
                        return true;
                    }
                    bounds = Rect.Empty;
                    return false;

                case TextEditAnnotation tea:
                    bounds = tea.OriginalBounds;
                    return bounds.Contains(pos);

                case ImageEditAnnotation iea:
                    bounds = iea.TargetBounds;
                    return bounds.Contains(pos);

                case SignatureAnnotation sa:
                    double sigW = sa.SourceWidth * sa.Scale;
                    double sigH = sa.SourceHeight * sa.Scale;
                    bounds = new Rect(sa.Position.X, sa.Position.Y, sigW, sigH);
                    return bounds.Contains(pos);

                default:
                    bounds = Rect.Empty;
                    return false;
            }
        }

        private void SelectAnnotation(PageAnnotation annot, Rect bounds)
        {
            ClearSelection();
            _selectedAnnotation = annot;
            _selectionBorder = new Border
            {
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(2),
                Background = FrozenSolidColorBrush(Color.FromArgb(20, 74, 222, 128)),
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
            _annotationCanvas.Children.Add(_selectionBorder);
            if (annot is ImageEditAnnotation)
            {
                _imageResizeHandle = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = (SolidColorBrush)FindResource("AccentGreen"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    Cursor = Cursors.SizeNWSE
                };
                Canvas.SetLeft(_imageResizeHandle, bounds.Right - 2);
                Canvas.SetTop(_imageResizeHandle, bounds.Bottom - 2);
                _annotationCanvas.Children.Add(_imageResizeHandle);
            }
            SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation - press Delete to remove");
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void ClearSelection()
        {
            if (_selectionBorder is not null)
            {
                _annotationCanvas.Children.Remove(_selectionBorder);
                _selectionBorder = null;
            }
            if (_imageResizeHandle is not null)
            {
                _annotationCanvas.Children.Remove(_imageResizeHandle);
                _imageResizeHandle = null;
            }
            _selectedAnnotation = null;
        }

        private void DeleteSelected()
        {
            if (_selectedAnnotation is null) return;
            int pageIdx = _selectedAnnotation.PageIndex;
            if (_annotations.ContainsKey(pageIdx))
                _annotations[pageIdx].Remove(_selectedAnnotation);
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            MarkDirty();
            SetStatus("Deleted selected annotation");
        }

        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);
                _selectedText = page.Text;
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus("No text found on this page");
                    return;
                }
                Clipboard.SetText(_selectedText);
                // Visual feedback: highlight entire canvas
                ClearTextSelection();
                _selectRect = new Rectangle
                {
                    Fill = FrozenSolidColorBrush(Color.FromArgb(30, 74, 130, 255)),
                    Stroke = FrozenSolidColorBrush(Color.FromArgb(80, 74, 130, 255)),
                    StrokeThickness = 1,
                    Width = _annotationCanvas.Width,
                    Height = _annotationCanvas.Height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_selectRect, 0);
                Canvas.SetTop(_selectRect, 0);
                _annotationCanvas.Children.Add(_selectRect);
                SetStatus($"Selected all text - copied to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Select all error: {ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus($"Copied to clipboard");
            }
            else
            {
                SetStatus("No text selected - drag to select text");
            }
        }

        private void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                _annotationCanvas.Children.Remove(_selectRect);
                _selectRect = null;
            }
            _selectedText = null;
        }

        private void ExtractTextFromRegion(int pageIdx, Rect canvasBounds)
        {
            if (_currentFile is null || pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = pdfW / renderW;
                double sy = pdfH / renderH;

                // Convert canvas rect to PDF coordinates (flip Y - PDF origin is bottom-left)
                double pdfLeft = canvasBounds.Left * sx;
                double pdfRight = canvasBounds.Right * sx;
                double pdfTop = pdfH - (canvasBounds.Top * sy);
                double pdfBottom = pdfH - (canvasBounds.Bottom * sy);
                // pdfTop > pdfBottom because of Y flip
                double pdfMinY = Math.Min(pdfTop, pdfBottom);
                double pdfMaxY = Math.Max(pdfTop, pdfBottom);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var bb = w.BoundingBox;
                        double cx = (bb.Left + bb.Right) / 2;
                        double cy = (bb.Bottom + bb.Top) / 2;
                        return cx >= pdfLeft && cx <= pdfRight && cy >= pdfMinY && cy <= pdfMaxY;
                    })
                    .OrderByDescending(w => w.BoundingBox.Top)
                    .ThenBy(w => w.BoundingBox.Left)
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus("No text found in selection");
                    ClearTextSelection();
                    return;
                }

                // Group into lines by Y proximity
                var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
                double lastY = double.MaxValue;
                foreach (var w in words)
                {
                    double wy = w.BoundingBox.Top;
                    if (Math.Abs(wy - lastY) > 3)
                    {
                        lines.Add(new List<UglyToad.PdfPig.Content.Word>());
                        lastY = wy;
                    }
                    lines[^1].Add(w);
                }

                _selectedText = string.Join("\n",
                    lines.Select(line => string.Join(" ", line.Select(w => w.Text))));

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"Copied {wordCount} word(s) to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Text extraction error: {ex.Message}");
                ClearTextSelection();
            }
        }

        // ============================================================
        // Search (Ctrl+F)
        // ============================================================

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid
                _searchBox = new TextBox
                {
                    Width = 260,
                    Height = 28,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Background = FrozenSolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
                    Foreground = FrozenSolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(_searchBox, "Search text");
                AutomationProperties.SetHelpText(_searchBox, "Enter text to find in the current PDF. Press Enter for next result and Shift Enter for previous result.");
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;

                _searchStatus = new TextBlock
                {
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                AutomationProperties.SetName(_searchStatus, "Search status");
                AutomationProperties.SetHelpText(_searchStatus, "Search result status");
                AutomationProperties.SetLiveSetting(_searchStatus, AutomationLiveSetting.Polite);

                var closeBtn = new Button
                {
                    Content = "\ue711",  // MDL2 Cancel glyph \u2014 matches ToolbarButton font
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("ToolbarButton"),
                    ToolTip = "Close search (Esc)"
                };
                AutomationProperties.SetName(closeBtn, "Close search");
                AutomationProperties.SetHelpText(closeBtn, "Close the search bar. Shortcut Escape.");
                closeBtn.Click += (s, e) => CloseSearchBar();

                var searchIcon = new TextBlock
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(8)
                };
                panel.Children.Add(searchIcon);
                panel.Children.Add(_searchBox);
                panel.Children.Add(_searchStatus);
                panel.Children.Add(closeBtn);

                _searchBar = new Border
                {
                    Background = FrozenSolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(0, 0, 4, 4),
                    Padding = new Thickness(4),
                    Child = panel,
                    Margin = new Thickness(0, 0, 16, 0)
                };

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    Panel.SetZIndex(_searchBar, 100);
                    previewGrid.Children.Add(_searchBar);
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            _searchBox!.Text = "";
            if (_searchStatus != null) _searchStatus.Text = "Enter = next  Shift+Enter = prev";
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        private void CloseSearchBar()
        {
            if (_searchBar is not null)
                _searchBar.Visibility = Visibility.Collapsed;
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length >= 2)
                RunSearch(text);
            else
            {
                ClearSearchHighlights();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
            }
        }

        private void RunSearch(string query)
        {
            ClearSearchHighlights();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _currentFile is null)
            {
                if (_searchStatus != null) _searchStatus.Text = "";
                return;
            }

            try
            {
                string lowerQuery = query.ToLowerInvariant();
                int totalHits = 0;

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                for (int pi = 0; pi < pigDoc.NumberOfPages; pi++)
                {
                    var page = pigDoc.GetPage(pi + 1);
                    var hits = FindMatchesOnPage(page, lowerQuery);
                    if (hits.Count > 0)
                    {
                        _allSearchRects[pi] = hits;
                        _searchResultPages.Add(pi);
                        totalHits += hits.Count;
                    }
                }

                if (_searchResultPages.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "No matches";
                    return;
                }

                // Start from current page or the first page with results
                int startPage = PageList.SelectedIndex;
                _searchPageCursor = _searchResultPages.FindIndex(p => p >= startPage);
                if (_searchPageCursor < 0) _searchPageCursor = 0;

                if (_searchStatus != null)
                    _searchStatus.Text = totalHits == 1
                        ? $"1 match ({_searchResultPages.Count} page)"
                        : $"{totalHits} matches ({_searchResultPages.Count} page{(_searchResultPages.Count != 1 ? "s" : "")})";

                int targetPage = _searchResultPages[_searchPageCursor];
                if (PageList.SelectedIndex != targetPage)
                    PageList.SelectedIndex = targetPage;  // triggers SelectionChanged -> HighlightSearchResultsOnCurrentPage
                else
                    HighlightSearchResultsOnCurrentPage();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "Search error";
            }
        }

        private static List<(double left, double bottom, double right, double top)> FindMatchesOnPage(
            UglyToad.PdfPig.Content.Page page, string lowerQuery)
        {
            var result = new List<(double left, double bottom, double right, double top)>();
            var words = page.GetWords().ToList();

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].Text.ToLowerInvariant().Contains(lowerQuery))
                {
                    var bb = words[i].BoundingBox;
                    result.Add((bb.Left, bb.Bottom, bb.Right, bb.Top));
                    continue;
                }

                // Multi-word match
                string combined = words[i].Text;
                for (int j = i + 1; j < words.Count && combined.Length < lowerQuery.Length + 20; j++)
                {
                    combined += " " + words[j].Text;
                    if (combined.ToLowerInvariant().Contains(lowerQuery))
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        for (int k = i; k <= j; k++)
                        {
                            var wbb = words[k].BoundingBox;
                            minX = Math.Min(minX, wbb.Left);
                            minY = Math.Min(minY, wbb.Bottom);
                            maxX = Math.Max(maxX, wbb.Right);
                            maxY = Math.Max(maxY, wbb.Top);
                        }
                        result.Add((minX, minY, maxX, maxY));
                        break;
                    }
                }
            }
            return result;
        }

        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            int curPage = PageList.SelectedIndex;
            if (!_allSearchRects.ContainsKey(curPage)) return;
            if (!_renderDims.ContainsKey(curPage)) return;

            var (renderW, renderH) = _renderDims[curPage];

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile!);
                var page = pigDoc.GetPage(curPage + 1);
                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = renderW / pdfW;
                double sy = renderH / pdfH;

                foreach (var (left, bottom, right, top) in _allSearchRects[curPage])
                    AddSearchHighlight(left, bottom, right, top, sx, sy, renderH);
            }
            catch { }
        }

        private void SearchNextResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor + 1) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void SearchPrevResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor - 1 + _searchResultPages.Count) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void AddSearchHighlight(double left, double bottom, double right, double top,
            double sx, double sy, double renderH)
        {
            double cx = left  * sx;
            double cy = renderH - (top * sy);
            double cw = (right - left) * sx;
            double ch = (top - bottom) * sy;
            var rect = new Rectangle
            {
                Fill = FrozenSolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                Stroke = FrozenSolidColorBrush(Color.FromArgb(160, 255, 165, 0)),
                StrokeThickness = 1,
                Width = Math.Max(cw, 4),
                Height = Math.Max(ch, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            _annotationCanvas.Children.Add(rect);
        }

        private void ClearSearchHighlights()
        {
            var toRemove = _annotationCanvas.Children.OfType<Rectangle>()
                .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
            foreach (var r in toRemove)
                _annotationCanvas.Children.Remove(r);
            if (_searchStatus is not null)
                _searchStatus.Text = "";
        }

        // ============================================================
        // Inline text editing (double-click)
        // ============================================================

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;
            ClearSelection();

            // Commit any existing edit first
            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];
                var hit = _contentEditor.FindTextRunAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
                if (hit is null) { SetStatus("No text found at this position"); return; }

                // Show editable TextBox over the line
                var tb = new TextBox
                {
                    Text = hit.Text,
                    Background = FrozenSolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                    Foreground = Brushes.Black,
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(2),
                    FontFamily = new FontFamily(hit.FontName),
                    FontSize = hit.FontSize,
                    MinWidth = Math.Max(hit.CanvasBounds.Width + 20, 100),
                    Height = Math.Max(hit.CanvasBounds.Height + 12, 24),
                    Padding = new Thickness(2, 0, 2, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    AcceptsReturn = false,
                    Tag = new TextEditContext
                    {
                        PageIndex = pageIdx,
                        OriginalText = hit.Text,
                        CanvasBounds = hit.CanvasBounds,
                        Position = hit.Position,
                        FontSize = hit.FontSize,
                        FontName = hit.FontName
                    }
                };
                Canvas.SetLeft(tb, hit.CanvasBounds.X);
                Canvas.SetTop(tb, hit.CanvasBounds.Y);
                _annotationCanvas.Children.Add(tb);
                _activeTextBox = tb;

                // Show white-out behind the edit box so original text is hidden
                var whiteout = new Rectangle
                {
                    Fill = Brushes.White,
                    Width = hit.CanvasBounds.Width + 4,
                    Height = hit.CanvasBounds.Height + 4,
                    IsHitTestVisible = false,
                    Tag = "EditWhiteout"
                };
                Canvas.SetLeft(whiteout, hit.CanvasBounds.X - 2);
                Canvas.SetTop(whiteout, hit.CanvasBounds.Y - 2);
                int tbIdx = _annotationCanvas.Children.IndexOf(tb);
                _annotationCanvas.Children.Insert(tbIdx, whiteout);

                tb.KeyDown += EditTextBox_KeyDown;
                tb.Loaded += (s, ev) =>
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.SelectAll();
                    tb.LostFocus += EditTextBox_LostFocus;
                };

                SetStatus("Editing text - Enter to save, Escape to cancel");
            }
            catch (Exception ex)
            {
                SetStatus($"Text edit error: {ex.Message}");
            }
        }

        /// <summary>Context data attached to an inline text edit TextBox via Tag.</summary>
        private class TextEditContext
        {
            public int PageIndex { get; set; }
            public string OriginalText { get; set; } = "";
            public Rect CanvasBounds { get; set; }
            public Point Position { get; set; }
            public double FontSize { get; set; }
            public string FontName { get; set; } = "Segoe UI";
        }

        private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitTextEdit();
                e.Handled = true;
            }
        }

        private void EditTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is not null && _activeTextBox.Tag is TextEditContext)
            {
                Dispatcher.BeginInvoke(new Action(CommitTextEdit),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CancelTextEdit()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            _annotationCanvas.Children.Remove(tb);
            // Remove the whiteout rectangle
            var whiteout = _annotationCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _annotationCanvas.Children.Remove(whiteout);
            SetStatus("Text edit cancelled");
        }

        private void CommitTextEdit()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not TextEditContext ctx) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            string newText = tb.Text.Trim();
            _annotationCanvas.Children.Remove(tb);

            // Remove the whiteout rectangle
            var whiteout = _annotationCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _annotationCanvas.Children.Remove(whiteout);

            if (string.IsNullOrEmpty(newText) || newText == ctx.OriginalText)
            {
                SetStatus(newText == ctx.OriginalText ? "No changes made" : "Text edit cancelled (empty)");
                return;
            }

            // Create a TextEditAnnotation
            var edit = new TextEditAnnotation
            {
                PageIndex = ctx.PageIndex,
                OriginalBounds = ctx.CanvasBounds,
                Position = ctx.Position,
                NewContent = newText,
                OriginalContent = ctx.OriginalText,
                FontSize = ctx.FontSize,
                FontName = ctx.FontName
            };
            AddAnnotation(edit);
            RenderAllAnnotations(ctx.PageIndex);
            SetStatus($"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\"");
        }

        private void EditImageAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;

            if (_annotations.TryGetValue(pageIdx, out var pageAnnots))
            {
                for (int i = pageAnnots.Count - 1; i >= 0; i--)
                {
                    if (pageAnnots[i] is ImageEditAnnotation existing && existing.TargetBounds.Contains(canvasPos))
                    {
                        SelectAnnotation(existing, existing.TargetBounds);
                        ShowImageEditMenu(existing);
                        return;
                    }
                }
            }

            var (renderW, renderH) = _renderDims[pageIdx];
            var hit = _contentEditor.FindImageAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
            if (hit is null)
            {
                SetStatus("No image found at this position");
                return;
            }

            var edit = new ImageEditAnnotation
            {
                PageIndex = pageIdx,
                OriginalBounds = hit.CanvasBounds,
                TargetBounds = hit.CanvasBounds,
                OriginalImageData = CapturePageImageRegion(hit.CanvasBounds)
            };
            AddAnnotation(edit);
            RenderAllAnnotations(pageIdx);
            SelectAnnotation(edit, edit.TargetBounds);
            ShowImageEditMenu(edit);
            SetStatus("Image selected - replace, delete, or drag the green handle to resize");
        }

        private string? CapturePageImageRegion(Rect bounds)
        {
            if (PageImage.Source is not BitmapSource source) return null;

            int x = Math.Max(0, (int)Math.Floor(bounds.X));
            int y = Math.Max(0, (int)Math.Floor(bounds.Y));
            int right = Math.Min(source.PixelWidth, (int)Math.Ceiling(bounds.Right));
            int bottom = Math.Min(source.PixelHeight, (int)Math.Ceiling(bounds.Bottom));
            if (right <= x || bottom <= y) return null;

            var crop = new CroppedBitmap(source, new Int32Rect(x, y, right - x, bottom - y));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(crop));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }

        private void ShowImageEditMenu(ImageEditAnnotation edit)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Replace Image...", (s, e) => ReplaceImageEdit(edit)));
            menu.Items.Add(MakeMenuItem("Delete Image", (s, e) => DeleteImageEdit(edit)));
            menu.Items.Add(MakeMenuItem("Reset Size", (s, e) => ResetImageEditSize(edit)));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Resize: drag the green handle" });
            menu.PlacementTarget = _annotationCanvas;
            menu.IsOpen = true;
        }

        private void ReplaceImageEdit(ImageEditAnnotation edit)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Select replacement image"
            };
            if (dlg.ShowDialog() != true) return;

            edit.ReplacementImagePath = dlg.FileName;
            edit.IsDeleted = false;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Replacement image selected - save to apply white-out + overdraw");
        }

        private void DeleteImageEdit(ImageEditAnnotation edit)
        {
            edit.IsDeleted = true;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Image marked for deletion - save to apply white-out");
        }

        private void ResetImageEditSize(ImageEditAnnotation edit)
        {
            edit.TargetBounds = edit.OriginalBounds;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Image size reset");
        }

        private void ResizeImageEditPreview(Point pos)
        {
            if (_resizingImageEdit is null) return;

            double newW = Math.Max(8, _imageResizeOriginalBounds.Width + (pos.X - _imageResizeStart.X));
            double newH = Math.Max(8, _imageResizeOriginalBounds.Height + (pos.Y - _imageResizeStart.Y));
            _resizingImageEdit.TargetBounds = new Rect(_imageResizeOriginalBounds.X, _imageResizeOriginalBounds.Y, newW, newH);

            if (_selectionBorder is not null)
            {
                _selectionBorder.Width = newW + 8;
                _selectionBorder.Height = newH + 8;
            }
            if (_imageResizeHandle is not null)
            {
                Canvas.SetLeft(_imageResizeHandle, _resizingImageEdit.TargetBounds.Right - 2);
                Canvas.SetTop(_imageResizeHandle, _resizingImageEdit.TargetBounds.Bottom - 2);
            }
        }

        // ============================================================
        // Text box handling
        // ============================================================

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            var tb = new TextBox
            {
                Background = FrozenSolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                Foreground = Brushes.Black,
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                MinWidth = 120,
                MinHeight = 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                Tag = pageIdx
            };
            AutomationProperties.SetName(tb, "Annotation text");
            AutomationProperties.SetHelpText(tb, "Type annotation text. Press Enter to save or Escape to cancel.");
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            _annotationCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.KeyDown += TextBox_KeyDown;
            // Defer focus until the TextBox is actually rendered
            tb.Loaded += (s, e) =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
                tb.LostFocus += TextBox_LostFocus;
            };
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_activeTextBox is not null)
                {
                    _annotationCanvas.Children.Remove(_activeTextBox);
                    _activeTextBox = null;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Only commit if the TextBox actually has content
            if (_activeTextBox is not null && !string.IsNullOrWhiteSpace(_activeTextBox.Text))
            {
                Dispatcher.BeginInvoke(new Action(CommitActiveTextBox),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox is null) return;
            // If it's an inline text edit, use the dedicated commit path
            if (_activeTextBox.Tag is TextEditContext)
            {
                CommitTextEdit();
                return;
            }
            var tb = _activeTextBox;
            _activeTextBox = null;

            string content = tb.Text.Trim();
            int pageIdx = tb.Tag is int idx ? idx : PageList.SelectedIndex;
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);

            _annotationCanvas.Children.Remove(tb);

            if (!string.IsNullOrEmpty(content))
            {
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize
                };
                AddAnnotation(ta);
                RenderTextAnnotation(ta);
            }
        }

        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Don't intercept keys when typing in a TextBox
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;

            if (Keyboard.Modifiers == ModifierKeys.Alt)
            {
                var accessKey = e.SystemKey != Key.None ? e.SystemKey : e.Key;
                switch (accessKey)
                {
                    case Key.O: Open_Click(this, e); break;
                    case Key.W: CloseFile(); break;
                    case Key.M: Merge_Click(this, e); break;
                    case Key.E: Split_Click(this, e); break;
                    case Key.D: Delete_Click(this, e); break;
                    case Key.U: MoveUp_Click(this, e); break;
                    case Key.N: MoveDown_Click(this, e); break;
                    case Key.S: SaveAs_Click(this, e); break;
                    case Key.F: SaveFlattened_Click(this, e); break;
                    case Key.P: Print_Click(this, e); break;
                    case Key.Q: SetTool(EditTool.Select); break;
                    case Key.T: SetTool(EditTool.Text); break;
                    case Key.H: SetTool(EditTool.Highlight); break;
                    case Key.R: SetTool(EditTool.Draw); break;
                    case Key.G: ToolSignature_Click(this, e); break;
                    case Key.C: ToolCrop_Click(this, e); break;
                    case Key.Z: Undo_Click(this, e); break;
                    case Key.L: ClearAnnotations_Click(this, e); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selectedAnnotation is not null)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Undo_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseFile();
                e.Handled = true;
            }
        }

        // ============================================================
        // Annotation management
        // ============================================================

        private void AddAnnotation(PageAnnotation annotation)
        {
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = new List<PageAnnotation>();
            _annotations[annotation.PageIndex].Add(annotation);
            MarkDirty();
        }

        private void RenderTextAnnotation(TextAnnotation ta)
        {
            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = Brushes.Black,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = ta.FontSize,
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(tb, ta.Position.X);
            Canvas.SetTop(tb, ta.Position.Y);
            _annotationCanvas.Children.Add(tb);
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            _annotationCanvas.Children.Clear();
            if (!_annotations.ContainsKey(pageIndex)) return;

            foreach (var annot in _annotations[pageIndex])
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        RenderTextAnnotation(ta);
                        break;
                    case HighlightAnnotation ha:
                        var rect = new Rectangle
                        {
                            Fill = FrozenSolidColorBrush(ha.GetColor()),
                            Width = ha.Bounds.Width,
                            Height = ha.Bounds.Height
                        };
                        Canvas.SetLeft(rect, ha.Bounds.X);
                        Canvas.SetTop(rect, ha.Bounds.Y);
                        _annotationCanvas.Children.Add(rect);
                        break;
                    case InkAnnotation ia:
                        if (ia.Points.Count < 2) continue;
                        var poly = new Polyline
                        {
                            Stroke = FrozenSolidColorBrush(ia.GetColor()),
                            StrokeThickness = ia.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round
                        };
                        foreach (var pt in ia.Points) poly.Points.Add(pt);
                        _annotationCanvas.Children.Add(poly);
                        break;
                    case TextEditAnnotation tea:
                        // White-out original text
                        var wo = new Rectangle
                        {
                            Fill = Brushes.White,
                            Width = tea.OriginalBounds.Width + 4,
                            Height = tea.OriginalBounds.Height + 4,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(wo, tea.OriginalBounds.X - 2);
                        Canvas.SetTop(wo, tea.OriginalBounds.Y - 2);
                        _annotationCanvas.Children.Add(wo);
                        // Draw replacement text
                        var etb = new TextBlock
                        {
                            Text = tea.NewContent,
                            Foreground = Brushes.Black,
                            FontFamily = new FontFamily(tea.FontName),
                            FontSize = tea.FontSize,
                            Padding = new Thickness(0)
                        };
                        Canvas.SetLeft(etb, tea.Position.X);
                        Canvas.SetTop(etb, tea.Position.Y);
                        _annotationCanvas.Children.Add(etb);
                        break;

                    case ImageEditAnnotation iea:
                        var imageWhiteout = new Rectangle
                        {
                            Fill = Brushes.White,
                            Width = iea.OriginalBounds.Width + 4,
                            Height = iea.OriginalBounds.Height + 4,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(imageWhiteout, iea.OriginalBounds.X - 2);
                        Canvas.SetTop(imageWhiteout, iea.OriginalBounds.Y - 2);
                        _annotationCanvas.Children.Add(imageWhiteout);

                        if (!iea.IsDeleted)
                        {
                            var source = LoadImageEditBitmap(iea);
                            if (source is not null)
                            {
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = source,
                                    Width = iea.TargetBounds.Width,
                                    Height = iea.TargetBounds.Height,
                                    Stretch = Stretch.Fill,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, iea.TargetBounds.X);
                                Canvas.SetTop(imgCtrl, iea.TargetBounds.Y);
                                _annotationCanvas.Children.Add(imgCtrl);
                            }
                        }
                        break;

                    case SignatureAnnotation sa:
                        if (sa.ImageData is not null)
                        {
                            // Image-based signature
                            try
                            {
                                var imgBytes = Convert.FromBase64String(sa.ImageData);
                                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                using (var imageStream = new System.IO.MemoryStream(imgBytes))
                                {
                                    bmp.BeginInit();
                                    bmp.StreamSource = imageStream;
                                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                    bmp.EndInit();
                                }
                                if (bmp.CanFreeze) bmp.Freeze();
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = bmp,
                                    Width = sa.SourceWidth * sa.Scale,
                                    Height = sa.SourceHeight * sa.Scale,
                                    Stretch = System.Windows.Media.Stretch.Uniform,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, sa.Position.X);
                                Canvas.SetTop(imgCtrl, sa.Position.Y);
                                _annotationCanvas.Children.Add(imgCtrl);
                            }
                            catch { /* skip broken image */ }
                        }
                        else
                        {
                            foreach (var stroke in sa.Strokes)
                            {
                                if (stroke.Count < 2) continue;
                                var sigPoly = new Polyline
                                {
                                    Stroke = Brushes.Black,
                                    StrokeThickness = 2 * sa.Scale,
                                    StrokeLineJoin = PenLineJoin.Round,
                                    StrokeStartLineCap = PenLineCap.Round,
                                    StrokeEndLineCap = PenLineCap.Round
                                };
                                foreach (var pt in stroke)
                                    sigPoly.Points.Add(new Point(
                                        sa.Position.X + pt.X * sa.Scale,
                                        sa.Position.Y + pt.Y * sa.Scale));
                                _annotationCanvas.Children.Add(sigPoly);
                            }
                        }
                        break;
                }
            }
        }

        private static BitmapSource? LoadImageEditBitmap(ImageEditAnnotation edit)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                if (!string.IsNullOrWhiteSpace(edit.ReplacementImagePath) && System.IO.File.Exists(edit.ReplacementImagePath))
                {
                    bmp.UriSource = new Uri(edit.ReplacementImagePath, UriKind.Absolute);
                }
                else if (!string.IsNullOrEmpty(edit.OriginalImageData))
                {
                    bmp.StreamSource = new MemoryStream(Convert.FromBase64String(edit.OriginalImageData));
                }
                else
                {
                    return null;
                }
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (!_annotations.ContainsKey(pageIdx) || _annotations[pageIdx].Count == 0)
            {
                SetStatus("Nothing to undo");
                return;
            }
            _annotations[pageIdx].RemoveAt(_annotations[pageIdx].Count - 1);
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            SetStatus("Undid last annotation");
        }

        private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (_annotations.ContainsKey(pageIdx) && _annotations[pageIdx].Count > 0)
            {
                _annotations[pageIdx].Clear();
                MarkDirty();
            }
            ClearSelection();
            _annotationCanvas.Children.Clear();
            SetStatus("Cleared annotations on this page");
        }

        // ============================================================
        // Dirty / unsaved-change tracking
        // ============================================================

        private void MarkDirty(bool dirty = true)
        {
            _isDirty = dirty;
            if (_saveAsBtnRef != null)
            {
                _saveAsBtnRef.Foreground = dirty
                    ? FrozenSolidColorBrush(Color.FromRgb(0xff, 0xa5, 0x00)) // orange = unsaved
                    : (SolidColorBrush)FindResource("AccentGreen");
            }
        }

        // ============================================================
        // Close file (Ctrl+W) — returns to drop-zone state
        // ============================================================

        private void CloseFile()
        {
            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _renderCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = null;
            _renderCancellationTokenSource = null;
            if (_doc is null) return;
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    "You have unsaved changes. Close this file without saving?",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _doc.Close();
            _doc = null;
            _currentFile = null;
            _annotations.Clear();
            InvalidateRenderCache();
            _contentEditor.ClearCache();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            PageList.Items.Clear();
            if (FindName("PageImage") is System.Windows.Controls.Image img)
            {
                img.Source = null;
                img.Width = double.NaN;
                img.Height = double.NaN;
            }
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideSignaturePopup();
            HideCropPopup();
            ClearCropSelection();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            MarkDirty(false);
            SetStatus("Ready");
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseFile();

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
            if (dlg.ShowDialog() == true) await OpenFileAsync(dlg.FileName);
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Merge failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "Select pages to extract."); return; }
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save extracted pages as" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus($"Extracted {indices.Count} page(s) to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Split failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "Select pages to delete."); return; }
            var result = KillerDialog.Show(this, $"Delete {selected.Count} {(selected.Count == 1 ? "page" : "pages")}?", "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus($"Deleted {indices.Count} page(s) - {_doc?.PageCount} remaining");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Delete failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx - 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx + 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx + 1;
        }

        private async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                SetFileOperationBusy(true, "Saving...");
                var doc = _doc;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                string targetFile = dlg.FileName;
                string status;

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAsync(() => doc.Save(targetFile), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    status = $"Saved with annotations to {System.IO.Path.GetFileName(targetFile)}";
                }
                else
                {
                    await _pdfDocumentService.SaveAsync(() => doc.Save(targetFile), CancellationToken.None);
                    status = $"Saved to {System.IO.Path.GetFileName(targetFile)}";
                }

                MarkDirty(false);
                SetStatus(status);
            }
            catch (Exception ex)
            {
                SetFileOperationBusy(false);
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF" };
            if (dlg.ShowDialog() != true) return;
            SetFileOperationBusy(true, "Flattening...");
            try
            {
                var doc = _doc;
                var pageSizes = GetPageSizes(doc);
                string sourcePath;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_clean_{Guid.NewGuid():N}.pdf");
                    var tempBurned = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_burned_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAsync(() => doc.Save(tempBurned), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    sourcePath = tempBurned;
                }
                else
                {
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_src_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(temp), CancellationToken.None);
                    sourcePath = temp;
                }

                await _pdfDocumentService.SaveFlattenedAsync(sourcePath, dlg.FileName, pageSizes, CancellationToken.None);
                MarkDirty(false);
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                SetFileOperationBusy(false);
                KillerDialog.Show(this, $"Flatten failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async Task<PdfDocument> RestoreDocumentAsync(PdfDocument currentDoc, string cleanPath, CancellationToken cancellationToken)
        {
            var restoredDoc = await _pdfDocumentService.OpenPdfSharpAsync(cleanPath, PdfDocumentOpenMode.Modify, cancellationToken);
            currentDoc.Close();
            _doc = restoredDoc;
            _currentFile = cleanPath;
            return restoredDoc;
        }

        private static IReadOnlyList<PdfPageSize> GetPageSizes(PdfDocument doc)
        {
            var pageSizes = new List<PdfPageSize>(doc.PageCount);
            for (int i = 0; i < doc.PageCount; i++)
                pageSizes.Add(new PdfPageSize(doc.Pages[i].Width.Point, doc.Pages[i].Height.Point));
            return pageSizes;
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                new PrintService().Print(this, _doc, _currentFile, hasAnnotations, DrawAnnotationsOnDocument, ReloadPrintedDocument, SetStatus);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Print failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReloadPrintedDocument(string path)
        {
            var previous = _doc;
            PdfDocument reopened;
            try
            {
                reopened = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex) when (KillerPDF.Services.PdfDocumentService.IsOwnerPasswordException(ex))
            {
                reopened = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
            }
            _doc = reopened;
            _currentFile = path;
            previous?.Close();
        }

        // ============================================================
        // Save annotations to PDF
        // ============================================================

        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

            foreach (var kvp in _annotations)
            {
                int pageIdx = kvp.Key;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx >= _doc.PageCount) continue;
                if (!_renderDims.ContainsKey(pageIdx)) continue;

                var page = _doc.Pages[pageIdx];
                var (renderW, renderH) = _renderDims[pageIdx];
                double sx = page.Width.Point / renderW;
                double sy = page.Height.Point / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                foreach (var annot in annots)
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                            var font = new XFont("Segoe UI", ta.FontSize * sy);
                            var lines = ta.Content.Split('\n');
                            double lineH = ta.FontSize * sy * 1.2;
                            double ty = ta.Position.Y * sy + ta.FontSize * sy;
                            foreach (var line in lines)
                            {
                                if (!string.IsNullOrEmpty(line))
                                    gfx.DrawString(line, font, XBrushes.Black, ta.Position.X * sx, ty);
                                ty += lineH;
                            }
                            break;

                        case HighlightAnnotation ha:
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            gfx.DrawRectangle(hBrush,
                                ha.Bounds.X * sx, ha.Bounds.Y * sy,
                                ha.Bounds.Width * sx, ha.Bounds.Height * sy);
                            break;

                        case InkAnnotation ia:
                            if (ia.Points.Count < 2) break;
                            var ic = ia.GetColor();
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx);
                            pen.LineJoin = XLineJoin.Round;
                            pen.LineCap = XLineCap.Round;
                            for (int i = 0; i < ia.Points.Count - 1; i++)
                            {
                                gfx.DrawLine(pen,
                                    ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                    ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                            }
                            break;

                        case TextEditAnnotation tea:
                            // PdfSharpCore cannot surgically edit existing PDF content streams here.
                            // Existing text/image edits are approximated by painting a white rectangle
                            // over the original region, then drawing replacement content on top.
                            var whiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(whiteRect,
                                (tea.OriginalBounds.X - 2) * sx, (tea.OriginalBounds.Y - 2) * sy,
                                (tea.OriginalBounds.Width + 4) * sx, (tea.OriginalBounds.Height + 4) * sy);
                            // Draw replacement text
                            var editFont = new XFont(tea.FontName, tea.FontSize * sy);
                            double ety = tea.Position.Y * sy + tea.FontSize * sy;
                            gfx.DrawString(tea.NewContent, editFont, XBrushes.Black, tea.Position.X * sx, ety);
                            break;

                        case ImageEditAnnotation iea:
                            var imageWhiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(imageWhiteRect,
                                (iea.OriginalBounds.X - 2) * sx, (iea.OriginalBounds.Y - 2) * sy,
                                (iea.OriginalBounds.Width + 4) * sx, (iea.OriginalBounds.Height + 4) * sy);
                            if (!iea.IsDeleted)
                            {
                                try
                                {
                                    XImage? xImg = null;
                                    if (!string.IsNullOrWhiteSpace(iea.ReplacementImagePath) && System.IO.File.Exists(iea.ReplacementImagePath))
                                    {
                                        xImg = XImage.FromFile(iea.ReplacementImagePath);
                                    }
                                    else if (!string.IsNullOrEmpty(iea.OriginalImageData))
                                    {
                                        var imageBytes = Convert.FromBase64String(iea.OriginalImageData);
                                        xImg = XImage.FromStream(() => new MemoryStream(imageBytes));
                                    }

                                    if (xImg is not null)
                                    {
                                        gfx.DrawImage(xImg,
                                            iea.TargetBounds.X * sx, iea.TargetBounds.Y * sy,
                                            iea.TargetBounds.Width * sx, iea.TargetBounds.Height * sy);
                                    }
                                }
                                catch { /* skip broken image edit */ }
                            }
                            break;

                        case SignatureAnnotation sa:
                            if (sa.ImageData is not null)
                            {
                                try
                                {
                                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                    double imgX = sa.Position.X * sx;
                                    double imgY = sa.Position.Y * sy;
                                    double imgW = sa.SourceWidth * sa.Scale * sx;
                                    double imgH = sa.SourceHeight * sa.Scale * sy;
                                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                                }
                                catch { /* skip broken image */ }
                            }
                            else
                            {
                                var sigPen = new XPen(XColors.Black, 2 * sa.Scale * sx);
                                sigPen.LineJoin = XLineJoin.Round;
                                sigPen.LineCap = XLineCap.Round;
                                foreach (var stroke in sa.Strokes)
                                {
                                    for (int i = 0; i < stroke.Count - 1; i++)
                                    {
                                        double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                        double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                        double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                        double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                        gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }

        // ============================================================
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload()
        {
            if (_doc is null || _currentFile is null) return;
            _annotations.Clear();
            InvalidateRenderCache();
            _contentEditor.ClearCache();
            ClearSelection();
            MarkDirty();
            var doc = _doc;
            int selectedIdx = PageList.SelectedIndex;
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"killerpdf_temp_{Guid.NewGuid():N}.pdf");
            doc.Save(tempPath);
            doc.Close();
            _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            _currentFile = tempPath;
            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;
        }

        // ============================================================
        // Zoom
        // ============================================================

        private enum ZoomChange
        {
            In,
            Out,
            Reset
        }

        private void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (e.Delta > 0) Zoom.ZoomIn();
                else Zoom.ZoomOut();
                return;
            }

            // Scroll past bottom -> next page; scroll past top -> previous page
            var sv = PagePreviewPanel;
            if (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight - 1)
            {
                int next = PageList.SelectedIndex + 1;
                if (_doc != null && next < _doc.PageCount)
                {
                    e.Handled = true;
                    PageList.SelectedIndex = next;
                    sv.ScrollToTop();
                }
            }
            else if (e.Delta > 0 && sv.VerticalOffset <= 1)
            {
                int prev = PageList.SelectedIndex - 1;
                if (prev >= 0)
                {
                    e.Handled = true;
                    PageList.SelectedIndex = prev;
                    sv.ScrollToBottom();
                }
            }
        }

        private void Zoom_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ZoomViewModel.ZoomLevel))
                ApplyZoom();
        }

        private void ChangeZoomByCommand(ZoomChange change)
        {
            switch (change)
            {
                case ZoomChange.In:
                    Zoom.ZoomIn();
                    break;
                case ZoomChange.Out:
                    Zoom.ZoomOut();
                    break;
                case ZoomChange.Reset:
                    Zoom.Reset();
                    break;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom.ZoomIn();

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom.ZoomOut();

        private void ResetZoom_Click(object sender, RoutedEventArgs e) => Zoom.Reset();

        private void ApplyZoom()
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = Zoom.ZoomLevel;
                st.ScaleY = Zoom.ZoomLevel;
            }

            CommitActiveTextBox();
            SaveZoomSetting();

            if (PageList.SelectedIndex >= 0 && _doc != null)
                RenderPage(PageList.SelectedIndex);
        }

        private void SaveZoomSetting()
        {
            try
            {
                Properties.Settings.Default.LastZoomLevel = Zoom.ZoomLevel;
                Properties.Settings.Default.Save();
            }
            catch
            {
                // Non-critical user preference; rendering should continue even if settings cannot be saved.
            }
        }

        private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_zoomBox?.SelectedItem is not ZoomLevelOption option) return;
            if (option.IsFitWidth) { FitToWidth(); return; }
            if (option.IsFitPage) { FitToPage(); return; }
            if (option.ZoomLevel is double zoom) Zoom.SetZoomLevel(zoom);
        }

        private void FitToWidth()
        {
            if (PageImage.Source is null || PageImage.ActualWidth <= 0) return;
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;
            Zoom.SetZoomLevel(viewW / PageImage.ActualWidth);
        }

        private void FitToPage()
        {
            if (PageImage.Source is null || PageImage.ActualWidth <= 0 || PageImage.ActualHeight <= 0) return;
            double viewW = PagePreviewPanel.ActualWidth - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;
            Zoom.SetZoomLevel(Math.Min(viewW / PageImage.ActualWidth, viewH / PageImage.ActualHeight));
        }

        private void PageContentGrid_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            double scale = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2.0;
            if (Math.Abs(scale - 1.0) < 0.01) return;
            Zoom.SetZoomLevel(Zoom.ZoomLevel * scale);
            e.Handled = true;
        }

        // ============================================================
        // Drag/drop: file open
        // ============================================================

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0 && files[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    await OpenFileAsync(files[0]);
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

        // ============================================================
        // Drag/drop: page reorder
        // ============================================================

        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _dragStartPoint = e.GetPosition(null);

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (PageList.SelectedIndex >= 0)
                    DragDrop.DoDragDrop(PageList, PageList.SelectedIndex, DragDropEffects.Move);
            }
        }

        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            if (_doc is null || !e.Data.GetDataPresent(typeof(int))) return;
            var doc = _doc;
            int fromIdx = (int)e.Data.GetData(typeof(int))!;
            var pos = e.GetPosition(PageList);
            int toIdx = PageList.Items.Count - 1;
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    var itemPos = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PageList);
                    if (pos.Y < itemPos.Y) { toIdx = i; break; }
                }
            }
            if (fromIdx == toIdx) return;
            var page = doc.Pages[fromIdx];
            doc.Pages.RemoveAt(fromIdx);
            if (toIdx > fromIdx) toIdx--;
            doc.Pages.Insert(toIdx, page);
            SaveTempAndReload();
            PageList.SelectedIndex = toIdx;
        }

        // ============================================================
        // Page selection handler
        // ============================================================

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedIndex >= 0)
            {
                CommitActiveTextBox();
                ClearSelection();
                ClearTextSelection();
                ClearCropSelection();
                RenderPage(PageList.SelectedIndex);
                // Re-highlight search results on this page if a search is active
                if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                    && _allSearchRects.Count > 0)
                    HighlightSearchResultsOnCurrentPage();
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }

    // ============================================================
    // Themed dialog — replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class KillerDialog
    {
        private static readonly System.Windows.Media.Color _green     = System.Windows.Media.Color.FromRgb(0x4a, 0xde, 0x80);
        private static readonly System.Windows.Media.Color _dark      = System.Windows.Media.Color.FromRgb(0x1a, 0x1a, 0x1a);
        private static readonly System.Windows.Media.Color _panel     = System.Windows.Media.Color.FromRgb(0x24, 0x24, 0x24);
        private static readonly System.Windows.Media.Color _text      = System.Windows.Media.Color.FromRgb(0xe0, 0xe0, 0xe0);
        private static readonly System.Windows.Media.Color _border    = System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33);
        private static readonly System.Windows.Media.Color _greenDim  = System.Windows.Media.Color.FromRgb(0x22, 0x54, 0x3d);
        private static readonly System.Windows.Media.Color _greenHov  = System.Windows.Media.Color.FromRgb(0x2d, 0x6a, 0x4f);
        private static readonly System.Windows.Media.Color _hover     = System.Windows.Media.Color.FromRgb(0x2e, 0x2e, 0x2e);

        private static SolidColorBrush FrozenSolidColorBrush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None)
        {
            var result = MessageBoxResult.OK;

            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var outerBorder = new Border
            {
                Background      = FrozenSolidColorBrush(_dark),
                BorderBrush     = FrozenSolidColorBrush(_greenDim),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var root = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = FrozenSolidColorBrush(_panel),
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock
            {
                Text       = title,
                Foreground = FrozenSolidColorBrush(_green),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            root.Children.Add(titleBar);

            // Message
            var msgBorder = new Border { Padding = new Thickness(20, 16, 20, 8) };
            msgBorder.Child = new TextBlock
            {
                Text        = message,
                Foreground  = FrozenSolidColorBrush(_text),
                FontSize    = 13,
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(msgBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Build a minimal ControlTemplate so Background binds correctly and
            // WPF's default blue hover chrome can't override our colors.
            static ControlTemplate MakeBtnTemplate()
            {
                var bf = new FrameworkElementFactory(typeof(Border));
                bf.SetBinding(Border.BackgroundProperty,
                    new System.Windows.Data.Binding("Background")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderBrushProperty,
                    new System.Windows.Data.Binding("BorderBrush")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderThicknessProperty,
                    new System.Windows.Data.Binding("BorderThickness")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.PaddingProperty,
                    new System.Windows.Data.Binding("Padding")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bf.AppendChild(cp);
                return new ControlTemplate(typeof(Button)) { VisualTree = bf };
            }

            Button MakeBtn(string label, MessageBoxResult res, bool accent = false)
            {
                var bgNorm = accent ? FrozenSolidColorBrush(_greenDim) : FrozenSolidColorBrush(_panel);
                var bgHov  = accent ? FrozenSolidColorBrush(_greenHov) : FrozenSolidColorBrush(_hover);
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? FrozenSolidColorBrush(_green) : FrozenSolidColorBrush(_text),
                    BorderBrush     = accent ? FrozenSolidColorBrush(_green) : FrozenSolidColorBrush(_border),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate()
                };
                btn.Click      += (_, _2) => { result = res; win.Close(); };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    btnPanel.Children.Add(MakeBtn("OK", MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.OKCancel:
                    btnPanel.Children.Add(MakeBtn("OK",     MessageBoxResult.OK,     accent: true));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn("Yes", MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn("No",  MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn("Yes",    MessageBoxResult.Yes,    accent: true));
                    btnPanel.Children.Add(MakeBtn("No",     MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child   = btnPanel
            });

            outerBorder.Child = root;
            win.Content = outerBorder;
            win.ShowDialog();
            return result;
        }
    }
}
