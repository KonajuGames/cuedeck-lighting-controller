using Cuedeck.Midi;

namespace Cuedeck.Tests.Triggers;

/// <summary>
/// Every MIDI input kept open as controllers come and go (MidiDevices, from Cuepoint), against stand-in ports.
/// </summary>
public class MidiDevicesTests
{
    sealed class FakePorts : IMidiPorts
    {
        public List<string> Inputs { get; } = [];
        public List<string> Outputs { get; } = [];
        public Dictionary<string, Action<MidiMessage>> Open { get; } = [];
        public List<string> Closed { get; } = [];

        public IReadOnlyList<string> InputNames() => Inputs;

        public IReadOnlyList<string> OutputNames() => Outputs;

        public IDisposable? OpenInput(int index, Action<MidiMessage> received)
        {
            var name = Inputs[index];
            Open[name] = received;
            return new Closer(() =>
            {
                Open.Remove(name);
                Closed.Add(name);
            });
        }

        public IMidiOutput? OpenOutput(int index) => null;

        sealed class Closer(Action close) : IDisposable
        {
            public void Dispose() => close();
        }
    }

    [Fact]
    public void Inputs_open_as_they_appear_and_close_as_they_go()
    {
        var ports = new FakePorts();
        ports.Inputs.Add("Pads");
        using var midi = new MidiDevices(ports);
        midi.Update(0);
        Assert.Equal(["Pads"], midi.Devices.Select(d => d.Name));

        ports.Open["Pads"](MidiMessage.NoteOn(0, 36, 100));
        Assert.True(midi.TryRead(out var device, out var message));
        Assert.Equal(("Pads", 36), (device.Name, message.Number));
        Assert.False(midi.TryRead(out _, out _));

        // Checked again only every couple of seconds.
        ports.Inputs.Add("Faders");
        midi.Update(1);
        Assert.Single(midi.Devices);
        midi.Update(2.5);
        Assert.Equal(2, midi.Devices.Count);

        ports.Inputs.Remove("Pads");
        midi.Scan();
        Assert.Equal(["Faders"], midi.Devices.Select(d => d.Name));
        Assert.Equal(["Pads"], ports.Closed);
    }
}
