using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace GalgamePersonaStudio;

public partial class ProcessPickerWindow : Window
{
    private readonly ObservableCollection<ProcessItem> _allProcesses = [];

    public string? SelectedProcessName { get; private set; }
    public string? SelectedWindowTitle { get; private set; }
    public int SelectedProcessId { get; private set; }

    public ProcessPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshProcessList();
    }

    private void RefreshProcessList()
    {
        _allProcesses.Clear();
        var seen = new HashSet<string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var title = GetWindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;

            try
            {
                var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName + ".exe";
                var key = $"{name}|{title}";
                if (!seen.Add(key)) return true;

                _allProcesses.Add(new ProcessItem
                {
                    ProcessName = name,
                    WindowTitle = title,
                    Pid = (int)pid
                });
            }
            catch { }
            return true;
        }, nint.Zero);

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";
        ProcessList.ItemsSource = string.IsNullOrEmpty(filter)
            ? _allProcesses
            : new ObservableCollection<ProcessItem>(
                _allProcesses.Where(p =>
                    p.ProcessName.ToLowerInvariant().Contains(filter) ||
                    p.WindowTitle.ToLowerInvariant().Contains(filter)));
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void Ok_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ConfirmSelection()
    {
        if (ProcessList.SelectedItem is ProcessItem item)
        {
            SelectedProcessName = item.ProcessName;
            SelectedWindowTitle = item.WindowTitle;
            SelectedProcessId = item.Pid;
            DialogResult = true;
        }
    }

    public class ProcessItem
    {
        public string ProcessName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public int Pid { get; set; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int count);
    private static string GetWindowText(nint hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
