using System.Runtime.InteropServices;
using System.Windows;
using static SnapActions.Core.ClipboardTransaction;

namespace SnapActions.Core;

internal static class InputExecutor
{
    private const int INPUT_KEYBOARD = 1;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;    // Alt
    private const ushort VK_INSERT = 0x2D;  // Ctrl+Insert = Copy / Shift+Insert = Paste
    private const ushort VK_DELETE = 0x2E;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly KeyStroke[] ShiftInsertInputs = BuildExtendedInsertCombo(VK_SHIFT);
    private static readonly KeyStroke[] CtrlInsertInputs = BuildExtendedInsertCombo(VK_CONTROL);
    private static readonly KeyStroke[] DeleteInputs =
    [
        new(VK_DELETE, KeyUp: false, Extended: true),
        new(VK_DELETE, KeyUp: true, Extended: true),
    ];
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    internal enum InputInjectionStatus
    {
        Rejected,
        Succeeded,
        Partial,
    }

    internal readonly record struct InputInjectionOutcome(
        InputInjectionStatus Status,
        bool CleanupSucceeded = true,
        uint AcceptedCount = 0);

    internal readonly record struct KeyStroke(
        ushort VirtualKey,
        bool KeyUp,
        bool Extended);
    internal static bool CanInjectAtBoundary(
        bool operationCurrent,
        ForegroundTarget expectedTarget,
        ForegroundTarget currentTarget,
        ClipboardObservation? expectedClipboard,
        ClipboardObservation currentClipboard) =>
        operationCurrent
        && ForegroundGuard.HasSufficientInputIdentity(expectedTarget)
        && ForegroundGuard.Matches(expectedTarget, currentTarget)
        && (expectedClipboard == null
            || CanRestoreClipboard(expectedClipboard.Value, currentClipboard));

    internal static bool CanRollbackAfterPartialPaste(
        InputInjectionOutcome outcome) =>
        outcome.Status == InputInjectionStatus.Partial
        && outcome.CleanupSucceeded
        && outcome.AcceptedCount < 2;

