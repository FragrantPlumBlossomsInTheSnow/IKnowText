using System.Threading;
using SnapActions.Detection;

namespace SnapActions.Actions;

public record ActionResult(bool Success, string? ResultText = null, string? Message = null)
{
    // Only failures known not to have attempted target input may offer an in-place retry.
    internal bool CanRetry { get; init; }

    // Actions that open their own UI (e.g. translate popup) must not let the toolbar hide
    // right after Execute — the popup lives on the toolbar window.
    internal bool KeepToolbarOpen { get; init; }
}

public interface IAction
{
    string Id { get; }
    string Name { get; }
    ActionCategory Category { get; }
    bool CanExecute(string text, TextAnalysis analysis);
    ActionResult Execute(string text, TextAnalysis analysis);

    /// <summary>
    /// True if Execute() is pure (no I/O, no clipboard write, no key/mouse input, no process launch).
    /// Hover preview only runs Execute() for actions where this is true. Default: false.
    /// </summary>
    bool IsPreviewSafe => false;
}

/// <summary>
/// Implemented only by actions that can mutate the focused application. They require the
/// immutable selection operation; the ordinary IAction entry point must fail closed.
/// </summary>
internal interface IOperationAction
{
    Task<ActionResult> ExecuteAsync(
        string text, TextAnalysis analysis, Core.SelectionOperation operation);
}

/// <summary>
/// 需要异步执行的动作（当前只有「允许访问网络」的 JS 脚本）。它不向目标注入输入，因此只需要选区
/// 仍然有效，不需要 TryCommit；<paramref name="ct"/> 是本次执行的总时长上限（由调用方给出）。
/// </summary>
internal interface IAsyncAction
{
    Task<ActionResult> ExecuteAsync(string text, TextAnalysis analysis, CancellationToken ct);
}
