using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Gh3Skin.Packages;

namespace Gh3Skin;

// The animation sets, hair, and IK chains of a character library (characters.json, PACKAGE-FORMAT.md section 10.1).
internal sealed class CharacterMotionBank
{
    private static readonly ConcurrentDictionary<string, CharacterMotionBank> Loaded = new(StringComparer.Ordinal);
    public Dictionary<string, PerformanceMember> Animations { get; set; } = new();
    public Dictionary<string, HairDefinition> Hair { get; set; } = new();
    public Dictionary<string, IkChainDefinition[]> Ik { get; set; } = new();
    public RigConstraint[] Constraints { get; set; } = Array.Empty<RigConstraint>();

    public static CharacterMotionBank Load(string folder) => Loaded.GetOrAdd(Path.GetFullPath(folder), Read);

    private static CharacterMotionBank Read(string folder)
    {
        var bank = JsonSerializer.Deserialize<CharacterMotionBank>(File.ReadAllText(Path.Combine(folder, CharacterLibrary.FileName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Invalid character library");
        foreach (var (name, member) in bank.Animations)
            if (member.Clips.Count is < 1 or > StagePerformance.MaximumClips ||
                member.Clips.Any(c => c.Key.Length != 8 || c.Key.Any(x => !Uri.IsHexDigit(x)) ||
                    !float.IsFinite(c.Value.Duration) || c.Value.Duration is <= 0 or > PerformanceClip.MaximumSeconds || c.Value.Frames is < 2 or > PerformanceClip.MaximumFrames) ||
                member.Groups.Values.Concat(member.Layers.Values).SelectMany(c => c).Any(c => !member.Clips.ContainsKey(c)))
                throw new InvalidDataException($"Invalid animation set {name}");
        foreach (var (name, chains) in bank.Ik)
            if (chains.Length < 1 || !IkChainDefinition.ValidChains(chains)) throw new InvalidDataException($"Invalid IK chains {name}");
        if (!RigConstraint.ValidRows(bank.Constraints)) throw new InvalidDataException("Invalid rig constraints");
        return bank;
    }
}

// The animation set that a performer plays and the GH3 rig that it plays on (PACKAGING-PLAN decision 15): a set of a
// character library profile on the profile rig (PACKAGE-FORMAT.md section 10.1), or the set of a band library member
// on the role rig. GH3 keys clip translations relative to the skeleton rest pose, and the character library stores them
// on the clip rig of the set (pipeline/character_library.py), so a clip plays on the profile with
// offset = profile rest position - clip rig rest position for each bone.
internal sealed class CharacterMotion
{
    public CharacterProfile Profile { get; init; } = new();
    // The rig of Profile (for a band library member: the rig of band/<role>/animation.bin).
    public BandAnimation Rig { get; init; } = null!;
    // The band role of a band library set ("" for a set of a character library). Its member comes from
    // direction/performance.json when the timeline loads.
    public string LibraryRole { get; init; } = "";
    public PerformanceMember Member { get; set; } = new();
    // The clip folder (for a band library set: direction/<role> of the band library, set when the timeline loads).
    public string ClipDirectory { get; set; } = "";
    public Vector3[]? Offsets { get; init; }
    public HairDefinition? Hair { get; init; }
    // The IK chains, the twist and split constraints, and the rig points of the rig of the set (catalog.json or
    // characters.json).
    public IkChainDefinition[] Ik { get; init; } = Array.Empty<IkChainDefinition>();
    public RigConstraint[] Constraints { get; init; } = Array.Empty<RigConstraint>();
    public RigPoints Points { get; init; } = RigPoints.None;
    public string SetName { get; init; } = "";
    // The song facial clips of the band library member of the role, and their offsets to the profile rig.
    public PerformanceMember? Face { get; set; }
    public string FaceDirectory { get; set; } = "";
    public Vector3[]? FaceOffsets { get; set; }

    public bool IsLibrary => LibraryRole.Length > 0;
    // The band role of the set: the role of the band library member or of the profile.
    public string Role => IsLibrary ? LibraryRole : Profile.Role;

    // The set of the band library member of a role on its rig, with the rig data of the library (catalog.json). `bind`:
    // the local bind rows; a performer without a character does not need them.
    public static CharacterMotion Library(string role, BandAnimation rig, HairDefinition? hair, LibraryRigs rigs, float[][]? bind = null)
    {
        var data = rigs.Role(role);
        return new()
        {
            Profile = new CharacterProfile { Role = role, Bones = data.Bones, Parents = rig.Parents, Bind = bind ?? Array.Empty<float[]>() },
            Rig = rig, LibraryRole = role, Hair = hair, Ik = data.Ik, Constraints = rigs.Constraints, Points = data.Points,
        };
    }

    public static CharacterMotion Create(CharacterModel character)
    {
        var library = CharacterLibrary.Load(character.LibraryFolder);
        var profile = library.Profiles.TryGetValue(character.Profile, out var found) ? found
            : throw new InvalidDataException($"library {character.Library} has no profile {character.Profile}");
        var bank = CharacterMotionBank.Load(character.LibraryFolder);
        var setName = character.Animations.Length > 0 ? character.Animations : profile.Animations;
        var set = bank.Animations.TryGetValue(setName, out var animations) ? animations : throw new InvalidDataException($"no animation set {setName}");
        if (!library.SetClipRigs[setName].Bones.SequenceEqual(profile.Bones)) throw new InvalidDataException($"animation set {setName} is for another rig than profile {profile.Id}");
        HairDefinition? hair = null;
        if (profile.Hair.Length > 0 && !bank.Hair.TryGetValue(profile.Hair, out hair)) throw new InvalidDataException($"no hair {profile.Hair}");
        return new CharacterMotion
        {
            Profile = profile, SetName = setName, Rig = BandAnimation.FromRig(profile.Parents, profile.Bind),
            // The sets are shared by the profiles; each performer gets its own member with the bone names of its rig.
            Member = new PerformanceMember { Bones = profile.Bones, Groups = set.Groups, Layers = set.Layers, Clips = set.Clips },
            ClipDirectory = Path.Combine(character.LibraryFolder, "clips"),
            Offsets = RestOffsets(profile.Bones, profile.Bind, library.SetClipRigs[setName].Bones, library.SetClipRigs[setName].Bind),
            Hair = hair, Ik = bank.Ik.TryGetValue(profile.Ik, out var ik) ? ik : Array.Empty<IkChainDefinition>(), Constraints = bank.Constraints,
            Points = library.SetClipRigs[setName].Points,
        };
    }

    // For each bone of the profile: its rest position minus the rest position of the bone with the same name in the other
    // rig (zero when the other rig does not have it).
    public static Vector3[] RestOffsets(IReadOnlyList<string> bones, IReadOnlyList<float[]> bind, IReadOnlyList<string> otherBones, IReadOnlyList<float[]> otherBind)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < otherBones.Count; i++) index.TryAdd(otherBones[i], i);
        var result = new Vector3[bones.Count];
        for (var i = 0; i < bones.Count; i++)
            if (index.TryGetValue(bones[i], out var other))
                result[i] = new Vector3(bind[i][0] - otherBind[other][0], bind[i][1] - otherBind[other][1], bind[i][2] - otherBind[other][2]);
        return result;
    }
}
