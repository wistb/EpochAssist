using System.Security.Cryptography;
using System.Text;
using EpochAssist.Core;
using Il2Cpp;
using Il2CppItemFiltering;

namespace EpochAssist.Features;

internal static partial class Planner
{
    /// <summary>The game's loot filter classes and its serializer, which every made filter goes through.</summary>
    public const string LootFilterGroup = "loot filter";

    /// <summary>The game's loot filter window and its settings panel, made to show a filter Use now loads.</summary>
    public const string LootFilterPanelGroup = "loot filter panel";

    /// <summary>
    /// The game's own filter objects: listing, loading and saving filter files in its Filters folder. Game objects live
    /// only while a filter is made, loaded or judged; nothing here changes the player's active filter.
    /// </summary>
    internal static class LootFilterGame
    {
        /// <summary>The game's filter manager, or null before it's made.</summary>
        public static ItemFilterManager? Manager
        {
            get
            {
                var manager = ItemFilterManager.Instance;
                return Game.Alive(manager) ? manager : null;
            }
        }

        /// <summary>The game's Filters folder, or null before the manager is made.</summary>
        public static string? FilterFolder => Manager != null && ItemFilterManager.filtersPath is { Length: > 0 } path ? path : null;

        /// <summary>The most rules one filter holds; disabled rules count.</summary>
        public static int Limit => ItemFilter.MAX_RULES_PER_FILTER;

        /// <summary>The filter the player uses now, by name, or null.</summary>
        public static string? ActiveName => Manager?.Filter?.name;

        /// <summary>The filter files the game lists, by name, with the two numbers it keeps for each.</summary>
        public static List<(string Name, int A, int B)> ListFilters()
        {
            var list = new List<(string, int, int)>();
            var files = Manager?.GetFilterFileList();
            if (files == null) return list;
            for (int i = 0; i < files.Count; i++)
            {
                var row = files[i];
                list.Add((row.Item1, row.Item2, row.Item3));
            }
            return list;
        }

        /// <summary>Makes a filter file the active filter, as the game's panel does; only ever on the player's press.</summary>
        public static bool Use(string name)
        {
            try
            {
                // Loading the filter already in use again (after Make): the window keeps its place in the list.
                float? scroll = ActiveName == name ? WindowScroll() : null;
                if (Manager is not { } manager || !manager.LoadFilter(name)) return false;
                // Loading alone leaves the character's binding on the old filter; the panel sets both.
                manager.UpdateBindingForActiveCharacter(name);
                RefreshPanels(scroll);
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:use", $"Planner: loot filter {name} couldn't be made active: {e.Message}", 30);
                return false;
            }
        }

        // An open loot filter window keeps showing the filter it was opened on until it builds itself again, and loading a
        // filter can start that rebuild on its own; a second rebuild started meanwhile draws rules twice. So the window is
        // looked at over the next frames: left alone while it builds, rebuilt once only if it doesn't show the filter in use,
        // then its filter choice (and the settings panel's) set to the filter in use.
        private static int s_refreshFrames;
        private const int RefreshFrames = 60;

        // Where the window's rule list was scrolled to (its content's offset, in pixels), to put back once it's rebuilt, and
        // how many more frames it's kept there: the window goes back to the top more than once while it settles.
        private static float? s_keepScroll;
        private static int s_scrollFrames;
        private const int ScrollFrames = 60;

        private static void RefreshPanels(float? scroll = null)
        {
            if (s_run?.Feature is not { } feature || !feature.Has(LootFilterPanelGroup)) return;
            s_refreshFrames = RefreshFrames;
            s_keepScroll = scroll is > 1f ? scroll : null;
            // Held from this frame on, through the rebuild, so the list is never drawn anywhere else.
            s_scrollFrames = s_keepScroll != null ? ScrollFrames : 0;
            KeepScroll();
        }

        /// <summary>What the hold found each pass (the list's offset before it was put back), for the dev bridge.</summary>
        internal static List<string>? ScrollTrace { get; set; }

