using Gh3Skin;

// The centre messages: the ready banner trigger and GH3's rule of one centre message at a time.
internal static class CenterMessageTests
{
    private const float Streak = 2.45f;
    private const float Ready = 2.1f;

    public static void Run()
    {
        ReadyTrigger();
        ReadyWaitsForStreak();
        StreakWaitsForReady();
        SameFrame();
        DroppedMessages();
        ActivationWhileWaiting();
        SingleMessages();
    }

    private static void ReadyTrigger()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        Check(queue.ReadyFrame(0) == -1, "inactive before threshold");
        queue.Update(0, 0.49f, false, 10);
        Check(queue.ReadyFrame(10) == -1, "inactive below threshold");
        queue.Update(0, 0.5f, false, 11);
        Check(queue.ReadyFrame(11) == 0, "starts on threshold crossing");
        queue.Update(0, 1, false, 12);
        Check(queue.ReadyFrame(12) == 60, "does not restart while ready");
        Check(queue.ReadyFrame(13.1f) == -1, "ends while power remains ready");
        queue.Update(0, 0.25f, false, 14);
        Check(queue.ReadyFrame(14) == -1, "hidden below threshold");
        queue.Update(0, 0.5f, false, 15);
        Check(queue.ReadyFrame(15) == 0, "restarts after another crossing");
        queue.Reset();
        Check(queue.ReadyFrame(15) == -1, "reset on gameplay scene load");
        queue.Update(0, 0.75f, false, 20);
        queue.Update(0, 0.7f, true, 20.2f);
        Check(queue.ReadyFrame(20.2f) == -1, "activation hides the ready banner");
        queue.Update(0, 0.6f, true, 21);
        Check(queue.ReadyFrame(21) == -1, "active charge cannot trigger ready");
        queue.Update(0, 0, false, 22);
        queue.Update(0, 0.5f, false, 23);
        Check(queue.ReadyFrame(23) == 0, "earned recharge starts another banner");
    }

    // Story of My Life: 100 notes at 29.90 s, Star Power ready at 31.77 s; GH3 shows the banner after the streak.
    private static void ReadyWaitsForStreak()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        queue.Update(99, 0.4f, false, 20);
        queue.Update(100, 0.4f, false, 29.9f);
        Check(Near(queue.StreakStart, 29.9f) && queue.StreakCombo == 100, "the streak message starts");
        queue.Update(101, 0.5f, false, 31.77f);
        Check(queue.ReadyFrame(31.77f) == -1, "the ready banner waits while the streak message plays");
        queue.Update(110, 0.5f, false, 32.36f);
        Check(queue.ReadyFrame(32.36f) == 0, "the ready banner starts when the streak message ends");
        queue.Update(111, 0.5f, false, 32.40f);
        Check(Near(queue.ReadyStart, 29.9f + Streak), "the banner starts at the end of the streak, not at the next frame");
        Check(queue.ReadyFrame(29.9f + Streak + 1.01f) == 60, "the waiting banner plays in full");
        Check(queue.ReadyFrame(29.9f + Streak + Ready + 0.01f) == -1, "the waiting banner ends after its length");
    }

    // Welcome to the Jungle: Star Power ready at 38.72 s, 100 notes at 39.45 s; GH3 shows the streak after the banner.
    private static void StreakWaitsForReady()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        queue.Update(95, 0.4f, false, 30);
        queue.Update(95, 0.5f, false, 38.72f);
        Check(queue.ReadyFrame(38.72f) == 0, "the ready banner starts");
        queue.Update(100, 0.5f, false, 39.45f);
        Check(queue.StreakStart == float.NegativeInfinity, "the streak message waits while the banner plays");
        queue.Update(120, 0.5f, false, 40.85f);
        Check(Near(queue.StreakStart, 38.72f + Ready) && queue.StreakCombo == 100,
            "the streak message starts at the end of the banner with its own streak");
        Check(queue.ReadyFrame(40.85f) == -1, "the banner is gone when the streak message plays");
    }

    // Number of the Beast: 200 notes and Star Power ready at 31.77 s; GH3 shows the banner first.
    private static void SameFrame()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        queue.Update(199, 0.45f, false, 20);
        queue.Update(200, 0.5f, false, 31.77f);
        Check(queue.ReadyFrame(31.77f) == 0, "the banner wins in the same frame");
        Check(queue.StreakCombo == 100, "the streak message waits");
        queue.Update(230, 0.5f, false, 31.77f + Ready + 0.01f);
        Check(Near(queue.StreakStart, 31.77f + Ready) && queue.StreakCombo == 200, "then the streak message plays");
    }

    private static void DroppedMessages()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        queue.Update(100, 0, false, 1);
        queue.Update(200, 0, false, 2);
        Check(Near(queue.StreakStart, 1) && queue.StreakCombo == 100, "a streak during a streak message is dropped");
        queue.Update(200, 0, false, 4);
        Check(Near(queue.StreakStart, 1), "the dropped streak does not play later");

        queue.Reset();
        queue.Update(0, 0.5f, false, 10);
        queue.Update(100, 0.5f, false, 10.5f);
        queue.Update(200, 0.5f, false, 11);
        queue.Update(201, 0.5f, false, 12.2f);
        Check(Near(queue.StreakStart, 10 + Ready) && queue.StreakCombo == 100, "only the first waiting streak plays");
    }

    private static void ActivationWhileWaiting()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        queue.Update(100, 0.4f, false, 1);
        queue.Update(101, 0.5f, false, 2);
        queue.Update(102, 0.5f, true, 3);
        queue.Update(103, 0.45f, true, 4);
        Check(queue.ReadyFrame(4) == -1, "a waiting banner does not play when Star Power is activated");

        queue.Reset();
        queue.Update(0, 0.5f, false, 10);
        queue.Update(100, 0.5f, false, 10.5f);
        queue.Update(101, 0.5f, true, 11);
        Check(Near(queue.StreakStart, 11) && queue.ReadyFrame(11) == -1,
            "activation hides the banner; the waiting streak message starts at once");
    }

    // Without a coincidence each message starts at once, as before.
    private static void SingleMessages()
    {
        var queue = new CenterMessageQueue(Streak, Ready);
        queue.Update(99, 0, false, 1);
        queue.Update(100, 0, false, 1.1f);
        Check(Near(queue.StreakStart, 1.1f) && queue.StreakCombo == 100, "a streak message alone starts at once");
        queue.Update(0, 0, false, 4);
        queue.Update(100, 0, false, 9);
        Check(Near(queue.StreakStart, 9), "a new streak after a break shows again");
        queue.Update(102, 0, false, 11.6f);
        queue.Update(102, 0.5f, false, 11.6f);
        Check(queue.ReadyFrame(11.6f) == 0, "a banner after the streak message starts at once");

        var noMessages = new CenterMessageQueue(0, Ready);
        noMessages.Update(100, 0.5f, false, 1);
        Check(noMessages.ReadyFrame(1) == 0, "without a message font the banner never waits");
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Centre message test failed: {name}");
    }
}
