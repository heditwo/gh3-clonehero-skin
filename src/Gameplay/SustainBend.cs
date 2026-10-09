using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using WhammyManager = Il2Cpp.ObjectPublicAbstractSealedInBoInObDoInSiDoInDoUnique;

namespace Gh3Skin;

// Clone Hero records each channel's whammy value in a CPU texture: one column per tick, 1024 columns, one row per channel.
// The manager's column counter and tick length locate the sample of a given age.
internal sealed class WhammyHistory
{
    private const int Channels = 6;
    private static readonly int TextureId = Shader.PropertyToID("_GlobalWhammyTexture");
    private readonly float[,] _entries = new float[Channels, (int)SustainState.WibbleEntries + 1];
    private readonly int[] _sampledFrame = new int[Channels];
    private Texture2D? _texture;
    private int _column;
    private int _frame;
    private float _secondsPerColumn;

    public bool Begin()
    {
        if (_texture == null)
            _texture = Shader.GetGlobalTexture(TextureId)?.TryCast<Texture2D>();
        if (_texture == null || !_texture.isReadable)
            return false;
        _column = WhammyManager.field_Private_Static_Int32_0;
        _secondsPerColumn = (float)WhammyManager.field_Private_Static_Double_2;
        _frame++;
        return _secondsPerColumn > 0;
    }

    // Deflection at a fraction of the highway from the strike line (0) to the far end (1), read from GH3's 128 wibble entries.
    public float Sample(int channel, float highwayFraction)
    {
        if (_sampledFrame[channel] != _frame)
        {
            _sampledFrame[channel] = _frame;
            for (var entry = 0; entry <= SustainState.WibbleEntries; entry++)
                _entries[channel, entry] = Read(channel, entry / SustainState.WibbleEntriesPerSecond);
        }
        var position = Math.Clamp(highwayFraction, 0, 1) * SustainState.WibbleEntries;
        var index = Math.Min((int)position, (int)SustainState.WibbleEntries - 1);
        var fraction = position - index;
        return _entries[channel, index] + (_entries[channel, index + 1] - _entries[channel, index]) * fraction;
    }

    public float Value(int channel, float secondsAgo) => Read(channel, secondsAgo);

    private float Read(int channel, float secondsAgo)
    {
        var texture = _texture!;
        var width = texture.width;
        var position = _column - 1 - secondsAgo / _secondsPerColumn;
        var left = (int)MathF.Floor(position);
        var fraction = position - left;
        var first = texture.GetPixel(((left % width) + width) % width, channel).r;
        var second = texture.GetPixel((((left + 1) % width) + width) % width, channel).r;
        return first + (second - first) * fraction;
    }

    // Verification only: drives Clone Hero's own whammy input path for player 1, lanes green to orange.
    public static void Force(float deflection) =>
        WhammyManager.Method_Public_Static_Void_Int32_UInt16_Byte_0(0, 0x3e, (byte)Math.Clamp(MathF.Round(deflection * 255), 0, 255));
}

// Replaces a held sustain's sliced sprite with a strip mesh while its whammy history is not flat, so the bar width can
// change per row like GH3's WhammyBar shader. The strip uses Clone Hero's sustain material and property block, so the
// length, track fade, and sorting stay native.
internal sealed class SustainBend
{
    // GH3's WhammyBar mesh has 128 rows. Extra columns keep the bright core straight: with only edge vertices, the affine
    // texture mapping of each widening trapezoid bends the core into a zigzag.
    private const int Rows = 128;
    private const int Columns = 8;
    private const int Stride = Columns + 1;
    private const float SpriteHeight = 100.2f;
    private const float CapHeight = 0.38f;
    private const float BodyTop = 32f / 70;
    private static readonly int LengthId = Shader.PropertyToID("_SustainLength");
    private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private readonly Il2CppStructArray<Vector3> _vertices = new((Rows + 2) * Stride);
    private readonly float[] _scales = new float[Rows + 1];

