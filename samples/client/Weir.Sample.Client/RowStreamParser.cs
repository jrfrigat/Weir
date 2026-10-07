using System.Text.Json;

namespace Weir.Sample.Client;

/// <summary>What the parser found while a Weir response body was still arriving.</summary>
internal enum StreamEventKind
{
    /// <summary>A new result set has begun. For the demo's streaming procedure this is one batch.</summary>
    SetStarted,

    /// <summary>One row has been read in full; <see cref="StreamEvent.Json"/> holds its raw object.</summary>
    Row,
}

/// <summary>One thing completed within a chunk of the body.</summary>
/// <param name="Kind">What completed.</param>
/// <param name="Json">The row's raw JSON object, for <see cref="StreamEventKind.Row"/>; empty otherwise.</param>
internal readonly record struct StreamEvent(StreamEventKind Kind, ReadOnlyMemory<byte> Json);

/// <summary>
/// Reads a Weir response envelope out of a stream that is still arriving and reports each row the moment
/// its bytes are complete, so a caller can print data as it comes instead of waiting for the whole body.
/// <para>
/// The envelope is <c>{ "data": [[ {row}, ... ], ... ], ... }</c>, so the rows of interest are the objects
/// inside the inner arrays of <c>data</c>. Parsing is incremental: bytes are fed in as they arrive and a
/// <see cref="Utf8JsonReader"/> backed by a <see cref="JsonReaderState"/> resumes across chunk boundaries,
/// so a row split between two network reads still comes out whole. The reader is a ref struct and never
/// crosses an <c>await</c> - <see cref="Feed"/> is synchronous and the caller awaits only the socket.
/// </para>
/// </summary>
internal sealed class RowStreamParser : IDisposable
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

    /// <summary>The raw bytes of the row currently being captured, or null when none is in progress.</summary>
    private MemoryStream? _row;

    /// <summary>Creates a parser.</summary>
    /// <param name="bufferSize">Initial buffer size in bytes; it grows when a value outruns it.</param>
    public RowStreamParser(int bufferSize = 64 * 1024) => _buffer = new byte[bufferSize];

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
    public List<StreamEvent> Feed(ReadOnlySpan<byte> chunk, bool isFinal)
    {
        Append(chunk);

        var events = new List<StreamEvent>();
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
    private void Track(ref Utf8JsonReader reader, List<StreamEvent> events)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.PropertyName:
                _expectDataArray = !_inData && reader.CurrentDepth == 1 && reader.ValueTextEquals("data");
                break;

            case JsonTokenType.StartArray:
                if (_expectDataArray)
                {
                    _inData = true;
                    _dataDepth = reader.CurrentDepth;
                    _expectDataArray = false;
                }
                else if (_inData && reader.CurrentDepth == _dataDepth + 1)
                {
                    _setDepth = reader.CurrentDepth;
                    _rowDepth = _setDepth + 1;
                    events.Add(new StreamEvent(StreamEventKind.SetStarted, default));
                }
                else
                {
                    _expectDataArray = false;
                }

                break;

            case JsonTokenType.EndObject:
                if (_row is not null && reader.CurrentDepth == _rowDepth)
                {
                    events.Add(new StreamEvent(StreamEventKind.Row, _row.ToArray()));
                    _row.Dispose();
                    _row = null;
                }

                break;
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
