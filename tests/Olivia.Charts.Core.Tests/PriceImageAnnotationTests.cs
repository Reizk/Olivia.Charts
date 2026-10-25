using System.Security.Cryptography;
using Olivia.Core.Charts;
using Olivia.Core.Imaging;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceImageAnnotationTests
{
    [Fact]
    public void NewDraftCommitsExactGeneratedRgbaPixelsAndChecksumWithoutSharingMutableStorage()
    {
        var pixels = new byte[] { 17, 29, 43, 0, 61, 73, 89, 127, 101, 113, 137, 255, 149, 157, 173, 1 };
        var expected = (byte[])pixels.Clone();
        var image = new RasterImage(2, 2, pixels);
        var store = new PriceImageAnnotations();
        Assert.Empty(store.Items);
        Assert.Equal(0, store.Revision);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(12.5, 23.75));
        Assert.Equal(1, draft.Revision);
        Assert.Equal(store.Revision, draft.Revision);
        Assert.Null(draft.NoteId);
        Assert.Null(draft.OriginalImage);
        Assert.Same(draft, store.Draft);
        Assert.Empty(store.Items);

        Array.Fill(pixels, (byte)0);
        Assert.True(store.Commit(draft, image));
        var note = Assert.Single(store.Items);
        Assert.Equal(new PriceImageNote(1, 12.5, 23.75, image), note);
        Assert.Equal(note.Id, store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Equal(2, store.Revision);
        Assert.Equal(expected, note.Image.CopyPixels());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), note.Image.PixelChecksum);
        Array.Fill(note.Image.CopyPixels(), (byte)255);
        Assert.Equal(expected, note.Image.CopyPixels());
        Assert.False(store.Commit(draft, null!));
        Assert.False(store.Cancel(draft));
        Assert.Equal(2, store.Revision);
    }

    [Fact]
    public void ReplacementRetainsIdentityPositionOrderAndImmutableOldSnapshot()
    {
        var store = new PriceImageAnnotations();
        var first = Add(store, Image(3, 2, 1), 10, 20);
        var second = Add(store, Image(2, 3, 2), 30, 40);
        var snapshot = store.Items;
        var originalPixels = first.Image.CopyPixels();
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(first.Id));
        Assert.Equal(first.Id, draft.NoteId);
        Assert.Equal(first.X, draft.X);
        Assert.Equal(first.Y, draft.Y);
        Assert.Same(first.Image, draft.OriginalImage);
        Assert.Equal(first.Id, store.SelectedId);
        Assert.Equal(snapshot, store.Items);

        var replacement = Image(4, 1, 3);
        Assert.True(store.Commit(draft, replacement));
        Assert.Equal(new[] { first with { Image = replacement }, second }, store.Items.ToArray());
        Assert.Equal(new[] { first, second }, snapshot.ToArray());
        Assert.Equal(originalPixels, snapshot[0].Image.CopyPixels());
        Assert.Equal(first.Id, store.SelectedId);
        Assert.Null(store.Draft);
    }

    [Fact]
    public void NullCurrentImageIsAtomicForBothNewAndEditAndDoesNotConsumeDraft()
    {
        var store = new PriceImageAnnotations();
        var create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(1, 2));
        AssertNullImageIsAtomic(store, create);
        Assert.True(store.Commit(create, Image()));
        var note = Assert.Single(store.Items);
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        AssertNullImageIsAtomic(store, edit);
        Assert.True(store.Commit(edit, Image(2, 1)));
        Assert.Equal(2, Assert.Single(store.Items).Image.Width);
    }

    [Fact]
    public void ForgedCopiedForeignAndNullTokensAreRejectedBeforePayloadValidation()
    {
        var store = new PriceImageAnnotations();
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(1, 2));
        var foreign = Assert.IsType<PriceImageAnnotationDraft>(new PriceImageAnnotations().BeginNew(1, 2));
        var copy = draft with { };
        Assert.Equal(draft, copy);
        Assert.NotSame(draft, copy);
        Assert.Equal(draft, foreign);
        foreach (var rejected in new[] { copy, foreign,
            new PriceImageAnnotationDraft(draft.Revision, null, 1, 2, null),
            draft with { X = double.NaN }, draft with { Revision = draft.Revision + 1 },
            draft with { NoteId = 1 }, draft with { OriginalImage = Image() } })
        {
            Assert.False(store.Commit(rejected, null!));
            Assert.False(store.Cancel(rejected));
        }
        Assert.False(store.Commit(null!, null!));
        Assert.False(store.Cancel(null!));
        Assert.Same(draft, store.Draft);
        Assert.Empty(store.Items);
        Assert.Null(store.SelectedId);
        Assert.Equal(1, store.Revision);
        Assert.True(store.Commit(draft, Image()));
    }

    [Fact]
    public void NewerDraftsSupersedeNewAndEditTokensBeforePayloadValidation()
    {
        var store = new PriceImageAnnotations();
        var note = Add(store, Image());
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        var create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(3, 4));
        var latest = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        var revision = store.Revision;
        foreach (var stale in new[] { edit, create })
        {
            Assert.False(store.Commit(stale, null!));
            Assert.False(store.Cancel(stale));
        }
        Assert.Equal(revision, store.Revision);
        Assert.Same(latest, store.Draft);
        Assert.True(store.Commit(latest, Image(3, 1)));
        Assert.Equal(3, Assert.Single(store.Items).Image.Width);
    }

    [Fact]
    public void CancellationRetainsCommittedImageAndSelectionAndConsumesOnlyExactToken()
    {
        var store = new PriceImageAnnotations();
        var note = Add(store, Image());
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        var revision = store.Revision;
        Assert.True(store.Cancel(edit));
        Assert.Equal(revision + 1, store.Revision);
        Assert.Equal(note.Id, store.SelectedId);
        Assert.Equal(note, Assert.Single(store.Items));
        Assert.False(store.Commit(edit, null!));
        Assert.False(store.Cancel(edit));
        var create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(3, 4));
        Assert.Null(store.SelectedId);
        Assert.True(store.Cancel(create));
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Equal(note, Assert.Single(store.Items));
        Assert.False(store.Commit(create, null!));
    }

    [Fact]
    public void NoteCountLimitPreservesExistingEditAndAllowsReplacementAndRecovery()
    {
        var store = new PriceImageAnnotations();
        Assert.Equal(16, PriceImageAnnotations.MaximumNotes);
        for (var index = 0; index < PriceImageAnnotations.MaximumNotes; index++)
            Add(store, Image(), index, index + .5);
        var first = store.Items[0];
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(first.Id));
        var snapshot = store.Items;
        var revision = store.Revision;
        Assert.Null(store.BeginNew(100, 100));
        AssertState(store, snapshot, revision, first.Id, edit);
        Assert.True(store.Commit(edit, Image(2, 2)));
        Assert.Equal(PriceImageAnnotations.MaximumNotes, store.Items.Length);
        Assert.True(store.RemoveSelected());
        var replacement = Add(store, Image());
        Assert.Equal(PriceImageAnnotations.MaximumNotes, store.Items.Length);
        Assert.True(replacement.Id > snapshot[^1].Id);
    }

    [Fact]
    public void ExactAggregatePixelLimitBlocksNewWithoutDisturbingEditButAllowsReplacement()
    {
        var store = new PriceImageAnnotations();
        Assert.Equal(1_048_576, PriceImageAnnotations.MaximumPixelCount);
        var maximum = Image(512, 512);
        for (var index = 0; index < 4; index++) Add(store, maximum);
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        var first = store.Items[0];
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(first.Id));
        var snapshot = store.Items;
        var revision = store.Revision;
        Assert.Null(store.BeginNew(1, 2));
        AssertState(store, snapshot, revision, first.Id, edit);
        Assert.True(store.Commit(edit, Image(1024, 256)));
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        var shrink = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(first.Id));
        Assert.True(store.Commit(shrink, Image()));
        Assert.Equal(3 * RasterImage.MaximumPixelCount + 1, Pixels(store));
        Add(store, Image(511, 512));
        Assert.Equal(5, store.Items.Length);
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount - 511, Pixels(store));
    }

    [Fact]
    public void OverbudgetNewCommitIsAtomicAndSameDraftCanCommitAnExactFit()
    {
        var store = new PriceImageAnnotations();
        var maximum = Image(512, 512);
        for (var index = 0; index < 3; index++) Add(store, maximum);
        Add(store, Image(511, 512));
        Assert.Equal(512, PriceImageAnnotations.MaximumPixelCount - Pixels(store));
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(5, 6));
        var snapshot = store.Items;
        var revision = store.Revision;
        Assert.False(store.Commit(draft, Image(1024, 1)));
        AssertState(store, snapshot, revision, null, draft);
        Assert.True(store.Commit(draft, Image(512, 1)));
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        Assert.Equal(5, store.Items.Length);
    }

    [Fact]
    public void OverbudgetReplacementIsAtomicAndSubtractsOriginalBeforeCheckingBudget()
    {
        var store = new PriceImageAnnotations();
        var target = Add(store, Image());
        var maximum = Image(512, 512);
        for (var index = 0; index < 3; index++) Add(store, maximum);
        Add(store, Image(511, 512));
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(target.Id));
        var snapshot = store.Items;
        var revision = store.Revision;
        Assert.False(store.Commit(edit, Image(1024, 1)));
        AssertState(store, snapshot, revision, target.Id, edit);
        Assert.Equal(511, PriceImageAnnotations.MaximumPixelCount - Pixels(store));
        Assert.True(store.Commit(edit, Image(512, 1)));
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        Assert.Equal(target.Id, store.Items[0].Id);
        Assert.Equal(5, store.Items.Length);
        Assert.Equal(1, snapshot[0].Image.Width);
    }

    [Fact]
    public void RemovingAndClearingReleasePixelBudgetWithoutReusingIds()
    {
        var store = new PriceImageAnnotations();
        var image = Image(512, 512);
        for (var index = 0; index < 4; index++) Add(store, image);
        var snapshot = store.Items;
        var previousId = snapshot[^1].Id;
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(snapshot[0].Id));
        Assert.True(store.RemoveSelected());
        Assert.Equal(3 * RasterImage.MaximumPixelCount, Pixels(store));
        Assert.False(store.Commit(edit, null!));
        Assert.Null(store.Draft);
        Assert.Null(store.SelectedId);
        var replacement = Add(store, image);
        Assert.True(replacement.Id > previousId);
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        Assert.Equal(4, snapshot.Length);

        store.Clear();
        Assert.Empty(store.Items);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        var revision = store.Revision;
        store.Clear();
        Assert.Equal(revision, store.Revision);
        var next = Add(store, image);
        Assert.True(next.Id > replacement.Id);
        Assert.Equal(RasterImage.MaximumPixelCount, Pixels(store));
        Assert.Equal(4, snapshot.Length);
    }

    [Fact]
    public void MissingIdsAndRepeatedSelectionDoNotMutateState()
    {
        var store = new PriceImageAnnotations();
        var note = Add(store, Image());
        var revision = store.Revision;
        Assert.True(store.Select(note.Id));
        Assert.Equal(revision, store.Revision);
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        revision = store.Revision;
        var snapshot = store.Items;
        foreach (var missing in new[] { long.MinValue, 0, note.Id + 1, long.MaxValue })
        {
            Assert.False(store.Select(missing));
            Assert.Null(store.BeginEdit(missing));
        }
        AssertState(store, snapshot, revision, note.Id, draft);
    }

    [Fact]
    public void SelectionAndClearSelectionInvalidateEditAndNewDraftsWithoutRemovingImages()
    {
        var store = new PriceImageAnnotations();
        Assert.False(store.ClearSelection());
        Assert.Equal(0, store.Revision);
        var first = Add(store, Image());
        var second = Add(store, Image(2, 1));
        var snapshot = store.Items;
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(first.Id));
        Assert.True(store.Select(first.Id));
        Assert.False(store.Commit(edit, null!));
        Assert.Null(store.Draft);
        var create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(5, 6));
        Assert.True(store.Select(second.Id));
        Assert.False(store.Cancel(create));
        Assert.Equal(second.Id, store.SelectedId);

        edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(first.Id));
        var revision = store.Revision;
        Assert.True(store.ClearSelection());
        Assert.Equal(revision + 1, store.Revision);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.False(store.Commit(edit, null!));
        Assert.False(store.ClearSelection());
        Assert.False(store.RemoveSelected());
        Assert.Equal(revision + 1, store.Revision);
        create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(7, 8));
        Assert.True(store.ClearSelection());
        Assert.False(store.Commit(create, null!));
        Assert.Equal(snapshot, store.Items);
        Assert.Equal(3, Pixels(store));
    }

    [Fact]
    public void RemoveWithoutSelectionCancelsOnlyDraftAndClearInvalidatesEveryDraft()
    {
        var store = new PriceImageAnnotations();
        store.Clear();
        Assert.Equal(0, store.Revision);
        var note = Add(store, Image());
        var snapshot = store.Items;
        var create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(3, 4));
        var revision = store.Revision;
        Assert.False(store.RemoveSelected());
        Assert.Equal(revision + 1, store.Revision);
        Assert.Null(store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Equal(snapshot, store.Items);
        Assert.False(store.Commit(create, null!));
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        store.Clear();
        Assert.False(store.Commit(edit, null!));
        Assert.Empty(store.Items);
        create = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(5, 6));
        store.Clear();
        Assert.False(store.Commit(create, null!));
        Assert.Null(store.Draft);
        Assert.Equal(note, Assert.Single(snapshot));
        Assert.True(Add(store, Image()).Id > note.Id);
    }

    [Fact]
    public void InvalidCoordinatesPreserveCurrentEditSelectionItemsAndRevision()
    {
        var store = new PriceImageAnnotations();
        var note = Add(store, Image());
        var edit = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(note.Id));
        var snapshot = store.Items;
        var revision = store.Revision;
        foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1d, -double.Epsilon })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => store.BeginNew(invalid, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.BeginNew(0, invalid));
            AssertState(store, snapshot, revision, note.Id, edit);
        }
        Assert.True(store.Commit(edit, Image()));
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(.125, .25)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void FiniteNonnegativeCoordinatesArePreservedWithoutClamping(double x, double y)
    {
        var note = Add(new PriceImageAnnotations(), Image(), x, y);
        Assert.Equal(x, note.X);
        Assert.Equal(y, note.Y);
    }

    [Fact]
    public void ConcurrentCommitOfOneTokenCreatesExactlyOneNote()
    {
        var store = new PriceImageAnnotations();
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(1, 2));
        var image = Image();
        var successes = 0;
        Parallel.For(0, 64, _ =>
        {
            if (store.Commit(draft, image)) Interlocked.Increment(ref successes);
        });
        Assert.Equal(1, successes);
        Assert.Single(store.Items);
        Assert.Equal(2, store.Revision);
        Assert.Null(store.Draft);
    }

    [Fact]
    public void ConcurrentReplacementOfOneTokenCommitsExactlyOnceAtFullPixelCapacity()
    {
        var store = new PriceImageAnnotations();
        var image = Image(512, 512);
        for (var index = 0; index < 4; index++) Add(store, image);
        var snapshot = store.Items;
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginEdit(snapshot[0].Id));
        var replacement = Image(1024, 256);
        var revision = store.Revision;
        var successes = 0;
        Parallel.For(0, 64, _ =>
        {
            if (store.Commit(draft, replacement)) Interlocked.Increment(ref successes);
        });
        Assert.Equal(1, successes);
        Assert.Equal(revision + 1, store.Revision);
        Assert.Equal(4, store.Items.Length);
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        Assert.Same(replacement, store.Items[0].Image);
        Assert.Same(image, snapshot[0].Image);
    }

    [Fact]
    public void ConcurrentNewOperationsRetainBothBoundsUniqueIdsAndImmutableSnapshots()
    {
        var store = new PriceImageAnnotations();
        var image = Image(256, 256);
        var first = Add(store, image);
        var snapshot = store.Items;
        Parallel.For(0, 256, index =>
        {
            var draft = store.BeginNew(index, index);
            if (draft is not null) store.Commit(draft, image);
            var observed = store.Items;
            Assert.InRange(observed.Length, 1, PriceImageAnnotations.MaximumNotes);
            Assert.InRange(observed.Sum(note => note.Image.Width * note.Image.Height), 1, PriceImageAnnotations.MaximumPixelCount);
            Assert.Equal(observed.Length, observed.Select(note => note.Id).Distinct().Count());
        });
        while (store.Items.Length < PriceImageAnnotations.MaximumNotes) Add(store, image);
        Assert.Equal(PriceImageAnnotations.MaximumPixelCount, Pixels(store));
        Assert.Null(store.BeginNew(0, 0));
        Assert.Equal(first, Assert.Single(snapshot));
        Assert.Equal(image.PixelChecksum, snapshot[0].Image.PixelChecksum);
    }

    [Fact]
    public async Task NonseekableShortReadsImportGeneratedRgbaAndLeaveStreamOpen()
    {
        var original = Image(7, 5, 1701);
        var encoded = PngRasterCodec.Encode(original);
        using var stream = new ProbeStream(encoded, chunkSize: 3);
        var imported = await PriceAnnotationImageImport.ReadAsync(stream);
        Assert.Equal(original.Width, imported.Width);
        Assert.Equal(original.Height, imported.Height);
        Assert.Equal(original.CopyPixels(), imported.CopyPixels());
        Assert.Equal(original.PixelChecksum, imported.PixelChecksum);
        Assert.Equal(encoded.Length, stream.BytesRead);
        Assert.True(stream.ReadCalls > 1);
        Assert.Equal(0, stream.MetadataReads);
        Assert.False(stream.Disposed);
        Array.Fill(encoded, (byte)0);
        Assert.Equal(original.CopyPixels(), imported.CopyPixels());
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public async Task ImportIgnoresLyingLengthAndSeekClaims(long declaredLength)
    {
        var original = Image(3, 2);
        var encoded = PngRasterCodec.Encode(original);
        using var stream = new ProbeStream(encoded, canSeek: true, declaredLength: declaredLength);
        var imported = await PriceAnnotationImageImport.ReadAsync(stream);
        Assert.Equal(original.CopyPixels(), imported.CopyPixels());
        Assert.Equal(0, stream.MetadataReads);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task OversizedInfiniteStreamStopsAtOneSentinelByteWithoutTrustingLength()
    {
        using var stream = new ProbeStream([], generatedLength: long.MaxValue, canSeek: true, declaredLength: 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => PriceAnnotationImageImport.ReadAsync(stream));
        Assert.Equal(PngRasterCodec.MaximumEncodedBytes + 1, stream.BytesRead);
        Assert.Equal(0, stream.MetadataReads);
        Assert.InRange(stream.LargestRequest, 1, 81_920);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task ExactlyAtEncodedLimitChecksEofThenUsesExistingCodecValidation()
    {
        using var stream = new ProbeStream([], generatedLength: PngRasterCodec.MaximumEncodedBytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => PriceAnnotationImageImport.ReadAsync(stream));
        Assert.Equal(PngRasterCodec.MaximumEncodedBytes, stream.BytesRead);
        Assert.Equal(1, stream.EofReads);
        Assert.Equal(1, stream.LastRequest);
        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(stream.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(-1)]
    public async Task TruncatedSelectedStreamsAreRejectedWithoutDisposal(int length)
    {
        var encoded = PngRasterCodec.Encode(Image(2, 2));
        var prefix = encoded[..(length < 0 ? encoded.Length - 1 : length)];
        using var stream = new ProbeStream(prefix, chunkSize: 2, canSeek: true, declaredLength: long.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(() => PriceAnnotationImageImport.ReadAsync(stream));
        Assert.Equal(prefix.Length, stream.BytesRead);
        Assert.Equal(0, stream.MetadataReads);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task CorruptionUnsupportedColorTypeAndTrailingDataUseStrictExistingCodec()
    {
        var encoded = PngRasterCodec.Encode(Image(2, 2));
        var corrupt = (byte[])encoded.Clone();
        corrupt[^1] ^= 1;
        var unsupported = (byte[])encoded.Clone();
        unsupported[25] = 2;
        RewriteHeaderChecksum(unsupported);
        foreach (var invalid in new[] { corrupt, unsupported, encoded.Concat(new byte[] { 0 }).ToArray() })
        {
            using var stream = new ProbeStream(invalid);
            await Assert.ThrowsAsync<InvalidDataException>(() => PriceAnnotationImageImport.ReadAsync(stream));
            Assert.False(stream.Disposed);
        }
    }

    [Fact]
    public async Task PreCancelledImportPerformsNoReadsAndDoesNotDisposeCallerStream()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new ProbeStream(PngRasterCodec.Encode(Image()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PriceAnnotationImageImport.ReadAsync(stream, cancellation.Token));
        Assert.Equal(0, stream.ReadCalls);
        Assert.Equal(0, stream.MetadataReads);
        Assert.False(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterReadIncludingEofPreventsDecodeOrDelivery(bool cancelAtEof)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ProbeStream(PngRasterCodec.Encode(Image(2, 2)), chunkSize: 2);
        stream.AfterRead = read => { if ((read == 0) == cancelAtEof) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PriceAnnotationImageImport.ReadAsync(stream, cancellation.Token));
        Assert.Equal(cancellation.Token, stream.LastCancellationToken);
        if (cancelAtEof) Assert.Equal(1, stream.EofReads);
        else Assert.Equal(1, stream.ReadCalls);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task CancellationTokenReachesPendingReadAndCallerRetainsOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new PendingReadStream();
        var import = PriceAnnotationImageImport.ReadAsync(stream, cancellation.Token);
        await stream.ReadStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        Assert.Equal(cancellation.Token, stream.ObservedToken);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public async Task ReadFailureAndNullStreamAreNotDisguisedAndNoCallerStreamIsDisposed()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => PriceAnnotationImageImport.ReadAsync(null!));
        using var stream = new ProbeStream([]) { ReadError = new IOException("selected stream failed") };
        var error = await Assert.ThrowsAsync<IOException>(() => PriceAnnotationImageImport.ReadAsync(stream));
        Assert.Same(stream.ReadError, error);
        Assert.False(stream.Disposed);
    }

    private static RasterImage Image(int width = 1, int height = 1, int seed = 19)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);
        return new RasterImage(width, height, pixels);
    }

    private static PriceImageNote Add(PriceImageAnnotations store, RasterImage image, double x = 1, double y = 2)
    {
        var draft = Assert.IsType<PriceImageAnnotationDraft>(store.BeginNew(x, y));
        Assert.True(store.Commit(draft, image));
        return store.Items[^1];
    }

    private static int Pixels(PriceImageAnnotations store) => store.Items.Sum(note => note.Image.Width * note.Image.Height);

    private static void AssertState(PriceImageAnnotations store,
        System.Collections.Immutable.ImmutableArray<PriceImageNote> items, long revision, long? selectedId,
        PriceImageAnnotationDraft draft)
    {
        Assert.Equal(items, store.Items);
        Assert.Equal(revision, store.Revision);
        Assert.Equal(selectedId, store.SelectedId);
        Assert.Same(draft, store.Draft);
    }

    private static void AssertNullImageIsAtomic(PriceImageAnnotations store, PriceImageAnnotationDraft draft)
    {
        var items = store.Items;
        var revision = store.Revision;
        var selection = store.SelectedId;
        Assert.Throws<ArgumentNullException>(() => store.Commit(draft, null!));
        AssertState(store, items, revision, selection, draft);
    }

    private static void RewriteHeaderChecksum(byte[] png)
    {
        var crc = uint.MaxValue;
        foreach (var value in png.AsSpan(12, 17))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), ~crc);
    }

    private sealed class ProbeStream(byte[] bytes, int chunkSize = int.MaxValue, bool canSeek = false,
        long? declaredLength = null, long? generatedLength = null) : Stream
    {
        public long BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public int EofReads { get; private set; }
        public int MetadataReads { get; private set; }
        public int LargestRequest { get; private set; }
        public int LastRequest { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public Action<int>? AfterRead { get; set; }
        public IOException? ReadError { get; init; }
        public override bool CanRead => !Disposed;
        public override bool CanWrite => false;
        public override bool CanSeek => canSeek;
        public override long Length { get { MetadataReads++; return declaredLength ?? throw new NotSupportedException(); } }
        public override long Position
        {
            get { MetadataReads++; throw new NotSupportedException(); }
            set { MetadataReads++; throw new NotSupportedException(); }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            LastRequest = buffer.Length;
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            LastCancellationToken = cancellationToken;
            if (ReadError is { } error) return ValueTask.FromException<int>(error);
            var remaining = (generatedLength ?? bytes.Length) - BytesRead;
            var count = (int)Math.Min(Math.Min(buffer.Length, chunkSize), remaining);
            if (generatedLength is not null) buffer.Span[..count].Clear();
            else bytes.AsMemory((int)BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            if (count == 0) EofReads++;
            // Deliberately ignores token cancellation itself so the importer's own checks are tested.
            AfterRead?.Invoke(count);
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) { MetadataReads++; throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class PendingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
