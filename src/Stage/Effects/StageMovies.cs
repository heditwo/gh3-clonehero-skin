using UnityEngine;

namespace Gh3Skin;

// Plays the venue movies (GH3VID01 frame banks) into their material texture slots.

internal sealed class StageMovies
{
    private sealed record TextureFrame(Texture2D Texture, StageMovieClip Clip, int Frame);
    private readonly StageAssets _assets;
    private readonly StageScripts _scripts;
    private readonly Dictionary<int, TextureFrame> _textures = new();
    public Dictionary<int, int> Frames { get; } = new();

    public StageMovies(StageAssets assets, StageScripts scripts) { _assets = assets; _scripts = scripts; }

    public void Update(double time)
    {
        Frames.Clear();
        foreach (var (slot, movie) in _scripts.State.Movies.Slots)
        {
            var frame = movie.Frame(time);
            if (frame < 0) continue;
            if (!_textures.TryGetValue(slot, out var current))
            {
                var texture = _assets.Own(new Texture2D(2, 2, TextureFormat.RGBA32, false));
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                current = new(texture, movie.Clip, -1);
            }
            if (!ReferenceEquals(current.Clip, movie.Clip) || current.Frame != frame)
            {
                if (!ImageConversion.LoadImage(current.Texture, movie.Clip.ReadFrame(frame), false) || current.Texture.width != movie.Clip.Width || current.Texture.height != movie.Clip.Height)
                    throw new InvalidDataException("Invalid movie image frame");
                current = current with { Clip = movie.Clip, Frame = frame };
                _textures[slot] = current;
            }
            Frames[slot] = frame;
        }
        foreach (var surface in _assets.MovieSurfaces)
            surface.Material.mainTexture = Frames.ContainsKey(surface.Slot) ? _textures[surface.Slot].Texture : surface.Original;
    }
}
