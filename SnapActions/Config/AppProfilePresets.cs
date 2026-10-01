using SnapActions.Actions;

namespace SnapActions.Config;

internal static class AppProfilePresets
{
    private static readonly string[] First = ["dictionary", "translate", "currency_convert"];

    // Explicitly chosen presets only add hidden actions. Existing per-app choices are preserved.
    internal static IEnumerable<string> HiddenActions(string preset, ActionRegistry registry) => preset switch
    {
        "Reading" => registry.AllActionDescriptors().Where(a => a.Category is ActionCategory.Encode).Select(a => a.Id),
        "Writing" => registry.AllActionDescriptors().Where(a => a.Category == ActionCategory.Encode).Select(a => a.Id),
        "Development" => First.Intersect(registry.AllActionDescriptors().Select(a => a.Id)),
        _ => []
    };
}
