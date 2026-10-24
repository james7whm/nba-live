using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NbaLive;

/// <summary>Small Shell_NotifyIcon wrapper; avoids loading Windows Forms and GDI+.</summary>
internal sealed class NativeTray : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private IntPtr _icon;
    private string _iconFile;
    private int _iconSize;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;
    internal bool ClickThrough { get; set; }
    internal bool CapsuleEnabled { get; set; }
    internal OverlayContent ContentMode { get; set; }
    internal bool FollowedPlayersOnly { get; set; }
    internal TextSourcePreference TextSource { get; set; }
    internal TextDelivery TextDelivery { get; set; }
    internal event Action<int>? Command;

    internal NativeTray(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        (_iconFile, _iconSize) = IconPreference();
        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, _iconFile), 1, _iconSize, _iconSize, 0x10);
        if (_icon == IntPtr.Zero)
        {
            _iconFile = "Tray.ico";
            _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, _iconFile), 1, _iconSize, _iconSize, 0x10);
        }
        if (_icon == IntPtr.Zero) throw new InvalidOperationException("无法加载托盘图标。");
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _data = new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Hwnd = _hwnd, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon, Tip = "NBA 直播条", Info = "", InfoTitle = "" };
        _source.AddHook(Hook);
        if (!ShellNotifyIcon(0, ref _data))
        {
            _source.RemoveHook(Hook); DestroyIcon(_icon);
            throw new InvalidOperationException("无法创建系统托盘图标。");
        }
    }
    internal string Text
    {
        set { _data.Tip = value.Length > 127 ? value[..127] : value; ShellNotifyIcon(1, ref _data); }
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreated) { ShellNotifyIcon(0, ref _data); RefreshAppearance(); }
        if (message is 0x001A or 0x031A or Native.WmDpiChanged) RefreshAppearance(); // settings/theme/DPI
        if (message != CallbackMessage) return IntPtr.Zero;
        if (lParam.ToInt32() == 0x0203) Command?.Invoke(1); // double click
        if (lParam.ToInt32() is 0x0205 or 0x007B) ShowMenu();
        handled = true;
        return IntPtr.Zero;
    }
    private (string File, int Size) IconPreference()
    {
        var file = "Tray.ico"; // The tile remains readable when taskbar-theme information is unavailable.
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            file = key?.GetValue("SystemUsesLightTheme") switch { 0 => "TrayDark.ico", 1 => "TrayLight.ico", _ => file };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        int size;
        try { size = GetSystemMetricsForDpi(49, Native.WindowDpi(_hwnd)); }
        catch (EntryPointNotFoundException) { size = GetSystemMetrics(49); }
        return (file, RuntimeCompat.Clamp(size, 16, 256));
    }
    private void RefreshAppearance()
    {
        if (_disposed) return;
        var (file, size) = IconPreference();
        if (file == _iconFile && size == _iconSize) return;
        var replacement = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, file), 1, size, size, 0x10);
        if (replacement == IntPtr.Zero) return; // Keep a working icon if optional theme artwork is unavailable.
        var next = _data; next.Icon = replacement;
        if (!ShellNotifyIcon(1, ref next)) { DestroyIcon(replacement); return; }
        var previous = _icon; _icon = replacement; _iconFile = file; _iconSize = size; _data.Icon = replacement;
        DestroyIcon(previous);
    }
    internal void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "显示／隐藏全部窗口");
            AppendMenu(menu, 0, 2, "选择比赛");
            AppendMenu(menu, 0, 3, "本次会话历史");
            AppendMenu(menu, 0, 16, "精彩收藏");
            AppendMenu(menu, 0, 9, "球员技术统计");
            AppendMenu(menu, 0, 10, "刚才发生了啥／事件回顾");
            AppendMenu(menu, CapsuleEnabled ? 8u : 0u, 11, "胶囊模式");
            AppendMenu(menu, ContentMode == OverlayContent.Text ? 8u : 0u, 12, "显示文字直播");
            AppendMenu(menu, ContentMode == OverlayContent.Players ? 8u : 0u, 13, "显示关注球员");
            AppendMenu(menu, ContentMode == OverlayContent.Lineup ? 8u : 0u, 14, "显示场上五人");
            AppendMenu(menu, FollowedPlayersOnly ? 8u : 0u, 15, "文字只看关注球员");
            AppendMenu(menu, TextSource == TextSourcePreference.AnchorPreferred ? 8u : 0u, 17, "优先主播解说");
            AppendMenu(menu, TextSource == TextSourcePreference.Structured ? 8u : 0u, 18, "赛况实录");
            AppendMenu(menu, TextDelivery == TextDelivery.Latest ? 8u : 0u, 19, "实时最新");
            AppendMenu(menu, TextDelivery == TextDelivery.Paged ? 8u : 0u, 20, "完整补播（每页 4 秒）");
            AppendMenu(menu, ClickThrough ? 8u : 0u, 4, "鼠标穿透");
            AppendMenu(menu, 0, 5, "设置");
            AppendMenu(menu, 0, 8, "回到当前屏幕右下角");
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, 0, 7, "退出");
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, 0x0102, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (command > 0) Command?.Invoke(command);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ShellNotifyIcon(2, ref _data);
        _source.RemoveHook(Hook);
        DestroyIcon(_icon);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id, Flags;
        public int Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
