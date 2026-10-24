namespace EpochAssist.Core;

// Runtime only, never stored: a setting typed by one of our enums would not match after a reload.
internal enum StatusState { Off, Waiting, Active, Holding, Paused }

/// <summary>
/// What a feature is doing, for the HUD, the menu and the dev bridge. Off: not running or switched off; Waiting: a
/// global pause rule holds it (no player, loading, typing...); Holding: a momentary rule of its own (a key held, the
/// enemy gate); Paused: a latched rule of its own (zone change, non-combat zone); Active: free to act.
/// Detail is the reason, empty when active; Slots marks toggled slots for the HUD pips, null when the feature has none.
/// </summary>
internal readonly record struct FeatureStatus(StatusState State, string Detail, bool[]? Slots)
{
    public static FeatureStatus Off(string detail) => new(StatusState.Off, detail, null);
}
