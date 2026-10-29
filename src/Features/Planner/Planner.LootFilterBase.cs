using Il2Cpp;

namespace EpochAssist.Features;

internal static partial class Planner
{
    /// <summary>
    /// EA · Base: the mod's general base filter, a normal game filter made from this table when first needed and on
    /// Restore. Built from game data only. It has no hide rule of its own, so on its own it hides nothing.
    /// </summary>
    internal static class LootFilterBase
    {
        /// <summary>One rule of the table: its id (the plan key is base.&lt;id&gt;), name, steps, look and conditions.</summary>
        internal sealed record BaseRule(string Id, string Name, Steps Steps, Func<RuleLook> Look, Action<RuleConditions> Fill);

        private static AffixGroups? s_groups;

        /// <summary>Affix ids by group, read once from the game's affix list.</summary>
        internal sealed class AffixGroups
        {
            public readonly List<int> Equipment = new(), Weaver = new(), Experimental = new(), Personal = new(), Champion = new();
            /// <summary>Equipment affixes giving health or physical resistance, any elemental or other resistance,
            /// movement speed, or cooldown recovery (an affix with several properties counts when one of them is).</summary>
            public readonly List<int> Health = new(), Resistances = new(), Movespeed = new(), Cooldown = new();
        }

        /// <summary>The game's affix groups: every equipment affix, weaver idol affixes, experimental, personal and
        /// champion affixes. Empty lists when the game's affix list isn't loaded yet (not kept then).</summary>
        public static AffixGroups Groups
        {
            get
            {
                if (s_groups != null) return s_groups;
                var groups = new AffixGroups();
                var all = AffixList.get()?.GetAllAffixes();
                if (all == null) return groups;
                foreach (var affix in all)
                {
                    if (affix == null) continue;
                    if (affix.specialAffixType == AffixList.SpecialAffixType.Standard && affix.rollsOn == AffixList.RollsOn.Equipment)
                    {
                        foreach (var property in PropertiesOf(affix))
                        {
                            if (property is SP.Health or SP.PhysicalResistance) AddOnce(groups.Health, affix.affixId);
                            else if (s_resistances.Contains(property)) AddOnce(groups.Resistances, affix.affixId);
                            else if (property == SP.Movespeed) AddOnce(groups.Movespeed, affix.affixId);
                            else if (property == SP.IncreasedCooldownRecoverySpeed) AddOnce(groups.Cooldown, affix.affixId);
                        }
                    }
                    switch (affix.specialAffixType)
                    {
                        case AffixList.SpecialAffixType.Standard:
                            groups.Equipment.Add(affix.affixId);
                            break;
                        case AffixList.SpecialAffixType.Experimental:
                            groups.Equipment.Add(affix.affixId);
                            groups.Experimental.Add(affix.affixId);
                            break;
                        case AffixList.SpecialAffixType.Personal:
                            groups.Equipment.Add(affix.affixId);
                            // The game marks champion affixes only by name.
                            if (affix.affixName?.Contains("Champion", StringComparison.Ordinal) == true) groups.Champion.Add(affix.affixId);
                            else groups.Personal.Add(affix.affixId);
                            break;
                        case AffixList.SpecialAffixType.IdolWeaver:
                            groups.Weaver.Add(affix.affixId);
                            break;
                    }
                }
                foreach (var list in new[] { groups.Equipment, groups.Weaver, groups.Experimental, groups.Personal, groups.Champion, groups.Health, groups.Resistances, groups.Movespeed, groups.Cooldown })
                    list.Sort();
                return s_groups = groups;
            }
        }

        private static readonly HashSet<SP> s_resistances = new()
        {
            SP.FireResistance, SP.ColdResistance, SP.LightningResistance, SP.VoidResistance, SP.NecroticResistance, SP.PoisonResistance,
            SP.AllResistances, SP.ElementalResistance,
        };

        // An affix's main property: a single affix's own, or a multi affix's first ("Level of Reaper Form" also lists health
        // further on). Minion, channelling and other tagged versions are left out: they don't help the character itself.
        private static IEnumerable<SP> PropertiesOf(AffixList.Affix affix)
        {
            if (affix.TryCast<AffixList.SingleAffix>() is { } single)
            {
                if (single.tags == 0) yield return single.property;
            }
            else if (affix.TryCast<AffixList.MultiAffix>()?.affixProperties is { Count: > 0 } properties && properties[0] is { } first && first.tags == 0)
            {
                yield return first.property;
            }
        }

