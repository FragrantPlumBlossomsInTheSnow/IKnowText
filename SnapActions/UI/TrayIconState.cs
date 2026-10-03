using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SnapActions.UI;

public sealed class TrayIconState : INotifyPropertyChanged
{
    public static TrayIconState Instance { get; } = new();

    private string _autoStartGlyph = IconGlyphs.AutoStartEnable;
    public string AutoStartGlyph
    {
        get => _autoStartGlyph;
        set { if (_autoStartGlyph != value) { _autoStartGlyph = value; OnPropertyChanged(); } }
    }

    // 「启用动作」子菜单 5 个动作组的勾选态（由 TrayMenu.SyncActionGroupGlyphs 按设置刷新）。
    private string _pasteGlyph = IconGlyphs.ToggleOn;
    public string PasteGlyph
    {
        get => _pasteGlyph;
        set { if (_pasteGlyph != value) { _pasteGlyph = value; OnPropertyChanged(); } }
    }

    private string _translateGlyph = IconGlyphs.ToggleOn;
    public string TranslateGlyph
    {
        get => _translateGlyph;
        set { if (_translateGlyph != value) { _translateGlyph = value; OnPropertyChanged(); } }
    }

    private string _transformGlyph = IconGlyphs.ToggleOn;
    public string TransformGlyph
    {
        get => _transformGlyph;
        set { if (_transformGlyph != value) { _transformGlyph = value; OnPropertyChanged(); } }
    }

    private string _encodeDecodeGlyph = IconGlyphs.ToggleOn;
    public string EncodeDecodeGlyph
    {
        get => _encodeDecodeGlyph;
        set { if (_encodeDecodeGlyph != value) { _encodeDecodeGlyph = value; OnPropertyChanged(); } }
    }

    private string _searchGlyph = IconGlyphs.ToggleOn;
    public string SearchGlyph
    {
        get => _searchGlyph;
        set { if (_searchGlyph != value) { _searchGlyph = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}