    public SustainBend(SpriteRenderer sustain)
    {
        _mesh = new Mesh { name = "GH3 Sustain", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _mesh.MarkDynamic();
        _mesh.vertices = _vertices;
        var uv = new Vector2[_vertices.Length];
        var colors = new Color32[_vertices.Length];
        var triangles = new int[(Rows + 1) * Columns * 6];
        var index = 0;
        for (var row = 0; row <= Rows + 1; row++)
            for (var column = 0; column <= Columns; column++)
            {
                var vertex = row * Stride + column;
                uv[vertex] = new Vector2 { x = (float)column / Columns, y = row <= Rows ? BodyTop * row / Rows : 1 };
                colors[vertex] = new Color32(255, 255, 255, 255);
                if (row > Rows || column == Columns)
                    continue;
                triangles[index++] = vertex;
                triangles[index++] = vertex + Stride;
                triangles[index++] = vertex + 1;
                triangles[index++] = vertex + 1;
                triangles[index++] = vertex + Stride;
                triangles[index++] = vertex + Stride + 1;
            }
        _mesh.uv = new Il2CppStructArray<Vector2>(uv);
        _mesh.colors32 = new Il2CppStructArray<Color32>(colors);
        _mesh.triangles = new Il2CppStructArray<int>(triangles);
        _mesh.bounds = new Bounds(new Vector3 { y = SpriteHeight / 2 }, new Vector3 { x = 10, y = SpriteHeight + 10, z = 10 });
        var strip = new GameObject("GH3 Sustain") { layer = sustain.gameObject.layer };
        strip.transform.SetParent(sustain.transform, false);
        strip.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = strip.AddComponent<MeshRenderer>();
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.enabled = false;
    }

    public void Hide(SpriteRenderer sustain)
    {
        if (!_renderer.enabled)
            return;
        _renderer.enabled = false;
        sustain.forceRenderingOff = false;
    }

    public void Update(SpriteRenderer sustain, MaterialPropertyBlock properties, WhammyHistory history, int channel,
        float strikeZ, float highwayLength)
    {
        sustain.GetPropertyBlock(properties);
        var length = properties.GetFloat(LengthId);
        var bodyLength = Math.Max(0, length - (CapHeight - 0.2f));
        var transform = sustain.transform;
        var baseZ = transform.position.z;
        var axisZ = transform.TransformVector(new Vector3 { y = 1 }).z;
        var bent = false;
        for (var row = 0; row <= Rows; row++)
        {
            var z = baseZ + axisZ * bodyLength * row / Rows;
            var deflection = history.Sample(channel, (z - strikeZ) / highwayLength);
            bent |= deflection > 1f / 255;
            _scales[row] = SustainState.WhammyWidth(deflection);
        }
        if (!bent)
        {
            Hide(sustain);
            return;
        }
        // The native sustain shader moves vertices at y >= 1 to y - 100 + _SustainLength, so the strip is built in that space.
        var width = sustain.size.x;
        var offset = Math.Max(1, 100 - length);
        for (var row = 0; row <= Rows + 1; row++)
        {
            var y = row <= Rows ? offset + bodyLength * row / Rows : SpriteHeight;
            var rowWidth = width * _scales[Math.Min(row, Rows)];
            for (var column = 0; column <= Columns; column++)
                _vertices[row * Stride + column] = new Vector3 { x = rowWidth * ((float)column / Columns - 0.5f), y = y };
        }
        _mesh.vertices = _vertices;
        _renderer.sharedMaterial = sustain.sharedMaterial;
        _renderer.sortingLayerID = sustain.sortingLayerID;
        _renderer.sortingOrder = sustain.sortingOrder;
        properties.SetTexture(MainTextureId, sustain.sprite.texture);
        _renderer.SetPropertyBlock(properties);
        _renderer.enabled = true;
        sustain.forceRenderingOff = true;
    }
}
