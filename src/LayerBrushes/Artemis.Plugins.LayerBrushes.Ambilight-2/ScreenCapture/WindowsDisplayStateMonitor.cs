using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture;

internal sealed class WindowsDisplayStateMonitor : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int HWND_MESSAGE = -3;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUIT = 0x0012;
    private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly AutoResetEvent _windowReady = new(false);
    private readonly Thread _messageThread;

    private IntPtr _windowHandle = IntPtr.Zero;
    private IntPtr _powerNotifyHandle = IntPtr.Zero;
    private uint _threadId;
    private bool _disposed;

    /// <summary>Raw GUID_CONSOLE_DISPLAY_STATE value: 0=off, 1=on, 2=dimmed.</summary>
    public event EventHandler<int>? DisplayStateChanged;

    public WindowsDisplayStateMonitor()
    {
        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "AmbilightDisplayStateMonitor"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
        _windowReady.WaitOne();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_windowHandle != IntPtr.Zero)
            PostMessage(_windowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        else if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (!_messageThread.Join(TimeSpan.FromSeconds(2)))
            _messageThread.Interrupt();

        _windowReady.Dispose();
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = WindowProc,
            lpszClassName = "ArtemisAmbilightDisplayStateMonitorWindow"
        };

        ushort classAtom = RegisterClass(ref wndClass);
        if (classAtom == 0)
        {
            _windowReady.Set();
            return;
        }

        _windowHandle = CreateWindowEx(
            0,
            wndClass.lpszClassName,
            wndClass.lpszClassName,
            0,
            0,
            0,
            0,
            0,
            new IntPtr(HWND_MESSAGE),
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_windowHandle != IntPtr.Zero)
        {
            Guid powerSettingGuid = GuidConsoleDisplayState;
            _powerNotifyHandle = RegisterPowerSettingNotification(_windowHandle, ref powerSettingGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        _windowReady.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_powerNotifyHandle != IntPtr.Zero)
            UnregisterPowerSettingNotification(_powerNotifyHandle);

        if (_windowHandle != IntPtr.Zero)
            DestroyWindow(_windowHandle);

        UnregisterClass(wndClass.lpszClassName, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST && wParam == (IntPtr)PBT_POWERSETTINGCHANGE)
        {
            POWERBROADCAST_SETTING setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GuidConsoleDisplayState)
            {
                IntPtr dataPtr = IntPtr.Add(lParam, Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32());
                int displayState = Marshal.ReadInt32(dataPtr);
                DisplayStateChanged?.Invoke(this, displayState);
            }
        }
        else if (msg == WM_CLOSE)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);
}
