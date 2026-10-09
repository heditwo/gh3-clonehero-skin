using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using System.Buffers.Binary;
using System.Text.Json;
using UnityEngine;

namespace Gh3Skin;

internal readonly record struct AnimationFrame(Texture2D Texture, Rect Layout);

internal sealed class SkinAssets
{
    private static readonly string[] Lanes = { "green", "red", "yellow", "blue", "orange" };
    private readonly Dictionary<string, Sprite> _sprites = new();
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly List<AnimationFrame> _readyFrames = new();
    private readonly List<AnimationFrame> _readyLightningFrames = new();
    private readonly Sprite[,,] _gems = new Sprite[5, 6, GemAnimation.FrameCount];
    private readonly Sprite[,] _activeGems = new Sprite[6, GemAnimation.FrameCount];
    private static readonly string[] SustainVariants = { "green", "red", "yellow", "blue", "orange", "star", "dead" };
    private readonly Sprite[] _sustains = new Sprite[SustainVariants.Length];
    private readonly Sprite[] _heldSustains = new Sprite[SustainVariants.Length];
    public const float SustainSpriteWidth = 73;
    private Material? _beatlineMaterial;
    private Material? _ampMaterial;

    public IReadOnlyDictionary<string, Texture2D> Textures => _textures;
    public IReadOnlyList<AnimationFrame> ReadyFrames => _readyFrames;
    public IReadOnlyList<AnimationFrame> ReadyLightningFrames => _readyLightningFrames;
    public int GameplaySpriteCount => _sprites.Count;
    public static string Lane(int index) => Lanes[index];
    // The native track material can clip lines at highway level.
    public Material BeatlineMaterial => _beatlineMaterial ??= new Material(Shader.Find("Sprites/Default"))
    {
        renderQueue = 2980,
        hideFlags = HideFlags.DontUnloadUnusedAsset
    };

