using System.Windows;
using System.Windows.Input;

namespace GalgamePersonaStudio;

public partial class ClickPositionPickerWindow : Window
{
    private bool _hasPoint;
    private System.Windows.Point _selectedPoint;

    public int SelectedX => (int)_selectedPoint.X;
    public int SelectedY => (int)_selectedPoint.Y;
    public bool PointSelected { get; private set; }

    public ClickPositionPickerWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            return;
        }
        if (e.Key == Key.Enter && _hasPoint)
            ConfirmSelection();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _selectedPoint = e.GetPosition(this);
        _hasPoint = true;

        CoordText.Text = $"点击位置: ({SelectedX}, {SelectedY})   Enter 确认，Esc 取消";
        ButtonPanel.Visibility = Visibility.Visible;
        CoordLabel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        CoordLabel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        CoordLabel.Margin = new Thickness(10, 10, 0, 0);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmSelection();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _hasPoint = false;
        ButtonPanel.Visibility = Visibility.Collapsed;
        CoordLabel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        CoordLabel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        CoordLabel.Margin = new Thickness(0);
        CoordText.Text = "点击游戏翻页位置（Enter 确认，Esc 取消）";
    }

    private void ConfirmSelection()
    {
        if (_hasPoint)
        {
            PointSelected = true;
            DialogResult = true;
        }
    }
}
