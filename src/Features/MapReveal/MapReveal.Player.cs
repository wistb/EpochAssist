using EpochAssist.Core;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using S = EpochAssist.Features.MapRevealSettings;

namespace EpochAssist.Features;

// The player's own map icon: another of the game's loaded icon sprites, a tint, a size and a turn, set on the icon's
// map image only. The game's ChangeIcon is not used: it resizes the image to the sprite's raw size (400 for its own
// arrow) instead of the game's icon size. One Apply/Restore edit holds all four, so switching off gives the game's back.
internal static partial class MapReveal
{
    public const string PlayerGroup = "map: player icon";

    /// <summary>What the player's icon looks like: a sprite name (empty for the game's), a tint, a size factor and a
    /// turn (degrees, or "none" to keep it upright).</summary>
    internal readonly record struct PlayerLook(string Sprite, Color32 Tint, float Size, string Turn, Vector2 Offset);

    // The game's image as Apply found it, to put back on Restore.
    private static IntPtr s_playerImage;
    private static Sprite? s_playerSprite;
    private static Color s_playerColour;
    private static Vector2 s_playerBox;
    private static Vector2 s_playerPivot;
    // The turn added after the game sets the icon's rotation each frame (from where the player faces); "Keep upright"
    // keeps it at zero instead. The rotation we last wrote, so a frame where the game wrote none adds nothing twice.
    private static bool s_playerUpright;
    private static float s_playerTurn;
    private static float s_playerWritten = float.NaN;


    // The player's icon's UI, from the icon manager's list, found once per icon.
    private static Il2CppLE.UI.Minimap.MinimapIconUI? s_playerUi;
    private static IntPtr s_playerUiFor;

    // Loaded sprites by name, the largest of each: looked up once and again when a name isn't there yet.
    private static readonly Dictionary<string, Sprite> s_sprites = new();
    private static float s_spritesReadAt = -999f;

    internal static readonly EditTarget<PlayerLook> PlayerIconTarget = new()
    {
        Name = "map: player icon",
        Find = () => One(PlayerImage()),
        Apply = (o, look) =>
        {
            var image = o.Cast<Image>();
            var rt = image.rectTransform;
            s_playerImage = image.Pointer;
            s_playerSprite = image.sprite;
            s_playerColour = image.color;
            s_playerBox = rt.sizeDelta;
            s_playerPivot = rt.pivot;
            var pivot = s_playerPivot;
            if (look.Sprite.Length > 0 && SpriteNamed(look.Sprite) is { } sprite)
            {
                image.sprite = sprite;
                // Into the game's box, keeping the sprite's shape, turning about the sprite's own pivot (its artist's
                // point for where the player is), which the image otherwise ignores.
                var r = sprite.rect;
                float side = Mathf.Max(s_playerBox.x, s_playerBox.y), fit = side / Mathf.Max(r.width, r.height);
                rt.sizeDelta = new Vector2(r.width * fit, r.height * fit);
                pivot = new Vector2(sprite.pivot.x / r.width, sprite.pivot.y / r.height);
            }
            // The offset moves the picture within the icon, so it turns with it about the player's point: the pivot
            // goes the other way, in the icon's own units.
            var size = rt.sizeDelta;
            if (size.x > 0f && size.y > 0f) pivot -= new Vector2(look.Offset.x / size.x, look.Offset.y / size.y);
            rt.pivot = pivot;
            image.color = look.Tint;
            rt.localScale = new Vector3(look.Size, look.Size, 1f);
            s_playerUpright = look.Turn == S.TurnNone;
            s_playerTurn = float.TryParse(look.Turn, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t) ? t : 0f;
            s_playerWritten = float.NaN;

        },
        Restore = (o, _) =>
        {
            var image = o.Cast<Image>();
            if (image.Pointer != s_playerImage) return;
            var rt = image.rectTransform;
            if (s_playerSprite != null) image.sprite = s_playerSprite;
            image.color = s_playerColour;
            rt.sizeDelta = s_playerBox;
            rt.pivot = s_playerPivot;
            rt.localScale = Vector3.one;
            s_playerUpright = false;
            s_playerTurn = 0f;

            s_playerImage = IntPtr.Zero;
        },
    };

    /// <summary>Whether the turn patch is needed: a turn or "Keep upright" is held.</summary>
    private static bool PlayerTurning => s_playerImage != IntPtr.Zero && (s_playerUpright || s_playerTurn != 0f);

    // After the game set the icon's rotation for this frame: our turn added, or upright.
    [HarmonyPatch(typeof(Il2CppLE.UI.Minimap.MinimapIconManager), nameof(Il2CppLE.UI.Minimap.MinimapIconManager.LateUpdate))]
    internal static class PlayerTurnPatch
    {
        private static void Postfix()
        {
            if (!PlayerTurning || !Game.Alive(s_playerUi)) return;
            var image = s_playerUi!.MainImage;
            if (!Game.Alive(image) || image.Pointer != s_playerImage) return;
            var rt = image.rectTransform;
            if (s_playerUpright || s_playerTurn != 0f)
            {
                float now = rt.localEulerAngles.z;
                if (float.IsNaN(s_playerWritten) || Mathf.Abs(Mathf.DeltaAngle(now, s_playerWritten)) >= 0.01f)
                {
                    s_playerWritten = s_playerUpright ? 0f : now + s_playerTurn;
                    rt.localEulerAngles = new Vector3(0f, 0f, s_playerWritten);
                }
            }

        }
    }

