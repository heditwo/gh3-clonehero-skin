using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using System.Buffers.Binary;
using System.Runtime;
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

    // The constructor only plans the texture batches and starts to decode the images on worker threads. Upload puts the
    // decoded images into Unity textures on the main thread, a few milliseconds in each menu frame, so the menu does not
    // wait for the skin; Finish uploads the rest at once (the gameplay scene needs all of them).
    public SkinAssets(string root)
    {
        Root = root;
        PlanCoreTextures(root);
        PlanReadyAnimation(root);
        PlanGameplaySprites(root);
        PlanHudTextures(root);
        var files = _batches.SelectMany(batch => batch.Files).ToArray();
        _decoded = new DecodedImage?[files.Length];
        _sizes = new long[files.Length];
        _decoding = Task.Run(() =>
        {
            for (var index = 0; index < files.Length; index++) _sizes[index] = DecodedSize(files[index]);
            var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).Select(_ => Task.Run(() => DecodeInOrder(files))).ToArray();
            Task.WaitAll(workers);
        });
    }

    // The workers decode the images in upload order and stop while the decoded images that wait for the upload would
    // pass the budget, so the skin needs about 32 MB at a time instead of 190 MB.
    private const long DecodeBudget = 32_000_000;
    private readonly object _gate = new();
    private readonly long[] _sizes;
    private long _waiting;
    private int _nextDecode;
    private bool _failed;

    private void DecodeInOrder(string[] files)
    {
        while (true)
        {
            int index;
            lock (_gate)
            {
                while (!_failed && _nextDecode < files.Length && _waiting > 0 && _waiting + _sizes[_nextDecode] > DecodeBudget) Monitor.Wait(_gate);
                if (_failed || _nextDecode >= files.Length) return;
                index = _nextDecode++;
                _waiting += _sizes[index];
            }
            try { Volatile.Write(ref _decoded[index], Decode(files[index])); }
            catch
            {
                lock (_gate)
                {
                    _failed = true;
                    Monitor.PulseAll(_gate);
                }
                throw;
            }
        }
    }

    private static long DecodedSize(string file)
    {
        if (file.EndsWith(".dds", StringComparison.Ordinal)) return new FileInfo(file).Length;
        // The PNG IHDR chunk: width and height at bytes 16 and 20.
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(file);
        for (var read = 0; read < header.Length;)
        {
            var count = stream.Read(header[read..]);
            if (count == 0) return 0;
            read += count;
        }
        return (long)BinaryPrimitives.ReadInt32BigEndian(header[16..]) * BinaryPrimitives.ReadInt32BigEndian(header[20..]) * 4;
    }

    private sealed record DecodedImage(byte[] Data, int Width, int Height, bool Dxt5);
    private readonly List<(IReadOnlyList<string> Files, Action<List<Texture2D>> Apply)> _batches = new();
    private readonly DecodedImage?[] _decoded;
    private readonly Task _decoding;
    private readonly List<Texture2D> _uploaded = new();
    private int _batch, _next;
    public bool Ready => _batch == _batches.Count;

    private void Plan(IReadOnlyList<string> files, Action<List<Texture2D>> apply) => _batches.Add((files, apply));

    // Uploads decoded images until the time budget is used; false while images remain.
    public bool Upload(double milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!Ready)
        {
            if (_decoding.IsFaulted) _decoding.GetAwaiter().GetResult();
            var (files, apply) = _batches[_batch];
            if (_uploaded.Count < files.Count)
            {
                var image = Volatile.Read(ref _decoded[_next]);
                if (image == null)
                {
                    if (clock.Elapsed.TotalMilliseconds >= milliseconds) return false;
                    if (_decoding.IsCompleted) _decoding.GetAwaiter().GetResult();
                    Thread.Sleep(1);
                    continue;
                }
                _decoded[_next] = null;
                _uploaded.Add(image.Dxt5 ? UploadDxt5(image, files[_uploaded.Count]) : UploadRgba(image));
                lock (_gate)
                {
                    _waiting -= _sizes[_next++];
                    Monitor.PulseAll(_gate);
                }
            }
            else
            {
                apply(_uploaded.ToList());
                _uploaded.Clear();
                _batch++;
                if (Ready)
                {
                    // The decoded images (about 190 MB, mostly the ready animation) are garbage now; give the memory back at once.
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect();
                    return true;
                }
            }
            if (clock.Elapsed.TotalMilliseconds >= milliseconds) return false;
        }
        return true;
    }

    public void Finish() => Upload(double.PositiveInfinity);

    private void PlanCoreTextures(string root)
    {
        var highway = SkinAssetPaths.Highway(root);
        var frames = Enumerable.Range(0, StarPowerHighwayAnimation.FrameCount).Select(frame => $"active-highway-{frame:00}").ToList();
        Plan(new[] { "highway", "highway-edges-blue", "highway-edges-normal" }.Select(name => Path.Combine(highway, name + ".png"))
            .Concat(frames.Select(name => Path.Combine(SkinAssetPaths.StarPowerHighway(root), name + ".png"))).ToList(), textures => ApplyCoreTextures(frames, textures));
    }

    private void ApplyCoreTextures(List<string> frames, List<Texture2D> textures)
    {
        _textures["highway"] = textures[0];
        _textures["highway"].wrapMode = TextureWrapMode.Repeat;
        HighwayTexture = _textures["highway"];
        _textures["highway-edges-blue"] = textures[1];
        _textures["highway-edges-normal"] = textures[2];
        for (var frame = 0; frame < frames.Count; frame++)
        {
            var texture = textures[3 + frame];
            texture.name = frames[frame];
            texture.wrapMode = TextureWrapMode.Clamp;
            _textures[frames[frame]] = texture;
        }
    }

    private void PlanReadyAnimation(string root)
    {
        root = SkinAssetPaths.ReadyAnimation(root);
        using var animation = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ready-animation.json")));
        if (animation.RootElement.GetProperty("fps").GetInt32() != 60)
            throw new InvalidDataException("Ready animation must have 60 frames per second");
        var frames = Layouts(animation.RootElement.GetProperty("frames"));
        var lightning = Layouts(animation.RootElement.GetProperty("lightning"));
        if (frames.Count == 0 || lightning.Count == 0)
            throw new InvalidDataException("Missing GH3 ready animation frames");
        Plan(Enumerable.Range(0, frames.Count).Select(index => Path.Combine(root, $"ready-animation-{index:000}.png"))
            .Concat(Enumerable.Range(0, lightning.Count).Select(index => Path.Combine(root, $"ready-lightning-{index:000}.png"))).ToList(), textures =>
        {
            for (var index = 0; index < textures.Count; index++)
            {
                textures[index].filterMode = FilterMode.Point;
                if (index < frames.Count) _readyFrames.Add(new AnimationFrame(textures[index], frames[index]));
                else _readyLightningFrames.Add(new AnimationFrame(textures[index], lightning[index - frames.Count]));
            }
            _textures["ready-animation-first"] = _readyFrames[0].Texture;
            _textures["ready-lightning-first"] = _readyLightningFrames[0].Texture;
        });
    }

    private static List<Rect> Layouts(JsonElement entries) => entries.EnumerateArray().Select(frame => new Rect(
        frame.GetProperty("x").GetInt32() * 1.2f,
        frame.GetProperty("y").GetInt32() * 1.2f,
        frame.GetProperty("width").GetInt32() * 1.2f,
        frame.GetProperty("height").GetInt32() * 1.2f)).ToList();

    private void PlanGameplaySprites(string root)
    {
        var gemSprites = new List<(string Name, string File, float PixelsPerUnit)>();
        foreach (var lane in Lanes.Append("active"))
        {
            var gems = SkinAssetPaths.Gems(root, lane);
            foreach (var kind in new[] { "note", "glow", "tap" }) gemSprites.Add(($"{lane}-{kind}", Path.Combine(gems, $"{lane}-{kind}.png"), 720));
            for (var frame = 0; frame < GemAnimation.FrameCount; frame++)
                foreach (var kind in new[] { "star", "star-glow", "star-tap" })
                    gemSprites.Add(($"{lane}-{kind}-{frame}", Path.Combine(gems, $"{lane}-{kind}-{frame}.png"), 550));
        }
        Plan(gemSprites.Select(sprite => sprite.File).ToList(), textures =>
        {
            for (var index = 0; index < gemSprites.Count; index++)
                AddSprite(gemSprites[index].Name, textures[index], gemSprites[index].PixelsPerUnit, new Vector2(0.5f, 0.16f));
            ApplyGems();
        });
        var lines = new[] { "beatline", "beatline-beat", "beatline-half" };
        Plan(new[] { Path.Combine(SkinAssetPaths.Hud(root), "clear.png") }.Concat(lines.Select(name => Path.Combine(SkinAssetPaths.BeatLines(root), name + ".png"))).ToList(), textures =>
        {
            AddSprite("clear", textures[0], 20, new Vector2(0.5f, 0.16f));
            for (var index = 0; index < lines.Length; index++)
            {
                AddSprite(lines[index], textures[1 + index], 1024, new Vector2(0.5f, 0));
                _sprites[lines[index]].texture.wrapMode = TextureWrapMode.Clamp;
            }
        });
        var sustains = SkinAssetPaths.Sustains(root);
        var held = Enumerable.Range(0, SustainVariants.Length).Where(variant => (SustainVariant)variant != SustainVariant.Dead).ToList();
        Plan(SustainVariants.Select(name => Path.Combine(sustains, $"sustain-{name}.png"))
            .Concat(held.Select(variant => Path.Combine(sustains, $"sustain-held-{SustainVariants[variant]}.png"))).ToList(), textures =>
        {
            for (var variant = 0; variant < SustainVariants.Length; variant++)
                _sustains[variant] = AddSustain($"sustain-{SustainVariants[variant]}", textures[variant]);
            for (var variant = 0; variant < SustainVariants.Length; variant++)
                _heldSustains[variant] = held.IndexOf(variant) is var at and >= 0
                    ? AddSustain($"sustain-held-{SustainVariants[variant]}", textures[SustainVariants.Length + at]) : _sustains[variant];
        });
    }

    private void ApplyGems()
    {
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
    }

    private void PlanHudTextures(string root)
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

        // The score body and the nixie digits are DXT5 blocks captured from GH3 (assets/hud/score).
        Plan(hudNames.Select(name => Path.Combine(SkinAssetPaths.HudTextureFolder(root, name),
            name + (name == "HUD_score_body" || name.StartsWith("HUD_score_nixie_", StringComparison.Ordinal) ? ".dds" : ".png"))).ToList(), textures =>
        {
            for (var index = 0; index < hudNames.Count; index++)
            {
                var name = hudNames[index];
                if (name.StartsWith("HUD_score_light_", StringComparison.Ordinal) || name.StartsWith("gh3-", StringComparison.Ordinal))
                    textures[index].wrapMode = TextureWrapMode.Clamp;
                _textures[name] = textures[index];
            }
            MelonLogger.Msg($"Loaded {GameplaySpriteCount} GH3 skin gameplay sprites");
        });
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
    private Sprite AddSustain(string name, Texture2D texture)
    {
        texture.wrapMode = TextureWrapMode.Clamp;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0), 100, 0,
            SpriteMeshType.FullRect, new Vector4(0, 0, 0, 38));
        sprite.name = "GH3_" + name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sprites[name] = sprite;
        return sprite;
    }

    private void AddSprite(string name, Texture2D texture, float pixelsPerUnit, Vector2 pivot)
    {
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, pixelsPerUnit);
        sprite.name = "GH3_" + name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _sprites[name] = sprite;
    }

    // PNG files decode with the managed decoder on worker threads (Unity's LoadImage decodes on the main thread: 0.6 s for
    // the skin at start). The texels equal LoadImage's: 8-bit RGBA, bottom row first, sRGB.
    private static DecodedImage Decode(string file)
    {
        if (file.EndsWith(".dds", StringComparison.Ordinal)) return ReadDxt5(file);
        var rgba = Packages.Png.DecodeRgba(File.ReadAllBytes(file), out var width, out var height);
        return new DecodedImage(rgba, width, height, false);
    }

    private static Texture2D UploadRgba(DecodedImage image)
    {
        var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false);
        LoadRaw(texture, image.Data, 0);
        texture.Apply(false, true);
        texture.filterMode = FilterMode.Bilinear;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

    // Texture2D.LoadRawTextureDataImpl copies the bytes from a pointer: the managed array goes to the texture without a
    // second copy in an Il2Cpp array.
    internal static void LoadRaw(Texture2D texture, byte[] data, int offset)
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            if (!texture.LoadRawTextureDataImpl(handle.AddrOfPinnedObject() + offset, (ulong)(data.Length - offset)))
                throw new InvalidDataException("The texture data does not match the texture size");
        }
        finally { handle.Free(); }
    }

    internal static Texture2D LoadTexture(string file)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(new Il2CppStructArray<byte>(File.ReadAllBytes(file)), true))
            throw new InvalidDataException(file);

        texture.filterMode = FilterMode.Bilinear;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

    private static DecodedImage ReadDxt5(string file)
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
        return new DecodedImage(data[128..], width, height, true);
    }

    private static Texture2D UploadDxt5(DecodedImage image, string file)
    {
        var texture = new Texture2D(image.Width, image.Height, TextureFormat.DXT5, false);
        texture.name = Path.GetFileNameWithoutExtension(file);
        LoadRaw(texture, image.Data, 0);
        texture.Apply(false, true);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }
}
