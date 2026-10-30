using System.Text.Json;
using System.Text.Json.Nodes;
using EpochAssist.Core;
using MelonLoader.Utils;

namespace EpochAssist.Features;

internal static partial class Stash
{
    internal enum TabChoice { Keep, Reuse, ReuseIfEmpty }

    /// <summary>
    /// The player's choices for one season stash: Keep/Reuse per tab and folder (only those changed from the default),
    /// tabs per section, the preset and the budget. Saved in
    /// UserData/EpochAssist/stash/stashes/&lt;key&gt;.json.
    /// </summary>
    internal sealed class Choices
    {
        public Dictionary<int, TabChoice> Tabs { get; } = new();
        public Dictionary<int, TabChoice> Folders { get; } = new();
        // The name each chosen tab and folder had: the game gives folders and tabs new ids when one is deleted, so a
        // choice whose id now names something else is dropped.
        public Dictionary<int, string> TabNames { get; } = new();
        public Dictionary<int, string> FolderNames { get; } = new();

        /// <summary>Drops choices whose id names another tab or folder than when chosen; true when any was dropped.</summary>
        public bool Validate(StashSnapshot snap)
        {
            bool dropped = false;
            foreach (int id in Tabs.Keys.ToList())
                if (snap.Tab(id) is not { } t || TabNames.TryGetValue(id, out var n) && n != t.Name)
                {
                    Tabs.Remove(id);
                    TabNames.Remove(id);
                    dropped = true;
                }
            foreach (int id in Folders.Keys.ToList())
                if (snap.Folder(id) is not { } f || FolderNames.TryGetValue(id, out var n) && n != f.Name)
                {
                    Folders.Remove(id);
                    FolderNames.Remove(id);
                    dropped = true;
                }
            return dropped;
        }

        /// <summary>Takes the current names for every choice (after the page's own step renamed one); true when one changed.</summary>
        public bool RememberNames(StashSnapshot snap)
        {
            bool changed = false;
            foreach (int id in Tabs.Keys)
                if (snap.Tab(id) is { } t && (!TabNames.TryGetValue(id, out var n) || n != t.Name))
                {
                    TabNames[id] = t.Name;
                    changed = true;
                }
            foreach (int id in Folders.Keys)
                if (snap.Folder(id) is { } f && (!FolderNames.TryGetValue(id, out var n) || n != f.Name))
                {
                    FolderNames[id] = f.Name;
                    changed = true;
                }
            return changed;
        }
        /// <summary>Enough tabs per section for the stash's items (the page's "Fit items").</summary>
        public bool FullLayout { get; set; }
        public string? Preset { get; set; }
        public long Budget { get; set; }

        /// <summary>Default: Keep for a tab holding items, Reuse for an empty one.</summary>
        public TabChoice Of(TabSnap tab) => Tabs.TryGetValue(tab.Id, out var c) ? c : tab.ItemCount > 0 ? TabChoice.Keep : TabChoice.Reuse;

        /// <summary>Default: Keep when any of its tabs is kept, else Reuse.</summary>
        public TabChoice OfFolder(StashSnapshot snap, FolderSnap folder) =>
            Folders.TryGetValue(folder.Id, out var c) ? c : snap.TabsIn(folder.Id).Any(t => Of(t) == TabChoice.Keep) ? TabChoice.Keep : TabChoice.Reuse;

        public bool Reusable(TabSnap tab) => Of(tab) switch
        {
            TabChoice.Reuse => true,
            TabChoice.ReuseIfEmpty => tab.ItemCount == 0,
            _ => false,
        };

        /// <summary>Sets a folder's choice and copies it onto each of its tabs.</summary>
        public void SetFolder(StashSnapshot snap, int folderId, TabChoice choice)
        {
            Folders[folderId] = choice;
            foreach (var t in snap.TabsIn(folderId)) Tabs[t.Id] = choice;
        }

        public ulong Hash()
        {
            var h = new HashCode();
            foreach (var p in Tabs.OrderBy(p => p.Key)) h.Add(p);
            foreach (var p in Folders.OrderBy(p => p.Key)) h.Add(p);
            h.Add(FullLayout);
            h.Add(Preset);
            h.Add(Budget);
            return (ulong)(uint)h.ToHashCode();
        }
    }

    internal static class ChoicesFile
    {
        private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "stash", "stashes");

        private static string PathOf(StashKey key) => Path.Combine(Dir, key.File + ".json");

        /// <summary>The saved choices for a stash, with tab and folder ids it no longer has dropped; defaults without a file.</summary>
        public static Choices Load(StashKey key, StashSnapshot? snap)
        {
            var choices = new Choices();
            string path = PathOf(key);
            if (!File.Exists(path)) return choices;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return choices;
                Read(o["tabs"], choices.Tabs, choices.TabNames);
                Read(o["folders"], choices.Folders, choices.FolderNames);
                choices.FullLayout = (bool?)o["fullLayout"] ?? false;
                choices.Preset = (string?)o["preset"];
                choices.Budget = Math.Max(0, (long?)o["budget"] ?? 0);
                if (snap != null) choices.Validate(snap);
            }
            catch (Exception e)
            {
                Log.Warn($"Stash: choices for {key.Id} unreadable, defaults used: {e.Message}");
            }
            return choices;
        }

        // Each entry is {"choice", "name"}, or a bare choice from before names were kept.
        private static void Read(JsonNode? node, Dictionary<int, TabChoice> into, Dictionary<int, string> names)
        {
            if (node is not JsonObject o) return;
            foreach (var (k, v) in o)
            {
                if (!int.TryParse(k, out int id)) continue;
                string? text = v is JsonObject e ? (string?)e["choice"] : (string?)v;
                if (!Enum.TryParse<TabChoice>(text, true, out var c)) continue;
                into[id] = c;
                if (v is JsonObject entry && (string?)entry["name"] is { } name) names[id] = name;
            }
        }

        public static void Save(StashKey key, Choices choices)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tabs = new JsonObject();
                foreach (var (id, c) in choices.Tabs) tabs[id.ToString()] = new JsonObject { ["choice"] = c.ToString(), ["name"] = choices.TabNames.GetValueOrDefault(id) };
                var folders = new JsonObject();
                foreach (var (id, c) in choices.Folders) folders[id.ToString()] = new JsonObject { ["choice"] = c.ToString(), ["name"] = choices.FolderNames.GetValueOrDefault(id) };
                var o = new JsonObject
                {
                    ["fileVersion"] = 1,
                    ["tabs"] = tabs,
                    ["folders"] = folders,
                    ["fullLayout"] = choices.FullLayout,
                    ["preset"] = choices.Preset,
                    ["budget"] = choices.Budget,
                };
                string path = PathOf(key);
                // Written in place: a swap through a temporary file is refused while the last write still holds the file.
                File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("stash:choices", $"Stash: choices not saved: {e.Message}", 30);
            }
        }
    }
}
