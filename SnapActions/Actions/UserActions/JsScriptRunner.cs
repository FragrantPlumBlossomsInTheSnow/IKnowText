using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Jint.Runtime;
using SnapActions.Config;
using SnapActions.Services;

namespace SnapActions.Actions.UserActions;

/// <summary>
/// 安全地执行用户 JS 脚本动作：约定脚本定义全局函数 JSAction(text)，传入选区文本，返回结果。
/// 返回值不限于字符串：字符串直接使用，数组/普通对象转成 JSON，其余走 String()（见 __snapResultText）——
/// 不能直接用 Jint 的 CLR ToString，否则 JsArray 会渲染成 "(5)[]" 这种调试形态。
/// Jint 纯托管沙箱：关闭 CLR 互操作（Interop.Enabled=false），带 2 秒超时、语句数/内存上限与结果长度上限。
/// 约束在每次 Evaluate/Invoke 时自动按独立预算生效，缓存引擎重复调用不受影响。
/// 引擎按脚本源码缓存复用（脚本可简单有状态）；执行出错即逐出重建，避免坏状态被后续调用复用。
/// 沙箱另注入 console（log/info/warn/error/debug）：Jint 本身没有 console，而 Strict 模式下访问未定义全局
/// 会直接抛 ReferenceError。注入是纯 JS 实现（不引入 .NET 对象，Interop 保持关闭），日志只留在引擎内的
/// 闭包数组里，由调用方通过 <see cref="Run"/> 的 logs 参数读取——目前仅编辑器「试跑」使用。
/// </summary>
internal sealed class JsScriptRunner
{
    internal const int MaxResultChars = 128 * 1024;
    /// <summary>单次执行的试跑日志条数上限，超出只置溢出标记（防脚本刷爆内存）。</summary>
    internal const int MaxLogEntries = 200;
    /// <summary>单条日志字符上限，超出截断。</summary>
    internal const int MaxLogEntryChars = 2_000;
    private const int MaxCachedEngines = 8;

    // 生产共享实例（UI 线程单线程执行）；测试用各自实例，避免并行测试互相污染缓存。
    internal static readonly JsScriptRunner Shared = new();

    /// <summary>缓存条目：引擎 + 建引擎时抓到的返回值文本化函数（不受脚本改写全局影响）。</summary>
    private sealed record CachedScript(Engine Engine, JsValue ResultText);

    private readonly Dictionary<string, CachedScript> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    private static Options BuildOptions(bool network = false) => new Options()
        .TimeoutInterval(network ? NetworkTimeout : TimeSpan.FromSeconds(2))
        .MaxStatements(50_000)
        .LimitMemory(16_000_000);

    /// <summary>联网脚本的 Jint 超时。spike 实测 <c>TimeoutInterval</c> 按「同步段」重置、且无法中断
    /// 挂起的 await —— 它只拦得住死循环；单次运行真正的时长上限由外部 CancellationToken 负责，
    /// 所以这里取得比单请求上限（8s）宽，避免合法请求在恢复时被误判超时。</summary>
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 注入沙箱 console 的宿主脚本。纯 JS，因此 Interop 关闭时同样可用，脚本也拿不到任何额外宿主能力；
    /// 日志存在闭包数组里，只经 __snap.snapshot() 交回宿主。条数/长度上限由宿主写入的全局变量提供，
    /// 避免上限常量在 C# 与 JS 两处维护。
    /// </summary>
    private const string ConsolePrelude = """
        var __snap = (function (maxEntries, maxChars) {
            var logs = [];
            var overflow = false;
            function fmt(v) {
                if (typeof v === 'string') return v;
                if (v === undefined) return 'undefined';
                if (v === null) return 'null';
                if (typeof v === 'object' || typeof v === 'function') {
                    try { var s = JSON.stringify(v); if (s !== undefined) return s; } catch (e) { }
                }
                return String(v);
            }
            function push() {
                if (logs.length >= maxEntries) { overflow = true; return; }
                var parts = [];
                for (var i = 0; i < arguments.length; i++) parts.push(fmt(arguments[i]));
                var line = parts.join(' ');
                logs.push(line.length > maxChars ? line.slice(0, maxChars) + '…' : line);
            }
            var api = { log: push };
            return {
                console: api,
                snapshot: function () { return JSON.stringify({ overflow: overflow, lines: logs }); },
                reset: function () { logs.length = 0; overflow = false; }
            };
        })(__snapMaxLogs, __snapMaxLogChars);
        var console = __snap.console;

        // 返回值的文本化。脚本返回数组/对象是常见写法（例如生成一串随机数），
        // 直接交给 Jint 的 CLR ToString 会得到 "(5)[]" 这类调试文本，所以在这里按 JS 语义转：
        // 字符串、undefined/null 字面量、数组与普通对象 → JSON、其余 → String()。
        // 整体包 try：Object.create(null) 之类没有 toString 的值也不会让动作失败。
        function __snapResultText(v) {
            try {
                if (typeof v === 'string') return v;
                if (v === undefined) return 'undefined';
                if (v === null) return 'null';
                if (Array.isArray(v) || (typeof v === 'object' && Object.getPrototypeOf(v) === Object.prototype)) {
                    // 循环引用等让 JSON.stringify 抛错的情况：落回 String()，别让整个动作失败。
                    try { var json = JSON.stringify(v); if (json !== undefined) return json; } catch (e) { }
                }
                return String(v);
            } catch (e) {
                return '[无法转换为文本的返回值]';
            }
        }
        """;

