using Il2CppInterop.Runtime;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>A game prompt kind: its panel type and title (the game's English text, matched exactly), and whether it may
/// ever be confirmed.</summary>
internal sealed record PromptKind(string Id, string Panel, string Title, bool Confirmable);

/// <summary>
/// The game prompts the mod knows. Confirm presses Yes only on a known, confirmable kind the request names;
/// prompts that can't be undone are listed as never confirmable. A feature adds confirmable kinds with the feature that
/// needs them, each checked in game.
/// </summary>
internal static class PromptKinds
{
    public const string ConfirmationPanel = "ConfirmationModal";

    private static readonly List<PromptKind> s_all = new()
    {
        new("exit-to-login", ConfirmationPanel, "Exit to Login", false),
        new("exit-to-desktop", ConfirmationPanel, "Are you sure you want to logout?", false),
#if DEV
        new("dev-test", ConfirmationPanel, DevTestTitle, true),
#endif
    };

#if DEV
    public const string DevTestTitle = "Epoch Assist dev test";
#endif

    public static IReadOnlyList<PromptKind> All => s_all;

    /// <summary>The open prompt's modal, or null with none open.</summary>
    internal static Il2CppLE.UI.PanelSystem.ModalTemplate? OpenModal()
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppLE.UI.PanelSystem.ModalTemplate>()))
        {
            var modal = obj.TryCast<Il2CppLE.UI.PanelSystem.ModalTemplate>();
            if (Game.Alive(modal) && modal!.gameObject.activeInHierarchy) return modal;
        }
        return null;
    }

    /// <summary>The open prompt's kind, an "unknown" kind for one not on the list, or null with none open.</summary>
    public static PromptKind? Open => OpenModal() is { } modal ? KindOf(modal) : null;

    internal static PromptKind KindOf(Il2CppLE.UI.PanelSystem.ModalTemplate modal)
    {
        string title = Game.Alive(modal._title) ? modal._title.text ?? string.Empty : string.Empty;
        string panel = PanelOf(modal);
        return s_all.Find(k => k.Panel == panel && k.Title == title) ?? new PromptKind("unknown", panel, title, false);
    }

    // The modal panel's type is the component next to the template ("ConfirmationModal", "WarningConfirmationModal", …).
    private static string PanelOf(Il2CppLE.UI.PanelSystem.ModalTemplate modal)
    {
        foreach (var component in modal.GetComponents<Component>())
        {
            string name = component.GetIl2CppType().Name;
            if (name.EndsWith("Modal", StringComparison.Ordinal)) return name;
        }
        return "unknown";
    }
}
