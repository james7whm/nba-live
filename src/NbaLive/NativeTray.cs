using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NbaLive;

/// <summary>Small Shell_NotifyIcon wrapper; avoids loading Windows Forms and GDI+.</summary>
internal sealed class NativeTray : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;
    internal bool ClickThrough { get; set; }
    internal bool CapsuleEnabled { get; set; }
    internal OverlayContent ContentMode { get; set; }
    internal bool FollowedPlayersOnly { get; set; }
    internal event Action<int>? Command;

    internal NativeTray(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Tray.ico"), 1, 16, 16, 0x10);
        if (_icon == IntPtr.Zero) throw new InvalidOperationException("无法加载托盘图标。");
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _data = new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Hwnd = _hwnd, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon, Tip = "NBA 直播条", Info = "", InfoTitle = "" };
        _source.AddHook(Hook);
        if (!ShellNotifyIcon(0, ref _data)) throw new InvalidOperationException("无法创建系统托盘图标。");
    }
    internal string Text
    {
        set { _data.Tip = value.Length > 127 ? value[..127] : value; ShellNotifyIcon(1, ref _data); }
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreated) ShellNotifyIcon(0, ref _data);
        if (message != CallbackMessage) return IntPtr.Zero;
        if (lParam.ToInt32() == 0x0203) Command?.Invoke(1); // double click
        if (lParam.ToInt32() is 0x0205 or 0x007B) ShowMenu();
        handled = true;
        return IntPtr.Zero;
    }
    internal void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "显示／隐藏直播条");
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
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