        // The open window's rule list offset, or null when no window is open.
        private static float? WindowScroll()
        {
            if (s_run?.Feature is not { } feature || !feature.Has(LootFilterPanelGroup)) return null;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LootFilterUI>()))
                if (obj.TryCast<LootFilterUI>() is { } ui && Game.Alive(ui) && ui.gameObject.activeInHierarchy && Game.Alive(ui.autoScrollScrollRect?.content))
                    return ui.autoScrollScrollRect!.content.anchoredPosition.y;
            return null;
        }

        // Holds the rule list where it was for about a second after the rebuild: the window moves it back more than once
        // (its own scroll to the chosen rule, its layout settling), and no one scrolls in that second.
        private static void KeepScroll(bool late = false)
        {
            if (s_keepScroll is not float y || s_scrollFrames <= 0) return;
            // Counted once a frame; while the window still builds the hold doesn't run out.
            if (late && s_refreshFrames <= 0) s_scrollFrames--;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LootFilterUI>()))
            {
                if (obj.TryCast<LootFilterUI>() is not { } ui || !Game.Alive(ui) || !ui.gameObject.activeInHierarchy) continue;
                var rect = ui.autoScrollScrollRect;
                if (!Game.Alive(rect) || !Game.Alive(rect!.content)) continue;
                var at = rect.content.anchoredPosition;
                ScrollTrace?.Add($"{UnityEngine.Time.frameCount} {(late ? "late" : "update")} {at.y:0}{(ui._isBuildingUi ? " building" : string.Empty)}");
                if (Math.Abs(at.y - y) < 0.5f) continue;
                rect.StopMovement();
                rect.content.anchoredPosition = new UnityEngine.Vector2(at.x, y);
            }
            if (s_scrollFrames == 0) s_keepScroll = null;
        }

        /// <summary>Once a frame, while a refresh is waiting: see <see cref="RefreshPanels"/>.</summary>
        /// <summary>The hold's second pass each frame, after the game's own scripts have moved the list.</summary>
        public static void LateFrame() => KeepScroll(late: true);

        public static void RefreshFrame()
        {
            KeepScroll();
            if (s_refreshFrames <= 0) return;
            s_refreshFrames--;
            try
            {
                var active = Manager?.Filter;
                string? name = active?.name;
                bool waiting = false;
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LootFilterUI>()))
                {
                    if (obj.TryCast<LootFilterUI>() is not { } ui || !Game.Alive(ui) || !ui.gameObject.activeInHierarchy) continue;
                    if (ui._isBuildingUi)
                    {
                        waiting = true;
                        continue;
                    }
                    if (active != null && ui._lastActiveFilterWithUiBuilt?.Pointer != active.Pointer)
                    {
                        ui.ForcedInit(ItemFilterManager.ChangeType.Loaded);
                        waiting = true;
                        continue;
                    }
                    Select(ui.filterSelector, name);
                }
                if (waiting) return;
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LootFilterSettingsPanelUI>()))
                {
                    if (obj.TryCast<LootFilterSettingsPanelUI>() is not { } panel || !Game.Alive(panel) || !panel.gameObject.activeInHierarchy) continue;
                    Select(panel.filterSelector, name);
                }
                s_refreshFrames = 0;
                // Built: from now on the list is held where it was for a while longer.
                if (s_keepScroll != null) s_scrollFrames = ScrollFrames;
            }
            catch (Exception e)
            {
                s_refreshFrames = 0;
                s_keepScroll = null;
                Log.ErrorThrottled("planner:filter:panel", $"Planner: the game's loot filter window couldn't be refreshed: {e.Message}", 30);
            }
        }

        // The filter choice shows the active filter's entry, chosen without its change event (which would load it again).
        private static void Select(Il2CppLE.UI.Controls.ColoredIconDropdown? selector, string? name)
        {
            if (!Game.Alive(selector) || name == null || selector!.options is not { } options) return;
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i]?.text != name) continue;
                if (selector.value != i) selector.SetValueWithoutNotify(i);
                return;
            }
        }

        /// <summary>The path of a filter file by its name.</summary>
        public static string? PathOf(string name) => FilterFolder is { } folder ? System.IO.Path.Combine(folder, name + ".xml") : null;

        /// <summary>The mod's own folder for filter data: choices, records and the copy a base is read from.</summary>
        public static string DataDir => System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "EpochAssist", "planner", "filters");

        /// <summary>
        /// A filter read from its file by name; never made the active filter. The game rewrites a file of an older
        /// format as it loads it, so the file is read from a copy in the mod's own folder and the player's file is never
        /// touched. Null when it can't be read.
        /// </summary>
        public static ItemFilter? LoadByName(string name)
        {
            if (PathOf(name) is not { } path || !File.Exists(path)) return null;
            string copy = System.IO.Path.Combine(DataDir, "base-copy.xml");
            try
            {
                Directory.CreateDirectory(DataDir);
                File.Copy(path, copy, true);
                return ItemFilterManager.LoadFilterFromPath(copy, name, false);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:load", $"Planner: loot filter {name} couldn't be read: {e.Message}", 30);
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(copy)) File.Delete(copy);
                }
                catch (IOException)
                {
                    // Replaced by the next read.
                }
            }
        }

        /// <summary>
        /// Writes a filter with the game's serializer to a temporary file beside <paramref name="path"/>, then moves it into
        /// place. Returns the SHA-256 of the bytes written, or null when nothing was written (no temporary file is left).
        /// </summary>
        public static string? SerializeTo(ItemFilter filter, string path)
        {
            if (Manager is not { } manager || ItemFilterManager.serializer is not { } serializer) return null;
            string tmp = path + ".tmp";
            try
            {
                filter.UpdateRuleOrders();
                filter.UpdatedLastEditVersion();
                filter.PreSerialization();
                byte[] bytes = Serialize(serializer, manager.ns, filter);
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, true);
                return Hash(bytes);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:save", $"Planner: loot filter {System.IO.Path.GetFileName(path)} couldn't be saved: {e.Message}", 30);
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch (IOException)
                {
                    // Left for the next save to replace.
                }
                return null;
            }
        }

        /// <summary>The fingerprint a filter would have once saved, without writing it; null when it can't be serialised.</summary>
        public static string? HashOf(ItemFilter filter)
        {
            if (Manager is not { } manager || ItemFilterManager.serializer is not { } serializer) return null;
            try
            {
                filter.UpdateRuleOrders();
                filter.UpdatedLastEditVersion();
                filter.PreSerialization();
                return Hash(Serialize(serializer, manager.ns, filter));
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:save", $"Planner: loot filter {filter.name} couldn't be serialised: {e.Message}", 30);
                return null;
            }
        }

        // The game's files: UTF-8 with its mark, two-space indents, no XML declaration.
        private static byte[] Serialize(Il2CppSystem.Xml.Serialization.XmlSerializer serializer, Il2CppSystem.Xml.Serialization.XmlSerializerNamespaces? ns,
            ItemFilter filter)
        {
            var stream = new Il2CppSystem.IO.MemoryStream();
            var settings = new Il2CppSystem.Xml.XmlWriterSettings { Indent = true, OmitXmlDeclaration = true };
            var writer = Il2CppSystem.Xml.XmlWriter.Create(stream, settings);
            serializer.Serialize(writer, filter, ns);
            writer.Flush();
            writer.Close();
            return stream.ToArray();
        }

        /// <summary>
        /// The game filter for a plan: its name, description and icon, and one game rule per plan rule. A foreign base's
        /// rules are cloned from <paramref name="baseFilter"/> unchanged. The plan lists rules top first; the game keeps
        /// its list bottom first, so the rules are added from the bottom up.
        /// </summary>
        public static ItemFilter Build(FilterPlan plan, ItemFilter? baseFilter = null)
        {
            // A new filter's format is 0, which the game would take for an old file and convert on load (map icons and
            // beams lost); it's made in the current one.
            var filter = new ItemFilter(plan.Name) { description = plan.Description, lootFilterVersion = ItemFilter.CURRENT_LF_VERSION };
            if (baseFilter != null)
            {
                filter.filterIcon = baseFilter.filterIcon;
                filter.filterIconColor = baseFilter.filterIconColor;
            }
            for (int i = plan.Rules.Count - 1; i >= 0; i--)
            {
                var rule = plan.Rules[i];
                var made = rule.BaseIndex >= 0 && baseFilter?.rules is { } baseRules && rule.BaseIndex < baseRules.Count
                    ? baseRules[rule.BaseIndex].Clone().Cast<Rule>()
                    : MakeRule(rule);
                made.isEnabled = rule.Enabled;
                filter.AddRule(made);
            }
            return filter;
        }

        /// <summary>One game rule from a plan rule: outcome, name, look and conditions.</summary>
        public static Rule MakeRule(PlanRule plan)
        {
            var look = plan.Look;
            var rule = new Rule(plan.Hide ? Rule.RuleOutcome.HIDE : Rule.RuleOutcome.SHOW, look.Colour ?? 0, look.Emphasized)
            {
                nameOverride = plan.Name,
                isEnabled = plan.Enabled,
                recolor = look.Colour != null,
                emphasized = look.Emphasized,
                SoundId = look.Sound,
                MapIconId = look.MapIcon,
            };
            if (look.Colour is int colour) rule.color = colour;
            if (look.Beam is { } beam)
            {
                rule.BeamOverride = true;
                rule.BeamSizeOverride = (EpochColor.BeamSize)beam.Size;
                rule.BeamColorOverride = beam.Colour;
            }
            foreach (var condition in Conditions(plan.Conditions)) rule.conditions.Add(condition);
            return rule;
        }

        private static IEnumerable<Condition> Conditions(RuleConditions c)
        {
            if (c.Types is { Count: > 0 } types)
            {
                var sub = new SubTypeCondition();
                sub.SetTypes(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(types.ToArray()));
                if (c.SubTypes is { Count: > 0 } subTypes) sub.SetSubTypes(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(subTypes.ToArray()));
                yield return sub;
            }
            foreach (var need in c.Affixes)
            {
                var affix = new AffixCondition();
                affix.SetAffixes(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(need.Ids.ToArray()));
                affix.minOnTheSameItem = Math.Max(1, need.Min);
                if (need.Raw is { } raw)
                {
                    affix.comparsion = (Comparsion)raw.Cmp;
                    affix.comparsionValue = raw.Value;
                    affix.combinedComparsion = (Comparsion)raw.CombinedCmp;
                    affix.combinedComparsionValue = raw.CombinedValue;
                    affix.advanced = raw.Advanced;
                    yield return affix;
                    continue;
                }
                // The tier comparisons count only with the advanced flag on; tiers as the game shows them.
                affix.comparsion = need.TierBar > 0 ? Comparsion.MORE_OR_EQUAL : Comparsion.ANY;
                affix.comparsionValue = Math.Max(1, need.TierBar);
                affix.combinedComparsion = need.Sum != null ? Comparsion.MORE_OR_EQUAL : Comparsion.ANY;
                affix.combinedComparsionValue = need.Sum ?? 1;
                affix.advanced = need.TierBar > 0 || need.Sum != null;
                yield return affix;
            }
            if (c.Uniques is { Count: > 0 } uniques)
            {
                var condition = new UniqueModifiersCondition();
                condition.SetUniqueIds(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort>(uniques.ToArray()));
                yield return condition;
            }
            if (c.Rarity != 0) yield return new RarityCondition { rarity = (RarityCondition.Rarity)c.Rarity };
            if (c.Potential != null || c.Forging != null)
            {
                var condition = new PotentialCondition();
                MinLp.Set(condition, c.Potential?.Lp);
                MinWw.Set(condition, c.Potential?.Ww);
                MinFp.Set(condition, c.Forging);
                yield return condition;
            }
            if (c.Corruption != 0) yield return new CorruptionCondition((CorruptionCondition.CorruptionType)c.Corruption);
            if (c.Levels is { } levels) yield return new CharacterLevelCondition { minimumLvl = levels.Min, maximumLvl = levels.Max };
            if (c.Classes != 0) yield return new ClassCondition { req = (ItemList.ClassRequirement)c.Classes };
            if (c.Resource is { } resource) yield return ResourceCondition(resource);
            if (c.Factions is { Count: > 0 } factions)
            {
                var condition = new FactionCondition();
                condition.SetEligibleFactions(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(factions.ToArray()));
                yield return condition;
            }
        }

        private static Condition ResourceCondition(ResourceKind kind)
        {
            switch (kind)
            {
                case ResourceKind.Runes: return new RuneCondition { RuneFilterFlags = RuneFilterFlags.AllRunes };
                case ResourceKind.Glyphs: return new GlyphCondition { GlyphFilterFlags = GlyphFilterFlags.AllGlyphs };
            }
            AbstractNonEquipmentCondition condition = kind switch
            {
                ResourceKind.Keys => new KeysCondition(),
                ResourceKind.WovenEchoes => new WovenEchoesCondition(),
                ResourceKind.Shards => new CraftingMaterialsCondition(),
                _ => new ResonancesCondition(),
            };
            condition.NonEquippableItemFilterFlags = kind switch
            {
                ResourceKind.Keys => NonEquippableItemFilterFlags.AllKeys,
                ResourceKind.WovenEchoes => NonEquippableItemFilterFlags.AllWovenEchoes,
                ResourceKind.Shards => NonEquippableItemFilterFlags.AllShards,
                _ => NonEquippableItemFilterFlags.AllResonances,
            };
            return condition;
        }

        // The kind a game resource condition shows, when it asks for all of that kind.
        private static ResourceKind? ResourceOf(Condition condition)
        {
            if (condition.TryCast<RuneCondition>() is { } rune) return rune.RuneFilterFlags == RuneFilterFlags.AllRunes ? ResourceKind.Runes : null;
            if (condition.TryCast<GlyphCondition>() is { } glyph) return glyph.GlyphFilterFlags == GlyphFilterFlags.AllGlyphs ? ResourceKind.Glyphs : null;
            if (condition.TryCast<AbstractNonEquipmentCondition>() is not { } other) return null;
            var (kind, all) = condition.TryCast<KeysCondition>() != null ? (ResourceKind.Keys, NonEquippableItemFilterFlags.AllKeys)
                : condition.TryCast<WovenEchoesCondition>() != null ? (ResourceKind.WovenEchoes, NonEquippableItemFilterFlags.AllWovenEchoes)
                : condition.TryCast<CraftingMaterialsCondition>() != null ? (ResourceKind.Shards, NonEquippableItemFilterFlags.AllShards)
                : condition.TryCast<ResonancesCondition>() != null ? (ResourceKind.Resonances, NonEquippableItemFilterFlags.AllResonances)
                : ((ResourceKind)(-1), (NonEquippableItemFilterFlags)0);
            return (int)kind >= 0 && other.NonEquippableItemFilterFlags == all ? kind : null;
        }

        // ---- reading a base ----

        // A foreign base's rules as plan rules, kept while its file is unchanged: read once per base, not per change.
        private static (string Name, string Hash, List<PlanRule> Rules)? s_baseRules;

        /// <summary>
        /// A filter's rules as plan rules, top first: its own names, outcomes and on or off, copied unchanged at Make.
        /// Null when the file isn't there or can't be read.
        /// </summary>
        public static List<PlanRule>? BaseRules(string name)
        {
            if (PathOf(name) is not { } path || HashFile(path) is not { } hash) return null;
            if (s_baseRules is { } kept && kept.Name == name && kept.Hash == hash) return Copies(kept.Rules);
            var filter = LoadByName(name);
            if (filter?.rules == null) return null;
            var rules = new List<PlanRule>(filter.rules.Count);
            for (int i = filter.rules.Count - 1, n = 1; i >= 0; i--, n++)
            {
                var rule = filter.rules[i];
                if (rule == null) continue;
                rules.Add(new PlanRule
                {
                    Key = "base." + n,
                    Origin = RuleOrigin.Base,
                    Name = rule.nameOverride is { Length: > 0 } ruleName ? ruleName : $"Rule {n} of {name}",
                    Hide = rule.type == Rule.RuleOutcome.HIDE,
                    Enabled = rule.isEnabled,
                    BaseIndex = i,
                    Look = new RuleLook
                    {
                        Colour = rule.recolor ? rule.color : null, Emphasized = rule.emphasized, Sound = rule.SoundId, MapIcon = rule.MapIconId,
                        Beam = rule.BeamOverride ? ((int)rule.BeamSizeOverride, rule.BeamColorOverride) : null,
                    },
                });
            }
            s_baseRules = (name, hash, rules);
            return Copies(rules);
        }

        private static List<PlanRule> Copies(List<PlanRule> rules) => rules.Select(r => new PlanRule
        {
            Key = r.Key, Origin = r.Origin, Name = r.Name, Hide = r.Hide, Enabled = r.Enabled, BaseIndex = r.BaseIndex, Look = r.Look.Copy(),
        }).ToList();

        /// <summary>Forgets the kept base rules (the feature stopping).</summary>
        public static void Forget()
        {
            s_baseRules = null;
            s_hashes.Clear();
        }

        // ---- edits made in the game's editor ----

        /// <summary>
        /// Takes a made filter's edits from the game's editor, as saved in its file, into the page's choices. Null when the
        /// file can't be read.
        /// </summary>
        public static List<string>? ImportEdits(FilterPlan plan, MadeRecord record, StageChoices choices)
        {
            var filter = LoadByName(Path.GetFileNameWithoutExtension(record.File));
            if (filter?.rules == null) return null;
            var baseFilter = plan.Base != EaBaseName && plan.Base != NoBase ? LoadByName(plan.Base) : null;
            return ImportFrom(filter, plan, record.Snapshot, choices, ConditionCheck.ToPage, baseFilter, out _, out _, out _);
        }

        /// <summary>The filter the game uses now when it's this record's file, still holding edits its editor hasn't saved.</summary>
        public static ItemFilter? LiveFilterFor(MadeRecord record)
        {
            var filter = Manager?.Filter;
            return filter?.rules != null && filter.name == Path.GetFileNameWithoutExtension(record.File) ? filter : null;
        }

        /// <summary>
        /// Takes edits from a game filter into the page's choices. With what the mod saved (<paramref name="made"/>), only
        /// what differs from that counts as an edit, so changes on the page not saved yet stay; without it every difference
        /// from the page counts. Each rule found by name gets its edited on or off and outcome, and a planner rule its look.
        /// Edits the choices can't hold (conditions changed, rules added, removed or moved) are returned as notes; a removed
        /// rule is turned off. <paramref name="signature"/> names the edits found, to take each in once.
        /// </summary>
        public static List<string> ImportFrom(ItemFilter filter, FilterPlan plan, PlanSnapshot? made, StageChoices choices, out string signature) =>
            ImportFrom(filter, plan, made, choices, ConditionCheck.None, null, out signature, out _, out _);

        /// <summary>How a rule's conditions are checked for an edit: not at all, against the game's copy as last seen (its
        /// conditions' fingerprints in <c>made</c>), or against what the page would write (with the base filter for rules
        /// copied from it).</summary>
        internal enum ConditionCheck { None, ToSeen, ToPage }

        /// <summary>As above; <paramref name="seen"/> is the game filter's rules as found (to compare the next look with), and
        /// <paramref name="edited"/> how many rules were taken in.</summary>
        public static List<string> ImportFrom(ItemFilter filter, FilterPlan plan, PlanSnapshot? made, StageChoices choices, ConditionCheck check,
            ItemFilter? baseFilter, out string signature, out PlanSnapshot seen, out int edited)
        {
            var notes = new List<string>();
            var edits = new StringBuilder();
            seen = new PlanSnapshot { Name = plan.Name, Base = plan.Base, Strictness = plan.Strictness };
            edited = 0;
            var fileRules = new List<Rule>();
            for (int i = filter.rules.Count - 1; i >= 0; i--)
                if (filter.rules[i] is { } r) fileRules.Add(r);
            var madeRules = new Dictionary<string, SnapRule>();
            if (made != null)
                foreach (var rule in made.Rules) madeRules.TryAdd(rule.Key, rule);

            // A foreign base's rules keep their own on or off unless the player switched them.
            var baseDefaults = new Dictionary<string, bool>();
            if (plan.Base != EaBaseName && plan.Base != NoBase && BaseRules(plan.Base) is { } baseRules)
                foreach (var rule in baseRules) baseDefaults[rule.Key] = rule.Enabled;

            var byName = new Dictionary<string, Queue<PlanRule>>();
            foreach (var rule in plan.Rules)
            {
                if (!byName.TryGetValue(rule.Name, out var queue)) byName[rule.Name] = queue = new Queue<PlanRule>();
                queue.Enqueue(rule);
            }
            var matched = new HashSet<PlanRule>();
            int lastIndex = -1;
            bool moved = false;
            foreach (var fileRule in fileRules)
            {
                string name = fileRule.nameOverride ?? string.Empty;
                if (!byName.TryGetValue(name, out var queue) || queue.Count == 0)
                {
                    notes.Add($"Added in the game: #{fileRules.IndexOf(fileRule) + 1} {(name.Length > 0 ? name : "a rule with no name")}");
                    continue;
                }
                var rule = queue.Dequeue();
                matched.Add(rule);
                int index = plan.Rules.IndexOf(rule);
                if (index < lastIndex) moved = true;
                lastIndex = index;

                bool on = fileRule.isEnabled, hide = fileRule.type == Rule.RuleOutcome.HIDE;
                var look = LookOf(fileRule);
                string condKey = ConditionsKey(fileRule);
                seen.Rules.Add(new SnapRule(rule.Key, rule.Name, on, hide, LookHash(look), condKey));
                // Against what was saved when it's known, else against the page.
                bool hasMade = madeRules.TryGetValue(rule.Key, out var saved);
                bool onEdited = hasMade ? on != saved!.On : on != rule.Enabled;
                bool hideEdited = hasMade ? hide != saved!.Hide : hide != rule.Hide;
                bool lookEdited = rule.Origin == RuleOrigin.Planner && (hasMade ? LookHash(look) != saved!.Look : !SameLook(look, rule.Look));
                bool condEdited = check switch
                {
                    ConditionCheck.ToSeen => hasMade && condKey != saved!.Cond,
                    ConditionCheck.ToPage => condKey != ConditionsKey(rule.BaseIndex >= 0 && baseFilter?.rules is { } baseList && rule.BaseIndex < baseList.Count
                        ? baseList[rule.BaseIndex] : MakeRule(rule)),
                    _ => false,
                };
                if (!onEdited && !hideEdited && !lookEdited && !condEdited) continue;
                var change = choices.Rules.TryGetValue(rule.Key, out var c) ? c : new RuleChoice();
                if (condEdited)
                {
                    // Kept as edited; a kind the page can't hold stays a note, and an Update would undo it.
                    if (ConditionsFrom(fileRule) is { } kept) change.Conditions = kept;
                    else notes.Add($"Conditions changed in the game that this page can't keep: #{fileRules.IndexOf(fileRule) + 1} {rule.Name}");
                }
                if (onEdited)
                {
                    bool onDefault = rule.Origin == RuleOrigin.Base
                        ? baseDefaults.TryGetValue(rule.Key, out bool own) ? own : rule.Enabled
                        : StepDefault(rule, plan, choices);
                    change.Enabled = on == onDefault ? null : on;
                }
                if (hideEdited) change.Hide = hide;
                if (lookEdited) change.Look = look;
                edits.Append(rule.Key).Append(on).Append(hide).Append(LookHash(look)).Append(condKey).Append(';');
                edited++;
                if (change.Enabled == null && change.Hide == null && change.Look == null && change.Conditions == null) choices.Rules.Remove(rule.Key);
                else choices.Rules[rule.Key] = change;
            }
            foreach (var rule in plan.Rules)
            {
                // A rule the page adds that wasn't saved yet isn't missing from the game's copy.
                if (matched.Contains(rule) || made != null && !madeRules.ContainsKey(rule.Key)) continue;
                notes.Add($"Removed in the game, shown as off: #{plan.Rules.IndexOf(rule) + 1} {rule.Name}");
                var change = choices.Rules.TryGetValue(rule.Key, out var c) ? c : choices.Rules[rule.Key] = new RuleChoice();
                if (change.Enabled != false) edited++;
                change.Enabled = false;
                edits.Append('-').Append(rule.Key).Append(';');
            }
            if (moved) notes.Add("Rules were moved in the game; the page keeps its own order");
            signature = edits.Append('|').Append(string.Join("|", notes)).ToString();
            return notes;
        }

        /// <summary>
        /// Brings the filter in use up to date with a saved copy of it without loading it again, when only rules' on or off,
        /// outcome or look changed (same rules, names and conditions, same order): those fields are copied rule by rule and
        /// the game's open window redraws just those rules, so it neither rebuilds nor moves. False when the rules differ
        /// otherwise; the filter is then loaded again.
        /// </summary>
        public static bool PatchActive(ItemFilter saved)
        {
            if (s_run?.Feature is not { } feature || !feature.Has(LootFilterPanelGroup)) return false;
            var live = Manager?.Filter;
            if (live?.rules == null || saved.rules == null || live.rules.Count != saved.rules.Count) return false;
            for (int i = 0; i < live.rules.Count; i++)
            {
                var a = live.rules[i];
                var b = saved.rules[i];
                if (a == null || b == null || a.nameOverride != b.nameOverride || ConditionsText(a) != ConditionsText(b)) return false;
            }
            var changed = new List<Rule>();
            for (int i = 0; i < live.rules.Count; i++)
            {
                var a = live.rules[i];
                var b = saved.rules[i];
                if (a.isEnabled == b.isEnabled && a.type == b.type && a.recolor == b.recolor && a.color == b.color && a.emphasized == b.emphasized
                    && a.SoundId == b.SoundId && a.MapIconId == b.MapIconId && a.BeamOverride == b.BeamOverride && a.BeamSizeOverride == b.BeamSizeOverride
                    && a.BeamColorOverride == b.BeamColorOverride) continue;
                a.isEnabled = b.isEnabled;
                a.type = b.type;
                a.recolor = b.recolor;
                a.color = b.color;
                a.emphasized = b.emphasized;
                a.SoundId = b.SoundId;
                a.MapIconId = b.MapIconId;
                a.BeamOverride = b.BeamOverride;
                a.BeamSizeOverride = b.BeamSizeOverride;
                a.BeamColorOverride = b.BeamColorOverride;
                changed.Add(a);
            }
            if (changed.Count == 0) return true;
            try
            {
                // Each changed rule's row: its tick and its text (outcome, colour); the window's own update leaves the tick.
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LootFilterUI>()))
                {
                    if (obj.TryCast<LootFilterUI>() is not { } ui || !Game.Alive(ui) || !ui.gameObject.activeInHierarchy || ui.entries == null) continue;
                    for (int i = 0; i < ui.entries.Count; i++)
                    {
                        var entry = ui.entries[i];
                        if (!Game.Alive(entry) || entry.rule == null || !changed.Exists(r => r.Pointer == entry.rule.Pointer)) continue;
                        // The row's own set-up writes its text from the rule (outcome, colour, the grey of a rule turned off).
                        entry.Init(entry.rule, ui, entry.rule.Order, false);
                        entry.SetRuleTogleActive(entry.rule.isEnabled);
                    }
                }
                // Items already on the ground are judged again, as after a change in the game's own editor.
                ItemFilterManager.InvokeLootFilterChanged(ItemFilterManager.ChangeType.Modified);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:panel", $"Planner: the game's loot filter window couldn't show the saved rules: {e.Message}", 30);
            }
            return true;
        }

        /// <summary>
        /// A game rule's conditions as plan data, for a rule edited in the game's editor; null when it holds a kind the plan
        /// can't (affix count, unique rolls, a potential beyond minimum LP, WW and FP, two conditions of one kind).
        /// </summary>
        public static RuleConditions? ConditionsFrom(Rule rule)
        {
            var c = new RuleConditions();
            if (rule.conditions == null) return c;
            for (int i = 0; i < rule.conditions.Count; i++)
            {
                var condition = rule.conditions[i];
                if (condition == null) continue;
                if (condition.TryCast<SubTypeCondition>() is { } sub)
                {
                    if (c.Types != null) return null;
                    c.Types = sub.GetTypes()?.ToList() ?? new List<int>();
                    c.SubTypes = sub.GetSubTypes() is { Length: > 0 } subs ? subs.ToList() : null;
                }
                else if (condition.TryCast<AffixCondition>() is { } affix)
                {
                    var ids = new List<int>();
                    if (affix.affixes != null)
                        foreach (int id in affix.affixes) ids.Add(id);
                    ids.Sort();
                    c.Affixes.Add(new AffixNeed
                    {
                        Ids = ids, Min = affix.minOnTheSameItem,
                        Raw = ((int)affix.comparsion, affix.comparsionValue, (int)affix.combinedComparsion, affix.combinedComparsionValue, affix.advanced),
                    });
                }
                else if (condition.TryCast<UniqueModifiersCondition>() is { } uniques)
                {
                    if (c.Uniques != null || uniques.UniquesSerialized is { Count: > 0 } rolled && HasRolls(uniques)) return null;
                    c.Uniques = uniques.GetUniqueIds()?.ToList() ?? new List<ushort>();
                }
                else if (condition.TryCast<RarityCondition>() is { } rarity)
                {
                    if (c.Rarity != 0) return null;
                    c.Rarity = (int)rarity.rarity;
                }
                else if (condition.TryCast<PotentialCondition>() is { } potential)
                {
                    if (c.Potential != null || c.Forging != null || MaxLp.Get(potential) != null || MaxWw.Get(potential) != null || MinWt.Get(potential) != null
                        || MaxWt.Get(potential) != null || MaxFp.Get(potential) != null) return null;
                    if (MinLp.Get(potential) != null || MinWw.Get(potential) != null) c.Potential = (MinLp.Get(potential), MinWw.Get(potential));
                    c.Forging = MinFp.Get(potential);
                }
                else if (condition.TryCast<CorruptionCondition>() is { } corruption)
                {
                    if (c.Corruption != 0) return null;
                    c.Corruption = (int)corruption.Corruption;
                }
                else if (condition.TryCast<CharacterLevelCondition>() is { } level)
                {
                    if (c.Levels != null) return null;
                    c.Levels = (level.minimumLvl, level.maximumLvl);
                }
                else if (condition.TryCast<ClassCondition>() is { } cls)
                {
                    if (c.Classes != 0) return null;
                    c.Classes = (int)cls.req;
                }
                else if (ResourceOf(condition) is { } resource)
                {
                    if (c.Resource != null) return null;
                    c.Resource = resource;
                }
                else if (condition.TryCast<FactionCondition>() is { } faction)
                {
                    if (c.Factions != null) return null;
                    c.Factions = faction.GetEligibleFactions()?.ToList() ?? new List<int>();
                }
                else
                {
                    return null;
                }
            }
            return c;
        }

        // A unique condition with roll ranges set for its uniques.
        private static bool HasRolls(UniqueModifiersCondition condition)
        {
            var list = condition.UniquesSerialized;
            for (int i = 0; i < list.Count; i++)
                if (list[i]?.Rolls is { Count: > 0 }) return true;
            return false;
        }

        /// <summary>A game rule's look as plan data.</summary>
        public static RuleLook LookOf(Rule rule) => new()
        {
            Colour = rule.recolor ? rule.color : null, Emphasized = rule.emphasized, Sound = rule.SoundId, MapIcon = rule.MapIconId,
            Beam = rule.BeamOverride ? ((int)rule.BeamSizeOverride, rule.BeamColorOverride) : null,
        };

        private static bool SameLook(RuleLook a, RuleLook b) =>
            a.Colour == b.Colour && a.Emphasized == b.Emphasized && a.Sound == b.Sound && a.MapIcon == b.MapIcon && a.Beam == b.Beam;

        /// <summary>A rule's conditions with their affix ids and types in full (not only counts), as a short fingerprint; any
        /// edit to them in the game's editor changes it.</summary>
        public static string ConditionsKey(Rule rule)
        {
            var hash = new HashCode();
            hash.Add(ConditionsText(rule));
            if (rule.conditions != null)
                for (int i = 0; i < rule.conditions.Count; i++)
                {
                    var condition = rule.conditions[i];
                    if (condition?.TryCast<AffixCondition>() is { affixes: { } ids })
                    {
                        // Order-free: the sum and xor of the ids.
                        long sum = 0;
                        int xor = 0;
                        foreach (int id in ids)
                        {
                            sum += id;
                            xor ^= id * 2654435761u is var m ? (int)m : 0;
                        }
                        hash.Add(sum);
                        hash.Add(xor);
                    }
                    else if (condition?.TryCast<SubTypeCondition>() is { } sub)
                    {
                        foreach (int t in sub.GetTypes() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0)) hash.Add(t);
                        foreach (int t in sub.GetSubTypes() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0)) hash.Add(t * 31);
                    }
                    else if (condition?.TryCast<UniqueModifiersCondition>() is { } uniques && uniques.GetUniqueIds() is { } list)
                    {
                        foreach (ushort id in list) hash.Add(id);
                    }
                }
            return hash.ToHashCode().ToString("x8");
        }

        private static string ConditionsText(Rule rule)
        {
            var parts = new List<string>();
            if (rule.conditions != null)
                for (int i = 0; i < rule.conditions.Count; i++)
                    if (rule.conditions[i] is { } condition) parts.Add(DescribeCondition(condition));
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" & ", parts);
        }

        // ---- making ----

        /// <summary>What Make did: the saved name, or why nothing was saved; Ask when an edited file waits for a yes.</summary>
        internal readonly record struct MakeResult(string? Saved, string Message, bool Ask = false);

        /// <summary>
        /// Whether the filter the game uses now is exactly what Make would write for this plan (every rule's name, on or
        /// off, outcome, look and conditions, in order). Builds the filter without writing it; the plan isn't changed.
        /// </summary>
        public static bool SameAsInUse(FilterPlan plan, string file)
        {
            var live = Manager?.Filter;
            if (live?.rules == null || live.name != file) return false;
            try
            {
                ItemFilter? baseFilter;
                var copy = new FilterPlan { Name = plan.Name, Description = plan.Description, Base = plan.Base };
                foreach (var rule in plan.Rules)
                    copy.Rules.Add(new PlanRule
                    {
                        Key = rule.Key, Origin = rule.Origin, Name = rule.Name, Hide = rule.Hide, Enabled = rule.Enabled, Steps = rule.Steps,
                        Look = rule.Look.Copy(), Conditions = rule.Conditions, GameConditions = rule.GameConditions, BaseIndex = rule.BaseIndex,
                    });
                if (copy.Base == NoBase)
                {
                    baseFilter = null;
                }
                else if (copy.Base == EaBaseName)
                {
                    if (LootFilterStore.RecordFor(string.Empty, "base") is not { } baseRecord) return false;
                    baseFilter = LoadByName(Path.GetFileNameWithoutExtension(baseRecord.File));
                    if (baseFilter != null) UseEaBaseFile(copy, baseFilter);
                }
                else
                {
                    baseFilter = LoadByName(copy.Base);
                }
                var made = Build(copy, baseFilter);
                if (made.rules.Count != live.rules.Count) return false;
                for (int i = 0; i < made.rules.Count; i++)
                {
                    var a = made.rules[i];
                    var b = live.rules[i];
                    if (a == null || b == null || a.nameOverride != b.nameOverride || a.isEnabled != b.isEnabled || a.type != b.type
                        || !SameLook(LookOf(a), LookOf(b)) || ConditionsKey(a) != ConditionsKey(b)) return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:same", $"Planner: the filter in use couldn't be compared with the page: {e.Message}", 30);
                return false;
            }
        }

        /// <summary>
        /// Makes and saves the plan's filter: EA · Base first when it has no file of the mod's, then the planner rules on the
        /// base's rules and the hide rule. Replaces only the mod's own untouched file for this build and stage; an edited one
        /// only with <paramref name="replaceEdited"/>; a file the mod has no record of is never written (the new one is
        /// numbered). Never changes the active filter.
        /// </summary>
        public static MakeResult Make(FilterPlan plan, bool replaceEdited = false)
        {
            if (Manager == null || FilterFolder == null) return new(null, "Not saved: the game's loot filters aren't loaded yet");
            // Taken before EA · Base's file reshapes the rules, so it matches the page's own plan.
            var snapshot = SnapshotOf(plan);
            if (plan.Rules.Count > Limit) return new(null, $"Not saved: {plan.Rules.Count} rules is over the game's {Limit}");
            ItemFilter? baseFilter = null;
            if (plan.Base == NoBase)
            {
                // Only the planner's rules: nothing to copy.
            }
            else if (plan.Base == EaBaseName)
            {
                if (EnsureEaBase() is not { } baseFile) return new(null, "Not saved: EA · Base couldn't be made");
                baseFilter = LoadByName(baseFile);
                if (baseFilter != null) UseEaBaseFile(plan, baseFilter);
            }
            else
            {
                baseFilter = LoadByName(plan.Base);
                if (baseFilter == null) return new(null, $"Not saved: {plan.Base} couldn't be read");
            }
            if (plan.Rules.Count > Limit) return new(null, $"Not saved: {plan.Rules.Count} rules is over the game's {Limit}");

            var record = LootFilterStore.RecordFor(plan.BuildId, plan.StageKey);
            // A gone file's record no longer holds its name.
            if (record != null && LootFilterStore.StateOf(record) == MadeState.Gone)
            {
                LootFilterStore.Forget(record);
                record = null;
            }
            string? file = null;
            if (record != null && IsFileFor(record.File, plan.Name))
            {
                switch (LootFilterStore.StateOf(record))
                {
                    case MadeState.Unchanged:
                        file = record.File;
                        break;
                    case MadeState.Edited when replaceEdited:
                        file = record.File;
                        break;
                    case MadeState.Edited:
                        return new(null, $"Replace {Path.GetFileNameWithoutExtension(record.File)}? It was edited in the game's editor.", Ask: true);
                }
            }
            file ??= FreeFile(plan.Name);
            string name = Path.GetFileNameWithoutExtension(file);
            plan.Name = name;
            var filter = Build(plan, baseFilter);
            if (PathOf(name) is not { } path) return new(null, "Not saved: the game's Filters folder isn't known");
            if (SerializeTo(filter, path) is not { } hash) return new(null, "Not saved: the game's serializer refused it (see the log)");
            LootFilterStore.Record(file, plan.BuildId, plan.StageKey, hash, snapshot, plan.Rules.Count);
            // The game keeps the filter it uses in memory; when that's this one it's loaded again, so the change applies now
            // and the copy in memory isn't taken for edits made in the game's editor.
            if (ActiveName == name && !PatchActive(filter)) Use(name);
            return new(name, $"Saved as {name}");
        }

        /// <summary>
        /// The EA · Base file the mod owns, made from the table when it has no record or its file is gone. Never rewritten
        /// once its bytes differ from the record (the player edited it). Null when it couldn't be made.
        /// </summary>
        public static string? EnsureEaBase(bool restore = false)
        {
            var record = LootFilterStore.RecordFor(string.Empty, "base");
            var state = record != null ? LootFilterStore.StateOf(record) : MadeState.Gone;
            string file = record != null && state != MadeState.Gone ? record.File : FreeFile(EaBaseName);
            var plan = LootFilterBase.Plan();
            plan.Name = Path.GetFileNameWithoutExtension(file);
            // An edited one is the player's now; an untouched one is made again only when the table has changed.
            if (record != null && !restore && (state == MadeState.Edited || state == MadeState.Unchanged && HashOf(Build(plan)) == record.Hash))
                return plan.Name;
            if (PathOf(plan.Name) is not { } path || SerializeTo(Build(plan), path) is not { } hash) return null;
            LootFilterStore.Record(file, string.Empty, "base", hash);
            if (ActiveName == plan.Name) Use(plan.Name);
            return plan.Name;
        }

        // EA · Base's rules as its file has them, so the player's edits carry: each table rule found by name is copied from
        // the file (its look and conditions as edited) and keeps its steps; a rule the player deleted is left out; rules
        // the player added follow with their own on or off.
        private static void UseEaBaseFile(FilterPlan plan, ItemFilter baseFilter)
        {
            int first = plan.Rules.FindIndex(r => r.Origin == RuleOrigin.EaBase);
            if (first < 0 || baseFilter.rules == null) return;
            var byName = new Dictionary<string, int>();
            for (int i = baseFilter.rules.Count - 1; i >= 0; i--)
                if (baseFilter.rules[i]?.nameOverride is { Length: > 0 } n) byName.TryAdd(n, i);
            var used = new HashSet<int>();
            foreach (var rule in plan.Rules.Where(r => r.Origin == RuleOrigin.EaBase).ToList())
            {
                if (byName.TryGetValue(rule.Name, out int at))
                {
                    // A rule with conditions kept from the game's editor is built from them, not copied from EA · Base.
                    if (!rule.GameConditions) rule.BaseIndex = at;
                    used.Add(at);
                }
                else
                {
                    plan.Rules.Remove(rule);
                }
            }
            int insertAt = plan.Rules.FindLastIndex(r => r.Origin == RuleOrigin.EaBase) + 1;
            if (insertAt <= 0) insertAt = first;
            for (int i = baseFilter.rules.Count - 1; i >= 0; i--)
            {
                var rule = baseFilter.rules[i];
                if (rule == null || used.Contains(i)) continue;
                plan.Rules.Insert(insertAt++, new PlanRule
                {
                    Key = "base.added." + i, Origin = RuleOrigin.Base, Name = rule.nameOverride ?? string.Empty,
                    Hide = rule.type == Rule.RuleOutcome.HIDE, Enabled = rule.isEnabled, BaseIndex = i,
                });
            }
        }

        /// <summary>A made file is the plan's when it has the plan's name, or that name numbered (" (2)") because a file
        /// the mod doesn't own had it.</summary>
        public static bool IsFileFor(string file, string name)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            return stem == name || stem.StartsWith(name + " (", StringComparison.Ordinal) && stem.EndsWith(')')
                && int.TryParse(stem[(name.Length + 2)..^1], out _);
        }

        // A file name in the Filters folder the mod may write: the name itself when no file has it, else numbered.
        private static string FreeFile(string name)
        {
            for (int n = 1; n < 100; n++)
            {
                string candidate = n == 1 ? name : $"{name} ({n})";
                string file = candidate + ".xml";
                if (PathOf(candidate) is { } path && !File.Exists(path) && LootFilterStore.RecordOfFile(file) == null) return file;
            }
            return $"{name} ({DateTime.UtcNow:yyyyMMddHHmmss}).xml";
        }

        /// <summary>
        /// A filter file's fingerprint: the SHA-256 of its text with the byte-order mark, line endings and trailing
        /// whitespace left out. The game saves a file again in its own form (no mark, a final newline) once it sees it in
        /// the folder; only an edit to the filter itself changes this.
        /// </summary>
        public static string Hash(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").TrimEnd();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        // Each file's fingerprint with the size and write time it was taken at, so an unchanged file isn't read again.
        private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> s_hashes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A file's fingerprint (<see cref="Hash"/>), read again only when its size or write time changed; null
        /// when it isn't there or can't be read.</summary>
        public static string? HashFileCached(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    s_hashes.Remove(path);
                    return null;
                }
                if (s_hashes.TryGetValue(path, out var kept) && kept.Length == info.Length && kept.Written == info.LastWriteTimeUtc) return kept.Hash;
                string? hash = HashFile(path);
                if (hash != null) s_hashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
                return hash;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// A quick signature of a game filter: each rule's name, on or off, outcome and look, in order; no conditions and no
        /// game rule made. Equal signatures mean nothing the page takes in has changed.
        /// </summary>
        public static int QuickSignature(ItemFilter filter)
        {
            var hash = new HashCode();
            var rules = filter.rules;
            int count = rules?.Count ?? 0;
            hash.Add(count);
            for (int i = 0; i < count; i++)
            {
                var rule = rules![i];
                if (rule == null) continue;
                hash.Add(rule.nameOverride);
                hash.Add(rule.isEnabled);
                hash.Add((int)rule.type);
                hash.Add(rule.recolor ? rule.color : -1);
                hash.Add(rule.emphasized);
                hash.Add(rule.SoundId);
                hash.Add(rule.MapIconId);
                hash.Add(rule.BeamOverride ? (int)rule.BeamSizeOverride * 100 + rule.BeamColorOverride : -1);
                var conditions = rule.conditions;
                int conditionCount = conditions?.Count ?? 0;
                hash.Add(conditionCount);
                // Each condition's kind and numbers, read as numbers (no text made): an affix added or a tier bar moved in
                // the game's editor shows.
                for (int c = 0; c < conditionCount; c++)
                {
                    var condition = conditions![c];
                    if (condition == null) continue;
                    hash.Add(Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(condition.Pointer));
                    if (condition.TryCast<AffixCondition>() is { } affix)
                    {
                        hash.Add(affix.affixes?.Count ?? 0);
                        hash.Add((int)affix.comparsion * 100 + affix.comparsionValue);
                        hash.Add((int)affix.combinedComparsion * 100 + affix.combinedComparsionValue);
                        hash.Add(affix.minOnTheSameItem * 2 + (affix.advanced ? 1 : 0));
                    }
                    else if (condition.TryCast<SubTypeCondition>() is { } sub)
                    {
                        hash.Add(sub.type?.Count ?? 0);
                        hash.Add(sub.subTypes?.Count ?? 0);
                    }
                    else if (condition.TryCast<CharacterLevelCondition>() is { } level)
                    {
                        hash.Add(level.minimumLvl * 1000 + level.maximumLvl);
                    }
                    else if (condition.TryCast<RarityCondition>() is { } rarity)
                    {
                        hash.Add((int)rarity.rarity);
                    }
                }
            }
            return hash.ToHashCode();
        }

        /// <summary>A file's fingerprint (<see cref="Hash"/>), or null when it can't be read.</summary>
        public static string? HashFile(string path)
        {
            try
            {
                return File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>Each rule, top first: order, outcome, look, name and a one-line summary of its conditions.</summary>
        public static List<string> Describe(ItemFilter filter)
        {
            var lines = new List<string>();
            var rules = filter.rules;
            if (rules == null) return lines;
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule == null) continue;
                lines.Add(DescribeRule(rule, i));
            }
            return lines;
        }

        public static string DescribeRule(Rule rule, int index)
        {
            string outcome = rule.type == Rule.RuleOutcome.HIDE ? "HIDE" : "SHOW";
            string colour = rule.recolor ? rule.color.ToString().PadLeft(2) : " -";
            string beam = rule.BeamOverride ? $"{rule.BeamSizeOverride}/{rule.BeamColorOverride}" : $"-/{rule.BeamColorOverride}";
            var parts = new List<string>();
            var conditions = rule.conditions;
            if (conditions != null)
                for (int c = 0; c < conditions.Count; c++)
                    if (conditions[c] is { } condition) parts.Add(DescribeCondition(condition));
            return $"{index,3} order {rule.Order,3} {outcome} {(rule.emphasized ? "E" : "-")}{(rule.isEnabled ? "on " : "OFF")} col {colour} snd{rule.SoundId} map{rule.MapIconId} beam{beam} | {rule.nameOverride} | {string.Join(" & ", parts)}";
        }

        internal static string DescribeCondition(Condition condition)
        {
            if (condition.TryCast<SubTypeCondition>() is { } sub)
            {
                var types = sub.GetTypes();
                var subs = sub.GetSubTypes();
                string typeText = types == null ? string.Empty : string.Join(",", types.Select(t => ((EquipmentType)t).ToString()));
                string subText = subs == null || subs.Length == 0 ? "*" : string.Join(",", subs);
                return $"Sub[{typeText}:{subText}]";
            }
            if (condition.TryCast<AffixCondition>() is { } affix)
                return $"Affix[{affix.affixes?.Count ?? 0} ids; cmp {affix.comparsion} {affix.comparsionValue}; min {affix.minOnTheSameItem}; sum {affix.combinedComparsion} {affix.combinedComparsionValue}; adv {affix.advanced.ToString().ToLowerInvariant()}]";
            if (condition.TryCast<CharacterLevelCondition>() is { } level) return $"Lvl{level.minimumLvl}-{level.maximumLvl}";
            if (condition.TryCast<CorruptionCondition>() is { } corruption) return $"Corr[{corruption.Corruption}]";
            if (condition.TryCast<RarityCondition>() is { } rarity) return $"Rar[{rarity.rarity}]";
            if (condition.TryCast<ClassCondition>() is { } cls) return $"Class[{cls.req}]";
            if (condition.TryCast<RuneCondition>() is { } runes) return $"Runes[{runes.RuneFilterFlags}]";
            if (condition.TryCast<GlyphCondition>() is { } glyphs) return $"Glyphs[{glyphs.GlyphFilterFlags}]";
            if (condition.TryCast<AbstractNonEquipmentCondition>() is { } items) return $"{condition.GetIl2CppType().Name.Replace("Condition", string.Empty)}[{items.NonEquippableItemFilterFlags}]";
            if (condition.TryCast<FactionCondition>() is { } faction) return $"Faction[{string.Join(",", faction.GetEligibleFactions() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0))}]";
            if (condition.TryCast<UniqueModifiersCondition>() is { } uniques)
            {
                var ids = uniques.GetUniqueIds();
                return $"Uniques[{ids?.Length ?? 0} ids{(ids is { Length: > 0 and <= 6 } ? ": " + string.Join(",", ids) : string.Empty)}]";
            }
            if (condition.TryCast<PotentialCondition>() is { } potential)
            {
                var sb = new StringBuilder("Pot[");
                Range(sb, "LP", MinLp.Get(potential), MaxLp.Get(potential));
                Range(sb, "WW", MinWw.Get(potential), MaxWw.Get(potential));
                Range(sb, "WT", MinWt.Get(potential), MaxWt.Get(potential));
                Range(sb, "FP", MinFp.Get(potential), MaxFp.Get(potential));
                return sb.ToString().TrimEnd() + "]";
            }
            return condition.GetIl2CppType().Name;
        }

        private static void Range(StringBuilder sb, string name, int? lo, int? hi)
        {
            if (lo == null && hi == null) return;
            sb.Append(name).Append(lo?.ToString() ?? string.Empty).Append('-').Append(hi?.ToString() ?? string.Empty).Append(' ');
        }

        // The potential condition's fields are nullable ints, which the interop's getters and setters don't carry right;
        // they're read and written in the object's own memory at the game's field offsets.
        public static readonly NullableIntField MinLp = new(typeof(PotentialCondition), "MinLegendaryPotential");
        public static readonly NullableIntField MaxLp = new(typeof(PotentialCondition), "MaxLegendaryPotential");
        public static readonly NullableIntField MinWw = new(typeof(PotentialCondition), "MinWeaversWill");
        public static readonly NullableIntField MaxWw = new(typeof(PotentialCondition), "MaxWeaversWill");
        public static readonly NullableIntField MinWt = new(typeof(PotentialCondition), "MinWeaversTouch");
        public static readonly NullableIntField MaxWt = new(typeof(PotentialCondition), "MaxWeaversTouch");
        public static readonly NullableIntField MinFp = new(typeof(PotentialCondition), "MinForgingPotential");
        public static readonly NullableIntField MaxFp = new(typeof(PotentialCondition), "MaxForgingPotential");
    }

    /// <summary>A game object's Nullable&lt;int&gt; field, read and written at its offsets; offsets found on first use.</summary>
    internal sealed class NullableIntField
    {
        private readonly Type _owner;
        private readonly string _name;
        private int _has = -2, _value;

        public NullableIntField(Type owner, string name)
        {
            _owner = owner;
            _name = name;
        }

        /// <summary>The field was found in the game's class.</summary>
        public bool Found => Find();

        public int? Get(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj)
        {
            if (!Find()) return null;
            return System.Runtime.InteropServices.Marshal.ReadByte(obj.Pointer, _has) != 0
                ? System.Runtime.InteropServices.Marshal.ReadInt32(obj.Pointer, _value)
                : null;
        }

        public void Set(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase obj, int? value)
        {
            if (!Find()) throw new MissingFieldException(_owner.Name, _name);
            System.Runtime.InteropServices.Marshal.WriteInt32(obj.Pointer, _value, value ?? 0);
            System.Runtime.InteropServices.Marshal.WriteByte(obj.Pointer, _has, value.HasValue ? (byte)1 : (byte)0);
        }

        private bool Find()
        {
            if (_has != -2) return _has >= 0;
            _has = -1;
            try
            {
                var store = typeof(Il2CppInterop.Runtime.Il2CppClassPointerStore<>).MakeGenericType(_owner);
                IntPtr owner = (IntPtr)store.GetField("NativeClassPtr")!.GetValue(null)!;
                IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(owner, _name);
                if (field == IntPtr.Zero) field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(owner, $"<{_name}>k__BackingField");
                if (field == IntPtr.Zero) return false;
                IntPtr nullable = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_type(field));
                IntPtr hasValue = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(nullable, "hasValue");
                IntPtr value = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(nullable, "value");
                if (hasValue == IntPtr.Zero || value == IntPtr.Zero) return false;
                // Field offsets in a value type count its object header, which an embedded struct doesn't have.
                int header = 2 * IntPtr.Size;
                int at = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                _value = at + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(value) - header;
                _has = at + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(hasValue) - header;
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:filter:field", $"Planner: {_owner.Name}.{_name} couldn't be found: {e.Message}", 30);
                _has = -1;
                return false;
            }
        }
    }
}
