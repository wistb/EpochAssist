using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.InteractSettings;

namespace EpochAssist.Features;

/// <summary>
/// Uses what the character is already within the game's own reach of: shrines, chests and chosen loot, one at a time
/// at a varying pace, through the dispatcher, never moving the character. It only picks; the dispatcher checks reach
/// again and makes the game's own click call.
/// </summary>
internal static partial class Interact
{
    private const float ScanSeconds = 0.1f;
    private const float ConfirmSeconds = 1f;
    // A request with no result after this long is dropped (the dispatcher ends every request well before).
    private const float RequestTimeoutSeconds = 2f;
    private const float ListedScanSeconds = 0.5f;
    // Listed objects farther than this aren't kept between listener scans.
    private const float ListedKeepRange = 30f;
    private const int RefusalsToHold = 3;

    private enum Phase { Idle, Pending, Confirming }

    // One thing within reach this scan; picked by distance, ties by the time it came into reach.
    private struct Candidate
    {
        public InteractKind Kind;
        public long Key;
        public float Distance;
        public float Reach;
        public string? Name;
        public WorldObjectClickListener? Listener;
        public GroundItemVisuals? Visual;
        public uint ItemId;
    }

    // An item of an enabled stackable kind within reach when a pick-up was asked for: the game's pick-up of one
    // stackable takes the ones lying near it too, so each of these that goes counts.
    private readonly record struct Swept(GroundItemVisuals Visual, uint Id, InteractKind Kind);

    // Everything one run changes. Made in Start, dropped in Shutdown, cleared on zone change.
    private sealed class Run
    {
        public Feature Feature = null!;
        public DispatchClient Client = null!;
        public Phase Phase;
        public Candidate Current;
        public InteractTarget? Target;
        public float AskedAt, ConfirmBy, NextAt;
        public readonly List<Swept> Sweep = new();
        public readonly Dictionary<IntPtr, WorldObjectClickListener?> Listeners = new();
        public readonly Dictionary<IntPtr, InteractKind?> ListedKinds = new();
        public readonly List<(WorldObjectClickListener Listener, InteractKind Kind)> Listed = new();
        public float NextListedScan;
        public readonly Dictionary<long, string> Skipped = new();
        public readonly Dictionary<long, float> InReachSince = new();
        public Candidate Best;
        public bool HasBest;
        public int ItemRefusals;
        public bool ItemsHeld;
        public int? HeldCount;
        public bool NotifiedFull;
        public readonly Dictionary<string, int> ZoneCounts = new();
        // The game's own reach seen per object kind this zone (shrines 2.5 m, caches 3 m, ...), for the page and the ring.
        public readonly Dictionary<string, (float Min, float Max)> GameReach = new();
    }

    private static Run? s_run;
    // Kept across runs and reloads of the feature, cleared at character select or the main menu.
    private static readonly Dictionary<string, int> s_sessionCounts = new();

    /// <summary>The dispatcher's view of the feature while it runs.</summary>
    public static DispatchClient? Client => s_run?.Client;

    public static void Start(Feature feature)
    {
        var run = new Run { Feature = feature };
        // The last class: an interaction never takes a frame or a press a potion or cast wants. Besides the dispatcher's
        // global rules, only the player's own hold key holds it (none of the Macros pauses).
        run.Client = Dispatcher.Register(new DispatchClient(feature, PriorityClass.Interact, S.Enabled, new[]
        {
            new PauseRule("hold-key", HoldReason, RuleKind.Momentary, HoldActive),
        }));
        s_run = run;
        Scheduler.Every(feature, "scan", ScanSeconds, () => { if (s_run == run) Scan(run); }, startNow: true);
        Bus.Subscribe<ZoneChange>(feature, _ => ClearZone(run));
        Actions.Register(feature.Id, "interact.toggle", "Proximity interact", () => S.Enabled.Value = !S.Enabled.Value, () => S.Enabled.Value);
    }

    public static void Shutdown()
    {
        if (s_run?.Client is { } client) Dispatcher.Unregister(client);
        s_run = null;
    }

    private static void ClearZone(Run run)
    {
        run.Phase = Phase.Idle;
        run.Target = null;
        run.Sweep.Clear();
        run.Listeners.Clear();
        run.ListedKinds.Clear();
        run.Listed.Clear();
        run.NextListedScan = 0f;
        run.Skipped.Clear();
        run.InReachSince.Clear();
        run.ItemRefusals = 0;
        run.ItemsHeld = false;
        run.HeldCount = null;
        run.NotifiedFull = false;
        run.ZoneCounts.Clear();
        run.GameReach.Clear();
    }

