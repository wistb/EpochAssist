#!/usr/bin/env bash
# Starts a new feature the way the codebase does it, in its own folder src/Features/<Name>/: a feature class, its logic
# with a per-run state holder, a settings group and a menu page. Registration in Runtime.Init is left to do by hand; the
# update order follows the feature's Needs and RunsAfter, not its place in the list.
# Usage: tools/new-feature.sh <Name> [--id <id>] [--automation] [--patch]
#   <Name>        PascalCase letters and digits (AutoPotion)
#   --id          lowercase letters and digits; default: the name lowercased
#   --automation  adds a dispatcher client (priority Skills) with one pause rule of its own
#   --patch       adds a Harmony patch class with a placeholder target, in an optional group
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
usage() { echo "usage: $0 <Name> [--id <id>] [--automation] [--patch]" >&2; exit 1; }

name="" id="" automation=0 patch=0
while [ $# -gt 0 ]; do
    case "$1" in
        --id) [ $# -ge 2 ] || usage; id="$2"; shift 2 ;;
        --automation) automation=1; shift ;;
        --patch) patch=1; shift ;;
        -*) usage ;;
        *) [ -z "$name" ] || usage; name="$1"; shift ;;
    esac
done
[ -n "$name" ] || usage
[ -n "$id" ] || id="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"

refuse() { echo "Refused: $1; nothing was written." >&2; exit 1; }
[[ "$name" =~ ^[A-Z][A-Za-z0-9]*$ ]] || refuse "the name must be PascalCase letters and digits, starting with a capital"
[[ "$id" =~ ^[a-z][a-z0-9]*$ ]] || refuse "the id must be lowercase letters and digits, starting with a letter"

feature="src/Features/${name}/${name}Feature.cs"
logic="src/Features/${name}/${name}.cs"
settings="src/Features/${name}/${name}Settings.cs"
page="src/Features/${name}/${name}Page.cs"
for file in "$feature" "$logic" "$settings" "$page"; do
    [ ! -e "$root/$file" ] || refuse "$file already exists"
done
grep -rqF "Id => \"$id\"" "$root/src" && refuse "a feature already has the id '$id'"
grep -rqE "class ${name}Feature\b" "$root/src" && refuse "class ${name}Feature already exists"

# Parts that depend on the options.
needs='"game"'
status='State == FeatureState.Running ? (S.Enabled.Value ? new FeatureStatus(StatusState.Active, string.Empty, null) : FeatureStatus.Off("off")) : base.Status'
run_client='' client_prop='' client_register='' client_check='' client_unregister=''
if [ "$automation" = 1 ]; then
    needs='"game", "dispatch"'
    status='State == FeatureState.Running && __NAME__.Client is { } client ? Dispatcher.StatusOf(client) : base.Status'
    run_client='        public DispatchClient? Client;'
    client_prop='    /// <summary>The dispatcher'"'"'s view of __NAME__ while it runs.</summary>\n    public static DispatchClient? Client => s_run?.Client;\n'
    client_register='        // Automation acts only through the dispatcher, in its priority class. Its own pause rules\n        // come after the global ones; the first active rule wins and is what the status shows.\n        run.Client = Dispatcher.Register(new DispatchClient(feature, PriorityClass.Skills, S.Enabled, new[]\n        {\n            new PauseRule("__ID__-hold", "__NAME__ is holding", RuleKind.Momentary, () => false),\n        }));'
    client_check='        if (run.Client == null || !Dispatcher.Allowed(run.Client)) return;'
    client_unregister='        if (run.Client != null) Dispatcher.Unregister(run.Client);'
fi
patch_members=''
if [ "$patch" = 1 ]; then
    patch_members='    // The host applies and removes patches and checks their targets first. This one is in an optional group,\n    // so while its target is missing the feature runs without it (shown as degraded). Point it at a real method.\n    private const string Hook = "hook";\n    public override Type[] PatchClasses => NestedPatches(typeof(__NAME__Feature));\n    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } = new Dictionary<Type, string> { [typeof(PlaceholderPatch)] = Hook };\n\n    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.CameraManager), "ReplaceWithTheMethodToPatch")]\n    private static class PlaceholderPatch\n    {\n        private static void Postfix() { }\n    }\n'
