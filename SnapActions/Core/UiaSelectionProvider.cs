using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using SnapActions.Config;

namespace SnapActions.Core;

internal static class UiaSelectionProvider
{
    internal readonly record struct CaptureResult(
        string? Text,
        SelectionOperation Operation);

    internal readonly record struct SelectionGesture(
        bool IsDrag,
        int ClickCount,
        int StartX,
        int StartY,
        int EndX,
        int EndY);

    internal readonly record struct Utf16Span(int Start, int Length)
    {
        internal int End => Start + Length;
    }

    private static readonly SemaphoreSlim CaptureLock = new(1, 1);

    /// <summary>Reads through UI Automation only. Capturing can never synthesize copy or touch the clipboard.</summary>
    internal static async Task<CaptureResult> CaptureSelectedTextAsync(
        SelectionOperation operation, SelectionGesture gesture, int cursorX, int cursorY)
    {
        CaptureResult Result(string? text) => new(
            text?.Length <= SelectionSnapshot.MaximumTextLength ? text : null, operation);
        await CaptureLock.WaitAsync();
        try
        {
            if (!await operation.CanInjectInputAsync())
            {
                SnapActions.Helpers.Log.Info("Capture aborted: cannot inject input");
                return Result(null);
            }
            if (UiaSkipApps.Contains(ForegroundApp.GetActiveProcessName() ?? ""))
            {
                SnapActions.Helpers.Log.Info("Capture aborted: app is UIA-skip listed");
                return Result(null);
            }
            var probe = await RunBoundedUiaAsync(
                () => ProbeSelectionViaUIA(cursorX, cursorY, operation.Target.ProcessId,
                    operation.Target.AutomationRuntimeId, gesture, preferExactCopy: false, acceptCursorPointText: true),
                new SelectionProbe(SelectionProbeOutcome.Unknown, null, "UIA busy or unavailable"),
                busyHandoffMs: operation.Target.AutomationRuntimeId == null ? UiaBusyHandoffMs : 0);
            if (!await operation.CanInjectInputAsync()) return Result(null);
            operation = operation.WithTarget(BindProbeIdentity(operation.Target, probe));
            if (probe.Outcome == SelectionProbeOutcome.HasText)
            {
                operation = operation.WithInputValidation(probe.ValidateInput);
                return Result(probe.Text);
            }
            if (probe.Outcome is SelectionProbeOutcome.SuppressItemElement or SelectionProbeOutcome.UntrustedText)
            {
                SnapActions.Helpers.Log.Info($"Capture aborted: probe outcome {probe.Outcome} ({(probe.Reason ?? "no reason")})");
                return Result(null);
            }
            var fallback = await RunBoundedUiaAsync(
                () => CopyViaUIA(operation.Target.ProcessId, operation.Target.AutomationRuntimeId), null);
            if (fallback is { } selected)
                operation = operation.WithTarget(BindProbeIdentity(operation.Target, selected))
                    .WithInputValidation(selected.ValidateInput);
            var uiaText = fallback?.Text;
            if (string.IsNullOrEmpty(uiaText))
            {
                // UIA 全链路（聚焦树 + 光标点 + CopyViaUIA）都没读到选区，走合成键兜底前先记录
                // 关键闸门状态，便于区分：只能注入但未勾选、滚动冷却中、还是注入后被拒。
                SnapActions.Helpers.Log.Info(
                    $"UIA produced no text (probe={probe.Outcome}, canInject={operation.CanInjectInput}, " +
                    $"scrollCooldown={MouseHook.IsRecentScroll(ScrollCooldownAfterScrollMs)}, syntheticKeys={UseSyntheticKeys()})");
            }
            // UIA 永远优先。勾选“默认使用合成键”后，UIA 读不到选区才用合成复制键兜底
            // （Java Swing IDE 如 Rider、部分 Chromium）；未勾选则只走 UIA、绝不注入按键。
            if (!string.IsNullOrEmpty(uiaText))
                return Result(operation.CanInjectInput ? uiaText : null);
            if (!await operation.CanInjectInputAsync())
            {
                SnapActions.Helpers.Log.Info("Synthetic fallback skipped: cannot inject input after UIA returned empty");
                return Result(null);
            }
            if (!UseSyntheticKeys())
            {
                SnapActions.Helpers.Log.Info("Synthetic fallback skipped: synthetic keys disabled in settings");
                return Result(null);
            }
            if (MouseHook.IsRecentScroll(ScrollCooldownAfterScrollMs))
            {
                SnapActions.Helpers.Log.Info("Synthetic fallback skipped: scroll cooldown active");
                return Result(null);
            }
            // 经精确前台目标校验、剪贴板事务读回并恢复原剪贴板。
            var synthetic = await TrySyntheticCopyAsync(operation);
            if (!operation.CanInjectInput) return Result(null);
            return !string.IsNullOrEmpty(synthetic) ? Result(synthetic) : Result(operation.CanInjectInput ? uiaText : null);
        }
        catch (Exception ex)
        {
            SnapActions.Helpers.Log.Error("UIA capture", ex);
            return Result(null);
        }
        finally { CaptureLock.Release(); }
    }

    internal static async Task<SelectionOperation> BindInputSelectionAsync(SelectionOperation operation)
    {
        if (!await operation.CanInjectInputAsync()) return operation.WithInputValidation(null);
        var probe = await RunBoundedUiaAsync(
            () => CopyViaUIA(operation.Target.ProcessId, operation.Target.AutomationRuntimeId, allowEmpty: true), null);
        return probe is { } selected
            ? operation.WithTarget(BindProbeIdentity(operation.Target, selected)).WithInputValidation(selected.ValidateInput)
            : operation.WithInputValidation(null);
    }

    private static Func<bool>? CreateInputValidation(TextPattern pattern, TextPatternRange[] ranges, string expectedText)
    {
        try
        {
            if (ranges.Length is 0 or > 256 || expectedText.Length > SelectionSnapshot.MaximumTextLength) return null;
            var captured = ranges.Select(range => range.Clone()).ToArray();
            var text = captured.Select(range => range.GetText(SelectionSnapshot.MaximumTextLength + 1)).ToArray();
            // Geometry may rescue Chromium display text even when UIA reports an adjacent range.
            // Such a capture remains useful for Copy but cannot authorize an edit of that range.
            if (CombineSelectionRanges(text) != expectedText) return null;
            return () =>
            {
                if (!ForegroundApp.IsEditableFieldFocused()) return false;
                var current = pattern.GetSelection();
                if (current.Length != captured.Length) return false;
                for (int i = 0; i < captured.Length; i++)
                    if (captured[i].CompareEndpoints(TextPatternRangeEndpoint.Start, current[i], TextPatternRangeEndpoint.Start) != 0
                        || captured[i].CompareEndpoints(TextPatternRangeEndpoint.End, current[i], TextPatternRangeEndpoint.End) != 0
                        || current[i].GetText(SelectionSnapshot.MaximumTextLength + 1) != text[i])
                        return false;
                return true;
            };
        }
        catch { return null; }
    }

