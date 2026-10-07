using System.Text.Json;

namespace Weir.Client;

/// <summary>
/// Reads a Weir response envelope out of a stream that is still arriving and reports each row the moment
/// its bytes are complete, which is what lets <see cref="WeirClient.StreamEventsAsync"/> hand data to a
/// caller before the gateway has finished writing the body.
/// <para>
/// The envelope is <c>{ "data": [[ {row}, ... ], ... ], ... }</c>, so the rows of interest are the objects
/// inside the inner arrays of <c>data</c>. Parsing is incremental: bytes are fed in as they arrive and a
/// <see cref="Utf8JsonReader"/> backed by a <see cref="JsonReaderState"/> resumes across chunk boundaries,
/// so a row split between two network reads still comes out whole. The reader is a ref struct and never
/// crosses an <c>await</c> - <see cref="Feed"/> is synchronous and the caller awaits only the socket.
/// </para>
/// </summary>
internal sealed class EnvelopeStreamParser : IDisposable
{
    /// <summary>The bytes received but not yet consumed by the reader.</summary>
    private byte[] _buffer;

    /// <summary>How many bytes of <see cref="_buffer"/> are live.</summary>
    private int _filled;

    /// <summary>The reader state carried across feeds (container stack included), so depth survives a chunk split.</summary>
    private JsonReaderState _state;

    /// <summary>Set when the <c>data</c> property was seen and its array is expected next.</summary>
    private bool _expectDataArray;

    /// <summary>True once the <c>data</c> array has begun.</summary>
    private bool _inData;

    /// <summary>The depth of the <c>data</c> array.</summary>
    private int _dataDepth = -1;

    /// <summary>The depth of a result-set array (an element of <c>data</c>).</summary>
    private int _setDepth = -1;

    /// <summary>The depth of a row object (an element of a result set); -1 until a set is seen.</summary>
    private int _rowDepth = -1;

    /// <summary>Index of the result set being read; -1 until the first one begins.</summary>
    private int _setIndex = -1;

    /// <summary>The raw bytes of the row currently being captured, or null when none is in progress.</summary>
    private MemoryStream? _row;

    /// <summary>Creates a parser.</summary>
    /// <param name="bufferSize">Initial buffer size in bytes; it grows when a value outruns it.</param>
    public EnvelopeStreamParser(int bufferSize = 64 * 1024) => _buffer = new byte[bufferSize];

    /// <inheritdoc />
    public void Dispose()
    {
        _row?.Dispose();
        _row = null;
    }

    /// <summary>Feeds one chunk of the body and returns whatever completed within it.</summary>
    /// <param name="chunk">The bytes read; empty on the final call.</param>
    /// <param name="isFinal">True when no more bytes follow.</param>
    /// <returns>The events completed by this chunk, in order.</returns>
    public List<WeirStreamEvent> Feed(ReadOnlySpan<byte> chunk, bool isFinal)
    {
        Append(chunk);

        var events = new List<WeirStreamEvent>();
        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, 0, _filled), isFinal, _state);
        var previous = 0;
        while (reader.Read())
        {
            var consumed = (int)reader.BytesConsumed;

            // A row starts here. Its capture begins at the object itself, not at the previous token: the
            // bytes in between are the comma that separates rows (JSON punctuation is not a token of its
            // own), so taking the whole range would put that comma inside the row and break its JSON.
            if (reader.TokenType == JsonTokenType.StartObject && _inData && _rowDepth >= 0 &&
                reader.CurrentDepth == _rowDepth)
            {
                _row?.Dispose();
                _row = new MemoryStream(256);
                _row.WriteByte((byte)'{');
            }
            else if (_row is not null)
            {
                _row.Write(_buffer, previous, consumed - previous);
            }

            Track(ref reader, events);
            previous = consumed;
        }

        _state = reader.CurrentState;

        // Whatever the reader did not consume is too little to complete a value: keep it for the next chunk
        // by moving it to the front of the buffer (an overlapping move that Span.CopyTo handles).
        var remaining = _filled - previous;
        if (remaining > 0)
        {
            _buffer.AsSpan(previous, remaining).CopyTo(_buffer);
        }

        _filled = remaining;
        return events;
    }

    /// <summary>Tracks the structure as tokens are consumed and reports completed rows and sets.</summary>
    /// <param name="reader">The reader, positioned on the current token.</param>
    /// <param name="events">The list to add completed events to.</param>
    private void Track(ref Utf8JsonReader reader, List<WeirStreamEvent> events)
    {
        if (reader.TokenType == JsonTokenType.PropertyName)
        {
            // Only the envelope's own "data" counts, and only before the rows have started: a "data" seen
            // later belongs to some row, not to the envelope.
            _expectDataArray = !_inData && reader.CurrentDepth == 1 && reader.ValueTextEquals("data");
            return;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            if (_expectDataArray)
            {
                _inData = true;
                _dataDepth = reader.CurrentDepth;
            }
            else if (_inData && reader.CurrentDepth == _dataDepth + 1)
            {
                _setDepth = reader.CurrentDepth;
                _rowDepth = _setDepth + 1;
                _setIndex++;
                events.Add(new WeirStreamEvent(WeirStreamEventKind.ResultSetStarted, _setIndex, default));
            }

            _expectDataArray = false;
            return;
        }

        // Anything else means the "data" property was not an array after all (null, say), so a later array
        // in the body - one inside "messages" - must not be mistaken for the data array.
        _expectDataArray = false;

        if (reader.TokenType == JsonTokenType.EndObject && _row is not null && reader.CurrentDepth == _rowDepth)
        {
            events.Add(new WeirStreamEvent(WeirStreamEventKind.Row, _setIndex, _row.ToArray()));
            _row.Dispose();
            _row = null;
        }
    }

    /// <summary>Appends a chunk to the buffer, growing it when the unconsumed tail needs more room.</summary>
    /// <param name="chunk">The bytes to append.</param>
    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        if (_filled + chunk.Length > _buffer.Length)
        {
            var size = _buffer.Length;
            while (size < _filled + chunk.Length)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }

        chunk.CopyTo(_buffer.AsSpan(_filled));
        _filled += chunk.Length;
    }
}
