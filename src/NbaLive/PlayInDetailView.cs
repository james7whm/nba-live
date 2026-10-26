using System.Globalization;

namespace NbaLive;

internal sealed class PlayInDetailView:DockPanel,IDisposable
{
    readonly PlayInDetailCoordinator _coordinator;
    readonly StackPanel _content=new();
    readonly TextBlock _status=Ui.Text("",10);
    readonly ScrollViewer _scroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    readonly List<StatisticsWindow.TeamSheet> _sheets=[];
    readonly Dictionary<string,Button> _tabs=[];
    readonly Grid _body=new();
    TabControl? _playerTabs;
    PlayInBoxScore? _shown;
    string _shownId="",_shownTab="";
    TextBlock? _heading;
    TextBlock? _gameLine;
    string _outcome="";
    string _tab="概览";bool _disposed;
    internal event Action? CloseRequested,FullRequested;
    internal PlayInDetailView(PlayInDetailCoordinator coordinator)
    {
        _coordinator=coordinator;_scroll.Content=_content;_status.TextWrapping=TextWrapping.Wrap;_status.Margin=new(0,3,0,0);DockPanel.SetDock(_status,Dock.Bottom);Children.Add(_status);Children.Add(_scroll);
        _coordinator.Changed+=OnState;SizeChanged+=(_,_)=>{ArrangeBody();if(_playerTabs is not null)_playerTabs.Height=Math.Max(180,ActualHeight-140);};
    }
    internal void Reset(){_tab="概览";_scroll.ScrollToTop();Render();}
    internal void SetOutcome(string outcome){_outcome=outcome;if(_gameLine is not null&&_coordinator.State.Game is {} game)_gameLine.Text=Description(game);}
    internal void ShowPlayers(){_tab="球员数据";Render();}
    internal void RefreshTheme(){_shownId="";Render();}
    void OnState(PlayInDetailState state)
    {
        if(_disposed)return;if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>OnState(state));return;}if(!ReferenceEquals(state,_coordinator.State))return;Render();
    }
    void Render()
    {
        var state=_coordinator.State;
        _status.Text=string.Join(" · ",new[]{state.Message,state.Snapshot is {} s?"最近成功 "+MatchTime.Format(s.FetchedAt):""}.Where(s=>s.Length>0));
        if(state.Game is {} current&&current.Id==_shownId&&_shownTab==_tab&&_shown is not null)
        {
            if(ReferenceEquals(_shown,state.Snapshot))return;
            if(_tab=="球员数据"&&state.Snapshot is {} update&&_sheets.Count==2){_sheets[0].Update(update.A.Players);_sheets[1].Update(update.B.Players);if(_heading is not null)_heading.Text=Title(update.Game);if(_gameLine is not null)_gameLine.Text=Description(update.Game);_shown=update;return;}
        }
        if(_disposed)return;var offset=_scroll.VerticalOffset;foreach(var sheet in _sheets)sheet.Stop();_sheets.Clear();_playerTabs=null;_content.Children.Clear();_body.Children.Clear();_body.RowDefinitions.Clear();_body.ColumnDefinitions.Clear();
        _shown=state.Snapshot;_shownTab=_tab;_shownId=state.Game?.Id??"";if(state.Game is not {} game)return;
        var top=new DockPanel();var close=Ui.Button("收起",()=>CloseRequested?.Invoke());close.Margin=new(0);DockPanel.SetDock(close,Dock.Right);top.Children.Add(close);
        var identity=new StackPanel{Orientation=Orientation.Horizontal};identity.Children.Add(new TeamLogo{Team=game.Team.LogoTeam,Width=25,Height=25});var heading=Ui.Text(Title(game),20);_heading=heading;heading.Margin=new(8,0,8,0);identity.Children.Add(heading);identity.Children.Add(new TeamLogo{Team=game.Opponent.LogoTeam,Width=25,Height=25});top.Children.Add(identity);_content.Children.Add(top);
        var line=Ui.Text(Description(game),10);_gameLine=line;line.TextWrapping=TextWrapping.Wrap;line.Margin=new(0,3,0,4);_content.Children.Add(line);
        var tabs=new DockPanel{Margin=new(0,0,0,6)};var full=Ui.Button("完整单场统计  ›",()=>FullRequested?.Invoke());full.Margin=new(0);DockPanel.SetDock(full,Dock.Right);tabs.Children.Add(full);
        var buttons=new WrapPanel();_tabs.Clear();foreach(var name in new[]{"概览","球队对比","球员数据"}){var captured=name;var button=Ui.Button(name,()=>{_tab=captured;if(captured=="球员数据")FullRequested?.Invoke();else Render();});button.Margin=new(0,0,6,0);button.Padding=new(10,6,10,6);if(name==_tab){button.Background=Ui.Brush("AccentTint");button.Foreground=Ui.Brush("AccentInk");}_tabs[name]=button;buttons.Children.Add(button);}tabs.Children.Add(buttons);_content.Children.Add(tabs);
        if(state.Snapshot is not {} box){_content.Children.Add(Ui.Text(state.Loading?"正在读取本场统计…":state.Message.Length>0?state.Message:"本场详细数据暂未提供",12));return;}
        if(_tab=="球员数据")
        {
            var teams=new TabControl{Height=Math.Max(210,ActualHeight-140)};_playerTabs=teams;
            foreach(var t in new[]{box.A,box.B})
            {
                var sheet=new StatisticsWindow.TeamSheet(true);_sheets.Add(sheet);sheet.Update(t.Players);sheet.SetCourtStates(t.Players.Players.ToDictionary(p=>p.Id,_=>new PlayerCourtState(CourtPresence.Unknown,"历史单场归档未提供实时在场状态")));
                teams.Items.Add(new TabItem{Header=t.Team.Name+(game.Team.Key==t.Team.Key?(game.Home?"（主）":"（客）"):(game.Home?"（客）":"（主）")),Content=sheet});
            }
            _content.Children.Add(teams);
        }
        else if(_tab=="球队对比")_content.Children.Add(Comparison(box,false));
        else
        {
            _body.Children.Add(Periods(box));var comparison=Comparison(box,true);_body.Children.Add(comparison);var players=Leaders(box);_body.Children.Add(players);_content.Children.Add(_body);ArrangeBody();
        }
        Dispatcher.BeginInvoke(()=>{if(!_disposed)_scroll.ScrollToVerticalOffset(offset);},DispatcherPriority.Loaded);
    }
    void ArrangeBody()
    {
        if(_body.Children.Count!=3)return;_body.ColumnDefinitions.Clear();_body.RowDefinitions.Clear();var narrow=ActualWidth<900;
        if(narrow)for(var i=0;i<3;i++)_body.RowDefinitions.Add(new(){Height=GridLength.Auto});else for(var i=0;i<3;i++)_body.ColumnDefinitions.Add(new(){Width=new(i==0?1.3:1,GridUnitType.Star)});
        for(var i=0;i<3;i++){Grid.SetColumn(_body.Children[i],narrow?0:i);Grid.SetRow(_body.Children[i],narrow?i:0);((FrameworkElement)_body.Children[i]).Margin=new(i>0&&!narrow?14:0,i>0&&narrow?12:0,0,0);}
    }
    static string Title(TeamGame game)=>game.Team.Name+"  "+game.Score+"  "+game.Opponent.Name;
    string Description(TeamGame game)=>game.RoundName+" · "+(game.Status==GameStatus.Finished?"已结束":game.Status==GameStatus.Live?"进行中":"未开始")+" · "+game.Tipoff.ToOffset(TimeSpan.FromHours(8)).ToString("MM月dd日 HH:mm",CultureInfo.InvariantCulture)+" 北京时间 · "+game.Team.Name+(game.Home?"主场":"客场")+(_outcome.Length>0?" · "+_outcome:"");
    static FrameworkElement Periods(PlayInBoxScore box)
    {
        var root=new StackPanel();var title=Ui.Text("分节比分",12);title.FontWeight=FontWeights.SemiBold;title.Foreground=Ui.Brush("Ink");title.Margin=new(0);root.Children.Add(title);var grid=new Grid{Margin=new(0,6,0,0)};var count=Math.Max(box.A.Periods.Count,box.B.Periods.Count);
        if(count==0){root.Children.Add(Ui.Text("分节数据未提供",11));return root;}
        grid.MinWidth=110+count*36;for(var i=0;i<count+2;i++)grid.ColumnDefinitions.Add(new(){Width=new(i==0?1.5:1,GridUnitType.Star)});for(var r=0;r<3;r++)grid.RowDefinitions.Add(new(){Height=new(23)});
        for(var c=0;c<count+2;c++)for(var r=0;r<3;r++)
        {
            var team=r==1?box.A:box.B;var text=r==0?(c==0?"球队":c==count+1?"总分":c<=4?c.ToString(CultureInfo.InvariantCulture):"加"+(c-4)):
                c==0?team.Team.Name:c==count+1?team.Metric("points")?.Value?.ToString(CultureInfo.InvariantCulture)??"—":c<=team.Periods.Count?team.Periods[c-1].ToString(CultureInfo.InvariantCulture):"—";
            var cell=Ui.Text(text,11);cell.Margin=new(0);cell.Foreground=Ui.Brush(r==0?"MutedInk":"Ink");if(r>0&&c==count+1)cell.FontWeight=FontWeights.SemiBold;cell.TextAlignment=TextAlignment.Center;cell.TextTrimming=TextTrimming.CharacterEllipsis;cell.ToolTip=text;var border=new Border{Child=cell,Background=Ui.Brush(r==0?"SoftBrush":"CardBrush")};Grid.SetColumn(border,c);Grid.SetRow(border,r);grid.Children.Add(border);
        }
        root.Children.Add(new ScrollViewer{Content=grid,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});return root;
    }
    static FrameworkElement Comparison(PlayInBoxScore box,bool compact)
    {
        var root=new StackPanel();var title=Ui.Text("球队对比 · "+box.A.Team.Name+" / "+box.B.Team.Name,12);title.FontWeight=FontWeights.SemiBold;title.Foreground=Ui.Brush("Ink");title.Margin=new(0);root.Children.Add(title);
        var metrics=box.A.Metrics.Where(m=>compact?m.Key is "rebounds" or "assists" or "fieldGoal":m.Key is not ("fgm" or "fga" or "tpm" or "tpa" or "ftm" or "fta"));
        foreach(var a in metrics)
        {
            static string Display(TeamMetric? m)=>m?.Value is {} v?m.Percent?m.Display:v.ToString("0.##",CultureInfo.InvariantCulture):"—";
            var row=new DockPanel{Margin=new(0,7,0,0)};var value=Ui.Text(Display(a)+" / "+Display(box.B.Metric(a.Key)),11);value.Margin=new(0);value.Foreground=Ui.Brush("Ink");DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);var label=Ui.Text(a.Label,11);label.Margin=new(0);row.Children.Add(label);root.Children.Add(row);
        }
        if(!compact)foreach(var key in new[]{("fgm","fga","投篮"),("tpm","tpa","三分"),("ftm","fta","罚球")})
        {
            string Pair(PlayInTeamBox t)=>t.Metric(key.Item1)?.Value is {} made&&t.Metric(key.Item2)?.Value is {} attempts?$"{made}/{attempts}":"—";
            root.Children.Add(Ui.Text(key.Item3+"   "+Pair(box.A)+" / "+Pair(box.B),11));
        }
        return root;
    }
    FrameworkElement Leaders(PlayInBoxScore box)
    {
        var root=new StackPanel();var title=Ui.Text("球员表现",12);title.FontWeight=FontWeights.SemiBold;title.Foreground=Ui.Brush("Ink");title.Margin=new(0);root.Children.Add(title);
        foreach(var team in new[]{box.A,box.B})
        {
            var player=team.Players.Players.Where(p=>p.Points.HasValue).OrderByDescending(p=>p.Points).FirstOrDefault();
            var text=Ui.Text(player is null?team.Team.Name+"：数据未提供":$"{player.Name}  {player.Points}分 · {player.Rebounds?.ToString()??"—"}板 · {player.Assists?.ToString()??"—"}助",11);text.TextWrapping=TextWrapping.Wrap;text.Margin=new(0,8,0,0);root.Children.Add(text);
        }
        return root;
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_coordinator.Changed-=OnState;foreach(var sheet in _sheets)sheet.Stop();_sheets.Clear();}
}
