namespace EpochAssist.Features;

internal static partial class Stash
{
    internal enum Fill { Done, Reuse, Buy }

    /// <summary>A folder of the planned stash: an existing one (kept, or renamed into a preset folder) or one to create.</summary>
    internal sealed class PlanFolder
    {
        public PresetFolder? Preset { get; init; }
        /// <summary>The existing folder it is; null for a folder to create.</summary>
        public int? ExistingId { get; init; }
        public string Name { get; init; } = string.Empty;
        public int IconId { get; init; }
        public int ColourId { get; init; }
        /// <summary>An existing folder renamed, recoloured or re-iconed into its preset folder.</summary>
        public bool Configure { get; init; }
        /// <summary>The player's own folder, left as it is.</summary>
        public bool Keep { get; init; }
        /// <summary>An empty folder no preset folder uses now, given back the game's default name.</summary>
        public bool Spare { get; init; }
        /// <summary>Place among the planned stash's folders, from 0.</summary>
        public int Index { get; set; }
    }

    /// <summary>One preset tab in the planned stash and what fills it.</summary>
    internal sealed class Target
    {
        public PresetTab Tab { get; init; } = new();
        /// <summary>1 for a section's first tab or a merged tab, 2… for the extra tabs that hold the stash's items.</summary>
        public int Copy { get; init; } = 1;
        /// <summary>How many tabs the section gets in all (1 without extra tabs).</summary>
        public int Of { get; init; } = 1;
        public TabRules Rules { get; init; } = TabRules.Empty;
        public Fill Fill { get; set; }
        /// <summary>The existing tab that fills it; null for one to buy.</summary>
        public int? TabId { get; set; }
        /// <summary>The game's price at the place this purchase comes in the step order; 0 for a reused tab.</summary>
        public long Price { get; set; }
        public PlanFolder Folder { get; set; } = new();
        /// <summary>Place among the planned folder's tabs, from 0.</summary>
        public int Index { get; set; }
        /// <summary>What a reused tab changes: name, rules, icon, colour, folder.</summary>
        public List<string> Changes { get; } = new();
        /// <summary>A reused tab in a Keep folder: configured where it is, never moved out.</summary>
        public bool InKeepFolder { get; set; }
        public int Order { get; init; }
        public string Name => Tab.Name;
    }

    internal sealed record OrderWarning(string TargetKey, int TakenById, string TakenByName, string Example);

    /// <summary>The stash a preset makes at a budget, worked out again from every read; never stored.</summary>
    internal sealed class Preview
    {
        public Preset Preset { get; init; } = new();
        public List<Target> Targets { get; } = new();
        public List<PlanFolder> Folders { get; } = new();
        public int Reusable { get; init; }
        public int NewTabs { get; set; }
        public long Cost { get; set; }
        public long FullCost { get; set; }
        public int FullTabs { get; set; }
        /// <summary>With enough tabs for the stash's items: how many extra tabs that adds to the whole preset, and their
        /// cost.</summary>
        public int ExtraTabs { get; set; }
        public long ExtraCost { get; set; }
        /// <summary>Merge keys whose group split at this budget.</summary>
        public HashSet<string> Split { get; } = new();
        public List<OrderWarning> Warnings { get; } = new();
        /// <summary>Why the preview stops short (tab or folder limit).</summary>
        public List<string> Notes { get; } = new();
        public ulong Fingerprint { get; init; }
    }

    /// <summary>Everything the planner reads: plain values only, so it runs on any data.</summary>
    internal sealed class PlanInput
    {
        public StashSnapshot Snapshot { get; init; } = new();
        public Preset Preset { get; init; } = new();
        public Choices Choices { get; init; } = new();
        public long Budget { get; init; }
        /// <summary>The game's price of the next tab with n owned.</summary>
        public Func<int, long> Price { get; init; } = n => n;
        public int MaxTabs { get; init; } = 200;
        public int MaxFolders { get; init; } = 100;
        /// <summary>Full layout: tabs needed per section key; null when off.</summary>
        public IReadOnlyDictionary<string, int>? Fill { get; init; }
        /// <summary>A preset tab's rules with its unique list filled in.</summary>
        public Func<PresetTab, TabRules> RulesOf { get; init; } = t => t.Rules;
    }

