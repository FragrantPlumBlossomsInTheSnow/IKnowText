using SnapActions.Actions;
using SnapActions.Config;
using Xunit;

namespace SnapActions.Tests;

public class ToolbarPreferencesTests
{
    private static IAction Action(string id) => new ActionRegistry()
        .GetAllActionsForCategory(ActionCategory.Transform).Single(a => a.Id == id);

    [Fact]
    public void PinningHiddenActionShowsItAndKeepsExistingOrder()
    {
        var settings = new AppSettings { PinnedActionIds = ["case_upper", "ws_trim"], DisabledActionIds = ["case_snake"] };
        ToolbarPreferences.Pin(settings, Action("case_snake"), "ws_trim");
        Assert.Equal(["case_upper", "case_snake", "ws_trim"], settings.PinnedActionIds);
        Assert.DoesNotContain("case_snake", settings.DisabledActionIds);
    }

    [Theory]
    [InlineData("case_upper", "case_snake", true, "ws_trim,case_snake,case_upper")]
    [InlineData("case_snake", "case_upper", false, "case_snake,case_upper,ws_trim")]
    [InlineData("case_upper", "ws_trim", true, "ws_trim,case_upper,case_snake")]
    [InlineData("ws_trim", "ws_trim", true, "case_upper,ws_trim,case_snake")]
    [InlineData("case_upper", null, false, "ws_trim,case_snake,case_upper")]
    public void DropPositionReordersWithoutDuplicates(string id, string? target, bool after, string expected)
    {
        var settings = new AppSettings { PinnedActionIds = ["case_upper", "ws_trim", "case_snake"] };
        ToolbarPreferences.Pin(settings, Action(id), target, after);
        Assert.Equal(expected.Split(','), settings.PinnedActionIds);
    }

    [Fact]
    public void HideAndShowKeepTheSavedPinPosition()
    {
        var settings = new AppSettings { PinnedActionIds = ["case_upper", "ws_trim"] };
        var action = Action("case_upper");
        ToolbarPreferences.SetHidden(settings, action, true);
        Assert.True(ToolbarPreferences.IsHidden(settings, action));
        ToolbarPreferences.SetHidden(settings, action, false);
        Assert.False(ToolbarPreferences.IsHidden(settings, action));
        Assert.Equal(["case_upper", "ws_trim"], settings.PinnedActionIds);
    }

    [Fact]
    public void PinRestoresSearchEngineAndSettingsCanEnableItAfterHiding()
    {
        var settings = new AppSettings();
        var engine = settings.SearchEngines.Single(e => e.Id == "google");
        engine.Enabled = false;
        settings.DisabledActionIds.Add("search_google");
        var action = new ActionRegistry().GetAllActionsForCategory(ActionCategory.Search).Single(a => a.Id == "search_google");
        ToolbarPreferences.Pin(settings, action);
        Assert.True(engine.Enabled);
        Assert.False(ToolbarPreferences.IsHidden(settings, action));
        ToolbarPreferences.SetHidden(settings, action, true);
        Assert.False(engine.Enabled);
        engine.Enabled = true;
        Assert.False(ToolbarPreferences.IsHidden(settings, action));
    }

    [Fact]
    public void RetiredActionsArePrunedWithoutLosingPinnedActions()
    {
        var settings = SettingsManager.Parse("""{"PinnedActionIds":["generate_qr","inspect_text","case_upper","case_snake"],"DisabledActionIds":["generate_qr","inspect_text"]}""");
        Assert.Equal(["case_upper", "case_snake"], settings.PinnedActionIds);
        Assert.Empty(settings.DisabledActionIds);
        var ids = new ActionRegistry().AllActionDescriptors().Select(a => a.Id).ToList();
        Assert.DoesNotContain("generate_qr", ids);
        Assert.DoesNotContain("inspect_text", ids);
    }
}
