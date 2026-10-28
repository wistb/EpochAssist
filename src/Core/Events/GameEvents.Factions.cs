using Il2CppLE.Factions;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- Factions: favour, reputation and rank (game facts GF-104) ----

    private const float FactionSeconds = 0.25f;

    private static FactionTracker? s_factionTracker;
    // Online the player's values are on the one ClientFactionInfo (game facts GF-104), found again when it goes.
    private static ClientFactionInfo? s_clientFactions;
    private static float s_factionAt = float.MinValue;
    // The last read per faction (FactionID name): favour, total reputation and rank; empty is a baseline to come.
    private static readonly Dictionary<string, (int Favour, int Reputation, int Rank)> s_lastFactions = new(StringComparer.Ordinal);
    // The last read's rank and progress per faction, for the live figures.
    private static readonly Dictionary<string, (int Rank, double? Percent, long? ToNext, bool Member)> s_factionProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// Polls the player's faction values every 0.25 s while anything subscribes, from the player's FactionTracker offline
    /// and the ClientFactionInfo online (the same Faction members): each faction's Favor (memory amber is the Weaver's),
    /// TotalReputation (the running total: Reputation counts within the rank) and Rank. A change raises FavourChanged
    /// (negative when spent), ReputationGained or RankChanged.
    /// </summary>
    private static void UpdateFactions(Feature feature, bool? online)
    {
        bool active = (Bus.HasSubscribers<FavourChanged>() || Bus.HasSubscribers<ReputationGained>() || Bus.HasSubscribers<RankChanged>())
            && feature.Has(s_factions.Group) && online is bool mode && s_factions.Supports(mode);
        if (active != s_factions.Active)
        {
            s_factions.Active = active;
            s_lastFactions.Clear();
            s_factionProgress.Clear();
        }
        if (!active || Clock.Now - s_factionAt < FactionSeconds) return;
        s_factionAt = Clock.Now;
        Il2CppSystem.Collections.Generic.Dictionary<FactionID, Faction>? factions;
        if (online == true)
        {
            if (!Game.Alive(s_clientFactions))
            {
                s_clientFactions = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<ClientFactionInfo>(), true)?.TryCast<ClientFactionInfo>();
                if (!Game.Alive(s_clientFactions)) return;
            }
            factions = s_clientFactions!.factions;
        }
        else
        {
            if (!Game.Alive(s_factionTracker))
            {
                var player = Player.Object;
                s_factionTracker = player != null ? player.GetComponent<FactionTracker>() : null;
                if (!Game.Alive(s_factionTracker)) return;
            }
            factions = s_factionTracker!.factions;
        }
        if (factions == null) return;
        bool baseline = s_lastFactions.Count == 0;
        foreach (var pair in factions)
        {
            var faction = pair.Value;
            if (faction == null) continue;
            string id = pair.Key.ToString();
            int favour = faction.Favor, reputation = faction.TotalReputation, rank = faction.Rank;
            double percent = faction.ReputationToNextRankPercent;
            // The rank's threshold from its share: the reputation left to the next rank, none at the top.
            long? toNext = percent > 0d && percent < 1d ? (long)Math.Round(faction.Reputation * (1d - percent) / percent) : null;
            s_factionProgress[id] = (rank, percent > 0d && percent < 1d ? percent * 100d : null, toNext, faction.IsMember);
            if (s_lastFactions.TryGetValue(id, out var last) && !baseline)
            {
                if (favour != last.Favour) Bus.Raise(new FavourChanged(id, favour - last.Favour, favour));
                if (reputation > last.Reputation) Bus.Raise(new ReputationGained(id, reputation - last.Reputation, reputation));
                if (rank > last.Rank) Bus.Raise(new RankChanged(id, rank));
            }
            s_lastFactions[id] = (favour, reputation, rank);
        }
    }

    private static readonly List<string> s_rankedFactions = new();

    /// <summary>Factions with a rank or favour at the last read (live figures show them before anything is gained).</summary>
    internal static IReadOnlyCollection<string> RankedFactions()
    {
        s_rankedFactions.Clear();
        if (!s_factions.Active) return s_rankedFactions;
        foreach (var (id, last) in s_lastFactions)
            if (last.Rank > 0 || last.Favour > 0) s_rankedFactions.Add(id);
        return s_rankedFactions;
    }

    /// <summary>A faction's live rank, percent to the next and reputation left, from the last read; null when unread.</summary>
    internal static (int Rank, double? Percent, long? ToNext, bool Member)? FactionProgress(string faction) =>
        s_factions.Active && s_factionProgress.TryGetValue(faction, out var progress) ? progress : null;
}
