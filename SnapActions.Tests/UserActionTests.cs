using System.Collections.Generic;
using System.Linq;
using SnapActions.Actions;
using SnapActions.Actions.UserActions;
using SnapActions.Config;
using SnapActions.Detection;
using Xunit;

namespace SnapActions.Tests;

[Collection("Sequential")] // Ensure tests run sequentially to avoid conflicts with shared settings
public class UserActionTests
{
    // ── User recipe actions (data-driven custom actions) ─────────

    [Fact]
    public void UserRecipeAction_AppliesToAny_WhenTypeBlank()
    {
        var a = new UserRecipeAction(new UserAction { Id = "a", Name = "A", UrlTemplate = "https://x/{0}" });
        Assert.True(a.CanExecute("hello", TextAnalysis.PlainText));
    }

    [Fact]
    public void UserRecipeAction_AppliesToType_Filters()
    {
        var urlOnly = new UserRecipeAction(new UserAction
        { Id = "u", Name = "U", UrlTemplate = "https://x/{0}", AppliesToType = "Url" });
        Assert.False(urlOnly.CanExecute("hello", TextAnalysis.PlainText));
        Assert.True(urlOnly.CanExecute("http://e.com", new TextAnalysis(TextType.Url, 0.95)));
    }

    [Fact]
    public void GetActions_IncludesEnabledUserActions_AndSkipsDisabled()
    {
        var registry = new ActionRegistry();
        var list = SettingsManager.Current.UserActions;
        var previous = SettingsManager.Current.EnableCustomActions;
        SettingsManager.Current.EnableCustomActions = true;
        try
        {
            list.Add(new UserAction { Id = "t_on", Name = "On", UrlTemplate = "https://x/{0}", Enabled = true });
            list.Add(new UserAction { Id = "t_off", Name = "Off", UrlTemplate = "https://x/{0}", Enabled = false });
            var ids = registry.GetActions("hello", TextAnalysis.PlainText, null)
                .SelectMany(g => g.Actions).Select(a => a.Id).ToList();
            Assert.Contains("user_t_on", ids);
            Assert.DoesNotContain("user_t_off", ids);
        }
        finally { list.RemoveAll(u => u.Id is "t_on" or "t_off"); SettingsManager.Current.EnableCustomActions = previous; }
    }

    // ── App-aware profiles (per-app hidden actions) ──────────────

    [Fact]
    public void GetActions_AppProfile_HidesActionForThatAppOnly()
    {
        var registry = new ActionRegistry();
        var analysis = new TextAnalysis(TextType.MathExpression, 0.9);
        bool HasCalc(string? app) => registry.GetActions("2+2", analysis, app)
            .SelectMany(g => g.Actions).Any(a => a.Id == "calculate");

        Assert.True(HasCalc(null)); // baseline — Calculate is offered for a math expression

        var map = SettingsManager.Current.AppHiddenActions;
        try
        {
            map["TestApp"] = new List<string> { "calculate" };
            Assert.False(HasCalc("TestApp"));  // hidden for the configured app
            Assert.True(HasCalc("OtherApp"));  // unaffected elsewhere
            Assert.True(HasCalc(null));        // unaffected with no app context
        }
        finally { map.Remove("TestApp"); }
    }

    [Fact]
    public void GetAllKnownActionIds_IncludesUserActions()
    {
        var list = SettingsManager.Current.UserActions;
        try
        {
            list.Add(new UserAction { Id = "k1", Name = "K", UrlTemplate = "https://x/{0}" });
            var ids = ActionRegistry.GetAllKnownActionIds(SettingsManager.Current.SearchEngines);
            Assert.Contains("user_k1", ids);
        }
        finally { list.RemoveAll(u => u.Id == "k1"); }
    }

    // ── JS script actions ─────────────────────────────────────────

    private const string UpperJs = "function JSAction(text) { return text.toUpperCase(); }";

    [Fact]
    public void UserScriptAction_RunsAndReturnsText()
    {
        var runner = new JsScriptRunner();
        Assert.True(runner.Run(UpperJs, "hello", out var result, out var error));
        Assert.Equal("HELLO", result);
        Assert.Null(error);
    }

