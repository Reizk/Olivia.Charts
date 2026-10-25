using Olivia.Core.Charts;

namespace Olivia.Core.Workspaces;

public sealed record TypedChartBindingPublication(long Revision, TypedChartBindingResult Result);

/// <summary>Owner-thread, synchronous bounded binding. Prepare before commit, then notify once.
/// Source business versions may repeat; delayed selection requires the exact publication reference.
/// Any effective source/binding change clears bucket selection. Equal requests are exact no-ops.</summary>
public sealed class TypedChartBindingSession : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private bool _changing;
    public TypedChartBindingSession(TypedChartBindingSource source, ChartBindingRequest? request = null)
    { Publication = new(1, TypedChartBinding.Build(source, request ?? ChartBindingRequest.Default)); }
    public TypedChartBindingPublication Publication { get; private set; }
    public ChartBindingBucket? SelectedBucket => SelectedBucketKey is { } key
        ? Publication.Result.Buckets.FirstOrDefault(b => b.Key == key) : null;
    public long? SelectedBucketKey { get; private set; }
    public bool IsClosed { get; private set; }
    public int CallbackFailureCount { get; private set; }
    public event EventHandler? Changed;
    public bool Apply(ChartBindingRequest request) => Replace(Publication.Result.Source, request);
    public bool ReplaceSource(TypedChartBindingSource source) => Replace(source, Publication.Result.Request);
    private bool Replace(TypedChartBindingSource source, ChartBindingRequest request)
    {
        Verify();
        if (IsClosed || _changing) return false;
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (ReferenceEquals(source, Publication.Result.Source) && request == Publication.Result.Request) return false;
        var result = TypedChartBinding.Build(source, request);
        var next = new TypedChartBindingPublication(checked(Publication.Revision + 1), result);
        return Change(() => { Publication = next; SelectedBucketKey = null; });
    }
    public bool Select(long? key, TypedChartBindingPublication expectedPublication)
    {
        Verify();
        if (IsClosed || _changing || !ReferenceEquals(Publication, expectedPublication)
            || key is { } requested && !Publication.Result.Buckets.Any(b => b.Key == requested)
            || SelectedBucketKey == key) return false;
        return Change(() => SelectedBucketKey = key);
    }
    private bool Change(Action mutation)
    {
        if (IsClosed || _changing) return false;
        _changing = true;
        try
        {
            mutation();
            if (Changed is { } handlers)
                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    if (IsClosed) break;
                    try { handler(this, EventArgs.Empty); } catch { CallbackFailureCount++; }
                }
            return true;
        }
        finally { _changing = false; }
    }
    private void Verify()
    { if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Owner thread required."); }
    public void Dispose() { Verify(); if (IsClosed) return; IsClosed = true; SelectedBucketKey = null; Changed = null; }
}
