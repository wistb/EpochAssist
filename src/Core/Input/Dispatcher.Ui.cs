using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class Dispatcher
{
    private const float UiWaitSeconds = 1f;
    // An interaction gives up sooner: the feature picks again at its next scan.
    private const float InteractWaitSeconds = 0.5f;
    // What last kept a frame from UI presses, for the capped reason.
    private static string s_uiHeldBy = "none";

    /// <summary>
    /// Asks for a press of the game's UI; it replaces the client's pending one. It runs in a later LateUpdate under the
    /// UI rule set (the global pause rules without dead, game menu open and game paused, plus the client's own), at most
    /// one per frame, never in a frame with an automated key-down or the player's own key or click, and within the send
    /// rate; it counts as a press. Refused at once while a rule holds, and after 1 s without a free frame ("capped").
    /// An interaction runs under every global rule, last in the frame, only with 10 presses of the cap to spare and no
    /// drink landing or held back, and is capped after 0.5 s.
    /// </summary>
    public static void RequestUi(DispatchClient client, UiAction action)
    {
        client.PendingUi = action;
        client.PendingUiAt = Clock.Now;
        client.LastUi = null;
    }

    /// <summary>The client's last UI request's result; null while it is pending or none was asked for.</summary>
    public static UiResult? LastUiResult(DispatchClient client) => client.LastUi;

    // After the skill presses: the first pending UI request that may run, survival clients first.
    private static void RunUi(bool keyDownSent)
    {
        string? held = keyDownSent ? "automated key-down" : ManualInputThisFrame() ? "manual input" : PressesLastSecond >= MaxPressesPerSecond ? "send rate" : null;
        if (held != null) s_uiHeldBy = held;
        // The player's own key or click doesn't hold an interaction: players move by holding the mouse, and a use from
        // in reach doesn't stop a run or a cast. An automated key-down this frame still does.
        string? interactHeldBy = keyDownSent ? "automated key-down" : PressesLastSecond >= MaxPressesPerSecond ? "send rate" : null;
        bool ran = false;
        foreach (var client in s_clients)
        {
            if (client.PendingUi is not { } action) continue;
            bool interact = action is UiAction.Interact;
            if (interact && client.Priority != PriorityClass.Interact)
            {
                EndUi(client, action, UiResult.No("not allowed"));
                continue;
            }
            if (PauseRules.FirstActive(client, forUi: true) is { } rule)
            {
                EndUi(client, action, UiResult.No(rule.Reason));
                continue;
            }
            string? interactHeld = !interact ? null
                : PressesLastSecond > MaxPressesPerSecond - InteractHeadroom ? "send rate headroom"
                : s_survivalWaiting ? "drink waiting"
                : null;
            string? frameHeld = interact ? interactHeldBy : held;
            if (frameHeld != null || ran || interactHeld != null)
            {
                if (Clock.Now - client.PendingUiAt > (interact ? InteractWaitSeconds : UiWaitSeconds))
                    EndUi(client, action, UiResult.No($"capped ({(ran ? "another UI press" : frameHeld != null ? (interact ? frameHeld : s_uiHeldBy) : interactHeld)})"));
                continue;
            }
            ran = true;
            UiResult result;
            try
            {
                result = Press(client, action);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("dispatch:ui", $"Dispatcher UI press failed: {e.Message}", 30);
                result = UiResult.No("failed");
            }
            EndUi(client, action, result);
        }
    }

    private static void EndUi(DispatchClient client, UiAction action, UiResult result)
    {
        client.PendingUi = null;
        client.LastUi = result;
        Log.Debug($"dispatch: {client.Feature.Id} {action.GetType().Name.ToLowerInvariant()} {result}");
        if (action is not UiAction.Interact { Target: var target }) return;
        if (result.Pressed) InteractsPressed++;
        else InteractsRefused++;
        Trace?.Invoke(FormattableString.Invariant($"{client.Name} ui interact {target.Kind} {target.Name} {InteractDistance(target):F2} m: {result} f={Time.frameCount} t={Time.unscaledTime:F3}"));
    }

    // Metres from the player's position this frame to the point the game measures to; infinity when either is gone.
    private static float InteractDistance(InteractTarget target)
    {
        var player = Player.Transform;
        if (!Game.Alive(player) || !Game.Alive(target.Object)) return float.PositiveInfinity;
        return Vector3.Distance(player!.position, target.Point());
    }

    // A key or mouse button the player holds this frame: their input goes first. Input.anyKeyDown reads true on every
    // frame in this game, so it can't be used; anyKey covers keys pressed this frame too.
    private static bool ManualInputThisFrame()
    {
        try
        {
            return Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static UiResult Press(DispatchClient client, UiAction action)
    {
        var feature = FeatureHost.Find("dispatch");
        switch (action)
        {
            case UiAction.Confirm confirm:
            {
                if (feature?.Has(DispatcherFeature.Prompts) != true) return UiResult.No("unavailable");
                if (PromptKinds.OpenModal() is not { } modal) return UiResult.No("no prompt");
                var kind = PromptKinds.KindOf(modal);
                // Never the background shadow or the close button: only Yes, on a kind the client may confirm.
                if (!kind.Confirmable || !confirm.Kinds.Contains(kind.Id)) return UiResult.No("kind not allowed");
                return PressButton(modal._yesButton);
            }
            case UiAction.Decline:
            {
                if (feature?.Has(DispatcherFeature.Prompts) != true) return UiResult.No("unavailable");
                return PromptKinds.OpenModal() is { } modal ? PressButton(modal._noButton) : UiResult.No("no prompt");
            }
            case UiAction.Respawn:
            {
                if (feature?.Has(DispatcherFeature.RespawnGroup) != true) return UiResult.No("unavailable");
                var screen = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<DeathScreen>())?.TryCast<DeathScreen>();
                if (!Game.Alive(screen) || !screen!.gameObject.activeInHierarchy) return UiResult.No("no death screen");
                return PressButton(screen.normalRespawnButton);
            }
            case UiAction.Interact { Target: var target }:
            {
                // Checked again here, with this frame's position: the call walks the character from out of reach.
                if (!Game.Alive(target.Object)) return UiResult.No("gone");
                if (target.Used()) return UiResult.No("used");
                if (!(InteractDistance(target) <= target.Reach)) return UiResult.No("out of reach");
                target.Use();
                CountPress();
                return UiResult.Done;
            }
            case UiAction.QuickMove move:
            {
                if (feature?.Has(DispatcherFeature.QuickMoveGroup) != true) return UiResult.No("unavailable");
                if (!Enum.TryParse(move.Container, true, out ContainerID id)) return UiResult.No($"unknown container {move.Container}");
                var container = ContainerUi(id);
                if (container == null) return UiResult.No("container not open");
                if (!container.TryQuickMove(move.Position, out bool _)) return UiResult.No("not moved");
                CountPress();
                return UiResult.Done;
            }
            default:
                return UiResult.No("unknown action");
        }
    }

    // What a click does: the button's own onClick, only while it is there, shown and interactable.
    private static UiResult PressButton(UnityEngine.UI.Button? button)
    {
        if (!Game.Alive(button)) return UiResult.No("button missing");
        if (!button!.isActiveAndEnabled || !button.interactable) return UiResult.No("not interactable");
        button.onClick.Invoke();
        CountPress();
        return UiResult.Done;
    }

    private static ItemContainerUI? ContainerUi(ContainerID id)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ItemContainerUI>()))
        {
            var ui = obj.TryCast<ItemContainerUI>();
            if (Game.Alive(ui) && ui!.gameObject.activeInHierarchy && ui.identifier == id) return ui;
        }
        return null;
    }
}