    /// <summary>
    /// Waits up to ~300 ms for the user to release the given modifier keys before we inject a
    /// synthetic chord. A modifier still held at gesture end (Shift+drag to extend a selection,
    /// Ctrl+drag for a discontiguous one) would otherwise corrupt the chord — Ctrl+Insert into
    /// Ctrl+Shift+Insert, Shift+Insert into Ctrl+Shift+Insert — which copies/pastes nothing in
    /// many apps. Destructive and clipboard-mutating chords require Shift, Ctrl, and Alt all to
    /// be released so our synthetic key-up cannot interfere with a physically held modifier.
    /// </summary>
    private static async Task<bool> WaitForModifierKeysReleasedAsync(params int[] vkeys)
    {
        for (int i = 0; i < 15; i++)
        {
            if (AreModifierKeysReleased(vkeys)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    private static bool AreModifierKeysReleased(params int[] vkeys)
    {
        foreach (var vk in vkeys)
        {
            if ((SnapActions.Helpers.NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                return false;
        }
        return true;
    }
    internal static async Task<bool> PreparePasteAsync(SelectionOperation operation)
    {
        if (!await operation.CanMutateTargetAsync()) return false;
        return await WaitForModifierKeysReleasedAsync(VK_SHIFT, VK_CONTROL, VK_MENU)
               && await operation.CanMutateTargetAsync();
    }

    /// <summary>
    /// 宽松的粘贴前置（翻译替换等"按选区替换原文"场景，语义等同旧版 TextCapture.PreparePasteAsync）：
    /// 只要求窗口级身份一致（前台窗口/PID/线程仍匹配、选区仍有效），不要求可编辑字段聚焦或
    /// 精确 UIA 输入目标 —— 划词翻译的选区往往没有 AutomationRuntimeId，严格校验会统一拒绝。
    /// </summary>
    internal static async Task<bool> PreparePasteForReplaceAsync(SelectionOperation operation)
    {
        if (!operation.IsCurrent)
        {
            SnapActions.Helpers.Log.Warn("Replace prepare rejected: operation no longer current");
            return false;
        }
        if (!await ForegroundGuard.StillValidAsync(operation.Target))
        {
            SnapActions.Helpers.Log.Warn("Replace prepare rejected: foreground target no longer valid");
            return false;
        }
        if (!await operation.CanUseSelectionAsync())
        {
            SnapActions.Helpers.Log.Warn("Replace prepare rejected: selection validation failed");
            return false;
        }
        if (!await WaitForModifierKeysReleasedAsync(VK_SHIFT, VK_CONTROL, VK_MENU))
        {
            SnapActions.Helpers.Log.Warn("Replace prepare rejected: modifier keys still held");
            return false;
        }
        return await operation.CanInjectInputAsync();
    }

    /// <summary>
    /// 替换注入：发送 Shift+Insert 覆盖仍选中的文本，但用窗口级一致校验（MatchesWindow +
    /// PID/TID + 修饰键释放 + 剪贴板不漂移），不复用强制 AutomationRuntimeId 的
    /// TryRunWithExactInputTargetAsync —— 与合成键兜底 TrySimulateCopyAsync 同一模式，
    /// 让"划词翻译→替换原文"在没有精确输入身份的目标上也能执行。
    /// </summary>
    internal static async Task<InputInjectionOutcome> TrySimulatePasteForReplaceAsync(
        SelectionOperation operation, ClipboardObservation? expectedClipboard)
    {
        var outcome = new InputInjectionOutcome(InputInjectionStatus.Rejected);
        if (!operation.IsCurrent)
        {
            SnapActions.Helpers.Log.Warn("Replace paste rejected: operation no longer current");
            return outcome;
        }
        if (!AreModifierKeysReleased(VK_SHIFT, VK_CONTROL, VK_MENU))
        {
            SnapActions.Helpers.Log.Warn("Replace paste rejected: modifier keys still held");
            return outcome;
        }
        var current = ForegroundGuard.Capture();
        if (!ForegroundGuard.MatchesWindow(operation.Target, current) || !ForegroundGuard.StillValid(operation.Target))
        {
            SnapActions.Helpers.Log.Warn("Replace paste rejected: foreground window no longer matches capture target");
            return outcome;
        }
        if (expectedClipboard is { } ec)
        {
            var currentClipboard = ObserveClipboard();
            if (!CanRestoreClipboard(ec, currentClipboard))
            {
                SnapActions.Helpers.Log.Warn(
                    $"Replace paste rejected: clipboard drift (expected seq={ec.Sequence}, now seq={currentClipboard.Sequence})");
                return outcome;
            }
        }
        if (!TrySendKeySequenceForOperation(operation, ShiftInsertInputs, SendNativeKeyStrokes, out outcome))
        {
            SnapActions.Helpers.Log.Warn("Replace paste rejected: operation claim failed at send");
            return new InputInjectionOutcome(InputInjectionStatus.Rejected);
        }
        return outcome;
    }

    internal static async Task<bool> PrepareDeleteAsync(SelectionOperation operation)
    {
        if (!await operation.CanMutateTargetAsync()) return false;
        return await WaitForModifierKeysReleasedAsync(VK_SHIFT, VK_CONTROL, VK_MENU)
               && await operation.CanMutateTargetAsync();
    }

    /// <summary>
    /// Sends Shift+Insert only if the immutable operation, exact input target, physical modifiers,
    /// and optional clipboard observation all still match at the final injection boundary.
    /// Call <see cref="PreparePasteAsync"/> before changing clipboard data.
    /// </summary>
    internal static Task<InputInjectionOutcome> TrySimulatePasteAsync(
        SelectionOperation operation, ClipboardObservation? expectedClipboard = null)
    {
        return TrySendInputAsync(
            operation,
            expectedClipboard,
            ShiftInsertInputs,
            VK_SHIFT, VK_CONTROL, VK_MENU);
    }

    /// <summary>
    /// Injects a synthetic copy chord (Ctrl+Insert) into the exact input target. This is the
    /// last-resort capture fallback for apps whose UI Automation exposes no selectable text
    /// (Java Swing IDEs, some Chromium content). Because that fallback runs
    /// precisely when UIA yields no selection, the target has no AutomationRuntimeId to identify —
    /// so unlike the paste/delete paths, this uses window-level identity (foreground + focused + pid +
    /// tid + clipboard-unchanged + modifiers-released). This matches the pre-v2.4.0 capture safety.
    /// </summary>
    internal static async Task<InputInjectionOutcome> TrySimulateCopyAsync(
        SelectionOperation operation, ClipboardObservation? expectedClipboard)
    {
        var strokes = CtrlInsertInputs;
        var outcome = new InputInjectionOutcome(InputInjectionStatus.Rejected);

        if (!operation.IsCurrent)
        {
            SnapActions.Helpers.Log.Warn("Synthetic copy rejected: operation no longer current");
            return outcome;
        }
        if (!AreModifierKeysReleased(VK_SHIFT, VK_CONTROL, VK_MENU))
        {
            SnapActions.Helpers.Log.Warn("Synthetic copy rejected: modifier keys still held");
            return outcome;
        }
        var current = ForegroundGuard.Capture();
        if (!ForegroundGuard.MatchesWindow(operation.Target, current) || !ForegroundGuard.StillValid(operation.Target))
        {
            SnapActions.Helpers.Log.Warn("Synthetic copy rejected: foreground window no longer matches capture target");
            return outcome;
        }
        if (expectedClipboard is { } ec)
        {
            var currentClipboard = ClipboardTransaction.ObserveClipboard();
            if (!ClipboardTransaction.CanRestoreClipboard(ec, currentClipboard))
            {
                SnapActions.Helpers.Log.Warn(
                    $"Synthetic copy rejected: clipboard drift (expected seq={ec.Sequence}, now seq={currentClipboard.Sequence})");
                return outcome;
            }
        }
        if (!TrySendKeySequenceForOperation(operation, strokes, SendNativeKeyStrokes, out outcome))
        {
            SnapActions.Helpers.Log.Warn("Synthetic copy rejected: operation claim failed at send");
            return new InputInjectionOutcome(InputInjectionStatus.Rejected);
        }
        return outcome;
    }

    internal static async Task<InputInjectionOutcome> SimulatePasteAsync(
        SelectionOperation operation)
    {
        var expectedClipboard = ObserveClipboard();
        // 窗口级宽松链路（同翻译替换）：粘贴按钮的目标（普通工具栏粘贴场景）往往没有
        // AutomationRuntimeId，严格校验会被 HasSufficientInputIdentity 统一拒绝。
        if (!await PreparePasteForReplaceAsync(operation))
            return new InputInjectionOutcome(InputInjectionStatus.Rejected);
        return await TrySimulatePasteForReplaceAsync(operation, expectedClipboard);
    }

    internal static async Task<InputInjectionOutcome> SimulateDeleteAsync(
        SelectionOperation operation)
    {
        if (!await PrepareDeleteAsync(operation))
            return new InputInjectionOutcome(InputInjectionStatus.Rejected);
        return await TrySendInputAsync(
            operation,
            expectedClipboard: null,
            DeleteInputs,
            VK_SHIFT, VK_CONTROL, VK_MENU);
    }

    private static async Task<InputInjectionOutcome> TrySendInputAsync(
        SelectionOperation operation,
        ClipboardObservation? expectedClipboard,
        KeyStroke[] strokes,
        params int[] modifiersThatMustBeReleased)
    {
        var outcome = new InputInjectionOutcome(
            InputInjectionStatus.Rejected);
        bool reachedInputBoundary =
            await ForegroundGuard.TryRunWithExactInputTargetAsync(
            operation.Target,
            currentTarget =>
            {
                var currentClipboard = expectedClipboard == null
                    ? default
                    : ObserveClipboard();
                if (!CanInjectAtBoundary(
                        operation.IsCurrent,
                        operation.Target,
                        currentTarget,
                        expectedClipboard,
                        currentClipboard))
                    return false;
                if (!AreModifierKeysReleased(modifiersThatMustBeReleased))
                    return false;
                // Re-sample native identity immediately before SendInput. The UIA identity was
                // captured directly before this callback on the same worker.
                if (!ForegroundGuard.StillValid(operation.Target))
                    return false;
                // Repeat the claim after every potentially yielding or cross-process validation.
                // Hook-thread invalidation remains lock-free and wins before this final send point.
                if (!TrySendKeySequenceForOperation(
                        operation,
                        strokes,
                        SendNativeKeyStrokes,
                        out outcome))
                    return false;
                return true;
            }, operation.ValidateInput);
        return reachedInputBoundary
            ? outcome
            : new InputInjectionOutcome(InputInjectionStatus.Rejected);
    }
    // Insert is an extended key — without the flag some apps see numpad-0 instead.
    private static KeyStroke[] BuildExtendedInsertCombo(ushort modifier) =>
    [
        new(modifier, KeyUp: false, Extended: false),
        new(VK_INSERT, KeyUp: false, Extended: true),
        new(VK_INSERT, KeyUp: true, Extended: true),
        new(modifier, KeyUp: true, Extended: false),
    ];

    internal static InputInjectionOutcome SendKeySequence(
        IReadOnlyList<KeyStroke> strokes,
        Func<IReadOnlyList<KeyStroke>, uint> sender)
    {
        // SendInput inserts an INPUT array serially and returns the inserted event count. For a
        // short prefix, synthesize key-up events for every accepted key-down not already paired
        // with an accepted key-up, in reverse press order.
        uint inserted = sender(strokes);
        if (inserted == (uint)strokes.Count)
            return new InputInjectionOutcome(
                InputInjectionStatus.Succeeded,
                AcceptedCount: inserted);
        if (inserted == 0)
            return new InputInjectionOutcome(InputInjectionStatus.Rejected);

        bool cleanupSucceeded = inserted < (uint)strokes.Count;
        if (!cleanupSucceeded)
            return new InputInjectionOutcome(
                InputInjectionStatus.Partial,
                cleanupSucceeded,
                inserted);
        foreach (var release in BuildRecoveryKeyUps(strokes, inserted))
        {
            if (sender([release]) != 1)
                cleanupSucceeded = false;
        }

        return new InputInjectionOutcome(
            InputInjectionStatus.Partial,
            cleanupSucceeded,
            inserted);
    }

    internal static bool TrySendKeySequenceForOperation(
        SelectionOperation operation,
        IReadOnlyList<KeyStroke> strokes,
        Func<IReadOnlyList<KeyStroke>, uint> sender,
        out InputInjectionOutcome outcome)
    {
        outcome = new InputInjectionOutcome(InputInjectionStatus.Rejected);
        if (!operation.TryClaim()) return false;
        outcome = SendKeySequence(strokes, sender);
        return true;
    }

    private static IReadOnlyList<KeyStroke> BuildRecoveryKeyUps(
        IReadOnlyList<KeyStroke> strokes, uint inserted)
    {
        var pressed = new List<KeyStroke>();
        int accepted = Math.Min(strokes.Count, checked((int)inserted));
        for (int i = 0; i < accepted; i++)
        {
            var stroke = strokes[i];
            if (!stroke.KeyUp)
            {
                pressed.Add(stroke);
                continue;
            }

            int down = pressed.FindLastIndex(
                candidate => candidate.VirtualKey == stroke.VirtualKey);
            if (down >= 0) pressed.RemoveAt(down);
        }

        var releases = new List<KeyStroke>(pressed.Count);
        for (int i = pressed.Count - 1; i >= 0; i--)
        {
            var down = pressed[i];
            releases.Add(down with { KeyUp = true });
        }
        return releases;
    }

    private static uint SendNativeKeyStrokes(
        IReadOnlyList<KeyStroke> strokes)
    {
        var inputs = new INPUT[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
            inputs[i] = MakeKeyInput(strokes[i]);
        return SendInput((uint)inputs.Length, inputs, InputSize);
    }

    private static INPUT MakeKeyInput(KeyStroke stroke)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = stroke.VirtualKey;
        uint flags = 0;
        if (stroke.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (stroke.KeyUp) flags |= KEYEVENTF_KEYUP;
        input.u.ki.dwFlags = flags;
        return input;
    }

    // P/Invoke structs
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
