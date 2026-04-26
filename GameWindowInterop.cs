using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GalgamePersonaStudio;

public static class GameWindowInterop
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public MOUSEINPUT MI;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    /// <summary>
    /// Find first visible window owned by the given process name (e.g. "game.exe").
    /// </summary>
    public static IntPtr FindWindowHandle(string processName)
    {
        var target = processName.Replace(".exe", "").ToLowerInvariant();
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.ProcessName.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Send a mouse click at absolute screen coordinates using SendInput.
    /// Move + down + up in one SendInput call.
    /// </summary>
    public static void ClickAt(int x, int y)
    {
        var screenW = GetSystemMetrics(SM_CXSCREEN);
        var screenH = GetSystemMetrics(SM_CYSCREEN);
        if (screenW <= 0 || screenH <= 0) return;

        var screenX = x * 65535 / screenW;
        var screenY = y * 65535 / screenH;

        var inputs = new INPUT[3];

        inputs[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            MI = new MOUSEINPUT { Dx = screenX, Dy = screenY, Flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE }
        };

        inputs[1] = new INPUT
        {
            Type = INPUT_MOUSE,
            MI = new MOUSEINPUT { Dx = 0, Dy = 0, Flags = MOUSEEVENTF_LEFTDOWN }
        };

        inputs[2] = new INPUT
        {
            Type = INPUT_MOUSE,
            MI = new MOUSEINPUT { Dx = 0, Dy = 0, Flags = MOUSEEVENTF_LEFTUP }
        };

        SendInput(3, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Bring the specified window to foreground.
    /// </summary>
    public static void BringToForeground(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero)
            SetForegroundWindow(hWnd);
    }
}
