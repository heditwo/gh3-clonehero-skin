using Il2Cpp;
using UnityEngine;

namespace Gh3Skin;

internal readonly record struct GameplayState(int Score, int Combo, float Rock, float StarPower);

internal static class GameplayStateReader
{
    public static GameplayState Read(GameplayState previous, SkinOptions options)
    {
        var score = previous.Score;
        var combo = previous.Combo;
        var rock = previous.Rock;
        var power = previous.StarPower;

        foreach (var manager in UnityEngine.Object.FindObjectsOfType<ScoreManager>())
        {
            score = manager.field_Private_Int32_4;
            combo = manager.field_Private_Int32_2;
            var particles = manager.starProgress?.starParticles;
            if (particles != null)
            {
                var renderer = particles.GetComponent<ParticleSystemRenderer>();
                if (renderer != null)
                    renderer.enabled = false;
            }
            break;
        }

        foreach (var player in UnityEngine.Object.FindObjectsOfType<BasePlayer>())
        {
            if (player.engine != null)
            {
                rock = Math.Clamp(player.engine.field_Protected_Single_0, 0, 1);
                var amount = player.spBar != null && player.spBar.field_Private_Single_0 >= 0
                    ? player.spBar.field_Private_Single_0
                    : player.engine.prop_Single_0;
                power = Math.Clamp(amount > 1 ? amount / 100 : amount, 0, 1);
            }
            break;
        }

        if (options.DisplayPower.HasValue && combo >= options.ForcePowerAtCombo)
            power = options.DisplayPower.Value;
        if (options.DisplayRock.HasValue)
            rock = options.DisplayRock.Value;
        return new GameplayState(score, combo, rock, power);
    }
}
