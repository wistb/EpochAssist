using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;
using UnityEngine.UI.Extensions;

namespace EpochAssist.Features;

internal static partial class Stash
{
    // Reordering a tab or folder goes through the stash window's own drop handlers, as a drag does: the element is placed in the
    // window's list and the list's removed and added events run. They need the window's list, so the window is open.
    // Folders move on the client copy only: on the save file's copy the game's own folder delete needs each folder's id to
    // be its place.
    private static string? MoveBlocked() => Has(MoveGroup) ? null : "The game changed how tabs are moved.";

    // Every step waits for the game's stash window, as the game's own buttons do: each change is then one a player could
    // make by hand, at the stash. (Moves on the client copy go through the window's drop handlers besides.)
    private static bool NeedsWindow(Step step) => true;

    private static void SendMove(Step step, StashSnapshot snap, StashTabbedUIControls ui)
    {
        bool folder = step.Kind == StepKind.ReorderFolder;
        int id = step.Id!.Value;
        int to;
        Transform? element = null;
        if (folder)
        {
            to = step.Place;
            foreach (var pair in ui.localCategories)
                if (pair.category.CategoryId == id) element = pair.tabUI.transform;
        }
        else
        {
            var tab = snap.Tab(id)!;
            // The tab row lists the open folder's tabs.
            ui.OpenCategoryOnly(tab.FolderId, true);
            to = step.Target!.Index;
            foreach (var pair in ui.localTabs)
                if (pair.tab.TabId == id) element = pair.tabUI.transform;
        }
        if (element == null || !Game.Alive(element)) throw new InvalidOperationException("the stash window doesn't show it");
        var list = folder ? ui.reorderableCategories : ui.reorderableTabs;
        var e = new ReorderableList.ReorderableListEventStruct
        {
            DroppedObject = element.gameObject, SourceObject = element.gameObject, FromIndex = element.GetSiblingIndex(), ToIndex = to,
            FromList = list, ToList = list, IsAClone = false,
        };
        // The window's layout group keeps its own order: the element is placed through it, as a drag's drop places it.
        var group = folder ? ui.categoryLayoutGroup : ui.tabLayoutGroup;
        group.SetItemOrder(element.gameObject, to);
        if (folder)
        {
            ui.OnCategoryRemoved(e);
            ui.OnCategoryAdded(e);
        }
        else
        {
            ui.OnTabRemoved(e);
            ui.OnTabAdded(e);
        }
        // The removed handler takes the element out of the layout group and a drag's drop puts it back; put it back
        // here, at its place, or the window draws it where it was.
        bool inGroup = false;
        var elements = group._elements;
        for (int i = 0; i < elements.Count; i++)
            if (elements[i]?.RectTransformReference == element) inGroup = true;
        if (!inGroup) group.AddElement(element.gameObject);
        group.SetItemOrder(element.gameObject, to);
        group.UpdateLayout();
    }
}
