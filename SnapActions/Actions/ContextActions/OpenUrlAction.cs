using SnapActions.Detection;
using SnapActions.Helpers;

namespace SnapActions.Actions.ContextActions;

public class OpenUrlAction : IAction
{
    public string Id => "open_url";
    public string Name => "Open URL";
    public string IconKey => "IconOpenUrl";
    public ActionCategory Category => ActionCategory.Context;

    public bool CanExecute(string text, TextAnalysis analysis) => analysis.Type == TextType.Url;

    public ActionResult Execute(string text, TextAnalysis analysis) =>
        ProcessHelper.TryShellOpen(BuildUrl(text), "Opened in browser");

    internal static string BuildUrl(string text)
    {
        var url = text.Trim();
        // Don't blindly prepend https:// — the detector accepts ftp:// as well as bare domains.
        // A scheme inside a path or query does not belong to the selected URL itself.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
            url = "https://" + url;
        return url;
    }
}