    /// <summary>
    /// 联网脚本额外注入的宿主 API（纯 JS 包装，不引入任何 .NET 对象）：
    /// <c>http.get(url, options?)</c> / <c>http.post(url, body, options?)</c> → Promise&lt;{status, ok, headers, body}&gt;。
    /// 宿主委托返回 Task，Jint 把它转成 Promise 由事件循环兑现（spike 实测）。
    /// </summary>
    private const string NetworkPrelude = """
        async function __snapHttpRequest(method, url, body, options) {
            return JSON.parse(await __snapHttp(JSON.stringify({
                method: method,
                url: String(url),
                body: body === undefined || body === null ? '' : String(body),
                options: options === undefined || options === null ? {} : options
            })));
        }
        var http = {
            get: function (url, options) { return __snapHttpRequest('GET', url, '', options); },
            post: function (url, body, options) { return __snapHttpRequest('POST', url, body, options); }
        };
        """;

    /// <summary>
    /// <c>Translation(text, from?, to?)</c> → Promise&lt;string&gt;，执行设置里选中的自定义翻译引擎。
    /// 单独一份 prelude：翻译引擎自己的脚本里<b>不</b>注入它，否则引擎调用 Translation 会自递归。
    /// </summary>
    private const string TranslationPrelude = """
        async function Translation(text, from, to) {
            return await __snapTranslate(JSON.stringify({
                text: String(text),
                from: from === undefined || from === null ? '' : String(from),
                to: to === undefined || to === null ? '' : String(to)
            }));
        }
        """;

    /// <summary>联网脚本的沙箱附加项：语言上下文 + 是否暴露 <c>Translation</c>。</summary>
    internal readonly record struct NetworkSandbox(
        string SourceLanguage, string TargetLanguage, bool ExposeTranslation)
    {
        /// <summary>默认沙箱（普通联网脚本）：不限定语言、暴露 <c>Translation</c>。
        /// 注意：结构体请勿用 <c>new NetworkSandbox()</c> 或 <c>default</c> 取默认值——
        /// 前者会绑定到 struct 的隐式无参构造函数，等价于 default，把 <see cref="ExposeTranslation"/> 归零成 false。</summary>
        internal static readonly NetworkSandbox Default = new("", "", true);
    }

