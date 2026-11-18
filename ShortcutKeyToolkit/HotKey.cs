namespace ShortcutKeyToolkit;

[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>
/// A modifier mask and a Windows virtual-key code, such as 0x41 for A.
/// </summary>
public readonly record struct HotKey(HotKeyModifiers Modifiers, int VirtualKey)
{
    internal const HotKeyModifiers AllModifiers = HotKeyModifiers.Alt | HotKeyModifiers.Control
        | HotKeyModifiers.Shift | HotKeyModifiers.Windows;

    internal bool IsValid => (Modifiers & ~AllModifiers) == 0
        && VirtualKey is >= 0x08 and <= 0xFE
        && VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x7B)
        && VirtualKey is not (>= 0xA0 and <= 0xA5);
}

public enum HotKeyStatus
{
    Success,
    InvalidHotKey,
    LocalConflict,
    SystemConflict,
    NotFound,
    CapacityReached,
    WindowsError
}

public readonly record struct HotKeyResult(
    HotKeyStatus Status,
    int? NativeError = null,
    string? ConflictingName = null)
{
    public bool Succeeded => Status == HotKeyStatus.Success;
}
