using Il2Cpp;

namespace Gh3Skin;

// Reads score, streak, rock, and Star Power from Clone Hero once per frame (GameplayStateReader).

internal static class GameplayStateReader
{
    public static GameplayState Read(BasePlayer player, ScoreManager manager, SkinOptions options, ComboOverride comboOverride)
    {
        var engine = player.engine;
        var score = manager.field_Private_Int32_4;
        var combo = comboOverride.Apply(manager.field_Private_Int32_2);
        var rock = HudState.UnitValue(engine.field_Protected_Single_0);
        var power = HudState.UnitValue(CloneHeroBindings.StarPowerCharge(engine));

        if (options.DisplayPower.HasValue && combo >= options.ForcePowerAtCombo)
            power = options.DisplayPower.Value;
        if (options.DisplayRock.HasValue)
            rock = options.DisplayRock.Value;
        return new GameplayState(score, combo, rock, power, CloneHeroBindings.Multiplier(engine),
            engine.field_Public_Boolean_0 || options.ForceStarPowerActive);
    }
}