    // A thing to take: a section's first tab, a merged tab, or a duplicate.
    private sealed record Unit(PresetFolder Folder, PresetTab Tab, int Copy, int Order, string? Group);

    /// <summary>
    /// Plans the preset against the stash: reused tabs are free slots, the budget buys as many more as it pays for at the
    /// game's prices; units are taken first tabs before duplicates, then by rank, then preset order; a merge group splits
    /// only when all its sections fit; reused tabs go to the units they already match, then same name, then empty ones
    /// first; everything is placed in preset order.
    /// </summary>
    public static Preview Plan(PlanInput input, bool withFull = true)
    {
        var snap = input.Snapshot;
        var choices = input.Choices;
        var reusable = snap.Tabs.Where(choices.Reusable).ToList();
        int owned = snap.TabCount;

        // Units in preset order.
        var units = new List<Unit>();
        var groupOf = new Dictionary<string, string>();
        int order = 0;
        foreach (var (folder, tab) in input.Preset.AllTabs())
            if (tab.IsMerged)
                foreach (string k in tab.MergeOf!)
                    groupOf.TryAdd(k, tab.Key);
        foreach (var (folder, tab) in input.Preset.AllTabs())
        {
            units.Add(new Unit(folder, tab, 1, order++, tab.IsMerged ? tab.Key : groupOf.GetValueOrDefault(tab.Key)));
        }
        var needOf = new Dictionary<string, int>();
        if (input.Fill != null)
            foreach (var (folder, tab) in input.Preset.AllTabs().Where(p => !p.Tab.IsMerged).ToList())
            {
                int need = Math.Max(input.Fill.GetValueOrDefault(tab.Key, 1), tab.SavedCount ?? 1);
                needOf[tab.Key] = need;
                var first = units.First(u => u.Tab == tab);
                for (int c = 2; c <= need; c++) units.Add(new Unit(folder, tab, c, first.Order, first.Group));
            }
        var ranked = units.OrderBy(u => u.Copy > 1 ? 1 : 0).ThenBy(u => u.Tab.Rank).ThenBy(u => u.Order).ThenBy(u => u.Copy).ToList();

        // How many tabs the budget buys, within the tab limit.
        int maxBuy = Math.Max(0, input.MaxTabs - owned);
        int buys = 0;
        long spent = 0;
        while (buys < maxBuy && spent + input.Price(owned + buys) <= input.Budget && buys < units.Count)
            spent += input.Price(owned + buys++);
        int slots = reusable.Count + buys;

        // Groups start folded; each splits when all its sections then fit.
        var merged = units.Where(u => u.Tab.IsMerged).Select(u => u.Tab.Key).ToList();
        var split = new HashSet<string>();
        List<Unit> Take()
        {
            var taken = new List<Unit>();
            foreach (var u in ranked)
            {
                if (taken.Count >= slots) break;
                if (u.Tab.IsMerged)
                {
                    // A split group's merged tab is no target: a tab already holding it is the player's to reuse or keep.
                    if (split.Contains(u.Tab.Key)) continue;
                }
                else if (u.Group != null && !split.Contains(u.Group)) continue;
                taken.Add(u);
            }
            return taken;
        }
        var take = Take();
        foreach (string g in merged.OrderBy(g => units.First(u => u.Tab.Key == g).Tab.Rank))
        {
            split.Add(g);
            var trial = Take();
            var sections = units.Where(u => u.Group == g && !u.Tab.IsMerged && u.Copy == 1).ToList();
            if (sections.All(trial.Contains)) take = trial;
            else split.Remove(g);
        }

        var preview = new Preview { Preset = input.Preset, Reusable = reusable.Count, Fingerprint = snap.Fingerprint };
        preview.Split.UnionWith(split);
        var targets = take.Select(u => new Target { Tab = u.Tab, Copy = u.Copy, Of = needOf.GetValueOrDefault(u.Tab.Key, 1), Rules = input.RulesOf(u.Tab), Order = u.Order }).ToList();
        var folderOf = take.ToDictionary(u => (u.Tab.Key, u.Copy), u => u.Folder);

        // Reused tabs: exact matches, then a split group's merged tab (when the player lets it go) as the group's first
        // section, then same name,
        // then by rank with empty tabs first.
        var free = new List<TabSnap>(reusable);
        bool Same(TabSnap t, Target x) => t.Name == x.Tab.Name && t.IconId == x.Tab.IconId && t.ColourId == x.Tab.ColourId && t.Rules.Equals(x.Rules);
        void Give(Target x, TabSnap t)
        {
            x.TabId = t.Id;
            free.Remove(t);
        }
        foreach (var x in targets)
            if (free.FirstOrDefault(t => Same(t, x)) is { } t) Give(x, t);
        foreach (string g in split)
        {
            var mergedTab = input.Preset.AllTabs().First(p => p.Tab.Key == g).Tab;
            var mergedRules = input.RulesOf(mergedTab);
            var firstSection = targets.Where(x => x.TabId == null && x.Copy == 1 && groupOf.GetValueOrDefault(x.Tab.Key) == g).OrderBy(x => x.Order).FirstOrDefault();
            if (firstSection != null && free.FirstOrDefault(t => t.Name == mergedTab.Name && t.Rules.Equals(mergedRules)) is { } holder) Give(firstSection, holder);
        }
        foreach (var x in targets.Where(x => x.TabId == null))
            if (free.FirstOrDefault(t => t.Name == x.Tab.Name) is { } t) Give(x, t);
        foreach (var x in targets.Where(x => x.TabId == null))
        {
            var t = free.OrderBy(t => t.ItemCount > 0 ? 1 : 0).ThenBy(t => snap.Tabs.IndexOf(t)).FirstOrDefault();
            if (t == null) break;
            Give(x, t);
        }

        // Purchases in rank order get the game's price at their turn.
        int bought = 0;
        foreach (var x in targets.Where(x => x.TabId == null))
        {
            x.Fill = Fill.Buy;
            x.Price = input.Price(owned + bought++);
            preview.Cost += x.Price;
        }
        preview.NewTabs = bought;
        if (buys >= maxBuy && maxBuy < units.Count - reusable.Count) preview.Notes.Add($"The stash holds at most {input.MaxTabs} tabs.");

        PlaceFolders(input, preview, targets, folderOf);
        PlaceTabs(snap, targets);
        foreach (var x in targets.Where(x => x.TabId != null))
        {
            var t = snap.Tab(x.TabId!.Value)!;
            if (t.Name != x.Tab.Name) x.Changes.Add("name");
            if (!t.Rules.Equals(x.Rules)) x.Changes.Add("rules");
            if (t.IconId != x.Tab.IconId) x.Changes.Add("icon");
            if (t.ColourId != x.Tab.ColourId) x.Changes.Add("colour");
            if (!x.InKeepFolder && x.Folder.ExistingId != t.FolderId) x.Changes.Add("folder");
            x.Fill = x.Changes.Count == 0 ? Fill.Done : Fill.Reuse;
        }
        preview.Targets.AddRange(targets.OrderBy(x => x.Folder.Index).ThenBy(x => x.Index));

        if (withFull)
        {
            var full = Plan(new PlanInput
            {
                Snapshot = snap, Preset = input.Preset, Choices = choices, Budget = long.MaxValue, Price = input.Price,
                MaxTabs = input.MaxTabs, MaxFolders = input.MaxFolders, Fill = input.Fill, RulesOf = input.RulesOf,
            }, withFull: false);
            preview.FullCost = full.Cost;
            preview.FullTabs = full.NewTabs;
            if (input.Fill != null)
            {
                // What the extra tabs add: the whole preset with one tab per section against the whole preset with them.
                var one = Plan(new PlanInput
                {
                    Snapshot = snap, Preset = input.Preset, Choices = choices, Budget = long.MaxValue, Price = input.Price,
                    MaxTabs = input.MaxTabs, MaxFolders = input.MaxFolders, Fill = null, RulesOf = input.RulesOf,
                }, withFull: false);
                preview.ExtraTabs = Math.Max(0, full.NewTabs - one.NewTabs);
                preview.ExtraCost = Math.Max(0, full.Cost - one.Cost);
            }
        }
        else
        {
            preview.FullCost = preview.Cost;
            preview.FullTabs = preview.NewTabs;
        }
        return preview;
    }

