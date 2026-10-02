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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}