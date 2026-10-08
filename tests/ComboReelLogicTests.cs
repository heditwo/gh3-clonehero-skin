using Gh3Skin;

internal static class ComboReelLogicTests
{
    public static void Run()
    {
        Check(Near(Gh3Morphs.Ease(Gh3Motion.EaseIn, 0.5f), 0.25f), "ease_in is t squared");
        Check(Near(Gh3Morphs.Ease(Gh3Motion.EaseOut, 0.5f), 0.75f), "ease_out is 2t - t squared");
        Check(Near(Gh3Morphs.Ease(Gh3Motion.Smooth, 0.5f), 0.5f) && Near(Gh3Morphs.Ease(Gh3Motion.Smooth, 0.25f), 0.15625f),
            "smooth is 3t^2 - 2t^3");
        var chain = new[] { new Gh3Morph(1, 10, Gh3Motion.Linear), new Gh3Morph(1, 0, Gh3Motion.EaseIn) };
        Check(Near(Gh3Morphs.Sample(0, chain, 0.5f), 5) && Near(Gh3Morphs.Sample(0, chain, 1.5f), 7.5f) &&
            Near(Gh3Morphs.Sample(0, chain, 3), 0), "morph chains start from the previous end value");

        foreach (var (combo, digits) in new[] { (0, 3), (25, 3), (999, 3), (1000, 4), (9999, 4), (10000, 5), (100000, 6), (99999999, 7) })
            Check(ComboReelLayout.DigitCount(combo) == digits, $"{combo} has {digits} counter digits");

        // GH3's own layout is unchanged up to four digits.
        for (var digits = 3; digits <= 4; digits++)
        {
            Check(ComboReelLayout.Width(digits) == 0, "GH3 counters do not open");
            for (var place = 0; place < digits; place++)
                Check(ComboReelLayout.DigitX(place, digits, 0) == 222 - 37 * (place + 1), "GH3 digit position");
        }

        var leftLimit = ComboReelLayout.LeftLimit(AmpLampRight - ComboOffsetX);
        for (var digits = 5; digits <= ComboReelLayout.MaxDigits; digits++)
        {
            var width = ComboReelLayout.Width(digits);
            // Every digit sits on its own reel, one pitch apart, with the first reel at the left.
            var reels = new List<float> { ComboReelLayout.DrumX + 54 + 18.5f - width };
            for (var k = 0; k < ComboReelLayout.ExtraReels(digits); k++)
            {
                var (x, visible) = ComboReelLayout.Reel(k, width);
                Check(visible == ComboReelLayout.Pitch, "settled reels are whole");
                reels.Add(x + visible / 2);
            }
            for (var place = 3; place < digits; place++)
            {
                var x = ComboReelLayout.DigitX(place, digits, width);
                Check(reels.Any(center => Math.Abs(center - x) <= 3), $"digit {place} of {digits} is on a reel");
                Check(Near(ComboReelLayout.DigitX(place - 1, digits, width) - x, ComboReelLayout.Pitch), "digit pitch");
            }
            // Inserted reels fill the gap between the first reel and the split without overlap.
            var covered = Enumerable.Range(0, ComboReelLayout.ExtraReels(digits)).Sum(k => ComboReelLayout.Reel(k, width).Width);
            Check(Near(covered, width), "inserted reels cover the opening");
        }
        for (var width = 0f; width <= ComboReelLayout.Width(ComboReelLayout.MaxDigits) + 12; width += 0.5f)
        {
            var reels = Enumerable.Range(0, ComboReelLayout.MaxDigits - 3).Select(k => ComboReelLayout.Reel(k, width)).ToArray();
            Check(Near(reels.Sum(reel => reel.Width), Math.Min(width, ComboReelLayout.Pitch * reels.Length)), "partial opening is covered");
            for (var k = 0; k < reels.Length; k++)
                if (reels[k].Width > 0) Check(Near(reels[k].X + reels[k].Width, ComboReelLayout.Split - ComboReelLayout.Pitch * k), "reels are contiguous");
            // The body never reaches the score lamps, and stays at full size while it has room.
            var left = ComboReelLayout.LeftEdge(width, leftLimit);
            var lampRight = ComboOffsetX + left * 1.05f;
            Check(lampRight >= AmpLampRight, $"counter covers the lamps when open by {width}");
            if (width <= ComboReelLayout.Width(5)) Check(ComboReelLayout.FitScale(width, leftLimit) == 1, "five digits fit at full size");
        }
        Check(ComboReelLayout.FitScale(ComboReelLayout.Width(6), leftLimit) is > 0.85f and < 1, "six digits shrink slightly");

        var reel = new ComboReelAnimation();
        reel.Reset(0);
        reel.Open(37, 10);
        Check(Near(reel.Width(10), 0) && Near(reel.Width(10.3f), 49) && Near(reel.Width(20), 37), "opening overshoots and settles");
        Check(reel.Pop(10.05f).Scale == 0 && reel.Pop(10.3f).Scale > 1.3f && Near(reel.Pop(20).Scale, 1), "new digit pops in");
        Check(reel.Flash(10).Alpha > 0 && reel.Flash(11).Alpha == 0, "glowburst flash");
        Check(Near(reel.Squash(20), 1), "squash settles");
        reel.Close(30);
        Check(reel.Closing && !reel.Closed(30.3f) && reel.Closed(31) && Near(reel.Width(30.5f), 0), "closing collapses");
        Check(reel.DigitAlpha(30.2f) == 0 && reel.Pop(30.2f) == (1, 0), "closing hides digits without a pop");

        var force = new ComboOverride(9990, 10005);
        Check(force.Apply(0) == 0 && force.Apply(1) == 9991 && force.Apply(14) == 10004, "forced streak offset");
        Check(force.Apply(15) == 0 && force.Apply(16) == 1 && force.Apply(40) == 25, "forced break restarts the streak");
        Check(force.Apply(0) == 0 && force.Apply(3) == 3, "a real miss after the forced break");
        Check(new ComboOverride(0, 0).Apply(42) == 42, "no override");
    }

    // AmpHud.FrameLeftEdge and ScorePanelHud's counter_pos - score_pos offset, in canvas units.
    private const float AmpLampRight = 36;
    private const float ComboOffsetX = 30 * 1.05f;

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Combo reel: {name}");
    }
}
