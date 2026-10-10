using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace Gh3Skin;

// Finds the game manager and the player of the gameplay scene, and remembers them for the scene.

internal sealed class GameplayScene
{
    private GameManager? _manager;
    private BasePlayer? _player;
    private bool _unsupported;
    private float _nextSearch;

    public void Reset()
    {
        _manager = null;
        _player = null;
        _unsupported = false;
        _nextSearch = 0;
    }

    public bool TryGetPlayer(out BasePlayer player, out ScoreManager score)
    {
        player = null!;
        score = null!;
        if (_unsupported)
            return false;
        if (_manager == null)
        {
            if (Time.unscaledTime < _nextSearch)
                return false;
            _nextSearch = Time.unscaledTime + 0.25f;
            _manager = UnityEngine.Object.FindObjectOfType<GameManager>();
        }
        if (_manager == null || !_manager.playersInit || _manager.scoreManager == null)
            return false;
        if (_player == null)
        {
            var players = _manager.playerObjects;
            if (players == null)
                return false;
            var count = 0;
            foreach (var candidate in players)
            {
                if (candidate == null)
                    continue;
                _player = candidate;
                count++;
            }
            if (count == 0)
                return false;
            if (count != 1 || _player!.TryCast<GuitarPlayer>() == null)
            {
                MelonLogger.Msg("GH3 skin supports one five-fret guitar player. This session uses the stock display.");
                _unsupported = true;
                return false;
            }
        }
        if (_player.engine == null)
            return false;
        player = _player;
        score = _manager.scoreManager;
        return true;
    }
}
