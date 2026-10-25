namespace EpochAssist.Core;

// Runtime only, never stored.
internal enum CharacterState { NoCharacter, Reading, NotReadable, Loaded, Unreadable }

/// <summary>
/// Which character is playing: the mode, the game's character slot and the character's name offline; online, where the
/// player has no slot, the game's character id instead. The name and id stay in memory for the file name and scrubbing
/// only; they are never written to the log, diagnostics or reports.
/// </summary>
internal sealed record CharacterKey(string Mode, int Slot, string Name, string Id = "")
{
    private const int MaxSlug = 40;

    /// <summary>
    /// The character's file: mode-slot-name.json offline, the name as lowercase letters, digits and dashes; online
    /// mode-id.json, so a renamed character keeps its file.
    /// </summary>
    public string FileName => Id.Length > 0
        ? $"{Mode}-{Profiles.Slug(Id, MaxSlug, "unknown")}.json"
        : $"{Mode}-{Slot}-{Profiles.Slug(Name, MaxSlug, "unnamed")}.json";

    /// <summary>For the log: the mode, and the slot where there is one.</summary>
    public string Label => Id.Length > 0 ? Mode : $"{Mode}, slot {Slot}";
}

/// <summary>
/// The game's own record of the character in play (its gameplay service): id, name and mode, set when a character
/// starts playing. Read by reflection, as the service's interface uses UniTask, which the project doesn't reference.
/// Online it is the only place the character is named: the online player has no actor or character data tracker.
/// </summary>
internal static class GameplayCharacter
{
    private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

    /// <summary>The service's id, name and offline flag; false with the reason when it isn't there yet.</summary>
    public static bool TryRead(out string id, out string name, out bool offline, out string reason)
    {
        id = name = string.Empty;
        offline = false;
        try
        {
            if (Service() is not { } service)
            {
                reason = "no gameplay service";
                return false;
            }
            object? Get(string member) => service.GetType().GetProperty(member, Any)?.GetValue(service);
            id = Get("CharacterId")?.ToString() ?? string.Empty;
            name = Get("CharacterName") as string ?? string.Empty;
            offline = Get("CharacterIsOffline") is true;
            reason = id.Length == 0 ? "no character id" : string.Empty;
            return id.Length > 0;
        }
        catch (Exception e)
        {
            reason = $"gameplay service: {(e.InnerException ?? e).GetType().Name}";
            return false;
        }
    }

    // The stored service first; GameplayService itself hands out a UniTask, read only once it has finished.
    private static object? Service()
    {
        var provider = typeof(Il2CppLE.Services.ServiceProvider);
        var property = provider.GetProperty("_GameplayService", Any) ?? provider.GetProperty("GameplayService", Any);
        if (property?.GetMethod == null) return null;
        object? owner = property.GetMethod.IsStatic ? null : provider.GetProperty("Instance", Any)?.GetValue(null);
        object? service = property.GetValue(owner);
        if (service == null || !service.GetType().Name.StartsWith("UniTask")) return service;
        if (service.GetType().GetProperty("Status", Any)?.GetValue(service)?.ToString() != "Succeeded") return null;
        object? awaiter = service.GetType().GetMethod("GetAwaiter", Any)?.Invoke(service, null);
        return awaiter?.GetType().GetMethod("GetResult", Any)?.Invoke(awaiter, null);
    }
}

/// <summary>A settings profile: its id (the file name, kept on rename) and the name the player gave it.</summary>
internal sealed record ProfileInfo(string Id, string Name)
{
    public bool IsDefault => Id == Profiles.DefaultId;
}
