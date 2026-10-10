namespace Gh3Skin;

// Standard MIDI File values (SMF 1.0) that the chart reader uses.
internal static class Midi
{
    public const int MaximumDataByte = 127;
    // A data byte has bit 7 clear; a status byte has it set.
    public const int StatusBit = 0x80;
    public const int StatusMask = 0xF0;
    public const int ChannelMask = 15;
    public const int NoteOff = 0x80;
    public const int NoteOn = 0x90;
    public const int ProgramChange = 0xC0;
    public const int ChannelPressure = 0xD0;
    public const int SystemExclusive = 0xF0;
    public const int SystemExclusiveEscape = 0xF7;
    public const int MetaEvent = 0xFF;
    public const int MetaText = 0x01;
    public const int MetaTrackName = 0x03;
    public const int MetaLyric = 0x05;
    public const int MetaMarker = 0x06;
    public const int MetaEndOfTrack = 0x2F;
    public const int MetaTempo = 0x51;
    // Header division with bit 15 set is SMPTE time, not ticks per quarter note.
    public const int SmpteDivision = 0x8000;
    public const int HeaderSize = 6;
    // 120 beats per minute.
    public const double DefaultMicrosecondsPerBeat = 500000.0;
    public const double MicrosecondsPerSecond = 1000000.0;
    public const int MicrosecondsPerMinute = 60000000;
    public const int MaximumFileBytes = 32 * 1024 * 1024;
}

// Rock Band and Clone Hero chart conventions (GuitarGame_ChartFormats).
internal static class ChartNote
{
    // .chart files without a Resolution use 192 ticks per beat.
    public const int DefaultResolution = 192;
    // .chart tempo values are thousandths of a beat per minute: microseconds per beat = 60e9 / value.
    public const long ChartTempoNumerator = 60000000000;
    // PART GUITAR/BASS/RHYTHM: the Expert gems green to orange (strum cues).
    public const int ExpertGreen = 96, ExpertOrange = 100;
    // PART VOCALS: sung pitches 36-84; phrase markers 105 and 106.
    public const int VocalLowest = 36, VocalHighest = 84;
    public const int VocalPhrase = 105, VocalPhrase2 = 106;
}
