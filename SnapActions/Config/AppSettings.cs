namespace SnapActions.Config;

/// <summary>
/// What gesture summons paste mode (the menu that appears with no selection, offering
/// Paste / Paste-as-transform / Paste-as-encoded).
/// JSON values are kept as the lowercase short string ("longpress", "doubleclick", "off")
/// so existing settings.json files remain readable.
/// </summary>
public enum PasteModeTrigger
{
    /// <summary>Hold left mouse button for <see cref="AppSettings.LongPressDuration"/> ms (default).</summary>
    LongPress,
    /// <summary>Double-click on an empty editable input.</summary>
    DoubleClick,
    /// <summary>Don't summon paste mode automatically.</summary>
    Off,
}

/// <summary>
/// How a search engine consumes the language code.
/// JSON values are kept as the lowercase short string ("url", "query", "none") so existing
/// settings.json files remain readable.
/// </summary>
public enum LangMode
{
    /// <summary>Substitute {1} into the URL template (default).</summary>
    Url,
    /// <summary>Append `lang:xx` to the search text (Twitter/X uses this).</summary>
    Query,
    /// <summary>Engine doesn't accept a language hint.</summary>
    None,
}

/// <summary>
/// Reads/writes the lowercase string form of <see cref="PasteModeTrigger"/>. Unknown values
/// fall back to the current default (<see cref="PasteModeTrigger.LongPress"/>) so a manually-
/// edited settings.json with a typo doesn't silently disable the feature.
/// </summary>
public class PasteModeTriggerJsonConverter : System.Text.Json.Serialization.JsonConverter<PasteModeTrigger>
{
    public override PasteModeTrigger Read(ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? "";
        return s.ToLowerInvariant() switch
        {
            "off" => PasteModeTrigger.Off,
            "doubleclick" => PasteModeTrigger.DoubleClick,
            _ => PasteModeTrigger.LongPress,
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, PasteModeTrigger value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PasteModeTrigger.Off => "off",
            PasteModeTrigger.DoubleClick => "doubleclick",
            _ => "longpress",
        });
}

public class SearchEngine
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>
    /// URL template. {0} = URL-encoded query. {1} = language code (e.g. "en", "ar", "ja").
    /// Example: "https://www.google.com/search?q={0}&hl={1}"
    /// </summary>
    public string UrlTemplate { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }

    /// <summary>How this engine consumes the language code.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(LangModeJsonConverter))]
    public LangMode LangMode { get; set; } = LangMode.Url;

    /// <summary>Whether to apply the global SearchLanguage filter to this engine.</summary>
    public bool UseLanguageFilter { get; set; } = true;
}

/// <summary>
/// Reads the legacy string values ("url"/"query"/""/"none") and writes the lowercase form.
/// Keeps existing user settings.json files compatible across the enum migration.
/// </summary>
public class LangModeJsonConverter : System.Text.Json.Serialization.JsonConverter<LangMode>
{
    public override LangMode Read(ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? "";
        return s.ToLowerInvariant() switch
        {
            "url" => LangMode.Url,
            "query" => LangMode.Query,
            "" or "none" => LangMode.None,
            _ => LangMode.Url, // unknown values fall back to default
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, LangMode value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            LangMode.Url => "url",
            LangMode.Query => "query",
            _ => "none",
        });
}

/// <summary>What a user-defined recipe action does with the templated URL.</summary>
public enum UserActionKind
{
    /// <summary>Open the URL in the default browser.</summary>
    OpenUrl,
    /// <summary>GET the URL and show the response (optionally one JSON field) in a result popup.</summary>
    FetchText,
}

/// <summary>Reads/writes the lowercase string form so a hand-edited settings.json stays readable
/// and an unknown value falls back to the safe default (OpenUrl) instead of throwing on load.</summary>
public class UserActionKindJsonConverter : System.Text.Json.Serialization.JsonConverter<UserActionKind>
{
    public override UserActionKind Read(ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        (reader.GetString() ?? "").ToLowerInvariant() switch
        {
            "fetchtext" or "fetch" => UserActionKind.FetchText,
            _ => UserActionKind.OpenUrl,
        };

    public override void Write(System.Text.Json.Utf8JsonWriter writer, UserActionKind value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value == UserActionKind.FetchText ? "fetchtext" : "openurl");
}

/// <summary>
/// A user-defined "recipe" action — the same data-driven shape as <see cref="SearchEngine"/>,
/// generalized so any selected text can drive a templated URL. {0} in UrlTemplate is replaced with
/// the URL-encoded selection.
/// </summary>
public class UserAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>URL with {0} = URL-encoded selection. Example: https://api.github.com/users/{0}</summary>
    public string UrlTemplate { get; set; } = "";

    [System.Text.Json.Serialization.JsonConverter(typeof(UserActionKindJsonConverter))]
    public UserActionKind Kind { get; set; } = UserActionKind.OpenUrl;

