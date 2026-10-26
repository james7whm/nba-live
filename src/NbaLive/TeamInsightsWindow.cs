using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace NbaLive;

internal sealed class TeamInsightsWindow : Window
{
    private readonly TeamInsightsCoordinator _coordinator;
    private readonly bool _demo;
    private readonly string _favorite;
    private readonly Grid _body=new();
    private readonly Grid _views=new();
    private readonly DockPanel _postBar=new(){Margin=new(0,0,0,10)};
    private readonly PlayoffBracketView _bracket;
    private readonly PlayInView _playIn;
    private readonly Button _tableView,_graphView;
    private bool _bracketMode;
    private bool _playInMode;
    private string? _pendingPlayoffTeam;
    private readonly DockPanel _ranking=new();
    private readonly ScrollViewer _detailScroll=new() {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly StackPanel _detail=new();
    private readonly TextBlock _season=Ui.Text("读取赛季…",12),_status=Ui.Text("",11);
    private readonly Button _refresh,_back;
    private readonly TeamSeasonPicker _seasonPicker=new();
    private readonly Dictionary<TeamPhase,Button> _phaseButtons=[];
    private readonly Button _currentButton;
    private readonly ComboBox _division=new() {ItemsSource=TeamCatalog.Divisions,SelectedIndex=0,Width=134,Margin=new(8,0,0,0),Visibility=Visibility.Collapsed};
    private readonly Dictionary<string,Button> _groupButtons=[];
    private readonly DataGrid _rows=new() {IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,CanUserSortColumns=false,
        CanUserReorderColumns=false,CanUserResizeRows=false,CanUserResizeColumns=false,HeadersVisibility=DataGridHeadersVisibility.Column,SelectionMode=DataGridSelectionMode.Single,
        SelectionUnit=DataGridSelectionUnit.FullRow,RowHeight=30,ColumnHeaderHeight=32,FontSize=12,BorderThickness=new(0),Background=Brushes.Transparent,
        GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,HorizontalGridLinesBrush=Ui.Brush("LineBrush"),RowHeaderWidth=0};
    private readonly DispatcherTimer _timer=new() {Interval=TeamInsightsCoordinator.RefreshInterval};
    private string _group="东部";
    private bool _narrowDetail,_rendering,_closed,_future=true;
    private bool _postColumns,_allGames=true;
    private bool _playInColumns;
    private string _seriesKey="";
    private TeamInsightsState? _rendered;
    internal event Action<string>? TeamSelected;
    internal event Action<TeamScope>? ScopeSelected;
    internal event Action<bool>? BracketModeSelected;
    internal event Action<bool>? PlayInModeSelected;
    private sealed record StandingRow(TeamStanding Standing,int Rank,bool Favorite,bool Playoffs=false)
    {
        public string Name=>Standing.Team.Name;
        public string RankText=>Playoffs?Standing.PlayoffSeed?.ToString(CultureInfo.InvariantCulture)??"—":Rank>0?Rank.ToString(CultureInfo.InvariantCulture):"—";
        public string LogoTeam=>Standing.Team.LogoTeam;
        public string Progress=>Standing.Progress;
        public string Star=>Favorite?"☆":"";
        public string FullName=>Standing.Team.FullName+(Favorite?" · 设置中的主队":"");
        public string Record=>Standing.Record;
        public string GameCount=>Standing.Games.ToString(CultureInfo.InvariantCulture);
        public string Rate=>Standing.WinPercent is {} n?n.ToString("0.0",CultureInfo.InvariantCulture)+"%":"—";
        public string Gap=>Standing.GamesBehind is {} n?n.ToString("0.0",CultureInfo.InvariantCulture):"—";
        public string Streak=>Standing.Streak;
        public Brush StreakInk=>Ui.Brush(Streak.EndsWith("连胜",StringComparison.Ordinal)?"AccentInk":"MutedInk");
    }
    internal TeamInsightsWindow(TeamInsightsCoordinator coordinator,AppSettings settings,bool demo)
    {
        _coordinator=coordinator;_demo=demo;_favorite=TeamPalette.Resolve(settings.FavoriteTeam).Key;
        _bracketMode=settings.TeamBracketMode;
        _playInMode=settings.PlayInBracketMode;
        Ui.Initialize(this,"球队与排名 · NBA 直播条",1100,760);
        _bracket=new(_favorite);_bracket.TeamRequested+=key=>OpenTeam(key);
        _playIn=new(_favorite,coordinator.PlayInSource);_playIn.PlayoffRequested+=key=>{_pendingPlayoffTeam=key;_bracketMode=true;BracketModeSelected?.Invoke(true);if(_coordinator.State.Scope is {} scope)ChooseScope(scope with {Phase=TeamPhase.Playoffs});};
        MinWidth=Math.Min(720,Width);MinHeight=Math.Min(560,Height);
        var root=new DockPanel {Margin=new(24,20,24,16)};
        var header=new DockPanel {Margin=new(0,0,0,18)};
        var actions=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        _season.Margin=new(0,0,14,0);_season.VerticalAlignment=VerticalAlignment.Center;actions.Children.Add(_season);
        _refresh=Ui.Button("刷新",()=>{_=_coordinator.Refresh();_playIn.Refresh(true);});_refresh.Margin=new(0);_refresh.MinWidth=72;actions.Children.Add(_refresh);
        DockPanel.SetDock(actions,Dock.Right);header.Children.Add(actions);
        var title=new StackPanel();var name=Ui.Text("球队与排名",23);name.Margin=new(0,0,0,5);title.Children.Add(name);
        var subtitle=Ui.Text(demo?"演示数据 · 仅用于界面预览":"赛季概览 · 排名与球队数据",11);subtitle.Margin=new(0);title.Children.Add(subtitle);
        header.Children.Add(title);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var filters=new StackPanel {Orientation=Orientation.Horizontal,Margin=new(0,0,0,14)};filters.Children.Add(_seasonPicker);
        _seasonPicker.Selected+=season=>ChooseScope(new(season,_coordinator.State.Scope?.Phase??TeamPhase.Regular));
        foreach(var phase in new[]{TeamPhase.Regular,TeamPhase.PlayIn,TeamPhase.Playoffs})
        {
            var captured=phase;var button=Ui.Button(phase==TeamPhase.Regular?"常规赛":phase==TeamPhase.PlayIn?"附加赛":"季后赛",()=>{if(_coordinator.State.Scope is {} scope)ChooseScope(scope with {Phase=captured});});
            button.Margin=new(0,0,4,0);button.MinWidth=76;_phaseButtons[phase]=button;filters.Children.Add(button);
        }
        _currentButton=Ui.Button("回到本赛季",()=>{if(_coordinator.State.Seasons.FirstOrDefault(s=>s.Current) is {} season)ChooseScope(new(season.Id));});
        _currentButton.Margin=new(12,0,0,0);filters.Children.Add(_currentButton);DockPanel.SetDock(filters,Dock.Top);root.Children.Add(filters);
        DockPanel.SetDock(_bracket.Tools,Dock.Right);_postBar.Children.Add(_bracket.Tools);
        DockPanel.SetDock(_playIn.Tools,Dock.Right);_postBar.Children.Add(_playIn.Tools);
        var views=new StackPanel{Orientation=Orientation.Horizontal};
        _tableView=Ui.Button("球队列表",()=>SetBracketMode(false));_graphView=Ui.Button("对阵图",()=>SetBracketMode(true));
        _tableView.Margin=new(0,0,6,0);_graphView.Margin=new(0);views.Children.Add(_tableView);views.Children.Add(_graphView);_postBar.Children.Add(views);
        DockPanel.SetDock(_postBar,Dock.Top);root.Children.Add(_postBar);
        _status.TextWrapping=TextWrapping.Wrap;_status.Margin=new(0,12,0,0);DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);
        _body.ColumnDefinitions.Add(new() {Width=new(1.25,GridUnitType.Star)});_body.ColumnDefinitions.Add(new() {Width=new(16)});_body.ColumnDefinitions.Add(new());
        var bar=new DockPanel {Margin=new(0,0,0,12)};
        foreach(var group in new[]{"全部","东部","西部","赛区"})
        {
            var captured=group;var button=Ui.Button(group,()=>{_group=captured;_narrowDetail=false;FillRows();Arrange();});
            button.Margin=new(0,0,6,0);button.MinWidth=62;_groupButtons.Add(group,button);bar.Children.Add(button);
        }
        bar.Children.Add(_division);_division.SelectionChanged+=(_,_)=>{if(!_rendering){_narrowDetail=false;FillRows();Arrange();}};
        DockPanel.SetDock(bar,Dock.Top);_ranking.Children.Add(bar);
        var hint=Ui.Text("点击球队查看概览",11);hint.Margin=new(4,10,0,0);DockPanel.SetDock(hint,Dock.Bottom);_ranking.Children.Add(hint);
        SetupTable();_rows.SizeChanged+=(_,_)=>SizeColumns();_ranking.Children.Add(_rows);
        var rankingCard=Ui.Card(_ranking,new(12));_body.Children.Add(rankingCard);
        var detailHost=new DockPanel();
        _back=Ui.Button("‹ 返回排名",()=>{_narrowDetail=false;Arrange();});_back.Margin=new(0,0,0,12);_back.HorizontalAlignment=HorizontalAlignment.Left;
        DockPanel.SetDock(_back,Dock.Top);detailHost.Children.Add(_back);
        _detailScroll.Content=_detail;detailHost.Children.Add(_detailScroll);
        var detailCard=Ui.Card(detailHost,new(18));Grid.SetColumn(detailCard,2);_body.Children.Add(detailCard);
        _rows.SelectionChanged+=(_,_)=>
        {
            if(_rendering||_rows.SelectedItem is not StandingRow row)return;
            _narrowDetail=true;TeamSelected?.Invoke(row.Standing.Team.Key);_=_coordinator.SelectTeam(row.Standing.Team.Key);Arrange();
        };
        _rows.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter&&_rows.SelectedItem is StandingRow){_narrowDetail=true;Arrange();e.Handled=true;}};
        _rows.AddHandler(System.Windows.Input.Mouse.MouseUpEvent,new System.Windows.Input.MouseButtonEventHandler((_,e)=>
        {
            if(e.ChangedButton==System.Windows.Input.MouseButton.Left&&e.OriginalSource is DependencyObject origin&&ItemsControl.ContainerFromElement(_rows,origin) is DataGridRow row&&Equals(row.Item,_rows.SelectedItem))
                OpenSelectedRow();
        }),true);
        _views.Children.Add(_body);_views.Children.Add(_bracket);_views.Children.Add(_playIn);_views.SizeChanged+=(_,_)=>Arrange();root.Children.Add(_views);Content=root;
        _coordinator.Changed+=OnState;IsVisibleChanged+=(_,_)=>UpdateDemand();StateChanged+=(_,_)=>UpdateDemand();
        _timer.Tick+=(_,_)=>{_=_coordinator.Refresh(false);_playIn.Refresh();};
        ThemeManager.Changed+=OnThemeChanged;
        Closed+=(_,_)=>{_closed=true;_seasonPicker.Close();_timer.Stop();_coordinator.Changed-=OnState;_coordinator.Dispose();_playIn.Dispose();ThemeManager.Changed-=OnThemeChanged;};
        OnState(_coordinator.State);
    }
    private void UpdateDemand()
    {
        if(_closed)return;
        var active=IsVisible&&WindowState!=WindowState.Minimized;
        _playIn.SetActive(active);
        if(active)_timer.Start();else {_timer.Stop();_seasonPicker.Close();}_=_coordinator.SetActive(active);
    }
    private void OnThemeChanged(){if(!_closed){RenderDetail(true);_bracket.RefreshTheme();_playIn.RefreshTheme();}}
    private void SetBracketMode(bool value)
    {
        if(_coordinator.State.Scope?.Phase==TeamPhase.PlayIn){if(_playInMode!=value){_playInMode=value;PlayInModeSelected?.Invoke(value);}}
        else if(_bracketMode!=value){_bracketMode=value;BracketModeSelected?.Invoke(value);}
        Arrange();
    }
    private void ChooseScope(TeamScope scope)
    {
        _seriesKey="";_allGames=true;_narrowDetail=false;
        if(_group=="赛区"||_group=="全部"&&scope.Phase==TeamPhase.Regular)_group=_coordinator.State.SelectedTeam?.Conference is "西部"?"西部":"东部";
        ScopeSelected?.Invoke(scope);_=_coordinator.SelectScope(scope);
    }
    private void OpenSelectedRow()
    {
        if(_rows.SelectedItem is not StandingRow)return;
        _narrowDetail=true;Arrange();
    }
    internal void OpenTeam(string key,bool revealDetail=true)
    {
        if(TeamCatalog.Resolve(key) is not {} team)return;
        if(_coordinator.State.SelectedTeam?.Key!=team.Key)TeamSelected?.Invoke(team.Key);
        _group=team.Conference;_narrowDetail=revealDetail;_=_coordinator.SelectTeam(key);
        if(revealDetail&&(_coordinator.State.Scope?.Phase==TeamPhase.PlayIn?_playInMode:_bracketMode))SetBracketMode(false);
        FillRows();Arrange();
    }
    private void SetupTable(bool playoffs=false,bool playIn=false)
    {
        _rows.Columns.Clear();_postColumns=playoffs;_playInColumns=playIn;
        _rows.Columns.Add(Column(playIn?"常规位次":playoffs?"种子":"排名",nameof(StandingRow.RankText),playIn?1.2:.65));
        var template=(DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="clr-namespace:NbaLive;assembly=NbaLive">
              <Grid ToolTip="{Binding FullName}" Margin="3,0"><Grid.ColumnDefinitions><ColumnDefinition Width="24"/><ColumnDefinition Width="*"/><ColumnDefinition Width="15"/></Grid.ColumnDefinitions>
                <local:TeamLogo Team="{Binding LogoTeam}" Width="20" Height="20" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="1" Text="{Binding Name}" VerticalAlignment="Center" Margin="5,0,0,0" TextTrimming="CharacterEllipsis"/>
                <TextBlock Grid.Column="2" Text="{Binding Star}" VerticalAlignment="Center" Foreground="{DynamicResource AccentInk}" FontSize="12"/>
              </Grid>
            </DataTemplate>
            """);
        _rows.Columns.Add(new DataGridTemplateColumn {Header="球队",CellTemplate=template,Width=new(1.8,DataGridLengthUnitType.Star),MinWidth=112});
        _rows.Columns.Add(Column("战绩",nameof(StandingRow.Record),1));_rows.Columns.Add(Column("胜率",nameof(StandingRow.Rate),1));
        if(playoffs){_rows.Columns.RemoveAt(3);if(playIn)_rows.Columns.Add(Column("场数",nameof(StandingRow.GameCount),.7));_rows.Columns.Add(Column(playIn?"结果":"进程",nameof(StandingRow.Progress),1.3));return;}
        _rows.Columns.Add(Column("胜差",nameof(StandingRow.Gap),.8));var streak=Column("近况",nameof(StandingRow.Streak),1.1);
        streak.ElementStyle.Setters.Add(new Setter(ForegroundProperty,new Binding(nameof(StandingRow.StreakInk))));_rows.Columns.Add(streak);
        _rows.SetResourceReference(DataGrid.RowBackgroundProperty,"CardBrush");_rows.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty,"CanvasBrush");
    }
    private void SizeColumns()
    {
        if(_rows.ActualWidth<100||_rows.Columns.Count is not (4 or 5 or 6))return;
        // Explicit widths also recover correctly after a collapsed pane is measured at zero width.
        var available=Math.Max(332,_rows.ActualWidth-SystemParameters.VerticalScrollBarWidth-2);
        double[] weights=_playInColumns?[1.2,1.8,1,.7,1.3]:_postColumns?[.65,1.8,1,1.3]:[.65,1.8,1,1,.8,1.1];var used=0d;
        for(var i=0;i<weights.Length;i++)
        {
            var width=i==weights.Length-1?Math.Max(44,available-used):Math.Max(_rows.Columns[i].MinWidth,Math.Floor(available*weights[i]/weights.Sum()));
            _rows.Columns[i].Width=new DataGridLength(width);used+=width;
        }
    }
    private static DataGridTextColumn Column(string name,string path,double width)
    {
        var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,TextAlignment.Center));
        style.Setters.Add(new Setter(VerticalAlignmentProperty,VerticalAlignment.Center));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(ToolTipProperty,new Binding(path)));
        return new() {Header=name,Binding=new Binding(path),Width=new(width,DataGridLengthUnitType.Star),ElementStyle=style,MinWidth=44};
    }
    private void FillRows()
    {
        _rendering=true;
        try
        {
            var state=_coordinator.State;var playIn=state.Scope?.Phase==TeamPhase.PlayIn;var post=state.Scope?.Phase is TeamPhase.Playoffs or TeamPhase.PlayIn;
            if(_postColumns!=post||_playInColumns!=playIn)SetupTable(post,playIn);
            _groupButtons["全部"].Visibility=post?Visibility.Visible:Visibility.Collapsed;
            _groupButtons["赛区"].Visibility=post?Visibility.Collapsed:Visibility.Visible;
            _groupButtons["赛区"].IsEnabled=state.Standings?.Rows.Any(r=>!string.IsNullOrEmpty(r.Team.Division))==true;
            if(_group=="赛区"&&!_groupButtons["赛区"].IsEnabled)_group=state.SelectedTeam?.Conference=="西部"?"西部":"东部";
            foreach(var pair in _groupButtons) {pair.Value.Background=Ui.Brush(pair.Key==_group?"AccentTint":"CardBrush");pair.Value.Foreground=Ui.Brush(pair.Key==_group?"AccentInk":"Ink");}
            _division.Visibility=_group=="赛区"?Visibility.Visible:Visibility.Collapsed;
            var rank=state.Standings;
            if(rank is not null)
            {
                var divisions=rank.Rows.Select(r=>r.Team.Division).Where(s=>s.Length>0).Distinct().ToArray();
                if(divisions.Length>0&&(_division.ItemsSource is not string[] old||!old.SequenceEqual(divisions))){var selected=_division.SelectedItem;_division.ItemsSource=divisions;_division.SelectedItem=divisions.Contains(selected as string??"")?selected:divisions[0];}
            }
            var rows=rank?.Rows.Where(r=>_group=="全部"||(_group=="赛区"?r.Team.Division==(_division.SelectedItem as string):r.Team.Conference==_group))
                .Select(r=>new StandingRow(r,_group=="赛区"?rank.DivisionRanks.GetValueOrDefault(r.Team.Key):r.Rank,r.Team.Key==_favorite,post&&!playIn)).ToArray()??[];
            if(post)rows=rows.OrderBy(r=>r.Standing.Team.Conference).ThenBy(r=>r.Standing.PlayoffSeed??int.MaxValue).ToArray();
            // Missing division ranks are displayed as —; don't invent ordering from conference records.
            if(_group=="赛区")rows=rows.OrderBy(r=>r.Rank==0?int.MaxValue:r.Rank).ThenBy(r=>r.Standing.Rank).ToArray();
            _rows.ItemsSource=rows;_rows.SelectedItem=rows.FirstOrDefault(r=>r.Standing.Team.Key==_coordinator.State.SelectedTeam?.Key);
            SizeColumns();
        }
        finally{_rendering=false;}
    }
    private void Arrange()
    {
        if(_body.Children.Count<2)return;
        var playIn=_coordinator.State.Scope?.Phase==TeamPhase.PlayIn;var post=_coordinator.State.Scope?.Phase is TeamPhase.Playoffs or TeamPhase.PlayIn;var graph=post&&(playIn?_playInMode:_bracketMode);
        _postBar.Visibility=post?Visibility.Visible:Visibility.Collapsed;_bracket.Tools.Visibility=graph&&!playIn?Visibility.Visible:Visibility.Collapsed;_playIn.Tools.Visibility=graph&&playIn?Visibility.Visible:Visibility.Collapsed;
        _body.Visibility=graph?Visibility.Collapsed:Visibility.Visible;_bracket.Visibility=graph&&!playIn?Visibility.Visible:Visibility.Collapsed;_playIn.Visibility=graph&&playIn?Visibility.Visible:Visibility.Collapsed;
        _playIn.SetActive(IsVisible&&WindowState!=WindowState.Minimized);
        _tableView.Background=Ui.Brush(graph?"CardBrush":"AccentTint");_tableView.Foreground=Ui.Brush(graph?"Ink":"AccentInk");
        _graphView.Background=Ui.Brush(graph?"AccentTint":"CardBrush");_graphView.Foreground=Ui.Brush(graph?"AccentInk":"Ink");
        _=_coordinator.SetDetailsActive(!graph);
        var narrow=_views.ActualWidth<1000;
        _body.ColumnDefinitions[0].Width=narrow&&_narrowDetail?new(0):new(1.25,GridUnitType.Star);
        _body.ColumnDefinitions[1].Width=narrow?new(0):new(16);
        _body.ColumnDefinitions[2].Width=narrow&&!_narrowDetail?new(0):new(1,GridUnitType.Star);
        _body.Children[0].Visibility=narrow&&_narrowDetail?Visibility.Collapsed:Visibility.Visible;
        _body.Children[1].Visibility=narrow&&!_narrowDetail?Visibility.Collapsed:Visibility.Visible;
        _back.Visibility=narrow&&_narrowDetail?Visibility.Visible:Visibility.Collapsed;
    }
    private void OnState(TeamInsightsState state)
    {
        if(_closed)return;if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>OnState(state));return;}
        if(!ReferenceEquals(state,_coordinator.State))return;
        var previous=_rendered;_rendered=state;
        if(previous?.SelectedTeam?.Key!=state.SelectedTeam?.Key||previous?.Scope!=state.Scope)_seriesKey="";
        _seasonPicker.Update(state.Seasons,state.Scope?.Season??"");
        foreach(var pair in _phaseButtons){pair.Value.IsEnabled=state.Scope is not null;pair.Value.Background=Ui.Brush(state.Scope?.Phase==pair.Key?"AccentTint":"CardBrush");pair.Value.Foreground=Ui.Brush(state.Scope?.Phase==pair.Key?"AccentInk":"Ink");}
        _currentButton.IsEnabled=state.Seasons.Any(s=>s.Current);
        _season.Text=state.Scope is {} scope?state.Seasons.Any(s=>s.Id==scope.Season&&s.Current)?"本赛季":"已结束赛季":"读取赛季…";
        _refresh.IsEnabled=!state.LoadingStandings&&!state.LoadingOverview;
        _status.Text=string.Join(" · ",new[]{_demo?"演示数据":"赛季数据非实时比分",state.Seasons.Count==1?"历史赛季目录暂未提供":"",state.Standings is {} r?$"排名更新 {MatchTime.Format(r.FetchedAt)}":"",state.Standings?.DivisionFetchedAt is {} dt?$"赛区更新 {MatchTime.Format(dt)}":"",state.StandingsMessage}.Where(s=>s.Length>0));
        if(!ReferenceEquals(previous?.Standings,state.Standings)||previous?.SelectedTeam?.Key!=state.SelectedTeam?.Key||previous?.Scope!=state.Scope)FillRows();
        _bracket.Update(state);
        _playIn.Update(state);
        if(_pendingPlayoffTeam is {} target&&state.Scope?.Phase==TeamPhase.Playoffs&&state.Standings?.Bracket is {} bracket&&!state.LoadingStandings)
        {
            _pendingPlayoffTeam=null;var first=bracket.Series.FirstOrDefault(n=>n.Stage==0&&n.HasTeam(target));
            if(first is not null)Dispatcher.BeginInvoke(()=>{if(!_closed&&_coordinator.State.Scope==state.Scope)_bracket.SelectSeries(first.Id);},DispatcherPriority.Loaded);
            else _status.Text+=" · 对应首轮对阵暂未提供";
        }
        if(previous?.Scope!=state.Scope||previous?.SelectedTeam?.Key!=state.SelectedTeam?.Key||!ReferenceEquals(previous?.Standings,state.Standings)||!ReferenceEquals(previous?.Overview,state.Overview)||previous?.OverviewMessage!=state.OverviewMessage||previous?.LoadingOverview!=state.LoadingOverview||previous?.LoadingStandings!=state.LoadingStandings)
            RenderDetail(previous?.SelectedTeam?.Key==state.SelectedTeam?.Key&&previous?.Scope==state.Scope);
        Arrange();
    }
    private void RenderDetail(bool keepScroll)
    {
        var offset=keepScroll?_detailScroll.VerticalOffset:0;var state=_coordinator.State;_detail.Children.Clear();
        if(state.Standings is null&&state.Scope is {} pending)
        {
            var waiting=state.LoadingStandings||state.StandingsMessage.Length==0;
            Heading(pending.Label);_detail.Children.Add(Ui.Text(waiting?"正在读取所选赛季的球队资料…":"该范围资料暂不可用，请刷新或选择其他赛季。",13));return;
        }
        if(state.SelectedTeam is not {} team){_detail.Children.Add(Ui.Text("选择一支球队查看概览",15));return;}
        var scope=state.Scope;var playIn=scope?.Phase==TeamPhase.PlayIn;var post=scope?.Phase is TeamPhase.Playoffs or TeamPhase.PlayIn;var historical=scope is not null&&state.Seasons.Any(s=>s.Id==scope.Season&&!s.Current);
        var identity=new DockPanel {Margin=new(0,0,0,10)};var logo=new TeamLogo {Team=team.LogoTeam,Width=32,Height=32,Margin=new(0,3,12,0)};DockPanel.SetDock(logo,Dock.Left);identity.Children.Add(logo);
        var names=new StackPanel();var title=Ui.Text(team.FullName,19);title.Margin=new(0,0,0,5);names.Children.Add(title);
        var english=Ui.Text(scope?.Label??team.EnglishName,11);english.Margin=new(0,0,0,5);names.Children.Add(english);var location=Ui.Text(team.Conference+" · "+(team.Division.Length>0?team.Division:"赛区资料未提供"),11);location.Margin=new(0);names.Children.Add(location);identity.Children.Add(names);_detail.Children.Add(identity);
        var line=new Border {Height=2,Background=team.LogoTeam==team.Key?new SolidColorBrush(ThemeManager.ChartColor(TeamPalette.Resolve(team.Key))):Ui.Brush("LineBrush"),Margin=new(0,0,0,10)};_detail.Children.Add(line);
        var rank=state.Standings;var standing=rank?.Rows.FirstOrDefault(r=>r.Team.Key==team.Key);
        var summary=state.Overview?.Summary??standing;
        var division=rank?.DivisionRanks.GetValueOrDefault(team.Key)??0;
        _detail.Children.Add(Values(post?new[]{("出场",summary is not null?$"{summary.Games}场":"—"),("战绩",summary?.Record??"—"),("当前／最终进程",standing?.Progress??"—")}:new[]{("战绩",summary?.Record??"—"),(team.Conference+"排名",standing is {Rank:>0} s?$"第{s.Rank}":"—"),("赛区排名",division>0?$"第{division}":"—")},3,19));
        var record=Ui.Text(post?"仅统计所选赛季季后赛 · 附加赛不计入":$"胜率 {Format(summary?.WinPercent,true)} · 主场 {standing?.HomeRecord??"—"} · 客场 {standing?.AwayRecord??"—"}",11);record.Margin=new(0,10,0,0);
        var recordCard=Ui.Card(record,new(10,0,10,10));recordCard.Margin=new(0,0,0,12);if(!post)_detail.Children.Add(recordCard);
        Heading(playIn?"附加赛场均":post?"季后赛场均":"常规赛场均");
        var averages=state.Overview?.Averages??rank?.Averages.GetValueOrDefault(team.Key);
        if(!post&&averages is not null&&standing is not null&&summary?.Record==standing.Record)
            averages=averages with {Metrics=averages.Metrics.Select(m=>m.Value is null&&m.Key=="opponent"?m with {Value=standing.OpponentPoints}:m.Value is null&&m.Key=="differential"?m with {Value=standing.Differential}:m).ToArray()};
        var core=averages?.Metrics.Take(9).Select(m=>(m.Label,m.Display)).ToArray()??new[]{("得分",Format(standing?.Points)),("失分",Format(standing?.OpponentPoints)),("净胜分",Format(standing?.Differential)),("篮板","—"),("助攻","—"),("投篮","—"),("三分","—"),("罚球","—"),("失误","—")};
        _detail.Children.Add(Values(core,3,18));
        var extra=averages?.Metrics.Skip(9).Select(m=>(m.Label,m.Display)).ToArray()??new[]{("前场篮板","—"),("后场篮板","—"),("抢断","—"),("盖帽","—"),("犯规","—")};
        var moreHeader=new StackPanel {Orientation=Orientation.Horizontal};var moreText=Ui.Text("更多场均指标",12);moreText.Margin=new(0,0,12,0);moreHeader.Children.Add(moreText);
        var dataTime=Ui.Text(_demo?"演示数据":(state.Overview?.AveragesFetchedAt??rank?.AveragesFetchedAt) is {} time?$"更新 {MatchTime.Format(time)}":"完整场均暂未提供",10);dataTime.Margin=new(0);dataTime.VerticalAlignment=VerticalAlignment.Center;moreHeader.Children.Add(dataTime);
        _detail.Children.Add(new Expander {Header=moreHeader,Content=Values(extra,3,15),Margin=new(0,6,0,10),Foreground=Ui.Brush("MutedInk")});
        if(post)
        {
            if(playIn){Heading("附加赛赛程");foreach(var g in state.Overview?.Games??[])ScheduleRow(g);if(state.Overview?.Games.Count is not >0)_detail.Children.Add(Ui.Text(state.LoadingOverview?"正在读取附加赛资料…":state.OverviewMessage.Length>0?state.OverviewMessage:"该队暂无所选附加赛记录",12));}
            else RenderSeries(state);DetailNotice(state);RestoreScroll(offset,team.Key);return;
        }
        var recent=state.Overview?.Recent(10)??[];var wins=recent.Count(g=>g.Won==true);
        Heading(recent.Count>0?$"{(historical?"赛季最后":"最近")}{recent.Count}场  ·  {wins}胜{recent.Count-wins}负":"近10场");
        var form=new System.Windows.Controls.Primitives.UniformGrid {Columns=10,Margin=new(0,0,0,5)};
        foreach(var game in recent)
        {
            var label=new TextBlock {Text=game.Won==true?"胜":"负",Foreground=Ui.Brush(game.Won==true?"AccentInk":"MutedInk"),TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=12};
            form.Children.Add(new Border {Child=label,Background=Ui.Brush(game.Won==true?"AccentTint":"SoftBrush"),CornerRadius=new(4),Margin=new(0,0,4,0),Height=27,ToolTip=game.Description});
        }
        _detail.Children.Add(form);var formNote=Ui.Text(recent.Count>0?"从旧到新 → · 仅计所选赛季常规赛":state.LoadingOverview?"正在读取所选赛季赛果…":"暂无已核验的所选赛季赛果",10);formNote.Margin=new(0,0,0,12);_detail.Children.Add(formNote);
        var scheduleHeader=new DockPanel {Margin=new(0,0,0,7)};var tabs=new StackPanel {Orientation=Orientation.Horizontal};
        foreach(var option in new[]{false,true}){var captured=option;var b=Ui.Button(historical?(option?"全部":"最近5场"):(option?"未来5场":"近5场"),()=>{if(historical)_allGames=captured;else _future=captured;RenderDetail(true);});b.FontSize=11;b.Margin=new(4,0,0,0);b.Padding=new(8,4,8,4);if(option==(historical?_allGames:_future))b.Foreground=Ui.Brush("AccentInk");tabs.Children.Add(b);}
        DockPanel.SetDock(tabs,Dock.Right);scheduleHeader.Children.Add(tabs);var heading=Ui.Text(historical?"赛季赛程":"近期赛程",14);heading.Margin=new(0);heading.VerticalAlignment=VerticalAlignment.Center;scheduleHeader.Children.Add(heading);_detail.Children.Add(scheduleHeader);
        var games=state.Overview is {} overview?(historical?(_allGames?overview.Games:overview.Recent(5).Reverse().ToArray()):_future?overview.Upcoming(DateTimeOffset.UtcNow,5):overview.Recent(5).Reverse().ToArray()):[];
        foreach(var game in games)ScheduleRow(game);
        if(games.Count==0){var empty=Ui.Text(state.LoadingOverview?"正在加载赛程…":_future?"暂无后续常规赛赛程":"暂无已结束的常规赛",12);empty.Margin=new(0,10,0,10);_detail.Children.Add(empty);}
        DetailNotice(state);RestoreScroll(offset,team.Key);
    }
    private void RestoreScroll(double offset,string teamKey)
    {
        var scope=_coordinator.State.Scope;
        Dispatcher.BeginInvoke(()=>{if(!_closed&&_coordinator.State.SelectedTeam?.Key==teamKey&&_coordinator.State.Scope==scope)_detailScroll.ScrollToVerticalOffset(offset);},DispatcherPriority.Loaded);
    }
    private void DetailNotice(TeamInsightsState state)
    {
        var notice=Ui.Text(string.Join(" · ",new[]{state.Overview is {} o&&(o.GamesAvailable||o.GamesFetchedAt is not null)?$"赛程更新 {MatchTime.Format(o.GamesFetchedAt??o.FetchedAt)} · 北京时间":"",state.OverviewMessage}.Where(s=>s.Length>0)),10);notice.Margin=new(0,10,0,0);_detail.Children.Add(notice);
    }
    private void RenderSeries(TeamInsightsState state)
    {
        Heading("系列赛");var series=state.Overview?.Series??[];
        if(series.Count==0){_detail.Children.Add(Ui.Text(state.LoadingOverview?"正在读取季后赛资料…":state.OverviewMessage.Length>0?state.OverviewMessage:"该队在所选季后赛暂无已确认的系列赛",12));return;}
        var selected=series.FirstOrDefault(s=>s.Key==_seriesKey)??series.Last();_seriesKey=selected.Key;
        var team=state.SelectedTeam!;var score=new Grid();score.ColumnDefinitions.Add(new());score.ColumnDefinitions.Add(new(){Width=GridLength.Auto});score.ColumnDefinitions.Add(new());
        var left=SeriesTeam(team);Grid.SetColumn(left,0);score.Children.Add(left);
        var value=Ui.Text(selected.Score,25);value.Margin=new(12,0,12,0);value.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(value,1);score.Children.Add(value);
        var right=SeriesTeam(selected.Opponent);right.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(right,2);score.Children.Add(right);
        var card=new StackPanel();card.Children.Add(score);var status=Ui.Text(selected.Round+" · "+selected.Status,11);status.TextAlignment=TextAlignment.Center;status.Margin=new(0,8,0,0);card.Children.Add(status);var frame=Ui.Card(card,new(12));frame.Margin=new(0,0,0,10);_detail.Children.Add(frame);
        var tabs=new WrapPanel {Margin=new(0,0,0,8)};
        foreach(var item in series){var key=item.Key;var button=Ui.Button(item.Round,()=>{_seriesKey=key;RenderDetail(true);});button.Margin=new(0,0,6,6);button.Padding=new(10,5,10,5);if(item.Key==selected.Key){button.Background=Ui.Brush("AccentTint");button.Foreground=Ui.Brush("AccentInk");}tabs.Children.Add(button);}
        _detail.Children.Add(tabs);foreach(var game in selected.Games)ScheduleRow(game);
    }
    private static DockPanel SeriesTeam(TeamIdentity team)
    {
        var panel=new DockPanel {VerticalAlignment=VerticalAlignment.Center,ToolTip=team.FullName};
        var logo=new TeamLogo {Team=team.LogoTeam,Width=20,Height=20,Margin=new(0,0,6,0)};DockPanel.SetDock(logo,Dock.Left);panel.Children.Add(logo);
        panel.Children.Add(new TextBlock {Text=team.Name,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center});return panel;
    }
    private void ScheduleRow(TeamGame game)
    {
        var row=new Grid {Margin=new(0,0,0,1),Height=44,ToolTip=game.Description};row.ColumnDefinitions.Add(new() {Width=new(26)});row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new() {Width=GridLength.Auto});
        row.Children.Add(new TeamLogo {Team=game.Opponent.LogoTeam,Width=20,Height=20,VerticalAlignment=VerticalAlignment.Center});
        var identity=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
        identity.Children.Add(new TextBlock {Text=(game.GameNumber is {} n?$"G{n} · ":"")+(game.Home?"vs ":"@ ")+game.Opponent.Name,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis});
        identity.Children.Add(new TextBlock {Text=(game.Home?"主场":"客场")+(game.Status==GameStatus.Finished?" · "+game.Tipoff.ToOffset(TimeSpan.FromHours(8)).ToString("MM月dd日 HH:mm",CultureInfo.InvariantCulture):" · 北京时间"),FontSize=10,Foreground=Ui.Brush("MutedInk"),Margin=new(0,2,0,0),TextTrimming=TextTrimming.CharacterEllipsis});
        Grid.SetColumn(identity,1);row.Children.Add(identity);
        var text=game.Status==GameStatus.Finished?$"{game.Score}  {(game.Won==true?"胜":"负")}":game.Tipoff.ToOffset(TimeSpan.FromHours(8)).ToString("MM月dd日 HH:mm",CultureInfo.InvariantCulture);
        var value=new TextBlock {Text=text,FontSize=11,VerticalAlignment=VerticalAlignment.Center,Foreground=Ui.Brush("MutedInk"),Margin=new(8,0,0,0)};Grid.SetColumn(value,2);row.Children.Add(value);
        if(_coordinator.State.Scope?.Phase==TeamPhase.PlayIn){var open=Ui.Button("",()=>{SetBracketMode(true);_playIn.SelectGame(game.Id);});open.Content=row;open.HorizontalContentAlignment=HorizontalAlignment.Stretch;open.Padding=new(6,0,6,0);open.Margin=new(0,0,0,2);open.ToolTip="查看这场附加赛与单场统计";_detail.Children.Add(open);}
        else _detail.Children.Add(new Border {Child=row,BorderBrush=Ui.Brush("LineBrush"),BorderThickness=new(0,0,0,1)});
    }
    private void Heading(string text){var heading=Ui.Text(text,14);heading.FontWeight=FontWeights.SemiBold;heading.Margin=new(0,3,0,10);_detail.Children.Add(heading);}
    private static string Format(double? value,bool percent=false)=>value.HasValue?value.Value.ToString("0.0",CultureInfo.InvariantCulture)+(percent?"%":""):"—";
    private static Grid Values(IEnumerable<(string Label,string Value)> items,int columns,double size)
    {
        var grid=new Grid();for(var c=0;c<columns;c++)grid.ColumnDefinitions.Add(new());var i=0;
        foreach(var item in items)
        {
            if(i%columns==0)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var stack=new StackPanel {Margin=new(2,4,2,4)};
            stack.Children.Add(new TextBlock {Text=item.Label,Foreground=Ui.Brush("MutedInk"),FontSize=11,TextAlignment=TextAlignment.Center,Margin=new(0,0,0,3)});
            stack.Children.Add(new TextBlock {Text=item.Value,Foreground=Ui.Brush("Ink"),FontWeight=FontWeights.SemiBold,FontSize=size,TextAlignment=TextAlignment.Center});
            var cell=new Border {Child=stack,BorderBrush=Ui.Brush("LineBrush"),BorderThickness=new(i%columns==0?0:1,i>=columns?1:0,0,0)};Grid.SetColumn(cell,i%columns);Grid.SetRow(cell,i/columns);grid.Children.Add(cell);i++;
        }
        return grid;
    }
}
