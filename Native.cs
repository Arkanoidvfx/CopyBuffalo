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
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);

    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, Bits; public int Compression, Image, XPels, YPels, Used, Important; }
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref POINT to, ref SIZE size, IntPtr source, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, int usage, out IntPtr bits, IntPtr section, int offset);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    /// <summary>Картинка сразу под окном after (HWND_TOPMOST — поверх всех): мимо мыши, фокуса и панели задач. Появляется сразу, без кадра ожидания WPF.</summary>
    public static IntPtr ShowPicture(RECT at, System.Windows.Media.Imaging.BitmapSource picture, IntPtr after)
    {
        const uint layered = 0x80000, transparent = 0x20, noActivate = 0x08000000, topmost = 0x8, popup = 0x80000000, show = 0x40;
        var hwnd = CreateWindowExW(layered | transparent | noActivate | topmost | (uint)WS_EX_TOOLWINDOW, "STATIC", null, popup,
            at.Left, at.Top, at.Right - at.Left, at.Bottom - at.Top, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return hwnd;
        UpdateLayered(hwnd, at, picture, 0, 0);
        SetWindowPos(hwnd, after, 0, 0, 0, 0, SWP_NOSIZE | 0x2 | SWP_NOACTIVATE | show); // SWP_NOMOVE
        return hwnd;
    }

    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr Hwnd, After; public int X, Y, W, H; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, ref WINDOWPOS l);

    /// <summary>WM_WINDOWPOSCHANGED с новым местом и размером: по нему DefWindowProc пришлёт WM_MOVE и WM_SIZE.</summary>
    public static void SendWindowPosChanged(IntPtr hwnd, RECT r)
    {
        var pos = new WINDOWPOS { Hwnd = hwnd, X = r.Left, Y = r.Top, W = r.Right - r.Left, H = r.Bottom - r.Top, Flags = 0x4 | SWP_NOACTIVATE }; // SWP_NOZORDER
        SendMessage(hwnd, 0x47, IntPtr.Zero, ref pos);
    }

    /// <summary>Прозрачное окно одним вызовом получает место, размер и картинку: picture (Pbgra32) в точке (x, y) окна, остальное прозрачно; null — прозрачно всё.</summary>
    public static bool UpdateLayered(IntPtr hwnd, RECT window, System.Windows.Media.Imaging.BitmapSource? picture, int x, int y, byte alpha = 255)
    {
        int w = window.Right - window.Left, h = window.Bottom - window.Top;
        var info = new BITMAPINFOHEADER { Size = 40, Width = w, Height = -h, Planes = 1, Bits = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) { DeleteDC(dc); return false; }
        var old = SelectObject(dc, bitmap);
        try
        {
            int left = Math.Max(0, x), top = Math.Max(0, y), right = Math.Min(w, x + (picture?.PixelWidth ?? 0)), bottom = Math.Min(h, y + (picture?.PixelHeight ?? 0));
            if (picture != null && right > left && bottom > top)
                picture.CopyPixels(new System.Windows.Int32Rect(left - x, top - y, right - left, bottom - top),
                    bits + (top * w + left) * 4, (bottom - top) * w * 4 - left * 4, w * 4);
            var to = new POINT { X = window.Left, Y = window.Top };
            var size = new SIZE { cx = w, cy = h };
            var from = new POINT();
            var blend = new BLENDFUNCTION { Alpha = alpha, Format = 1 }; // AC_SRC_ALPHA
            return UpdateLayeredWindow(hwnd, IntPtr.Zero, ref to, ref size, dc, ref from, 0, ref blend, 2); // ULW_ALPHA
        }
        finally { SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); }
    }

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
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
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

    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [DllImport("gdi32.dll")] static extern int GetObjectW(IntPtr h, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    /// <summary>Значок файла или ярлыка с прозрачностью, или null. Вызывать из фонового потока: оболочка может думать.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? ShellIcon(string path, int size)
    {
        System.Windows.Media.Imaging.BitmapSource? result = null;
        var t = new Thread(() =>
        {
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out var hbmp) != 0) return; // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                try
                {
                    if (GetObjectW(hbmp, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height == 0) return;
                    int w = bitmap.Width, h = Math.Abs(bitmap.Height);
                    var info = new BITMAPINFOHEADER { Size = 40, Width = w, Height = -h, Planes = 1, Bits = 32 };
                    var pixels = new byte[w * h * 4];
                    if (GetDIBits(dc, hbmp, 0, (uint)h, pixels, ref info, 0) == 0) return;
                    result = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, w * 4);
                    result.Freeze();
                }
                finally { DeleteDC(dc); DeleteObject(hbmp); }
            }
            catch (Exception e) when (e is COMException or System.IO.IOException or UnauthorizedAccessException or ArgumentException) { } // файла нет или к нему не пускают: останется без значка
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return t.Join(TimeSpan.FromSeconds(5)) ? result : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder name, ref uint size);

    /// <summary>Путь к EXE процесса, в том числе из магазина и с повышенными правами, или null.</summary>
    public static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new System.Text.StringBuilder(1024);
            uint size = (uint)name.Capacity;
            return QueryFullProcessImageNameW(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(process); }
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
