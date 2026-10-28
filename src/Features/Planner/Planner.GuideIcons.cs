using EpochAssist.Core;
using Il2Cpp;
using Il2CppLE.AssetBundles;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class Planner
{
    public const string GuideIconsGroup = "guide icons";

    internal enum IconState { Asked, Ready, Missing }

    /// <summary>A guide name's game icon: asked for once, ready when its sprite has loaded.</summary>
    internal sealed class GuideIcon
    {
        public IconState State = IconState.Asked;
        public Texture? Texture;
        /// <summary>The sprite's part of its texture, in UV.</summary>
        public Rect Uv;
        /// <summary>The sprite's size in texture pixels.</summary>
        public Vector2 Size;
        internal float NextCheck;
        public string Source = string.Empty;
        public string Why = string.Empty;
        // A skill's or item's own sprite; a node's icon comes from its tree's prefab.
        internal LoadRef? Handle;
        internal string? Tree;
        internal byte Node;
    }

    // One loaded tree prefab per tree id: its node icons by node id once it has loaded.
    private sealed class TreeIcons
    {
        public readonly List<LoadRef> Handles = new();
        public Dictionary<byte, Sprite>? Nodes;
        public bool Failed;
        /// <summary>A class's passive tree: its icons are read from the game's built passive panel.</summary>
        public int? ClassId;
        public float NextLook;
        public Dictionary<byte, Sprite>? Built;
        /// <summary>The tree's nodes by id, for the game's node tooltip.</summary>
        public readonly Dictionary<byte, SkillTreeNode> Objects = new();
    }

    private static readonly Dictionary<string, GuideIcon> s_icons = new();
    private static readonly Dictionary<string, TreeIcons> s_treeIcons = new();
    private static int s_iconsPending;
    // Each icon's name, to ask for it again.
    private static readonly Dictionary<GuideIcon, Embed> s_iconsEmbeds = new();

    /// <summary>The loaded icons, for the dev bridge.</summary>
    internal static IReadOnlyDictionary<string, GuideIcon> GuideIcons => s_icons;

    /// <summary>Asks once for every resolved name's icon, without waiting for any.</summary>
    public static void AskIcons(BuildGuide guide)
    {
        if (!PlannerSettings.GuideEnabled.Value || s_run?.Feature is not { } feature || !feature.Has(GuideIconsGroup)) return;
        foreach (var embed in guide.Embeds.Values)
        {
            if (!embed.Resolved || embed.Kind == EmbedKind.Affix || s_icons.ContainsKey(IconKey(embed))) continue;
            var icon = new GuideIcon();
            s_icons[IconKey(embed)] = icon;
            s_iconsEmbeds[icon] = embed;
            try
            {
                Ask(embed, icon);
            }
            catch (Exception e)
            {
                icon.State = IconState.Missing;
                icon.Why = e.Message;
                Log.ErrorThrottled("planner:guide icons", $"Planner: a guide icon couldn't be loaded: {e.Message}", 30);
            }
            if (icon.State == IconState.Asked) s_iconsPending++;
        }
    }

    /// <summary>The name's icon once it has loaded, else null.</summary>
    public static GuideIcon? Icon(Embed embed) =>
        s_icons.TryGetValue(IconKey(embed), out var icon) && icon.State == IconState.Ready ? icon : null;

    // Items share an icon per base and unique, whatever their affixes; made once per name.
    private static string IconKey(Embed embed) =>
        embed.IconKey ??= embed.Kind == EmbedKind.Item ? $"item:{embed.ItemType}:{embed.SubType}:{embed.Unique}" : embed.Key;

    private static void Ask(Embed embed, GuideIcon icon)
    {
        switch (embed.Kind)
        {
            case EmbedKind.Skill:
            {
                var ability = embed.AbilityId != null ? AbilityManager.getPlayerAbility(embed.AbilityId) : null;
                icon.Source = "ability sprite";
                if (!Game.Alive(ability) || ability!.abilitySpriteSoftRef is not { } soft) { Missing(icon, "no ability sprite"); return; }
                icon.Handle = Load(soft);
                return;
            }
            case EmbedKind.Item:
            {
                icon.Source = "item sprite";
                var soft = ItemData.GetItemSprite(embed.ItemType, embed.SubType, embed.Unique != 0, embed.Unique, ItemUIContext.Default);
                if (soft == null) { Missing(icon, "no item sprite"); return; }
                icon.Handle = Load(soft);
                return;
            }
            default:
                icon.Source = "tree";
                icon.Tree = embed.Tree;
                icon.Node = embed.Node;
                if (!s_treeIcons.ContainsKey(embed.Tree)) s_treeIcons[embed.Tree] = AskTree(embed.Tree);
                return;
        }
    }

    // A class's passive tree from the passive panel's own list, a skill's tree from the skills panel's holder; loaded as
    // assets only, never placed in a scene.
    private static TreeIcons AskTree(string tree)
    {
        var entry = new TreeIcons();
        var softs = new List<SoftRef>();
        if (ClassOfTree(tree) is int classId)
        {
            // The passive pages' prefabs hold no sprites until the game builds them, so the icons come from the
            // character's own passive panel once it's been opened.
            entry.ClassId = classId;
            return entry;
        }
        if (IsAbilityId(tree) && Panels.Find<SkillsTreesUIManager>() is { } skills && Game.Alive(skills.prefabSkillTreesHolder)
                 && skills.prefabSkillTreesHolder.Find(AbilityManager.getPlayerAbility(tree)) is { } skill)
        {
            softs.Add(skill);
        }
        foreach (var soft in softs) entry.Handles.Add(Load(soft));
        entry.Failed = entry.Handles.Count == 0;
        return entry;
    }

    // Every load is also tied to the holder, a mod object: a plain load handle alone doesn't keep the asset, which the
    // game then unloads under the guide. Destroying the holder lets them all go.
    private static GameObject? s_iconHolder;

    private static LoadRef Load(SoftRef soft)
    {
        var handle = SoftRefExtensions.CreateLoadRefTypeless(soft, "Planner.GuideIcons.cs", 0);
        if (!Game.Alive(s_iconHolder))
        {
            s_iconHolder = new GameObject(Marks.MarkName + ".GuideIcons");
            UnityEngine.Object.DontDestroyOnLoad(s_iconHolder);
        }
        handle.AddSceneLifetimeReferenceTo(s_iconHolder!);
        return handle;
    }

    /// <summary>Destroys a holder an earlier load left behind, which would keep its icons loaded.</summary>
    private static void SweepIconHolder()
    {
        var old = GameObject.Find(Marks.MarkName + ".GuideIcons");
        if (Game.Alive(old) && old != s_iconHolder) UnityEngine.Object.Destroy(old);
    }

    private static int? ClassOfTree(string tree)
    {
        for (int id = 0; id <= 4; id++)
            if (CharacterClassList.getClass(id) is { } cls && Game.Alive(cls) && cls.treeID == tree) return id;
        return null;
    }

    /// <summary>The game's own node for a guide's passive, from its tree's prefab or the built passive panel, or null.</summary>
    private static SkillTreeNode? TreeNodeOf(Embed embed) =>
        s_treeIcons.TryGetValue(embed.Tree, out var tree) && tree.Objects.TryGetValue(embed.Node, out var node) && Game.Alive(node) ? node : null;

    // An icon whose texture is gone: its handle released and the sprite asked for again; a tree's nodes are read again.
    private static void AskAgain(GuideIcon icon)
    {
        if (icon.State != IconState.Ready) return;
        icon.State = IconState.Asked;
        icon.Texture = null;
        if (icon.Tree != null)
        {
            if (s_treeIcons.TryGetValue(icon.Tree, out var tree))
            {
                foreach (var handle in tree.Handles) Release(handle);
                s_treeIcons.Remove(icon.Tree);
            }
            foreach (var other in s_icons.Values)
                if (other.Tree == icon.Tree && other.State == IconState.Ready)
                {
                    other.State = IconState.Asked;
                    other.Texture = null;
                    s_iconsPending++;
                }
        }
        if (s_iconsEmbeds.TryGetValue(icon, out var embed))
        {
            Release(icon.Handle);
            icon.Handle = null;
            try
            {
                Ask(embed, icon);
            }
            catch (Exception e)
            {
                Missing(icon, e.Message);
            }
            if (icon.Tree != null && !s_treeIcons.ContainsKey(icon.Tree)) s_treeIcons[icon.Tree] = AskTree(icon.Tree);
        }
        s_iconsPending++;
    }

    private static void Missing(GuideIcon icon, string why)
    {
        icon.State = IconState.Missing;
        icon.Why = why;
    }

    /// <summary>Every frame while icons are loading: takes in the ones that have.</summary>
    private static void PollIcons()
    {
        if (s_iconsPending == 0) return;
        try
        {
            foreach (var (id, tree) in s_treeIcons)
            {
                if (tree.ClassId != null) PollPassive(tree, id);
                else PollTree(tree);
            }
            int pending = 0;
            foreach (var icon in s_icons.Values)
            {
                if (icon.State != IconState.Asked) continue;
                if (icon.Tree != null) TakeNode(icon);
                else TakeSprite(icon);
                if (icon.State == IconState.Asked) pending++;
            }
            s_iconsPending = pending;
        }
        catch (Exception e)
        {
            s_iconsPending = 0;
            Log.ErrorThrottled("planner:guide icons", $"Planner: a guide icon couldn't be loaded: {e.Message}", 30);
        }
    }

    private static void TakeSprite(GuideIcon icon)
    {
        if (icon.Handle == null) { Missing(icon, "not asked"); return; }
        var status = icon.Handle.Status;
        if (status == AssetStatus.Loading) return;
        if (status == AssetStatus.Error) { Missing(icon, "load failed"); return; }
        var sprite = icon.Handle.TypelessAssetOrNull?.TryCast<Sprite>();
        if (!Game.Alive(sprite)) { Missing(icon, "not a sprite"); return; }
        Ready(icon, sprite!);
    }

    private static void PollTree(TreeIcons tree)
    {
        if (tree.Nodes != null || tree.Failed) return;
        if (tree.ClassId != null) return;
        foreach (var handle in tree.Handles)
            if (handle.Status == AssetStatus.Loading) return;
        tree.Nodes = new Dictionary<byte, Sprite>();
        foreach (var handle in tree.Handles)
        {
            var root = handle.Status == AssetStatus.Loaded ? handle.TypelessAssetOrNull : null;
            var go = root?.TryCast<GameObject>() ?? root?.TryCast<Component>()?.gameObject;
            if (!Game.Alive(go)) continue;
            var nodes = go!.GetComponentsInChildren<SkillTreeNode>(true);
            foreach (var node in nodes)
            {
                if (!Game.Alive(node)) continue;
                tree.Objects[node.id] = node;
                if (Game.Alive(node.icon) && Game.Alive(node.icon.sprite)) tree.Nodes[node.id] = node.icon.sprite;
            }
        }
    }

    // Looked at once a second while a passive icon is missing: every node the game's passive panel has built so far
    // (its base page, and each mastery page once shown), for this tree.
    private const float PassiveLookSeconds = 1f;

    private static void PollPassive(TreeIcons tree, string treeId)
    {
        if (Clock.Now < tree.NextLook) return;
        tree.NextLook = Clock.Now + PassiveLookSeconds;
        var panel = PassivePanelManager.instance;
        if (!Game.Alive(panel)) return;
        tree.Built ??= new Dictionary<byte, Sprite>();
        foreach (var node in panel!.GetComponentsInChildren<SkillTreeNode>(true))
            if (Game.Alive(node) && node.TreeID == treeId && Game.Alive(node.icon) && Game.Alive(node.icon.sprite))
            {
                tree.Built[node.id] = node.icon.sprite;
                tree.Objects[node.id] = node;
            }
    }

    private static void TakeNode(GuideIcon icon)
    {
        var tree = s_treeIcons[icon.Tree!];
        if (tree.Failed) { Missing(icon, "no tree prefab"); return; }
        if (tree.ClassId != null)
        {
            if (tree.Built != null && tree.Built.TryGetValue(icon.Node, out var built))
            {
                Ready(icon, built);
                // Names laid out without room for this icon get it now.
                if (icon.State == IconState.Ready) s_guideStamp++;
            }
            else icon.Why = "until the game's passive panel shows its page";
            return;
        }
        if (tree.Nodes == null)
        {
            icon.Why = "loading";
            return;
        }
        if (tree.Nodes.TryGetValue(icon.Node, out var sprite)) Ready(icon, sprite);
        else Missing(icon, "node not in the tree");
    }

    private static void Ready(GuideIcon icon, Sprite sprite)
    {
        var texture = sprite.texture;
        if (!Game.Alive(texture)) { Missing(icon, "sprite has no texture"); return; }
        Rect area;
        try
        {
            area = sprite.textureRect;
        }
        catch (Exception)
        {
            // A tightly packed sprite has no rect of its own in the atlas.
            area = sprite.rect;
        }
        icon.Texture = texture;
        icon.Uv = new Rect(area.x / texture.width, area.y / texture.height, area.width / texture.width, area.height / texture.height);
        icon.Size = new Vector2(area.width, area.height);
        icon.State = IconState.Ready;
    }

    /// <summary>Releases every icon's load handle (the Guide turned off, the feature stopping).</summary>
    public static void FreeIcons()
    {
        foreach (var icon in s_icons.Values) Release(icon.Handle);
        foreach (var tree in s_treeIcons.Values)
            foreach (var handle in tree.Handles) Release(handle);
        if (Game.Alive(s_iconHolder)) UnityEngine.Object.Destroy(s_iconHolder);
        s_iconHolder = null;
        s_icons.Clear();
        s_iconsEmbeds.Clear();
        s_treeIcons.Clear();
        s_iconsPending = 0;
    }

    private static void Release(LoadRef? handle)
    {
        if (handle == null) return;
        try
        {
            handle.ExplicitDisposeInternal();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:guide icons free", $"Planner: a guide icon couldn't be released: {e.Message}", 30);
        }
    }
}
