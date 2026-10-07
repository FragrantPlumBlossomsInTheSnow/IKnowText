using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SnapActions.Actions.ContextActions;
using SnapActions.Actions.SearchActions;
using SnapActions.Actions.TransformActions;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using SnapActions.Detection;

namespace SnapActions.Actions;

public partial class ActionRegistry
{
    /// <summary>
    ///     The fixed (non-search) action IDs — invariant across the process lifetime, so we
    ///     instantiate the registry once instead of rebuilding it on every call.
    /// </summary>
    private static readonly Lazy<IReadOnlySet<string>> _fixedActionIds = new(() =>
    {
        var registry = new ActionRegistry();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in registry._allActions) ids.Add(a.Id);
        return ids;
    });

    private readonly List<IAction> _allActions;

    public ActionRegistry()
    {
        _allActions =
        [
            // Context actions
            new OpenUrlAction(),
            new SendEmailAction(),
            new OpenFilePathAction(),
            new OpenContainingFolderAction(),
            new PreviewColorAction(),
            new ConvertColorAction(),
            new FormatJsonAction(),
            new MinifyJsonAction(),
            new FormatXmlAction(),
            new StripTagsAction(),
            new CalculateAction(),
            new IpLookupAction(),
            new DecodeBase64Action(),
            new DecodeJwtAction(),
            new GenerateUuidAction(),
            new ConvertTimezoneAction(),
            new UnitConvertAction(),
            new TranslateAction(),
            // new DictionaryAction(),
            new CurrencyConverterAction(),
            new CleanLinkAction(),

            // 转换操作
            new CaseTransformAction("upper", "全大写", text => text.ToUpperInvariant()),
            new CaseTransformAction("lower", "全小写", text => text.ToLowerInvariant()),
            new CaseTransformAction("title", "标题式", ToTitleCase),
            new CaseTransformAction("pascal", "大驼峰式", ToPascalCase),
            new CaseTransformAction("camel", "小驼峰式", ToCamelCase),
            new CaseTransformAction("snake", "下_划_线", ToSnakeCase),
            new CaseTransformAction("kebab", "短-横-线", ToKebabCase),
            new CaseTransformAction("reverse", "反转", ReverseGraphemes),

            new WhitespaceAction("trim", "去除首尾空格", text => text.Trim()),
            new WhitespaceAction("remove_extra_spaces", "去除多余空格", text => MyRegex1().Replace(text, " ")),
            new WhitespaceAction("sort_lines", "排序行", text => SortLines(text, false)),
            new WhitespaceAction("dedup_lines", "去除重复项", text => SortLines(text, true)),
            new WhitespaceAction("remove_linebreaks", "去除换行符", text => MyRegex().Replace(text, " ").Trim()),

            // 包围操作
            new WrapAction("wrap_backticks", "` `", "`", "`"),
            new WrapAction("wrap_single_quotes", "' '", "'", "'"),
            new WrapAction("wrap_quotes", "\" \"", "\"", "\""),
            new WrapAction("wrap_parens", "( )", "(", ")"),
            new WrapAction("wrap_brackets", "[ ]", "[", "]"),
            new WrapAction("wrap_braces", "{ }", "{", "}"),
            new WrapAction("wrap_chinese_quotes", "「」", "「", "」"),

            // 编码操作
            new EncodingAction("url_encode", "URL编码", Uri.EscapeDataString),
            new EncodingAction("url_decode", "URL解码", Uri.UnescapeDataString),
            
            new EncodingAction("base64_encode", "Base64编码",
                text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))),
            
            new EncodingAction("base64_decode", "Base64解码",
                text => new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text))),
            
            new EncodingAction("html_encode", "HTML编码",
                text => WebUtility.HtmlEncode(text)),
            
            new EncodingAction("html_decode", "HTML解码",
                text => WebUtility.HtmlDecode(text)),

            // Hex / ROT13
            new EncodingAction("hex_encode", "Hex编码",
                text => Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant()),
            
            new EncodingAction("hex_decode", "Hex解码",
                text => new UTF8Encoding(false, true).GetString(Convert.FromHexString(text.Trim()))),
            
            new EncodingAction("rot13", "ROT13", Rot13),

            // 哈希操作
            new EncodingAction("md5", "MD5", text => Hash(MD5.HashData, text)),
            new EncodingAction("sha1", "SHA-1", text => Hash(SHA1.HashData, text)),
            new EncodingAction("sha256", "SHA-256", text => Hash(SHA256.HashData, text)),
            new EncodingAction("sha512", "SHA-512", text => Hash(SHA512.HashData, text))
        ];
    }

    private static string Rot13(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c is >= 'a' and <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
            else if (c is >= 'A' and <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
            else sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    ///     All action IDs known to the registry, including the generated `search_
    ///     <engine.Id>
    ///         ` ones.
    ///         Used by SettingsManager.PruneStaleActionIds to drop orphan entries on Load.
    /// </summary>
    public static IReadOnlySet<string> GetAllKnownActionIds(IEnumerable<SearchEngine> engines,
        IEnumerable<UserAction>? userActions = null,
        IEnumerable<TextRecipeDefinition>? recipes = null)
    {
        // Reuse the cached fixed-ID set and just merge in the per-call search engine IDs. Previously
        // every call constructed a fresh ActionRegistry — fine for the single Load-time caller but
        // wasteful if anything else starts to use this API.
        var ids = new HashSet<string>(_fixedActionIds.Value, StringComparer.Ordinal);
        foreach (var e in engines) ids.Add($"search_{e.Id}");
        foreach (var u in userActions ?? SettingsManager.Current.UserActions) ids.Add($"user_{u.Id}");
        foreach (var recipe in recipes ?? SettingsManager.Current.TextRecipes) ids.Add($"recipe_{recipe.Id}");
        return ids;
    }

    public List<ActionGroup> GetActions(string text, TextAnalysis analysis, string? appName = null)
    {
        var s = SettingsManager.Current;
        var groups = new List<ActionGroup>();
        // Global disabled set, unioned with any per-app hidden actions for the foreground app.
        var disabled = new HashSet<string>(s.DisabledActionIds, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(appName))
            foreach (var (app, ids) in s.AppHiddenActions)
                if (app.Equals(appName, StringComparison.OrdinalIgnoreCase))
                    foreach (var id in ids)
                        disabled.Add(id);
        var applicable = _allActions
            .Where(a => a.CanExecute(text, analysis) && !disabled.Contains(a.Id))
            .ToList();
        foreach (var recipe in s.TextRecipes.Where(r => r.Enabled))
        {
            var action = new TextRecipeAction(recipe, PureTextOperations());
            if (!disabled.Contains(action.Id) && action.CanExecute(text, analysis)) applicable.Add(action);
        }

        // 翻译动作是 Context 类别但独立成组（工具栏翻译按钮的显示开关）；从上下文组剔除，
        // 避免 translate 同时在 Context/Translate 两组出现导致重复渲染。
        var contextActions =
            applicable.Where(a => a.Category == ActionCategory.Context && a.Id != "translate").ToList();

        // 自定义操作：脚本动作（Code 内嵌或 ScriptFile 独立文件）按 JS 脚本（Transform），否则按 URL 模板（Context）。
        if (s.EnableCustomActions)
            foreach (var ua in s.UserActions.Where(u => u.Enabled))
            {
                IAction action = IsScriptAction(ua)
                    ? new UserScriptAction(ua)
                    : new UserRecipeAction(ua);
                if (action.CanExecute(text, analysis) && !disabled.Contains(action.Id))
                {
                    if (action.Category == ActionCategory.Context)
                    {
                        contextActions.Add(action);
                    }
                    else
                    {
                        applicable.Add(action);
                        // 配了「上下文触发」正则的 JS 脚本动作：命中选区时同时作为上下文动作内联显示
                        //（ContextSeparator 后那一排），但仍留在 Transform 组里，转换子菜单照旧可用。
                        if (action is UserScriptAction script && script.IsContextTriggered(text))
                            contextActions.Add(action);
                    }
                }
            }

        if (contextActions.Count > 0)
            groups.Add(new ActionGroup("Context", contextActions));
        if (s.ShowPasteActions)
        {
            var list = applicable.Where(a => a.Category == ActionCategory.Paste).ToList();
            if (list.Count > 0) groups.Add(new ActionGroup("Paste", list));
        }

        if (s.ShowTranslateActions)
        {
            // 仅翻译动作进入 Translate 组；不可用 Transform 类别过滤，否则会把所有文本转换
            // 动作重复塞进 Translate 组，造成工具栏同一动作出现两次。
            var list = applicable.Where(a => a.Id == "translate").ToList();
            if (list.Count > 0) groups.Add(new ActionGroup("Translate", list));
        }

        if (s.ShowTransformActions)
        {
            var list = applicable.Where(a => a.Category == ActionCategory.Transform).ToList();
            if (list.Count > 0) groups.Add(new ActionGroup("Transform", list));
        }

        if (s.ShowEncodeActions)
        {
            var list = applicable.Where(a => a.Category == ActionCategory.Encode).ToList();
            if (list.Count > 0) groups.Add(new ActionGroup("Encode", list));
        }

        if (s.ShowSearchActions && !string.IsNullOrEmpty(text.Trim()))
        {
            var searchActions = s.SearchEngines
                .Where(e => e.Enabled && !disabled.Contains($"search_{e.Id}"))
                .Select(e => (IAction)new WebSearchAction(
                    e.Id, e.Name, e.UrlTemplate, langMode: e.LangMode))
                .ToList();
            if (searchActions.Count > 0)
                groups.Add(new ActionGroup("Search",  searchActions));
        }

        return groups;
    }

    /// <summary>
    ///     图钉在选择和类别菜单偏好中保持顺序。
    ///     不适用的引脚仍然可见；工具栏解释了它们无法运行的原因。
    /// </summary>
    internal List<IAction> GetPinnedActions(string? appName = null)
    {
        var settings = SettingsManager.Current;
        var actions = Enum.GetValues<ActionCategory>().SelectMany(GetAllActionsForCategory).ToDictionary(a => a.Id);
        var appHidden = settings.AppHiddenActions
            .Where(p => p.Key.Equals(appName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value).ToHashSet(StringComparer.Ordinal);
        return settings.PinnedActionIds.Distinct(StringComparer.Ordinal)
            .Where(actions.ContainsKey).Select(id => actions[id])
            .Where(a => !ToolbarPreferences.IsHidden(settings, a) && !appHidden.Contains(a.Id)).ToList();
    }

    /// <summary>
    ///     All fixed (non-search) actions as (id, name, category) — for the per-app profile
    ///     editor in Settings, which lets the user choose actions to hide by name.
    /// </summary>
    public IEnumerable<(string Id, string Name, ActionCategory Category)> AllActionDescriptors()
    {
        return Enum.GetValues<ActionCategory>().SelectMany(GetAllActionsForCategory)
            .Select(a => (a.Id, a.Name, a.Category));
    }

    public IReadOnlyDictionary<string, IAction> PureTextOperations()
    {
        return _allActions
            .Where(a => a.IsPreviewSafe && a.Category is ActionCategory.Transform or ActionCategory.Encode)
            .ToDictionary(a => a.Id);
    }

    /// <summary>Get all actions for a category (including disabled ones) for the edit mode UI.</summary>
    public List<IAction> GetAllActionsForCategory(ActionCategory category)
    {
        if (category == ActionCategory.Search)
            // Search actions are built from settings, not from _allActions
            return SettingsManager.Current.SearchEngines
                .Select(e => (IAction)new WebSearchAction(
                    e.Id, e.Name, e.UrlTemplate, langMode: e.LangMode))
                .ToList();
        var actions = _allActions.Where(a => a.Category == category).ToList();
        var userActions = SettingsManager.Current.EnableCustomActions
            ? SettingsManager.Current.UserActions
            : [];
        if (category == ActionCategory.Transform)
            actions.AddRange(
                SettingsManager.Current.TextRecipes.Select(r => new TextRecipeAction(r, PureTextOperations())));
        if (category == ActionCategory.Context)
            actions.AddRange(userActions.Where(a => !IsScriptAction(a)).Select(a => new UserRecipeAction(a)));
        if (category == ActionCategory.Transform)
            actions.AddRange(userActions.Where(a => IsScriptAction(a)).Select(a => new UserScriptAction(a)));
        return actions;
    }

    /// <summary>脚本动作判定：Code 内嵌或 ScriptFile 独立文件任一非空即为脚本动作。</summary>
    private static bool IsScriptAction(UserAction ua)
    {
        return !string.IsNullOrWhiteSpace(ua.Code) || !string.IsNullOrWhiteSpace(ua.ScriptFile);
    }

    // Text transformation helpers
    private static string ToTitleCase(string text)
    {
        // InvariantCulture, not CurrentCulture — Turkish (and similar) locales would otherwise
        // turn "Hello" into "Helloİ" via the dotted-I rule, which is surprising for English text.
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            text.ToLowerInvariant());
    }

    // All four helpers use the *Invariant case ops, matching ToTitleCase above. The parameterless
    // ToLower()/char.ToUpper(char) overloads are CurrentCulture-sensitive and corrupt identifiers
    // on Turkish-family locales (the dotted/dotless-I rule), e.g. snake_case("HELLO_INDIA") would
    // otherwise yield "hello_ındıa".
    private static string ToCamelCase(string text)
    {
        var words = SplitWords(text);
        if (words.Length == 0) return text;
        return words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w =>
            char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static string ToPascalCase(string text)
    {
        var words = SplitWords(text);
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static string ToSnakeCase(string text)
    {
        return string.Join('_', SplitWords(text).Select(w => w.ToLowerInvariant()));
    }

    private static string ToKebabCase(string text)
    {
        return string.Join('-', SplitWords(text).Select(w => w.ToLowerInvariant()));
    }

    private static string ReverseGraphemes(string text)
    {
        // Iterate Unicode text elements so emoji and combining marks survive
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var stack = new Stack<string>();
        while (enumerator.MoveNext())
            stack.Push((string)enumerator.Current);
        return string.Concat(stack);
    }

    private static string Hash(Func<byte[], byte[]> hashFn, string text)
    {
        var bytes = hashFn(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string SortLines(string text, bool distinct)
    {
        // Detect line ending: keep \r\n if input uses it, else \n
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        IEnumerable<string> seq = lines.OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
        // Match the sort comparer so "Hello" and "hello" dedupe to one entry.
        if (distinct) seq = seq.Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(nl, seq);
    }

    private static string[] SplitWords(string text)
    {
        // Split on spaces, underscores, hyphens, dots, and camelCase boundaries.
        // Two camel-case boundaries are recognized:
        //   1. upper-after-lower      ("helloWorld" → "hello" | "World")
        //   2. upper-after-upper but followed by lower ("XMLHttpRequest" → "XML" | "Http" | "Request")
        // Without rule 2, "XMLHttpRequest" would split as "XMLHttp" + "Request".
        var result = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ' ' || c == '_' || c == '-' || c == '.')
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (i > 0 && char.IsUpper(c) &&
                     (char.IsLower(text[i - 1]) ||
                      (i + 1 < text.Length && char.IsLower(text[i + 1]))))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                current.Append(c);
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result.Where(w => w.Length > 0).ToArray();
    }

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex MyRegex1();
}

public record ActionGroup(string Name, List<IAction> Actions);