fi

# Writes a template to a file: a line @@PART@@ becomes that option part (or goes when it is empty), then __NAME__ and
# __ID__ are filled in.
write() {
    local target="$root/$1"
    mkdir -p "$(dirname "$target")"
    awk -v needs="$needs" -v status="${status//&/\\\\&}" -v run_client="$run_client" -v client_prop="$client_prop" \
        -v client_register="$client_register" -v client_check="$client_check" -v client_unregister="$client_unregister" \
        -v patch_members="$patch_members" '
        {
            gsub(/@NEEDS@/, needs); gsub(/@STATUS@/, status)
            part = ""
            if ($0 == "@@RUN_CLIENT@@") part = run_client
            else if ($0 == "@@CLIENT_PROP@@") part = client_prop
            else if ($0 == "@@CLIENT_REGISTER@@") part = client_register
            else if ($0 == "@@CLIENT_CHECK@@") part = client_check
            else if ($0 == "@@CLIENT_UNREGISTER@@") part = client_unregister
            else if ($0 == "@@PATCH@@") part = patch_members
            else { print; next }
            if (part != "") print part
        }' | sed -e "s/__NAME__/$name/g" -e "s/__ID__/$id/g" > "$target"
}

write "$feature" <<'EOF'
using EpochAssist.Core;
using EpochAssist.Ui;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.__NAME__Settings;

namespace EpochAssist.Features;

// The feature class wires the host to the logic in __NAME__, which keeps everything a run changes.
internal sealed class __NAME__Feature : Feature
{
    // Members the feature can run without go in a named group, checked with Has().
    private const string Camera = "camera";

    public override string Id => "__ID__";
    public override string Name => "__NAME__";
    // Features it can't act without: the host runs it after them, and only while they run.
    public override string[] Needs => new[] { @NEEDS@ };
    // Features it runs after each frame, order only: profiles puts a character's settings in place first.
    public override string[] RunsAfter => new[] { "profiles" };
    // A page in the menu (its switch in the header) and a place on the HUD pill.
    public override FeaturePage? Page => s_page;
    public override string? StatusName => "__NAME__";

    private static readonly FeaturePage s_page = new("__NAME__", 20, __NAME__Page.Draw) { Switch = S.Enabled };

    // Player, world and game data come from the models: add "player", "world" or "data" to Needs and declare each part
    // it reads, e.g. public override PartNeed[] Parts => new[] { Part("player", "skill bar") };
    // Outlines, tints and minimap markers come from the visuals layer the same way: "visuals" in Needs and
    // Part("visuals", "outlines").
    // Every other game member it reads, checked before it starts. Replace these examples.
    public override Requirement[] Requirements => new[]
    {
        Need(typeof(Il2Cpp.PlayerFinder), nameof(Il2Cpp.PlayerFinder.getPlayerActor)),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.targetZoom), Camera),
    };
    // The default frame budget is 0.3 ms; override BudgetMs only with a measured reason.

@@PATCH@@
    public override FeatureStatus Status => @STATUS@;

    public override void RegisterSettings() => S.Register();
    public override void Start() => __NAME__.Start(this, Has(Camera));
    public override void Update() => __NAME__.OnUpdate();
    public override void Shutdown() => __NAME__.Shutdown();
}
EOF

write "$logic" <<'EOF'
using EpochAssist.Core;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.__NAME__Settings;

namespace EpochAssist.Features;

/// <summary>What __NAME__ does, in a sentence or two.</summary>
internal static class __NAME__
{
    // Everything one run changes. Made in Start and dropped in Shutdown once its effects on the game are undone,
    // so a restart or a dev reload begins clean.
    private sealed class Run
    {
@@RUN_CLIENT@@
        public bool UseCamera;
        // The run's changes to the game, each an edit the registry restores.
        public readonly List<Edit> Edits = new();
    }

