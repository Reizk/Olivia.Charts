using System.Collections.Immutable;
using Olivia.Core.Imaging;

namespace Olivia.Core.Charts;

/// <summary>An authored image at a chart-local DIP position, not a market-data anchor.</summary>
public sealed record PriceImageNote(long Id, double X, double Y, RasterImage Image);

/// <summary>
/// An opaque, single-use edit token. Only the exact instance returned by this store is accepted;
/// a value-equal record copy cannot authorize a commit or cancellation.
/// </summary>
public sealed record PriceImageAnnotationDraft(long Revision, long? NoteId, double X, double Y,
    RasterImage? OriginalImage);

/// <summary>
/// Bounded, in-memory authored images independent of chart data or source versions. Coordinates are
/// chart-local device-independent pixels; the UI owns placement, rendering and edit-mode policy.
/// Every public operation is serialized. Items is an immutable snapshot, but separate property
/// reads are not one atomic snapshot of the whole store. A later operation may supersede a draft.
/// Revision advances when items, selection or draft state changes. Rejected requests do not advance it.
/// No file paths, source metadata, I/O or native rendering resources are retained.
/// </summary>
public sealed class PriceImageAnnotations
{
    public const int MaximumNotes = 16;
    /// <summary>The aggregate committed image budget: 1,048,576 RGBA8 pixels (4 MiB).</summary>
    public const int MaximumPixelCount = 1_048_576;

    private readonly object _gate = new();
    private ImmutableArray<PriceImageNote> _items = ImmutableArray<PriceImageNote>.Empty;
    private int _pixelCount;
    private long _revision;
    private long _lastId;
    private long? _selectedId;
    private PriceImageAnnotationDraft? _draft;

    public ImmutableArray<PriceImageNote> Items { get { lock (_gate) return _items; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public long? SelectedId { get { lock (_gate) return _selectedId; } }
    public PriceImageAnnotationDraft? Draft { get { lock (_gate) return _draft; } }

    /// <summary>
    /// Starts a new note and clears selection. Exhausted note or pixel capacity returns null without
    /// changing anything, including an existing edit. Invalid coordinates throw without changing state.
    /// </summary>
    public PriceImageAnnotationDraft? BeginNew(double x, double y)
    {
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));
        lock (_gate)
        {
            if (_items.Length >= MaximumNotes || _pixelCount >= MaximumPixelCount) return null;
            var revision = checked(_revision + 1);
            var draft = new PriceImageAnnotationDraft(revision, null, x, y, null);
            _revision = revision;
            _selectedId = null;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>Starts an edit and selects the note. A missing ID leaves all state unchanged.</summary>
    public PriceImageAnnotationDraft? BeginEdit(long id)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            if (index < 0) return null;
            var note = _items[index];
            var revision = checked(_revision + 1);
            var draft = new PriceImageAnnotationDraft(revision, note.Id, note.X, note.Y, note.Image);
            _revision = revision;
            _selectedId = note.Id;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>
    /// Commits the current exact token once and selects the resulting note. Stale or fabricated tokens
    /// return false before image validation. A null image for the current token throws; capacity or
    /// aggregate pixel budget failures return false. Both retain the draft and all other state.
    /// A replacement releases its previous image's pixels before checking the aggregate budget.
    /// RasterImage is already validated and immutable; no mutable storage is shared with the caller.
    /// </summary>
    public bool Commit(PriceImageAnnotationDraft draft, RasterImage image)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            ArgumentNullException.ThrowIfNull(image);
            var imagePixels = PixelCount(image);
            var index = draft.NoteId is { } existingId ? FindIndex(existingId) : -1;
            if (draft.NoteId is not null && index < 0) return false;
            if (index < 0 && _items.Length >= MaximumNotes) return false;
            var remainingPixels = _pixelCount - (index >= 0 ? PixelCount(_items[index].Image) : 0);
            if (imagePixels > MaximumPixelCount - remainingPixels) return false;

            var revision = checked(_revision + 1);
            var id = index >= 0 ? _items[index].Id : checked(_lastId + 1);
            var note = new PriceImageNote(id, draft.X, draft.Y, image);
            var items = index >= 0 ? _items.SetItem(index, note) : _items.Add(note);
            _items = items;
            _pixelCount = remainingPixels + imagePixels;
            _lastId = Math.Max(_lastId, id);
            _selectedId = id;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels only the current exact token, retaining selection and committed notes.</summary>
    public bool Cancel(PriceImageAnnotationDraft draft)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            var revision = checked(_revision + 1);
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Selects an existing note and cancels any draft. A missing ID changes nothing.</summary>
    public bool Select(long id)
    {
        lock (_gate)
        {
            if (FindIndex(id) < 0) return false;
            if (_selectedId == id && _draft is null) return true;
            var revision = checked(_revision + 1);
            _selectedId = id;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Clears selection and cancels any draft, retaining notes and IDs. Returns whether state changed.</summary>
    public bool ClearSelection()
    {
        lock (_gate)
        {
            if (_selectedId is null && _draft is null) return false;
            var revision = checked(_revision + 1);
            _selectedId = null;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels any draft and removes the selected note, if any. Returns whether one was removed.</summary>
    public bool RemoveSelected()
    {
        lock (_gate)
        {
            var index = _selectedId is { } id ? FindIndex(id) : -1;
            if (index < 0 && _draft is null) return false;
            var revision = checked(_revision + 1);
            var pixels = _pixelCount - (index >= 0 ? PixelCount(_items[index].Image) : 0);
            var items = index >= 0 ? _items.RemoveAt(index) : _items;
            _items = items;
            _pixelCount = pixels;
            _selectedId = null;
            _draft = null;
            _revision = revision;
            return index >= 0;
        }
    }

    /// <summary>Clears notes, selection and drafts without reusing IDs during this store's lifetime.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.IsEmpty && _selectedId is null && _draft is null) return;
            var revision = checked(_revision + 1);
            _items = ImmutableArray<PriceImageNote>.Empty;
            _pixelCount = 0;
            _selectedId = null;
            _draft = null;
            _revision = revision;
        }
    }

    private bool IsCurrent(PriceImageAnnotationDraft? draft) =>
        draft is not null && ReferenceEquals(draft, _draft) && draft.Revision == _revision;

    private int FindIndex(long id)
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index].Id == id) return index;
        return -1;
    }

    private static int PixelCount(RasterImage image) => image.Width * image.Height;

    private static void ValidateCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "A chart position must be finite and nonnegative.");
    }
}

/// <summary>
/// Explicit selected-stream import for chart annotations. Reads actual bytes rather than trusting
/// Length or seek claims, leaves the stream open, and delegates all format validation to the existing
/// bounded PNG subset. No native decoder, path, metadata or stream is retained in the result.
/// </summary>
public static class PriceAnnotationImageImport
{
    public static async Task<RasterImage> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        // One sentinel byte distinguishes exactly-at-limit streams from oversized input. Even a
        // nonseekable or infinite stream cannot make this importer read more than the stated bound.
        var encoded = new byte[PngRasterCodec.MaximumEncodedBytes + 1];
        var count = 0;
        while (count < encoded.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(encoded.AsMemory(count, Math.Min(81_920, encoded.Length - count)),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            count += read;
        }
        if (count > PngRasterCodec.MaximumEncodedBytes)
            throw new InvalidDataException("The PNG exceeds the encoded byte budget.");
        cancellationToken.ThrowIfCancellationRequested();
        var image = PngRasterCodec.Decode(encoded.AsSpan(0, count));
        // The existing synchronous decoder is bounded. Never deliver its result after cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }
}
