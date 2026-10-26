using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NbaLive.Core;

internal static class TeamInsightsRegression
{
    static object? Field(object o,string name)=>o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(o);
    static object? Call(object o,string name,params object?[] args)=>o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(o,args);
    static async Task Pump(){await Task.Delay(100);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    internal static async Task<int> Run(AppSettings settings)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Teams: "+message);count++;}
        new SettingsStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"NbaLive-Demo")).Save(settings with {LastBrowsedTeam="lakers",FavoriteTeam="勇士"});
        var type=typeof(NbaLive.App).Assembly.GetType("NbaLive.AppController")!;
        var controller=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[true,false,false,false],null)!;
        try
        {
            Call(controller,"ShowTeams");await Pump();var window=(Window)Field(controller,"_teams")!;
            var coordinator=(TeamInsightsCoordinator)Field(window,"_coordinator")!;var table=(DataGrid)Field(window,"_rows")!;
            Check(window.IsVisible&&coordinator.Active,"independent window active without selected game");
            Check(coordinator.State.SelectedTeam?.Key=="lakers"&&coordinator.State.Standings?.Rows.Count==30,"last team preferred and league populated");
            Check(table.Items.Count==15,"conference rows populate");
            Check(window.Title.Contains("球队与排名")&&window.MinWidth==720&&window.MinHeight==560,"window title and resize limits");
            Call(controller,"ShowTeams");await Pump();Check(ReferenceEquals(window,Field(controller,"_teams")),"repeated entry reuses window");
            var session=(TextFeedCoordinator)Field(controller,"_session")!;var game=session.State.SelectedGame;
            Call(window,"OpenTeam","celtics",true);await Pump();Check(coordinator.State.SelectedTeam?.Key=="celtics"&&table.Items.Count==15,"specific team selects correct conference");
            Check(((AppSettings)Field(controller,"_settings")!).LastBrowsedTeam=="celtics","specific entry remembers the browsed team too");
            Check(session.State.SelectedGame==game,"team browsing cannot change live game");
            Check(table.Columns.OfType<DataGridTextColumn>().All(c=>c.ElementStyle.Setters.OfType<Setter>().Any(s=>s.Property==TextBlock.TextAlignmentProperty&&Equals(s.Value,TextAlignment.Center))),"numeric columns centered");
            var buttons=(System.Collections.IDictionary)Field(window,"_groupButtons")!;
            ((Button)buttons["赛区"]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var division=(ComboBox)Field(window,"_division")!;division.SelectedItem="太平洋赛区";await Pump();
            Check(division.IsVisible&&table.Items.Count==5,"six-division filter shows five teams");
            window.Width=760;await Pump();
            var body=(Grid)Field(window,"_body")!;var back=(Button)Field(window,"_back")!;
            Check(body.Children[0].Visibility==Visibility.Visible&&body.Children[1].Visibility==Visibility.Collapsed,"narrow window initially shows ranking");
            table.SelectedIndex=0;await Pump();Check(body.Children[0].Visibility==Visibility.Collapsed&&body.Children[1].Visibility==Visibility.Visible&&back.IsVisible,"narrow team click opens full detail with back");
            var stored=(AppSettings)Field(controller,"_settings")!;Check(stored.FavoriteTeam=="勇士"&&stored.LastBrowsedTeam==coordinator.State.SelectedTeam?.Key,"browse preference saved separately");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();Check(body.Children[0].Visibility==Visibility.Visible&&!back.IsVisible,"back restores narrow rankings");
            var scroll=Find<ScrollViewer>(table);if(scroll is not null)Check(scroll.ExtentWidth<=scroll.ViewportWidth+2,"narrow numerical columns fit viewport");
            var selected=table.SelectedItem;var row=(DataGridRow)table.ItemContainerGenerator.ContainerFromItem(selected);
            row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.MouseUpEvent});await Pump();
            Check(back.IsVisible&&Equals(selected,table.SelectedItem),"clicking the selected row again reopens narrow detail");
            window.Width=1100;Call(window,"OpenTeam","lakers",true);await Pump();
            Check(body.Children[0].IsVisible&&body.Children[1].IsVisible&&!back.IsVisible,"wide layout returns to master detail");
            Check(table.Columns.Sum(c=>c.ActualWidth)>=table.ActualWidth-22,"standings columns expand with the panel after narrow mode");
            var detail=(ScrollViewer)Field(window,"_detailScroll")!;detail.ScrollToVerticalOffset(75);await Pump();var offset=detail.VerticalOffset;
            Check(coordinator.State.Overview?.Upcoming(DateTimeOffset.UtcNow,5).Count==5,"demo upcoming schedule includes all five future games");
            Call(controller,"ToggleVisibility");await Pump();Check(!window.IsVisible&&!coordinator.Active,"global hide pauses teams");
            Call(controller,"ToggleVisibility");await Pump();Check(window.IsVisible&&coordinator.Active&&coordinator.State.SelectedTeam?.Key=="lakers","global restore retains team");
            Check(Math.Abs(detail.VerticalOffset-offset)<2,"global restore retains scroll");
            window.WindowState=WindowState.Minimized;await Pump();Check(!coordinator.Active,"minimize pauses requests");window.WindowState=WindowState.Normal;await Pump();Check(coordinator.Active,"restore resumes demand");
            detail.ScrollToTop();await Pump();Capture(window,settings.Theme);
            count+=await Historical(window,controller,settings.Theme);
            count+=await Bracket(window,controller,settings.Theme);
            count+=await LoadingAndSeasonPicker(settings);
            window.Close();await Pump();Check(Field(controller,"_teams") is null&&!coordinator.Active,"closed panel disposed and released");
            Call(controller,"OpenPicker");await Pump();var picker=(Window)Field(controller,"_picker")!;
            Check(FindButton(picker,"球队与排名  ›") is not null,"game picker includes independent entry");picker.Close();
            Call(controller,"ShowStatistics");await Pump();var stats=(Window)Field(controller,"_statistics")!;
            var heading=(FrameworkElement)Field(stats,"_matchHeading")!;Check(FindButton(heading,"查看主队概览与排名",true) is not null,"statistics header has keyboard-accessible team entry");
            stats.Close();
        }
        finally
        {
            ((IDisposable)controller).Dispose();
            var windows=(IEnumerable<Window>)Call(controller,"Windows")!;foreach(var window in windows.ToArray())window.Close();
        }
        return count;
    }
    static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T value)return value;if(Find<T>(child) is {} nested)return nested;}return null;
    }
    static Button? FindButton(DependencyObject root,string label,bool automation=false)
    {
        if(root is Button button&&(automation?System.Windows.Automation.AutomationProperties.GetName(button)==label:Equals(button.Content,label)))return button;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindButton(VisualTreeHelper.GetChild(root,i),label,automation) is {} found)return found;return null;
    }
    static void Capture(Window window,AppTheme theme,string name="teams-ui")
    {
        window.UpdateLayout();
        var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth+root.Margin.Left+root.Margin.Right),(int)Math.Ceiling(root.ActualHeight+root.Margin.Top+root.Margin.Bottom),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=System.IO.Path.GetFullPath("artifacts/"+name+"-"+theme.ToString().ToLowerInvariant()+".png");System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=System.IO.File.Create(path);encoder.Save(stream);
    }
    static async Task<int> Historical(Window window,object controller,AppTheme theme)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Historical UI: "+message);count++;}
        var coordinator=(TeamInsightsCoordinator)Field(window,"_coordinator")!;var table=(DataGrid)Field(window,"_rows")!;
        var regular=new TeamScope("2023-2024");Call(window,"ChooseScope",regular);await Pump();
        Check(coordinator.State.Scope==regular&&coordinator.State.Overview?.Season==regular.Season,"season scope switched together");
        Check(coordinator.State.SelectedTeam?.Key=="lakers"&&table.Columns.Count==6,"regular selection retained and regular columns present");
        Check(FindText(window,"2023–24 · 常规赛"),"scope repeated inside team detail");
        Check(FindButton(window,"未来5场") is null&&FindButton(window,"全部") is not null,"finished season uses season schedule");
        var settings=(AppSettings)Field(controller,"_settings")!;Check(settings.LastTeamSeason==regular.Season&&settings.LastTeamPhase==TeamPhase.Regular&&settings.FavoriteTeam=="勇士","scope persistence independent of favorite");
        var picker=(Button)Field(window,"_seasonPicker")!;picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Check((bool)picker.GetType().GetProperty("IsOpen",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(picker)!,"custom season popup opens");
        var search=(TextBox)Field(picker,"_search")!;search.Text="2023";await Pump();var list=(ListBox)Field(picker,"_list")!;
        Check(list.Items.Count==1&&((TeamSeason)list.Items[0]).Id==regular.Season,"search filters verified seasons");
        list.SelectedIndex=0;Call(picker,"Apply");await Pump();Check(!(bool)picker.GetType().GetProperty("IsOpen",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(picker)!,"apply closes season popup");
        var detail=(ScrollViewer)Field(window,"_detailScroll")!;detail.ScrollToTop();await Pump();Capture(window,theme,"history-ui-regular");
        var buttons=(System.Collections.IDictionary)Field(window,"_phaseButtons")!;((Button)buttons[TeamPhase.Playoffs]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Check(coordinator.State.Scope==new TeamScope(regular.Season,TeamPhase.Playoffs)&&coordinator.State.Overview?.Phase==TeamPhase.Playoffs,"phase switch preserves selected season");
        Check(table.Columns.Count==4&&Equals(table.Columns[0].Header,"种子")&&Equals(table.Columns[3].Header,"进程"),"playoff table uses seed and progress");
        Check(FindText(window,"系列赛")&&FindText(window,"1 : 4")&&FindText(window,"2023–24 · 季后赛"),"series and phase-specific summary visible");
        Check(FindText(window,"G1 · @ 掘金"),"source game number is visible");
        detail.ScrollToTop();await Pump();Capture(window,theme,"history-ui-playoffs");
        window.Width=760;await Pump();var body=(Grid)Field(window,"_body")!;Check(body.Children[0].IsVisible&&!body.Children[1].IsVisible,"narrow scope change returns to list");
        var row=(DataGridRow)table.ItemContainerGenerator.ContainerFromItem(table.SelectedItem);row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.MouseUpEvent});await Pump();Check(body.Children[1].IsVisible,"narrow playoff row reopens detail");
        window.Width=1100;await Pump();picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();Call(controller,"ToggleVisibility");await Pump();
        Check(!coordinator.Active&&!(bool)picker.GetType().GetProperty("IsOpen",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(picker)!,"global hide closes season popup and pauses archive");
        Call(controller,"ToggleVisibility");await Pump();Check(coordinator.State.Scope?.Phase==TeamPhase.Playoffs&&coordinator.State.SelectedTeam?.Key=="lakers","restore retains historical range and team");
        Call(window,"OpenTeam","thunder",true);Call(window,"ChooseScope",new TeamScope("2004-2005"));await Pump();
        Check(coordinator.State.SelectedTeam?.Name=="超音速"&&coordinator.State.SelectedTeam.LogoTeam!="thunder"&&FindText(window,"西雅图超音速"),"historical name and logo fallback");
        ((Button)Field(window,"_currentButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Check(coordinator.State.Scope?.Phase==TeamPhase.Regular&&coordinator.State.Seasons.Single(s=>s.Current).Id==coordinator.State.Scope.Season,"current button restores current regular season");
        Check(coordinator.State.SelectedTeam?.Name=="雷霆"&&coordinator.State.SelectedTeam.LogoTeam=="thunder","returning to cached current scope restores its identity");
        return count;
    }
    static bool FindText(DependencyObject root,string text)
    {
        if(root is TextBlock block&&block.Text==text)return true;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindText(VisualTreeHelper.GetChild(root,i),text))return true;return false;
    }
    static async Task<int> Bracket(Window window,object controller,AppTheme theme)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Bracket UI: "+message);count++;Console.WriteLine("PASS Bracket UI: "+message);}
        var coordinator=(TeamInsightsCoordinator)Field(window,"_coordinator")!;var graph=(FrameworkElement)Field(window,"_bracket")!;
        var body=(Grid)Field(window,"_body")!;var scope=new TeamScope("2023-2024",TeamPhase.Playoffs);
        var session=(TextFeedCoordinator)Field(controller,"_session")!;var game=session.State.SelectedGame;
        Call(window,"ChooseScope",scope);await Pump();Check(!graph.IsVisible&&body.IsVisible,"legacy list remains default");
        Call(window,"SetBracketMode",true);await Pump();
        Check(graph.IsVisible&&!body.IsVisible&&!coordinator.DetailsActive,"graph replaces body and suspends selected team requests");
        Check(((AppSettings)Field(controller,"_settings")!).TeamBracketMode,"view saved");
        var data=coordinator.State.Standings!.Bracket!;var cards=(System.Collections.IDictionary)Field(graph,"_cards")!;
        var canvas=(Canvas)Field(graph,"_canvas")!;var positions=(Dictionary<string,Rect>)Field(graph,"_positions")!;
        Check(data.Series.Count==15&&cards.Count==15&&canvas.Children.OfType<System.Windows.Shapes.Path>().Count()==14,"15 actual series and 14 links");
        Check(Equals(Field(graph,"_effectiveRegion"),"全景")&&positions.Values.Select(p=>p.X).Distinct().Count()==7,"wide panorama has seven columns");
        Check(!positions.Any(a=>positions.Any(b=>a.Key!=b.Key&&a.Value.IntersectsWith(b.Value))),"series cards never overlap");
        Check(positions.Values.All(p=>p.Left>=0&&p.Right<=canvas.Width&&p.Top>=0&&p.Bottom<=canvas.Height),"cards within canvas");
        Check(FindText(graph,"总冠军 · 凯尔特人"),"confirmed champion shown");
        var logos=Descendants(graph).Where(v=>v.GetType().Name=="TeamLogo").ToArray();Check(logos.Length==30,"each team row includes logo");
        Check(Descendants(canvas).OfType<TextBlock>().Where(t=>t.FontSize==13).All(t=>t.TextAlignment==TextAlignment.Right),"series scores align");
        Capture(window,theme,"bracket-ui-overview");
        var la=data.Series.Single(n=>n.HasTeam("lakers"));((Button)cards[la.Id]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        var drawer=(Border)Field(graph,"_drawer")!;
        Check((bool)Field(graph,"_expanded")!&&FindText(drawer,"掘金  4 : 1  湖人"),"click opens consistent ordered series score");
        Check(FindText(drawer,"114 : 103")&&FindText(drawer,"108 : 119")&&FindText(drawer,"G5"),"all five individual games retained");
        var chip=Descendants(drawer).OfType<Button>().First(b=>b.Content is StackPanel&&FindText(b,"G1"));chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Check((string)Field(graph,"_selectedGame")! ==la.Games[0].Id&&Descendants(drawer).OfType<TextBlock>().Any(t=>t.Text.Contains("北京时间")&&t.Text.Contains("114 : 103")),"game click reveals date, venue and same score order");
        Capture(window,theme,"bracket-ui-series");
        Call(controller,"ToggleVisibility");await Pump();Check(!window.IsVisible&&!coordinator.Active,"global hide pauses graph window");
        Call(controller,"ToggleVisibility");await Pump();Check(window.IsVisible&&(string)Field(graph,"_selected")! ==la.Id&&(string)Field(graph,"_selectedGame")! ==la.Games[0].Id,"restore preserves node and game");
        await coordinator.Refresh(false);await Pump();Check((bool)Field(graph,"_expanded")!&&(string)Field(graph,"_selectedGame")! ==la.Games[0].Id,"cached refresh retains detail snapshot selection");
        var teamButton=FindButton(drawer,"湖人统计")!;teamButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Check(body.IsVisible&&!graph.IsVisible&&coordinator.DetailsActive&&coordinator.State.SelectedTeam?.Key=="lakers"&&coordinator.State.Scope==scope,"team action returns to matching scoped detail");
        Check(session.State.SelectedGame==game,"bracket browsing does not switch live game");
        Call(window,"SetBracketMode",true);await Pump();Check((string)Field(graph,"_selected")! ==la.Id,"return to graph preserves selection");
        window.Width=760;await Pump();Check(Equals(Field(graph,"_effectiveRegion"),"西部")&&((System.Collections.IDictionary)Field(graph,"_cards")!).Count==8,"narrow window shows conference plus finals");
        var regions=(System.Collections.IDictionary)Field(graph,"_regions")!;Check(!((Button)regions["全景"]!).IsEnabled,"narrow panorama avoids tiny text");
        Check(Descendants((Canvas)Field(graph,"_canvas")!).OfType<TextBlock>().Any(t=>t.FontSize==11.5),"team type size retained in narrow view");
        Capture(window,theme,"bracket-ui-narrow");
        var gameScroll=(ScrollViewer)Field(graph,"_gamesScroll")!;gameScroll.ScrollToHorizontalOffset(120);await Pump();var gameOffset=gameScroll.HorizontalOffset;
        Call(graph,"Update",coordinator.State with {Standings=coordinator.State.Standings! with {Bracket=data with {}}});await Pump();
        Check(Math.Abs(((ScrollViewer)Field(graph,"_gamesScroll")!).HorizontalOffset-gameOffset)<2&&Equals(Field(graph,"_effectiveRegion"),"西部"),"new snapshot retains game scroll and conference");
        ((Button)regions["东部"]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Check(Equals(Field(graph,"_effectiveRegion"),"东部")&&((string)Field(graph,"_selected")!).Length==0,"conference switch clears out-of-scope selection");
        ((Button)regions["总决赛"]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();Check(((System.Collections.IDictionary)Field(graph,"_cards")!).Count==1,"final remains accessible at narrow width");
        window.Width=1100;await Pump();((Button)regions["全景"]!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Pump();
        Call(graph,"LocateFavorite");await Pump();Check(FindText(graph,"该季主队没有已确认的季后赛对阵"),"nonqualified favorite has explicit hint");
        var graphType=graph.GetType();var favoriteGraph=(FrameworkElement)Activator.CreateInstance(graphType,BindingFlags.Instance|BindingFlags.NonPublic,null,["mavericks"],null)!;
        var favoriteWindow=new Window{Width=1100,Height=650,Content=favoriteGraph,Owner=window,ShowInTaskbar=false};
        try
        {
            Call(favoriteGraph,"Update",coordinator.State);favoriteWindow.Show();await Pump();Call(favoriteGraph,"LocateFavorite");await Pump();
            var favoriteCanvas=(Canvas)Field(favoriteGraph,"_canvas")!;
            Check(favoriteCanvas.Children.OfType<System.Windows.Shapes.Path>().Count(p=>p.StrokeThickness==2)==3,"qualified favorite highlights three actual advancement links");
            Check(((System.Collections.IDictionary)Field(favoriteGraph,"_cards")!).Values.Cast<Button>().Count(b=>ReferenceEquals(b.BorderBrush,((Button)((System.Collections.IDictionary)Field(favoriteGraph,"_cards")!)[data.Series.Single(n=>n.Stage==3).Id]!).BorderBrush))==4,"favorite highlights all four series");
            Check((string)Field(favoriteGraph,"_selected")! ==data.Series.Single(n=>n.Stage==3).Id&&!((bool)Field(favoriteGraph,"_expanded")!),"favorite locates last confirmed round without opening drawer");
        }
        finally{favoriteWindow.Close();}
        Call(graph,"SelectSeries",la.Id);await Pump();
        var source=PresentationSource.FromVisual(window)!;
        void Key(System.Windows.Input.Key key)=>graph.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});
        Key(System.Windows.Input.Key.Escape);await Pump();Check(!(bool)Field(graph,"_expanded")!,"Escape closes detail");
        ((Button)((System.Collections.IDictionary)Field(graph,"_cards")!)[la.Id]!).Focus();Key(System.Windows.Input.Key.Enter);await Pump();Check((bool)Field(graph,"_expanded")!,"Enter opens focused series");
        Key(System.Windows.Input.Key.Right);await Pump();Check(System.Windows.Input.Keyboard.FocusedElement is Button focused&&focused.Tag is string id&&id!=la.Id,"arrows move between connected columns");
        Call(window,"ChooseScope",new TeamScope(scope.Season));await Pump();Check(!graph.IsVisible&&((string)Field(graph,"_selected")!).Length==0&&!((FrameworkElement)Field(window,"_postBar")!).IsVisible,"regular season hides graph and clears old scope selection");
        Call(window,"ChooseScope",scope);await Pump();Check(graph.IsVisible&&((string)Field(graph,"_selected")!).Length==0,"saved graph preference returns for playoffs only");
        Call(window,"SetBracketMode",false);await Pump();return count;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    internal static async Task<int> LoadingAndSeasonPicker(AppSettings settings)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Season picker/loading: "+message);count++;}
        var source=new LoadingSource();using var coordinator=new TeamInsightsCoordinator(source);
        var scope=new TeamScope("2015-2016",TeamPhase.Playoffs);await coordinator.SelectTeam("lakers");await coordinator.SelectScope(scope);
        var type=typeof(NbaLive.App).Assembly.GetType("NbaLive.TeamInsightsWindow")!;
        var window=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[coordinator,settings,false],null)!;
        try
        {
            window.Show();await Pump();
            Check(coordinator.State.LoadingStandings&&FindText(window,"正在读取所选赛季的球队资料…")&&!FindText(window,"该范围资料暂不可用，请刷新或选择其他赛季。"),"slow initial request shows loading instead of unavailable");
            var picker=(Button)Field(window,"_seasonPicker")!;var label=(TextBlock)Field(picker,"_label")!;var arrow=Descendants(picker).OfType<System.Windows.Shapes.Path>().Single();
            Check(label.Text=="2015–16"&&picker.Content is Grid,"season text is separate from vector arrow");
            foreach(var scale in new[]{1d,1.25,1.5,2})
            {
                picker.LayoutTransform=new ScaleTransform(scale,scale);await Pump();
                var textBounds=label.TransformToAncestor(picker).TransformBounds(new Rect(label.RenderSize));var arrowBounds=arrow.TransformToAncestor(picker).TransformBounds(new Rect(arrow.RenderSize));
                Check(Math.Abs(textBounds.Top+textBounds.Height/2-arrowBounds.Top-arrowBounds.Height/2)<=1,"arrow centered at scale "+scale);
            }
            picker.LayoutTransform=Transform.Identity;await Pump();Capture(window,settings.Theme,"season-loading-fixed");
            source.Pending.SetException(new System.IO.IOException("offline"));await Pump();
            Check(!coordinator.State.LoadingStandings&&FindText(window,"该范围资料暂不可用，请刷新或选择其他赛季。"),"failure replaces loading with unavailable");
            source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var retry=coordinator.Refresh(false);await Pump();
            Check(FindText(window,"正在读取所选赛季的球队资料…")&&!FindText(window,"该范围资料暂不可用，请刷新或选择其他赛季。"),"same-scope retry clears previous failure immediately");
            source.Pending.SetResult(source.Rank(scope));await retry;await Pump();
            Check(FindText(window,"洛杉矶湖人")&&!FindText(window,"正在读取所选赛季的球队资料…"),"success replaces loading with selected team");
            source.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var next=coordinator.SelectScope(scope with {Phase=TeamPhase.Regular});await Pump();
            Check(coordinator.State.LoadingStandings&&FindText(window,"正在读取所选赛季的球队资料…")&&!FindText(window,"该范围资料暂不可用，请刷新或选择其他赛季。"),"switching phase cannot flash old unavailable state");
            source.Pending.SetResult(source.Rank(scope with {Phase=TeamPhase.Regular}));await next;await Pump();
        }
        finally{window.Close();}
        return count;
    }
    sealed class LoadingSource:IHistoricalTeamInsightsSource
    {
        public TaskCompletionSource<TeamStandingsSnapshot> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TeamStandingsSnapshot Rank(TeamScope scope)=>new(scope.Season,[new(TeamCatalog.Resolve("lakers")!,1,4,1,80,0,"—","—",100,90,10,"—")],DateTimeOffset.UtcNow){Phase=scope.Phase};
        public void Invalidate(TeamScope scope){}
        public Task<IReadOnlyList<TeamSeason>> GetSeasons(CancellationToken ct)=>Task.FromResult<IReadOnlyList<TeamSeason>>([new("2026-2027",true),new("2015-2016",false)]);
        public Task<TeamStandingsSnapshot> GetStandings(CancellationToken ct)=>Pending.Task;
        public Task<TeamStandingsSnapshot> GetStandings(TeamScope scope,CancellationToken ct)=>Pending.Task;
        public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,string season,CancellationToken ct)=>GetTeamOverview(team,new TeamScope(season),ct);
        public Task<TeamOverviewSnapshot> GetTeamOverview(TeamIdentity team,TeamScope scope,CancellationToken ct)=>Task.FromResult(new TeamOverviewSnapshot(team,scope.Season,[],DateTimeOffset.UtcNow){Phase=scope.Phase});
    }
    internal static async Task<int> RunLiveHistory(AppSettings settings)
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception("Live historical UI: "+message);count++;}
        using var transport=new WebGameSource();using var coordinator=new TeamInsightsCoordinator(new HistoricalTeamSource(transport));
        await coordinator.SelectScope(new("2023-2024"));
        var type=typeof(NbaLive.App).Assembly.GetType("NbaLive.TeamInsightsWindow")!;
        var window=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[coordinator,settings,false],null)!;
        try
        {
            Call(window,"OpenTeam","lakers",true);window.Show();var load=coordinator.Refresh(false);
            if(await Task.WhenAny(load,Task.Delay(60000))!=load)throw new Exception("Historical source timeout");await load;await Pump();
            Check(coordinator.State.Standings?.Rows.Count==30&&coordinator.State.Overview?.Summary?.Record=="47–35","real regular source in WPF");
            Check(coordinator.State.Standings!.DivisionRanks.GetValueOrDefault("lakers")==3,"real historical division rank");
            Check(FindText(window,"2023–24 · 常规赛"),"real regular scope header");Capture(window,settings.Theme,"history-live-regular");
            await coordinator.SelectScope(new("2023-2024",TeamPhase.Playoffs));await Pump();
            Check(coordinator.State.Overview?.Summary?.Games==5&&coordinator.State.Overview.Series.Single().Score=="1 : 4","real west playoff detail");
            Check(FindText(window,"季后赛场均")&&FindText(window,"1 : 4"),"real playoff UI");Capture(window,settings.Theme,"history-live-playoffs");
            Call(window,"SetBracketMode",true);await Pump();var graph=(FrameworkElement)Field(window,"_bracket")!;var bracket=coordinator.State.Standings!.Bracket!;
            Check(bracket.Series.Count==15&&bracket.Series.Sum(n=>n.Games.Count)==82&&bracket.Edges.Count==14&&bracket.ChampionKey=="celtics"&&bracket.Notice=="","real complete 2024 bracket validated");
            Check(((System.Collections.IDictionary)Field(graph,"_cards")!).Count==15&&!coordinator.DetailsActive,"real graph rendered without team fetch");Capture(window,settings.Theme,"bracket-live-overview");
            Call(graph,"SelectSeries",bracket.Series.Single(n=>n.HasTeam("lakers")).Id);await Pump();
            Check(FindText((Border)Field(graph,"_drawer")!,"114 : 103")&&FindText((Border)Field(graph,"_drawer")!,"掘金  4 : 1  湖人"),"real series drawer verifies west scores");Capture(window,settings.Theme,"bracket-live-series");
            Call(window,"OpenTeam","celtics",true);await coordinator.SelectTeam("celtics");await Pump();
            Check(coordinator.State.Overview?.Summary?.Games==19&&coordinator.State.Overview.Series.Count==4&&coordinator.State.Standings!.Rows.Single(r=>r.Team.Key=="celtics").Progress=="冠军","real east playoff rounds and result");
            Capture(window,settings.Theme,"history-live-celtics");
        }
        finally{window.Close();}
        return count;
    }
}
