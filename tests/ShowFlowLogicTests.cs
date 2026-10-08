using System.Numerics;
using Gh3Skin;

internal static class ShowFlowLogicTests
{
    public static void Run()
    {
        var source = new StageTimeline(new[] { new StageCue(0, "gh3-camera", "85", 12), new StageCue(30, "gh3-camera", "84") }, "test");
        var win = StageShowFlow.WinTime(20);
        Check(Math.Abs(win - 23.1) < 1e-9, "song_won follows the last note by GH3's PC win delays");
        var timeline = StageShowFlow.Apply(source, "gh3.dive", win);
        Check(timeline.Start == -StageShowFlow.IntroLength, "the intro transition starts 8 s before the song");
        Check(!timeline.Cameras.Any(cue => cue.Time >= win && cue.Value != "win"), "note cameras do not cut away from the win camera");
        Check(timeline.Track("stance", "guitarist").First().Value == "intro_smstg", "small venues use the small-stage intro stance");
        Check(timeline.Track("stance", "drummer").First().Value == "intro", "the drummer always uses the intro stance");
        Check(timeline.Track("stance", "guitarist").Last().Value == "win_smstg" && timeline.Track("stance", "singer").Last().Value == "win",
            "play_win_anims uses the small-stage win stance only for the guitarists");
        Check(timeline.PlaybackCues.Count(cue => cue.Type == "gh3-audio") == 7, "intro and win sounds are scheduled");
        Check(!timeline.Cameras.Any(cue => cue.Value == "default"), "a note camera at the end of the intro takes over from it");
        var plain = StageShowFlow.Apply(new StageTimeline(Array.Empty<StageCue>(), "test"), "gh3.budokan", null);
        Check(plain.Track("stance", "bassist").First().Value == "intro", "large venues use the full intro stance");
        Check(plain.Cameras.Any(cue => cue.Value == "default" && cue.Time == 0), "without a note camera the default cameras return at song start");

        var intro = new OriginalCameraShot { Name = "intro", Control = "TwoCam", Points = new[] { Point(-5), Point(5) } };
        var normal = new OriginalCameraShot { Name = "normal", Control = "TwoCam", Points = new[] { Point(20) } };
        var bank = new StageCameraBank { Version = 1, Arrays = new() { ["cameras"] = new[] { normal }, ["cameras_intro"] = new[] { intro },
            ["cameras_win"] = new[] { intro } }, Notes = new() };
        var path = new StageCameraPath(new(Vector3.Zero, Vector3.UnitZ, 55, "fixed"), new Dictionary<string, Vector3>(), bank: bank);
        Check(path.Sample(plain, -4).Shot == "intro" && Math.Abs(path.Sample(plain, -4).Position.X) < 1e-4, "the intro camera morphs over the 8 s transition");
        Check(path.Sample(plain, 0.5).Shot == "normal", "the default cameras follow the intro");

        var member = new PerformanceMember { Bones = new[] { "00000000" }, Clips = new() {
            ["00000001"] = new() { Duration = 2, Frames = 2 }, ["00000002"] = new() { Duration = 3, Frames = 2 }
        }, Groups = new() { ["stance_a/idle/normal/med"] = new[] { "00000001" }, ["intro/idle/normal/med"] = new[] { "00000002" } } };
        var player = new PerformancePlayer("bassist", "", member, new[] { new BandPose(Vector3.Zero, Quaternion.Identity) }, plain);
        Check(player.At(-7).Stance == "intro" && player.At(-7).Clip == "00000002", "the band plays its intro animation before the song");
        Check(player.At(-4.5).Stance == "stance_a", "Stance_A follows the end of the intro animation");
        member.Groups["stance_a/transitions/win"] = new[] { "00000001" };
        member.Groups["win/idle/normal/med"] = new[] { "00000002" };
        var won = new PerformancePlayer("bassist", "", member, new[] { new BandPose(Vector3.Zero, Quaternion.Identity) },
            StageShowFlow.Apply(new StageTimeline(Array.Empty<StageCue>(), "test"), "gh3.budokan", 10));
        Check(won.At(10.1).Stance == "win" && won.At(10.1).Clip == "00000002", "the win animation starts without a stance transition");

        var animation = new YouRockAnimation(174);
        Check(Math.Abs(animation.BaseScale - 207f / 174) < 1e-5, "split_text_into_array_elements fits the text to 23 units per letter");
        var letters = animation.Sample(0.01).ToArray();
        Check(letters.Length == 18 && letters[0].Shadow && !letters[1].Shadow, "each letter has a shadow drawn below it");
        Check(Math.Abs((letters[^1].Position.X + letters[1].Position.X) / 2 - 640) < 11, "the text is centred on the screen");
        var rest = animation.Sample(0.08).ToArray();
        Check(rest[1].Scale == animation.BaseScale && rest[1].Position.Y == 360, "the letters return to rest between jiggles");
        var gone = animation.Sample(YouRockAnimation.JiggleTime + YouRockAnimation.ExplodeTime - 0.001).ToArray();
        Check(gone.All(letter => letter.Position.X is < 0 or > 1280 || letter.Position.Y is < 0 or > 720), "the letters explode off the screen");
        Check(!animation.Visible(YouRockAnimation.JiggleTime + YouRockAnimation.ExplodeTime), "the text is removed after the explosion");

        Check(IntroHudAnimation.HighwayOffset(-3) == 720 && IntroHudAnimation.HighwayOffset(0) == 0, "the highway rises from below the screen before the song");
        var rises = Enumerable.Range(0, 120).Select(i => IntroHudAnimation.HighwayOffset(-2 + i / 60.0)).ToArray();
        Check(rises.Zip(rises.Skip(1), (a, b) => b <= a).All(x => x), "the highway rise never moves back down");
        Check(IntroHudAnimation.HudOffsets(-1).Rock.X > 700 && IntroHudAnimation.HudOffsets(-1).Score.X <= -800, "the HUD waits off screen");
        Check(IntroHudAnimation.HudOffsets(0.5) == (Vector2.Zero, 0, Vector2.Zero), "the HUD settles at its GH3 position");
    }

    private static OriginalCameraPoint Point(float x) =>
        new() { Position = new[] { x, 2, -10 }, Rotation = new float[] { 0, 0, 0, 1 }, FieldOfView = 72 };

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
}