    // Preset folders the targets need, in preset order, never moved: the game's own folder delete only works while
    // every folder's id is its place, so folders keep their places and the order comes from which folder becomes which.
    // The player's folders they let go (not Keep) are taken in stash order and renamed into the preset folders; the rest
    // are created at the end. A reused tab in a Keep folder stays in it.
    private static void PlaceFolders(PlanInput input, Preview preview, List<Target> targets, Dictionary<(string, int), PresetFolder> folderOf)
    {
        var snap = input.Snapshot;
        var choices = input.Choices;
        var keepPlans = new Dictionary<int, PlanFolder>();
        PlanFolder KeepPlan(FolderSnap f) => keepPlans.TryGetValue(f.Id, out var p) ? p
            : keepPlans[f.Id] = new PlanFolder { ExistingId = f.Id, Name = f.Name, IconId = f.IconId, ColourId = f.ColourId, Keep = true };
        bool Keep(FolderSnap f) => choices.OfFolder(snap, f) switch
        {
            TabChoice.Keep => true,
            TabChoice.ReuseIfEmpty => snap.TabsIn(f.Id).Any(t => t.ItemCount > 0),
            _ => false,
        };

        foreach (var x in targets)
            if (x.TabId is { } id && snap.Tab(id) is { } t && snap.Folder(t.FolderId) is { } f && Keep(f))
            {
                x.InKeepFolder = true;
                x.Folder = KeepPlan(f);
            }

        var slots = snap.Folders.Where(f => !Keep(f)).ToList();
        // On the client copy folders can move (the server's delete finds a folder by its id): a preset folder keeps the
        // folder already named for it, moved into place if need be (one message, where renaming the folders in between
        // costs one each); else it takes the next free folder in stash order, which needs no move. On the save file's copy
        // they never move: free folders are taken in stash order. A reused tab joins its folder in its own configure.
        bool movable = snap.ClientCopy;
        var presetFolderNames = new HashSet<string>(input.Preset.Folders.Where(pf => targets.Any(x => folderOf[(x.Tab.Key, x.Copy)] == pf)).Select(pf => pf.Name));
        var taken = new HashSet<int>();
        FolderSnap? Pick(PresetFolder pf, List<Target> mine)
        {
            var free = slots.Where(f => !taken.Contains(f.Id)).ToList();
            if (free.Count == 0) return null;
            if (!movable) return free[0];
            // A folder named for a later preset folder stays for that one.
            return free.FirstOrDefault(f => f.Name == pf.Name)
                ?? free.FirstOrDefault(f => !presetFolderNames.Contains(f.Name)) ?? free[0];
        }
        int newFolders = 0;
        var byExisting = new Dictionary<int, PlanFolder>();
        var created = new List<PlanFolder>();
        foreach (var pf in input.Preset.Folders)
        {
            var mine = targets.Where(x => !x.InKeepFolder && folderOf[(x.Tab.Key, x.Copy)] == pf).ToList();
            if (mine.Count == 0) continue;
            PlanFolder plan;
            if (Pick(pf, mine) is { } existing)
            {
                taken.Add(existing.Id);
                plan = new PlanFolder
                {
                    Preset = pf, ExistingId = existing.Id, Name = pf.Name, IconId = pf.IconId, ColourId = pf.ColourId,
                    Configure = existing.Name != pf.Name || existing.IconId != pf.IconId || existing.ColourId != pf.ColourId,
                };
                byExisting[existing.Id] = plan;
            }
            else
            {
                if (snap.Folders.Count + newFolders >= input.MaxFolders)
                {
                    preview.Notes.Add($"The stash holds at most {input.MaxFolders} folders.");
                    foreach (var x in mine) targets.Remove(x);
                    continue;
                }
                newFolders++;
                plan = new PlanFolder { Preset = pf, Name = pf.Name, IconId = pf.IconId, ColourId = pf.ColourId };
                created.Add(plan);
            }
            foreach (var x in mine) x.Folder = plan;
        }

        // A folder left over that isn't the player's to keep and still carries a preset folder's name (renamed into place
        // for an earlier plan) goes back to the game's own default name, so it reads as the empty spare it is.
        var presetNames = new HashSet<string>(input.Preset.Folders.Select(pf => pf.Name));
        PlanFolder Spare(FolderSnap f, int place)
        {
            string name = $"Category {place + 1}";
            return new PlanFolder { ExistingId = f.Id, Name = name, Spare = true, Configure = f.Name != name || f.IconId != 0 || f.ColourId != 0 };
        }
        // Final order. Fixed: every existing folder where it is (as planned, as a spare, or as it is), then the new ones.
        // Movable: kept folders where they are, the planned ones in preset order in the other places, then the rest.
        List<PlanFolder> order;
        if (!movable)
            order = snap.Folders.Select((f, i) => byExisting.TryGetValue(f.Id, out var p) ? p
                : !Keep(f) && presetNames.Contains(f.Name) && !snap.TabsIn(f.Id).Any() ? Spare(f, i) : KeepPlan(f)).Concat(created).ToList();
        else
        {
            var planned = input.Preset.Folders.Select(pf => byExisting.Values.Concat(created).FirstOrDefault(p => p.Preset == pf)).OfType<PlanFolder>().ToList();
            var rest = snap.Folders.Where(f => !Keep(f) && !byExisting.ContainsKey(f.Id)).ToList();
            int total = snap.Folders.Count + created.Count;
            order = new List<PlanFolder>(total);
            int next = 0;
            for (int i = 0; i < total; i++)
            {
                if (i < snap.Folders.Count && Keep(snap.Folders[i])) order.Add(KeepPlan(snap.Folders[i]));
                else if (next < planned.Count) order.Add(planned[next++]);
                else
                {
                    var f = rest[0];
                    rest.RemoveAt(0);
                    order.Add(presetNames.Contains(f.Name) && !snap.TabsIn(f.Id).Any() ? Spare(f, i) : KeepPlan(f));
                }
            }
        }
        for (int i = 0; i < order.Count; i++) order[i].Index = i;
        preview.Folders.AddRange(order);
        // A folder of the player's own left out of its place before: the game's folder delete then removes another.
        if (!snap.ClientCopy && snap.Folders.Select((f, i) => f.Id != i).Any(b => b))
            preview.Notes.Add("Folders were moved before: deleting a folder in the game's window removes the wrong one.");
    }

    // Within each planned folder: tabs that aren't targets keep their places (Keep tabs above), targets follow in preset
    // order; a Keep folder's reused tab stays where it is.
    private static void PlaceTabs(StashSnapshot snap, List<Target> targets)
    {
        var targetTabs = new HashSet<int>(targets.Where(x => x.TabId != null).Select(x => x.TabId!.Value));
        foreach (var group in targets.GroupBy(x => x.Folder))
        {
            var folder = group.Key;
            var stays = folder.ExistingId is { } fid ? snap.TabsIn(fid).Where(t => !targetTabs.Contains(t.Id)).ToList() : new List<TabSnap>();
            if (folder.Keep)
            {
                // Configured in place: the target keeps its tab's place.
                foreach (var x in group)
                    x.Index = snap.TabsIn(folder.ExistingId!.Value).ToList().FindIndex(t => t.Id == x.TabId);
                continue;
            }
            int i = stays.Count;
            foreach (var x in group.OrderBy(x => x.Order).ThenBy(x => x.Copy)) x.Index = i++;
        }
    }
}
