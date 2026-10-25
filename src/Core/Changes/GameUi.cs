using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>The game's screen UI hidden and shown again, as an edit on its root canvases.</summary>
internal static class GameUi
{
    // Left visible: what the player must always see (loading, errors, disconnects, the on-screen keyboard, the item on
    // the cursor).
    private static readonly string[] s_kept =
    {
        "Canvas (loading screen)", "Canvas (Disconnected Prompt)", "Canvas (Error Prompt)", "Canvas (Bug Report)",
        "Canvas (Screen Keyboard)", "Draw Over Login Canvas",
    };
    // FloatingBarksCanvas (NPC barks) is left out: the game enables it again on every load.
    private static readonly string[] s_extraRoots = { "PlayerInteractionMarker" };

    /// <summary>The game UI's root canvases, enabled true while shown; restored canvas by canvas.</summary>
    public static readonly EditTarget<bool> Target = new()
    {
        Name = "game ui",
        Find = RootCanvases,
        Read = o => o.Cast<Canvas>().enabled,
        Write = (o, v) => o.Cast<Canvas>().enabled = v,
        Show = v => v ? "shown" : "hidden",
        Scene = false,
    };

    /// <summary>Hides the game's screen UI until the edit is removed (or its owner stops); null when the part is missing.</summary>
    public static Edit<bool>? Hide(Feature owner) =>
        FeatureHost.Find("gameui") is { State: FeatureState.Running } f && f.Has(GameUiFeature.HideGroup) ? Edits.Apply(owner, Target, false) : null;

    // Every enabled canvas under the GUI object's root canvases (nested ones too: Unity draws a nested canvas while its
    // parent canvas is disabled), and under two other overlays, but the kept roots. Found from the game's UI root and
    // its scene's root objects, not by an object search, which walks every object.
    private static IEnumerable<UnityEngine.Object> RootCanvases()
    {
        var gui = Game.UI?.transform.root;
        if (gui == null || gui.name != "GUI") yield break;
        for (int i = 0; i < gui.childCount; i++)
        {
            var child = gui.GetChild(i);
            if (Array.IndexOf(s_kept, child.name) >= 0 || !child.gameObject.activeInHierarchy) continue;
            foreach (var canvas in Enabled(child)) yield return canvas;
        }
        foreach (var root in gui.gameObject.scene.GetRootGameObjects())
        {
            if (Array.IndexOf(s_extraRoots, root.name) >= 0 && root.activeInHierarchy)
            {
                foreach (var canvas in Enabled(root.transform)) yield return canvas;
            }
        }
    }

    private static IEnumerable<Canvas> Enabled(Transform root)
    {
        foreach (var canvas in root.GetComponentsInChildren<Canvas>(false))
        {
            if (Game.Alive(canvas) && canvas.enabled && canvas.rootCanvas is { } top && top.renderMode != RenderMode.WorldSpace) yield return canvas;
        }
    }
}