        private static void AddOnce(List<int> list, int id)
        {
            if (!list.Contains(id)) list.Add(id);
        }

        private static UniqueGroups? s_uniques;

        /// <summary>The game's uniques by what makes one worth a look, read once from its unique list.</summary>
        internal sealed class UniqueGroups
        {
            // Uniques that can drop (a reroll chance of 1 never drops as itself), with the game's numbers for each.
            private readonly List<(ushort Id, int LpLevel, float Reroll, bool Random, bool Primordial, bool Cocooned)> _all = new();

            public UniqueGroups(IEnumerable<UniqueList.Entry> entries)
            {
                foreach (var e in entries)
                {
                    if (e == null || e.hideFromPlayers || e.isSetItem || e.rerollChance >= 1f) continue;
                    // The LP level depends on the base; the game gives 0 for a base the unique doesn't use.
                    byte sub = e.subTypes is { Count: > 0 } subs ? subs[0] : (byte)0;
                    _all.Add((e.uniqueID, e.getEffectiveLevelForLegendaryPotential(sub), e.rerollChance, e.canDropRandomly, e.isPrimordialItem, e.isCocoonedItem));
                }
            }

            private List<ushort> Where(Func<(ushort Id, int LpLevel, float Reroll, bool Random, bool Primordial, bool Cocooned), bool> keep) =>
                _all.Where(keep).Select(u => u.Id).OrderBy(id => id).ToList();

            /// <summary>Uniques whose LP level is at least the given one (the higher, the rarer a high LP roll).</summary>
            public List<ushort> LpLevel(int min) => Where(u => !u.Cocooned && u.LpLevel >= min);
            /// <summary>Uniques a drop rerolls into another at least this often, below the next band.</summary>
            public List<ushort> Reroll(float min, float below = 1f) => Where(u => u.Reroll >= min - 0.0001f && u.Reroll < below - 0.0001f);
            public List<ushort> Primordial => Where(u => u.Primordial);
            public List<ushort> Cocooned => Where(u => u.Cocooned);
            /// <summary>Uniques that never drop at random: boss, dungeon and other set sources (not Primordial or Cocooned).</summary>
            public List<ushort> SetSource => Where(u => !u.Random && !u.Primordial && !u.Cocooned);
        }

        /// <summary>The unique groups; empty when the game's unique list isn't loaded yet (not kept then).</summary>
        public static UniqueGroups Uniques
        {
            get
            {
                if (s_uniques != null) return s_uniques;
                var list = UniqueList.get()?.uniques;
                if (list == null || list.Count == 0) return new UniqueGroups(Array.Empty<UniqueList.Entry>());
                var entries = new List<UniqueList.Entry>(list.Count);
                foreach (var e in list) entries.Add(e);
                return s_uniques = new UniqueGroups(entries);
            }
        }

        /// <summary>Forgets the affix and unique groups (the feature stopping).</summary>
        public static void Forget()
        {
            s_groups = null;
            s_uniques = null;
        }

        private const Steps LRSV = Steps.All, LRS = Steps.Levelling | Steps.Regular | Steps.Strict, LR = Steps.Levelling | Steps.Regular;

        private static readonly List<int> s_smallIdols = new() { 25, 26, 27, 28 };

        /// <summary>The table, top first (contracts: EA · Base).</summary>
        // Every equipment type (weapons, armour, jewellery) and the idols, for the levelling rules.
        private static readonly List<int> s_gear = HiddenTypes;

        // Uniques by the game's own numbers: how often a drop is rerolled into
        // another unique, and the LP level that makes a high LP or WW roll rare.
        private static Action<RuleConditions> UniquesOf(Func<UniqueGroups, List<ushort>> ids, int? lp = null, int? ww = null) => c =>
        {
            c.Uniques = ids(Uniques);
            if (lp != null || ww != null) c.Potential = (lp, ww);
        };

