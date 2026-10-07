namespace Weir.Client;

/// <summary>What a streamed response reported at one point in time.</summary>
public enum WeirStreamEventKind
{
    /// <summary>
    /// A result set has begun. A procedure that sends its rows in batches produces one of these per
    /// batch, so this is what says "a new batch started" before any of its rows arrive - and it arrives
    /// even for a batch that turns out to have no rows.
    /// </summary>
    ResultSetStarted,

    /// <summary>A row has been read in full; <see cref="WeirStreamEvent.Json"/> holds its raw object.</summary>
    Row,
}

/// <summary>
/// One step of a response that is still arriving, handed over the moment its bytes are complete.
/// Produced by <see cref="WeirClient.StreamEventsAsync"/>.
/// <para>
/// The row travels as its raw UTF-8 JSON rather than as a parsed element: the row shape is the
/// procedure's, which no client-side model can be sure of in advance, and holding a
/// <see cref="System.Text.Json.JsonDocument"/> per row would keep the whole stream's memory alive. A
/// caller deserializes the row, prints it, or ignores it, as it likes.
/// </para>
/// </summary>
public readonly struct WeirStreamEvent
{
    /// <summary>Creates an event.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="resultSetIndex">Zero-based index of the result set this belongs to.</param>
    /// <param name="json">The row's raw JSON object, or empty for <see cref="WeirStreamEventKind.ResultSetStarted"/>.</param>
    internal WeirStreamEvent(WeirStreamEventKind kind, int resultSetIndex, ReadOnlyMemory<byte> json)
    {
        Kind = kind;
        ResultSetIndex = resultSetIndex;
        Json = json;
    }

    /// <summary>What happened.</summary>
    public WeirStreamEventKind Kind { get; }

    /// <summary>
    /// Zero-based index of the result set this belongs to. A streaming procedure that sends batches
    /// produces one index per batch.
    /// </summary>
    public int ResultSetIndex { get; }

    /// <summary>The row's raw JSON object, for <see cref="WeirStreamEventKind.Row"/>; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Json { get; }
}
