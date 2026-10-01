using System.Collections.Generic;
using System.Linq;
using SnapActions.Actions;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using SnapActions.Detection;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// JS 脚本动作的「上下文触发」正则：命中时该动作要像内置上下文动作（数学计算、格式化 JSON…）一样进入
/// Context 组 —— 也就是工具栏 ContextSeparator 后面那排内联按钮；未命中/非法正则/动作停用时都不能触发。
/// 触发时它仍留在 Transform 组里，转换子菜单与固定区行为不变。
/// </summary>
[Collection("SettingsManager collection")]
public class UserScriptContextTriggerTests
{
    private const string Js = "function JSAction(t) { return t; }";

    private static UserAction Script(string id, string? regex, bool enabled = true) =>
        new() { Id = id, Name = "Ctx " + id, Code = Js, ContextRegex = regex ?? "", Enabled = enabled };

    private static (List<IAction> Context, List<IAction> Transform) Groups(ActionRegistry registry, string text)
    {
        var groups = registry.GetActions(text, TextAnalysis.PlainText, null);
        return (groups.FirstOrDefault(g => g.Name == "Context")?.Actions ?? [],
                groups.FirstOrDefault(g => g.Name == "Transform")?.Actions ?? []);
    }

    /// <summary>在开启自定义动作的前提下跑一段断言，结束后把设置恢复原样。</summary>
    private static void WithScripts(string id, string? regex, bool enabled, Action<ActionRegistry> body)
    {
        var registry = new ActionRegistry();
        var list = SettingsManager.Current.UserActions;
        var prevCustom = SettingsManager.Current.EnableCustomActions;
        var prevTransform = SettingsManager.Current.ShowTransformActions;
        SettingsManager.Current.EnableCustomActions = true;
        SettingsManager.Current.ShowTransformActions = true;
        list.Add(Script(id, regex, enabled));
        try { body(registry); }
        finally
        {
            list.RemoveAll(u => u.Id == id);
            SettingsManager.Current.EnableCustomActions = prevCustom;
            SettingsManager.Current.ShowTransformActions = prevTransform;
        }
    }

    // ── 注册表分组 ──────────────────────────────────────────────

    [Fact]
    public void MatchingRegex_PutsScriptActionInContextAndTransform()
    {
        WithScripts("ctx_trigger", @"^\s*\{", enabled: true, registry =>
        {
            var (context, transformGroup) = Groups(registry, "{\"a\":1}");
            Assert.Contains(context, a => a.Id == "user_ctx_trigger");
            Assert.Contains(transformGroup, a => a.Id == "user_ctx_trigger");
        });
    }

    [Fact]
    public void NonMatchingRegex_KeepsScriptActionOutOfContext()
    {
        WithScripts("ctx_trigger", @"^\s*\{", enabled: true, registry =>
        {
            var (context, transformGroup) = Groups(registry, "hello world");
            Assert.DoesNotContain(context, a => a.Id == "user_ctx_trigger");
            Assert.Contains(transformGroup, a => a.Id == "user_ctx_trigger");
        });
    }

    [Fact]
    public void EmptyRegex_NeverTriggers()
    {
        WithScripts("ctx_trigger", "", enabled: true, registry =>
        {
            var (context, _) = Groups(registry, "{\"a\":1}");
            Assert.DoesNotContain(context, a => a.Id == "user_ctx_trigger");
        });
    }

    [Fact]
    public void InvalidRegex_NeverTriggersAndDoesNotThrow()
    {
        WithScripts("ctx_trigger", "([", enabled: true, registry =>
        {
            var (context, transformGroup) = Groups(registry, "([ broken");
            Assert.DoesNotContain(context, a => a.Id == "user_ctx_trigger");
            Assert.Contains(transformGroup, a => a.Id == "user_ctx_trigger");
        });
    }

    [Fact]
    public void DisabledScriptAction_TriggersNothing()
    {
        WithScripts("ctx_trigger", @"^\s*\{", enabled: false, registry =>
        {
            var (context, transformGroup) = Groups(registry, "{\"a\":1}");
            Assert.DoesNotContain(context, a => a.Id == "user_ctx_trigger");
            Assert.DoesNotContain(transformGroup, a => a.Id == "user_ctx_trigger");
        });
    }

    // ── 正则求值本身 ────────────────────────────────────────────

    [Fact]
    public void RegexMatchesWholeSelectionCaseSensitively()
    {
        Assert.True(ContextTriggerRegex.IsMatch("^JSON:", "JSON:{\"a\":1}"));
        Assert.False(ContextTriggerRegex.IsMatch("^JSON:", "json:{\"a\":1}"));
        Assert.True(ContextTriggerRegex.IsMatch("a{2,}", "caat"));
    }

    [Fact]
    public void EmptyInputsAndInvalidPatterns_FailClosed()
    {
        Assert.False(ContextTriggerRegex.IsMatch(null, "x"));
        Assert.False(ContextTriggerRegex.IsMatch("", "x"));
        Assert.False(ContextTriggerRegex.IsMatch("x", ""));
        Assert.False(ContextTriggerRegex.IsMatch("x", null));
        Assert.False(ContextTriggerRegex.IsMatch("([", "x"));

        Assert.True(ContextTriggerRegex.IsValid(null));
        Assert.True(ContextTriggerRegex.IsValid(""));
        Assert.True(ContextTriggerRegex.IsValid(@"^\s*\{"));
        Assert.False(ContextTriggerRegex.IsValid("(["));
    }

    [Fact]
    public void Action_ExposesTriggerStateWithoutChangingAvailability()
    {
        var action = new UserScriptAction(Script("ctx_exec", "^x"));
        Assert.True(action.IsContextTriggered("xyz"));
        Assert.False(action.IsContextTriggered("abc"));
        // 触发与否都不影响 CanExecute：转换子菜单/固定区里始终可用。
        Assert.True(action.CanExecute("abc", TextAnalysis.PlainText));
    }

    // ── 设置持久化 ──────────────────────────────────────────────

    [Fact]
    public void SettingsValidator_KeepsContextRegexAndTruncatesRunawayValues()
    {
        var s = new AppSettings
        {
            UserActions =
            [
                new UserAction { Id = "c_re", Name = "C", Code = Js, ContextRegex = @"^\d+$" },
                new UserAction { Id = "c_long", Name = "L", Code = Js, ContextRegex = new string('a', 3000) },
                new UserAction { Id = "c_null", Name = "N", Code = Js, ContextRegex = null! },
            ]
        };
        SettingsValidator.Normalize(s);
        Assert.Equal(@"^\d+$", s.UserActions.Single(a => a.Id == "c_re").ContextRegex);
        Assert.Equal(2048, s.UserActions.Single(a => a.Id == "c_long").ContextRegex.Length);
        Assert.Equal("", s.UserActions.Single(a => a.Id == "c_null").ContextRegex);
    }
}
