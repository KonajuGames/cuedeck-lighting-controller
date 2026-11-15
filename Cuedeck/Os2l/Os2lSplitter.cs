using System.Text;

namespace Cuedeck.Os2l;

/// <summary>
/// Cuts a TCP byte stream of back-to-back JSON objects (how OS2L sends) into whole messages, as the bytes arrive.
/// Braces in strings, and escaped quotes, are handled; whatever's between objects (whitespace, newlines, NULs) is
/// skipped. It works on bytes, so a UTF-8 character split between reads is fine, and it carries on from where it got
/// to, so a big message arriving in pieces is looked at once, not again with every piece.
/// </summary>
sealed class Os2lSplitter
{
    byte[] _buffer = new byte[256];
    int _length;
    int _scanned;
    int _start = -1;
    int _depth;
    bool _inString;
    bool _escaped;
    readonly List<string> _ready = [];

    /// <summary>
    /// The bytes held that aren't a whole message yet.
    /// </summary>
    public ReadOnlySpan<byte> Pending => _buffer.AsSpan(0, _length);

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
        Scan();
    }

    /// <summary>
    /// The whole messages found since last asked, in order.
    /// </summary>
    public List<string> Take()
    {
        var output = new List<string>(_ready);
        _ready.Clear();
        return output;
    }

    void Scan()
    {
        var consumed = 0;
        for (; _scanned < _length; _scanned++)
        {
            var b = _buffer[_scanned];
            if (_inString)
            {
                if (_escaped)
                    _escaped = false;
                else if (b == '\\')
                    _escaped = true;
                else if (b == '"')
                    _inString = false;
            }
            else if (_depth == 0)
            {
                if (b == '{')
                {
                    _start = _scanned;
                    _depth = 1;
                }
                else
                    consumed = _scanned + 1; // junk between objects
            }
            else if (b == '"')
                _inString = true;
            else if (b == '{')
                _depth++;
            else if (b == '}' && --_depth == 0)
            {
                _ready.Add(Encoding.UTF8.GetString(_buffer, _start, _scanned + 1 - _start));
                consumed = _scanned + 1;
                _start = -1;
            }
        }
        // What's been dealt with goes, so the buffer holds only the message still coming.
        if (consumed > 0)
        {
            Array.Copy(_buffer, consumed, _buffer, 0, _length - consumed);
            _length -= consumed;
            _scanned -= consumed;
            if (_start >= 0)
                _start -= consumed;
        }
    }
}
