using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SnapActions.Config;
using SnapActions.Core;
using SnapActions.Helpers;
using SnapActions.Services;

namespace SnapActions.UI;

/// <summary>
/// 翻译弹层逻辑（自原独立 TranslationPopup 窗口迁入）：翻译面板挂在 ToolbarWindow 的
/// TranslatePopup 上，纯 XAML 渲染百度翻译结果，不使用 WebView。
/// 目标语言解析规则（系统语言→指定输出、自动检测等）与旧版完全一致。
/// </summary>
public partial class ToolbarWindow
{
    private string _translateText = "";
    private CancellationTokenSource? _translateLifetime;
    private bool _translateBusy;
    // 替换原文的互斥门：翻译弹层打开时工具栏的 ActionGate 已被点翻译按钮占用，
    // 替换用独立门防止连点/重复注入（语义与 ResultPopup._applyGate 一致）。
    private readonly OperationActionGate _translateApplyGate = new();

    private void InitializeTranslatePopup()
    {
        TranslateSourceCombo.ItemsSource = new[]
        {
            new LanguageOption("", "检测语言"),
            new LanguageOption("system", "Windows显示语言"),
        }.Concat(LanguageOptions.All);
        TranslateTargetCombo.ItemsSource = LanguageOptions.All;
        Closed += (_, _) => CloseTranslatePopup();
    }

    /// <summary>工具栏翻译按钮/动作的入口：打开翻译弹层并立即发起翻译。</summary>
    internal void ShowTranslate(string text)
    {
        // 选中自定义翻译引擎时不套百度的字节/纯文本门槛 —— 能处理什么是引擎自己的事。
        var hasEngine = TranslationEngineService.HasSelectedEngine();
        if ((!hasEngine && !BaiduTranslator.CanTranslate(text)) || !ResultPopup.EnsureOnlineLookupConsent())
            return;
        text = TranslationTextHelper.NormalizeEnglishIdentifiers(text);
        if (string.IsNullOrEmpty(text)) return;

        ResultPopup.CloseCurrent();
        CloseTranslatePopup();

        _translateText = text.Trim();
        var s = SettingsManager.Current;
        var followSource = s.TranslationSourceFollowSystem;
        TranslateSourceCombo.SelectedValue = followSource ? "system" : s.TranslationSourceLanguage;
        // 目标：遵循 Windows 显示语言或所选目标；若所选文本是系统语言且目标同为系统语言，
        // 改用“系统语言源”的指定输出（系统语言 → 指定输出）。
        TranslateTargetCombo.SelectedValue = ResolveTarget(followSource ? "system" : s.TranslationSourceLanguage, _translateText);

        TranslatePopup.IsOpen = true;
        AlignTranslateDirection();
        Dispatcher.InvokeAsync(AlignTranslateDirection, DispatcherPriority.Loaded);
        // 翻译期间工具栏不自动收起，避免阅读结果时消失；外部点击/Esc/关闭按钮才收。
        _dismissTimer.Stop();
        _ = TranslateAsync();
    }

    internal void CloseTranslatePopup()
    {
        TranslatePopup.IsOpen = false;
        _translateBusy = false;
        _translateLifetime?.Cancel();
        _translateLifetime?.Dispose();
        _translateLifetime = null;
    }

