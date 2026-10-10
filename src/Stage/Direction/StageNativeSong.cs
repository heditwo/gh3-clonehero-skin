namespace Gh3Skin;

// Finds and loads the native GH3 song PAK of a song folder.

internal static class StageNativeSong
{
    public static StageTimeline Load(string path)
    {
        var song = Gh3Song.Load(path);
        return new(StageGh3Events.Read(song), "native " + Path.GetFileName(path)) { NativeSong = song, SourceSong = song.Id };
    }
}