    /// <summary>
    /// 执行脚本。成功返回 true 且 result 是脚本返回值的文本形态（可能为空字符串）；
    /// 失败返回 false 且 error 为中文说明（脚本抛错/超时、没定义 JSAction、返回 undefined/null、
    /// 或文本化后超过 128K）。日志见 logs 参数。
    /// logs 非空时收集本次执行的 console 输出（编辑器试跑）；传 null 表示不关心（动作实际执行，静默）。
    /// </summary>
    internal bool Run(string code, string text, out string? result, out string? error, List<string>? logs = null)
    {
        CachedScript entry;
        lock (_cache)
        {
            if (!_cache.TryGetValue(code, out var cached))
            {
                var options = BuildOptions();
                options.Strict = true;
                options.Interop.Enabled = false; // 沙箱：脚本无法触达任何 .NET 对象
                var created = new Engine(options);
                created.SetValue("__snapMaxLogs", MaxLogEntries);
                created.SetValue("__snapMaxLogChars", MaxLogEntryChars);
                created.Evaluate(ConsolePrelude); // 宿主自己的注入代码（编译期常量），不会失败
                // 在这里就把文本化函数抓到宿主手里：脚本之后改写/清空这个全局也影响不到结果转换。
                var resultText = created.GetValue("__snapResultText");
                try
                {
                    // 顶层只定义 JSAction，不接触用户输入；仍在预算内执行，防超长脚本拖垮加载。
                    created.Evaluate(code);
                }
                catch
                {
                    created.Dispose();
                    return Fail(out result, out error, "脚本解析失败");
                }
                entry = new CachedScript(created, resultText);
                _cache[code] = entry;
                _order.Add(code);
                if (_order.Count > MaxCachedEngines)
                {
                    var oldest = _order[0];
                    _order.RemoveAt(0);
                    if (_cache.Remove(oldest, out var oldEntry)) oldEntry.Engine.Dispose();
                }
            }
            else entry = cached;
        }
        var engine = entry.Engine;

        if (!TryResetAndCheckEntryPoint(engine))
        {
            return Fail(out result, out error,
                "脚本未定义入口函数 JSAction(text)，请检查函数名（必须叫 JSAction）。");
        }
        try
        {
            var v = engine.Invoke("JSAction", text);
            if (v.IsUndefined() || v.IsNull())
            {
                CollectLogs(engine, logs);
                return Fail(out result, out error, "脚本未返回结果（JSAction 返回了 undefined/null）");
            }
            // 字符串走快路径；其它类型交给创建引擎时抓到的 __snapResultText 按 JS 语义文本化
            // （数组/对象 → JSON）。用 v.ToString() 会把 JsArray 渲染成 "(5)[]"。
            string str;
            if (v.IsString())
            {
                str = v.AsString();
            }
            else
            {
                try { str = engine.Invoke(entry.ResultText, JsValue.Undefined, [v]).ToString() ?? ""; }
                catch { str = v.ToString() ?? ""; } // 极端兜底（例如文本化函数被销毁），至少仍是字符串
            }
            CollectLogs(engine, logs);
            if (str.Length > MaxResultChars)
                return Fail(out result, out error, $"输出超过 {MaxResultChars / 1024}K 字符");
            result = str;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // 抛错前的 console 输出也要可见，随后该引擎会被逐出，所以先读再 Dispose。
            CollectLogs(engine, logs);
            // 脚本异常/超时等：逐出该引擎，避免坏状态被后续调用复用。
            lock (_cache)
            {
                if (_cache.TryGetValue(code, out var cached) && ReferenceEquals(cached.Engine, engine))
                {
                    _cache.Remove(code);
                    _order.Remove(code);
                    engine.Dispose();
                }
            }
            return Fail(out result, out error, ex is JintException
                ? "脚本错误: " + ex.Message
                : "脚本执行失败: " + ex.Message);
        }
    }

    /// <summary>脚本执行结果（异步路径用；同步 <see cref="Run"/> 保留 out 参数以兼容既有调用）。</summary>
    internal readonly record struct ScriptResult(bool Success, string? Text, string? Error);

    /// <summary>
    /// 异步执行入口：<paramref name="allowNetwork"/> 为 true 时按「允许访问网络」新建引擎并注入
    /// <c>http</c> / <c>Translation</c>；否则原样走同步 <see cref="Run"/>（既有脚本零行为变化）。
    /// 联网引擎<b>不进缓存</b>：宿主委托要绑定本次运行的请求预算与取消令牌，缓存会把它们变成陈旧状态。
    /// </summary>
    internal async Task<ScriptResult> RunAsync(string code, string text, bool allowNetwork,
        List<string>? logs = null, CancellationToken ct = default, NetworkSandbox? sandbox = null)
    {
        if (!allowNetwork)
        {
            var ok = Run(code, text, out var result, out var error, logs);
            return new ScriptResult(ok, result, error);
        }
        return await RunNetworkAsync(code, text, logs, ct, sandbox ?? NetworkSandbox.Default);
    }

