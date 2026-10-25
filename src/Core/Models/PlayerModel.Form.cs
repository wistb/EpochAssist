using Il2Cpp;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    private static void FillForm(Form form)
    {
        if (Online == true)
        {
            // Online there is no PlayerAbilityList; the client's list says whether the player is transformed. Which
            // transform it is wasn't seen online (no transform character), so a transformed player reads unknown.
            ClientAbilityList clientList = Component(ref s_clientAbilities) ?? throw new InvalidOperationException("no client ability list");
            if (clientList.transformed) throw new InvalidOperationException("transformed online: transform id not known");
            form.TransformId = null;
        }
        else
        {
            PlayerAbilityList list = Game.LocalTree?.playerAbilityList ?? throw new InvalidOperationException("no ability list");
            form.TransformId = TransformId(list);
        }
        form.Reaper = null;
        if (!Has("reaper form")) return;
        try
        {
            form.Reaper = ReaperMaterial() is { } material && material.materialToChangeTo == UIGlobeHealth.AlternateMaterial.ReaperForm;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("player:reaper", $"Player model Reaper form read failed: {e.Message}", 30);
        }
    }

    // The game hands an empty Nullable<int> back boxed as null, which the interop getter can't unbox: in human form the
    // getter throws a NullReferenceException (seen in 1.5.3). Throwing every frame is slow, so the backing field is read
    // in place: its hasValue flag and value at the offsets il2cpp reports. The getter is the fallback.
    private static int s_formHasValueOffset = -2, s_formValueOffset;

    private static int? TransformId(PlayerAbilityList list)
    {
        if (s_formHasValueOffset == -2) FindFormOffsets();
        if (s_formHasValueOffset >= 0)
        {
            IntPtr field = list.Pointer + s_formHasValueOffset;
            return System.Runtime.InteropServices.Marshal.ReadByte(field) != 0
                ? System.Runtime.InteropServices.Marshal.ReadInt32(list.Pointer + s_formValueOffset)
                : null;
        }
        try
        {
            var id = list.TransformAbilityId;
            return id.HasValue ? id.Value : null;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static void FindFormOffsets()
    {
        s_formHasValueOffset = -1;
        try
        {
            IntPtr owner = Il2CppInterop.Runtime.Il2CppClassPointerStore<PlayerAbilityList>.NativeClassPtr;
            IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(owner, "<TransformAbilityId>k__BackingField");
            if (field == IntPtr.Zero) return;
            IntPtr nullable = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_type(field));
            IntPtr hasValue = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(nullable, "hasValue");
            IntPtr value = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(nullable, "value");
            if (hasValue == IntPtr.Zero || value == IntPtr.Zero) return;
            // Field offsets in a value type count its object header, which an embedded struct doesn't have.
            int header = 2 * IntPtr.Size;
            int at = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
            s_formValueOffset = at + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(value) - header;
            s_formHasValueOffset = at + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(hasValue) - header;
            Log.Debug($"Player model: transform id at +{s_formHasValueOffset} (has) / +{s_formValueOffset} (value)");
        }
        catch (Exception e)
        {
            s_formHasValueOffset = -1;
            Log.Warn($"Player model: transform id field not found, using the getter: {e.Message}");
        }
    }

    // A search of the player's children is slow, so while the component is absent it is searched for at most twice a
    // second; once found it is kept while it lives. Entering the Reaper form can show up to half a second late.
    private const float ReaperLookupSeconds = 0.5f;
    private static ChangeHealthMaterialDuringLifetime? s_reaper;
    private static Cooldown s_reaperLookup;

    private static ChangeHealthMaterialDuringLifetime? ReaperMaterial()
    {
        if (Game.Alive(s_reaper)) return s_reaper;
        s_reaper = null;
        if (s_object == null || !s_reaperLookup.Ready) return null;
        s_reaperLookup.Start(ReaperLookupSeconds);
        s_reaper = s_object.GetComponentInChildren<ChangeHealthMaterialDuringLifetime>();
        return Game.Alive(s_reaper) ? s_reaper : null;
    }
}
