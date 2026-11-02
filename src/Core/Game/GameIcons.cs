using Il2Cpp;
using Il2CppLE.AssetBundles;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>An item's icon from the game: asked once per base type, subtype and unique; ready once its sprite loaded.</summary>
internal sealed class ItemIcon
{
    public bool Ready;
    public bool Missing;
    public string Why = string.Empty;
    public Sprite? Sprite;
    /// <summary>Loads asked for this icon (a sprite the game unloaded is asked for again, a few times).</summary>
    public int Asks;
    public Texture? Texture;
    /// <summary>The sprite's part of its texture, in UV (IMGUI draws with it).</summary>
    public Rect Uv;
    /// <summary>The sprite's size in texture pixels.</summary>
    public Vector2 Size;
    internal LoadRef? Handle;
}

/// <summary>
/// Item icons as the game draws them (its own sprites, through its soft refs), for uGUI images and IMGUI alike. Each
/// load is tied to one holder object of the mod's, so the game doesn't unload a sprite while it shows; <see
/// cref="Free"/> destroys the holder, which lets them all go the game's own way (never by disposing the handles). Owners call <see cref="Poll"/> each frame while icons wait.
/// </summary>
internal static class GameIcons
{
    public const string HolderName = "EpochAssist.ItemIcons";

    private static readonly Dictionary<(int Type, int Sub, int Unique), ItemIcon> s_icons = new();
    private static int s_pending;
    private static GameObject? s_holder;

    /// <summary>Icons asked and not loaded yet.</summary>
    public static int Pending => s_pending;

    /// <summary>The icon for an item (unique 0 for a plain base); asks for it the first time.</summary>
    public static ItemIcon Get(int type, int sub, int unique)
    {
        var key = (type, sub, unique);
        if (s_icons.TryGetValue(key, out var icon))
        {
            // The game can unload a sprite it shares with its own windows: ask again, a few times at most.
            if (!icon.Ready || Game.Alive(icon.Sprite) || icon.Asks >= 3) return icon;
            Release(icon);
            var again = new ItemIcon { Asks = icon.Asks };
            s_icons[key] = icon = again;
        }
        else s_icons[key] = icon = new ItemIcon();
        icon.Asks++;
        try
        {
            var soft = ItemData.GetItemSprite((byte)type, (ushort)sub, unique != 0, (ushort)unique, ItemUIContext.Default);
            if (soft == null) return Miss(icon, "no item sprite");
            icon.Handle = SoftRefExtensions.CreateLoadRefTypeless(soft, "GameIcons.cs", 0);
            if (!Game.Alive(s_holder))
            {
                s_holder = new GameObject(HolderName);
                UnityEngine.Object.DontDestroyOnLoad(s_holder);
            }
            icon.Handle.AddSceneLifetimeReferenceTo(s_holder!);
            s_pending++;
        }
        catch (Exception e)
        {
            Miss(icon, e.Message);
        }
        return icon;
    }

    /// <summary>Takes in loaded sprites; cheap when nothing waits.</summary>
    public static void Poll()
    {
        if (s_pending == 0) return;
        int pending = 0;
        foreach (var icon in s_icons.Values)
        {
            if (icon.Ready || icon.Missing) continue;
            try
            {
                var status = icon.Handle!.Status;
                if (status == AssetStatus.Loading)
                {
                    pending++;
                    continue;
                }
                if (status == AssetStatus.Error) { Miss(icon, "load failed"); continue; }
                var sprite = icon.Handle.TypelessAssetOrNull?.TryCast<Sprite>();
                if (!Game.Alive(sprite)) { Miss(icon, "not a sprite"); continue; }
                Take(icon, sprite!);
            }
            catch (Exception e)
            {
                Miss(icon, e.Message);
            }
        }
        s_pending = pending;
    }

    private static void Take(ItemIcon icon, Sprite sprite)
    {
        var texture = sprite.texture;
        if (!Game.Alive(texture)) { Miss(icon, "sprite has no texture"); return; }
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
        icon.Sprite = sprite;
        icon.Texture = texture;
        icon.Uv = new Rect(area.x / texture.width, area.y / texture.height, area.width / texture.width, area.height / texture.height);
        icon.Size = new Vector2(area.width, area.height);
        icon.Ready = true;
    }

    private static ItemIcon Miss(ItemIcon icon, string why)
    {
        icon.Missing = true;
        icon.Why = why;
        return icon;
    }

    /// <summary>Draws an icon in IMGUI fitted into a rect, keeping its shape; nothing until it is ready.</summary>
    public static void Draw(Rect r, ItemIcon icon)
    {
        if (!icon.Ready || !Game.Alive(icon.Texture) || icon.Size.x <= 0f || icon.Size.y <= 0f) return;
        float fit = Mathf.Min(r.width / icon.Size.x, r.height / icon.Size.y);
        var at = new Rect(r.center.x - icon.Size.x * fit / 2f, r.center.y - icon.Size.y * fit / 2f, icon.Size.x * fit, icon.Size.y * fit);
        GUI.DrawTextureWithTexCoords(at, icon.Texture, icon.Uv);
    }

    // A load's hold on its sprite is the reference on the holder, which goes with the holder (see Free). Disposing the
    // handle as well let the game's own loads of the same sprite go too: the stash grid showed blank items.
    private static void Release(ItemIcon icon) => icon.Handle = null;

    /// <summary>Releases every load and the holder (the owner stopping).</summary>
    public static void Free()
    {
        foreach (var icon in s_icons.Values) Release(icon);
        if (Game.Alive(s_holder)) UnityEngine.Object.Destroy(s_holder);
        s_holder = null;
        s_icons.Clear();
        s_pending = 0;
    }

    /// <summary>Destroys a holder an earlier load left behind, which would keep its icons loaded.</summary>
    public static void Sweep()
    {
        var old = GameObject.Find(HolderName);
        if (Game.Alive(old) && old != s_holder) UnityEngine.Object.Destroy(old);
    }
}
