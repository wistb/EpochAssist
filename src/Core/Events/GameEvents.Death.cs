using System.Text.RegularExpressions;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- Death cause ----

    private static Subscription? s_deathSubscription;
    private static string? s_deathTextBefore;
    private static float s_deathAt = -1f;

    private static void UpdateDeath(Feature feature, bool? online)
    {
        bool active = Wanted<DeathCause>(feature, s_death, online);
        if (active != s_death.Active)
        {
            s_death.Active = active;
            s_deathAt = -1f;
            s_deathSubscription?.Dispose();
            s_deathSubscription = null;
            if (active)
            {
                // The text changes only at a death, so it is read here and after each cause, not every frame.
                s_deathTextBefore = DeathInformationText.mostRecentDeathInfo;
                s_deathSubscription = Bus.Subscribe<PlayerDied>(feature, _ => s_deathAt = Clock.Now);
            }
        }
        if (!active || s_deathAt < 0f) return;

        string? text = DeathInformationText.mostRecentDeathInfo;
        bool changed = !string.Equals(text, s_deathTextBefore, StringComparison.Ordinal) && !string.IsNullOrEmpty(text);
        if (!changed && Clock.Now - s_deathAt < DeathTextSeconds) return;
        s_deathAt = -1f;
        if (!changed)
        {
            Bus.Raise(new DeathCause(null, null, null));
            return;
        }
        s_deathTextBefore = text;
        Bus.Raise(ParseDeath(text!));
    }

    private static readonly Regex s_tags = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] s_typeSeparators = { ", ", " and " };

    // "You have been slain by a Melee Attack from an Imperial Assassin\nKilling Blow Damage Types: Necrotic and
    // Physical\n…": the killer after the first line's last " from " (and its a/an), the types after
    // "Damage Type(s): ".
    internal static DeathCause ParseDeath(string raw)
    {
        string text = s_tags.Replace(raw, string.Empty).Trim();
        string[] lines = text.Split('\n');
        string first = lines[0];
        // Unique enemies have no article ("…a Meteorite from Radamasa the Cursed").
        int from = first.LastIndexOf(" from ", StringComparison.Ordinal);
        string? killer = null;
        if (from >= 0)
        {
            string rest = first[(from + " from ".Length)..].Trim();
            if (rest.StartsWith("an ", StringComparison.Ordinal)) rest = rest[3..];
            else if (rest.StartsWith("a ", StringComparison.Ordinal)) rest = rest[2..];
            killer = NullIfEmpty(rest);
        }
        string[]? types = null;
        foreach (string line in lines)
        {
            int at = line.IndexOf("Damage Types: ", StringComparison.Ordinal);
            int length = "Damage Types: ".Length;
            if (at < 0)
            {
                at = line.IndexOf("Damage Type: ", StringComparison.Ordinal);
                length = "Damage Type: ".Length;
            }
            if (at < 0) continue;
            types = line[(at + length)..].Split(s_typeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant()).ToArray();
            if (types.Length == 0) types = null;
            break;
        }
        return new DeathCause(killer, types, NullIfEmpty(text));
    }
}