    /// <summary>Whether a kind's switch is on and the game members it needs are there.</summary>
    public static bool KindOn(InteractKind kind) => kind.Toggle.Value && Available(kind);

    public static bool Available(InteractKind kind) => s_run?.Feature.Has(kind.Group) == true;

    private static bool AnyKindOn()
    {
        foreach (var kind in Kinds)
        {
            if (KindOn(kind)) return true;
        }
        return false;
    }

    // The hold key, per its mode: held while it is down (pause), or while it is up (use). Unbound, it holds nothing.
    private static bool HoldActive()
    {
        if (S.HoldKey.Value.Length == 0) return false;
        bool down = Hotkeys.Held(S.HoldKey);
        return S.HoldMode.Value == S.HoldUses ? !down : down;
    }

    private static string HoldReason() => S.HoldMode.Value == S.HoldUses ? $"waiting for {S.HoldKey.Display}" : $"{S.HoldKey.Display} held";

    /// <summary>Held by the "Not in town" choice: the player is in a town or other non-combat zone.</summary>
    public static bool HeldInTown => S.Where.Value == S.NotInTown && GameState.Kind == SceneKind.NonCombat;

    private static void Scan(Run run)
    {
        if (GameState.Kind == SceneKind.Menu) s_sessionCounts.Clear();
        Advance(run);
        if (run.ItemsHeld && run.Feature.Has(InventoryGroup) && InventoryCount() is int count && count != run.HeldCount)
        {
            run.ItemsHeld = false;
            run.ItemRefusals = 0;
        }
        if (!S.Enabled.Value || run.Phase != Phase.Idle || Clock.Now < run.NextAt || HeldInTown || !AnyKindOn()) return;
        if (!Dispatcher.Allowed(run.Client) || Player.Transform is not { } player || !Game.Alive(player)) return;
        Pick(run, player.position);
        if (run.HasBest) Ask(run, run.Best, player.position);
    }

    // Moves the one interaction on: its dispatcher result, then the effect it should have within a second.
    private static void Advance(Run run)
    {
        float now = Clock.Now;
        if (run.Phase == Phase.Pending)
        {
            if (Dispatcher.LastUiResult(run.Client) is not { } result)
            {
                if (now - run.AskedAt > RequestTimeoutSeconds) End(run, now);
                return;
            }
            if (result.Pressed)
            {
                run.Phase = Phase.Confirming;
                run.ConfirmBy = now + ConfirmSeconds;
                return;
            }
            // Gone, used, out of reach, capped or a rule: picked again at a later scan. Only a failed call is a refusal.
            if (result.Refused == "failed") Refuse(run, "failed");
            End(run, now);
            return;
        }
        if (run.Phase != Phase.Confirming || run.Target == null) return;
        if (run.Target.Used())
        {
            Confirmed(run);
            End(run, now);
        }
        else if (now >= run.ConfirmBy)
        {
            Refuse(run, "no effect");
            End(run, now);
        }
    }

    // The next request waits the minimum wait and up to half of it again at random, so uses don't fall into a rhythm.
    private static void End(Run run, float now)
    {
        run.Phase = Phase.Idle;
        run.Target = null;
        run.Sweep.Clear();
        run.NextAt = now + S.MinWait.Value * (1f + UnityEngine.Random.Range(0f, 0.5f));
    }

    private static void Confirmed(Run run)
    {
        var kind = run.Current.Kind;
        foreach (var swept in run.Sweep)
        {
            if (ItemGone(swept.Visual, swept.Id)) Count(run, swept.Kind, 1);
        }
        Count(run, kind, 1);
        if (kind.IsItem) run.ItemRefusals = 0;
        // An object used once is left alone for the rest of the zone, even one the game would let trigger again.
        else run.Skipped[run.Current.Key] = "used";
        if (!S.NotifyEach.Value) return;
        string name = run.Current.Name ?? kind.One;
        Toasts.Show("interact", kind.IsItem ? $"Picked up {name}" : $"Used {name}", ToastKind.Info, repeats: true);
    }

    private static void Count(Run run, InteractKind kind, int n)
    {
        run.ZoneCounts[kind.Id] = run.ZoneCounts.GetValueOrDefault(kind.Id) + n;
        s_sessionCounts[kind.Id] = s_sessionCounts.GetValueOrDefault(kind.Id) + n;
    }

