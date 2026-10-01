using System.Collections.Generic;
using SnapActions.Actions.UserActions;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// 沙箱注入的 console（编辑器试跑日志）。Jint 4.16.4 本身没有 console，而引擎开了 Strict 模式，
/// 所以脚本里直接写 console.log 会抛 ReferenceError。这些用例固定住注入行为、日志上限、
/// 跨轮不串日志，以及「不传 logs 的动作执行路径完全静默」的约定。
/// </summary>
public class UserScriptConsoleTests
{
    private const string LoggingJs =
        "function JSAction(text) { console.log('sel:', text, { n: 1 }); return text.toUpperCase(); }";

    [Fact]
    public void Console_LogIsCapturedAndScriptStillReturns()
    {
        var runner = new JsScriptRunner();
        var logs = new List<string>();
        Assert.True(runner.Run(LoggingJs, "hey", out var result, out var error, logs));
        Assert.Equal("HEY", result);
        Assert.Null(error);
        Assert.Equal("sel: hey {\"n\":1}", Assert.Single(logs));
    }

    [Fact]
    public void Console_ExposesLogOnly()
    {
        var runner = new JsScriptRunner();
        var logs = new List<string>();
        Assert.True(runner.Run("function JSAction(t) { console.log('a'); return t; }", "x", out _, out _, logs));
        Assert.Equal("a", Assert.Single(logs));
        // 当前沙箱只注入 log；用到未暴露的级别时脚本会明确失败，而不是静默丢日志。
        Assert.False(runner.Run("function JSAction(t) { console.warn('b'); return t; }", "x", out _, out var error, logs));
        Assert.Contains("warn", error ?? "");
    }

    [Fact]
    public void Console_FormatsValuesLikeConsole()
    {
        var runner = new JsScriptRunner();
        var logs = new List<string>();
        const string js = "function JSAction(t) { console.log(undefined, null, [1,2], 3.5, true, NaN); return t; }";
        Assert.True(runner.Run(js, "x", out _, out _, logs));
        // 字符串原样、undefined/null 字面量、对象走 JSON、数字/布尔走 String()
        Assert.Equal("undefined null [1,2] 3.5 true NaN", Assert.Single(logs));
    }

    [Fact]
    public void Console_NoSink_IsSilentButStillRuns()
    {
        // 动作实际执行的路径：UserScriptAction 不传 logs，console 输出不落任何地方。
        var runner = new JsScriptRunner();
        Assert.True(runner.Run(LoggingJs, "hey", out var result, out var error));
        Assert.Equal("HEY", result);
        Assert.Null(error);

        var explicitNull = new List<string>();
        Assert.True(runner.Run(LoggingJs, "hey", out _, out _, null));
        Assert.Empty(explicitNull);
    }

    [Fact]
    public void Console_LogsDoNotLeakBetweenRunsOnCachedEngine()
    {
        var runner = new JsScriptRunner();
        var first = new List<string>();
        Assert.True(runner.Run(LoggingJs, "one", out _, out _, first));

        // 同一实例 + 同一源码 → 命中缓存引擎（脚本可简单有状态），日志必须按轮清空。
        var second = new List<string>();
        Assert.True(runner.Run(LoggingJs, "two", out _, out _, second));

        Assert.Equal("sel: one {\"n\":1}", Assert.Single(first));
        Assert.Equal("sel: two {\"n\":1}", Assert.Single(second));
    }

    [Fact]
    public void Console_EntryCountIsCappedAndReported()
    {
        var runner = new JsScriptRunner();
        var logs = new List<string>();
        string js = $"function JSAction(t) {{ for (var i = 0; i < {JsScriptRunner.MaxLogEntries + 50}; i++) console.log(i); return t; }}";
        Assert.True(runner.Run(js, "x", out _, out _, logs));
        Assert.Equal(JsScriptRunner.MaxLogEntries + 1, logs.Count); // 上限条 + 一行截断提示
        Assert.Contains("截断", logs[^1]);
    }

    [Fact]
    public void Console_LongEntryIsTruncated()
    {
        var runner = new JsScriptRunner();
        var logs = new List<string>();
        Assert.True(runner.Run("function JSAction(t) { console.log('x'.repeat(5000)); return t; }", "x", out _, out _, logs));
        var line = Assert.Single(logs);
        Assert.Equal(JsScriptRunner.MaxLogEntryChars + 1, line.Length); // +1 = 末尾省略号
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Console_LogsBeforeThrow_AreStillCollected()
    {
        var runner = new JsScriptRunner();
        var logs = new List<string>();
        const string js = "function JSAction(t) { console.log('before'); throw new Error('boom'); }";
        Assert.False(runner.Run(js, "x", out _, out var error, logs));
        Assert.Contains("boom", error ?? "");
        Assert.Equal("before", Assert.Single(logs));
    }

    [Fact]
    public void Sandbox_StillRejectsUnknownGlobals()
    {
        // 注入 console 不能顺带放宽沙箱：Strict 模式下其它未定义全局仍必须是失败。
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("function JSAction(t) { return nope.log(t); }", "x", out _, out var error));
        Assert.Contains("nope", error ?? "");
    }
}
