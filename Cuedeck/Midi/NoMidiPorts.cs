namespace Cuedeck.Midi;

/// <summary>
/// No MIDI ports at all: what the app has away from Windows, where <see cref="WinMidi"/> isn't.
/// </summary>
sealed class NoMidiPorts : IMidiPorts
{
    public IReadOnlyList<string> InputNames() => [];

    public IReadOnlyList<string> OutputNames() => [];

    public IDisposable? OpenInput(int index, Action<MidiMessage> received) => null;

    public IMidiOutput? OpenOutput(int index) => null;
}
