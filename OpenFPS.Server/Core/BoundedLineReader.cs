using System.Text;

namespace OpenFPS.Server.Core;

/// <summary>
/// Reads lines from a text stream without ever holding more than a set length of one.
///
/// <see cref="TextReader.ReadLineAsync()"/> keeps reading until it finds a newline, so a client that
/// sends a gigabyte with no newline in it makes the server hold a gigabyte. This keeps the first
/// <c>maxChars</c> of a line, throws the rest away as it arrives, and reports that it did.
/// A carriage return or a NUL (telnet sends "\r\n" and sometimes "\r\0") is not part of a line.
/// </summary>
public sealed class BoundedLineReader
{
    private readonly TextReader _reader;
    private readonly char[] _buffer = new char[1024];
    private int _position, _length;

    public BoundedLineReader(TextReader reader) => _reader = reader;

    /// <summary>
    /// The next line, or a null line at the end of the stream. A line longer than
    /// <paramref name="maxChars"/> comes back empty with <c>TooLong</c> set.
    /// </summary>
    public async Task<(string? Line, bool TooLong)> ReadLineAsync(int maxChars, CancellationToken cancel = default)
    {
        var line = new StringBuilder();
        bool tooLong = false, any = false;
        while (true)
        {
            if (_position == _length)
            {
                _length = await _reader.ReadAsync(_buffer.AsMemory(), cancel);
                _position = 0;
                if (_length == 0)
                    return any ? (tooLong ? "" : line.ToString(), tooLong) : (null, false);
            }

            char c = _buffer[_position++];
            any = true;
            if (c == '\n') return (tooLong ? "" : line.ToString(), tooLong);
            if (c is '\r' or '\0') continue;
            if (tooLong) continue;
            if (line.Length >= maxChars) { tooLong = true; line.Clear(); continue; }
            line.Append(c);
        }
    }
}
