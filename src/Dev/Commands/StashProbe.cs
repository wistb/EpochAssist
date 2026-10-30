using EpochAssist.Core;
using EpochAssist.Features;
using Il2Cpp;
using Il2CppLE.UI.PanelSystem;
using UnityEngine;

namespace EpochAssist.Dev;

// stash probe-call: one call of each of the game's stash paths the Stash page uses, on the saved copy only, on a backed-up save;
// prints what changed in the stash read. For the facts pass; the page sends through Stash.Steps.
internal static class StashProbe
{
    public static CommandResult Run(CommandCall call)
    {
        var before = Stash.Read();
        if (before == null) return CommandResult.Ok("stash probe-call: no stash");
        // Only the native-address lookup reads nothing of the stash and changes nothing; every other kind is for the saved
        // copy only.
        if (before.ClientCopy && call.Lower(2) != "rva") return CommandResult.Error("stash probe-call: saved copy only");
        var ui = UnityEngine.Object.FindObjectsOfType<StashTabbedUIControls>(true).FirstOrDefault()
            ?? Resources.FindObjectsOfTypeAll<StashTabbedUIControls>().FirstOrDefault(c => c.gameObject.scene.name != null);
        if (ui == null) return CommandResult.Error("stash probe-call: no stash controls");
        bool open = UIBase.instance is { } b && b.IsStashOpen();
        string did;
        try
        {
            switch (call.Lower(2))
            {
                case "buy":
                {
                    int folder = int.TryParse(call.Lower(3), out int f) ? f : before.Folders[0].Id;
                    int order = before.TabsIn(folder).Count();
                    int id = ItemContainersManager.Instance.PurchaseNewStashTab(folder, order, null, 0, 0, null, null, ui);
                    did = $"PurchaseNewStashTab(folder {folder}, order {order}) → {id}";
                    break;
                }
                case "drag-folder":
                case "drag-tab":
                {
                    bool folder = call.Lower(2) == "drag-folder";
                    int id = int.Parse(call.Lower(3)), to = int.Parse(call.Lower(4));
                    Transform? element = null;
                    if (folder)
                    {
                        foreach (var pair in ui.localCategories) if (pair.category.CategoryId == id) element = pair.tabUI.transform;
                    }
                    else foreach (var pair in ui.localTabs) if (pair.tab.TabId == id) element = pair.tabUI.transform;
                    if (element == null) return CommandResult.Error("stash probe-call: element not in the window (open the stash, and its folder for a tab)");
                    var list = folder ? ui.reorderableCategories : ui.reorderableTabs;
                    int from = element.GetSiblingIndex();
                    var e = new UnityEngine.UI.Extensions.ReorderableList.ReorderableListEventStruct
                    {
                        DroppedObject = element.gameObject, SourceObject = element.gameObject, FromIndex = from, ToIndex = to,
                        FromList = list, ToList = list, IsAClone = false,
                    };
                    if (call.Lower(5) != "nomove") element.SetSiblingIndex(to);
                    // As a drag does: removed from the list, then added at its new place.
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
                    did = $"{(folder ? "OnCategoryRemoved/Added" : "OnTabRemoved/Added")}(from {from} to {to}) on the window's own list";
                    break;
                }
                case "organize":
                    ui.OrganizeCategories();
                    ui.OrganizeTabs();
                    did = "OrganizeCategories(), OrganizeTabs()";
                    break;
                case "rebuild":
                {
                    var saved = PlayerFinder.getGlobalDataTracker().stash;
                    var tabs = new Il2CppSystem.Collections.Generic.List<Il2CppLE.Data.StashTab>();
                    for (int i = 0; i < saved.Tabs.Count; i++) tabs.Add(saved.Tabs[i]);
                    ui.OnDataUpdated(saved.Categories, tabs);
                    did = "OnDataUpdated(saved folders, saved tabs)";
                    break;
                }
                case "row":
                {
                    var parts = new List<string>();
                    foreach (var pair in ui.localCategories)
                        parts.Add($"{pair.category.DisplayName}@{pair.category.DisplayOrder}:sib{pair.tabUI.transform.GetSiblingIndex()}/x{(int)pair.tabUI.transform.localPosition.x}");
                    var holder = ui.categoryHolder;
                    var kids = new List<string>();
                    for (int i = 0; i < holder.childCount; i++)
                    {
                        var c = holder.GetChild(i);
                        kids.Add($"{i}:{c.name}{(c.gameObject.activeSelf ? string.Empty : "(off)")}");
                    }
                    var els = ui.categoryLayoutGroup._elements;
                    var inGroup = new List<string>();
                    for (int i = 0; i < els.Count; i++) inGroup.Add($"{els[i]?.RectTransformReference?.GetSiblingIndex()}{(els[i]?.Ignored == true ? "i" : string.Empty)}");
                    did = $"group {els.Count} elements [{string.Join(",", inGroup)}]; " + "row: " + string.Join(" ", parts) + " | holder " + string.Join(" ", kids);
                    break;
                }
                case "fix-row":
                {
                    var pairs = new List<StashTabbedUIControls.CategoryTabPair>();
                    foreach (var pair in ui.localCategories) pairs.Add(pair);
                    var group = ui.categoryLayoutGroup;
                    foreach (var pair in pairs.OrderBy(p => p.category.DisplayOrder))
                    {
                        bool inGroup = false;
                        for (int i = 0; i < group._elements.Count; i++)
                            if (group._elements[i]?.RectTransformReference == pair.tabUI.transform) inGroup = true;
                        if (!inGroup) group.AddElement(pair.tabUI.gameObject);
                        group.SetItemOrder(pair.tabUI.gameObject, pair.category.DisplayOrder);
                    }
                    ui.categoryLayoutGroup.UpdateLayout();
                    did = "folder row: SetItemOrder by display order, UpdateLayout";
                    break;
                }
                case "copies":
                {
                    var saved = PlayerFinder.getGlobalDataTracker().stash;
                    var holder = ItemContainersManager.Instance.stash;
                    var linked = holder.stashItemContainers[holder._CurrentStashIndex_k__BackingField].LinkedStash;
                    string Folders(Il2CppLE.Data.Stash st) => string.Join(" ", st.Categories.ToArray().OrderBy(c => c.DisplayOrder).Select(c => $"{c.CategoryId}:{c.DisplayName}"));
                    var local = new List<string>();
                    foreach (var pair in ui.localCategories) local.Add($"{pair.category.CategoryId}:{pair.category.DisplayName}");
                    did = $"same object {(saved.Pointer == linked?.Pointer)}; saved [{Folders(saved)}]; linked [{(linked == null ? "-" : Folders(linked))}]; window [{string.Join(" ", local)}]";
                    break;
                }
                case "gold-ui":
                {
                    var found = new List<string>();
                    foreach (var g in Resources.FindObjectsOfTypeAll<UIGoldText>())
                    {
                        if (g.gameObject.scene.name == null) continue;
                        var root = g.transform.parent != null ? g.transform.parent : g.transform;
                        // Each image with its sprite, slicing border, size on screen and parent (to draw the same frame).
                        var top = root.parent != null ? root.parent : root;
                        foreach (var img in top.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                        {
                            var sp = img.sprite;
                            var size = img.rectTransform.rect.size;
                            found.Add($"{g.name}>{img.transform.parent?.name}/{img.name}: sprite {(sp == null ? "-" : sp.name)} type {img.type} border {(sp == null ? "-" : sp.border.ToString())} "
                                + $"size {size.x:0}x{size.y:0} active {img.gameObject.activeInHierarchy} colour #{ColorUtility.ToHtmlStringRGBA(img.color)}");
                        }
                        if (g.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>(true) is { } tmp)
                            found.Add($"{g.name} text: font {tmp.font?.name} (source file {(tmp.font?.sourceFontFile == null ? "none" : tmp.font.sourceFontFile.name)}) size {tmp.fontSize} colour #{ColorUtility.ToHtmlStringRGBA(tmp.color)} align {tmp.alignment}");
                    }
                    did = "gold ui:\n  " + string.Join("\n  ", found);
                    break;
                }
                case "rva":
                {
                    // The native code of the game's stash methods, as offsets in GameAssembly.dll, for a decompiler.
                    var module = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                        .First(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                    long baseAddr = module.BaseAddress.ToInt64();
                    var parts = new List<string> { $"base 0x{baseAddr:X}" };
                    // The stash's own containers too (where an item stored by the game's rules goes), with their compiler-made
                    // lambda classes, whose methods hold a LINQ query's sort keys and filters.
                    var types = new List<Type> { typeof(StashTabbedUIControls), typeof(ServerStashCommands), typeof(ItemContainersManager), typeof(StashHelpers), typeof(PlayerSync) };
                    foreach (var owner in new[] { typeof(StashItemContainer), typeof(TabbedItemContainer) })
                    {
                        types.Add(owner);
                        types.AddRange(owner.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
                    }
                    foreach (var type in types)
                        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                        {
                            if (!field.Name.StartsWith("NativeMethodInfoPtr_") || !System.Text.RegularExpressions.Regex.IsMatch(field.Name, call.Rest(3).Length > 0 ? call.Rest(3) : "Categor|Delete|Remove")) continue;
                            var info = (IntPtr)field.GetValue(null)!;
                            if (info == IntPtr.Zero) continue;
                            long code = System.Runtime.InteropServices.Marshal.ReadIntPtr(info).ToInt64();
                            parts.Add($"{type.Name}.{field.Name.Substring(20)} 0x{code - baseAddr:X}");
                        }
                    did = string.Join("\n  ", parts);
                    break;
                }
                case "open-configure":
                    ui.ConfigureCategory(ui.GetActiveCategoryID());
                    did = "ConfigureCategory(active folder)";
                    break;
                case "close-window":
                    did = $"folder window Cancel: {(Stash.CloseFolderWindow() ? "pressed" : "none open")}";
                    break;
                case "new-folder":
                    ui.AddNewCategory();
                    did = "AddNewCategory()";
                    break;
                case "configure-tab":
                {
                    int id = int.Parse(call.Lower(3));
                    var tab = before.Tab(id)!;
                    int parent = call.Args.Length >= 4 && int.TryParse(call.Lower(4), out int p) ? p : tab.FolderId;
                    string name = call.Args.Length >= 5 ? call.Rest(5) : "Probe";
                    var rules = name == "Any unique" ? new Stash.TabRules { Rarity = Stash.Rules.Unique }
                        : new Stash.TabRules { Rarity = Stash.Rules.Exalted, Types = new[] { 0 }, Corruption = Stash.Rules.Clean };
                    ui.HandleConfigureTabResult(new StashConfigureTabModalResult
                    {
                        SelectedTabId = id, SelectedIconId = 13, SelectedColorId = 15, SelectedParentId = parent, TabName = name,
                        TabPriority = Stash.Rules.ToGame(rules), IgnoreFolderPriority = false, Action = StashConfigureResult.Save,
                    });
                    did = $"HandleConfigureTabResult(tab {id}, parent {parent}, \"{name}\", Exalted helmet clean)";
                    break;
                }
                case "configure-folder":
                {
                    int id = int.Parse(call.Lower(3));
                    string name = call.Args.Length >= 4 ? call.Rest(4) : "ProbeF";
                    ui.HandleConfigureCategoryResult(new StashConfigureTabModalResult
                    {
                        SelectedTabId = id, SelectedIconId = 20, SelectedColorId = 9, SelectedParentId = id, TabName = name, Action = StashConfigureResult.Save,
                    });
                    did = $"HandleConfigureCategoryResult(folder {id}, \"{name}\")";
                    break;
                }
                case "tab-folder":
                {
                    // The page's local configure with only the folder changed, to read the display order a moved tab gets.
                    int id = int.Parse(call.Lower(3)), to = int.Parse(call.Lower(4));
                    var t = before.Tab(id)!;
                    ServerStashCommands.ServerSetTabInfo(ItemContainersManager.Instance, ItemContainersManager.Instance.stash._CurrentStashIndex_k__BackingField, id,
                        t.IconId, t.ColourId, to, t.Name, Stash.Rules.ToGame(t.Rules, forSend: true), t.IgnoresFolderRules);
                    did = $"ServerSetTabInfo(tab {id}, folder {to}) (the local command)";
                    break;
                }
                case "buy-at":
                {
                    // The page's purchase on the saved copy (no window controls) at a chosen display order.
                    int folder = int.Parse(call.Lower(3)), order = int.Parse(call.Lower(4));
                    int id = ItemContainersManager.Instance.PurchaseNewStashTab(folder, order, null, 0, 0, null, null);
                    did = $"PurchaseNewStashTab(folder {folder}, order {order}) → {id} (no window controls)";
                    break;
                }
                case "reorder-tab":
                {
                    int id = int.Parse(call.Lower(3)), to = int.Parse(call.Lower(4));
                    ServerStashCommands.ServerMoveTab(ItemContainersManager.Instance, ItemContainersManager.Instance.stash._CurrentStashIndex_k__BackingField, id, to);
                    did = $"ServerMoveTab(tab {id}, to {to}) (the local command, to read what a reorder changes)";
                    break;
                }
                case "reorder-folder":
                {
                    int id = int.Parse(call.Lower(3)), to = int.Parse(call.Lower(4));
                    ServerStashCommands.ServerMoveCategory(ItemContainersManager.Instance, ItemContainersManager.Instance.stash._CurrentStashIndex_k__BackingField, id, to);
                    did = $"ServerMoveCategory(folder {id}, to {to}) (the local command)";
                    break;
                }
                case "rules":
                {
                    var rules = new Stash.TabRules
                    {
                        Rarity = Stash.Rules.Unique, Lp = new Stash.Span(1, 1), Corruption = Stash.Rules.Clean, Types = new[] { 0, 21 }, Uniques = new[] { 1, 2 },
                        Level = new Stash.Span(10, 80), Ww = new Stash.Span(1, null),
                    };
                    var game = Stash.Rules.ToGame(rules);
                    var back = Stash.Rules.FromGame(game);
                    did = $"rules made in memory: {Stash.Rules.Describe(rules)}; read back {(back.Equals(rules) ? "equal" : "differs: " + Stash.Rules.Describe(back))}";
                    break;
                }
                default:
                    return CommandResult.Error("stash probe-call: buy [folder] | new-folder | configure-tab <tab> [parent] [name] | configure-folder <folder> [name] | reorder-tab|reorder-folder <id> <to> | rules");
            }
        }
        catch (Exception e)
        {
            return CommandResult.Error($"stash probe-call: threw {e.GetType().Name}: {e.Message}");
        }
        var after = Stash.Read()!;
        var lines = new List<string> { $"ok stash probe-call ({(open ? "window open" : "window closed")}): {did}" };
        lines.Add($"  tabs {before.TabCount} → {after.TabCount}, folders {before.Folders.Count} → {after.Folders.Count}, gold {before.Gold} → {after.Gold}, items {before.Tabs.Sum(t => t.ItemCount)} → {after.Tabs.Sum(t => t.ItemCount)}");
        foreach (var f in after.Folders)
        {
            var o = before.Folder(f.Id);
            if (o != f) lines.Add($"  folder {f.Id} #{f.Order} \"{f.Name}\" icon {f.IconId} colour {f.ColourId}{(o == null ? " (new)" : string.Empty)}");
        }
        foreach (var t in after.Tabs)
        {
            var o = before.Tab(t.Id);
            if (o != t)
                lines.Add($"  tab {t.Id} folder {t.FolderId} #{t.Order} \"{t.Name}\" icon {t.IconId} colour {t.ColourId} items {t.ItemCount} · {Stash.Rules.Describe(t.Rules)}{(o == null ? " (new)" : string.Empty)}");
        }
        return new CommandResult(DevOutput.Reply("stash-probe", lines));
    }
}
