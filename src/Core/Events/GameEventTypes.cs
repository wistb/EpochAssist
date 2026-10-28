using System.Text;

namespace EpochAssist.Core;

/// <summary>A hit the player took: one health drop in a frame, the same in both modes (GF-47).</summary>
internal readonly record struct HitTaken(float Amount);

/// <summary>XP the player gained, and the tracker's value after it.</summary>
internal readonly record struct XpGained(long Amount, long Total);

/// <summary>A change of the player's gold (a spend is negative), and the total after it.</summary>
internal readonly record struct GoldChanged(int Amount, int Total);

/// <summary>
/// An item that dropped in this scene (gold piles, potions, tomes and bones aren't items). Shown is whether the player's
/// loot filter shows it (its label's last result, by rule number Rule); null when no filter is active or the result
/// couldn't be read in time. Raised once the filter has judged the item, at most 2 s after it appeared. Key identifies the
/// item by its packed data (a hash of `ItemData.id`), so the same item dropped again isn't reported again; null when it
/// couldn't be read.
/// </summary>
internal readonly record struct ItemDropped(string? Name, string? BaseName, int Rarity, string RarityName, uint Id, bool? Shown = null, int? Rule = null,
    string? Key = null);

/// <summary>What killed the player, from the death screen's text, after a PlayerDied; all null when it didn't say.</summary>
internal readonly record struct DeathCause(string? Killer, string[]? Types, string? Text)
{
    private bool PrintMembers(StringBuilder text)
    {
        text.Append($"Killer = {Killer}, Types = {GameEvents.List(Types)}, Text = {Text?.Replace("\n", " / ")}");
        return true;
    }
}

/// <summary>The echo's objective was completed.</summary>
internal readonly record struct EchoCompleted(string Scene);

/// <summary>The player entered an echo chosen on the monolith; Reward is its echo reward's title ("Exalted Relics"),
/// null when it has none or it couldn't be read; Corruption the monolith's corruption at the entry (0 outside empowered
/// monoliths), null when unread.</summary>
internal readonly record struct MonolithPick(string? Modifier, int StabilityReward, int HexIndex, string? Reward = null, int? Corruption = null);

/// <summary>The player reached a level: the level before it + 1, whichever of the game's values updated first.</summary>
internal readonly record struct LevelUp(int Level);

/// <summary>A faction's favour changed (negative when spent) and its total after; Faction is the FactionID name. Memory
/// amber is the Weaver's (TheWeaver) favour.</summary>
internal readonly record struct FavourChanged(string Faction, int Amount, int Total);

/// <summary>Reputation gained with a faction, and its running total after.</summary>
internal readonly record struct ReputationGained(string Faction, int Amount, int Total);

/// <summary>A faction's rank rose to Rank.</summary>
internal readonly record struct RankChanged(string Faction, int Rank);
