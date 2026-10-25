using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

internal static partial class Dispatcher
{
    private static UsingAbilityPlayer? AbilityState()
    {
        if (Game.Alive(s_abilityState)) return s_abilityState;
        var actor = Player.Actor;
        s_abilityState = actor != null ? actor.GetComponent<UsingAbilityPlayer>() : null;
        return s_abilityState;
    }

    // The first slot whose skill button the player is holding right now, or SlotCount if none.
    // Asked of the input system each frame, so a missed key-up can't leave a slot looking held.
    private static int FirstManuallyHeldSlot()
    {
        if (!CanReadManualHolds) return SlotCount;
        try
        {
            if (!Game.Alive(s_rewiredPlayer)) s_rewiredPlayer = Il2CppRewired.ReInput.isReady ? Il2CppRewired.ReInput.players.GetPlayer(0) : null;
            if (s_rewiredPlayer == null) return SlotCount;
            for (int i = 0; i < SlotCount; i++)
            {
                // With a toggle chord held, the skill key toggles instead of casting.
                if (ChordHeld(i)) continue;
                if (s_rewiredPlayer.GetButton(s_slotActionIds[i])) return i;
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("dispatch:held", $"Dispatcher held-skill check failed: {e.Message}", 30);
        }
        return SlotCount;
    }

    private static bool ChordHeld(int slot)
    {
        foreach (var client in s_clients)
        {
            if (client.SlotChordHeld?.Invoke(slot) == true) return true;
        }
        return false;
    }

    private static void SendBarCommand(int slot, bool keyDown, Vector3 target, Transform? hit)
    {
        var processor = Game.AbilityProcessor;
        if (processor == null) return;
        s_sending = true;
        try
        {
            processor.UseBarAbilityCommand(keyDown, slot + 1, -1, target, hit, false);
            if (keyDown) CountPress();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("dispatch:send", $"Dispatcher bar command failed: {e.Message}", 30);
        }
        finally
        {
            s_sending = false;
        }
    }

    private static void SendStopChannel(int slot)
    {
        try
        {
            var clientState = Game.AbilityProcessor?.PlayerAbilityState?.TryCast<ClientUsingAbilityState>();
            if (clientState != null)
            {
                clientState.AttemptToStopAbility(slot + 1, true);
                return;
            }
        }
        catch (Exception)
        {
            // Fall through to the player state fallback.
        }

        try
        {
            AbilityState()?.stopUsingAbility(true);
        }
        catch (Exception)
        {
            // Nothing else to try; the channel ends on its own when the key state settles.
        }
    }

    private static void SendRetarget(Vector3 target)
    {
        try
        {
            AbilityState()?.TryUpdateTargetLocationIfChannelling(target);
        }
        catch (Exception)
        {
            // Target refresh is best-effort.
        }
    }

    // Every skill key press reaches the game through this method (__0 = keyDown, __1 = ability number).
    private static bool OnRealBarCommand(bool keyDown, int abilityNumber)
    {
        int slot = abilityNumber - 1;
        if (slot < 0 || slot >= SlotCount) return true;

        foreach (var client in s_clients)
        {
            if (client.SwallowRealPress?.Invoke(slot, keyDown) == true) return false;
        }
        if (keyDown)
        {
            LastManualPressAt[slot] = Time.unscaledTime;
            PlainPressThisFrame = true;
        }
        return true;
    }
}
