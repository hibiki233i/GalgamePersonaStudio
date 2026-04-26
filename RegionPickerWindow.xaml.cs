using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace GalgamePersonaStudio;

public partial class RegionPickerWindow : Window
{
    private const int HotkeyCaptureId = 1;
    private const int HotkeyCancelId = 2;
    private const uint ModCtrl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkR = 0x52;
    private const uint VkC = 0x43;

    private System.Windows.Point _startPoint;
    private bool _isDragging;
    private Bitmap? _fullScreenImage;
    private bool _hasScreenshot;
    private HwndSource? _hwndSource;

    public Rectangle SelectedRegion { get; private set; }
    public bool RegionSelected { get; private set; }

    public RegionPickerWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);
        var handle = new WindowInteropHelper(this).Handle;
        RegisterHotKey(handle, HotkeyCaptureId, ModCtrl | ModShift, VkR);
        RegisterHotKey(handle, HotkeyCancelId, ModCtrl | ModShift, VkC);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmHotkey = 0x0312;
        if (msg == wmHotkey)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyCaptureId && !_hasScreenshot)
                Dispatcher.Invoke(TakeScreenshot);
            else if (id == HotkeyCancelId)
                Dispatcher.Invoke(() => DialogResult = false);
        }
        return IntPtr.Zero;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            DialogResult = false;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter && _hasScreenshot)
            ConfirmSelection();
    }

    private void TakeScreenshot()
    {
        _fullScreenImage = CaptureFullScreen();
        _hasScreenshot = true;

        using var ms = new MemoryStream();
        _fullScreenImage.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        ScreenImage.Source = bmp;
        ScreenImage.Width = _fullScreenImage.Width;
        ScreenImage.Height = _fullScreenImage.Height;
        Width = _fullScreenImage.Width;
        Height = _fullScreenImage.Height;

        Background = System.Windows.Media.Brushes.Transparent;
        ScreenImage.Visibility = Visibility.Visible;
        OverlayCanvas.Visibility = Visibility.Visible;
        ButtonPanel.Visibility = Visibility.Visible;
        CoordLabel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        CoordLabel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        CoordLabel.Margin = new Thickness(10, 10, 0, 0);
        CoordText.FontSize = 13;
        CoordText.Text = $"点击并拖动鼠标选择区域   {_fullScreenImage.Width}x{_fullScreenImage.Height}";

        // Bring window to front for selection
        Topmost = true;
        Activate();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_hasScreenshot) return;
        _startPoint = e.GetPosition(this);
        _isDragging = true;
        Canvas.SetLeft(SelectionRect, _startPoint.X);
        Canvas.SetTop(SelectionRect, _startPoint.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging) return;

        var pos = e.GetPosition(this);
        var x = Math.Min(_startPoint.X, pos.X);
        var y = Math.Min(_startPoint.Y, pos.Y);
        var w = Math.Abs(pos.X - _startPoint.X);
        var h = Math.Abs(pos.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        CoordText.Text = $"x:{x:F0} y:{y:F0} w:{w:F0} h:{h:F0}";
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _isDragging = false;

        if (SelectionRect.Width > 10 && SelectionRect.Height > 10)
        {
            var x = Canvas.GetLeft(SelectionRect);
            var y = Canvas.GetTop(SelectionRect);
            SelectedRegion = new Rectangle((int)x, (int)y, (int)SelectionRect.Width, (int)SelectionRect.Height);
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmSelection();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ConfirmSelection()
    {
        if (SelectedRegion.Width > 0 && SelectedRegion.Height > 0)
        {
            RegionSelected = true;
            DialogResult = true;
        }
    }

    private static Bitmap CaptureFullScreen()
    {
        var screenBounds = Forms.Screen.PrimaryScreen.Bounds;
        var bmp = new Bitmap(screenBounds.Width, screenBounds.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenBounds.X, screenBounds.Y, 0, 0, screenBounds.Size);
        return bmp;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        var handle = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(handle, HotkeyCaptureId);
        UnregisterHotKey(handle, HotkeyCancelId);
        _fullScreenImage?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