    public Material AmpMaterial
    {
        get
        {
            if (_ampMaterial != null) return _ampMaterial;
            // GH3 blends straight alpha; UI/Default premultiplies it in the fragment shader.
            _ampMaterial = new Material(Shader.Find("Hidden/Internal-GUITextureClip"))
            {
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            _ampMaterial.SetTexture("_GUIClipTexture", Texture2D.whiteTexture);
            _ampMaterial.SetInt("_ManualTex2SRGB", 0);
            return _ampMaterial;
        }
    }

    public string Root { get; }
    public string Fonts => SkinAssetPaths.Fonts(Root);
    public string YouRockFlames => SkinAssetPaths.YouRockFlames(Root);
    public Texture2D? HighwayTexture { get; private set; }

    public SkinAssets(string root)
    {
        Root = root;
        LoadCoreTextures(root);
        LoadReadyAnimation(root);
        LoadGameplaySprites(root);
        LoadHudTextures(root);
    }

    private void LoadCoreTextures(string root)
    {
        var highway = SkinAssetPaths.Highway(root);
        _textures["highway"] = LoadTexture(Path.Combine(highway, "highway.png"));
        _textures["highway"].wrapMode = TextureWrapMode.Repeat;
        HighwayTexture = _textures["highway"];
        _textures["highway-edges-blue"] = LoadTexture(Path.Combine(highway, "highway-edges-blue.png"));
        _textures["highway-edges-normal"] = LoadTexture(Path.Combine(highway, "highway-edges-normal.png"));
        for (var frame = 0; frame < StarPowerHighwayAnimation.FrameCount; frame++)
        {
            var name = $"active-highway-{frame:00}";
            var texture = LoadTexture(Path.Combine(SkinAssetPaths.StarPowerHighway(root), name + ".png"));
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Clamp;
            _textures[name] = texture;
        }
    }

    private void LoadReadyAnimation(string root)
    {
        root = SkinAssetPaths.ReadyAnimation(root);
        using (var animation = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ready-animation.json"))))
        {
            if (animation.RootElement.GetProperty("fps").GetInt32() != 60)
                throw new InvalidDataException("Ready animation must have 60 frames per second");
            LoadAnimationFrames(root, "ready-animation", animation.RootElement.GetProperty("frames"), _readyFrames);
            LoadAnimationFrames(root, "ready-lightning", animation.RootElement.GetProperty("lightning"), _readyLightningFrames);
        }
        if (_readyFrames.Count == 0 || _readyLightningFrames.Count == 0)
            throw new InvalidDataException("Missing GH3 ready animation frames");
        _textures["ready-animation-first"] = _readyFrames[0].Texture;
        _textures["ready-lightning-first"] = _readyLightningFrames[0].Texture;
    }

    private static void LoadAnimationFrames(string root, string prefix, JsonElement entries, List<AnimationFrame> destination)
    {
        foreach (var frame in entries.EnumerateArray())
        {
            var texture = LoadTexture(Path.Combine(root, $"{prefix}-{destination.Count:000}.png"));
            texture.filterMode = FilterMode.Point;
            var layout = new Rect(
                frame.GetProperty("x").GetInt32() * 1.2f,
                frame.GetProperty("y").GetInt32() * 1.2f,
                frame.GetProperty("width").GetInt32() * 1.2f,
                frame.GetProperty("height").GetInt32() * 1.2f);
            destination.Add(new AnimationFrame(texture, layout));
        }
    }

    private void LoadGameplaySprites(string root)
    {
        foreach (var lane in Lanes.Append("active"))
        {
            var gems = SkinAssetPaths.Gems(root, lane);
            Load(gems, $"{lane}-note", 720, new Vector2(0.5f, 0.16f));
            Load(gems, $"{lane}-glow", 720, new Vector2(0.5f, 0.16f));
            Load(gems, $"{lane}-tap", 720, new Vector2(0.5f, 0.16f));
            for (var frame = 0; frame < GemAnimation.FrameCount; frame++)
            {
                Load(gems, $"{lane}-star-{frame}", 550, new Vector2(0.5f, 0.16f));
                Load(gems, $"{lane}-star-glow-{frame}", 550, new Vector2(0.5f, 0.16f));
                Load(gems, $"{lane}-star-tap-{frame}", 550, new Vector2(0.5f, 0.16f));
            }
        }

        for (var lane = 0; lane < Lanes.Length; lane++)
            for (var frame = 0; frame < GemAnimation.FrameCount; frame++)
            {
                _gems[lane, 0, frame] = _sprites[$"{Lanes[lane]}-note"];
                _gems[lane, 1, frame] = _sprites[$"{Lanes[lane]}-glow"];
                _gems[lane, 2, frame] = _sprites[$"{Lanes[lane]}-star-{frame}"];
                _gems[lane, 3, frame] = _sprites[$"{Lanes[lane]}-star-glow-{frame}"];
                _gems[lane, 4, frame] = _sprites[$"{Lanes[lane]}-tap"];
                _gems[lane, 5, frame] = _sprites[$"{Lanes[lane]}-star-tap-{frame}"];
            }

        for (var frame = 0; frame < GemAnimation.FrameCount; frame++)
        {
            _activeGems[0, frame] = _sprites["active-note"];
            _activeGems[1, frame] = _sprites["active-glow"];
            _activeGems[2, frame] = _sprites[$"active-star-{frame}"];
            _activeGems[3, frame] = _sprites[$"active-star-glow-{frame}"];
            _activeGems[4, frame] = _sprites["active-tap"];
            _activeGems[5, frame] = _sprites[$"active-star-tap-{frame}"];
        }

        Load(SkinAssetPaths.Hud(root), "clear", 20, new Vector2(0.5f, 0.16f));
        foreach (var name in new[] { "beatline", "beatline-beat", "beatline-half" })
        {
            Load(SkinAssetPaths.BeatLines(root), name, 1024, new Vector2(0.5f, 0));
            _sprites[name].texture.wrapMode = TextureWrapMode.Clamp;
        }
        var sustains = SkinAssetPaths.Sustains(root);
        for (var variant = 0; variant < SustainVariants.Length; variant++)
        {
            _sustains[variant] = LoadSustain(sustains, $"sustain-{SustainVariants[variant]}");
            _heldSustains[variant] = (SustainVariant)variant == SustainVariant.Dead
                ? _sustains[variant]
                : LoadSustain(sustains, $"sustain-held-{SustainVariants[variant]}");
        }
    }

    private void LoadHudTextures(string root)
    {
        var hudNames = new List<string>
        {
            "HUD_rock_body", "HUD_rock_BG_green", "HUD_rock_BG_yellow", "HUD_rock_BG_red",
            "HUD_rock_lights_all", "HUD_rock_lights_green", "HUD_rock_lights_yellow",
            "HUD_rock_lights_red", "HUD_rock_needle",
            "HUD_score_body",
            "HUD_counter_body", "HUD_counter_body_tile", "HUD_counter_drum", "HUD_counter_drum_icon", "hud-glowburst",
            "HUD_score_nixie_1a", "HUD_score_nixie_2a",
            "HUD_score_nixie_3a", "HUD_score_nixie_4a", "HUD_rock_tube",
            "HUD_score_nixie_2b", "HUD_score_nixie_4b", "HUD_score_nixie_6b", "HUD_score_nixie_8b",
            "HUD_rock_tube_glow_full", "HUD_rock_tube_glow_full_b",
            "gh3-note-hit", "gh3-note-hit-star", "gh3-whammy-hold", "gh3-big-bolt",
            "gh3-particle-spark", "gh3-particle-star", "gh3-particle-star2"
        };
        hudNames.AddRange(new[] { "", "_blue", "_green", "_purple" }.SelectMany(color =>
            Enumerable.Range(0, 3).Select(state => $"HUD_score_light_{state}{color}")));

        foreach (var name in hudNames)
        {
            var directory = SkinAssetPaths.HudTextureFolder(root, name);
            var texture = name == "HUD_score_body" || name.StartsWith("HUD_score_nixie_", StringComparison.Ordinal)
                ? LoadDxt5(Path.Combine(directory, name + ".dds"))
                : LoadTexture(Path.Combine(directory, name + ".png"));
            if (name.StartsWith("HUD_score_light_", StringComparison.Ordinal) || name.StartsWith("gh3-", StringComparison.Ordinal))
                texture.wrapMode = TextureWrapMode.Clamp;
            _textures[name] = texture;
        }
    }

    public Sprite GetSprite(string name) => _sprites[name];
    public Sprite Sustain(SustainVariant variant) => _sustains[(int)variant];
    public Sprite HeldSustain(SustainVariant variant) => _heldSustains[(int)variant];
    public Sprite Gem(int lane, GemKind kind, int frame, bool starPowerActive) =>
        starPowerActive ? _activeGems[kind.Index, frame] : _gems[lane, kind.Index, frame];

    public Sprite Highway(Sprite original)
    {
        if (_sprites.TryGetValue("highway", out var highway))
            return highway;
        var texture = _textures["highway"];
        highway = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
            new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height), original.pixelsPerUnit);
        highway.name = "GH3_highway";
        highway.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sprites["highway"] = highway;
        MelonLogger.Msg($"Applied GH3 highway (original PPU {original.pixelsPerUnit})");
        return highway;
    }

