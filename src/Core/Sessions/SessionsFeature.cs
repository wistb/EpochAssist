using System.Globalization;
using System.Text;

namespace EpochAssist.Core;

/// <summary>
/// Session history (Sessions): records each character's play session from the bus and keeps it within its limits. Core,
/// with its page in the Settings tab; the SessionHistory setting stops recording, the feature itself always runs.
/// </summary>
internal sealed class SessionsFeature : Feature
{
    public const string Usage = "sessions [list [n] | export [current|days <n>|all]]";
    private const int DefaultList = 10;

    public override string Id => "sessions";
    public override string Name => "Sessions";
    public override string[] Needs => new[] { "game", "events", "profiles" };
    public override string[] RunsAfter => new[] { "events" };
    public override IEnumerable<string> DiagnosticNotes => Sessions.Notes();

    public override void Start()
    {
        Sessions.Start(this);
        Commands.Register(new Command("sessions", Usage, "Shows session history, lists past sessions, or exports them as CSV.", CommandMark.Release, Id, Run)
        {
            DevVerbs = new[] { "show", "fill", "flood", "rotate", "crash", "writer" },
        });
        Actions.Register(Id, "sessions.export", "Export session history", () => SessionExport.Start(SessionExport.RangeOf(SessionExport.PageRange)));
    }

    public override void Update()
    {
        Sessions.Update();
        SessionExport.Poll();
    }

    public override void Shutdown()
    {
        SessionExport.Reset();
        Sessions.Stop();
    }

    // sessions | sessions list [n] | sessions export [current|days <n>|all]
    private static CommandResult Run(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "":
                return CommandResult.Ok("sessions: " + Sessions.StatusText());
            case "list":
                int count = int.TryParse(call.Lower(2), out int n) && n > 0 ? n : DefaultList;
                var list = Sessions.List();
                var text = new StringBuilder($"ok sessions list: {Math.Min(count, list.Count)} of {list.Count}");
                foreach (var entry in list.Take(count)) text.Append('\n').Append(ListLine(entry));
                return new CommandResult(text.ToString());
            case "export":
                if (!TryRange(call, out var range)) return CommandResult.Error("sessions: usage " + Usage);
                // Past sessions export with history off; a current one there can't be.
                if (range.Kind == "current" && !SessionSettings.History.Value) return CommandResult.Error("sessions: history is off");
                ExportResult? result = null;
                if (!SessionExport.Start(range, r => result = r)) return CommandResult.Error("sessions: export failed: an export is already running");
                return new CommandResult("ok sessions export: started") { Pending = () => result == null ? null : result.Ok ? "ok " + result.Text : "error sessions: " + result.Text };
            default:
                return CommandResult.Error("sessions: usage " + Usage);
        }
    }

    private static bool TryRange(CommandCall call, out ExportRange range)
    {
        range = ExportRange.All;
        switch (call.Lower(2))
        {
            case "" or "all": return true;
            case "current":
                range = ExportRange.Current;
                return true;
            case "days" when int.TryParse(call.Lower(3), out int days) && days > 0:
                range = ExportRange.LastDays(days);
                return true;
            default:
                return false;
        }
    }

    // One line per session, newest first: start and mode (never a character's name), duration, end kind and totals.
    private static string ListLine(SessionIndexEntry e)
    {
        string start = e.Start is { } s ? s.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?";
        string totals = e.Totals is { } t ? $"xp {t.Xp}, gold +{t.GoldGained}/-{t.GoldSpent}, drops {t.DropCount}, deaths {t.Deaths}" : "totals at end";
        return $"  {start} {e.Character}{(e.Header?.Test == true ? " (test)" : string.Empty)}: {Sessions.FormatDuration(e.Duration)}, {e.EndKind}; {totals}";
    }
}
