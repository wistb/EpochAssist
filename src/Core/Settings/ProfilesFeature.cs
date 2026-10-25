namespace EpochAssist.Core;

/// <summary>
/// Follows the playing character and keeps the profile it uses in effect. Without it (the game lacks one of the
/// character members) the Default profile is always in use and slot toggles behave as before profiles.
/// </summary>
internal sealed class ProfilesFeature : Feature
{
    public override string Id => "profiles";
    public override string Name => "Profiles";
    public override string[] Needs => new[] { "game" };
    public override Requirement[] Requirements => new[]
    {
        Need<Il2Cpp.Actor>(nameof(Il2Cpp.Actor.characterDataTracker)),
        Need<Il2Cpp.CharacterDataTracker>(nameof(Il2Cpp.CharacterDataTracker.characterSlotId)),
        Need<Il2Cpp.CharacterDataTracker>(nameof(Il2Cpp.CharacterDataTracker.charData)),
        Need<Il2CppLE.Data.CharacterData>(nameof(Il2CppLE.Data.CharacterData.CharacterName)),
    };

    public override void Start()
    {
        Diagnostics.ProfileStatus = Profiles.StatusText;
        Actions.Register(Id, "profiles.new", "New profile", () => Profiles.Create(NextName("Profile"), copy: false, out _));
        Actions.Register(Id, "profiles.duplicate", "Duplicate profile", () => Profiles.Create(NextName(Profiles.Active.Name + " copy"), copy: true, out _));
    }

    /// <summary>The name, or the name with the first free number after it.</summary>
    public static string NextName(string name)
    {
        if (Profiles.Find(name) == null && !name.Equals(Profiles.DefaultName, StringComparison.OrdinalIgnoreCase)) return name;
        for (int n = 2; ; n++)
        {
            if (Profiles.Find($"{name} {n}") == null) return $"{name} {n}";
        }
    }

    public override void Update() => Profiles.Update();
    public override void OnSceneChanged() => Profiles.OnSceneChanged();

    public override void Shutdown()
    {
        Diagnostics.ProfileStatus = null;
        Profiles.Reset();
    }
}