    /// <summary>Empty = applies to any selection; otherwise a <c>TextType</c> name (e.g. "Url",
    /// "Email", "IpAddress") so the action only appears for that detected type.</summary>
    public string AppliesToType { get; set; } = "";

    /// <summary>FetchText only: optional dotted path into a JSON response (e.g. "name" or
    /// "data.title"); empty shows the raw response.</summary>
    public string JsonField { get; set; } = "";

    /// <summary>JS 脚本动作源码（可选）。非空时该动作作为文本转换脚本执行（选区文本 → JSAction(text)
    /// → 返回文本），Code 优先于 UrlTemplate；为空保持旧 URL 模板行为以兼容旧 settings.json。</summary>
    public string Code { get; set; } = "";

    /// <summary>脚本动作代码存储为数据目录 scripts\ 下的独立 .js 文件，此字段存文件名；
    /// 非空时优先读文件（避免多行代码/引号在 JSON 中转义出错），为空回退内嵌 Code（旧数据兼容）。</summary>
    public string ScriptFile { get; set; } = "";

    /// <summary>「上下文触发」正则（可选，仅 JS 脚本动作用）：非空且命中当前选区时，该脚本动作会像
    /// 内置的数学计算/格式化 JSON 一样内联出现在工具栏上下文区（ContextSeparator 后）；留空则只作为
    /// 文本转换动作。非法正则/回溯超时按「不命中」处理（见 ContextTriggerRegex）。</summary>
    public string ContextRegex { get; set; } = "";

    /// <summary>允许该脚本访问网络：勾选后沙箱注入 Translation 与 http，并在执行前触发在线查询同意门。
    /// 默认 false —— 未勾选的脚本与以前完全一致，拿不到任何网络能力（Translation/http 均不存在）。</summary>
    public bool AllowNetwork { get; set; } = false;

    public bool Enabled { get; set; } = true;
}

public class AppSettings
{
    public bool AutoStart { get; set; } = false;
    public bool Enabled { get; set; } = true;
    public bool SelectCopy { get; set; } = false;
    public bool HoverOpen { get; set; } = false;
    public int ToolbarDismissTimeout { get; set; } = 8000;
    /// <summary>Delay in ms before showing toolbar after selection (0 = instant).</summary>
    public int ToolbarShowDelay { get; set; } = 0;
    /// <summary>Delay in ms after double/triple click before firing (allows next click). 0 = instant.</summary>
    public int MultiClickDelay { get; set; } = 200;
    /// <summary>How long the user must hold the left button (ms) before paste mode appears.</summary>
    public int LongPressDuration { get; set; } = 500;
    /// <summary>
    /// Which gesture summons paste mode. Defaults to LongPress so existing installs keep
    /// their current behavior across the upgrade. Off disables paste mode entirely;
    /// DoubleClick replaces long-press with double-click on an empty editable input.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(PasteModeTriggerJsonConverter))]
    public PasteModeTrigger PasteModeTrigger { get; set; } = PasteModeTrigger.LongPress;
    public List<string> ExcludedApps { get; set; } = [];
    /// <summary>
    /// Tracks which "default ExcludedApps additions" generation this settings file has
    /// already absorbed. SettingsManager.MigrateExcludedAppsDefaults merges new entries
    /// forward on Load — users upgrading from an earlier version pick up newly-added
    /// exclusions automatically without overwriting their own customizations. Removing
    /// an entry sticks because we only ever *add*, and we only add once per generation.
    /// </summary>
    public int ExcludedAppsDefaultsVersion { get; set; } = 0;

    /// <summary>
    /// When true, after a toolbar action puts result text on the clipboard the previous
    /// clipboard contents are restored ~3 seconds later. Opt-in because the delay can race
    /// with a slow user paste — default off.
    /// </summary>
    public bool RestoreClipboardAfterAction { get; set; } = false;

    /// <summary>
    /// Whether the user has consented to the online-lookup actions (Translate / Dictionary /
    /// Currency) sending their selected text to third-party HTTPS APIs. Off by default — the first
    /// such action prompts once; the user can also toggle it in Settings.
    /// </summary>
    public bool AllowOnlineLookups { get; set; } = false;

    /// <summary>
    /// Whether mouse selection gestures automatically capture text and show the toolbar. Automatic
    /// capture is UI Automation-only and does not read or mutate the clipboard. Apps that do not
    /// expose selected text through UIA require the explicit Ctrl+C trigger instead.
    /// </summary>
    public bool CaptureOnMouseSelection { get; set; } = true;

    /// <summary>
    /// Opt-in: also show the toolbar when the user presses Ctrl+C (reads what they just copied —
    /// no synthetic keystroke, no clipboard clear, so it can't interfere with other apps). Off by
    /// default; the mouse-selection trigger is the primary path.
    /// </summary>
    public bool CaptureOnCtrlC { get; set; } = false;

