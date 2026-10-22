using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NbaLive;

internal static class Native
{
    private const int GwlExStyle = -20;
    internal const int WmHotkey = 0x0312;
    internal const int WmMouseActivate = 0x0021;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmDpiChanged = 0x02E0;
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    internal static void ActivateDialog(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE retains the previous maximized state.
        window.Activate();
        SetForegroundWindow(hwnd);
    }

    internal static void Configure(IntPtr hwnd, bool clickThrough)
    {
        var style = GetWindowLong(hwnd, GwlExStyle) | 0x08000000;
        style = (style & 0x40000) != 0 ? style & ~0x80 : style | 0x80; // Retain the opt-in inspection mode.
        style = clickThrough ? style | 0x00000020 : style & ~0x00000020;
        SetWindowLong(hwnd, GwlExStyle, style);
    }
    internal static void AllowInspection(Window window)
    {
        // Opt-in developer switch: make the tool window discoverable to accessibility tools.
        // NOACTIVATE is retained; production launch keeps TOOLWINDOW and hides the taskbar button.
        window.ShowInTaskbar = true;
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLong(hwnd, GwlExStyle, (GetWindowLong(hwnd, GwlExStyle) & ~0x80) | 0x40000);
    }

    internal static (int X, int Y) Place(Window window, int? savedX = null, int? savedY = null, bool snap = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var rect);
        var monitor = savedX is not null && savedY is not null
            ? MonitorFromPoint(new() { X = savedX.Value, Y = savedY.Value }, 2)
            : MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("无法读取屏幕工作区。");
        var area = info.Work;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var position = WindowPlacement.Calculate(new(area.Left, area.Top, area.Right, area.Bottom), width, height, scale, savedX, savedY, snap);
        var (x, y) = position;
        SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0011); // NOSIZE | NOACTIVATE
        return (x, y);
    }
    internal readonly record struct Geometry(WorkArea Area, int X, int Y, int Width, int Height, double Scale);
    internal static Geometry GetGeometry(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var rect);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) throw new InvalidOperationException("无法读取屏幕工作区。");
        return new(new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top, GetDpiForWindow(hwnd) / 96.0);
    }
    internal static WindowPosition ResizeKeepingCorner(Window window, Geometry before)
    {
        var after = GetGeometry(window);
        var position = WindowPlacement.PreserveCorner(before.Area, before.Width, before.Height, after.Width, after.Height,
            before.Scale, before.X, before.Y);
        SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), position.X, position.Y, 0, 0, 0x0011);
        return position;
    }
    internal static void ResizeAt(Window window, double width, double height, WindowPosition position)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        window.Width = width; window.Height = height;
        SetWindowPos(hwnd, new IntPtr(-1), position.X, position.Y, (int)Math.Round(width * scale),
            (int)Math.Round(height * scale), 0x0010); // NOACTIVATE
    }
}

internal sealed class HotkeyManager(IntPtr hwnd) : IDisposable
{
    private readonly Dictionary<int, Hotkey> _registered = [];
    internal string Error { get; private set; } = "";

    internal bool Apply(string hide, string clickThrough)
    {
        if (!Hotkey.TryParse(hide, out var a) || !Hotkey.TryParse(clickThrough, out var b))
        { Error = "快捷键格式：Ctrl+Alt+字母／数字／F1～F24，也可添加 Shift。"; return false; }
        if (a == b) { Error = "隐藏与鼠标穿透不能使用同一个快捷键。"; return false; }
        var previous = _registered.ToArray();
        Clear();
        var okA = Register(1, a);
        var okB = Register(2, b);
        if (okA && okB) { Error = ""; return true; }
        Clear();
        foreach (var pair in previous) Register(pair.Key, pair.Value);
        Error = $"快捷键被占用：{(!okA ? hide : clickThrough)}。请修改；仍可通过托盘操作。";
        return false;
    }
    private bool Register(int id, Hotkey key)
    {
        if (!Native.RegisterHotKey(hwnd, id, key.Modifiers | 0x4000, key.VirtualKey)) return false;
        _registered[id] = key;
        return true;
    }
    private void Clear() { foreach (var id in _registered.Keys) Native.UnregisterHotKey(hwnd, id); _registered.Clear(); }
    public void Dispose() => Clear();
}