    private static Run? s_run;

@@CLIENT_PROP@@
    public static void Start(Feature feature, bool useCamera)
    {
        var run = new Run { UseCamera = useCamera };
@@CLIENT_REGISTER@@
        s_run = run;
        // Timers are jobs the feature owns, run next to its Update and dropped with it (never "next at" fields):
        //   Scheduler.Every(feature, "scan", 0.25f, () => { if (s_run == run) Scan(run); });
        // Events come through the bus; the host removes the subscription, so Shutdown doesn't:
        //   Bus.Subscribe<ZoneChange>(feature, change => { ... });
        // Every change to a game value goes through the edit registry, never a direct write: it keeps the original,
        // applies again after a scene rebuilds the object, and restores it when the edit is removed or the feature stops,
        // faults or reloads (no restore code in Shutdown). A target says where the value lives:
        //   var reveal = new EditTarget<float> { Name = "minimap: reveal radius", Find = …, Read = …, Write = … };
        //   run.Edits.Add(Edits.Apply(feature, reveal, 400f));
        // One action per control, so bindings, buttons and the bridge share one path.
        Actions.Register(feature.Id, "__ID__.toggle", "__NAME__", () => S.Enabled.Value = !S.Enabled.Value, () => S.Enabled.Value);
    }

    public static void OnUpdate()
    {
        var run = s_run;
        if (run == null) return;
        // Turned off by its switch, the feature's changes to the game go back.
        if (!S.Enabled.Value)
        {
            DropEdits(run);
            return;
        }
@@CLIENT_CHECK@@
        // Reach the game through Game.* and Player.*, null-safely, and don't keep objects across scenes.
    }

    private static void DropEdits(Run run)
    {
        foreach (var edit in run.Edits) edit.Remove();
        run.Edits.Clear();
    }

    // Undo everything the run did to the game first, then drop the run. Its edits go with the feature:
    // the host removes them.
    public static void Shutdown()
    {
        var run = s_run;
        if (run == null) return;
@@CLIENT_UNREGISTER@@
        s_run = null;
    }
}
EOF

write "$settings" <<'EOF'
using EpochAssist.Core;

namespace EpochAssist.Features;

internal static class __NAME__Settings
{
    // Stored ids are a contract. Renaming or retyping one raises the version and adds a migration for it.
    public static readonly SettingsGroup Group = new("EpochAssist___NAME__", "__NAME__", version: 1) { ActionKey = "__ID__" };

    public static readonly Section Main = Group.Section("main", "__NAME__");
    // Off until the player turns it on. The quick label offers it on the HUD panel.
    public static readonly Toggle Enabled = new(Main, "Enabled", "__NAME__", false) { QuickLabel = "__NAME__" };

    public static void Register()
    {
        // Migrations go here, before Register: Group.Migration(2, _ => { ... });
        Group.Register();
    }
}
EOF

write "$page" <<'EOF'
using EpochAssist.Core;
using EpochAssist.Ui;
using S = EpochAssist.Features.__NAME__Settings;

namespace EpochAssist.Features;

// __NAME__'s page below the header, which already shows its switch: its settings as cards.
internal static class __NAME__Page
{
    public static void Draw()
    {
        Menu.BeginScroll();
        SettingsView.Card(S.Main);
        Menu.EndScroll();
    }
}
EOF

echo "Created $feature, $logic, $settings, $page"
echo "Next:"
echo "  1. Register it in Runtime.Init (src/Core/Host/Entry.cs): new ${name}Feature(). The update order follows its Needs"
echo "     and RunsAfter, so its place in the list doesn't matter."
echo "  2. Replace the example requirements$([ "$patch" = 1 ] && echo " and the patch target") with real ones."
echo "  3. Check it with ./build.sh and ./build.sh dev."
