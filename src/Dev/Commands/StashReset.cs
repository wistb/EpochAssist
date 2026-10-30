using EpochAssist.Core;
using EpochAssist.Features;
using Il2Cpp;

namespace EpochAssist.Dev;

// Puts the stash's save files back to a snapshot saved by tools/devloader/stash-saves.sh: the game's own Exit to Character
// Select, the stash files swapped once the character is unloaded, the same character chosen as a click on its tile does,
// and Enter Game. Runs a step a frame from the dev trace feature; the Stash page's dev button and `stash reset` start it.
internal static class StashReset
{
    public const string DefaultSnapshot = "start";

    private static string s_step = "idle";
    private static string s_snapshot = string.Empty, s_character = string.Empty, s_from = string.Empty, s_saves = string.Empty;
    private static float s_until;

    /// <summary>The last run's outcome line ("ok …" / "error …"), or null while one runs or none ran.</summary>
    public static string? Result { get; private set; }

    public static bool Running => s_step != "idle";

    public static string Status => s_step switch
    {
        "leaving" => "Leaving to character select…",
        "choosing" => $"Choosing {s_character}…",
        "entering" or "loading" => $"Entering as {s_character}…",
        _ => string.Empty,
    };

    public static string Snapshots => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "EpochAssist", "dev", "stash-saves");

    /// <summary>Starts a reset; the reason it can't, or null.</summary>
    public static string? Start(string snapshot, string? character = null)
    {
        if (Running) return "a reset is running";
        if (Stash.Read() is not { ClientCopy: false }) return "saved copy only";
        s_from = Path.Combine(Snapshots, snapshot);
        if (snapshot.Length == 0 || !Directory.Exists(s_from)) return $"no snapshot {snapshot} (tools/devloader/stash-saves.sh save {snapshot})";
        s_saves = Path.Combine(UnityEngine.Application.persistentDataPath, "Saves");
        if (!Directory.Exists(s_saves)) return $"no saves at {s_saves}";
        var ui = UIBase.instance;
        if (!Game.Alive(ui)) return "no game UI";
        s_snapshot = snapshot;
        // The character to enter again: the one named, else the one loaded now.
        s_character = string.IsNullOrWhiteSpace(character) ? PlayerFinder.getPlayerData()?.CharacterName ?? string.Empty : character.Trim();
        Result = null;
        s_until = UnityEngine.Time.unscaledTime + 120f;
        s_step = "leaving";
        ui.ExitToCharacterSelect();
        return null;
    }

    /// <summary>Saves the stash files now as a snapshot, as `stash-saves.sh save` does.</summary>
    public static string Save(string snapshot)
    {
        string saves = Path.Combine(UnityEngine.Application.persistentDataPath, "Saves");
        var files = Directory.GetFiles(saves, "STASH_CYCLE_*");
        if (files.Length == 0) return "error no stash files";
        string to = Path.Combine(Snapshots, snapshot);
        if (Directory.Exists(to)) Directory.Delete(to, true);
        Directory.CreateDirectory(to);
        foreach (string f in files) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        return $"ok saved {files.Length} stash files as {snapshot}";
    }

    public static void Frame()
    {
        if (!Running) return;
        try
        {
            Step();
        }
        catch (Exception e)
        {
            Finish($"error stash reset: {e.Message}");
        }
    }

    private static void Finish(string result)
    {
        Result = result;
        s_step = "idle";
        Log.Info("Stash dev reset: " + result);
    }

    private static void Step()
    {
        if (UnityEngine.Time.unscaledTime > s_until)
        {
            Finish($"error stash reset: stuck while {s_step}");
            return;
        }
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        switch (s_step)
        {
            case "leaving":
                if (scene != "CharacterSelectScene" || GameState.InGame) return;
                foreach (string f in Directory.GetFiles(s_saves, "STASH_CYCLE_*")) File.Delete(f);
                foreach (string f in Directory.GetFiles(s_from, "STASH_CYCLE_*")) File.Copy(f, Path.Combine(s_saves, Path.GetFileName(f)));
                s_step = s_character.Length > 0 ? "choosing" : "entering";
                return;
            case "choosing":
            {
                var select = CharacterSelect.instance;
                var tiles = select?.AvailableCharacterTiles;
                if (!Game.Alive(select) || tiles == null || tiles.Count == 0) return;
                // Only into a character of the list the save files belong to.
                if (select!.isOnlineTabShowing)
                {
                    Finish($"error stash reset: {s_snapshot} restored, but character select shows the other character list; nothing entered");
                    return;
                }
                for (int i = 0; i < tiles.Count; i++)
                {
                    var tile = tiles[i];
                    if (!Game.Alive(tile) || !string.Equals(tile.charName?.text?.Trim(), s_character, StringComparison.OrdinalIgnoreCase)) continue;
                    if (select.SelectedCharacterIndex == i) s_step = "entering";
                    else select.OnCharacterTileClicked(tile);
                    return;
                }
                Finish($"error stash reset: {s_snapshot} restored, but no character named {s_character} in the list");
                return;
            }
            case "entering":
            {
                if (CharacterSelect.instance is { } list && Game.Alive(list) && list.isOnlineTabShowing)
                {
                    Finish($"error stash reset: {s_snapshot} restored, but character select shows the other character list; nothing entered");
                    return;
                }
                // The character list's own Enter Game, for the character it has selected.
                var button = UnityEngine.GameObject.Find("Load Character Button")?.GetComponent<UnityEngine.UI.Button>();
                if (!Game.Alive(button) || !button!.interactable) return;
                button.onClick.Invoke();
                s_step = "loading";
                return;
            }
            default:
                if (!GameState.InGame || string.IsNullOrEmpty(PlayerFinder.getPlayerData()?.CharacterName)) return;
                var snap = Stash.Read();
                if (snap is not { ClientCopy: false }) return;
                Finish($"ok stash reset: {s_snapshot} restored; in as {PlayerFinder.getPlayerData()?.CharacterName}; {snap.Folders.Count} folders, {snap.TabCount} tabs, gold {snap.Gold}");
                return;
        }
    }
}
