using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceTextAnnotationTests
{
    [Fact]
    public void NewDraftHasNoCommittedNoteUntilExactTextIsCommitted()
    {
        var store = new PriceTextAnnotations();
        Assert.Empty(store.Items);
        Assert.Equal(0, store.Revision);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);

        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(12.5, 23.75));
        Assert.Equal(1, draft.Revision);
        Assert.Equal(store.Revision, draft.Revision);
        Assert.Null(draft.NoteId);
        Assert.Equal(12.5, draft.X);
        Assert.Equal(23.75, draft.Y);
        Assert.Equal(string.Empty, draft.OriginalText);
        Assert.Same(draft, store.Draft);
        Assert.Empty(store.Items);
        Assert.True(store.Commit(draft, "  Author's note  "));

        var note = Assert.Single(store.Items);
        Assert.Equal(new PriceTextNote(1, 12.5, 23.75, "  Author's note  "), note);
        Assert.Equal(note.Id, store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Equal(2, store.Revision);
        Assert.False(store.Commit(draft, "duplicate"));
        Assert.False(store.Cancel(draft));
        Assert.Equal(2, store.Revision);
    }

    [Fact]
    public void EditPreservesIdentityPositionOrderAndOldSnapshots()
    {
        var store = new PriceTextAnnotations();
        var first = Add(store, 10, 20, "first");
        var second = Add(store, 30, 40, "second");
        var old = store.Items;
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(first.Id));
        Assert.Equal(first.Id, draft.NoteId);
        Assert.Equal(first.X, draft.X);
        Assert.Equal(first.Y, draft.Y);
        Assert.Equal(first.Text, draft.OriginalText);
        Assert.Equal(first.Id, store.SelectedId);
        Assert.Equal(old, store.Items);

        Assert.True(store.Commit(draft, "edited"));
        Assert.Equal(new[] { first with { Text = "edited" }, second }, store.Items.ToArray());
        Assert.Equal(new[] { first, second }, old.ToArray());
        Assert.Equal("first", old[0].Text);
        Assert.Equal(first.Id, store.SelectedId);
        Assert.Null(store.Draft);
    }

    [Fact]
    public void CancelNewAndEditNeverChangeCommittedTextAndConsumeTheirTokens()
    {
        var store = new PriceTextAnnotations();
        var note = Add(store, 1, 2, "original");
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        Assert.True(store.Cancel(edit));
        Assert.Equal(note.Id, store.SelectedId);
        Assert.Equal(note, Assert.Single(store.Items));
        Assert.False(store.Commit(edit, "late edit"));
        Assert.False(store.Cancel(edit));

        var create = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(3, 4));
        Assert.Null(store.SelectedId);
        Assert.True(store.Cancel(create));
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Equal(note, Assert.Single(store.Items));
        Assert.False(store.Commit(create, "late creation"));
    }

    [Fact]
    public void NewerDraftsSupersedeBothNewAndEditTokens()
    {
        var store = new PriceTextAnnotations();
        var note = Add(store, 1, 2, "kept");
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        var create = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(3, 4));
        var latest = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        var revision = store.Revision;
        foreach (var stale in new[] { edit, create })
        {
            Assert.False(store.Commit(stale, "stale"));
            Assert.False(store.Cancel(stale));
        }
        Assert.Equal(revision, store.Revision);
        Assert.Same(latest, store.Draft);
        Assert.True(store.Commit(latest, "current"));
        Assert.Equal("current", Assert.Single(store.Items).Text);
    }

    [Fact]
    public void FabricatedValueEqualAndForeignTokensCannotCommitOrCancel()
    {
        var store = new PriceTextAnnotations();
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(1, 2));
        var otherStore = new PriceTextAnnotations();
        var foreign = Assert.IsType<PriceTextAnnotationDraft>(otherStore.BeginNew(1, 2));
        var copy = draft with { };
        Assert.Equal(draft, copy);
        Assert.NotSame(draft, copy);
        Assert.Equal(draft, foreign);
        foreach (var rejected in new[] { copy, foreign,
            new PriceTextAnnotationDraft(draft.Revision, null, 1, 2, string.Empty),
            draft with { X = 500 }, draft with { Revision = draft.Revision + 1 } })
        {
            Assert.False(store.Commit(rejected, "not authorized"));
            Assert.False(store.Cancel(rejected));
        }
        Assert.False(store.Commit(null!, "not authorized"));
        Assert.False(store.Cancel(null!));
        Assert.Same(draft, store.Draft);
        Assert.Empty(store.Items);
        Assert.Equal(1, store.Revision);
        Assert.True(store.Commit(draft, "authorized"));
    }

    [Fact]
    public void FullCapacityRejectsNewWithoutDisturbingSelectionOrActiveEdit()
    {
        var store = new PriceTextAnnotations();
        Assert.Equal(32, PriceTextAnnotations.MaximumNotes);
        for (var index = 0; index < PriceTextAnnotations.MaximumNotes; index++)
            Add(store, index, index + .5, $"note {index}");
        var first = store.Items[0];
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(first.Id));
        var snapshot = store.Items;
        var revision = store.Revision;
        Assert.Null(store.BeginNew(100, 100));
        Assert.Equal(snapshot, store.Items);
        Assert.Equal(revision, store.Revision);
        Assert.Equal(first.Id, store.SelectedId);
        Assert.Same(edit, store.Draft);
        Assert.True(store.Commit(edit, "edit at full capacity"));
        Assert.Equal(PriceTextAnnotations.MaximumNotes, store.Items.Length);
        Assert.True(store.RemoveSelected());
        var replacement = Add(store, 100, 100, "replacement");
        Assert.Equal(PriceTextAnnotations.MaximumNotes, store.Items.Length);
        Assert.True(replacement.Id > snapshot[^1].Id);
    }

    [Fact]
    public void MissingIdsAndRepeatedSelectionAreNonMutating()
    {
        var store = new PriceTextAnnotations();
        var note = Add(store, 1, 2, "kept");
        var revision = store.Revision;
        Assert.True(store.Select(note.Id));
        Assert.Equal(revision, store.Revision);
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        revision = store.Revision;
        foreach (var missing in new[] { long.MinValue, 0, note.Id + 1, long.MaxValue })
        {
            Assert.False(store.Select(missing));
            Assert.Null(store.BeginEdit(missing));
        }
        Assert.Equal(revision, store.Revision);
        Assert.Same(draft, store.Draft);
        Assert.Equal(note.Id, store.SelectedId);
        Assert.Equal(note, Assert.Single(store.Items));
    }

    [Fact]
    public void SelectingEvenTheSameExistingNoteCancelsAnActiveDraft()
    {
        var store = new PriceTextAnnotations();
        var first = Add(store, 1, 2, "first");
        var second = Add(store, 3, 4, "second");
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(first.Id));
        Assert.True(store.Select(first.Id));
        Assert.False(store.Commit(edit, "stale"));
        Assert.Null(store.Draft);
        Assert.Equal(first.Id, store.SelectedId);

        var create = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(5, 6));
        Assert.True(store.Select(second.Id));
        Assert.False(store.Cancel(create));
        Assert.Null(store.Draft);
        Assert.Equal(second.Id, store.SelectedId);
        Assert.Equal(new[] { first, second }, store.Items.ToArray());
    }

    [Fact]
    public void ClearSelectionInvalidatesEditingAndNewDraftsWithoutRemovingNotesOrReusingIds()
    {
        var store = new PriceTextAnnotations();
        Assert.False(store.ClearSelection());
        Assert.Equal(0, store.Revision);
        var note = Add(store, 1, 2, "kept");
        var snapshot = store.Items;
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        var revision = store.Revision;
        Assert.True(store.ClearSelection());
        Assert.Equal(revision + 1, store.Revision);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Equal(snapshot, store.Items);
        Assert.False(store.Commit(edit, "stale"));
        Assert.False(store.Cancel(edit));
        Assert.False(store.RemoveSelected());
        Assert.False(store.ClearSelection());
        Assert.Equal(revision + 1, store.Revision);

        var create = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(3, 4));
        Assert.Null(store.SelectedId);
        Assert.True(store.ClearSelection());
        Assert.False(store.Commit(create, "stale"));
        Assert.Equal(snapshot, store.Items);
        Assert.Null(store.Draft);
        Assert.True(store.Select(note.Id));
        Assert.True(store.ClearSelection());
        Assert.Null(store.SelectedId);
        Assert.False(store.RemoveSelected());
        Assert.Equal(note, Assert.Single(store.Items));
        var next = Add(store, 5, 6, "next");
        Assert.Equal(note.Id + 1, next.Id);
    }

    [Fact]
    public void RemovingSelectionCancelsEditAndLeavesOldSnapshotUnchanged()
    {
        var store = new PriceTextAnnotations();
        var first = Add(store, 1, 2, "first");
        var second = Add(store, 3, 4, "second");
        var old = store.Items;
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(first.Id));
        Assert.True(store.RemoveSelected());
        Assert.Equal(second, Assert.Single(store.Items));
        Assert.Equal(new[] { first, second }, old.ToArray());
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.False(store.Commit(edit, "resurrect"));
        var revision = store.Revision;
        Assert.False(store.RemoveSelected());
        Assert.Equal(revision, store.Revision);
        var replacement = Add(store, 1, 2, "replacement");
        Assert.True(replacement.Id > second.Id);
    }

    [Fact]
    public void RemoveWithoutSelectionStillCancelsANewDraft()
    {
        var store = new PriceTextAnnotations();
        var note = Add(store, 1, 2, "kept");
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(3, 4));
        var revision = store.Revision;
        Assert.False(store.RemoveSelected());
        Assert.Equal(revision + 1, store.Revision);
        Assert.Null(store.Draft);
        Assert.Null(store.SelectedId);
        Assert.False(store.Commit(draft, "stale"));
        Assert.Equal(note, Assert.Single(store.Items));
    }

    [Fact]
    public void ClearCancelsDraftsRetainsOldSnapshotsAndNeverReusesIds()
    {
        var store = new PriceTextAnnotations();
        store.Clear();
        Assert.Equal(0, store.Revision);
        var first = Add(store, 1, 2, "first");
        var second = Add(store, 3, 4, "second");
        var old = store.Items;
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(first.Id));
        store.Clear();
        Assert.Empty(store.Items);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.False(store.Commit(edit, "stale"));
        Assert.Equal(new[] { first, second }, old.ToArray());
        var revision = store.Revision;
        store.Clear();
        Assert.Equal(revision, store.Revision);
        var create = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(5, 6));
        store.Clear();
        Assert.False(store.Commit(create, "stale"));
        Assert.Null(store.Draft);
        var next = Add(store, 7, 8, "next");
        Assert.True(next.Id > second.Id);
    }

    [Fact]
    public void InvalidCoordinatesLeaveExistingDraftSelectionItemsAndRevisionIntact()
    {
        var store = new PriceTextAnnotations();
        var note = Add(store, 1, 2, "kept");
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        var items = store.Items;
        var revision = store.Revision;
        foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1d, -double.Epsilon })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => store.BeginNew(invalid, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.BeginNew(0, invalid));
            Assert.Equal(revision, store.Revision);
            Assert.Equal(items, store.Items);
            Assert.Equal(note.Id, store.SelectedId);
            Assert.Same(draft, store.Draft);
        }
        Assert.True(store.Commit(draft, "still usable"));
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(.125, .25)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void FiniteNonnegativeCoordinatesAreNotClampedOrNormalized(double x, double y)
    {
        var store = new PriceTextAnnotations();
        var note = Add(store, x, y, "position");
        Assert.Equal(x, note.X);
        Assert.Equal(y, note.Y);
    }

    [Fact]
    public void InvalidTextIsAtomicForBothNewAndEditAndDoesNotConsumeTheDraft()
    {
        var store = new PriceTextAnnotations();
        var create = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(1, 2));
        AssertInvalidTextLeavesState(store, create);
        Assert.True(store.Commit(create, "original"));
        var note = Assert.Single(store.Items);
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        AssertInvalidTextLeavesState(store, edit);
        Assert.True(store.Commit(edit, "corrected"));
        Assert.Equal("corrected", Assert.Single(store.Items).Text);
    }

    [Fact]
    public void StaleTokensAreRejectedBeforeTextValidation()
    {
        var store = new PriceTextAnnotations();
        var stale = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(1, 2));
        var current = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(3, 4));
        Assert.False(store.Commit(stale, null!));
        Assert.False(store.Commit(current with { }, "\0"));
        Assert.Same(current, store.Draft);
        Assert.True(store.Commit(current, "current"));
    }

    [Fact]
    public void MultilineUnicodeTabsAndMaximumLengthArePreservedExactly()
    {
        var store = new PriceTextAnnotations();
        const string text = "  支撐位 🧭\tRésumé e\u0301\r\n第二行\n العربية\r終わり  ";
        var note = Add(store, 1, 2, text);
        Assert.Equal(text, note.Text);
        var edit = Assert.IsType<PriceTextAnnotationDraft>(store.BeginEdit(note.Id));
        Assert.Equal(text, edit.OriginalText);
        Assert.Equal(1024, PriceTextAnnotations.MaximumTextLength);
        var maximum = "🧭" + new string('界', PriceTextAnnotations.MaximumTextLength - 2);
        Assert.Equal(PriceTextAnnotations.MaximumTextLength, maximum.Length);
        Assert.True(store.Commit(edit, maximum));
        Assert.Equal(maximum, Assert.Single(store.Items).Text);
    }

    [Fact]
    public void EveryControlOtherThanTabCarriageReturnAndLineFeedIsRejectedAtomically()
    {
        var store = new PriceTextAnnotations();
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(1, 2));
        var revision = store.Revision;
        for (var value = 0; value <= char.MaxValue; value++)
        {
            var character = (char)value;
            if (!char.IsControl(character) || character is '\t' or '\r' or '\n') continue;
            Assert.Throws<ArgumentException>(() => store.Commit(draft, $"a{character}b"));
        }
        Assert.Empty(store.Items);
        Assert.Equal(revision, store.Revision);
        Assert.Same(draft, store.Draft);
        Assert.True(store.Commit(draft, "a\tb\rc\nd"));
    }

    [Fact]
    public void ConcurrentCommitOfOneTokenCreatesExactlyOneNote()
    {
        var store = new PriceTextAnnotations();
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(1, 2));
        var successes = 0;
        Parallel.For(0, 64, index =>
        {
            if (store.Commit(draft, $"attempt {index}")) Interlocked.Increment(ref successes);
        });
        Assert.Equal(1, successes);
        Assert.Single(store.Items);
        Assert.Equal(2, store.Revision);
        Assert.Null(store.Draft);
    }

    [Fact]
    public void ConcurrentOperationsRetainBoundUniqueIdsAndImmutableSnapshots()
    {
        var store = new PriceTextAnnotations();
        var first = Add(store, 1, 2, "immutable");
        var snapshot = store.Items;
        Parallel.For(0, 256, index =>
        {
            var draft = store.BeginNew(index, index);
            if (draft is not null) store.Commit(draft, $"note {index}");
            var observed = store.Items;
            Assert.InRange(observed.Length, 1, PriceTextAnnotations.MaximumNotes);
            Assert.Equal(observed.Length, observed.Select(note => note.Id).Distinct().Count());
        });
        while (store.Items.Length < PriceTextAnnotations.MaximumNotes)
            Add(store, 0, 0, "fill");
        Assert.Equal(PriceTextAnnotations.MaximumNotes, store.Items.Length);
        Assert.Equal(first, Assert.Single(snapshot));
        Assert.Equal(store.Items.Length, store.Items.Select(note => note.Id).Distinct().Count());
        Assert.Null(store.BeginNew(0, 0));
    }

    private static PriceTextNote Add(PriceTextAnnotations store, double x, double y, string text)
    {
        var draft = Assert.IsType<PriceTextAnnotationDraft>(store.BeginNew(x, y));
        Assert.True(store.Commit(draft, text));
        return store.Items[^1];
    }

    private static void AssertInvalidTextLeavesState(PriceTextAnnotations store, PriceTextAnnotationDraft draft)
    {
        var items = store.Items;
        var revision = store.Revision;
        var selection = store.SelectedId;
        foreach (var invalid in new string?[] { null, string.Empty, " \t\r\n", "\u2003\u00a0",
            new string('x', PriceTextAnnotations.MaximumTextLength + 1), "bad\0text", "bad\u007ftext", "bad\u0085text" })
        {
            Assert.Throws<ArgumentException>(() => store.Commit(draft, invalid!));
            Assert.Equal(items, store.Items);
            Assert.Equal(revision, store.Revision);
            Assert.Equal(selection, store.SelectedId);
            Assert.Same(draft, store.Draft);
        }
    }
}