    // An object the game refused or didn't act on is left alone for the rest of the zone. Three item refusals in a row
    // mean the bag is full: every item kind waits until the bag's count changes or the zone does.
    private static void Refuse(Run run, string reason)
    {
        run.Skipped[run.Current.Key] = reason;
        Log.Info($"Interact: {run.Current.Kind.Id} {run.Current.Name ?? "?"} skipped for this zone: {reason}");
        if (!run.Current.Kind.IsItem || ++run.ItemRefusals < RefusalsToHold) return;
        run.ItemsHeld = true;
        run.HeldCount = run.Feature.Has(InventoryGroup) ? InventoryCount() : null;
        if (run.NotifiedFull) return;
        run.NotifiedFull = true;
        Toasts.Show("interact", "Inventory full: picking up paused", ToastKind.Warning);
    }

    private static void Ask(Run run, Candidate best, Vector3 player)
    {
        best.Name ??= NameOf(best);
        run.Current = best;
        run.Target = best.Listener is { } listener ? ObjectTarget(best.Kind, best.Name, listener, best.Reach)
            : ItemTarget(best.Kind, best.Name, best.Visual!, best.ItemId, best.Reach);
        run.Sweep.Clear();
        if (best.Kind == Materials) SweepAround(run, player, best.Reach);
        run.Phase = Phase.Pending;
        run.AskedAt = Clock.Now;
        Dispatcher.RequestUi(run.Client, new UiAction.Interact(run.Target));
    }

    // The other enabled stackables within reach, besides the one asked for, to count those the pick-up takes along.
    private static void SweepAround(Run run, Vector3 player, float reach)
    {
        var all = GroundItemVisuals.all;
        if (all == null) return;
        float reach2 = reach * reach;
        for (int i = 0; i < all.Count; i++)
        {
            var visual = all[i];
            if (!Game.Alive(visual) || visual == run.Current.Visual || !visual.gameObject.activeInHierarchy) continue;
            if (ItemKind(visual.itemData) is not { } kind || kind != Materials || !kind.Toggle.Value) continue;
            if ((visual.transform.position - player).sqrMagnitude <= reach2) run.Sweep.Add(new Swept(visual, visual.id, kind));
        }
    }

    private static string NameOf(Candidate c)
    {
        if (c.Visual is { } visual && Game.Alive(visual)) return GameData.Item(visual.itemData).Name ?? c.Kind.One;
        return c.Kind.One;
    }

    // The nearest candidate of every enabled kind into run.Best; HasBest is false when nothing is within reach.
    private static void Pick(Run run, Vector3 player)
    {
        run.HasBest = false;
        if (KindOn(Shrines) || KindOn(Chests) || KindOn(RunePrisons) || KindOn(Forges)) OfferObjects(run, player);
        if (!run.ItemsHeld && (KindOn(Keys) || KindOn(Materials) || KindOn(Filtered))) OfferItems(run, player);
    }

    // Nearest wins; within a centimetre, the one that came into reach first.
    private static void Offer(Run run, in Candidate c)
    {
        if (run.Skipped.ContainsKey(c.Key)) return;
        if (!run.InReachSince.TryGetValue(c.Key, out float since)) run.InReachSince[c.Key] = since = Clock.Now;
        if (run.HasBest)
        {
            var b = run.Best;
            bool nearer = c.Distance < b.Distance - 0.01f;
            bool earlier = Math.Abs(c.Distance - b.Distance) <= 0.01f && since < run.InReachSince[b.Key];
            if (!nearer && !earlier) return;
        }
        run.Best = c;
        run.HasBest = true;
    }

    private static void OfferObjects(Run run, Vector3 player)
    {
        if (Player.Object is not { } me || !Game.Alive(me)) return;
        bool shrines = KindOn(Shrines), chests = KindOn(Chests);
        if (shrines) World.Want(WorldKind.Shrine);
        if (chests) World.Want(WorldKind.Chest);
        foreach (var entry in World.Entries)
        {
            if (entry.Gone || entry.Used == true) continue;
            var kind = entry.Kind == WorldKind.Shrine && shrines ? Shrines : entry.Kind == WorldKind.Chest && chests ? Chests : null;
            if (kind == null || ListenerOf(run, entry) is not { } listener) continue;
            OfferListener(run, kind, entry.Name, listener, player, me);
        }
        if (!chests && !KindOn(RunePrisons) && !KindOn(Forges)) return;
        if (Clock.Now >= run.NextListedScan)
        {
            run.NextListedScan = Clock.Now + ListedScanSeconds;
            FindListed(run, player);
        }
        foreach (var (listener, kind) in run.Listed)
        {
            if (Game.Alive(listener) && KindOn(kind)) OfferListener(run, kind, null, listener, player, me);
        }
    }

