using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Character packages on the band: a character on the role's rig, on its character library profile, or on its own
// skeleton (retargeted), and the player's instruments on the band members.
internal sealed partial class StageBand
{
    // A character package plays the role on the role's rig: its joints map to the rig bones by name (CharacterRig), and its
    // own inverse bind matrices skin the mesh. It draws through StageAssets as the library members do. Its own instrument
    // plays in the role of its profile; otherwise the role's instrument comes from the library band model (BandAttachments).
    // In another role, `characterHair` is the hair of its profile on the role's rig. Null: use the venue default.
    private static List<StageAssets>? LoadCharacter(Packages.CharacterModel character, string directory, BandMember member, GameObject performer, int layer,
        Transform[] bones, bool lighting, Shader? surfaceShader, HairBank? hair, out HairDefinition? characterHair, Packages.InstrumentModel? instrument,
        BandAnimation roleRig, out CharacterExtras? extras)
    {
        var role = member.Role;
        var children = performer.transform.childCount;
        characterHair = null;
        extras = null;
        StageAssets? assets = null;
        try
        {
            var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
            var (rig, parents, _) = Packages.CharacterImporter.Rig(library, role);
            var ownRole = character.Role == role;
            var (profileBones, profileParents, _) = ownRole ? (rig, parents, Array.Empty<float[]>()) : Packages.CharacterImporter.Rig(library, character.Role);
            HairDefinition? profileHair = null;
            if (hair != null && hair.Members.TryGetValue(character.Role, out var found)) profileHair = found;
            var hairJoints = profileHair?.Joints.Select(j => j.Bone).ToHashSet() ?? new HashSet<int>();
            var (map, bind) = CharacterExtras.MapJoints(character, rig.Length, joints => Packages.CharacterRig.Map(joints, profileBones, profileParents, rig, hairJoints, ownRole))
                ?? throw new InvalidDataException($"its joints are not all in the {role} rig");
            extras = CharacterExtras.Create(character, map, bind, bones, layer);
            var all = bones.Concat(extras.Bones).ToArray();
            if (!ownRole && profileHair != null)
                characterHair = profileHair.Remap(bone => bone < profileBones.Length ? Array.IndexOf(rig, profileBones[bone]) : -1);
            var bindposes = bind.Select(b => character.Bindposes[b]).Select(m => new Matrix4x4
            {
                m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7],
                m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11], m30 = m[12], m31 = m[13], m32 = m[14], m33 = m[15],
            }).ToArray();
            var body = Packages.BandAttachments.BodyJoints(rig, parents);
            extras.AddToBody(body, rig.Length);
            var parts = Packages.BandAttachments.ReadParts(Path.Combine(character.Folder, "meshes.bin"));
            var ownParts = Packages.BandAttachments.AttachmentParts(parts, body, map);
            var (add, keepOwn, note) = Packages.BandAttachments.Choose(ownParts.Length > 0, ownRole, character.Instrument, member.Instrument, role);
            var picked = instrument != null && CanHold(instrument, rig);
            if (picked) (add, keepOwn) = (false, false);
            assets = new StageAssets();
            assets.Load(character.Folder, performer, layer, map.Select(i => all[i]).ToArray(), lighting, surfaceShader: surfaceShader, bindposes: bindposes,
                parts: keepOwn || ownParts.Length == 0 ? null : Enumerable.Range(0, parts.Length).Except(ownParts).ToArray());
            var result = new List<StageAssets> { assets };
            if (picked)
            {
                var problem = LoadPicked(instrument!, rig, roleRig, performer, layer, bones, lighting, surfaceShader, result);
                if (problem == null) note = Picked(instrument!);
                else (add, note) = (true, $"{problem}; instrument {member.Instrument} from the library");
            }
            if (add) note = LoadInstrument(Path.Combine(directory, role), body, performer, layer, bones, lighting, surfaceShader, result) ?? note;
            MelonLogger.Msg($"GH3 band {role}: character {character.Id} ({character.Name}, {character.Role} profile) with {note}" +
                (characterHair != null ? $"; hair of the {character.Role} profile ({characterHair.Joints.Length} joints)" : "") + extras.Summary);
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            assets?.Dispose();
            characterHair = null;
            extras?.Destroy();
            extras = null;
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    // A character on a character library profile: its mesh on its own rig, with the GLB inverse bind matrices.
    // Its own instrument plays in the role of its profile. In another role (GH3 co-op: a guitarist character plays the
    // bass with its own animation set), the role's library instrument hangs on the bones with the same names, bound
    // with the role rig's rest pose, so that it follows the character's instrument bones.
    private static List<StageAssets>? LoadOwnCharacter(Packages.CharacterModel character, CharacterMotion motion, string directory, BandMember member,
        GameObject performer, int layer, Transform[] bones, BandAnimation roleRig, bool lighting, Shader? surfaceShader, Packages.InstrumentModel? instrument,
        BandAnimation profileRig, out CharacterExtras? extras)
    {
        var role = member.Role;
        StageAssets? assets = null;
        extras = null;
        try
        {
            var profile = motion.Profile;
            var (map, bind) = CharacterExtras.MapJoints(character, profile.Bones.Length,
                joints => Packages.CharacterRig.Map(joints, profile.Bones, profile.Parents, profile.Bones, new HashSet<int>(), ownRole: true))
                ?? throw new InvalidDataException($"its joints are not all in profile {profile.Id}");
            extras = CharacterExtras.Create(character, map, bind, bones, layer);
            var all = bones.Concat(extras.Bones).ToArray();
            var bindposes = bind.Select(b => character.Bindposes[b]).Select(Unity).ToArray();
            var body = Packages.BandAttachments.BodyJoints(profile.Bones, profile.Parents);
            extras.AddToBody(body, profile.Bones.Length);
            var parts = Packages.BandAttachments.ReadParts(Path.Combine(character.Folder, "meshes.bin"));
            var ownParts = Packages.BandAttachments.AttachmentParts(parts, body, map);
            var ownRole = character.Role == role;
            var (add, keepOwn, note) = Packages.BandAttachments.Choose(ownParts.Length > 0, ownRole, character.Instrument, member.Instrument, role);
            var picked = instrument != null && CanHold(instrument, profile.Bones);
            if (picked) (add, keepOwn) = (false, false);
            assets = new StageAssets();
            assets.Load(character.Folder, performer, layer, map.Select(i => all[i]).ToArray(), lighting, surfaceShader: surfaceShader, bindposes: bindposes,
                parts: keepOwn || ownParts.Length == 0 ? null : Enumerable.Range(0, parts.Length).Except(ownParts).ToArray());
            var result = new List<StageAssets> { assets };
            if (picked)
            {
                // GH3 binds the instrument to the rest pose of the band member's own skeleton.
                var problem = LoadPicked(instrument!, profile.Bones, profileRig, performer, layer, bones, lighting, surfaceShader, result);
                if (problem == null) note = Picked(instrument!);
                else (add, note) = (true, $"{problem}; instrument {member.Instrument} from the library");
            }
            if (add)
            {
                var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
                var (roleBones, roleParents, _) = Packages.CharacterImporter.Rig(library, role);
                var byName = roleBones.Select(name => Array.IndexOf(profile.Bones, name)).ToArray();
                if (byName.Any(i => i < 0)) throw new InvalidDataException($"the {role} instrument bones are not in profile {profile.Id}");
                note = LoadInstrument(Path.Combine(directory, role), Packages.BandAttachments.BodyJoints(roleBones, roleParents), performer, layer,
                    byName.Select(i => bones[i]).ToArray(), lighting, surfaceShader, result, RestBindposes(roleRig)) ?? note;
            }
            MelonLogger.Msg($"GH3 band {role}: character {character.Id} ({character.Name}) on its GH3 profile {profile.Id} with {note}; " +
                $"animation set {motion.SetName}, scale {profile.Scale:0.##}, {(motion.Hair != null ? $"hair {motion.Hair.Joints.Length} joints" : "no hair physics")}, IK {profile.Ik}" +
                extras.Summary);
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            assets?.Dispose();
            extras?.Destroy();
            extras = null;
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    // A character with its own skeleton (PACKAGE-FORMAT.md section 9.6): its mesh on its own bones, the GH3 rig of the
    // performance without a mesh (the profile rig and animation set of a character library profile as a guitarist or
    // bassist, else the rig of the role), the retarget between them, the instrument of the role on the GH3 rig, its
    // chains on its own bones, and its face blend shapes. Null: the venue default plays.
    private static (List<StageAssets> Assets, StageRetarget Retarget, BandAnimation Rig, Transform[] Bones, HairDefinition? Hair)? LoadRetargeted(
        Packages.CharacterModel character, CharacterMotion? motion, string directory, BandMember member, GameObject performer, int layer,
        BandAnimation roleAnimation, bool lighting, Shader? surfaceShader, Packages.InstrumentModel? instrument, Func<BandAnimation, Transform[]> makeBones)
    {
        var role = member.Role;
        var result = new List<StageAssets>();
        try
        {
            var skeleton = character.Skeleton!;
            var source = motion != null ? BandAnimation.FromRig(motion.Profile.Parents, motion.Profile.Bind) : roleAnimation;
            var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
            var (roleBones, roleParents, _) = Packages.CharacterImporter.Rig(library, role);
            var sourceNames = motion != null ? motion.Profile.Bones : roleBones;
            var sourceBones = makeBones(source);
            var target = new RetargetSkeleton
            {
                Bones = skeleton.Names, Parents = skeleton.Parents,
                Bind = skeleton.Bind.Select(b => new BandPose(new(b[0], b[1], b[2]), System.Numerics.Quaternion.Normalize(new(b[3], b[4], b[5], b[6])))).ToArray(),
                Humanoid = skeleton.Humanoid, Followers = skeleton.Followers, InstrumentBone = skeleton.InstrumentBone,
                InstrumentOffset = new(skeleton.InstrumentOffset[0], skeleton.InstrumentOffset[1], skeleton.InstrumentOffset[2]),
                InstrumentScale = skeleton.InstrumentScale,
            };
            var rig = new CharacterRetarget(sourceNames, source.Parents, source.BindPose, target, role);
            var own = BandAnimation.FromRig(skeleton.Parents, skeleton.Bind);
            var bones = new Transform[skeleton.Names.Length];
            for (var i = 0; i < bones.Length; i++) bones[i] = new GameObject($"GH3 {role} {skeleton.Names[i]}") { layer = layer }.transform;
            for (var i = 0; i < bones.Length; i++)
            {
                bones[i].SetParent(skeleton.Parents[i] < 0 ? performer.transform : bones[skeleton.Parents[i]], false);
                Apply(bones[i], own.BindPose[i]);
            }
            var model = new StageAssets();
            result.Add(model);
            var morphs = Path.Combine(character.Folder, Packages.CharacterImporter.MorphFile);
            model.Load(character.Folder, performer, layer, bones, lighting, surfaceShader: surfaceShader, bindposes: character.Bindposes.Select(Unity).ToArray(),
                morphs: File.Exists(morphs) ? morphs : null);
            string note;
            var picked = instrument != null && CanHold(instrument, sourceNames);
            var problem = picked ? LoadPicked(instrument!, sourceNames, source, performer, layer, sourceBones, lighting, surfaceShader, result) : "";
            if (picked && problem == null) note = Picked(instrument!);
            else if (motion != null)
            {
                var byName = roleBones.Select(name => Array.IndexOf(sourceNames, name)).ToArray();
                if (byName.Any(i => i < 0)) throw new InvalidDataException($"the {role} instrument bones are not in profile {motion.Profile.Id}");
                note = LoadInstrument(Path.Combine(directory, role), Packages.BandAttachments.BodyJoints(roleBones, roleParents), performer, layer,
                    byName.Select(i => sourceBones[i]).ToArray(), lighting, surfaceShader, result, RestBindposes(roleAnimation)) ?? $"instrument {member.Instrument}";
            }
            else note = LoadInstrument(Path.Combine(directory, role), Packages.BandAttachments.BodyJoints(roleBones, roleParents), performer, layer, sourceBones,
                lighting, surfaceShader, result) ?? $"instrument {member.Instrument}";
            var state = new StageRetarget(rig, source, sourceBones, bones, performer.transform, layer);
            if (model.BlendShapes.Count > 0) state.Face = new CharacterFace(model.BlendShapes, sourceNames, source.BindPose, character.Face);
            HairDefinition? hair = null;
            if (character.ChainJoints.Length > 0)
            {
                var identity = Enumerable.Range(0, bones.Length).ToArray();
                var (_, _, joints, colliders) = CharacterChainRig.Plan(character, identity, identity);
                hair = HairDefinition.Merge(null, joints, colliders);
            }
            MelonLogger.Msg(FormattableString.Invariant($"GH3 band {role}: character {character.Id} ({character.Name}) on its own skeleton ({bones.Length} joints, ") +
                FormattableString.Invariant($"{skeleton.Humanoid.Count} humanoid roles), retargeted from {(motion != null ? $"profile {motion.Profile.Id}, animation set {motion.SetName}" : $"the {role} rig")}; ") +
                FormattableString.Invariant($"hip scale {rig.HipScale:0.###}, torso scale {rig.TorsoScale:0.###}, instrument scale {rig.InstrumentScale:0.###}; {note}; ") +
                (hair != null ? $"{hair.Joints.Length} chain joints, {hair.Colliders.Length} colliders" : "no chains") +
                (state.Face != null ? $"; face shapes {state.Face.Summary}" : "; no face shapes"));
            return (result, state, own, bones, hair);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            foreach (var part in result) part.Dispose();
            for (var i = performer.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    private static Matrix4x4 Unity(float[] m) => new()
    {
        m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7],
        m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11], m30 = m[12], m31 = m[13], m32 = m[14], m33 = m[15],
    };

    // Inverse bind matrices of a rig at its rest pose, in the space of the performer (row-major, as bindposes.bin).
    private static Matrix4x4[] RestBindposes(BandAnimation rig)
    {
        var world = new System.Numerics.Matrix4x4[rig.Parents.Length];
        var result = new Matrix4x4[world.Length];
        for (var i = 0; i < world.Length; i++)
        {
            var pose = rig.BindPose[i];
            // System.Numerics uses row vectors: local = rotation, then translation; world = local, then the parent's world.
            var local = System.Numerics.Matrix4x4.CreateFromQuaternion(pose.Rotation) * System.Numerics.Matrix4x4.CreateTranslation(pose.Position);
            world[i] = rig.Parents[i] < 0 ? local : local * world[rig.Parents[i]];
        }
        for (var i = 0; i < world.Length; i++)
        {
            if (!System.Numerics.Matrix4x4.Invert(world[i], out var inverse)) throw new InvalidDataException("Invalid rig rest pose");
            // Transposed to the column-vector layout of Unity.
            result[i] = Unity(new[]
            {
                inverse.M11, inverse.M21, inverse.M31, inverse.M41, inverse.M12, inverse.M22, inverse.M32, inverse.M42,
                inverse.M13, inverse.M23, inverse.M33, inverse.M43, inverse.M14, inverse.M24, inverse.M34, inverse.M44,
            });
        }
        return result;
    }

    // The attachment parts of the role's library band model, on the role's bones with the library bind pose. A failure
    // leaves the character without an instrument; returns the note for the log then.
    private static string? LoadInstrument(string folder, HashSet<int> body, GameObject performer, int layer, Transform[] bones, bool lighting,
        Shader? surfaceShader, List<StageAssets> result, Matrix4x4[]? bindposes = null)
    {
        var children = performer.transform.childCount;
        StageAssets? instrument = null;
        try
        {
            var parts = Packages.BandAttachments.AttachmentParts(Packages.BandAttachments.ReadParts(Path.Combine(folder, "meshes.bin")), body);
            if (parts.Length == 0) return "no instrument (the library band model has no attachment parts)";
            instrument = new StageAssets();
            instrument.Load(folder, performer, layer, bones, lighting, surfaceShader: surfaceShader, parts: parts, bindposes: bindposes);
            result.Add(instrument);
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            instrument?.Dispose();
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band: the library instrument could not load ({error.Message})");
            return "no instrument";
        }
    }

    // A library band member with the player's instrument: the member's body parts without its library instrument
    // (BandAttachments), then the chosen instrument on the role's bones. Null: the default member plays.
    private static List<StageAssets>? LoadMemberWithInstrument(string directory, BandMember member, GameObject performer, int layer, Transform[] bones,
        BandAnimation rig, bool lighting, Shader? surfaceShader, Packages.InstrumentModel instrument)
    {
        var children = performer.transform.childCount;
        var result = new List<StageAssets>();
        try
        {
            var library = Path.GetDirectoryName(Path.GetFullPath(directory))!;
            var (rigBones, parents, _) = Packages.CharacterImporter.Rig(library, member.Role);
            if (!CanHold(instrument, rigBones)) return null;
            var folder = Path.Combine(directory, member.Role);
            var parts = Packages.BandAttachments.ReadParts(Path.Combine(folder, "meshes.bin"));
            var own = Packages.BandAttachments.AttachmentParts(parts, Packages.BandAttachments.BodyJoints(rigBones, parents));
            var body = new StageAssets();
            result.Add(body);
            body.Load(folder, performer, layer, bones, lighting, surfaceShader: surfaceShader, parts: Enumerable.Range(0, parts.Length).Except(own).ToArray());
            var problem = LoadPicked(instrument, rigBones, rig, performer, layer, bones, lighting, surfaceShader, result);
            if (problem != null) throw new InvalidDataException(problem);
            MelonLogger.Msg($"GH3 band {member.Role}: the venue default with {Picked(instrument)}");
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            foreach (var part in result) part.Dispose();
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band {member.Role}: instrument {instrument.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    private static string Picked(Packages.InstrumentModel instrument) => $"instrument {instrument.Id} ({instrument.Name}; the player's choice)";

    // The band member's rig has every joint of the instrument (the GH3 guitarist and bassist rigs have the instrument bones).
    private static bool CanHold(Packages.InstrumentModel instrument, string[] rigBones)
    {
        if (instrument.Joints.All(joint => Array.IndexOf(rigBones, joint) >= 0)) return true;
        MelonLogger.Warning($"GH3 band: instrument {instrument.Id} has joints that the band member's rig does not have; it is not used");
        return false;
    }

    // The chosen instrument on the band member's bones (`bones` in the order of `rigBones`), bound to the rest pose of
    // that rig as GH3 binds an instrument to the skeleton of the band member. Returns a problem text, or null.
    private static string? LoadPicked(Packages.InstrumentModel instrument, string[] rigBones, BandAnimation rig, GameObject performer, int layer,
        Transform[] bones, bool lighting, Shader? surfaceShader, List<StageAssets> result)
    {
        var children = performer.transform.childCount;
        StageAssets? assets = null;
        try
        {
            var map = instrument.Joints.Select(joint => Array.IndexOf(rigBones, joint)).ToArray();
            var rest = RestBindposes(rig);
            assets = new StageAssets();
            assets.Load(instrument.Folder, performer, layer, map.Select(i => bones[i]).ToArray(), lighting, surfaceShader: surfaceShader,
                bindposes: map.Select(i => rest[i]).ToArray());
            result.Add(assets);
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            assets?.Dispose();
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            MelonLogger.Warning($"GH3 band: instrument {instrument.Id} could not load ({error.Message})");
            return $"instrument {instrument.Id} did not load";
        }
    }
}
