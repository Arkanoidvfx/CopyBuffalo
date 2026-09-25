using System.Runtime.InteropServices;
using static ArkCopyBuffer.Native;

namespace ArkCopyBuffer;

/// <summary>
/// Перехват Win+V через WH_KEYBOARD_LL на отдельном потоке.
/// Свой поток обязателен: если колбэк не ответит за LowLevelHooksTimeout (UI занят),
/// Windows молча снимет хук. Здесь колбэк только читает флаги и постит сообщение.
///
/// Start: если система увидит «Win down → Win up» без других клавиш между ними, откроется меню Пуск
/// (V мы проглотили). Поэтому отпускание Win после Win+V придерживаем и сами шлём «маска → Win up».
/// Слать маску сразу при V нельзя: Win up может уже стоять в очереди ввода и уйдёт раньше маски.
/// </summary>
sealed class WinVHook : IDisposable
{
    const uint WM_SEND_MASK_WINUP = WM_APP + 1;

    readonly Action<IntPtr> _triggered; // аргумент — окно, которое было активно в момент нажатия
    readonly LowLevelKeyboardProc _proc; // держим ссылку, иначе GC соберёт делегат
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    uint _threadId;
    IntPtr _hook;
    bool _vSwallowed;  // держим V: глотаем автоповтор и отпускание
    bool _winHeld;     // Win зажат (для отличия нового нажатия от автоповтора)
    bool _comboUsed;   // за это нажатие Win был Win+V → отпускание Win надо замаскировать

    public WinVHook(Action<IntPtr> triggered)
    {
        _triggered = triggered;
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinVHook" };
        _thread.Start();
        _ready.Wait();
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_SEND_MASK_WINUP)
                SendKeys((VK_MASK, true), (VK_MASK, false), ((int)msg.wParam, false)); // SendInput — вне колбэка
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool ours = (k.flags & LLKHF_INJECTED) != 0 && k.dwExtraInfo == OurInputMarker;
            int m = (int)wParam;
            bool down = m is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = m is WM_KEYUP or WM_SYSKEYUP;

            if (!ours && k.vkCode is VK_LWIN or VK_RWIN)
            {
                if (down && !_winHeld) { _winHeld = true; _comboUsed = false; }
                if (up)
                {
                    _winHeld = false;
                    if (_comboUsed)
                    {
                        _comboUsed = false;
                        PostThreadMessage(_threadId, WM_SEND_MASK_WINUP, (nint)k.vkCode, 0);
                        return 1;
                    }
                }
            }
            else if (!ours && k.vkCode == VK_V)
            {
                if (down && _vSwallowed) return 1; // автоповтор
                if (up && _vSwallowed) { _vSwallowed = false; return 1; }

                // Только чистый Win+V: Win+Ctrl+V и т.п. остаются системе.
                if (down && (IsDown(VK_LWIN) || IsDown(VK_RWIN))
                    && !IsDown(VK_CONTROL) && !IsDown(VK_MENU) && !IsDown(VK_SHIFT))
                {
                    _vSwallowed = true;
                    _comboUsed = true;
                    var foreground = GetForegroundWindow();
                    ThreadPool.QueueUserWorkItem(_ => _triggered(foreground));
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => PostThreadMessage(_threadId, WM_QUIT, 0, 0);
}
