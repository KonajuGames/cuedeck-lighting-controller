namespace Cuedeck.Midi;

enum MidiKind
{
    NoteOff,
    NoteOn,
    ControlChange,
    PitchBend,
}

/// <summary>
/// A three-byte MIDI channel message: its status byte (kind and channel) and two data bytes. Adapted from Cuepoint's,
/// with pitch bend for faders that send it.
/// </summary>
readonly record struct MidiMessage(byte Status, byte Data1, byte Data2)
{
    const int NOTE_OFF = 0x80;
    const int NOTE_ON = 0x90;
    const int CONTROL_CHANGE = 0xB0;
    const int PITCH_BEND = 0xE0;

    public static MidiMessage NoteOn(int channel, int note, int velocity) =>
        new((byte)(NOTE_ON | (channel & 0x0F)), (byte)(note & 0x7F), (byte)(velocity & 0x7F));

    public static MidiMessage NoteOff(int channel, int note) => new((byte)(NOTE_OFF | (channel & 0x0F)), (byte)(note & 0x7F), 0);

    public static MidiMessage Control(int channel, int number, int value) =>
        new((byte)(CONTROL_CHANGE | (channel & 0x0F)), (byte)(number & 0x7F), (byte)(value & 0x7F));

    /// <summary>
    /// A pitch bend of <paramref name="value"/>, 0 to 16383.
    /// </summary>
    public static MidiMessage Bend(int channel, int value) =>
        new((byte)(PITCH_BEND | (channel & 0x0F)), (byte)(value & 0x7F), (byte)((value >> 7) & 0x7F));

    /// <summary>
    /// The message packed as WinMM passes it: status in the low byte, then the two data bytes.
    /// </summary>
    public static MidiMessage FromPacked(uint packed) => new((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));

    public uint Packed => Status | (uint)Data1 << 8 | (uint)Data2 << 16;

    public MidiKind? Kind =>
        (Status & 0xF0) switch
        {
            NOTE_OFF => MidiKind.NoteOff,
            NOTE_ON => MidiKind.NoteOn,
            CONTROL_CHANGE => MidiKind.ControlChange,
            PITCH_BEND => MidiKind.PitchBend,
            _ => null,
        };

    /// <summary>
    /// The channel, from 0 to 15 (shown to people as 1 to 16).
    /// </summary>
    public int Channel => Status & 0x0F;

    /// <summary>
    /// The note or controller number.
    /// </summary>
    public int Number => Data1;

    /// <summary>
    /// A note's velocity or a controller's value.
    /// </summary>
    public int Value => Data2;

    /// <summary>
    /// A pitch bend's value, 0 to 16383.
    /// </summary>
    public int BendValue => Data1 | (Data2 << 7);

    public bool IsNote => Kind is MidiKind.NoteOn or MidiKind.NoteOff;

    /// <summary>
    /// A note went down: a note-on with a velocity (most controllers send a note-on at 0 for a release).
    /// </summary>
    public bool IsPressed => Kind == MidiKind.NoteOn && Data2 > 0;
}
