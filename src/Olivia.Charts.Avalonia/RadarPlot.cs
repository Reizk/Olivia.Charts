using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Olivia.Core.Charts;

namespace Olivia.Avalonia;

/// <summary>Original categorical radar; model coordinates and identity drive markers and picking.</summary>
public sealed class RadarPlot : Control
{
    private RadarSession? _session;
    public RadarPlot(){Focusable=true;MinHeight=320;ClipToBounds=true;AutomationProperties.SetName(this,"Categorical radar. Arrows select raw series and axis identities; Home/End, Escape clear.");}
    public UiPalette Palette {get;set;}=UiPalette.Dark;
    public RadarSession? Session=>_session;
    public Point Center=>new(Bounds.Width/2,Bounds.Height/2);
    public double Radius=>Math.Max(0,Math.Min(Bounds.Width/2-105,Bounds.Height/2-60));
    private static readonly IBrush[] Colors=[Brushes.Turquoise,Brushes.Orange,Brushes.Violet,Brushes.Yellow,Brushes.CornflowerBlue,Brushes.LimeGreen,Brushes.Salmon,Brushes.White];
    public IBrush SeriesBrush(string id){if(_session is {} s)for(var i=0;i<s.Source.Series.Length;i++)if(s.Source.Series[i].Id==id)return Colors[i%Colors.Length];return Palette.Muted;}
    public void Bind(RadarSession session){if(_session is not null)_session.Changed-=Changed;_session=session;session.Changed+=Changed;Changed(this,EventArgs.Empty);}
    private void Changed(object? sender,EventArgs e){AutomationProperties.SetHelpText(this,_session?.SelectionDescription??"No source");InvalidateVisual();}
    public void Close(){if(_session is not null)_session.Changed-=Changed;_session=null;InvalidateVisual();}
    public Point ToScreen(double x,double y)=>new(Center.X+x*Radius,Center.Y+y*Radius);
    public Point? MarkerPosition(RadarKey key){if(_session is null)return null;foreach(var p in _session.Geometry.Points)if(p.Key==key)return ToScreen(p.X,p.Y);return null;}
    private void Text(DrawingContext c,string text,Point at,IBrush brush,double size=12){var f=new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,size,brush);c.DrawText(f,new Point(at.X-f.Width/2,at.Y-f.Height/2));}
    public override void Render(DrawingContext c)
    {
        base.Render(c);c.FillRectangle(Palette.Surface,new Rect(Bounds.Size));if(_session is not {} s||Radius<=0)return;
        var center=Center;var radius=Radius;var grid=new Pen(Palette.GridLine,1);
        for(var i=1;i<=5;i++){var r=radius*i/5;c.DrawEllipse(null,grid,center,r,r);Text(c,(s.Maximum*i/5).ToString("G4",CultureInfo.InvariantCulture),new(center.X+16,center.Y-r+8),Palette.Muted,11);}Text(c,"0",new(center.X+10,center.Y+10),Palette.Muted,11);
        for(var i=0;i<s.Source.Axes.Length;i++){var angle=2*Math.PI*i/s.Source.Axes.Length;var x=Math.Sin(angle);var y=-Math.Cos(angle);c.DrawLine(grid,center,ToScreen(x,y));Text(c,s.Source.Axes[i].Label,new(center.X+x*(radius+35),center.Y+y*(radius+24)),Palette.Text);}
        var geometry=s.Geometry;
        for(var i=0;i<s.Source.Series.Length;i++){var series=s.Source.Series[i];var brush=Colors[i%Colors.Length];var pen=new Pen(brush,2);foreach(var edge in geometry.Edges)if(edge.Start.Key.SeriesId==series.Id)c.DrawLine(pen,ToScreen(edge.Start.X,edge.Start.Y),ToScreen(edge.End.X,edge.End.Y));foreach(var p in geometry.Points)if(p.Key.SeriesId==series.Id)c.DrawEllipse(brush,null,ToScreen(p.X,p.Y),4,4);}
        if(s.SelectedKey is {} key&&MarkerPosition(key) is {} point)c.DrawEllipse(null,new Pen(Palette.Text,2),point,8,8);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);if(_session is not {} s||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus();var at=e.GetPosition(this);RadarKey? best=null;double distance=64;
        foreach(var p in s.Geometry.Points){var screen=ToScreen(p.X,p.Y);var d=(screen.X-at.X)*(screen.X-at.X)+(screen.Y-at.Y)*(screen.Y-at.Y);if(d<=64&&(best is null||d<distance)){best=p.Key;distance=d;}}
        if(best is {} key)s.Select(key);e.Handled=true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(_session is not {} s)return;if(e.Key==Key.Escape){s.ClearSelection();e.Handled=true;return;}
        if(e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))return;
        var keys=s.Source.Series.SelectMany(series=>s.Source.Axes.Select(axis=>new RadarKey(series.Id,axis.Id))).ToArray();if(keys.Length==0){e.Handled=true;return;}
        var index=s.SelectedKey is {} selected?Array.IndexOf(keys,selected):-1;index=e.Key switch{Key.Home=>0,Key.End=>keys.Length-1,Key.Left or Key.Up=>index<0?0:(index-1+keys.Length)%keys.Length,_=>(index+1)%keys.Length};s.Select(keys[index]);e.Handled=true;
    }
}
