namespace Gh3Skin;

internal sealed record StageCue(double Time, string Type, string Value = "", double Duration = 0,
    string Role = "", bool Immediate = false, bool Loop = false, int Count = 1, int DisableArms = 0, int? Velocity = null);