    private static void OfferListener(Run run, InteractKind kind, string? name, WorldObjectClickListener listener, Vector3 player, GameObject me)
    {
        if (!listener.isActiveAndEnabled) return;
        float game = listener.interactionRange;
        run.GameReach[kind.Id] = run.GameReach.TryGetValue(kind.Id, out var seen) ? (Math.Min(seen.Min, game), Math.Max(seen.Max, game)) : (game, game);
        float reach = Math.Min(S.Reach.Value, game) - ReachMargin;
        float distance = Vector3.Distance(player, listener.transform.position);
        if (distance > reach || ListenerUsed(listener)) return;
        // Objects that open a prompt, need a key or start an encounter aren't ready for the player.
        var handler = listener.handler;
        if (Game.Alive(handler) && !handler!.CheckConditionReadiness(me)) return;
        Offer(run, new Candidate { Kind = kind, Key = listener.Pointer.ToInt64(), Distance = distance, Reach = reach, Name = name, Listener = listener });
    }

    // Echo caches, reward chests and rune prisons near the player, from the game's own listener list (no scene search).
    // Each listener is looked at once per zone.
    private static void FindListed(Run run, Vector3 player)
    {
        run.Listed.Clear();
        var all = WorldObjectClickListener.allListener;
        if (all == null) return;
        float keep2 = ListedKeepRange * ListedKeepRange;
        for (int i = 0; i < all.Count; i++)
        {
            var listener = all[i];
            if (!Game.Alive(listener) || !listener.isActiveAndEnabled) continue;
            if ((listener.transform.position - player).sqrMagnitude > keep2) continue;
            if (!run.ListedKinds.TryGetValue(listener.Pointer, out var kind)) run.ListedKinds[listener.Pointer] = kind = ListedKind(listener);
            if (kind != null) run.Listed.Add((listener, kind));
        }
    }

    // Ground items: listed by the game, label shown, within the one pick-up reach. A kind switch takes its kind; the
    // filter switch takes items the active filter judged and shows; an item the filter hides is never taken.
    private static void OfferItems(Run run, Vector3 player)
    {
        var all = GroundItemVisuals.all;
        if (all == null) return;
        float reach = Math.Min(S.Reach.Value, ItemReach) - ReachMargin;
        float reach2 = reach * reach;
        bool filterOn = KindOn(Filtered) && GroundItems.FilterActive;
        for (int i = 0; i < all.Count; i++)
        {
            var visual = all[i];
            if (!Game.Alive(visual) || !visual.gameObject.activeInHierarchy) continue;
            float d2 = (visual.transform.position - player).sqrMagnitude;
            if (d2 > reach2) continue;
            var label = visual.label;
            if (!Game.Alive(label) || !label!.gameObject.activeInHierarchy || GroundItems.Read(visual) is not { Listed: true } read) continue;
            if (read.Rule > 0 && read.Hidden) continue;
            var kind = ItemKind(visual.itemData) is { } own && KindOn(own) ? own : filterOn && read.Rule > 0 ? Filtered : null;
            if (kind == null) continue;
            Offer(run, new Candidate { Kind = kind, Key = -((long)visual.id + 1), Distance = Mathf.Sqrt(d2), Reach = reach, Visual = visual, ItemId = visual.id });
        }
    }

    /// <summary>The status words: the holding rule's reason, then the feature's own state.</summary>
    public static FeatureStatus StatusOf(DispatchClient client)
    {
        var run = s_run;
        if (run == null || client.Blocking != null) return Dispatcher.StatusOf(client);
        if (HeldInTown) return new FeatureStatus(StatusState.Waiting, "Not in town", null);
        if (!AnyKindOn()) return new FeatureStatus(StatusState.Waiting, "No kinds on", null);
        if (run.Phase != Phase.Idle) return new FeatureStatus(StatusState.Active, $"Using {run.Current.Name ?? run.Current.Kind.One}", null);
        if (run.ItemsHeld && !KindOn(Shrines) && !KindOn(Chests)) return new FeatureStatus(StatusState.Holding, "Inventory full", null);
        if (Clock.Now < run.NextAt) return new FeatureStatus(StatusState.Active, "Waiting", null);
        return new FeatureStatus(StatusState.Active, run.ItemsHeld ? "Inventory full" : "Ready", null);
    }

