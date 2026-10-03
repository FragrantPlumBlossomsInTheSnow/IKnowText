using System.Threading;
using System.Windows;
using SnapActions.Core;
using SnapActions.Helpers;

namespace SnapActions.Actions;

internal enum ResultDestination { Copy, Replace }

/// <summary>Owns action execution and the clipboard/input transaction. Views only choose and present outcomes.</summary>
internal static class ActionRunner
{
    internal static async Task<ActionResult> ExecuteAsync(IAction action, SelectionSnapshot selection)
    {
        var operation = selection.Operation;
        try
        {
            if (!await operation.CanUseSelectionAsync())
            {
                Log.Info($"操作选择验证被拒绝 (提供者: {selection.Provider}, 当前操作: {operation.IsCurrent})");
                return Cancelled();
            }
            if (action is IOperationAction targeted)
            {
                if (!selection.CanReplace || !operation.TryClaim()) return Cancelled();
                return await targeted.ExecuteAsync(selection.Text, selection.Analysis, operation);
            }
            // 异步动作（联网脚本）：耗时长且不向目标注入输入，因此不套 TryCommit —— 同步提交会把
            // 整个 HTTP 往返压在 UI 线程上。选区有效性由上面的 CanUseSelectionAsync 前置保证。
            if (action is IAsyncAction asyncAction)
                return await asyncAction.ExecuteAsync(selection.Text, selection.Analysis, CancellationToken.None);
            ActionResult? result = null;
            return operation.TryCommit(() => { result = action.Execute(selection.Text, selection.Analysis); return true; })
                ? result! : Cancelled();
        }
        catch (Exception ex)
        {
            Log.Warn($"操作失败 ({ex.GetType().Name})");
            return new(false, Message: "无法完成该操作。");
        }
    }

    internal static async Task<ActionResult> ApplyTextAsync(string text, SelectionSnapshot selection, ResultDestination destination)
    {
        var operation = selection.Operation;
        bool paste = destination == ResultDestination.Replace;
        // 不做"选区可编辑"判断（CanReplace）：合成键捕获的目标往往判不出可编辑性，
        // 前置拦截会让替换永远失败。能否注入由窗口级校验 + 目标应用决定（同翻译替换）。
        if (!await operation.CanUseSelectionAsync())
        {
            Log.Warn("Apply replace failed: selection validation rejected up front");
            return Cancelled();
        }
        if (paste && !await InputExecutor.PreparePasteForReplaceAsync(operation)) return Cancelled();
        bool restoreAfterCopy = !paste && Config.SettingsManager.Current.RestoreClipboardAfterAction;
        ClipboardTransaction.ClipboardSnapshot? previous = null;
        ClipboardTransaction.ClipboardObservation? written = null;
        bool inputAttempted = false;
        try
        {
            if (paste || restoreAfterCopy)
            {
                previous = ClipboardTransaction.SnapshotClipboard();
                if (previous == null)
                {
                    Log.Warn("Apply replace failed: clipboard snapshot failed");
                    return new(false, Message: "剪贴板格式无法安全保留")
                        { CanRetry = !paste && operation.IsCurrent };
                }
                if (!ClipboardTransaction.CanStartClipboardWrite(previous, ClipboardTransaction.ObserveClipboard()))
                {
                    Log.Warn("Apply replace failed: clipboard changed before write");
                    return Cancelled();
                }
                // requireExactTarget: false —— 窗口级校验已在上面前置完成；替换/粘贴目标
                // 往往无 AutomationRuntimeId，严格精确输入目标校验会统一拒绝（同翻译替换）。
                written = await ClipboardTransaction.TrySetClipboardTextForOperationAsync(operation, previous, text, requireExactTarget: false);
                if (written == null)
                {
                    Log.Warn("Apply replace failed: clipboard write rejected");
                    return new(false, Message: "剪贴板已更改或无法写入--操作已取消");
                }
            }
            else if (!ClipboardTransaction.TryCommitClipboardMutation(operation, () => TryCopy(text)))
                return operation.IsCurrent
                    ? new(false, Message: "无法写入剪贴板--请重试") { CanRetry = true }
                    : Cancelled();

            if (paste)
            {
                if (!await operation.CanUseSelectionAsync())
                {
                    Log.Warn("Apply replace failed: selection invalidated after clipboard write");
                    ClipboardTransaction.RestoreClipboardIfUnchanged(previous!, written!.Value);
                    return Cancelled();
                }
                inputAttempted = true;
                var outcome = await InputExecutor.TrySimulatePasteForReplaceAsync(operation, written);
                if (outcome.Status == InputExecutor.InputInjectionStatus.Succeeded) return new(true, Message: "替换选择");
                if (outcome.Status != InputExecutor.InputInjectionStatus.Partial || InputExecutor.CanRollbackAfterPartialPaste(outcome))
                    ClipboardTransaction.RestoreClipboardIfUnchanged(previous!, written!.Value);
                Log.Warn($"Apply replace failed: paste injection {outcome.Status}");
                return new(false, Message: outcome.Status == InputExecutor.InputInjectionStatus.Partial
                    ? "Windows只接受粘贴快捷方式的一部分。请在重试之前检查目标。"
                    : "焦点已移动--粘贴已取消");
            }

            if (!restoreAfterCopy || previous == null || written is not { } accepted) return new(true, Message: "复制结果");
            _ = RestoreLaterAsync(previous, accepted);
            previous = null; // delayed restore owns and disposes the snapshot
            return new(true, Message: "复制结果");
        }
        catch (Exception ex)
        {
            // Once input may have reached the target, do not restore a payload it may still be reading.
            if (!inputAttempted && previous != null && written is { } accepted)
                ClipboardTransaction.RestoreClipboardIfUnchanged(previous, accepted);
            Log.Warn($"应用结果失败 ({ex.GetType().Name})");
            return new(false, Message: inputAttempted ? "无法确认粘贴。重试前请检查目标。" : "无法复制结果。再试一次。")
                { CanRetry = !paste && operation.IsCurrent };
        }
        finally { previous?.Dispose(); }
    }

    private static async Task RestoreLaterAsync(ClipboardTransaction.ClipboardSnapshot snapshot,
        ClipboardTransaction.ClipboardObservation accepted)
    {
        try
        {
            await Task.Delay(3000);
            ClipboardTransaction.RestoreClipboardIfUnchanged(snapshot, accepted);
        }
        catch (Exception ex) { Log.Warn($"剪贴板还原失败 ({ex.GetType().Name})"); }
        finally { snapshot.Dispose(); }
    }

    internal static bool TryCopy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch { return false; }
    }

    private static ActionResult Cancelled() => new(false, Message: "选择或焦点已更改--操作已取消");
}