    /// <summary>
    /// Allow the synthetic-copy chord (Ctrl+Insert) as a *fallback* after UI Automation
    /// has failed to expose the selection (Java Swing IDEs, some Chromium content), at the cost of
    /// a brief clipboard mutation and a SendInput keystroke. UIA stays the primary path either way.
    /// Defaults to true to match v2.4.x behavior (fallback always engaged); only when the user
    /// explicitly turns it off is capture UIA-only (never injects keys).
    /// </summary>
    public bool UseSyntheticKeys { get; set; } = true;
    public bool ShowPasteActions { get;  set; } = true;
    public bool ShowTranslateActions { get; set; } = true;
    public bool ShowTransformActions { get; set; } = true;
    public bool ShowEncodeActions { get; set; } = true;
    public bool ShowSurroundActions { get; set; } = true;
    public bool ShowSearchActions { get; set; } = true;

    /// <summary>Language code for search filtering (e.g. "en", "ar", "ja", ""). Empty = no filter.</summary>
    public string SearchLanguage { get; set; } = "";
    public string ExcludeRegex { get; set; } = "";
    public string TranslationSourceLanguage { get; set; } = "";
    /// <summary>When true, the translation source defaults to the Windows display language
    /// (used when no explicit source is chosen).</summary>
    public bool TranslationSourceFollowSystem { get; set; } = false;
    public string TranslationTargetLanguage { get; set; } = "en";
    /// <summary>When true, the translation target defaults to the Windows display language
    /// (used when no explicit target is chosen); otherwise <see cref="TranslationTargetLanguage"/>.</summary>
    public bool TranslationTargetFollowSystem { get; set; } = true;
    /// <summary>Target language used when the source language is the Windows display language
    /// (so the system language isn't translated back into itself). Independent of
    /// <see cref="TranslationTargetLanguage"/>.</summary>
    public string TranslationSystemTargetLanguage { get; set; } = "en";
    public string DictionaryLanguage { get; set; } = "en";

    /// <summary>DPAPI-protected, base64-encoded Baidu AppID + secret (encrypted at rest; never plaintext).</summary>
    public string BaiduCredentialsBlob { get; set; } = "";

    /// <summary>自定义翻译引擎：与「自定义 JS 脚本动作」同构的 JS 脚本（入口 Translate(text)，返回值即译文）。
    /// 内置「翻译」动作优先用它；未选中/未启用时回退百度翻译。</summary>
    public List<UserAction> TranslationEngines { get; set; } = [];

    /// <summary>默认使用的翻译引擎 Id；空 = 不使用自定义引擎（翻译回退百度）。</summary>
    public string SelectedTranslationEngineId { get; set; } = "";

    public List<SearchEngine> SearchEngines { get; set; } = GetDefaultEngines();

    /// <summary>Target currency for conversion (e.g. "USD", "EUR", "SAR")</summary>
    public string TargetCurrency { get; set; } = "USD";

    /// <summary>User-defined recipe actions (templated URL or JS script). See <see cref="UserAction"/>.</summary>
    public List<UserAction> UserActions { get; set; } = [];
    /// <summary>自定义操作开关：启用后 UserActions 注册为动作（URL 模板或 JS 脚本）。</summary>
    public bool EnableCustomActions { get; set; } = true;
    public List<TextRecipeDefinition> TextRecipes { get; set; } = [];
    public string Theme { get; set; } = "system";

    public List<string> DisabledActionIds { get; set; } = [];

    /// <summary>
    /// Per-app action overrides, keyed by process name (no .exe). Each value is the list of action
    /// IDs to HIDE when that app is in the foreground, so the toolbar can be tailored per app (e.g.
    /// hide Translate in your editor). Apps with no entry use the global lists.
    /// </summary>
    public Dictionary<string, List<string>> AppHiddenActions { get; set; } = new();

    /// <summary>动作ID固定在主工具栏上。</summary>
    public List<string> PinnedActionIds { get; set; } = [];

    /// <summary>How many context-action buttons to show inline on the toolbar (the rest stay in the dropdown).</summary>
    public int MaxInlineContextActions { get; set; } = 8;

    public static List<SearchEngine> GetDefaultEngines() =>
    [   
        new() { Id = "google", Name = "Google", IsBuiltIn = true,
            UrlTemplate = "https://www.google.com/search?q={0}" },
        new() { Id = "bing", Name = "Bing", IsBuiltIn = true,
            UrlTemplate = "https://www.bing.com/search?q={0}" },
        new() { Id = "bilibili", Name = "BiliBili", IsBuiltIn = true,
            UrlTemplate = "https://search.bilibili.com/all?keyword={0}" },
        new() { Id = "github", Name = "GitHub", IsBuiltIn = true,
            UrlTemplate = "https://github.com/search?q={0}" },
    ];
}
