using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    public const string TogglesGroup = "player: skill toggles";

    private const float ToggleScanSeconds = 0.25f;
    private static float s_togglesScannedAt = float.NegativeInfinity;
    private static GameObject? s_togglesRoot;

    // A switched-on toggle skill (Aura of Decay) keeps its object under the player's sync object, and the object's
    // creation references name the skill; switched off, the object goes. Only that layout was seen, so a player without
    // a sync object reads as failed rather than "off".
    private static void FillToggles(List<string> active)
    {
        if (s_object == null) throw new InvalidOperationException("no player object");
        float now = Time.unscaledTime;
        if (s_togglesRoot == s_object && now - s_togglesScannedAt < ToggleScanSeconds) return;
        Transform root = s_object.transform.root;
        if (!Game.Alive(root.GetComponentInChildren<ActorSync>(false))) throw new InvalidOperationException("no player sync object");
        s_togglesRoot = s_object;
        s_togglesScannedAt = now;
        active.Clear();
        var found = root.GetComponentsInChildren<CreationReferences>(false);
        for (int i = 0; i < found.Length; i++)
        {
            string? id = found[i]?.thisAbility?.playerAbilityID;
            if (id is { Length: > 0 } && !active.Contains(id)) active.Add(id);
        }
    }

    /// <summary>Whether a toggle skill is switched on; null when it can't be read.</summary>
    public static bool? Toggled(string abilityId) => Read(s_toggles, "skill toggles") ? s_toggles.Value.Contains(abilityId) : null;
}
