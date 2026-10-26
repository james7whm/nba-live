using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NbaLive.Core;

internal static class PlayInRegression
{
    static object? Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(o);
    static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(o,args);
    static async Task Pump(){await Task.Delay(100);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Walk(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    static bool Text(DependencyObject root,string text)=>Walk(root).OfType<TextBlock>().Any(t=>t.Text==text);
    static Button Button(DependencyObject root,string label)=>Walk(root).OfType<Button>().First(b=>Equals(b.Content,label));
    static Window WindowFor(TeamInsightsCoordinator coordinator,AppSettings settings,bool demo)=>(Window)Activator.CreateInstance(typeof(NbaLive.App).Assembly.GetType("NbaLive.TeamInsightsWindow")!,BindingFlags.Instance|BindingFlags.NonPublic,null,[coordinator,settings,demo],null)!;
    static void Capture(Window window,AppTheme theme,string name)
    {
        window.UpdateLayout();var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth+root.Margin.Left+root.Margin.Right),(int)Math.Ceiling(root.ActualHeight+root.Margin.Top+root.Margin.Bottom),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=System.IO.File.Create(System.IO.Path.GetFullPath("artifacts/play-in-"+name+"-"+theme.ToString().ToLowerInvariant()+".png"));encoder.Save(file);
    }
    internal static async Task<int> Run(AppSettings settings)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Play-in UI: "+message);count++;}
        var source=(IHistoricalTeamInsightsSource)Activator.CreateInstance(typeof(NbaLive.App).Assembly.GetType("NbaLive.DemoTeamInsightsSource")!,true)!;
        using var coordinator=new TeamInsightsCoordinator(source);var scope=new TeamScope("2023-2024",TeamPhase.PlayIn);await coordinator.SelectTeam("lakers");await coordinator.SelectScope(scope);
        var window=WindowFor(coordinator,settings,true);
        try
        {
            window.Show();await coordinator.Refresh(false);await Pump();var view=(FrameworkElement)Field(window,"_playIn")!;var graph=coordinator.State.Standings!.PlayIn!;
            Check(view.IsVisible&&!((FrameworkElement)Field(window,"_bracket")!).IsVisible&&!coordinator.DetailsActive,"play-in has own default graph and suspends team detail demand");
            Check(Text(window,"附加赛")||Button(window,"附加赛") is not null,"third phase entry");
            Check(graph.Games.Count==6&&graph.Berths.Count==4&&((System.Collections.IDictionary)Field(view,"_cards")!).Count==6,"six games and four confirmed berths");
            Check(Walk(view).Count(d=>d.GetType().Name=="TeamLogo")==16,"both teams on games plus four berth logos");Capture(window,settings.Theme,"demo-overview");
            var selected=graph.Games.Single(g=>g.RoundKey=="eighth"&&g.Team.Conference=="西部");Call(view,"SelectGame",selected.Id);await Pump();
            var detail=(FrameworkElement)Field(view,"_detail")!;var loader=(PlayInDetailCoordinator)Field(view,"_coordinator")!;
            Check(loader.State.Game?.Id==selected.Id&&loader.State.Snapshot is not null&&Equals(Field(view,"_effective"),"西部"),"click opens corresponding game and single conference");
            Check(Text(detail,"鹈鹕  105 : 98  国王")&&Text(detail,"分节比分"),"fixed team order and periods");
            Button(detail,"球队对比").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Pump();Check(Text(detail,"投篮命中率"),"team comparison tab");
            Button(detail,"球员数据").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Pump();
            var sheets=(System.Collections.IList)Field(detail,"_sheets")!;var grid=(DataGrid)Field(sheets[0]!,"_grid")!;
            Check(sheets.Count==2&&grid.Items.Count==1&&grid.Columns[0].Visibility==Visibility.Collapsed,"archived players reuse tables without unrelated live favorites");
            Check(grid.Columns.OfType<DataGridTextColumn>().All(c=>c.ElementStyle.Setters.OfType<Setter>().Any(s=>s.Property==TextBlock.TextAlignmentProperty&&Equals(s.Value,TextAlignment.Center))),"player numerical columns centered");
            var playerTabs=(TabControl)Field(detail,"_playerTabs")!;playerTabs.SelectedIndex=1;await loader.Refresh();await Pump();
            Check(ReferenceEquals(playerTabs,Field(detail,"_playerTabs"))&&playerTabs.SelectedIndex==1,"refresh retains player team tab and native table");
            Check(!Walk(detail).Any(v=>v.GetType().Name=="BasketballBadge"&&((UIElement)v).IsVisible),"finished archive never shows on-court basketball");
            Button(detail,"完整单场统计  ›").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Pump();Check((bool)Field(view,"_full")!&&!((UIElement)Field(view,"_scroll")!).IsVisible,"full single-game view uses entire body");
            Button(detail,"收起").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Pump();Check(!(bool)Field(view,"_expanded")!&&Equals(Field(view,"_effective"),"全景"),"collapse restores previous panorama");
            Call(view,"SelectGame",selected.Id);await Pump();window.Hide();await Pump();Check(!coordinator.Active&&!loader.State.Loading,"hidden panel pauses both requests");window.Show();await Pump();Check(coordinator.Active&&loader.State.Game?.Id==selected.Id,"restore keeps selected match");
            window.Width=720;await Pump();Check(Equals(Field(view,"_effective"),"西部")&&!((Button)((System.Collections.IDictionary)Field(view,"_regions")!)["全景"]!).IsEnabled,"minimum width uses conference without tiny type");
            Check(((System.Collections.IDictionary)Field(view,"_cards")!).Count==3&&Walk(view).OfType<TextBlock>().Any(t=>t.FontSize==13),"narrow cards retain all three games and font size");Capture(window,settings.Theme,"demo-narrow");
            window.Width=1100;await Pump();Call(window,"SetBracketMode",false);await Pump();var table=(DataGrid)Field(window,"_rows")!;
            Check(!view.IsVisible&&coordinator.DetailsActive&&table.Columns.Count==5&&Equals(table.Columns[0].Header,"常规位次")&&Equals(table.Columns[3].Header,"场数"),"team list separates original rank, record and games");
            Check(Text(window,"附加赛场均")&&Text(window,"附加赛赛程"),"team detail is scoped to play-in");
            Call(window,"SetBracketMode",true);await Pump();var berth=Walk(view).OfType<Button>().First(b=>b.Content is StackPanel&&Text(b,"季后赛第 7 种子")&&Text(b,"湖人"));berth.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Pump();await Pump();
            var bracket=coordinator.State.Standings!.Bracket!;var selectedSeries=(string)Field(Field(window,"_bracket")!,"_selected")!;
            Check(coordinator.State.Scope?.Phase==TeamPhase.Playoffs&&bracket.Series.Any(n=>n.Id==selectedSeries&&n.HasTeam("lakers")&&n.Stage==0),"berth enters actual first-round series in same season");
            Call(window,"ChooseScope",new TeamScope("2019-2020",TeamPhase.PlayIn));await Pump();Check(Text(view,"2019–20 特殊赛制：第8位赢1场即可，第9位需连赢2场。")&&Text(view,"第2场 · 条件未触发"),"2020 distinct one-game branch");
            Check(Text(view,"该赛季东部未举行附加赛"),"2020 no eastern play-in explicitly confirmed");Capture(window,settings.Theme,"demo-2020");
            Call(window,"ChooseScope",new TeamScope("2004-2005",TeamPhase.PlayIn));await Pump();Check(Text(view,"该赛季未设置附加赛"),"older season has explicit not-held state");
            Check(loader.State.Game is null&&loader.State.Snapshot is null,"scope switch clears and cancels old match detail");
            Call(window,"ChooseScope",new TeamScope("2023-2024"));await Pump();Check(!view.IsVisible&&!((FrameworkElement)Field(window,"_postBar")!).IsVisible,"regular season remains separate");
        }
        finally{window.Close();}
        return count;
    }
    internal static async Task<int> Live(AppSettings settings)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Real play-in UI: "+message);count++;}
        using var transport=new WebGameSource();using var coordinator=new TeamInsightsCoordinator(new HistoricalTeamSource(transport));await coordinator.SelectScope(new("2023-2024",TeamPhase.PlayIn));var window=WindowFor(coordinator,settings,false);
        try
        {
            window.Show();await coordinator.Refresh(false);await Pump();var view=(FrameworkElement)Field(window,"_playIn")!;var graph=coordinator.State.Standings?.PlayIn;
            Check(graph?.Games.Count==6&&graph.Berths.Count==4,"actual six games and berths: "+coordinator.State.StandingsMessage+" / "+coordinator.State.Scope+" / "+graph?.Notice);Capture(window,settings.Theme,"live-overview");
            Call(view,"SelectGame","401654661");var detailCoordinator=(PlayInDetailCoordinator)Field(view,"_coordinator")!;await detailCoordinator.Refresh(false);await Pump();var box=detailCoordinator.State.Snapshot;
            Check(box?.A.Periods.SequenceEqual(new[]{22,32,29,22})==true&&box.A.Metric("fieldGoal")?.Display=="51.8%","real single-game periods and shooting");
            Check(box?.A.Players.Players.Count==14&&box.B.Players.Players.Count==13,"real both rosters");Capture(window,settings.Theme,"live-detail");
            var detail=(FrameworkElement)Field(view,"_detail")!;Button(detail,"完整单场统计  ›").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Pump();Check(Walk(detail).OfType<DataGrid>().Any(g=>g.Items.Count==14),"real player table rendered");Capture(window,settings.Theme,"live-players");
            await coordinator.SelectScope(new("2019-2020",TeamPhase.PlayIn));await Pump();Check(coordinator.State.Standings?.PlayIn?.Games.Single().Score=="126 : 122"&&Text(view,"第2场 · 条件未触发"),"actual 2020 branch");Capture(window,settings.Theme,"live-2020");
        }
        finally{window.Close();}
        return count;
    }
}