    /// <summary>The debug dot: where the game places the player on the map (the icon's place, whatever its art).</summary>
    public static void DrawCharacterPoint()
    {
        if (!S.ShowCharacterPoint.Value || Event.current.type != EventType.Repaint || PlayerImage() is not { } image) return;
        // The transform's position is its pivot's: the point the icon turns around, whatever the picture's offset.
        Vector3 world = image.rectTransform.position;
        var canvas = image.canvas;
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
        var at = new Vector2(screen.x, Screen.height - screen.y);
        Draw.FillRect(new Rect(at.x - 4f, at.y - 4f, 8f, 8f), Color.black);
        Draw.FillRect(new Rect(at.x - 3f, at.y - 3f, 6f, 6f), new Color(1f, 0.25f, 0.9f, 1f));
    }

    private static Image? PlayerImage()
    {
        var icon = PlayerIcon();
        if (icon == null) return null;
        if (!Game.Alive(s_playerUi) || s_playerUiFor != icon.Pointer)
        {
            s_playerUi = null;
            var manager = Il2CppLE.UI.Minimap.MinimapIconManager.Instance;
            if (!Game.Alive(manager) || manager!.icons is not { } list) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                if (info?.icon == null || info.icon.Pointer != icon.Pointer || !Game.Alive(info.ui)) continue;
                s_playerUi = info.ui;
                s_playerUiFor = icon.Pointer;
                break;
            }
        }
        var image = Game.Alive(s_playerUi) ? s_playerUi!.MainImage : null;
        return Game.Alive(image) ? image : null;
    }

    // The local player's map icon, from the player's own object.
    private static Il2CppDMM.DMMapIcon? PlayerIcon()
    {
        var player = Player.Object;
        if (!Game.Alive(player)) return null;
        foreach (var icon in player!.GetComponentsInChildren<Il2CppDMM.DMMapIcon>(true))
        {
            if (Game.Alive(icon)) return icon;
        }
        return null;
    }

    /// <summary>A loaded sprite by name, the largest when the game has several sizes; null when it isn't loaded.</summary>
    internal static Sprite? SpriteNamed(string name)
    {
        if (s_sprites.TryGetValue(name, out var sprite) && sprite != null) return sprite;
        // A full sprite search is slow: at most every few seconds, for a name not found yet.
        if (Time.unscaledTime - s_spritesReadAt < 5f) return null;
        s_spritesReadAt = Time.unscaledTime;
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
        {
            var sp = obj.TryCast<Sprite>();
            if (sp == null || (sp.name != S.GamePlayerSprite && Array.FindIndex(S.PlayerIcons, o => o.Value == sp.name) < 0)) continue;
            if (!s_sprites.TryGetValue(sp.name, out var known) || known == null || known.rect.width < sp.rect.width) s_sprites[sp.name] = sp;
        }
        return s_sprites.TryGetValue(name, out sprite) ? sprite : null;
    }

    /// <summary>The dropdown's preview of an option: its sprite's texture and part of it.</summary>
    internal static (Texture Texture, Rect Uv)? SpritePreview(string name)
    {
        var sprite = SpriteNamed(name.Length > 0 ? name : S.GamePlayerSprite);
        if (sprite == null || sprite.texture == null) return null;
        var tex = sprite.texture;
        var r = sprite.textureRect;
        return (tex, new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height));
    }

    private static void TickPlayerIcon(Run run, bool on)
    {
        var look = run.Look;
        bool want = on && run.Feature.Has(PlayerGroup)
            && (S.PlayerIcon.Value.Length > 0 || S.PlayerIconColour.Value.Length > 0 || Mathf.Abs(S.PlayerIconSize.Value - 1f) > 1e-3f || S.PlayerIconTurn.Value != "0"
                || S.PlayerIconOffsetX.Value != 0f || S.PlayerIconOffsetY.Value != 0f);
        if (!want)
        {
            look.PlayerIcon?.Remove();
            look.PlayerIcon = null;
            HoldTurnPatch(run);
            return;
        }
        var value = new PlayerLook(S.PlayerIcon.Value,
            S.PlayerIconColour.TryGetColor(out var c) ? (Color32)c : new Color32(255, 255, 255, 255), S.PlayerIconSize.Value, S.PlayerIconTurn.Value,
            new Vector2(S.PlayerIconOffsetX.Value, S.PlayerIconOffsetY.Value));
        if (look.PlayerIcon is not { Removed: false } edit) look.PlayerIcon = Edits.Apply(run.Feature, PlayerIconTarget, value);
        else if (!edit.Value.Equals(value)) edit.Set(value);
        HoldTurnPatch(run);
    }

    /// <summary>The turn patch is on only while a turn is held.</summary>
    private static void HoldTurnPatch(Run run) => FeatureHost.SetPatchGroup(run.Feature, PlayerTurnGroup, PlayerTurning);

    public const string PlayerTurnGroup = "map: player icon turn";
}
