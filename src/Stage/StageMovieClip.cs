namespace Gh3Skin;

internal sealed class StageMovieClip : IDisposable
{
    private readonly BinaryReader _reader;
    private readonly long[] _offsets;
    public int Width { get; }
    public int Height { get; }
    public int Frames => _offsets.Length - 1;
    public double FrameRate { get; }

    public StageMovieClip(string path)
    {
        _reader = new(File.OpenRead(path));
        try
        {
            if (System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(8)) != "GH3VID01") throw new InvalidDataException("Invalid movie frame bank");
            Width = _reader.ReadInt32(); Height = _reader.ReadInt32();
            var frames = _reader.ReadInt32();
            var numerator = _reader.ReadUInt32(); var denominator = _reader.ReadUInt32();
            FrameRate = numerator / (double)denominator;
            if (Width is < 1 or > 4096 || Height is < 1 or > 4096 || frames is < 1 or > 100000 || !double.IsFinite(FrameRate) || FrameRate <= 0 || FrameRate > 240 || frames / FrameRate > 86400)
                throw new InvalidDataException("Invalid movie frame dimensions or rate");
            _offsets = Enumerable.Range(0, frames + 1).Select(_ => _reader.ReadInt64()).ToArray();
            if (_offsets[0] != _reader.BaseStream.Position || _offsets[^1] != _reader.BaseStream.Length ||
                _offsets.Skip(1).Where((offset, i) => offset <= _offsets[i] || offset - _offsets[i] > 64000000).Any())
                throw new InvalidDataException("Invalid movie frame offsets");
        }
        catch { _reader.Dispose(); throw; }
    }

    public byte[] ReadFrame(int frame)
    {
        if (frame < 0 || frame >= Frames) throw new ArgumentOutOfRangeException(nameof(frame));
        _reader.BaseStream.Position = _offsets[frame];
        var length = checked((int)(_offsets[frame + 1] - _offsets[frame]));
        var data = _reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException("Movie frame was truncated");
        return data;
    }

    public void Dispose() => _reader.Dispose();
}