    private async Task<ScriptResult> RunNetworkAsync(
        string code, string text, List<string>? logs, CancellationToken ct, NetworkSandbox sandbox)
    {
        var options = BuildOptions(network: true);
        options.Strict = true;
        options.Interop.Enabled = false; // 沙箱：脚本无法触达任何 .NET 对象
        var engine = new Engine(options);
        try
        {
            engine.SetValue("__snapMaxLogs", MaxLogEntries);
            engine.SetValue("__snapMaxLogChars", MaxLogEntryChars);
            // 语言上下文：只放字符串（原始值），脚本按需读取。
            engine.SetValue("SNAP_SOURCE_LANGUAGE", sandbox.SourceLanguage);
            engine.SetValue("SNAP_TARGET_LANGUAGE", sandbox.TargetLanguage);
            engine.Evaluate(ConsolePrelude);
            // 抓文本化函数到宿主手里（同同步路径）：脚本之后改写这个全局也影响不到结果转换。
            var resultText = engine.GetValue("__snapResultText");
            // 只注入委托，绝不注入 CLR 对象：spike 实测 Interop 关闭也不拦 SetValue 进去的对象成员，
            // 所以沙箱边界一律「JSON 字符串进、字符串/Promise 出」。
            var bridge = new ScriptHttpBridge(ct);
            engine.SetValue("__snapHttp", new Func<string, Task<string>>(bridge.SendAsync));
            engine.Evaluate(NetworkPrelude);
            if (sandbox.ExposeTranslation)
            {
                engine.SetValue("__snapTranslate", new Func<string, Task<string>>(json => TranslateForSandboxAsync(json, ct)));
                engine.Evaluate(TranslationPrelude);
            }
            try
            {
                engine.Evaluate(code);
            }
            catch
            {
                return new ScriptResult(false, null, "脚本解析失败");
            }
            if (!TryResetAndCheckEntryPoint(engine))
                return new ScriptResult(false, null,
                    "脚本未定义入口函数 JSAction(text)，请检查函数名（必须叫 JSAction）。");
            var v = await engine.InvokeAsync("JSAction", ct, new object[] { text });
            CollectLogs(engine, logs);
            return ToResult(engine, resultText, v);
        }
        catch (OperationCanceledException)
        {
            // 外部取消令牌到点：这是单次运行真正的时长上限（TimeoutInterval 拦不住挂起的 await）。
            CollectLogs(engine, logs);
            return new ScriptResult(false, null, $"脚本执行超时（超过 {NetworkTimeout.TotalSeconds:0} 秒）");
        }
        catch (Exception ex)
        {
            CollectLogs(engine, logs);
            return new ScriptResult(false, null, ex is JintException
                ? "脚本错误: " + ex.Message
                : "脚本执行失败: " + ex.Message);
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>沙箱 Translation 宿主委托：解析 {text,from,to}，执行设置里选中的自定义翻译引擎。
    /// 异常由 Jint 转成 JS 异常，脚本可 try/catch；未捕获时归为「脚本错误」。</summary>
    private static async Task<string> TranslateForSandboxAsync(string requestJson, CancellationToken ct)
    {
        string text, from, to;
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            text = ReadJsonString(root, "text");
            from = ReadJsonString(root, "from");
            to = ReadJsonString(root, "to");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Translation 参数无法解析：" + ex.Message);
        }
        if (text.Length == 0) throw new InvalidOperationException("Translation 的文本为空");
        return await TranslationEngineService.RunSelectedAsync(text, from, to, ct);
    }

    private static string ReadJsonString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>把 Jint 的返回值转成结果文本（与同步路径同一套语义：字符串直通，数组/对象转 JSON）。</summary>
    private static ScriptResult ToResult(Engine engine, JsValue resultText, JsValue v)
    {
        if (v.IsUndefined() || v.IsNull())
            return new ScriptResult(false, null, "脚本未返回结果（JSAction 返回了 undefined/null）");
        string str;
        if (v.IsString())
        {
            str = v.AsString();
        }
        else
        {
            try { str = engine.Invoke(resultText, JsValue.Undefined, [v]).ToString() ?? ""; }
            catch { str = v.ToString() ?? ""; }
        }
        return str.Length > MaxResultChars
            ? new ScriptResult(false, null, $"输出超过 {MaxResultChars / 1024}K 字符")
            : new ScriptResult(true, str, null);
    }

    /// <summary>
    ///     每轮开始前的一次 Evaluate 里做两件事：清空上一轮的试跑日志、确认入口函数存在。
    ///     注入对象被脚本改坏时返回 true（不误判），让后面的 Invoke 去报真正的原因。
    /// </summary>
    private static bool TryResetAndCheckEntryPoint(Engine engine)
    {
        try
        {
            var kind = engine.Evaluate("__snap.reset(); typeof JSAction");
            return !kind.IsString() || kind.AsString() == "function";
        }
        catch { return true; }
    }

    /// <summary>best-effort 读取本轮 console 输出；logs 为 null（动作实际执行）时不做任何事。</summary>
    private static void CollectLogs(Engine engine, List<string>? logs)
    {
        if (logs == null) return;
        try
        {
            var json = engine.Evaluate("__snap.snapshot()").ToString();
            if (string.IsNullOrEmpty(json) || json == "null") return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                    logs.Add(line.ValueKind == JsonValueKind.String ? line.GetString() ?? "" : line.ToString());
            }
            if (root.TryGetProperty("overflow", out var overflow) && overflow.ValueKind == JsonValueKind.True)
                logs.Add($"…（console 输出超过 {MaxLogEntries} 条，已截断）");
        }
        catch { /* 日志读取失败不影响脚本结果 */ }
    }

    private static bool Fail(out string? result, out string? error, string message)
    {
        result = null;
        error = message;
        return false;
    }
}
