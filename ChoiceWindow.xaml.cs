using System.Windows;
using System.Windows.Controls;

namespace GalgamePersonaStudio;

public partial class ChoiceWindow : Window
{
    public int SelectedIndex { get; private set; } = -1;
    public bool ChoiceMade => SelectedIndex >= 0;

    public ChoiceWindow(List<string> choices)
    {
        InitializeComponent();
        ChoiceList.ItemsSource = choices.Select((text, i) => new { Index = i, Text = text });
        Owner = System.Windows.Application.Current.MainWindow;
    }

    private void ChoiceButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is int index)
        {
            SelectedIndex = index;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
