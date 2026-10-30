using System.Security.Cryptography;
using System.Text;
using EpochAssist.Core;
using Il2Cpp;
using Il2CppLE.Data;

namespace EpochAssist.Features;

internal static partial class Stash
{
    /// <summary>Which season stash a snapshot is, for the per-stash choices file.</summary>
    internal sealed record StashKey(string Id)
    {
        /// <summary>The choices file's name: the id with anything but letters, digits and dashes made a dash.</summary>
        public string File => new(Id.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray());
    }

    internal sealed record FolderSnap(int Id, int Order, string Name, int IconId, int ColourId, TabRules Rules, bool Untouched);

    internal sealed record TabSnap(int Id, int FolderId, int Order, string Name, int IconId, int ColourId, TabRules Rules,
        bool IgnoresFolderRules, int ItemCount, int CellsUsed, bool Untouched);

    /// <summary>The stash as the game held it at one read, in plain values only; folders and tabs in game order.</summary>
    internal sealed class StashSnapshot
    {
        public StashKey Key { get; init; } = new(string.Empty);
        /// <summary>"saved" (the save file's copy, changed in place) or "client" (the copy the game keeps in sync, changed
        /// by the game's messages).</summary>
        public string Source { get; init; } = "saved";
        public List<FolderSnap> Folders { get; init; } = new();
        public List<TabSnap> Tabs { get; init; } = new();
        public int Gold { get; init; }
        public ulong Fingerprint { get; init; }
        /// <summary>Folders or tabs the game left sharing one id (names joined); empty normally.</summary>
        public List<string> SharedIds { get; init; } = new();
        public int TabCount => Tabs.Count;
        public bool ClientCopy => Source == "client";

        public FolderSnap? Folder(int id) => Folders.Find(f => f.Id == id);
        public TabSnap? Tab(int id) => Tabs.Find(t => t.Id == id);
        public IEnumerable<TabSnap> TabsIn(int folderId) => Tabs.Where(t => t.FolderId == folderId);
    }

    /// <summary>Cells in one stash tab (12 × 17 in 1.5.3).</summary>
    public const int TabCells = 12 * 17;

    /// <summary>The cells Fit items counts in a tab: <see cref="TabCells"/>, unless a dev verb shrinks it so a few items
    /// fill more than one tab.</summary>
    internal static int FillTabCells { get; private set; } = TabCells;

    internal static void SetFillTabCells(int cells)
    {
        FillTabCells = Math.Clamp(cells, 1, TabCells);
        s_fill = null;
    }

    /// <summary>
    /// Reads the current stash: the save file's copy when there is one (the client copy can then hold an older stash), else
    /// the client copy. Null without a loaded stash. Item counts come from the saved tab, or from the live container's tab
    /// of the same place.
    /// </summary>
    public static StashSnapshot? Read()
    {
        try
        {
            var info = (LocalPlayer.instance ?? PlayerFinder.localPlayer)?.stashInfo;
            int gold = Gold() is { } g ? g.value : 0;
            var saved = PlayerFinder.getGlobalDataTracker()?.stash;
            if (saved?.Tabs != null && saved.Categories != null && saved.Tabs.Count > 0)
            {
                var tabs = new StashTab[saved.Tabs.Count];
                for (int i = 0; i < tabs.Length; i++) tabs[i] = saved.Tabs[i];
                return Make("saved", new StashKey(saved.Id), saved.Categories.ToArray(), tabs, gold);
            }
            if (info?.tabs == null || info.tabs.Count == 0 || info.categories == null) return null;
            return Make("client", ClientKey(), info.categories.ToArray(), info.tabs.ToArray(), gold);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stash:read", $"Stash: read failed: {e.Message}", 30);
            return null;
        }
    }

