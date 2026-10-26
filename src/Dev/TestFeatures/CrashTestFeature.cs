using EpochAssist.Core;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: throws when armed by the crash command, so faults, failed starts and crash reports can be checked in game.
/// Fault: its next five updates throw the same error (the host faults it). Start: its next start throws. Handler: its
/// next n ZoneChange handler calls throw, so a subscriber's error can be seen to land on its owner. It also reads session
/// history and the colour roles through their APIs alone, as a later feature would, for the reload and fault checks.
/// </summary>
internal sealed class CrashTestFeature : Feature
{
    private static int s_updateThrows;
    private static string? s_startThrow;
    private static int s_handlerThrows;

    public override string Id => "crashtest";
    public override string Name => "Crash test";

    /// <summary>The members of the dev `edits` test targets; without them `edits test` refuses.</summary>
    public const string EditsGroup = "crashtest: edits";

    public override Requirement[] Requirements => new[]
    {
        Need<Il2CppLE.UI.Minimap.Minimap>(nameof(Il2CppLE.UI.Minimap.Minimap.RevealRadius), EditsGroup),
        Need<Il2CppLE.UI.Minimap.MinimapIconManager>(nameof(Il2CppLE.UI.Minimap.MinimapIconManager.minimap), EditsGroup),
        Need<Il2Cpp.DMMapZoom>(nameof(Il2Cpp.DMMapZoom.maxMinimapZoom), EditsGroup),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.instance), EditsGroup),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.zoomMin), EditsGroup),
    };

    // Always on, never saved: the dev UI client has no switch of its own.
    private static readonly Toggle s_uiOn = new(new Section(new SettingsGroup("crashtest", "Crash test"), "ui", "Dev UI actions"), "on", "On", true);

    /// <summary>The dev client for UI action requests (`ui`): skills class, no rules of its own.</summary>
    public static DispatchClient? UiClient
    {
        get
        {
            // A dispatcher restart drops its clients; this one registers again.
            if (s_uiClient != null && !Dispatcher.Clients.Contains(s_uiClient)) Dispatcher.Register(s_uiClient);
            return s_uiClient;
        }
    }
    private static DispatchClient? s_uiClient;

    // A dev-only page in the Dev tab: a list row over an in-memory list (nothing saved), for checking the list row by
    // mouse, keys, search and the bridge.
    public override MenuEntry? Menu => s_menu;

    private sealed class TestEntry
    {
        public int Kind;
        public float Value;
    }

    private static readonly string[] s_kinds = { "Health below", "Mana below", "Enemies within" };
    private static readonly List<TestEntry> s_entries = new()
    {
        new() { Kind = 0, Value = 40f }, new() { Kind = 1, Value = 25f }, new() { Kind = 2, Value = 10f },
    };

    // A condition list of its own, the layer's second user: kept out of profiles and exports (shared), checked on its
    // page with its own enemy tally, and reachable as the dev cond list "crashtest".
    private static readonly SettingsGroup s_conditionGroup = new("EpochAssist_CrashTest", "Crash test") { ActionKey = "crashtest", Shared = true };
    private static readonly ConditionSetting s_conditions = new(s_conditionGroup.Section("conditions", "Conditions"), "DevConditions", "Conditions", "{\"v\":1,\"lists\":{}}");
    private const string ConditionKey = "crashtest";
    private static readonly WorldTally s_tally = new();
    private static readonly List<float> s_ranges = new();
    private static readonly ConditionResult[] s_results = new ConditionResult[16];
    private static ListVerdict s_verdict = ListVerdict.Pass;
    private static bool s_running;

    private static ConditionContext Context() => new(-1, s_running ? s_tally : null, Clock.Now);

    private static float Weight(Rarity rarity) => rarity switch { Rarity.Boss => 20f, Rarity.Rare => 5f, Rarity.Magic => 2f, _ => 1f };

    private static readonly MenuEntry s_menu = new(MenuCategory.Dev, order: 90)
    {
        Pages =
        {
            new MenuPage("Conditions")
            {
                Rows =
                {
                    new HintRow(() => "A dev condition list with its own enemy count, checked every frame while the crash test feature runs."),
                    ConditionEditor.List(s_conditions, () => ConditionKey, Context, "Conditions", () => s_running),
                    new LiveText("Verdict", () => s_verdict.Passed ? ("pass", StatusState.Active)
                        : ($"fail (entry {s_verdict.FirstFailing + 1})", StatusState.Holding)),
                },
            },
            new MenuPage("Sessions and roles")
            {
                Rows =
                {
                    new HintRow(() => "Read through the session and colour role APIs only, as a later feature would."),
                    new LiveText("Session", SessionReadout),
                    new LiveText("Records seen", () => ($"{s_kept} since this feature started", null)),
                    new LiveText("Rare enemy", () => (Swatch(), null)),
                },
            },
            new MenuPage("List row")
            {
                Rows =
                {
                    new HintRow(() => "Test entries, kept in memory only. Add, move and remove them by mouse or keys."),
                    new ListRow("Conditions", () => s_entries, entry => new MenuRow[]
                    {
                        new ValueChoice("Condition", () => ((TestEntry)entry).Kind, v => ((TestEntry)entry).Kind = v, () => s_kinds),
                        new ValueNumber("Amount", () => ((TestEntry)entry).Value, v => ((TestEntry)entry).Value = v, 0f, 100f, 5f, "%"),
                    }, "Add condition")
                    {
                        Add = () => s_entries.Add(new TestEntry { Kind = 0, Value = 50f }),
                        Remove = i => s_entries.RemoveAt(i),
                        Move = (from, to) => (s_entries[from], s_entries[to]) = (s_entries[to], s_entries[from]),
                    },
                },
            },
        },
    };

    // Records kept in the current session since this feature started (SessionRecordKept).
    private static int s_kept;
    // The swatch text, made again only when the roles change.
    private static int s_swatchVersion = -1;
    private static string s_swatch = string.Empty;

    private static (string, StatusState?) SessionReadout()
    {
        if (Sessions.Current is not { } session) return ("no session", StatusState.Off);
        var t = session.Totals;
        return ($"{Sessions.FormatDuration(t.Duration)}, {session.Records.Count} records, xp {t.Xp}, gold +{t.GoldGained}, drops {t.DropCount}, deaths {t.Deaths}", StatusState.Active);
    }

    private static string Swatch()
    {
        var rare = ColourRoles.Enemy(Rarity.Rare);
        if (ColourRoles.Version == s_swatchVersion && s_swatch.Length > 0) return s_swatch;
        s_swatchVersion = ColourRoles.Version;
        s_swatch = $"<color={ColorSetting.Format(rare)}>■■■■■■</color> roles version {s_swatchVersion}";
        return s_swatch;
    }

    public static void ArmFault() => s_updateThrows = 5;

    public static void ArmStart(string message) => s_startThrow = message;

    public static void ArmHandler(int calls) => s_handlerThrows = calls;

    public override void RegisterSettings() => s_conditionGroup.Register();

    public override void Start()
    {
        s_running = true;
        s_tally.Clear();
        ConditionTargets.Register(new ConditionTargets.Target(Id, "crashtest", s_conditions, () => new[] { ConditionKey },
            name => name == ConditionKey ? ConditionKey : null, _ => Context()));
        ConditionKeys.Register(Id, s_conditions, _ => "crash test condition");
        Scheduler.Every(this, "enemy count", 0.15f, () =>
        {
            s_ranges.Clear();
            ConditionEditor.EnemyRanges(s_conditions.List(ConditionKey), s_ranges);
            if (s_ranges.Count > 0) s_tally.Refresh(s_ranges, s_weight);
        }, startNow: true);
        // An empty repeat job, so `jobs` can show a fault dropping it.
        Scheduler.Every(this, "tick", 1f, () => { });
        s_uiClient = Dispatcher.Register(new DispatchClient(this, PriorityClass.Skills, s_uiOn, Array.Empty<PauseRule>()));
        s_kept = 0;
        Bus.Subscribe<SessionRecordKept>(this, _ => s_kept++);
        Bus.Subscribe<ZoneChange>(this, _ =>
        {
            if (s_handlerThrows <= 0) return;
            s_handlerThrows--;
            throw new InvalidOperationException("crash test handler");
        });
        if (s_startThrow is not { } message) return;
        s_startThrow = null;
        throw new InvalidOperationException(message);
    }

    private static readonly Func<Rarity, float> s_weight = Weight;

    public override void Update()
    {
        s_verdict = Conditions.Check(s_conditions.List(ConditionKey), Context(), s_results);
        if (s_updateThrows <= 0) return;
        s_updateThrows--;
        throw new InvalidOperationException("crash test fault");
    }

    public override void Shutdown()
    {
        s_running = false;
        s_tally.Clear();
        s_verdict = ListVerdict.Pass;
        ConditionTargets.Remove(Id);
        ConditionKeys.Remove(Id);
        s_updateThrows = s_handlerThrows = 0;
        if (s_uiClient != null) Dispatcher.Unregister(s_uiClient);
        s_uiClient = null;
    }
}
