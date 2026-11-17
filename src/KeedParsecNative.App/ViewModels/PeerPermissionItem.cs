using KeedParsecNative.Contracts.Input;

namespace KeedParsecNative.App.ViewModels;

/// <summary>Bindable per-peer input permission row for the host UI.</summary>
public sealed class PeerPermissionItem : ObservableObject
{
    private readonly Func<PeerInputPermissions, Task> _apply;

    public PeerPermissionItem(string peerId, string displayName, PeerInputPermissions initial,
        Func<PeerInputPermissions, Task> apply)
    {
        PeerId = peerId;
        DisplayName = displayName;
        _apply = apply;
        _allowKeyboard = initial.AllowKeyboard;
        _allowMouse = initial.AllowMouse;
        _allowGamepad = initial.AllowGamepad;
    }

    public string PeerId { get; }
    public string DisplayName { get; }

    public bool AllowKeyboard
    {
        get => _allowKeyboard;
        set { if (SetProperty(ref _allowKeyboard, value)) _ = ApplyAsync(); }
    }
    private bool _allowKeyboard;

    public bool AllowMouse
    {
        get => _allowMouse;
        set { if (SetProperty(ref _allowMouse, value)) _ = ApplyAsync(); }
    }
    private bool _allowMouse;

    public bool AllowGamepad
    {
        get => _allowGamepad;
        set { if (SetProperty(ref _allowGamepad, value)) _ = ApplyAsync(); }
    }
    private bool _allowGamepad;

    private async Task ApplyAsync()
    {
        await _apply(new PeerInputPermissions
        {
            AllowKeyboard = _allowKeyboard,
            AllowMouse = _allowMouse,
            AllowGamepad = _allowGamepad
        });
    }
}