    private static readonly HashSet<string> UiaSkipApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "thunderbird",
    };

    /// <summary>滚动后的冷却窗口（毫秒）：期间抑制合成键注入，避免拖动/滚轮后页面回卷到光标处。</summary>
    private const long ScrollCooldownAfterScrollMs = 300;

    /// <summary>
    /// 是否允许在 UIA 读不到选区时用合成复制键兜底（设置项“默认使用合成键”）。
    /// 未勾选则只走 UIA、绝不注入按键。
    /// </summary>
    private static bool UseSyntheticKeys() => SettingsManager.Current.UseSyntheticKeys;

    private const int UiaCallTimeoutMs = 500;
    private const int UiaBusyHandoffMs = 50;

    /// <summary>
    /// Runs a UIA call with a hard timeout and a shared pre-start single-flight gate. If a broken
    /// provider blocks inside GetSelection/GetText, the await returns its fallback but the gate
    /// stays occupied until that underlying call really exits. Calls normally fail fast while it
    /// is occupied; the selection pre-gate may wait once for a short event-identity handoff, then
    /// retry without ever admitting concurrent UIA workers.
    /// </summary>
    private static Task<T> RunBoundedUiaAsync<T>(
        Func<T> uiaCall,
        T onTimeout,
        int busyHandoffMs = 0) =>
        ForegroundGuard.RunBoundedAutomationAsync(
            uiaCall, onTimeout, UiaCallTimeoutMs, busyHandoffMs);
    /// <summary>
    /// Maximum UIA parent levels to walk when probing for a TextPattern. Same rationale as
    /// <see cref="ForegroundApp.IsTextInputAtPoint"/>: leaf elements (a span / anchor / svg)
    /// usually don't expose TextPattern themselves even though the paragraph / document
    /// ancestor does.
    /// </summary>
    private const int TextPatternParentWalkDepth = 6;

    internal enum SelectionProbeOutcome
    {
        /// <summary>UIA returned selected text that passed identity and geometry checks.</summary>
        HasText,
        /// <summary>UIA confirmed a selection, but its returned text is not trusted.
        /// A later explicit user copy can still provide text.</summary>
        ConfirmedTextPreferExact,
        /// <summary>UIA returned text, but an exact clipboard-free gesture reconstruction was
        /// unavailable or a double-click word did not match its selection length.</summary>
        UntrustedText,
        /// <summary>The focused element is a non-text item (Explorer file, desktop icon, list row).
        /// Definitive — capture must not run (WM_COPY would copy the item's name).</summary>
        SuppressItemElement,
        /// <summary>A TextPattern was found but reported an empty selection. Usually means "no
        /// selection", but some providers lie (report empty despite a real selection), so this is
        /// a signal to try the remaining read-only UIA path.</summary>
        EmptyTextPattern,
        /// <summary>UIA could not establish a text selection.</summary>
        Unknown,
    }

    internal readonly record struct SelectionProbe(
        SelectionProbeOutcome Outcome,
        string? Text,
        string? Reason,
        string? AutomationRuntimeId = null,
        Func<bool>? ValidateInput = null);

    internal static SelectionProbe ClassifyUiaSelection(
        string text,
        bool fromCursorPoint,
        bool preferExactCopy = false,
        bool acceptCursorPointText = false,
        string? automationRuntimeId = null,
        string? gestureText = null,
        bool requireGestureText = false,
        bool acceptGestureLengthMismatch = false)
    {
        if (text.Length > SelectionSnapshot.MaximumTextLength || gestureText?.Length > SelectionSnapshot.MaximumTextLength)
            return new SelectionProbe(SelectionProbeOutcome.UntrustedText, null, "Selection is too large", automationRuntimeId);
        if ((fromCursorPoint && !acceptCursorPointText) || preferExactCopy)
        {
            return new SelectionProbe(
                SelectionProbeOutcome.ConfirmedTextPreferExact,
                null,
                "selection confirmed; exact copy preferred",
                automationRuntimeId);
        }

        bool gestureDefinesSelection = !string.IsNullOrEmpty(gestureText)
                                       && (acceptGestureLengthMismatch
                                           || gestureText.Length == text.Length);
        if (requireGestureText && !gestureDefinesSelection)
        {
            return new SelectionProbe(
                SelectionProbeOutcome.UntrustedText,
                null,
                string.IsNullOrEmpty(gestureText)
                    ? "Chromium gesture range was unavailable"
                    : "Chromium double-click range did not match the selected range length",
                automationRuntimeId);
        }

        return new SelectionProbe(
            SelectionProbeOutcome.HasText,
            gestureDefinesSelection ? gestureText : text,
            gestureDefinesSelection
                ? "gesture-derived selection text accepted"
                : "UIA selection text accepted",
            automationRuntimeId);
    }

    internal static ForegroundTarget BindProbeIdentity(
        ForegroundTarget target,
        SelectionProbe probe) =>
        target.IsComplete
        && target.AutomationRuntimeId == null
        && probe.AutomationRuntimeId != null
            ? target with { AutomationRuntimeId = probe.AutomationRuntimeId }
            : target;

    /// <summary>
    /// Item-style control types that are NOT text. When the focused element is one of these
    /// AND exposes SelectionItemPattern AND we found no TextPattern up the tree, we treat the
    /// "selection" as an item selection (file in Explorer, desktop icon, list-box row, tree
    /// node) and suppress. Deliberately narrow — Pane / Custom / Document stay out because
    /// browsers and Electron focus those for real text contexts.
    /// </summary>
    private static readonly System.Windows.Automation.ControlType[] NonTextItemTypes =
    [
        System.Windows.Automation.ControlType.DataItem,
        System.Windows.Automation.ControlType.ListItem,
        System.Windows.Automation.ControlType.TreeItem,
    ];

    /// <summary>Reads selected text from the original focused element or gesture point.
    /// Chromium gestures require matching geometry to avoid adjacent bidi runs. Missing or
    /// ambiguous evidence stays unavailable; automatic capture never invokes a copy fallback.</summary>
    internal static SelectionProbe ProbeSelectionViaUIA(
        int cursorX,
        int cursorY,
        uint expectedProcessId,
        string? expectedRuntimeId,
        SelectionGesture gesture,
        bool preferExactCopy,
        bool acceptCursorPointText = false)
    {
        AutomationElement? originalFocused = null;
        try
        {
            originalFocused = AutomationElement.FocusedElement;
            if (originalFocused == null)
                return new SelectionProbe(SelectionProbeOutcome.Unknown, null, "no focused element");
            if ((uint)originalFocused.Current.ProcessId != expectedProcessId)
                return new SelectionProbe(
                    SelectionProbeOutcome.Unknown, null, "focused element belongs to another process");
            if (!MatchesAutomationRuntimeId(
                    originalFocused, expectedRuntimeId))
                return new SelectionProbe(
                    SelectionProbeOutcome.Unknown, null, "focused element identity changed");
            string? RuntimeIdForResult() =>
                expectedRuntimeId
                ?? TryReadAutomationRuntimeId(originalFocused);

            // Walk up looking for TextPattern. If ANY ancestor has a non-empty selection,
            // return that focused-tree text immediately. If we exhaust the walk and saw at
            // least one TextPattern but all were empty → restrict the fallback. If we never
            // saw TextPattern → fall through to the item-element check below.
            var walker = TreeWalker.RawViewWalker;
            var element = originalFocused;
            bool sawAnyTextPattern = false;
            for (int depth = 0; element != null && depth < TextPatternParentWalkDepth; depth++)
            {
                try
                {
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pat))
                    {
                        sawAnyTextPattern = true;
                        var tp = (TextPattern)pat;
                        var ranges = tp.GetSelection();
                        if (ranges != null && ranges.Length > 0)
                        {
                            var combined = CombineSelectionRanges(ranges.Select(r => r.GetText(SelectionSnapshot.MaximumTextLength + 1)));
                            if (!string.IsNullOrEmpty(combined))
                            {
                                // 沿用旧版（v2.4.5 备份 TextCapture.cs）：不强制 Chromium 手势文本。
                                // 扩展桥未连接时，RangeFromPoint 重建手势选区常失败，导致浏览器
                                // 双击/拖拽划词全部 UntrustedText abort（2026-09-19 连败日志）。
                                // UIA 读到的选区文本直接接受，读不到再走剪贴板/合成键兜底。
                                bool requireGestureText = false;
                                string? gestureText = null;
                                var selected = ClassifyUiaSelection(
                                    combined,
                                    fromCursorPoint: false,
                                    preferExactCopy: preferExactCopy,
                                    automationRuntimeId: RuntimeIdForResult(),
                                    gestureText: gestureText,
                                    requireGestureText: requireGestureText,
                                    acceptGestureLengthMismatch: gesture.IsDrag);
                                return selected with { ValidateInput = selected.Outcome == SelectionProbeOutcome.HasText
                                    ? CreateInputValidation(tp, ranges, selected.Text!) : null };
                            }
                        }
                        // TextPattern at this level returned no selection text. Keep walking up
                        // — an ancestor pane / document may have the real selection (browsers
                        // often expose TextPattern at multiple levels with the leaf empty).
                    }
                }
                catch { /* per-level UIA failure — try the parent */ }

                try { element = walker.GetParent(element); }
                catch { break; }
            }

            if (sawAnyTextPattern)
                return new SelectionProbe(SelectionProbeOutcome.EmptyTextPattern,
                    null,
                    "TextPattern present but selection is empty",
                    RuntimeIdForResult());

            // No TextPattern anywhere up the walk from FOCUS. Before classifying, read the
            // selection from the element UNDER THE CURSOR: X/Twitter focuses the tweet container
            // (a ListItem — or, inconsistently, a plain group), not the text, so the upward walk
            // from focus misses the tweet's own text, which sits right under the cursor. Covers
            // both the item case AND the plain-Unknown case.
            // Read the selection from the element UNDER THE CURSOR. For bidi content the returned
            // string may be an adjacent run rather than the exact visual selection, but a non-empty
            // range still proves this is selectable text rather than a bare file/list item.
            var atPoint = TryReadSelectionAtPoint(
                cursorX, cursorY, expectedProcessId, gesture, acceptCursorPointText);
            if (atPoint is { } pointSelection)
            {
                var selected = ClassifyUiaSelection(
                    pointSelection.Text,
                    fromCursorPoint: true,
                    acceptCursorPointText: acceptCursorPointText,
                    automationRuntimeId: RuntimeIdForResult(),
                    gestureText: pointSelection.GestureText,
                    requireGestureText: pointSelection.RequireGestureText,
                    acceptGestureLengthMismatch: gesture.IsDrag);
                return selected with { ValidateInput = selected.Outcome == SelectionProbeOutcome.HasText
                    ? pointSelection.ValidateInput : null };
            }

            // Layer C: check the originally-focused element for non-text item patterns —
            // Explorer file rows, desktop icons, list-box rows. SelectionItemPattern means
            // "I am a selectable item" (vs. text); ControlType keeps us off Pane / Custom /
            // Document which browsers and Electron focus for real text contexts.
            try
            {
                var ct = originalFocused.Current.ControlType;
                if (NonTextItemTypes.Contains(ct)
                    && originalFocused.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
                    return new SelectionProbe(SelectionProbeOutcome.SuppressItemElement,
                        null,
                        $"focused element is {ct.ProgrammaticName} with SelectionItemPattern",
                        RuntimeIdForResult());
            }
            catch { /* couldn't read ControlType — fall through to Unknown */ }

            return new SelectionProbe(
                SelectionProbeOutcome.Unknown,
                null,
                "no TextPattern, not a known non-text item",
                RuntimeIdForResult());
        }
        catch (Exception ex)
        {
            // A provider failure may try the remaining read-only UIA path, never a copy command.
            return new SelectionProbe(SelectionProbeOutcome.Unknown, null, $"UIA exception: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Rebuilds a Chromium selection from the mouse coordinates instead of trusting
    /// TextPattern.GetSelection().GetText(), which can return an adjacent run for mixed RTL/LTR
    /// content. Same-line drags select the characters whose visual centers fall inside the drag;
    /// the visual line is then rotated back to the logical order exposed by the element name.
    /// Double-click word expansion is accepted only when its UTF-16 length matches GetSelection.
    /// </summary>
    private static bool RequiresChromiumGestureText(
        AutomationElement element,
        SelectionGesture gesture)
    {
        if (!gesture.IsDrag && gesture.ClickCount != 2) return false;
        try
        {
            return string.Equals(
                element.Current.FrameworkId,
                "Chrome",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadChromiumSelectionFromGesture(
        TextPattern textPattern,
        AutomationElement element,
        SelectionGesture gesture,
        string selectedText,
        TextPatternRange[] selectedRanges)
    {
        try
        {
            if (gesture.IsDrag)
                return TryReadChromiumDragFromGeometry(textPattern, element, gesture, selectedText, selectedRanges);
            if (gesture.ClickCount != 2) return null;

            var range = textPattern.RangeFromPoint(
                new Point(gesture.EndX, gesture.EndY));
            range.ExpandToEnclosingUnit(TextUnit.Word);
            if (!IsRangeWithinDocument(range, textPattern.DocumentRange)) return null;

            var text = range.GetText(SelectionSnapshot.MaximumTextLength + 1);
            if (text.Length > selectedText.Length)
            {
                var withoutTrailingWhitespace = text.TrimEnd();
                if (withoutTrailingWhitespace.Length == selectedText.Length)
                    return withoutTrailingWhitespace;
            }

            return text;
        }
        catch
        {
            return null;
        }
    }

    private const int ChromiumGeometryLineLimit = 512;

    private static bool IsRangeWithinDocument(TextPatternRange range, TextPatternRange document) =>
        range.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) >= 0
        && range.CompareEndpoints(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End) <= 0;

    private static string? TryReadChromiumDragFromGeometry(
        TextPattern textPattern,
        AutomationElement element,
        SelectionGesture gesture,
        string selectedText,
        TextPatternRange[] selectedRanges)
    {
        var anchorLine = textPattern.RangeFromPoint(
            new Point(gesture.StartX, gesture.StartY));
        anchorLine.ExpandToEnclosingUnit(TextUnit.Line);
        var focusLine = textPattern.RangeFromPoint(
            new Point(gesture.EndX, gesture.EndY));
        focusLine.ExpandToEnclosingUnit(TextUnit.Line);

        // Chromium can return unrelated UI chrome from RangeFromPoint (observed in VS Code).
        // Matching coordinates and line identity cannot make an out-of-document range valid.
        var document = textPattern.DocumentRange;
        if (!IsRangeWithinDocument(anchorLine, document) || !IsRangeWithinDocument(focusLine, document))
            return null;

        // A cross-line drag is not a horizontal rectangle. Keep the native selection (including
        // punctuation/newlines) only when its ranges agree with both visible gesture boundaries.
        if (!anchorLine.Compare(focusLine)
            || !HasSingleVisualLineGeometry(anchorLine.GetBoundingRectangles(), gesture))
            return TryReadChromiumMultilineSelection(textPattern, document, anchorLine, focusLine,
                gesture, selectedText, selectedRanges);
        if (anchorLine.GetText(ChromiumGeometryLineLimit + 1).Length
            > ChromiumGeometryLineLimit)
            return null;

        var cursor = anchorLine.Clone();
        cursor.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            cursor,
            TextPatternRangeEndpoint.Start);

        var visualText = new System.Text.StringBuilder();
        var selectedVisualText = new System.Text.StringBuilder();
        var selectedSpans = new List<Utf16Span>();
        for (int unit = 0; unit <= ChromiumGeometryLineLimit; unit++)
        {
            if (cursor.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    anchorLine,
                    TextPatternRangeEndpoint.End) >= 0)
                break;

            var character = cursor.Clone();
            if (character.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.End,
                    TextUnit.Character,
                    1) <= 0)
                return null;
            if (character.CompareEndpoints(
                    TextPatternRangeEndpoint.End,
                    anchorLine,
                    TextPatternRangeEndpoint.End) > 0)
            {
                character.MoveEndpointByRange(
                    TextPatternRangeEndpoint.End,
                    anchorLine,
                    TextPatternRangeEndpoint.End);
            }

            var characterText = character.GetText(SelectionSnapshot.MaximumTextLength + 1);
            if (characterText.Length == 0) return null;
            int characterStart = visualText.Length;
            visualText.Append(characterText);

            if (IsCharacterInsideDrag(
                    character.GetBoundingRectangles(), gesture))
            {
                selectedSpans.Add(new Utf16Span(
                    characterStart, characterText.Length));
                selectedVisualText.Append(characterText);
            }

            cursor.MoveEndpointByRange(
                TextPatternRangeEndpoint.Start,
                character,
                TextPatternRangeEndpoint.End);
            cursor.MoveEndpointByRange(
                TextPatternRangeEndpoint.End,
                cursor,
                TextPatternRangeEndpoint.Start);
        }

        if (cursor.CompareEndpoints(
                TextPatternRangeEndpoint.Start,
                anchorLine,
                TextPatternRangeEndpoint.End) < 0)
            return null;
        if (selectedSpans.Count == 0) return null;
        string automationName;
        try { automationName = element.Current.Name; }
        catch { automationName = string.Empty; }

        var logicalText = MapVisualSelectionToLogicalText(
            visualText.ToString(), selectedSpans, automationName);
        if (!string.IsNullOrWhiteSpace(logicalText)) return logicalText;

        // A single directional run keeps the same character order even if the provider moved the
        // run to the other side of an RTL line. Do not guess when both Arabic and Latin survived.
        var visualSelection = selectedVisualText.ToString();
        return !string.IsNullOrWhiteSpace(visualSelection)
               && !ContainsArabicAndLatin(visualSelection)
            ? visualSelection
            : null;
    }

    private static string? TryReadChromiumMultilineSelection(TextPattern pattern, TextPatternRange document,
        TextPatternRange anchorLine, TextPatternRange focusLine, SelectionGesture gesture,
        string selectedText, TextPatternRange[] selectedRanges)
    {
        if (selectedRanges.Length != 1 || string.IsNullOrWhiteSpace(selectedText)
            || selectedText.Length > SelectionSnapshot.MaximumTextLength) return null;
        var selected = selectedRanges[0].Clone();
        if (!IsRangeWithinDocument(selected, document)) return null;
        int lineOrder = anchorLine.CompareEndpoints(TextPatternRangeEndpoint.Start,
            focusLine, TextPatternRangeEndpoint.Start);
        bool anchorFirst = gesture.StartY < gesture.EndY;
        if (lineOrder != 0 && (lineOrder < 0) != anchorFirst) return null;
        var firstLine = anchorFirst ? anchorLine : focusLine;
        var lastLine = anchorFirst ? focusLine : anchorLine;
        if (selected.CompareEndpoints(TextPatternRangeEndpoint.Start, firstLine, TextPatternRangeEndpoint.Start) < 0
            || selected.CompareEndpoints(TextPatternRangeEndpoint.Start, firstLine, TextPatternRangeEndpoint.End) >= 0
            || selected.CompareEndpoints(TextPatternRangeEndpoint.End, lastLine, TextPatternRangeEndpoint.Start) <= 0
            || !MatchesMultilineSelectionGeometry(selected.GetBoundingRectangles(), anchorLine.GetBoundingRectangles(),
                focusLine.GetBoundingRectangles(), gesture)) return null;
        if (selected.CompareEndpoints(TextPatternRangeEndpoint.End, lastLine, TextPatternRangeEndpoint.End) > 0)
        {
            // Chromium can distinguish the end of a text node from the end of its visual line
            // even when no text lies between them. Accept only that empty caret-only gap.
            var gap = lastLine.Clone();
            gap.MoveEndpointByRange(TextPatternRangeEndpoint.Start, lastLine, TextPatternRangeEndpoint.End);
            gap.MoveEndpointByRange(TextPatternRangeEndpoint.End, selected, TextPatternRangeEndpoint.End);
            if (gap.GetText(1).Length != 0 || gap.GetBoundingRectangles().Any(IsTextRect)) return null;
        }

        // UIA work may race an app render or a newer selection. Never accept geometry for stale text.
        var current = pattern.GetSelection();
        if (current.Length != 1 || !selected.Compare(current[0])
            || current[0].GetText(SelectionSnapshot.MaximumTextLength + 1) != selectedText) return null;

        var enclosing = selected.GetEnclosingElement();
        if (enclosing.Current.ControlType != System.Windows.Automation.ControlType.Text)
            return ContainsRtlScript(selectedText) ? null : selectedText;
        string logicalName = enclosing.Current.Name;
        if (logicalName.Length > SelectionSnapshot.MaximumTextLength) return null;
        if (logicalName.Contains(selectedText, StringComparison.Ordinal)) return selectedText;

        // Chromium can expose an RTL line break before its text, despite the Text element's
        // logical Name placing it after the line. Prove each line's rotation before using Name.
        string firstText = firstLine.GetText(ChromiumGeometryLineLimit + 1);
        string lastText = lastLine.GetText(ChromiumGeometryLineLimit + 1);
        var prefix = firstLine.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selected, TextPatternRangeEndpoint.Start);
        int firstOffset = prefix.GetText(ChromiumGeometryLineLimit + 1).Length;
        var lastPart = lastLine.Clone();
        if (selected.CompareEndpoints(TextPatternRangeEndpoint.End, lastLine, TextPatternRangeEndpoint.End) < 0)
            lastPart.MoveEndpointByRange(TextPatternRangeEndpoint.End, selected, TextPatternRangeEndpoint.End);
        int lastLength = lastPart.GetText(ChromiumGeometryLineLimit + 1).Length;
        string? logicalText = MapMultilineSelectionToLogicalText(firstText, lastText,
            new Utf16Span(firstOffset, firstText.Length - firstOffset), new Utf16Span(0, lastLength),
            logicalName, selectedText);
        if (logicalText == null || enclosing.Current.Name != logicalName) return null;
        current = pattern.GetSelection();
        return current.Length == 1 && selected.Compare(current[0])
            && current[0].GetText(SelectionSnapshot.MaximumTextLength + 1) == selectedText ? logicalText : null;
    }

    private static bool ContainsRtlScript(string text) => text.Any(character =>
        character is >= '\u0590' and <= '\u08ff' or >= '\ufb1d' and <= '\ufdff' or >= '\ufe70' and <= '\ufeff');

    internal static string? MapMultilineSelectionToLogicalText(string firstVisualLine, string lastVisualLine,
        Utf16Span firstSelection, Utf16Span lastSelection, string logicalName, string selectedText)
    {
        var firstMappings = FindLogicalLineSelections(firstVisualLine, firstSelection, logicalName);
        var lastMappings = FindLogicalLineSelections(lastVisualLine, lastSelection, logicalName);
        (int Start, int End)? match = null;
        foreach (var first in firstMappings)
        foreach (var last in lastMappings)
        {
            if (first.LineStart + first.LineLength > last.LineStart || first.End > last.Start) continue;
            // Only the known line separators may fall between the selected endpoint fragments.
            // Extra prose would require proving additional visual lines, not inferring it from Name.
            if (!logicalName.AsSpan(first.End, last.Start - first.End).Trim().IsEmpty) continue;
            var candidate = (first.Start, last.End);
            if (match.HasValue && match.Value != candidate) return null;
            match = candidate;
        }
        if (match is not { } accepted || accepted.End - accepted.Start != selectedText.Length) return null;
        string result = logicalName[accepted.Start..accepted.End];
        // Rotation may move a separator, but must never add, remove, or substitute selected characters.
        char[] expected = selectedText.ToCharArray(), actual = result.ToCharArray();
        Array.Sort(expected);
        Array.Sort(actual);
        return expected.AsSpan().SequenceEqual(actual) ? result : null;
    }

    private readonly record struct LogicalLineSelection(int LineStart, int LineLength, int Start, int End);

    private static List<LogicalLineSelection> FindLogicalLineSelections(string visualLine, Utf16Span selected,
        string logicalName)
    {
        var result = new List<LogicalLineSelection>();
        if (visualLine.Length is 0 or > ChromiumGeometryLineLimit || selected.Start < 0 || selected.Length <= 0
            || selected.End > visualLine.Length || logicalName.Length > SelectionSnapshot.MaximumTextLength) return result;
        for (int rotation = 0; rotation < visualLine.Length; rotation++)
        {
            int selectedStart = selected.Length == visualLine.Length ? 0
                : (selected.Start - rotation + visualLine.Length) % visualLine.Length;
            if (selectedStart + selected.Length > visualLine.Length) continue;
            string logicalLine = visualLine[rotation..] + visualLine[..rotation];
            int offset = -1;
            while ((offset = logicalName.IndexOf(logicalLine, offset + 1, StringComparison.Ordinal)) >= 0)
            {
                var mapped = new LogicalLineSelection(offset, visualLine.Length, offset + selectedStart,
                    offset + selectedStart + selected.Length);
                if (result.Contains(mapped)) continue;
                if (result.Count == 64) return []; // Repeated text does not establish a unique source span.
                result.Add(mapped);
            }
        }
        return result;
    }

    private static bool IsTextRect(Rect rect) => !rect.IsEmpty && rect.Width > 1 && rect.Height > 0
        && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
        && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    internal static bool HasSingleVisualLineGeometry(IReadOnlyList<Rect> rectangles, SelectionGesture gesture)
    {
        var text = rectangles.Where(IsTextRect).ToArray();
        if (text.Length == 0 || text.Max(rect => rect.Top) >= text.Min(rect => rect.Bottom)) return false;
        double top = text.Min(rect => rect.Top), bottom = text.Max(rect => rect.Bottom);
        return gesture.StartY >= top && gesture.StartY <= bottom
            && gesture.EndY >= top && gesture.EndY <= bottom;
    }

    internal static bool MatchesMultilineSelectionGeometry(IReadOnlyList<Rect> selection,
        IReadOnlyList<Rect> anchorLine, IReadOnlyList<Rect> focusLine, SelectionGesture gesture)
    {
        static bool TryLineBounds(IReadOnlyList<Rect> rectangles, int y, out Rect bounds)
        {
            bounds = Rect.Empty;
            foreach (var rect in rectangles.Where(IsTextRect))
            {
                if (y < rect.Top || y > rect.Bottom) continue;
                bounds.Union(rect);
            }
            return !bounds.IsEmpty;
        }
        if (!gesture.IsDrag || !TryLineBounds(anchorLine, gesture.StartY, out var anchor)
            || !TryLineBounds(focusLine, gesture.EndY, out var focus)
            || !(anchor.Bottom <= focus.Top || focus.Bottom <= anchor.Top)) return false;
        var rectangles = selection.Where(IsTextRect).ToArray();
        if (rectangles.Length < 2) return false;
        double top = Math.Min(anchor.Top, focus.Top), bottom = Math.Max(anchor.Bottom, focus.Bottom);
        if (rectangles.Any(rect => rect.Top < top - 2 || rect.Bottom > bottom + 2)) return false;

        bool MatchesEndpoint(Rect line, int x, int y)
        {
            var row = rectangles.Where(rect => y >= rect.Top && y <= rect.Bottom).ToArray();
            // A selected wrapping space can extend just beyond TextUnit.Line's visible text.
            double tolerance = Math.Max(2, line.Height / 4);
            if (row.Length == 0 || row.Any(rect => rect.Left < line.Left - tolerance || rect.Right > line.Right + tolerance))
                return false;
            double boundary = Math.Clamp(x, line.Left, line.Right);
            return row.Any(rect => Math.Abs(rect.Left - boundary) <= tolerance
                || Math.Abs(rect.Right - boundary) <= tolerance);
        }
        return MatchesEndpoint(anchor, gesture.StartX, gesture.StartY)
            && MatchesEndpoint(focus, gesture.EndX, gesture.EndY);
    }

    internal static bool IsCharacterInsideDrag(
        IReadOnlyList<Rect> rectangles,
        SelectionGesture gesture)
    {
        double minimumX = Math.Min(gesture.StartX, gesture.EndX);
        double maximumX = Math.Max(gesture.StartX, gesture.EndX);
        double minimumY = Math.Min(gesture.StartY, gesture.EndY);
        double maximumY = Math.Max(gesture.StartY, gesture.EndY);
        bool hasNonCaretRectangle = rectangles.Any(
            rectangle => rectangle.Width > 1.0 && rectangle.Height > 0);

        foreach (var rectangle in rectangles)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) continue;
            // Chromium can attach a 1-pixel caret-affinity rectangle at a bidi boundary to a
            // character whose real glyph is at the far side of the line. Ignore only that tiny
            // duplicate; a genuinely narrow character with no wider rectangle remains eligible.
            if (hasNonCaretRectangle && rectangle.Width <= 1.0) continue;
            if (rectangle.Bottom < minimumY || rectangle.Top > maximumY) continue;
            double centerX = rectangle.Left + rectangle.Width / 2.0;
            if (centerX >= minimumX && centerX <= maximumX) return true;
        }

        return false;
    }

    internal static string? MapVisualSelectionToLogicalText(
        string visualLine,
        IReadOnlyList<Utf16Span> selectedSpans,
        string automationName)
    {
        if (visualLine.Length == 0 || selectedSpans.Count == 0) return null;
        if (selectedSpans.Any(span =>
                span.Start < 0 || span.Length <= 0 || span.End > visualLine.Length))
            return null;

        var results = new HashSet<string>(StringComparer.Ordinal);
        var logicalLines = automationName
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        foreach (var logicalLine in logicalLines)
        {
            if (logicalLine.Length != visualLine.Length) continue;
            for (int rotation = 0; rotation < visualLine.Length; rotation++)
            {
                if (!IsRotation(visualLine, logicalLine, rotation)) continue;
                var mapped = selectedSpans
                    .Select(span => new Utf16Span(
                        (span.Start - rotation + visualLine.Length)
                        % visualLine.Length,
                        span.Length))
                    .OrderBy(span => span.Start)
                    .ToArray();
                if (mapped.Any(span => span.End > logicalLine.Length)) continue;

                int start = mapped[0].Start;
                int end = mapped[0].End;
                bool contiguous = true;
                for (int index = 1; index < mapped.Length; index++)
                {
                    if (mapped[index].Start != end)
                    {
                        contiguous = false;
                        break;
                    }
                    end = mapped[index].End;
                }
                if (!contiguous) continue;

                var result = logicalLine[start..end];
                if (!string.IsNullOrWhiteSpace(result)) results.Add(result);
            }
        }

        return results.Count == 1 ? results.Single() : null;
    }

    private static bool IsRotation(
        string visualLine,
        string logicalLine,
        int rotation)
    {
        for (int index = 0; index < logicalLine.Length; index++)
        {
            if (logicalLine[index]
                != visualLine[(index + rotation) % visualLine.Length])
                return false;
        }
        return true;
    }

    private static bool ContainsArabicAndLatin(string text)
    {
        bool hasArabic = false;
        bool hasLatin = false;
        foreach (char character in text)
        {
            hasArabic |= character is >= '\u0600' and <= '\u06FF'
                or >= '\u0750' and <= '\u077F'
                or >= '\u08A0' and <= '\u08FF'
                or >= '\uFB50' and <= '\uFDFF'
                or >= '\uFE70' and <= '\uFEFF';
            hasLatin |= character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z';
        }
        return hasArabic && hasLatin;
    }

    /// <summary>
    /// Reads a non-empty text selection from the element under (<paramref name="x"/>,
    /// <paramref name="y"/>) — walking up a few levels for the TextPattern the way the feed's
    /// tweet text exposes it a level or two above the leaf under the cursor. Returns null when
    /// there's no selection there (an Explorer file row, a desktop icon, a bare button). Runs on
    /// the same worker thread as <see cref="ProbeSelectionViaUIA"/>; must not throw.
    /// </summary>
    private static (string Text, string? GestureText, bool RequireGestureText, Func<bool>? ValidateInput)? TryReadSelectionAtPoint(
        int x,
        int y,
        uint expectedProcessId,
        SelectionGesture gesture,
        bool deriveGestureText)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (element == null) return null;
            if ((uint)element.Current.ProcessId != expectedProcessId) return null;
            var walker = TreeWalker.RawViewWalker;
            for (int depth = 0; element != null && depth < TextPatternParentWalkDepth; depth++)
            {
                try
                {
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pat))
                    {
                        var ranges = ((TextPattern)pat).GetSelection();
                        if (ranges != null && ranges.Length > 0)
                        {
                            var combined = CombineSelectionRanges(ranges.Select(r => r.GetText(SelectionSnapshot.MaximumTextLength + 1)));
                            if (!string.IsNullOrEmpty(combined))
                            {
                                // 沿用旧版：光标点路径同样不强制 Chromium 手势文本（原因同上，
                                // 扩展桥未连接时手势重建失败会让浏览器划词全部 UntrustedText abort）。
                                bool requireGestureText = false;
                                string? gestureText = null;
                                return (combined, gestureText, requireGestureText,
                                    CreateInputValidation((TextPattern)pat, ranges, combined));
                            }
                        }
                    }
                }
                catch { /* per-level UIA failure — try the parent */ }

                try { element = walker.GetParent(element); }
                catch { break; }
            }
        }
        catch { /* FromPoint / UIA failure — no rescue */ }
        return null;
    }

    /// <summary>
    /// Reads the current selection via UI Automation. Returns null when no focused element,
    /// no TextPattern within the walk depth, no selection ranges, or any UIA failure. Runs on
    /// a worker thread because UIA calls can take hundreds of ms in apps where a11y is cold.
    /// </summary>
    private static SelectionProbe? CopyViaUIA(
        uint expectedProcessId, string? expectedRuntimeId, bool allowEmpty = false)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element == null) return null;
            if ((uint)element.Current.ProcessId != expectedProcessId) return null;
            if (!MatchesAutomationRuntimeId(element, expectedRuntimeId)) return null;
            string? focusedRuntimeId = TryReadAutomationRuntimeId(element);

            var walker = TreeWalker.RawViewWalker;
            for (int depth = 0; element != null && depth < TextPatternParentWalkDepth; depth++)
            {
                try
                {
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pat))
                    {
                        var tp = (TextPattern)pat;
                        var ranges = tp.GetSelection();
                        if (ranges != null && ranges.Length > 0)
                        {
                            // Range reads are bounded before their result reaches the coordinator. For
                            // discontiguous selections (rare — Ctrl-click in Excel-style
                            // grids) join with \n so the caller sees all of it.
                            var combined = CombineSelectionRanges(ranges.Select(r => r.GetText(SelectionSnapshot.MaximumTextLength + 1)));
                            if (allowEmpty || !string.IsNullOrEmpty(combined))
                                return new SelectionProbe(SelectionProbeOutcome.HasText, combined, null, focusedRuntimeId,
                                    CreateInputValidation(tp, ranges, combined));
                        }
                    }
                }
                catch { /* per-level UIA failure — try the parent */ }

                try { element = walker.GetParent(element); }
                catch { break; }
            }
        }
        catch { /* UIA failure */ }
        return null;
    }

    private static bool MatchesAutomationRuntimeId(
        AutomationElement element, string? expectedRuntimeId)
    {
        if (expectedRuntimeId == null) return true;
        return TryReadAutomationRuntimeId(element) == expectedRuntimeId;
    }

    private static string? TryReadAutomationRuntimeId(
        AutomationElement element)
    {
        try
        {
            int[] runtimeId = element.GetRuntimeId();
            return runtimeId.Length > 0
                ? string.Join(",", runtimeId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Keep the oversize signal for the normal rejection path without allocating an unbounded join.
    internal static string CombineSelectionRanges(IEnumerable<string> fragments)
    {
        var result = new System.Text.StringBuilder();
        int count = 0;
        foreach (var fragment in fragments)
        {
            if (++count > 256 || result.Length + fragment.Length + (result.Length > 0 ? 1 : 0) > SelectionSnapshot.MaximumTextLength)
                return new string('\0', SelectionSnapshot.MaximumTextLength + 1);
            if (fragment.Length == 0) continue;
            if (result.Length > 0) result.Append('\n');
            result.Append(fragment);
        }
        return result.ToString();
    }

    /// <summary>
    /// 合成复制兜底：对 UI Automation 读不到选区的应用（Java Swing 等），注入 Ctrl+Insert 复制选区。
    /// 整段受精确前台目标校验约束，快照→注入→读回→恢复到原剪贴板，杜绝污染用户剪贴板。
    /// 剪贴板为空（没有可保护的内容）时仍允许注入，成功后把写入内容清空以恢复“空”状态。
    /// </summary>
    private static async Task<string?> TrySyntheticCopyAsync(SelectionOperation operation)
    {
        // 剪贴板可能被另一进程瞬时锁定（其正在复制/粘贴，持有 OpenClipboard 互斥，通常几十毫秒
        // 内释放）。此时 OLE 的 Clipboard.GetDataObject() 仍可能成功（走 OleGetClipboard，不占用
        // Win32 锁），而快照里的 Win32 OpenClipboard 备份会失败。若一次失败即放弃，会把一次
        // 瞬时锁定误判成"剪贴板被占用"，导致合成兜底整段跳过、划词失败。这里短延迟重试几次。
        ClipboardTransaction.ClipboardSnapshot? snapshot = null;
        bool clipboardEmpty = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(60);
            snapshot = await Application.Current.Dispatcher.InvokeAsync(
                ClipboardTransaction.SnapshotClipboard);
            if (snapshot != null) break;
            // 剪贴板为空时无需保护：仍允许合成注入（划词的兜底不因剪贴板为空而失效）。
            clipboardEmpty = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try { return Clipboard.GetDataObject() == null; }
                catch { return false; }
            });
            if (clipboardEmpty) break;
        }
        if (snapshot == null && !clipboardEmpty)
        {
            // 持续非空却无法快照（被其他进程长时间锁定等）：放弃，避免污染用户无法恢复的内容。
            SnapActions.Helpers.Log.Info("Synthetic fallback skipped: clipboard occupied but snapshot failed");
            return null;
        }
        SnapActions.Helpers.Log.Info(
            "Synthetic copy fallback engaged (UIA produced no text); app=" +
            ForegroundApp.GetActiveProcessName());
        ClipboardTransaction.ClipboardObservation? acceptedWrite = null;
        try
        {
            var before = ClipboardTransaction.ObserveClipboard();
            if (snapshot != null && before != snapshot.Observation)
            {
                SnapActions.Helpers.Log.Info("Synthetic fallback aborted: clipboard changed between snapshot and copy");
                return null;
            }
            if (!await operation.CanInjectInputAsync())
            {
                SnapActions.Helpers.Log.Info("Synthetic fallback aborted: input rejected before copy");
                return null;
            }

            var (text, clipboardObservation) = await TryOneSyntheticCopyAsync(operation, before);
            if (clipboardObservation is { } a1) acceptedWrite = a1;

            return text;
        }
        finally
        {
            // RestoreClipboardIfUnchanged 内部会 Dispose snapshot；只有未入账写时我们手动释放。
            // 不再要求 operation.IsCurrent：操作可能已被新的选区取代，但这次注入造成的剪贴板副作用
            // 仍必须撤销。还原只在锁内基线仍与本次写入完全一致时才生效，不会覆盖第三方内容。
            if (acceptedWrite is { } ak)
            {
                if (snapshot != null)
                {
                    bool restored = false;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                        restored = ClipboardTransaction.RestoreClipboardIfUnchanged(snapshot, ak));
                    SnapActions.Helpers.Log.Info(
                        $"Clipboard restore finished: ok={restored}, acceptedSeq={ak.Sequence}");
                }
                else
                {
                    // 原剪贴板为空：仅当剪贴板仍是本次写入的内容时清空，把剪贴板恢复为“空”。
                    // 剪贴板锁竞争会让观察瞬时不可用（返回全 0），与快照路径一样做短延迟重试；
                    // 仍失败则把残留记进台账，下一次快照按“空”处理它，泄漏不会继承下去。
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt > 0) Thread.Sleep(20);
                            try
                            {
                                var now = ClipboardTransaction.ObserveClipboard();
                                if (now == ak)
                                {
                                    Clipboard.Clear();
                                    ClipboardTransaction.ClearUnrestoredWrite();
                                    SnapActions.Helpers.Log.Info("Clipboard restore finished: ok=True, empty baseline cleared");
                                    return;
                                }
                                if (now.Sequence != 0 && now.OwnerWindow != IntPtr.Zero)
                                {
                                    // 已被第三方内容取代：我们的写入不复存在，也没有可清的东西。
                                    ClipboardTransaction.ClearUnrestoredWrite();
                                    SnapActions.Helpers.Log.Info("Clipboard restore finished: ok=True (write already replaced)");
                                    return;
                                }
                            }
                            catch { /* 清空失败不致命，任务已读回文本；下一轮重试或记台账 */ }
                        }
                        ClipboardTransaction.NoteUnrestoredWrite(ClipboardTransaction.ObserveClipboard());
                        SnapActions.Helpers.Log.Info("Clipboard restore finished: ok=False (empty baseline not cleared), residue noted");
                    });
                }
            }
            else
            {
                snapshot?.Dispose();
            }
        }
    }

    private static async Task<(string? Text, ClipboardTransaction.ClipboardObservation? AcceptedWrite)> TryOneSyntheticCopyAsync(
        SelectionOperation operation, ClipboardTransaction.ClipboardObservation before)
    {
        var outcome = await InputExecutor.TrySimulateCopyAsync(operation, before);
        // Partial：按键序列只送出了一部分（例如 Ctrl 的抬起失败），复制可能已经发生，仍按"可能
        // 已投递"观察并清理；Rejected（一个按键都没送出）时剪贴板不可能因本次注入变化，直接返回。
        bool delivered = outcome.Status != InputExecutor.InputInjectionStatus.Rejected;
        string? text = null;
        ClipboardTransaction.ClipboardObservation? acceptedWrite = null;
        if (!delivered)
        {
            SnapActions.Helpers.Log.Info(
                "Synthetic copy (Ctrl+Insert): status=Rejected, nothing delivered");
            return (null, null);
        }
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(10);
            var after = ClipboardTransaction.ObserveClipboard();
            if (after.Sequence == before.Sequence)
            {
                if (i < 29) continue;
                break; // 目标未写剪贴板（无选区或拒绝）
            }
            if (ClipboardTransaction.IsClipboardOwnedByProcess(after, operation.Target.ProcessId))
            {
                text = await ClipboardTransaction.ReadCurrentClipboardTextAsync();
                var afterRead = ClipboardTransaction.ObserveClipboard();
                // ★ 必须同时满足两件事才认账：
                //   1. 读回了非空文本（Ctrl+Insert 复制出来的一定是文本，图片/文件就不是我们的产物）；
                //   2. 读取完成后属主仍是目标进程（读取期间没被第三方改写）。
                // 只要"读不到文本"，就放弃清理 —— 否则会把截图工具/画图/游戏写进剪贴板的内容
                if (!string.IsNullOrEmpty(text)
                    && ClipboardTransaction.IsClipboardOwnedByProcess(afterRead, operation.Target.ProcessId))
                {
                    acceptedWrite = afterRead;
                }
                else
                {
                    text = null;               // 剪贴板里不是文本：不是 Ctrl+Insert 的产物，绝不清理
                }
                break;
            }
            // 序列号已变，但这次观察不可用（属主为空/两次采样不一致）：目标进程发布剪贴板时会
            // 先 EmptyClipboard 再写入，属主短暂为空；多格式发布过程中采样也常不稳定。这些都是瞬态，
            // 就此放弃会同时丢掉读取与清理，稍后完成的写入就留在用户剪贴板上。继续观察：稍后稳定
            // 由目标进程持有即入账并清理；始终不可用则耗尽后放弃，第三方内容不受影响。
            if (i < 29) continue;
            SnapActions.Helpers.Log.Info(
                "Synthetic copy gave up observing: clipboard changed but never seen owned by target " +
                $"(seq={after.Sequence}, ownerPid={after.OwnerProcessId}, targetPid={operation.Target.ProcessId})");
        }
        SnapActions.Helpers.Log.Info(
            $"Synthetic copy (Ctrl+Insert): status={outcome.Status}, " +
            $"text={(text == null ? "null" : text.Length + " chars")}, restorable={acceptedWrite != null}");
        return (text, acceptedWrite);
    }

}
