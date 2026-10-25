using System.Runtime.InteropServices;

namespace ArkCopyBuffer;

static class Native
{
    // Метка в dwExtraInfo наших SendInput — чтобы хук не ловил собственные нажатия.
    public static readonly IntPtr OurInputMarker = new(0x41524B43); // "ARKC"

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A;
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int WM_QUIT = 0x0012, WM_APP = 0x8000, WM_CLIPBOARDUPDATE = 0x031D;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;
    public const int VK_MASK = 0xE8; // неназначенный VK: гасит открытие Start после Win+V
    public const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // --- SendInput ---
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Шлёт последовательность клавиш: (vk, true)=down, (vk, false)=up. Помечает их нашей меткой.</summary>
    public static bool SendKeys(params (int vk, bool down)[] keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)k.vk, dwFlags = k.down ? 0 : KEYEVENTF_KEYUP, dwExtraInfo = OurInputMarker } }
        }).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Печатает текст как ввод с клавиатуры (VK_PACKET): без буфера обмена и независимо от раскладки.</summary>
    public static bool SendText(string text)
    {
        var inputs = text.SelectMany(ch => new[] { false, true }.Select(up => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = OurInputMarker } }
        })).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    // --- Clipboard listener ---
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();

    // --- Окна и фокус ---
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    // --- Смена окна переднего плана (клик вне панели), без опоры на IsActive WPF ---
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOSIZE = 0x1;
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Тёмная системная рамка окна (Windows 10 20H1+). На старых сборках вызов просто ничего не меняет.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // --- Каретка / мышь / мониторы ---
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;

    // --- Миниатюра файла из кэша Проводника (кадр видео) ---
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    const int SIIGBF_THUMBNAILONLY = 0x08; // только настоящая миниатюра; значок типа файла нам не нужен

    /// <summary>
    /// Кадр-миниатюра, как в Проводнике, или null. Обработчики миниатюр — COM и часть из них требует STA,
    /// поэтому вызываем на своём STA-потоке.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? ShellThumbnail(string path, int size)
    {
        System.Windows.Media.Imaging.BitmapSource? result = null;
        var t = new Thread(() =>
        {
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY, out var hbmp) != 0) return;
                try
                {
                    result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero,
                        System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    result.Freeze();
                }
                finally { DeleteObject(hbmp); }
            }
            catch { } // нет обработчика миниатюр для формата — покажем значок
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return t.Join(TimeSpan.FromSeconds(5)) ? result : null; // ponytail: зависший обработчик оставит поток висеть, но UI не ждёт
    }

    // --- Процессы: повышенные права цели ---
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int len, out int retLen);
    const int TokenElevation = 20;

    /// <summary>
    /// true, если в окно нельзя слать ввод (UIPI): процесс окна повышен, а мы нет.
    /// Не смогли даже прочитать токен — считаем, что он выше нас по правам.
    /// </summary>
    public static bool IsInputBlocked(IntPtr hwnd)
    {
        if (Environment.IsPrivilegedProcess) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == IntPtr.Zero) return true;
        try
        {
            if (!OpenProcessToken(proc, TOKEN_QUERY, out var token)) return true;
            try { return !GetTokenInformation(token, TokenElevation, out var elevated, 4, out _) || elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(proc); }
    }

    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

    // --- Job Object: дочерний процесс умирает вместе с нами ---
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, byte[] info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// <summary>
    /// Job с KILL_ON_JOB_CLOSE: его процессы завершаются, когда закрывается наш хэндл — в том числе при падении
    /// или Stop-Process, где OnExit не вызывается. Иначе llama-server остался бы сиротой и держал видеопамять.
    /// </summary>
    public static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new byte[144]; // JOBOBJECT_EXTENDED_LIMIT_INFORMATION (x64)
        BitConverter.TryWriteBytes(info.AsSpan(16), 0x2000); // BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (job == IntPtr.Zero || !SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, info, info.Length))
        {
            int error = Marshal.GetLastWin32Error();
            if (job != IntPtr.Zero) CloseHandle(job);
            throw new System.ComponentModel.Win32Exception(error);
        }
        return job;
    }

    public static void AssignToJob(IntPtr job, System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle)) throw new System.ComponentModel.Win32Exception();
    }
}