    private static StashSnapshot Make(string source, StashKey key, StashCategory[] categories, StashTab[] tabs, int gold)
    {
        var cells = ContainerCells(tabs.Length);
        var folders = categories.OrderBy(c => c.DisplayOrder)
            .Select(c =>
            {
                string name = c.DisplayName ?? string.Empty;
                return new FolderSnap(c.CategoryId, c.DisplayOrder, name, c.IconId, c.ColorId, Rules.FromGame(c.FolderPriority),
                    DefaultFolderName(name) && c.IconId == 0 && c.ColorId == 0);
            }).ToList();
        // The game can leave two folders (or tabs) with one id after a delete; the first in order keeps the id here,
        // and no step is sent while any id is shared.
        var folderOrder = new Dictionary<int, int>();
        for (int i = 0; i < folders.Count; i++) folderOrder.TryAdd(folders[i].Id, i);
        var list = new List<TabSnap>(tabs.Length);
        for (int i = 0; i < tabs.Length; i++)
        {
            var t = tabs[i];
            var rules = Rules.FromGame(t.TabPriority);
            // The live containers are kept by tab id, not by the tab list's order.
            bool live = t.TabId >= 0 && t.TabId < cells.Count;
            int items = source == "saved" ? t.SavedItems?.Count ?? 0 : live ? cells[t.TabId].Items : 0;
            int used = live ? cells[t.TabId].Cells : items;
            string name = t.DisplayName ?? string.Empty;
            list.Add(new TabSnap(t.TabId, t.CategoryId, t.DisplayOrder, name, t.IconId, t.ColorId, rules, t.IgnoreFolderPriority, items, used,
                IsDefaultTabName(name) && t.IconId == 0 && t.ColorId == 0 && rules.IsEmpty && items == 0));
        }
        list.Sort((a, b) =>
        {
            int fa = folderOrder.TryGetValue(a.FolderId, out int x) ? x : int.MaxValue, fb = folderOrder.TryGetValue(b.FolderId, out int y) ? y : int.MaxValue;
            return fa != fb ? fa.CompareTo(fb) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id);
        });
        var shared = folders.GroupBy(f => f.Id).Where(g => g.Count() > 1).Select(g => string.Join(" and ", g.Select(f => f.Name)))
            .Concat(list.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => string.Join(" and ", g.Select(t => t.Name)))).ToList();
        return new StashSnapshot
        {
            Key = key, Source = source, Folders = folders, Tabs = list, Gold = gold, SharedIds = shared,
            Fingerprint = Fingerprint(folders, list),
        };
    }

    // The live stash's tab containers, by tab id (container n holds tab n's items, whatever the tab list's order): items
    // and cells their items cover. Empty when the counts don't line up with the tabs.
    private static List<(int Items, int Cells)> ContainerCells(int tabs)
    {
        var result = new List<(int, int)>();
        var holder = ItemContainersManager.Instance?.stash;
        var all = holder?.stashItemContainers;
        if (all == null || all.Length == 0) return result;
        int index = holder!._CurrentStashIndex_k__BackingField;
        var current = index >= 0 && index < all.Length ? all[index] : all[0];
        var containers = current?.containers;
        if (containers == null || containers.Count != tabs) return result;
        for (int i = 0; i < containers.Count; i++)
        {
            var content = containers[i]?.content;
            int items = content?.Count ?? 0, cells = 0;
            if (content != null)
                for (int k = 0; k < content.Count; k++)
                    if (content[k] is { } e) cells += Math.Max(1, e.size.x * e.size.y);
            result.Add((items, cells));
        }
        return result;
    }

    private static bool IsDefaultTabName(string name) => name.StartsWith("Stash Tab ", StringComparison.Ordinal) && int.TryParse(name[10..], out _);

    private static bool DefaultFolderName(string name) =>
        name == "General" || name.StartsWith("Category ", StringComparison.Ordinal) && int.TryParse(name[9..], out _);

    // The client copy has no id of its own: the character's mode and cycle name it, as the game names its
    // season stashes. A solo challenge stash belongs to one character, named by a hash, never the name itself.
    private static StashKey ClientKey()
    {
        // The player's data tracker, as the mod's profiles read it; the finder's own lookups can come back empty.
        var data = Player.Actor?.characterDataTracker?.charData ?? PlayerFinder.getPlayerDataTracker()?.charData ?? PlayerFinder.getPlayerData();
        if (data == null) return new StashKey("client");
        string mode = data.Hardcore ? "hardcore" : "softcore";
        string key = $"client-{mode}-{(int)(byte)data.Cycle}";
        if (data.SoloChallenge)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data.CharacterName ?? string.Empty));
            key += "-solo-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }
        return new StashKey(key);
    }

    // FNV-1a over every value a step could depend on.
    private static ulong Fingerprint(List<FolderSnap> folders, List<TabSnap> tabs)
    {
        ulong h = 14695981039346656037UL;
        void Add(long v)
        {
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        }
        void AddText(string s)
        {
            foreach (char c in s) Add(c);
            Add(-1);
        }
        foreach (var f in folders)
        {
            Add(f.Id);
            Add(f.Order);
            AddText(f.Name);
            Add(f.IconId);
            Add(f.ColourId);
            Add(f.Rules.GetHashCode());
        }
        foreach (var t in tabs)
        {
            Add(t.Id);
            Add(t.FolderId);
            Add(t.Order);
            AddText(t.Name);
            Add(t.IconId);
            Add(t.ColourId);
            Add(t.Rules.GetHashCode());
            Add(t.IgnoresFolderRules ? 1 : 0);
            Add(t.ItemCount);
        }
        return h;
    }

    /// <summary>The player's gold tracker: the cached one, else the game's lookup; null without a character.</summary>
    public static GoldTracker? Gold()
    {
        var g = PlayerFinder.localGoldTracker;
        if (!Game.Alive(g)) g = PlayerFinder.getLocalGoldTracker();
        return Game.Alive(g) ? g : null;
    }

    // ---- Prices ----

    /// <summary>The game's price of the next tab with this many owned.</summary>
    public static long Price(int owned) => StashHelpers.NextTabCost(owned);

    /// <summary>The most tabs the stash may hold.</summary>
    public static int MaxTabs => StashConstants.MaxTabs;

    /// <summary>The most folders the stash may hold.</summary>
    public static int MaxFolders => StashManager.MaxFolderCount;
}
