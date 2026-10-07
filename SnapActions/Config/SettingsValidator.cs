using System.Text.RegularExpressions;

namespace SnapActions.Config;

internal static class SettingsValidator
{
    internal static void Normalize(AppSettings s)
    {
        s.ToolbarDismissTimeout = s.ToolbarDismissTimeout == 0 ? 0 : Math.Clamp(s.ToolbarDismissTimeout, 1000, 60000);
        s.ToolbarShowDelay = Math.Clamp(s.ToolbarShowDelay, 0, 5000);
        s.MultiClickDelay = Math.Clamp(s.MultiClickDelay, 0, 1000);
        s.LongPressDuration = Math.Clamp(s.LongPressDuration, 200, 3000);
        s.MaxInlineContextActions = Math.Clamp(s.MaxInlineContextActions, 0, 10);
        s.ExcludedApps = Clean(s.ExcludedApps ?? new AppSettings().ExcludedApps);
        s.DisabledActionIds = Clean(s.DisabledActionIds);
        s.PinnedActionIds = Clean(s.PinnedActionIds);
        s.Theme = s.Theme is "light" or "dark" ? s.Theme : "system";
        s.TextRecipes = s.TextRecipes.Where(r => r != null && ValidId(r.Id) && !string.IsNullOrWhiteSpace(r.Name))
            .DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var recipe in s.TextRecipes) recipe.Steps = (recipe.Steps ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        // s.SearchLanguage ??= "";
        s.ExcludeRegex ??= "";
        s.TranslationSourceLanguage = LanguageOptions.IsSupported(s.TranslationSourceLanguage) ? s.TranslationSourceLanguage : "";
        s.TranslationTargetLanguage = LanguageOptions.IsSupported(s.TranslationTargetLanguage) ? s.TranslationTargetLanguage : "en";
        s.DictionaryLanguage = LanguageOptions.IsSupported(s.DictionaryLanguage) ? s.DictionaryLanguage : "en";
        s.TargetCurrency = (s.TargetCurrency ?? "USD").ToUpperInvariant();
        if (!Helpers.MoneyValue.Currencies.ContainsKey(s.TargetCurrency)) s.TargetCurrency = "USD";
        if (!Enum.IsDefined(s.PasteModeTrigger)) s.PasteModeTrigger = PasteModeTrigger.LongPress;
        s.ExcludedAppsDefaultsVersion = Math.Max(0, s.ExcludedAppsDefaultsVersion);
        s.SearchEngines = (s.SearchEngines ?? AppSettings.GetDefaultEngines())
            .Where(e => ValidId(e.Id) && !string.IsNullOrWhiteSpace(e.Name) && ValidTemplate(e.UrlTemplate))
            .DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var e in s.SearchEngines)
            if (!Enum.IsDefined(e.LangMode)) e.LangMode = LangMode.Url;
        s.UserActions = (s.UserActions ?? [])
            .Where(a => a != null && ValidId(a.Id) && !string.IsNullOrWhiteSpace(a.Name)
                && (!string.IsNullOrWhiteSpace(a.Code) || !string.IsNullOrWhiteSpace(a.ScriptFile)
                    || ValidTemplate(a.UrlTemplate)))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var a in s.UserActions)
        {
            a.JsonField ??= "";
            a.AppliesToType ??= "";
            a.Code ??= "";
            a.ScriptFile ??= "";
            a.ContextRegex ??= "";
            if (a.Code.Length > 64 * 1024) a.Code = a.Code[..(64 * 1024)];
            if (a.ContextRegex.Length > 2048) a.ContextRegex = a.ContextRegex[..2048];
            if (!Enum.IsDefined(a.Kind)) a.Kind = UserActionKind.OpenUrl;
        }
        // 自定义翻译引擎：与脚本动作同一套有效性规则（必须能读到脚本），默认引擎不存在或未启用时清空
        // ——清空即回退百度翻译，不会留下指向空气的选中项。
        s.TranslationEngines = (s.TranslationEngines ?? [])
            .Where(a => a != null && ValidId(a.Id) && !string.IsNullOrWhiteSpace(a.Name)
                && (!string.IsNullOrWhiteSpace(a.Code) || !string.IsNullOrWhiteSpace(a.ScriptFile)))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var engine in s.TranslationEngines)
        {
            engine.Code ??= "";
            engine.ScriptFile ??= "";
            if (engine.Code.Length > 64 * 1024) engine.Code = engine.Code[..(64 * 1024)];
        }
        s.SelectedTranslationEngineId ??= "";
        if (!s.TranslationEngines.Any(e => e.Id == s.SelectedTranslationEngineId && e.Enabled))
            s.SelectedTranslationEngineId = "";
        var profiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in s.AppHiddenActions ?? [])
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            var key = pair.Key.Trim();
            profiles.TryGetValue(key, out var prior);
            profiles[key] = Clean((prior ?? []).Concat(pair.Value ?? []));
        }
        s.AppHiddenActions = profiles;
    }

    private static List<string> Clean(IEnumerable<string>? values) => (values ?? [])
        .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 160 && !id.Any(char.IsControl);
    internal static bool ValidTemplate(string? template) => template != null && template.Length <= 8192 &&
        Uri.TryCreate(template.Replace("{0}", "sample").Replace("{1}", "en"), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";
}
