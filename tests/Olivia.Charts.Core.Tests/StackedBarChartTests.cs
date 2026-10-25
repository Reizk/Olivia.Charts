using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class StackedBarChartTests
{
    private static BarChartSource One(params double?[] values) => new([new BarCategory("c", "Same")],
        [new BarSeries("s", "Samples", values.Select((v, i) => new BarSample(i + 1, "c", v)))], 7);
    private static BarChartSnapshot Stack(BarChartSource source, bool full = false) => source.Query(new(full ? BarChartLayout.FullStacked : BarChartLayout.Stacked));
    private static void Ready(BarChartSnapshot snapshot) => Assert.Equal(BarChartStackStatus.Ready, snapshot.StackStatus);
    private static void EmptyFailure(BarChartSnapshot snapshot, BarChartStackStatus status)
    { Assert.Equal(status, snapshot.StackStatus); Assert.Empty(snapshot.Bars); Assert.Empty(snapshot.Contributions); Assert.Empty(snapshot.Gaps); }
    private static MultiSeriesChartSource Host(double a = 8, double b = 2, long version = 1) => new([
        new MultiSeriesData("A", "Same", [new MultiSeriesSample(101, 0, a)]),
        new MultiSeriesData("B", "Same", [new MultiSeriesSample(202, 0, b)])], version);

    [Fact]
    public void Signed_stacks_use_literal_cumulative_bounds_and_shared_edges()
    {
        var source = new BarChartSource([new BarCategory("c", "Same")], [
            new BarSeries("A", "Same", [new(1,"c",2),new(2,"c",3),new(3,"c",-4),new(4,"c",-1)]),
            new BarSeries("B", "Same", [new(5,"c",5),new(6,"c",-2)])]);
        var s = Stack(source); Ready(s); Assert.Equal(new HistoryValueDomain(-7,10),s.Domain);
        Assert.Equal(new[]{0d,2,0,-4,5,-5},s.Contributions.Select(x=>x.Start));
        Assert.Equal(new[]{2d,5,-4,-5,10,-7},s.Contributions.Select(x=>x.End));
        Assert.Equal(s.Contributions[1].End,s.Contributions[4].Start);
        Assert.Equal(s.Contributions[3].End,s.Contributions[5].Start);
        Assert.All(s.Contributions,x=>Assert.Null(x.Share)); Assert.Equal(6,s.Bars.Length);
        var exact=Stack(One(1,Math.ScaleB(1,-53),Math.ScaleB(1,-53)));
        Assert.Equal(new[]{1d,1,1+Math.ScaleB(1,-52)},exact.Contributions.Select(x=>x.End));
        Assert.Equal(exact.Contributions[0].End,exact.Contributions[1].Start);
        Assert.Equal(exact.Contributions[1].End,exact.Contributions[2].Start);
    }

    [Fact]
    public void Repeated_category_samples_keep_each_original_contribution_and_order()
    {
        var source=new BarChartSource([new("later","Same"),new("early","Same")],[
            new BarSeries("a","Same",[new(42,"early",3),new(43,"later",2),new(44,"later",4),new(45,"later",null)]),
            new BarSeries("b","Same",[new(42,"later",5),new(43,"early",double.NaN)])],11);
        var s=Stack(source); Ready(s);
        Assert.Equal(new[]{("a",43L),("a",44L),("b",42L),("a",42L)},s.Contributions.Select(x=>(x.Hit.SeriesId,x.Hit.Key)));
        Assert.Equal(new[]{0d,2,6,0},s.Contributions.Select(x=>x.Start)); Assert.Equal(2,s.Gaps.Length);
        Assert.All(s.Bars,x=>Assert.Equal(11,x.Hit.DatasetVersion));
        Assert.Equal("later",s.Bars[0].Hit.CategoryKey); Assert.Equal("early",s.Bars[^1].Hit.CategoryKey);
    }

    [Fact]
    public void Full_stacks_use_separate_sign_denominators_and_exact_percentages()
    {
        var s=Stack(One(2,6,-1,-3),true); Ready(s); Assert.Equal(new HistoryValueDomain(-100,100),s.Domain);
        Assert.Equal(new[]{0d,25,0,-25},s.Contributions.Select(x=>x.Start));
        Assert.Equal(new[]{25d,100,-25,-100},s.Contributions.Select(x=>x.End));
        Assert.Equal(new double?[]{25,75,-25,-75},s.Contributions.Select(x=>x.Share));
        Assert.Equal(new double?[]{2,6,-1,-3},s.Contributions.Select(x=>x.Hit.Value));
        Assert.Equal(new HistoryValueDomain(0,100),Stack(One(2,6),true).Domain);
        Assert.Equal(new HistoryValueDomain(-100,0),Stack(One(-2,-6),true).Domain);
    }

    [Fact]
    public void Full_stack_subnormal_percentages_round_once_and_overflow_totals_normalize()
    {
        var s=Stack(One(double.Epsilon,128),true); Ready(s);
        Assert.Equal(double.Epsilon,s.Contributions[0].End); Assert.Equal(double.Epsilon,s.Contributions[0].Share);
        Assert.Equal(s.Contributions[0].End,s.Contributions[1].Start); Assert.Equal(100,s.Contributions[1].End);
        Assert.Equal(0d,(double.Epsilon/128)*100);
        var n=Stack(One(-double.Epsilon,-128),true); Assert.Equal(-double.Epsilon,n.Contributions[0].End);
        var extremes=Stack(One(double.MaxValue,double.MaxValue,-double.MaxValue,-double.MaxValue),true); Ready(extremes);
        Assert.Equal(new[]{50d,100,-50,-100},extremes.Contributions.Select(x=>x.End));
        Assert.All(extremes.Bars,x=>{Assert.True(double.IsFinite(x.Top));Assert.True(double.IsFinite(x.Height));});
    }

    [Fact]
    public void Zero_missing_empty_and_sparse_categories_do_not_create_denominators()
    {
        var negativeZero=BitConverter.Int64BitsToDouble(long.MinValue);
        var s=Stack(One(0,negativeZero,null,double.NaN,double.PositiveInfinity),true); Ready(s);
        Assert.Equal(new HistoryValueDomain(0,0),s.Domain);Assert.Equal(2,s.Bars.Length);Assert.Equal(3,s.Gaps.Length);
        Assert.All(s.Contributions,x=>{Assert.Equal(0,x.Start);Assert.Equal(0,x.End);Assert.Equal(0,x.Share);});
        Assert.Equal(long.MinValue,BitConverter.DoubleToInt64Bits(s.Contributions[1].Hit.Value!.Value));
        Assert.Null(Stack(new BarChartSource([],[])).Domain);Assert.Null(Stack(One(null,double.NaN)).Domain);
        var sparse=new BarChartSource([new("x","X"),new("y","Y"),new("z","Z")],[new BarSeries("s","S",[new(1,"z",4)])]);
        Assert.Equal(2,Assert.Single(Stack(sparse).Bars).CategoryIndex);
        using var zero=new MultiSeriesChartSession(Host(0,0));zero.Apply(MultiSeriesChartKind.FullStackedBar,["A","B"],0,0,true,-5,12);
        Assert.True(zero.AutoY);Assert.Equal(new HistoryValueDomain(-1,1),zero.Publication.YDomain);
        var zeroSnapshot=Assert.IsType<BarChartSnapshot>(zero.Publication.StackSnapshot);
        Assert.Equal(zero.Publication.YDomain,zeroSnapshot.Domain);
        Assert.Equal(zero.Publication.YDomain,zeroSnapshot.Options.FixedDomain);
        using var empty=new MultiSeriesChartSession(new MultiSeriesChartSource([]));
        empty.Apply(MultiSeriesChartKind.StackedBar,[],0,1,true,-5,12);Assert.True(empty.AutoY);
        var emptySnapshot=Assert.IsType<BarChartSnapshot>(empty.Publication.StackSnapshot);
        Assert.Null(emptySnapshot.Domain);Assert.Empty(emptySnapshot.Bars);
        Assert.Equal(new HistoryValueDomain(-1,1),emptySnapshot.Options.FixedDomain);
    }

    [Fact]
    public void Fixed_domains_clip_cumulative_intervals_and_keep_zero_height_identity()
    {
        var source=One(8,2,-3,0);var s=source.Query(new(BarChartLayout.Stacked,fixedDomain:new(9,11)));Ready(s);
        var bar=Assert.Single(s.Bars);Assert.Equal(2,bar.Hit.Key);Assert.Equal(.5,bar.Top);Assert.Equal(.5,bar.Height);
        Assert.Equal(bar.Hit,BarChartGeometry.HitTest(s,.5,.75));Assert.Null(BarChartGeometry.HitTest(s,.5,.25));
        Assert.Equal(8,s.Contributions[1].Start);Assert.Equal(10,s.Contributions[1].End);
        var point=source.Query(new(BarChartLayout.Stacked,fixedDomain:new(10,10)));Assert.Single(point.Bars);Assert.Equal(0,point.Bars[0].Height);
        Assert.Equal(point.Bars[0].Hit,BarChartGeometry.GetHit(point,0));
        Assert.Empty(source.Query(new(BarChartLayout.Stacked,fixedDomain:new(11,12))).Bars);
    }

    [Fact]
    public void Ordinary_overflow_and_both_budgets_refuse_atomically()
    {
        EmptyFailure(Stack(One(double.MaxValue,double.MaxValue)),BarChartStackStatus.ArithmeticOverflow);
        EmptyFailure(Stack(One(double.MaxValue,double.Epsilon)),BarChartStackStatus.ArithmeticOverflow);
        var source=One(1,2,null);var ok=Stack(source);var work=ok.RequiredStackWork!.Value;
        Ready(source.Query(new(BarChartLayout.Stacked,stackWorkBudget:work)));
        EmptyFailure(source.Query(new(BarChartLayout.Stacked,stackWorkBudget:work-1)),BarChartStackStatus.WorkBudgetExceeded);
        var early=source.Query(new(BarChartLayout.Stacked,stackWorkBudget:0));EmptyFailure(early,BarChartStackStatus.WorkBudgetExceeded);Assert.Null(early.RequiredStackWork);
        Ready(source.Query(new(BarChartLayout.Stacked,geometryBudget:2)));
        EmptyFailure(source.Query(new(BarChartLayout.Stacked,geometryBudget:1)),BarChartStackStatus.GeometryBudgetExceeded);
        Assert.Throws<ArgumentOutOfRangeException>(()=>new BarChartOptions(stackWorkBudget:-1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new BarChartOptions(stackWorkBudget:BarChartSource.MaximumStackWork+1));
        // Work is a new-mode gate; old Grouped remains admitted with its original geometry semantics.
        Assert.Equal(2,source.Query(new(stackWorkBudget:0)).Bars.Length);
    }

    [Fact]
    public void Maximum_admission_has_bounded_work_and_stable_raw_order()
    {
        var categories=Enumerable.Range(0,256).Select(i=>new BarCategory(i.ToString(),"Same")).ToArray();
        var source=new BarChartSource(categories,Enumerable.Range(0,8).Select(s=>new BarSeries("s"+s,"Same",Enumerable.Range(0,256).Select(c=>new BarSample(c,c.ToString(),1)))));
        var snapshot=Stack(source);Ready(snapshot);Assert.Equal(202816,snapshot.RequiredStackWork);Assert.Equal(2048,snapshot.Bars.Length);
        Assert.Equal(new HistoryValueDomain(0,8),snapshot.Domain);
        Assert.Equal(Enumerable.Range(0,8).Select(i=>"s"+i),snapshot.Bars.Take(8).Select(x=>x.Hit.SeriesId));
        Assert.All(snapshot.Contributions,x=>Assert.InRange(x.End,1,8));
        var values=new[]{new BarSample(7,"c",2)};var copy=new BarChartSource([new("c","C")],[new BarSeries("s","S",values)]);
        values[0]=new(7,"c",999);Assert.Equal(2,Stack(copy).Contributions[0].End);
    }

    [Fact]
    public void Host_kind_visibility_and_replacement_build_before_publication()
    {
        using var session=new MultiSeriesChartSession(Host());
        Assert.True(session.Apply(MultiSeriesChartKind.StackedBar,["A","B"],0,0,true,-5,12));
        Assert.Equal(new HistoryValueDomain(0,10),session.Publication.YDomain);var before=session.Publication;
        Assert.Throws<ArgumentOutOfRangeException>(()=>session.Apply(MultiSeriesChartKind.FullStackedBar,["A","B"],0,0,false,12,-5));Assert.Same(before,session.Publication);
        Assert.True(session.Apply(MultiSeriesChartKind.FullStackedBar,["A"],0,0,true,-5,12));Assert.Equal(100,Assert.Single(session.Publication.StackSnapshot!.Contributions).Share);
        Assert.False(session.Select("A",101,before));
        Assert.True(session.ReplaceData(Host(4,6,1)));Assert.Equal(MultiSeriesChartKind.FullStackedBar,session.Kind);Assert.Equal(100,Assert.Single(session.Publication.StackSnapshot!.Contributions).Share);
        Assert.True(session.Apply(MultiSeriesChartKind.FullStackedBar,["A","B"],0,0,true,-5,12));Assert.Equal(new double?[]{40,60},session.Publication.StackSnapshot!.Contributions.Select(x=>x.Share));
        using var maximum=new MultiSeriesChartSession(new MultiSeriesChartSource(Enumerable.Range(0,8).Select(i=>new MultiSeriesData("s"+i,"Series",[new MultiSeriesSample(1,0,1_000_000)]))));
        maximum.Apply(MultiSeriesChartKind.StackedBar,maximum.VisibleSeries,0,0,true,-5,12);
        Assert.Equal(new HistoryValueDomain(0,8_000_000),maximum.Publication.YDomain);var maxPublication=maximum.Publication;
        Assert.Throws<ArgumentOutOfRangeException>(()=>maximum.Apply(MultiSeriesChartKind.StackedBar,maximum.VisibleSeries,0,0,false,0,8_000_000));Assert.Same(maxPublication,maximum.Publication);
    }

    [Fact]
    public void Host_selection_uses_stack_interval_and_preserves_raw_identity()
    {
        using var session=new MultiSeriesChartSession(Host());session.Apply(MultiSeriesChartKind.StackedBar,["A","B"],0,0,false,9,11);
        Assert.True(session.Select("B",202));Assert.Equal(2,session.SelectedSample!.Value);Assert.True(session.IsSelectionVisible);
        var before=session.Publication;session.Apply(MultiSeriesChartKind.StackedBar,["A"],0,0,false,9,11);
        Assert.Equal(new MultiSeriesSelection("B",202),session.Selection);Assert.False(session.IsSelectionVisible);
        session.Apply(MultiSeriesChartKind.StackedBar,["A","B"],0,0,false,9,11);Assert.True(session.IsSelectionVisible);
        session.ReplaceData(Host(2,2,1));Assert.False(session.IsSelectionVisible);Assert.False(session.Select("A",101,before));
        session.Reset();Assert.Equal(MultiSeriesChartKind.Area,session.Kind);Assert.Null(session.Publication.StackSnapshot);Assert.Null(session.Selection);
    }
}