    [Fact]
    public void UserScriptAction_AppliesToType_Filters()
    {
        var urlOnly = new UserScriptAction(new UserAction
        { Id = "s", Name = "S", Code = "function JSAction(t) { return t; }", AppliesToType = "Url" });
        Assert.False(urlOnly.CanExecute("hello", TextAnalysis.PlainText));
        Assert.True(urlOnly.CanExecute("http://e.com", new TextAnalysis(TextType.Url, 0.95)));
    }

    [Fact]
    public void UserScriptAction_MissingEntryPoint_Fails()
    {
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("var x = 1;", "hello", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void UserScriptAction_ScriptThrows_Fails()
    {
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("function JSAction() { throw new Error('boom'); }", "hello", out _, out var error));
        Assert.Contains("boom", error ?? "");
    }

    [Fact]
    public void UserScriptAction_NoReturnValue_Fails()
    {
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("function JSAction(t) { t; }", "hello", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void UserScriptAction_InfiniteLoop_StopsWithinBudget()
    {
        var runner = new JsScriptRunner();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = runner.Run("function JSAction() { while (true) { } }", "hello", out _, out var error);
        sw.Stop();
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "loop not bounded");
    }

    [Fact]
    public void UserScriptAction_OversizedOutput_Fails()
    {
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("function JSAction() { return 'a'.repeat(200000); }", "x", out _, out var error));
        Assert.Contains("128K", error ?? "");
    }

    [Fact]
    public void GetActions_IncludesScriptActionInTransformGroup()
    {
        var registry = new ActionRegistry();
        var list = SettingsManager.Current.UserActions;
        var previous = SettingsManager.Current.EnableCustomActions;
        SettingsManager.Current.EnableCustomActions = true;
        try
        {
            list.Add(new UserAction { Id = "s_1", Name = "S1", Code = "function JSAction(t) { return t; }" });
            var ids = registry.GetActions("hello", TextAnalysis.PlainText, null)
                .SelectMany(g => g.Actions).Select(a => a.Id).ToList();
            Assert.Contains("user_s_1", ids);
            Assert.Contains(registry.GetAllActionsForCategory(ActionCategory.Transform), a => a.Id == "user_s_1");
        }
        finally { list.RemoveAll(u => u.Id == "s_1"); SettingsManager.Current.EnableCustomActions = previous; }
    }

    [Fact]
    public void GetActions_IncludesScriptFileActionInTransformGroup()
    {
        var registry = new ActionRegistry();
        var list = SettingsManager.Current.UserActions;
        var previous = SettingsManager.Current.EnableCustomActions;
        SettingsManager.Current.EnableCustomActions = true;
        try
        {
            list.Add(new UserAction { Id = "sf_1", Name = "S1", ScriptFile = "sf_1.js" });
            var transformActions = registry.GetAllActionsForCategory(ActionCategory.Transform);
            var script = Assert.Single(transformActions, a => a.Id == "user_sf_1");
            Assert.IsType<UserScriptAction>(script);
            Assert.DoesNotContain(registry.GetAllActionsForCategory(ActionCategory.Context), a => a.Id == "user_sf_1");
        }
        finally { list.RemoveAll(u => u.Id == "sf_1"); SettingsManager.Current.EnableCustomActions = previous; }
    }

    [Fact]
    public void SettingsValidator_KeepsCodeOnlyUserAction()
    {
        var s = new AppSettings
        {
            UserActions = [new UserAction { Id = "c1", Name = "C", Code = "function JSAction(t) { return t; }" }]
        };
        SettingsValidator.Normalize(s);
        var kept = Assert.Single(s.UserActions);
        Assert.Equal("c1", kept.Id);
        Assert.Equal("function JSAction(t) { return t; }", kept.Code);
    }

    [Fact]
    public void SettingsValidator_KeepsScriptFileUserAction()
    {
        var s = new AppSettings
        {
            UserActions = [new UserAction { Id = "f1", Name = "F", ScriptFile = "f1.js" }]
        };
        SettingsValidator.Normalize(s);
        var kept = Assert.Single(s.UserActions);
        Assert.Equal("f1.js", kept.ScriptFile);
    }

    [Fact]
    public void UserScriptAction_MissingScriptFile_FailsClosed()
    {
        var id = "sf_" + Guid.NewGuid().ToString("N");
        var action = new UserScriptAction(new UserAction { Id = id, Name = "NoFile", ScriptFile = id + ".js" });
        var result = action.Execute("hello", TextAnalysis.PlainText);
        Assert.False(result.Success);
        Assert.Contains("脚本文件缺失", result.Message ?? "");
    }
}