    // Matches Clone Hero's sliced sustain sprites: bottom pivot, 100 pixels per unit, and the cap above a 38-pixel top border.
    private Sprite LoadSustain(string directory, string name)
    {
        var texture = LoadTexture(Path.Combine(directory, name + ".png"));
        texture.wrapMode = TextureWrapMode.Clamp;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0), 100, 0,
            SpriteMeshType.FullRect, new Vector4(0, 0, 0, 38));
        sprite.name = "GH3_" + name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sprites[name] = sprite;
        return sprite;
    }

    private void Load(string directory, string name, float pixelsPerUnit, Vector2 pivot)
    {
        var texture = LoadTexture(Path.Combine(directory, name + ".png"));
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, pixelsPerUnit);
        sprite.name = "GH3_" + name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sprites[name] = sprite;
    }

    private static Texture2D LoadTexture(string file)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(file)), true))
            throw new InvalidDataException(file);

        texture.filterMode = FilterMode.Bilinear;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

    private static Texture2D LoadDxt5(string file)
    {
        var data = File.ReadAllBytes(file);
        if (data.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20534444 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(84)) != 0x35545844)
            throw new InvalidDataException(file);
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0 ||
            data.Length - 128 != (long)width * height)
            throw new InvalidDataException(file);
        var texture = new Texture2D(width, height, TextureFormat.DXT5, false);
        texture.name = Path.GetFileNameWithoutExtension(file);
        texture.LoadRawTextureData(new Il2CppStructArray<byte>(data[128..]));
        texture.Apply(false, true);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

}
