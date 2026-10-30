using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Config › General › About: the logo, the build's details, links out (Nexus, GitHub, releases, bug reports) and the
/// disclaimers. The footer's About opens it; its Feedback and Report a bug run the same links.
/// </summary>
internal static class AboutPage
{
    public const string Block = "about.header";

    private const string NexusPage = "https://www.nexusmods.com/lastepoch/mods/45";
    private const string Repository = "https://github.com/wistb/EpochAssist";
    private const string Discord = "https://discord.com/users/187106829984202761";

    public static void Register() =>
        MenuBlocks.Register(Block, (rect, _) => Header(rect), _ => new[] { BuildInfo.Name, Tagline }, (_, width) => HeaderHeight(width));

    public static MenuPage Page() => new("About")
    {
        Resettable = false,
        Actions =
        {
            new ActionButton("Back", "about.back")
            {
                OnClick = () => Menu.Open(Previous()), VisibleWhen = () => Previous() != null,
                TipText = () => $"Back to {Previous()?.Page.Title ?? "the last page"}",
            },
        },
        Rows =
        {
            new BlockRow(Block),
            new HeadingRow("Details"),
            new LiveText("Version", () => ($"{BuildInfo.Version}, build {Runtime.BuildId}", null)),
            new LiveText("Tested game version", () => (BuildInfo.TestedGameVersion.Length > 0 ? BuildInfo.TestedGameVersion : "not recorded", null)),
            new LiveText("Author", () => (BuildInfo.Author, null)) { OnClick = () => Application.OpenURL(Discord), Tip = "Opens Crash's Discord profile, for direct messages" },
            new LiveText("License", () => ("GPL-3.0", null)),
            new LiveText("Data folder", () => ("UserData/EpochAssist/ in game folder", null)),
            new HeadingRow("Links"),
            Link("Nexus Mods", "Mod page, downloads and posts", "nexus", NexusPage),
            Link("Source", "Code and build on GitHub", "source", Repository),
            Link("Releases", "Changelog and every version", "releases", Repository + "/releases"),
            Link("Feedback", "Comments and questions in the Nexus posts", "feedback", NexusPage + "?tab=posts"),
            new TextButtonRow("Bug reports", () => "Copies diagnostics, then opens a new GitHub issue",
                new ActionButton("Report", "about.bug") { OnClick = ReportBug, External = true, Tip = "Copies diagnostics and opens a new GitHub issue in the browser" }),
            new HeadingRow("Disclaimers"),
            new HintRow(() => "Using mods online is against Last Epoch's terms of service."),
            new HintRow(() => "Not made, endorsed or supported by Eleventh Hour Games."),
            new HintRow(() => "Built with AI coding assistance (Claude by Anthropic)."),
            new HintRow(() => "No telemetry. Web requests happen only for a build pasted into the Planner and the guide it names."),
        },
    };

    private const string Tagline = "Macros, overlays, stash presets, session stats, map mods and more for Last Epoch.";

    // The page shown before About, while it still exists.
    private static PageNode? Previous() => MenuRoutes.Previous is { } path && path != MenuRoutes.About ? MenuTree.Find(path) : null;

    private static TextButtonRow Link(string label, string text, string key, string url) =>
        new(label, () => text, new ActionButton("Open", "about." + key) { OnClick = () => Application.OpenURL(url), External = true, Tip = "Opens " + url });

    /// <summary>Opens the Nexus posts in the browser.</summary>
    public static void Feedback() => Application.OpenURL(NexusPage + "?tab=posts");

    /// <summary>Puts the diagnostics on the clipboard and opens a new issue whose body says where to paste them.</summary>
    public static void ReportBug()
    {
        DiagnosticsPage.Copy(Diagnostics.Build().ToText(), "Diagnostics copied; paste them into the issue.");
        string body = "What happened:\n\n\nSteps to see it:\n\n\nDiagnostics (Report a bug copied them; paste here):\n\n";
        Application.OpenURL($"{Repository}/issues/new?title={Uri.EscapeDataString($"[{BuildInfo.Version}] ")}&body={Uri.EscapeDataString(body)}");
    }

    // The logo at twice the top bar's size, then the tagline.
    private const float LogoWidth = 290f, LogoHeight = 34f, Gap = 10f;

    private static float HeaderHeight(float width) => 6f + LogoHeight + Gap + Controls.HintHeight(Tagline, width) + 8f;

    private static void Header(Rect rect)
    {
        if (Event.current.type != EventType.Repaint) return;
        var logo = new Rect(rect.x, rect.y + 6f, LogoWidth, LogoHeight);
        if (Theme.BigLogo is { } texture && Widgets.Themed) GUI.DrawTexture(Menu.PixelSnap(logo), texture);
        else Widgets.Paint(logo, BuildInfo.Name, Widgets.Style(() => Theme.Brand, GUI.skin.label));
        float y = logo.yMax + Gap;
        Widgets.Paint(new Rect(rect.x, y, rect.width, Controls.HintHeight(Tagline, rect.width)), Tagline, Widgets.Style(() => Theme.Hint, GUI.skin.label));
    }
}
