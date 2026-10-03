using System.Diagnostics;
using SnapActions.Detection;

namespace SnapActions.Core;

/// <summary>
///     Reads the selection through UI Automation and binds the result to the original operation before
///     presentation. (The browser selection companion was removed with its Chrome extension, so this is
///     the only provider for automatic capture; explicit Ctrl+C reads go through
///     <see cref="SelectionProviderKind.ExplicitCopy"/>.)
/// </summary>
internal sealed class SelectionCoordinator
{
    internal async Task<SelectionSnapshot?> CaptureAsync(SelectionOperation operation,
        UiaSelectionProvider.SelectionGesture gesture, int x, int y)
    {
        long started = Stopwatch.GetTimestamp();
        var result = await UiaSelectionProvider.CaptureSelectedTextAsync(operation, gesture, x, y);
        CaptureDiagnostics.Record("UIA read", started);
        var text = result.Text;
        operation = result.Operation;
        if (!operation.CanInjectInput || string.IsNullOrWhiteSpace(text) || text.Length > SelectionSnapshot.MaximumTextLength)
        {
            CaptureDiagnostics.SetStatus("UIA选择不可用、空、模糊或模糊");
            return null;
        }
        bool editable = await ForegroundGuard.RunBoundedAutomationAsync(ForegroundApp.IsEditableFieldFocused, false, 500);
        editable &= operation.HasInputValidation;
        if (!operation.CanInjectInput) { CaptureDiagnostics.SetStatus("选择已过时"); return null; }
        CaptureDiagnostics.SetStatus($"{SelectionProviderKind.UiAutomation}: 已捕获选择");
        started = Stopwatch.GetTimestamp();
        var analysis = new TextClassifier().Classify(text);
        CaptureDiagnostics.Record("Classification", started);
        return new SelectionSnapshot(text, analysis, operation, editable, SelectionProviderKind.UiAutomation);
    }
}
