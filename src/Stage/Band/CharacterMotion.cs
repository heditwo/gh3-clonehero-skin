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
            if (chains.Length is < 1 or > 8 || chains.Any(c => c.Bones.Length != 3 || c.Hinge.Length != 3 || !c.Hinge.All(float.IsFinite) ||
                    !float.IsFinite(c.CosMax) || !float.IsFinite(c.CosMin) || c.CosMax is < -1 or > 1 || c.CosMin is < -1 or > 1))
                throw new InvalidDataException($"Invalid IK chains {name}");
        return bank;
    }
}

// How a character on a character library profile plays: its own rig, the clips of its animation set, and the position
// offsets that move a clip from the clip rig to the profile rig. GH3 keys clip translations relative to the skeleton rest
// pose, and the library stores them on the clip rig (pipeline/character_library.py), so a clip plays on the profile with
// offset = profile rest position - clip rig rest position for each bone.
internal sealed class CharacterMotion
{
    public CharacterProfile Profile { get; init; } = new();
    public PerformanceMember Member { get; init; } = new();
    public string ClipDirectory { get; init; } = "";
    public Vector3[] Offsets { get; init; } = Array.Empty<Vector3>();
    public HairDefinition? Hair { get; init; }
    public IkChainDefinition[]? Ik { get; init; }
    public string SetName { get; init; } = "";
    // The song facial clips of the band library member of the role, and their offsets to the profile rig.
    public PerformanceMember? Face { get; set; }
    public string FaceDirectory { get; set; } = "";
    public Vector3[]? FaceOffsets { get; set; }

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
            Profile = profile, SetName = setName,
            // The sets are shared by the profiles; each performer gets its own member with the bone names of its rig.
            Member = new PerformanceMember { Bones = profile.Bones, Groups = set.Groups, Layers = set.Layers, Clips = set.Clips },
            ClipDirectory = Path.Combine(character.LibraryFolder, "clips"),
            Offsets = RestOffsets(profile.Bones, profile.Bind, library.SetClipRigs[setName].Bones, library.SetClipRigs[setName].Bind),
            Hair = hair, Ik = bank.Ik.TryGetValue(profile.Ik, out var ik) ? ik : null,
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
