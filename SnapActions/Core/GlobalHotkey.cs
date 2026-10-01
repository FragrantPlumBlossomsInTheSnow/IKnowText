using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SnapActions.Core;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly HwndSource _window;
    private const int Id = 0x5341;
    internal static bool IsRegistered { get; private set; }

    internal GlobalHotkey(Action requested)
    {
        _window = new HwndSource(new HwndSourceParameters("SnapActions keyboard palette") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0312 && wParam.ToInt32() == Id) { handled = true; requested(); }
            return IntPtr.Zero;
        });
        // Ctrl+Shift+Space, no repeat. RegisterHotKey 失败返回 false 时 GetLastError 给出原因
        //（最常见是被其它程序已占用同一全局热键），打日志便于定位"快捷键无反应"类问题。
        IsRegistered = RegisterHotKey(_window.Handle, Id, 0x4000 | 0x0002 | 0x0004, 0x20);
        if (!IsRegistered)
            SnapActions.Helpers.Log.Error($"Keyboard palette hotkey (Ctrl+Shift+Space) registration FAILED, GetLastError={Marshal.GetLastWin32Error()}");
        else
            SnapActions.Helpers.Log.Info("Keyboard palette hotkey (Ctrl+Shift+Space) registered");
    }

    public void Dispose()
    {
        UnregisterHotKey(_window.Handle, Id);
        _window.Dispose();
        IsRegistered = false;
    }

    internal static bool ReturnToTarget(ForegroundTarget target)
    {
        if (!target.IsComplete || !IsWindow(target.ForegroundWindow)) return false;
        GetWindowThreadProcessId(target.ForegroundWindow, out var pid);
        return pid == target.ProcessId && SetForegroundWindow(target.ForegroundWindow);
    }

    internal static async Task<bool> ReturnToTargetAsync(SelectionOperation operation)
    {
        if (!operation.IsCurrent || !ReturnToTarget(operation.Target)) return false;
        return await WaitForActivationAsync(operation, ForegroundGuard.StillValid);
    }

    // Cross-process SetForegroundWindow success can precede activation. Wait only for the
    // original native focus identity; the action runner still validates its selection once.
    internal static async Task<bool> WaitForActivationAsync(SelectionOperation operation,
        Func<ForegroundTarget, bool> isTargetActive)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            while (operation.IsCurrent && !deadline.IsCancellationRequested)
            {
                if (isTargetActive(operation.Target))
                    return operation.IsCurrent && !deadline.IsCancellationRequested;
                await Task.Delay(25, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
