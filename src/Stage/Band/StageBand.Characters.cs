using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Character packages on the band (PACKAGING-PLAN decision 15). Every character is a skeleton, a humanoid map, and the
// animation set that it plays; the GH3 rig of the set (the clip rig) drives the skeleton through the humanoid map. When
// the skeleton is that rig (a GH3 character in its own role, or a guitarist profile as the bassist as in GH3 co-op), the
// set plays directly on the character's bones (no retarget). Also the player's instruments on the band members.
internal sealed partial class StageBand
{
    private sealed record Loaded(Performer Player, List<StageAssets> Assets, HairDefinition? Hair, int[] Parents, int ChainJoints);

    // The set of the band library member of a role. `names`: read the rig bind rows (a character needs them).
    private static CharacterMotion LibraryMotion(string library, string role, BandAnimation rig, HairBank? hair, bool names)
    {
        var roleHair = hair != null && hair.Members.TryGetValue(role, out var found) ? found : null;
        var rigs = Packages.LibraryRigs.Load(library);
        if (!names) return CharacterMotion.Library(role, rig, roleHair, rigs);
        var (_, _, bind) = Packages.CharacterImporter.Rig(library, role);
        return CharacterMotion.Library(role, rig, roleHair, rigs, bind);
    }

    // The set that the character plays in the roles of its profile: the set of its character library profile, or the
    // band library member of its fixed profile.
    private static CharacterMotion OwnMotion(Packages.CharacterModel character, CharacterMotion roleMotion, string directory, string library, HairBank? hair)
    {
        if (character.Library.Length > 0) return CharacterMotion.Create(character);
        if (character.Role == roleMotion.LibraryRole) return roleMotion;
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(directory, character.Role, "animation.bin")));
        return LibraryMotion(library, character.Role, new BandAnimation(reader), hair, names: true);
    }

    // The venue default: the band library member, with the player's instrument when there is one.
    private static Loaded LoadMember(CharacterMotion motion, string directory, string library, BandMember member, GameObject performer, int layer, bool lighting,
        Shader? surfaceShader, Packages.InstrumentModel? instrument, Func<BandAnimation, Transform[]> makeBones)
    {
        var bones = makeBones(motion.Rig);
        var assets = instrument != null ? LoadMemberWithInstrument(directory, library, member, performer, layer, bones, motion.Rig, lighting, surfaceShader, instrument) : null;
        if (assets == null)
        {
            var model = new StageAssets();
            assets = new List<StageAssets> { model };
            model.Load(Path.Combine(directory, member.Role), performer, layer, bones, lighting, surfaceShader: surfaceShader);
        }
        return new Loaded(new Performer { Bones = bones, Animation = motion.Rig, Motion = motion }, assets, motion.Hair, motion.Rig.Parents, 0);
    }

    // A character package in a band role: the set of its profile when the profile plays the role (GH3 co-op: a guitarist
    // profile also plays the bassist), else the set of the role's band library member. Null: the venue default plays.
    private static Loaded? LoadCharacter(Packages.CharacterModel character, CharacterMotion own, CharacterMotion roleMotion, string directory, string library,
        BandMember member, GameObject performer, int layer, bool lighting, Shader? surfaceShader, Packages.InstrumentModel? instrument,
        Func<BandAnimation, Transform[]> makeBones)
    {
        var role = member.Role;
        var driver = Packages.CharacterLibrary.UsesOwnSet(own.Role, role) ? own : roleMotion;
        var children = performer.transform.childCount;
        var result = new List<StageAssets>();
        CharacterExtras? extras = null;
        try
        {
            return character.Skeleton == null && driver == own
                ? LoadOnRig(character, own, roleMotion.Rig, directory, library, member, performer, layer, lighting, surfaceShader, instrument, makeBones, result, out extras)
                : LoadRetargeted(character, own, driver, roleMotion.Rig, directory, library, member, performer, layer, lighting, surfaceShader, instrument, makeBones,
                    result, out extras);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Packages.PackageException or UnauthorizedAccessException or InvalidOperationException)
        {
            foreach (var part in result) part.Dispose();
            extras?.Destroy();
            for (var i = performer.transform.childCount - 1; i >= children; i--)
                UnityEngine.Object.Destroy(performer.transform.GetChild(i).gameObject);
            performer.transform.localScale = Vector3.one;
            MelonLogger.Warning($"GH3 band {role}: character {character.Id} could not load ({error.Message}); the venue default plays");
            return null;
        }
    }

    // The set plays on the character's own GH3 rig: its joints are the rig bones (by name) and the added bones, and its
    // GLB inverse bind matrices skin the mesh. Its own instrument plays in the role of its profile; else the role's
    // library instrument hangs on the bones with the same names (BandAttachments).
    private static Loaded LoadOnRig(Packages.CharacterModel character, CharacterMotion motion, BandAnimation roleRig, string directory, string library,
        BandMember member, GameObject performer, int layer, bool lighting, Shader? surfaceShader, Packages.InstrumentModel? instrument,
        Func<BandAnimation, Transform[]> makeBones, List<StageAssets> result, out CharacterExtras extras)
    {
        var role = member.Role;
        var profile = motion.Profile;
        // GH3 create_guitarist: the profile `scale` scales the whole band member (Judy 0.9).
        if (!motion.IsLibrary) performer.transform.localScale = new Vector3(profile.Scale, profile.Scale, profile.Scale);
        var bones = makeBones(motion.Rig);
        var (map, bind) = CharacterExtras.MapJoints(character, profile.Bones.Length, joints => CharacterChainRig.ByName(joints, profile.Bones))
            ?? throw new InvalidDataException($"its joints are not all in the {profile.Role} rig");
        extras = CharacterExtras.Create(character, map, bind, bones, layer);
        var all = bones.Concat(extras.Bones).ToArray();
        var body = Packages.BandAttachments.BodyJoints(profile.Bones, profile.Parents);
        extras.AddToBody(body, profile.Bones.Length);
        var parts = Packages.BandAttachments.ReadParts(Path.Combine(character.Folder, "meshes.bin"));
        var ownParts = Packages.BandAttachments.AttachmentParts(parts, body, map);
        var (add, keepOwn, note) = Packages.BandAttachments.Choose(ownParts.Length > 0, character.Role == role, character.Instrument, member.Instrument, role);
        var picked = instrument != null && CanHold(instrument, profile.Bones);
        if (picked) (add, keepOwn) = (false, false);
        var model = new StageAssets();
        result.Add(model);
        model.Load(character.Folder, performer, layer, map.Select(i => all[i]).ToArray(), lighting, surfaceShader: surfaceShader,
            bindposes: bind.Select(b => Unity(character.Bindposes[b])).ToArray(),
            parts: keepOwn || ownParts.Length == 0 ? null : Enumerable.Range(0, parts.Length).Except(ownParts).ToArray());
        if (picked)
        {
            // GH3 binds the instrument to the rest pose of the band member's own skeleton.
            var problem = LoadPicked(instrument!, profile.Bones, motion.Rig, performer, layer, bones, lighting, surfaceShader, result);
            if (problem == null) note = Picked(instrument!);
            else (add, note) = (true, $"{problem}; instrument {member.Instrument} from the library");
        }
        if (add) note = LoadRoleInstrument(directory, library, role, roleRig, motion, bones, performer, layer, lighting, surfaceShader, result) ?? note;
        var hair = HairDefinition.Merge(motion.Hair, extras.Joints, extras.Colliders);
        MelonLogger.Msg($"GH3 band {role}: character {character.Id} ({character.Name}) on its GH3 rig {ProfileLabel(motion)} with {note}; " +
            $"animation set {(motion.IsLibrary ? $"of the {motion.LibraryRole}" : motion.SetName)}, scale {profile.Scale:0.##}, " +
            $"{(motion.Hair != null ? $"hair {motion.Hair.Joints.Length} joints" : "no hair physics")}, IK {(profile.Ik.Length > 0 ? profile.Ik : "hero")}" + extras.Summary);
        return new Loaded(new Performer { Bones = all, Animation = motion.Rig, Motion = motion, ExtraBind = extras.Bind }, result, hair,
            motion.Rig.Parents.Concat(extras.Parents).ToArray(), extras.Joints.Length);
    }

    // The set plays on its GH3 rig without a mesh, and the retarget moves the character's skeleton (PACKAGE-FORMAT.md
    // section 9.6): its own skeleton (character.json skeleton), or its GH3 rig with the humanoid map of the GH3 bone
    // names, its face bones by name, the GH3 twist constraints, and its profile hair. The instrument of the role is on the
    // GH3 rig that plays; the character's own instrument does not load.
    private static Loaded LoadRetargeted(Packages.CharacterModel character, CharacterMotion own, CharacterMotion driver, BandAnimation roleRig, string directory,
        string library, BandMember member, GameObject performer, int layer, bool lighting, Shader? surfaceShader, Packages.InstrumentModel? instrument,
        Func<BandAnimation, Transform[]> makeBones, List<StageAssets> result, out CharacterExtras? extras)
    {
        var role = member.Role;
        extras = null;
        var source = driver.Rig;
        var sourceNames = driver.Profile.Bones;
        var sourceBones = makeBones(source);
        RetargetSkeleton target;
        Transform[] bones;
        HairDefinition? hair;
        StageGuitarRig? constraints = null;
        var model = new StageAssets();
        if (character.Skeleton is { } skeleton)
        {
            target = new RetargetSkeleton
            {
                Bones = skeleton.Names, Parents = skeleton.Parents,
                Bind = skeleton.Bind.Select(b => new BandPose(new(b[0], b[1], b[2]), System.Numerics.Quaternion.Normalize(new(b[3], b[4], b[5], b[6])))).ToArray(),
                Humanoid = skeleton.Humanoid, Followers = skeleton.Followers, InstrumentBone = skeleton.InstrumentBone,
                InstrumentOffset = new(skeleton.InstrumentOffset[0], skeleton.InstrumentOffset[1], skeleton.InstrumentOffset[2]),
                InstrumentScale = skeleton.InstrumentScale,
            };
            bones = MakeSkeleton(target, performer.transform, role, layer);
            result.Add(model);
            var morphs = Path.Combine(character.Folder, Packages.CharacterImporter.MorphFile);
            model.Load(character.Folder, performer, layer, bones, lighting, surfaceShader: surfaceShader, bindposes: character.Bindposes.Select(Unity).ToArray(),
                morphs: File.Exists(morphs) ? morphs : null);
            hair = null;
            if (character.ChainJoints.Length > 0)
            {
                var identity = Enumerable.Range(0, bones.Length).ToArray();
                var (_, _, joints, colliders) = CharacterChainRig.Plan(character, identity, identity);
                hair = HairDefinition.Merge(null, joints, colliders);
            }
        }
        else
        {
            var profile = own.Profile;
            // The profile scale (create_guitarist) scales the skeleton, not the GH3 rig that plays or its instrument.
            var holder = performer.transform;
            if (profile.Scale != 1)
            {
                holder = new GameObject($"GH3 {role} Skeleton") { layer = layer }.transform;
                holder.SetParent(performer.transform, false);
                holder.localScale = new Vector3(profile.Scale, profile.Scale, profile.Scale);
            }
            var rig = MakeSkeleton(new RetargetSkeleton { Bones = profile.Bones, Parents = own.Rig.Parents, Bind = own.Rig.BindPose }, holder, role, layer);
            var (map, bind) = CharacterExtras.MapJoints(character, profile.Bones.Length, joints => CharacterChainRig.ByName(joints, profile.Bones))
                ?? throw new InvalidDataException($"its joints are not all in the {own.Profile.Role} rig");
            extras = CharacterExtras.Create(character, map, bind, rig, layer);
            bones = rig.Concat(extras.Bones).ToArray();
            var names = profile.Bones.Concat(character.AddedBones.Select(b => b.Name)).ToArray();
            var parents = own.Rig.Parents.Concat(extras.Parents).ToArray();
            var rest = own.Rig.BindPose.Concat(extras.Bind).ToArray();
            target = new RetargetSkeleton
            {
                Bones = names, Parents = parents, Bind = rest, Humanoid = Packages.HumanoidRoles.FromGh3(profile.Bones), Scale = profile.Scale, Gh3Face = true,
            };
            constraints = new StageGuitarRig(parents, rest, names, System.Array.Empty<Packages.IkChainDefinition>(), own.Constraints);
            var body = Packages.BandAttachments.BodyJoints(profile.Bones, profile.Parents);
            extras.AddToBody(body, profile.Bones.Length);
            var parts = Packages.BandAttachments.ReadParts(Path.Combine(character.Folder, "meshes.bin"));
            var ownParts = Packages.BandAttachments.AttachmentParts(parts, body, map);
            result.Add(model);
            model.Load(character.Folder, performer, layer, map.Select(i => bones[i]).ToArray(), lighting, surfaceShader: surfaceShader,
                bindposes: bind.Select(b => Unity(character.Bindposes[b])).ToArray(),
                parts: ownParts.Length == 0 ? null : Enumerable.Range(0, parts.Length).Except(ownParts).ToArray());
            hair = HairDefinition.Merge(own.Hair, extras.Joints, extras.Colliders);
        }
        var retarget = new CharacterRetarget(sourceNames, source.Parents, source.BindPose, driver.Points, target, role);
        string note;
        var picked = instrument != null && CanHold(instrument, sourceNames);
        var problem = picked ? LoadPicked(instrument!, sourceNames, source, performer, layer, sourceBones, lighting, surfaceShader, result) : "";
        if (picked && problem == null) note = Picked(instrument!);
        else note = LoadRoleInstrument(directory, library, role, roleRig, driver, sourceBones, performer, layer, lighting, surfaceShader, result) ?? $"instrument {member.Instrument}";
        var state = new StageRetarget(retarget, source, sourceBones, bones, performer.transform, layer) { Constraints = constraints };
        if (model.BlendShapes.Count > 0) state.Face = new CharacterFace(model.BlendShapes, sourceNames, source.BindPose, character.Face);
        MelonLogger.Msg(FormattableString.Invariant($"GH3 band {role}: character {character.Id} ({character.Name}) on ") +
            (character.Skeleton != null ? "its own skeleton" : $"its GH3 rig {ProfileLabel(own)}") +
            FormattableString.Invariant($" ({bones.Length} joints, {target.Humanoid.Count} humanoid roles), retargeted from ") +
            (driver.IsLibrary ? $"the {driver.LibraryRole} rig" : $"profile {driver.Profile.Id}, animation set {driver.SetName}") +
            FormattableString.Invariant($"; hip scale {retarget.HipScale:0.###}, torso scale {retarget.TorsoScale:0.###}, instrument scale {retarget.InstrumentScale:0.###}") +
            (retarget.HasMicrophone ? FormattableString.Invariant($", stand scale {retarget.StandScale:0.###}") : "") +
            (retarget.HasKit ? FormattableString.Invariant($", seat lift {retarget.SeatLift * 100:0.#} cm") : "") + $"; {note}; " +
            (hair != null ? $"{hair.Joints.Length} hair and chain joints, {hair.Colliders.Length} colliders" : "no chains") +
            (state.Face != null ? $"; face shapes {state.Face.Summary}" : target.Gh3Face ? "; GH3 face bones" : "; no face shapes"));
        return new Loaded(new Performer { Bones = bones, Animation = source, Motion = driver, Retarget = state }, result, hair, target.Parents,
            extras?.Joints.Length ?? character.ChainJoints.Length);
    }

    private static string ProfileLabel(CharacterMotion motion) => motion.IsLibrary ? $"of the {motion.LibraryRole}" : motion.Profile.Id;

    // The bones of a skeleton at its rest pose, under `root`.
    private static Transform[] MakeSkeleton(RetargetSkeleton skeleton, Transform root, string role, int layer)
    {
        var bones = new Transform[skeleton.Parents.Length];
        for (var i = 0; i < bones.Length; i++) bones[i] = new GameObject($"GH3 {role} {skeleton.Bones[i]}") { layer = layer }.transform;
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i].SetParent(skeleton.Parents[i] < 0 ? root : bones[skeleton.Parents[i]], false);
            Apply(bones[i], skeleton.Bind[i]);
        }
        return bones;
    }

    // The library instrument of the role on the rig that plays: the role's own rig with the library bind pose, or the
    // bones with the same names of another GH3 rig, bound with the role rig's rest pose.
    private static string? LoadRoleInstrument(string directory, string library, string role, BandAnimation roleRig, CharacterMotion motion, Transform[] bones,
        GameObject performer, int layer, bool lighting, Shader? surfaceShader, List<StageAssets> result)
    {
        var (roleBones, roleParents, _) = Packages.CharacterImporter.Rig(library, role);
        var body = Packages.BandAttachments.BodyJoints(roleBones, roleParents);
        if (motion.LibraryRole == role) return LoadInstrument(Path.Combine(directory, role), body, performer, layer, bones, lighting, surfaceShader, result);
        var byName = roleBones.Select(name => Array.IndexOf(motion.Profile.Bones, name)).ToArray();
        if (byName.Any(i => i < 0)) throw new InvalidDataException($"the {role} instrument bones are not in the rig {ProfileLabel(motion)}");
        return LoadInstrument(Path.Combine(directory, role), body, performer, layer, byName.Select(i => bones[i]).ToArray(), lighting, surfaceShader, result,
            RestBindposes(roleRig));
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
    private static List<StageAssets>? LoadMemberWithInstrument(string directory, string library, BandMember member, GameObject performer, int layer,
        Transform[] bones, BandAnimation rig, bool lighting, Shader? surfaceShader, Packages.InstrumentModel instrument)
    {
        var children = performer.transform.childCount;
        var result = new List<StageAssets>();
        try
        {
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
