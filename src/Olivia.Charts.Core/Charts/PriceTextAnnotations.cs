using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>An authored text note at a chart-local DIP position, not a market-data anchor.</summary>
public record PriceTextNote(long Id, double X, double Y, string Text);

/// <summary>
/// An opaque, single-use edit token. Only the exact instance returned by this store is accepted;
/// a record copy, even with identical values, cannot authorize a commit or cancellation.
/// </summary>
public sealed record PriceTextAnnotationDraft(long Revision, long? NoteId, double X, double Y,
    string OriginalText);

/// <summary>
/// Bounded, in-memory authored text independent of chart data or source versions. Coordinates are
/// chart-local device-independent pixels; the UI owns placement, rendering and edit-mode policy.
/// Every public operation is serialized. Items is an immutable snapshot, but separate property
/// reads are not one atomic snapshot of the whole store. A later operation may supersede a draft.
/// Revision advances when items, selection or draft state changes. Rejected requests do not advance it.
/// </summary>
public sealed class PriceTextAnnotations
{
    public const int MaximumNotes = 32;
    /// <summary>The maximum number of UTF-16 code units, matching string.Length.</summary>
    public const int MaximumTextLength = 1024;

    private readonly object _gate = new();
    private ImmutableArray<PriceTextNote> _items = ImmutableArray<PriceTextNote>.Empty;
    private long _revision;
    private long _lastId;
    private long? _selectedId;
    private PriceTextAnnotationDraft? _draft;

    public ImmutableArray<PriceTextNote> Items { get { lock (_gate) return _items; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public long? SelectedId { get { lock (_gate) return _selectedId; } }
    public PriceTextAnnotationDraft? Draft { get { lock (_gate) return _draft; } }

    /// <summary>
    /// Starts a new note and clears selection. Full capacity returns null without changing anything,
    /// including an existing edit. Invalid coordinates throw without changing state.
    /// </summary>
    public PriceTextAnnotationDraft? BeginNew(double x, double y)
    {
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));
        lock (_gate)
        {
            if (_items.Length >= MaximumNotes) return null;
            var revision = checked(_revision + 1);
            var draft = new PriceTextAnnotationDraft(revision, null, x, y, string.Empty);
            _revision = revision;
            _selectedId = null;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>Starts an edit and selects the note. A missing ID leaves all state unchanged.</summary>
    public PriceTextAnnotationDraft? BeginEdit(long id)
    {
        lock (_gate)
        {
            var index = FindIndex(id);
            if (index < 0) return null;
            var note = _items[index];
            var revision = checked(_revision + 1);
            var draft = new PriceTextAnnotationDraft(revision, note.Id, note.X, note.Y, note.Text);
            _revision = revision;
            _selectedId = note.Id;
            _draft = draft;
            return draft;
        }
    }

    /// <summary>
    /// Commits the current exact token once and selects the resulting note. Stale or fabricated tokens
    /// return false, before text validation. Invalid current text throws ArgumentException and retains
    /// the draft and all other state. Valid text is stored exactly, without trimming or normalization.
    /// </summary>
    public bool Commit(PriceTextAnnotationDraft draft, string text)
    {
        lock (_gate)
        {
            if (!IsCurrent(draft)) return false;
            ValidateText(text);
            var revision = checked(_revision + 1);
            ImmutableArray<PriceTextNote> items;
            long id;
            if (draft.NoteId is { } existingId)
            {
                var index = FindIndex(existingId);
                if (index < 0) return false;
                id = existingId;
                items = _items.SetItem(index, new PriceTextNote(id, draft.X, draft.Y, text));
            }
            else
            {
                if (_items.Length >= MaximumNotes) return false;
                id = checked(_lastId + 1);
                items = _items.Add(new PriceTextNote(id, draft.X, draft.Y, text));
            }
            _items = items;
            _lastId = Math.Max(_lastId, id);
            _selectedId = id;
            _draft = null;
            _revision = revision;
            return true;
        }
    }

    /// <summary>Cancels only the current exact token, retaining selection and committed notes.</summary>
    public bool Cancel(PriceTextAnnotationDraft draft)
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
            var items = index >= 0 ? _items.RemoveAt(index) : _items;
            _items = items;
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
            _items = ImmutableArray<PriceTextNote>.Empty;
            _selectedId = null;
            _draft = null;
            _revision = revision;
        }
    }

    private bool IsCurrent(PriceTextAnnotationDraft? draft) =>
        draft is not null && ReferenceEquals(draft, _draft) && draft.Revision == _revision;

    private int FindIndex(long id)
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index].Id == id) return index;
        return -1;
    }

    private static void ValidateCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "A chart position must be finite and nonnegative.");
    }

    private static void ValidateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ArgumentException($"Text must contain non-whitespace content and at most {MaximumTextLength} UTF-16 code units.", nameof(text));
        foreach (var character in text)
            if (char.IsControl(character) && character is not ('\t' or '\r' or '\n'))
                throw new ArgumentException("Text cannot contain controls other than tab, carriage return and line feed.", nameof(text));
    }
}