        public static readonly BaseRule[] Table =
        {
            new("lp4", "BASE: uniques · 4 LP or 22 WW", LRSV, () => Looks.UniqueTop(emphasized: true, beam: true), c => { c.Rarity = RarityUnique; c.Potential = (4, 22); }),
            new("rarest", "BASE: rarest uniques (rerolled 98%+)", LRSV, () => Looks.UniqueTop(emphasized: true, beam: true), UniquesOf(u => u.Reroll(0.98f))),
            new("lp3.58", "BASE: uniques · 3 LP, LP level 58+", LRSV, () => Looks.UniqueTop(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(58), lp: 3)),
            new("lp2.100", "BASE: uniques · 2 LP, LP level 100+", LRSV, () => Looks.UniqueTop(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(100), lp: 2)),
            new("veryrare.lp1", "BASE: very rare uniques (rerolled 80%+) · 1 LP", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.Reroll(0.8f, 0.98f), lp: 1)),
            new("ww21", "BASE: uniques · 21+ WW, LP level 30+", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(30), ww: 21)),
            new("ww20", "BASE: uniques · 20+ WW, LP level 40+", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(40), ww: 20)),
            new("ww19", "BASE: uniques · 19+ WW, LP level 50+", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(50), ww: 19)),
            new("ww18", "BASE: uniques · 18+ WW, LP level 68+", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(68), ww: 18)),
            new("lp2.70", "BASE: uniques · 2 LP, LP level 70+", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(70), lp: 2)),
            new("lp3.27", "BASE: uniques · 3 LP, LP level 27+", LRSV, () => Looks.Unique(emphasized: true, beam: true), UniquesOf(u => u.LpLevel(27), lp: 3)),
            new("lp2.48", "BASE: uniques · 2 LP, LP level 48+", LRS, () => Looks.Unique(emphasized: false, beam: true), UniquesOf(u => u.LpLevel(48), lp: 2)),
            new("setsource.lp1", "BASE: boss and other set-source uniques · 1 LP", LRS, () => Looks.Unique(emphasized: true, beam: false), UniquesOf(u => u.SetSource, lp: 1)),
            new("veryrare", "BASE: very rare uniques (rerolled 80%+)", LRS, () => Looks.Unique(emphasized: false, beam: false), UniquesOf(u => u.Reroll(0.8f, 0.98f))),
            new("primordial", "BASE: Primordial uniques", LRS, () => Looks.Unique(emphasized: true, beam: false), UniquesOf(u => u.Primordial)),
            new("lp3", "BASE: uniques · 3 LP or 20 WW", LRS, () => Looks.Unique(emphasized: false, beam: true), c => { c.Rarity = RarityUnique; c.Potential = (3, 20); }),
            new("setsource", "BASE: boss and other set-source uniques", LR, () => new RuleLook { Emphasized = true }, UniquesOf(u => u.SetSource)),
            new("cocooned", "BASE: Cocooned uniques", LR, () => new RuleLook(), UniquesOf(u => u.Cocooned)),
            new("lp2", "BASE: uniques · 2 LP or 17 WW", LR, () => new RuleLook { Emphasized = true }, c => { c.Rarity = RarityUnique; c.Potential = (2, 17); }),
            new("lp1", "BASE: uniques · 1 LP or 14 WW", LR, () => new RuleLook(), c => { c.Rarity = RarityUnique; c.Potential = (1, 14); }),
            new("legendary", "BASE: legendary items", LRSV, () => Looks.UniqueTop(emphasized: true, beam: true, sound: false), c => c.Rarity = RarityLegendary),
            new("t8", "BASE: any T8 affix", LRSV, () => Looks.Exalted(loud: true, emphasized: true, largest: true), c =>
            {
                c.Types = Enumerable.Range(0, 25).ToList();
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 8, Min = 1 });
            }),
            new("exalt3", "BASE: exalted · 3+ T6, one T7", LRSV, () => Looks.Exalted(loud: true, emphasized: true), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 6, Min = 3 });
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 7, Min = 1 });
                c.Corruption = 2;
            }),
            new("exalt2t7", "BASE: exalted · two T7", LRSV, () => Looks.Exalted(loud: true, emphasized: true), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 7, Min = 2 });
                c.Corruption = 2;
            }),
            new("exalt2", "BASE: exalted · T7 and T6", LRS, () => Looks.Exalted(loud: false, emphasized: true), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 6, Min = 2 });
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 7, Min = 1 });
                c.Corruption = 2;
            }),
            new("exalt2t6", "BASE: exalted · two T6+", LR, () => Looks.Exalted(), c =>
            {
                c.Types = Enumerable.Range(0, 25).ToList();
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 6, Min = 2 });
            }),
            new("t7", "BASE: any T7 affix", LR, () => Looks.T7(emphasized: false, beam: false, sound: false), c =>
            {
                c.Types = Enumerable.Range(0, 25).ToList();
                c.Affixes.Add(new AffixNeed { Ids = Groups.Equipment, TierBar = 7, Min = 1 });
            }),
            new("sets", "BASE: set items", LR, Looks.Set, c => c.Rarity = RaritySet),
            new("uniques", "BASE: other uniques (to 80)", LR, () => new RuleLook(), c =>
            {
                c.Rarity = RarityUnique;
                c.Levels = (0, 80);
            }),
            new("weaver.idols", "BASE: weaver idols · two weaver affixes", LRSV, () => Looks.Idol(emphasized: true), c =>
            {
                c.Types = s_smallIdols.ToList();
                c.Affixes.Add(new AffixNeed { Ids = Groups.Weaver, Min = 2 });
            }),
            new("idols.35", "BASE: all idols (to 35)", LR, () => new RuleLook(), c =>
            {
                c.Types = IdolTypes.ToList();
                c.Levels = (0, 35);
            }),
            // Levelling: everything early, then rares, then exalted or better, each switched off by level as gear outgrows it.
            new("exalted.60", "BASE: exalted or better (to 60)", LRS, () => new RuleLook(), c =>
            {
                c.Rarity = RarityUnique | RaritySet | RarityLegendary | RarityExalted;
                c.Levels = (0, 60);
            }),
            new("rares.30", "BASE: rare items (to 30)", LR, () => new RuleLook(), c =>
            {
                c.Types = s_gear.ToList();
                c.Rarity = RarityRare;
                c.Levels = (0, 30);
            }),
            new("health.50", "BASE: health or physical resistance T3+ (to 50)", LR, () => new RuleLook(), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Health, TierBar = 3, Min = 1 });
                c.Levels = (0, 50);
            }),
            new("health.30", "BASE: health or physical resistance T2+ (to 30)", LR, () => new RuleLook(), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Health, TierBar = 2, Min = 1 });
                c.Levels = (0, 30);
            }),
            new("resist.50", "BASE: resistances T3+ (to 50)", LR, () => new RuleLook(), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Resistances, TierBar = 3, Min = 1 });
                c.Levels = (0, 50);
            }),
            new("resist.30", "BASE: resistances T2+ (to 30)", LR, () => new RuleLook(), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Resistances, TierBar = 2, Min = 1 });
                c.Levels = (0, 30);
            }),
            new("boots.50", "BASE: boots with movement speed or cooldown T3+ (to 50)", LR, () => new RuleLook { Emphasized = true }, c =>
            {
                c.Types = new List<int> { (int)EquipmentType.BOOTS };
                c.Affixes.Add(new AffixNeed { Ids = Groups.Movespeed.Concat(Groups.Cooldown).OrderBy(i => i).ToList(), TierBar = 3, Min = 1 });
                c.Levels = (0, 50);
            }),
            new("boots.30", "BASE: boots with movement speed or cooldown T2+ (to 30)", LR, () => new RuleLook { Emphasized = true }, c =>
            {
                c.Types = new List<int> { (int)EquipmentType.BOOTS };
                c.Affixes.Add(new AffixNeed { Ids = Groups.Movespeed.Concat(Groups.Cooldown).OrderBy(i => i).ToList(), TierBar = 2, Min = 1 });
                c.Levels = (0, 30);
            }),
            new("special.40", "BASE: experimental, personal and champion affixes (to 40)", Steps.Levelling, () => new RuleLook(), c =>
            {
                c.Affixes.Add(new AffixNeed { Ids = Groups.Experimental.Concat(Groups.Personal).Concat(Groups.Champion).ToList() });
                c.Levels = (0, 40);
            }),
        };

        /// <summary>The table as plan rules (origin EA · Base, key base.&lt;id&gt;), in order.</summary>
        public static List<PlanRule> RulesFor()
        {
            var rules = new List<PlanRule>(Table.Length);
            foreach (var row in Table)
            {
                var rule = new PlanRule { Key = "base." + row.Id, Origin = RuleOrigin.EaBase, Name = row.Name, Steps = row.Steps, Look = row.Look() };
                row.Fill(rule.Conditions);
                // A group the game's list didn't give leaves an affix condition that matches nothing.
                if (rule.Conditions.Affixes.Exists(a => a.Ids.Count == 0) || rule.Conditions.Uniques is { Count: 0 }) continue;
                rules.Add(rule);
            }
            return rules;
        }

        /// <summary>The EA · Base filter alone, as a plan: its table at every rule's own steps for Regular.</summary>
        public static FilterPlan Plan()
        {
            var plan = new FilterPlan { Name = EaBaseName, Description = "Made by Epoch Assist: general rules for any build. Planner filters are made on top of it." };
            plan.Rules.AddRange(RulesFor());
            foreach (var rule in plan.Rules) rule.Enabled = (rule.Steps & StepOf(1)) != 0;
            return plan;
        }
    }
}