    /// <summary>The page's status line.</summary>
    public static (string, StatusState?) StatusText()
    {
        if (Client is not { } client) return ("Off", StatusState.Off);
        var status = StatusOf(client);
        return (status.Detail.Length > 0 ? status.Detail : "Ready", status.State);
    }

    /// <summary>The counts table: one line per kind used this session, this zone's count and the session's.</summary>
    public static string CountsText()
    {
        if (s_sessionCounts.Count == 0) return "Nothing used this session.";
        var zone = s_run?.ZoneCounts;
        var lines = new List<string>();
        foreach (var kind in Kinds)
        {
            if (!s_sessionCounts.TryGetValue(kind.Id, out int session)) continue;
            lines.Add($"{kind.Label}: this zone {zone?.GetValueOrDefault(kind.Id) ?? 0}, session {session}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>The game's reach per kind for the page: items share one; objects as seen this zone.</summary>
    public static string ReachText()
    {
        var parts = new List<string>();
        foreach (var kind in Kinds)
        {
            if (!Available(kind)) parts.Add($"{kind.Label.ToLowerInvariant()} unavailable: the game changed");
            else if (kind.IsItem) continue;
            else if (s_run?.GameReach.TryGetValue(kind.Id, out var r) == true)
                parts.Add(r.Min == r.Max ? FormattableString.Invariant($"{kind.Label.ToLowerInvariant()} {r.Min:0.0} m")
                    : FormattableString.Invariant($"{kind.Label.ToLowerInvariant()} {r.Min:0.0}–{r.Max:0.0} m"));
        }
        if (Available(Keys)) parts.Add(FormattableString.Invariant($"items {ItemReach:0.0} m"));
        return parts.Count > 0 ? string.Join(", ", parts) : "not seen yet in this zone";
    }

    /// <summary>Uses this session, every kind together.</summary>
    public static int SessionTotal
    {
        get
        {
            int total = 0;
            foreach (var n in s_sessionCounts.Values) total += n;
            return total;
        }
    }

    /// <summary>The run's state for the dev `interact` verb: switches, reach, the pending request, pace, counts and the
    /// refusal memory.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        var run = s_run;
        if (run == null)
        {
            lines.Add("not running");
            return lines;
        }
        var status = StatusOf(run.Client);
        lines.Add($"master {(S.Enabled.Value ? "on" : "off")}, status {status.State} \"{status.Detail}\", holding rule {run.Client.Blocking?.Name ?? "none"}, where {S.Where.Value}");
        foreach (var kind in Kinds)
        {
            string game = !Available(kind) ? "unavailable" : kind.IsItem ? $"game reach {ItemReach:F2} m" : "game reach per object";
            float own = kind.IsItem ? Math.Min(S.Reach.Value, ItemReach) - ReachMargin : S.Reach.Value - ReachMargin;
            lines.Add($"  {kind.Id.PadRight(10)} {(kind.Toggle.Value ? "on " : "off")} {game}, effective {(kind.IsItem ? own.ToString("F2") + " m" : "min(" + S.Reach.Value.ToString("F1") + ", object) - " + ReachMargin + " m")}");
        }
        lines.Add($"phase {run.Phase}{(run.Phase != Phase.Idle ? $" {run.Current.Kind.Id} {run.Current.Name} at {run.Current.Distance:F2} m (reach {run.Current.Reach:F2})" : string.Empty)}, "
            + $"next allowed in {Math.Max(0f, run.NextAt - Clock.Now):F2} s, last result {Dispatcher.LastUiResult(run.Client)?.ToString() ?? "none"}");
        lines.Add($"counts zone [{string.Join(", ", run.ZoneCounts.Select(c => $"{c.Key} {c.Value}"))}], session [{string.Join(", ", s_sessionCounts.Select(c => $"{c.Key} {c.Value}"))}]");
        lines.Add($"items held {run.ItemsHeld} (bag {run.HeldCount?.ToString() ?? "?"} now {(run.Feature.Has(InventoryGroup) ? InventoryCount()?.ToString() ?? "?" : "unread")}), refusals in a row {run.ItemRefusals}, listed objects near {run.Listed.Count}");
        foreach (var skipped in run.Skipped) lines.Add($"  skipped {skipped.Key}: {skipped.Value}");
        return lines;
    }
}