    /// <summary>Windows 显示语言对应的目标语言（zh-CN / en / fr 等）。</summary>
    private static string SystemDisplayTarget()
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : "zh-CN";
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return LanguageOptions.All.FirstOrDefault(l => l.Code.Equals(two, StringComparison.OrdinalIgnoreCase))?.Code ??
               "en";
    }

    /// <summary>目标语言：系统语言或显式目标。源为系统语言且目标同为系统语言时，
    /// 用 <see cref="AppSettings.TranslationSystemTargetLanguage"/>（否则会原样回显）。</summary>
    private static string ResolveTarget(string rawFrom, string text)
    {
        var s = SettingsManager.Current;
        var tgt = s.TranslationTargetFollowSystem ? SystemDisplayTarget() : s.TranslationTargetLanguage;
        if (tgt.Equals(SystemDisplayTarget(), StringComparison.OrdinalIgnoreCase)
            && SourceIsSystemLanguage(rawFrom, text))
            return s.TranslationSystemTargetLanguage;
        return tgt;
    }

    private static bool SourceIsSystemLanguage(string rawFrom, string text)
    {
        if (rawFrom == "system") return true;
        if (rawFrom.Length > 0)
            return rawFrom.Equals(SystemDisplayTarget(), StringComparison.OrdinalIgnoreCase);
        return IsLikelySystemLanguage(text);
    }

    /// <summary>启发式：所选文本是否像 Windows 显示语言。仅中文可靠；其它系统语言回退 false。</summary>
    private static bool IsLikelySystemLanguage(string text)
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (!name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var c in text)
            if (c >= 0x4e00 && c <= 0x9fff)
                return true;
        return false;
    }

    private async Task TranslateAsync(bool persistSelection = false)
    {
        if (!TranslatePopup.IsOpen || _translateBusy) return;
        _translateBusy = true;
        _translateLifetime?.Cancel();
        _translateLifetime?.Dispose();
        _translateLifetime = new CancellationTokenSource();
        var token = _translateLifetime.Token;

        var settings = SettingsManager.Current;
        var rawFrom = TranslateSourceCombo.SelectedValue as string ?? "";
        var from = rawFrom == "system" ? SystemDisplayTarget() : rawFrom; // "" → auto-detect, sent as-is
        var to = TranslateTargetCombo.SelectedValue as string ?? "en";
        // 仅当用户手动改动语言或点重试时持久化；自动翻译只读不写。
        if (persistSelection)
        {
            PersistSelection(rawFrom, from, to);
            SettingsManager.Save();
        }

        // 避免把语言翻回自身（百度会原样回显）：系统语言源路径由 ResolveTarget 覆盖，
        // 含源为自动检测时按文本内容判定的情况。
        var requestTo = ResolveTarget(rawFrom, _translateText);
        if (from.Length > 0 && requestTo.Equals(from, StringComparison.OrdinalIgnoreCase))
            requestTo = settings.TranslationSystemTargetLanguage;

        // 选中的自定义翻译引擎优先：不再需要百度凭据，也不再套百度的字节上限。
        var engine = TranslationEngineService.SelectedEngine();
        if (engine != null)
        {
            ShowTranslateStatus("正在翻译…");
            TranslateResultBox.Text = "";
            try
            {
                Log.Info($"Custom translate engine '{engine.Name}': from={from} to={requestTo} len={_translateText.Length}");
                var translated = await TranslationEngineService.RunAsync(engine, _translateText, from, requestTo, token);
                if (!TranslatePopup.IsOpen || token.IsCancellationRequested) return;
                TranslateResultBox.Text = translated;
                TranslateStatusText.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                // 切换语言 / 关闭弹层 / 超时导致的取消：静默，交给下一次调用。
            }
            catch (Exception ex)
            {
                if (!TranslatePopup.IsOpen || token.IsCancellationRequested) return;
                ShowTranslateStatus(ex.Message);
            }
            finally
            {
                _translateBusy = false;
            }
            return;
        }

        var (appid, secret) = CredentialCrypto.DecryptBaidu(settings.BaiduCredentialsBlob);
        if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(secret))
        {
            TranslateResultBox.Text = "";
            ShowTranslateStatus("未配置百度翻译凭据，请在设置中配置。");
            _translateBusy = false;
            return;
        }

        ShowTranslateStatus("正在翻译…");
        TranslateResultBox.Text = "";
        try
        {
            Log.Info($"Baidu translate: from={from} to={requestTo} len={_translateText.Length}");
            var (result, error) =
                await BaiduTranslator.TranslateAsync(_translateText, from, requestTo, appid, secret, token);
            if (!TranslatePopup.IsOpen || token.IsCancellationRequested) return;
            if (error != null)
            {
                ShowTranslateStatus(error);
            }
            else
            {
                TranslateResultBox.Text = result;
                TranslateStatusText.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _translateBusy = false;
        }
    }

    private static void PersistSelection(string rawFrom, string from, string to)
    {
        var settings = SettingsManager.Current;
        settings.TranslationSourceFollowSystem = rawFrom == "system";
        if (rawFrom != "system") settings.TranslationSourceLanguage = from;
        if (rawFrom == "system")
        {
            settings.TranslationSystemTargetLanguage = to;
        }
        else
        {
            settings.TranslationTargetFollowSystem = false;
            settings.TranslationTargetLanguage = to;
        }
    }

    private void ShowTranslateStatus(string message)
    {
        TranslateStatusText.Text = message;
        TranslateStatusText.Visibility = Visibility.Visible;
    }

    private async void TranslateLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && TranslatePopup.IsOpen && !_translateBusy) await TranslateAsync(true);
    }

    private async void TranslateRetry_Click(object sender, RoutedEventArgs e)
    {
        await TranslateAsync(true);
    }

    private void TranslateClose_Click(object sender, RoutedEventArgs e)
    {
        CloseTranslatePopup();
        StartDismissTimer();
    }

    private void TranslateCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TranslateResultBox.Text)) return;
        try { Clipboard.SetText(TranslateResultBox.Text); } catch { /* 剪贴板被占用时静默失败 */ }
    }

    private async void TranslateWrite_Click(object sender, RoutedEventArgs e)
    {
        var resultText = TranslateResultBox.Text;
        if (string.IsNullOrEmpty(resultText))
        {
            Log.Warn("输入时翻译写入被拒绝：结果文本为空");
            return;
        }
        if (!_translateApplyGate.TryStart())
        {
            Log.Warn("翻译写在入口处被拒绝：申请门忙");
            return;
        }
        var context = Volatile.Read(ref _operationContext);
        if (context == null)
        {
            _translateApplyGate.AllowRetry();
            Log.Warn("翻译写入被拒绝：没有操作上下文");
            return;
        }
        Log.Info($"Translate write: applying {resultText.Length} chars to selection");

        // 按旧版 ReplaceSelectedAsync 的替换逻辑：不做选区可编辑判断，直接走受保护的
        // 写剪贴板 + 模拟粘贴链路；每次失败都回滚剪贴板并允许重试。
        var operation = context.Operation;
        TranslateCopyButton.IsEnabled = TranslateWriteButton.IsEnabled = false;
        try
        {
            // 1. 窗口级前置：目标仍是前台应用、选区仍有效、修饰键已释放（宽松校验，
            //    不要求可编辑字段聚焦/精确 UIA 输入目标，划词选区往往没有 runtime id）。
            if (!await InputExecutor.PreparePasteForReplaceAsync(operation))
            {
                ShowTranslateStatus("焦点已移动或按键未释放，无法替换。");
                return;
            }

            // 2. 快照剪贴板，失败的粘贴可以回滚。
            var previous = ClipboardTransaction.SnapshotClipboard();
            if (previous == null || !ClipboardTransaction.CanStartClipboardWrite(
                    previous, ClipboardTransaction.ObserveClipboard()))
            {
                previous?.Dispose();
                Log.Warn("Translate write rejected: clipboard snapshot failed");
                ShowTranslateStatus("剪贴板正忙，无法替换，请重试。");
                return;
            }

            // 3. 把翻译结果写入剪贴板作为本次操作的输出。requireExactTarget: false ——
            //    窗口级校验已在 PreparePasteForReplaceAsync 完成；Rider 等无 AutomationRuntimeId
            //    的目标走 TryRunWithExactInputTargetAsync 会被 HasSufficientInputIdentity 统一拒绝。
            var written = await ClipboardTransaction.TrySetClipboardTextForOperationAsync(
                operation, previous, TranslateResultBox.Text, requireExactTarget: false);
            if (written == null)
            {
                previous.Dispose();
                Log.Warn("Translate write rejected: clipboard write failed");
                ShowTranslateStatus("剪贴板写入失败，请重试。");
                return;
            }

            // 4. Shift+Insert 粘贴覆盖仍选中的文本（窗口级一致校验注入，同合成键兜底模式）。
            var pasteOutcome = await InputExecutor.TrySimulatePasteForReplaceAsync(operation, written);
            if (pasteOutcome.Status != InputExecutor.InputInjectionStatus.Succeeded)
            {
                // 尽量把用户原来的剪贴板放回去；RestoreClipboardIfUnchanged 自行释放快照。
                try { ClipboardTransaction.RestoreClipboardIfUnchanged(previous, written.Value); }
                catch { }
                ShowTranslateStatus("替换失败，请检查目标后重试。");
                return;
            }

            previous.Dispose();
            Log.Info("Translate write: replace succeeded");
            CloseTranslatePopup();
            HideToolbar();
        }
        finally
        {
            // 失败时恢复按钮供用户重试（成功路径已隐藏工具栏，这里无副作用）。
            _translateApplyGate.AllowRetry();   
            TranslateCopyButton.IsEnabled = true;
            TranslateWriteButton.IsEnabled = true;
        }
    }

    
}
