using System.Globalization;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Capture aids: fixed debug views of the stage camera (GH3_SKIN_DEBUG_VIEW and its head views) and the direction trace.
internal sealed partial class GameplayStage
{
    private const int TraceCameraCuts = 8;
    // Capture aid: GH3_SKIN_DEBUG_VIEW="x,y,z,targetX,targetY,targetZ,fov[;...]" fixes the stage camera (cycling through the
    // views every GH3_SKIN_DEBUG_VIEW_SECONDS of song time) and hides the highway; GH3_SKIN_DEBUG_SHOW="id,id" keeps those GH3 objects created.
    private static readonly float[][] DebugViews = (SkinEnvironment.Get(SkinEnvironment.DebugView) ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(view => view.Split(',').Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
    private static readonly float DebugViewSeconds = float.TryParse(SkinEnvironment.Get(SkinEnvironment.DebugViewSeconds),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 4;
    private static readonly string[] DebugShow = (SkinEnvironment.Get(SkinEnvironment.DebugShow) ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries);

    private void ApplyDebugView(Camera camera, StageAssets assets)
    {
        foreach (var id in DebugShow)
            if (assets.Objects.TryGetValue(id, out var item)) item.gameObject.SetActive(true);
        if (DebugViews.Length == 0) return;
        var index = (int)Math.Floor(Math.Max(0, _lastTime + StageShowFlow.IntroLength) / DebugViewSeconds) % DebugViews.Length;
        if (DebugViews[index] is not { Length: 7 } view) return;
        camera.transform.localPosition = new Vector3(view[0], view[1], view[2]);
        camera.transform.LookAt(_root!.transform.position + new Vector3(view[3], view[4], view[5]));
        var headMode = SkinEnvironment.Get(SkinEnvironment.DebugViewHead);
        // GH3_SKIN_DEBUG_VIEW_ROLE: the band member of the head views (default the guitarist).
        var headRole = SkinEnvironment.Get(SkinEnvironment.DebugViewRole) ?? "guitarist";
        if (headMode == "1" && _band?.Anchor(headRole, Packages.HumanoidRoles.Gh3["head"]) is { } head)
            camera.transform.LookAt(new Vector3(head.Position.X, head.Position.Y, head.Position.Z));
        if (headMode == "local" && _band?.Anchor(headRole, Packages.HumanoidRoles.Gh3["head"]) is { } face)
        {
            // Position and target in the frame of the head bone (on the Midori rig, local +X points up and +Y to the front of
            // the face), so the camera keeps the same view of the face while the head moves.
            var eye = face.Position + System.Numerics.Vector3.Transform(new(view[0], view[1], view[2]), face.Rotation);
            var target = face.Position + System.Numerics.Vector3.Transform(new(view[3], view[4], view[5]), face.Rotation);
            var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, face.Rotation);
            camera.transform.position = new Vector3(eye.X, eye.Y, eye.Z);
            camera.transform.LookAt(new Vector3(target.X, target.Y, target.Z), new Vector3(up.X, up.Y, up.Z));
        }
        if (headMode == "through" && _band?.Anchor("guitarist", Packages.HumanoidRoles.Gh3["head"]) is { } front && _band.Anchor("bassist", Packages.HumanoidRoles.Gh3["head"]) is { } back)
        {
            // The bassist's face behind the guitarist's head: the camera is view[0] metres past the guitarist's head on the
            // line from the bassist's head, raised by view[1] metres and moved view[2] metres to the side (checks of draw
            // order between band members).
            var away = System.Numerics.Vector3.Normalize(front.Position - back.Position);
            var side = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(System.Numerics.Vector3.UnitY, away));
            var eye = front.Position + away * view[0] + System.Numerics.Vector3.UnitY * view[1] + side * view[2];
            camera.transform.position = new Vector3(eye.X, eye.Y, eye.Z);
            // Aim a fifth of the height offset above the bassist's head.
            camera.transform.LookAt(new Vector3(back.Position.X, back.Position.Y + view[1] * ThroughViewAim, back.Position.Z));
        }
        camera.fieldOfView = view[6];
        foreach (var (other, _) in _cameras) other.cullingMask = 0;
        if (_playerCamera != null) _playerCamera.cullingMask = 0;
    }

    // The cue count of each direction channel and the first camera cuts, to compare two direction sources of one song.
    private void TraceDirection()
    {
        var channels = _timeline!.Cues.GroupBy(cue => StageSongDirection.Channel(cue) ?? cue.Type).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");
        var cameras = _timeline.Cameras.Take(TraceCameraCuts).Select(cue => $"{cue.Time.ToString("0.000", CultureInfo.InvariantCulture)}:{cue.Value}");
        MelonLogger.Msg($"[trace] direction {string.Join(" ", channels)} cameras {string.Join(",", cameras)}");
    }
}
