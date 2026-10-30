using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The dev window's Modules section, for testing module loading without moving settings files: each module's choice,
/// a reload to apply, a reload as a fresh install or as an upgrade, modules made unavailable at the next reload, and
/// the first-start toast. Shows while Config › General › Modules is open, the first-start choice is open, or pinned
/// (modules tools on).
/// </summary>
internal static class ModulesDevSection
{
    private static string s_message = string.Empty;

    public static void Register() => DevWindow.Add(new DevWindow.Section("Modules", Shown, Draw));

    private static bool Shown() =>
        ModuleCommands.Pinned || Modules.Pending || (Ui.Menu.Visible && MenuRoutes.Page?.Path == MenuRoutes.Modules);

    private static void Draw()
    {
        DevWindow.Text($"start: {ModuleChoices.Decision} · prompt {(Modules.Pending ? "open" : "answered")} · files {(ModuleChoices.FilesFresh() ? "fresh" : "not fresh")}");
        string? blocked = Modules.Pending ? "choice open" : null;
        foreach (var module in Modules.All)
        {
            bool on = ModuleChoices.Choice(module.Id);
            string state = FeatureHost.Find(module.Id) is { } feature ? feature.State.ToString().ToLowerInvariant() : "not loaded";
            bool was = GUI.enabled;
            GUI.enabled = was && blocked == null;
            bool next = GUILayout.Toggle(on, $" {module.Name}: {state}");
            GUI.enabled = was;
            if (next != on) s_message = ModuleCommands.Set(module.Id, next) ?? "reload to apply";
        }
        GUILayout.BeginHorizontal();
        if (DevWindow.Button("All on", blocked)) s_message = ModuleCommands.Set("all", true) ?? "reload to apply";
        if (DevWindow.Button("All off", blocked)) s_message = ModuleCommands.Set("all", false) ?? "reload to apply";
        GUILayout.EndHorizontal();
        if (DevWindow.Button("Reload to apply")) ModuleCommands.Reload(null, null);
        if (DevWindow.Button("Reload as fresh install")) ModuleCommands.Reload(null, "fresh");
        if (DevWindow.Button("Reload as upgrade (no Modules group)")) ModuleCommands.Reload(null, "upgrade");
        if (DevWindow.Button("Show the modules toast")) ModulesPage.ShowToast();

        DevWindow.Text("Unavailable at the next reload:");
        GUILayout.BeginHorizontal();
        int n = 0;
        foreach (var module in Modules.All)
        {
            bool marked = ModuleCommands.IsUnavailable(module.Id);
            if (GUILayout.Toggle(marked, " " + module.Name) != marked) ModuleCommands.ToggleUnavailable(module.Id);
            // Three to a line in the narrow window.
            if (++n % 3 == 0 && n < Modules.All.Count)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
            }
        }
        GUILayout.EndHorizontal();
        DevWindow.Text(s_message);
    }
}
