using System.Globalization;
using System.Windows.Input;
using System.Windows.Shapes;

namespace NbaLive;

internal sealed class PlayoffBracketView : ContentControl
{
    private const double CardWidth=132,CardHeight=56;
    private readonly DockPanel _root=new();
    private readonly Canvas _canvas=new(){Background=Brushes.Transparent};
    private readonly ScrollViewer _scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalContentAlignment=VerticalAlignment.Center};
    private readonly Border _drawer=new(){Margin=new(0,8,0,0)};
    private ScrollViewer? _gamesScroll;
    private string _drawerSeries="";
    private readonly TextBlock _note=Ui.Text("",11),_empty=Ui.Text("",14);
    private readonly WrapPanel _tools=new(){VerticalAlignment=VerticalAlignment.Center};
    private readonly Dictionary<string,Button> _regions=[];
    private readonly Dictionary<string,Button> _cards=[];
    private readonly Dictionary<string,Rect> _positions=[];
    private readonly Dictionary<string,string> _topTeams=[];
    private readonly string _favorite;
    private PlayoffBracketSnapshot? _snapshot;
    private TeamScope? _scope;
    private string _requestedRegion="全景",_effectiveRegion="全景",_preferred="西部",_selected="",_selectedGame="",_message="",_hint="";
    private bool _expanded,_highlight,_needsRender=true,_queued,_loading;
    internal event Action<string>? TeamRequested;
    internal FrameworkElement Tools=>_tools;
    internal string Region=>_effectiveRegion;
    internal string SelectedSeries=>_selected;
    internal bool Expanded=>_expanded;
    internal PlayoffBracketView(string favorite)
    {
        _favorite=favorite;
        foreach(var name in new[]{"全景","东部","西部","总决赛"})
        {
            var captured=name;var button=Ui.Button(name,()=>ChooseRegion(captured));button.Margin=new(0,0,4,0);button.Padding=new(10,6,10,6);_regions[name]=button;_tools.Children.Add(button);
        }
        var locate=Ui.Button("定位主队",LocateFavorite);locate.Padding=new(10,6,10,6);locate.Margin=new(6,0,0,0);_tools.Children.Add(locate);
        _note.TextWrapping=TextWrapping.Wrap;_note.Margin=new(0,0,0,6);DockPanel.SetDock(_note,Dock.Top);_root.Children.Add(_note);
        DockPanel.SetDock(_drawer,Dock.Bottom);_root.Children.Add(_drawer);
        var legend=Ui.Text("加粗：已确认晋级 · 数字：系列赛胜场",10);legend.Margin=new(0,6,0,0);DockPanel.SetDock(legend,Dock.Bottom);_root.Children.Add(legend);
        var scene=new Grid();_scroll.Content=_canvas;scene.Children.Add(_scroll);_empty.TextAlignment=TextAlignment.Center;_empty.VerticalAlignment=VerticalAlignment.Center;_empty.Margin=new(20);scene.Children.Add(_empty);_root.Children.Add(scene);Content=_root;
        SizeChanged+=(_,_)=>QueueRender();IsVisibleChanged+=(_,_)=>{if(IsVisible)QueueRender();};PreviewKeyDown+=OnKey;
    }
    internal void Update(TeamInsightsState state)
    {
        var data=state.Standings?.Bracket;var changed=!ReferenceEquals(data,_snapshot)||_scope!=state.Scope;
        if(_scope!=state.Scope){_selected="";_selectedGame="";_expanded=false;_highlight=false;_requestedRegion="全景";_hint="";_scroll.ScrollToHome();}
        _scope=state.Scope;_snapshot=data;_loading=state.LoadingStandings;_message=state.StandingsMessage;
        if(_selected.Length==0&&state.SelectedTeam?.Conference is "东部" or "西部")_preferred=state.SelectedTeam.Conference;
        if(data is not null&&!data.Series.Any(n=>n.Id==_selected)){_selected="";_selectedGame="";_expanded=false;}
        if(changed){_needsRender=true;QueueRender();}else UpdateNote();
    }
    internal void RefreshTheme(){_needsRender=true;QueueRender();}
    private void ChooseRegion(string region)
    {
        _requestedRegion=region;_hint="";
        var selected=_snapshot?.Series.FirstOrDefault(n=>n.Id==_selected);
        if(selected is not null&&region!="全景"&&(region=="总决赛"?selected.Stage!=3:selected.Conference!=region&&selected.Stage!=3)){_selected="";_selectedGame="";_expanded=false;}
        Render();
    }
    private void QueueRender()
    {
        _needsRender=true;if(!IsVisible||_queued)return;_queued=true;
        Dispatcher.BeginInvoke(()=>{_queued=false;if(IsVisible&&_needsRender)Render();},DispatcherPriority.Loaded);
    }
    private void UpdateNote()
    {
        _note.Text=string.Join(" · ",new[]{_hint,_snapshot?.Notice??""}.Where(s=>s.Length>0));_note.Visibility=_note.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        if(_snapshot is null||_snapshot.Series.Count==0)_empty.Text=_loading?"正在读取季后赛对阵…":_message.Length>0?_message:_snapshot?.Notice??"该赛季的完整对阵资料暂未提供，可切回球队列表。";
    }
    private void Render()
    {
        _needsRender=false;if(ActualWidth<100)return;
        var horizontal=_scroll.HorizontalOffset;var vertical=_scroll.VerticalOffset;
        _canvas.Children.Clear();_cards.Clear();_positions.Clear();_topTeams.Clear();
        var narrow=ActualWidth<1000;
        _effectiveRegion=narrow&&_requestedRegion=="全景"?_preferred:_requestedRegion;
        foreach(var pair in _regions){pair.Value.IsEnabled=pair.Key!="全景"||!narrow;pair.Value.Background=Ui.Brush(pair.Key==_effectiveRegion?"AccentTint":"CardBrush");pair.Value.Foreground=Ui.Brush(pair.Key==_effectiveRegion?"AccentInk":"Ink");}
        _regions["全景"].ToolTip=narrow?"窗口较窄，使用分区视图保持文字可读":"查看完整晋级路径";
        UpdateNote();var all=_snapshot?.Series??[];
        _empty.Visibility=all.Count==0?Visibility.Visible:Visibility.Collapsed;_scroll.Visibility=all.Count==0?Visibility.Collapsed:Visibility.Visible;
        if(all.Count==0){_drawer.Child=null;return;}
        var nodes=all.Where(n=>_effectiveRegion=="全景"||_effectiveRegion=="总决赛"?n.Stage==3||_effectiveRegion=="全景":n.Conference==_effectiveRegion||n.Stage==3).ToArray();
        if(nodes.Length==0){_empty.Text="该分组的对阵尚未确认";_empty.Visibility=Visibility.Visible;_scroll.Visibility=Visibility.Collapsed;RenderDrawer();return;}
        var depth=nodes.Where(n=>n.Stage is >=0 and <3&&n.Conference is "西部" or "东部").Select(n=>n.Stage+1).DefaultIfEmpty(0).Max();
        var full=_effectiveRegion=="全景";var finalOnly=_effectiveRegion=="总决赛";
        var columns=finalOnly?1:full?depth*2+1:depth+1;columns=Math.Max(1,columns);
        var available=Math.Max(260,ActualWidth-20);var gap=columns>1?Math.Max(8,Math.Min(32,(available-columns*CardWidth)/(columns-1))):0;
        var width=columns*CardWidth+(columns-1)*gap;var origin=Math.Max(0,(available-width)/2);var yMax=100d;
        var visibleEdges=(_snapshot?.Edges??[]).Where(e=>nodes.Any(n=>n.Id==e.From)&&nodes.Any(n=>n.Id==e.To)).ToArray();
        double X(int column)=>origin+column*(CardWidth+gap);
        void Header(string text,int column){var title=Ui.Text(text,11);title.Margin=new(0);title.Width=CardWidth;title.TextAlignment=TextAlignment.Center;Put(title,X(column),6);}
        string RoundTitle(int stage)=>stage switch{0=>"首轮",1=>"次轮",2=>"分区决赛",_=>"轮次待确认"};
        foreach(var side in new[]{"西部","东部"})
        {
            if(finalOnly||!full&&side!=_effectiveRegion)continue;
            var members=nodes.Where(n=>n.Conference==side&&n.Stage is >=0 and <3).ToArray();var counter=0;
            var children=members.ToDictionary(n=>n.Id,n=>visibleEdges.Where(e=>e.To==n.Id).Select(e=>members.FirstOrDefault(c=>c.Id==e.From)).Where(c=>c is not null).Select(c=>c!).ToArray());
            var minima=new Dictionary<string,int>();
            int Minimum(PlayoffBracketSeries n)
            {
                if(minima.TryGetValue(n.Id,out var seed))return seed;
                return minima[n.Id]=children[n.Id].Length>0?children[n.Id].Min(Minimum):Math.Min(n.SeedA??99,n.SeedB??99);
            }
            double Place(PlayoffBracketSeries n)
            {
                if(_positions.TryGetValue(n.Id,out var existing))return existing.Y;
                var incoming=children[n.Id].OrderBy(Minimum).ThenBy(c=>c.Id,StringComparer.Ordinal).ToArray();
                var y=incoming.Length>0?incoming.Select(Place).Average():32+counter++*66;
                var column=full&&side=="东部"?columns-1-n.Stage:n.Stage;
                _positions[n.Id]=new(X(column),y,CardWidth,CardHeight);
                var top=incoming.Length>0?visibleEdges.FirstOrDefault(e=>e.From==incoming[0].Id&&e.To==n.Id)?.TeamKey:null;
                _topTeams[n.Id]=top is not null&&n.HasTeam(top)?top:n.A.Key;
                yMax=Math.Max(yMax,y+CardHeight+8);return y;
            }
            foreach(var node in members.Where(n=>!visibleEdges.Any(e=>e.From==n.Id&&members.Any(p=>p.Id==e.To))).OrderBy(Minimum).ThenBy(n=>n.Id,StringComparer.Ordinal))Place(node);
            foreach(var node in members)Place(node);
            for(var stage=0;stage<depth;stage++)Header(side+RoundTitle(stage),full&&side=="东部"?columns-1-stage:stage);
        }
        var finals=nodes.Where(n=>n.Stage==3).ToArray();var center=full?depth:finalOnly?0:depth;Header(finals.Length>0?"总决赛":"后续对阵待确认",center);
        var roots=_positions.Where(p=>!visibleEdges.Any(e=>e.From==p.Key&&nodes.Any(n=>n.Id==e.To&&n.Stage<3))).Select(p=>p.Value.Y).ToArray();
        var finalY=roots.Length>0?roots.Average():84;
        for(var i=0;i<finals.Length;i++){_positions[finals[i].Id]=new(X(center),finalY+i*82,CardWidth,CardHeight);_topTeams[finals[i].Id]=finals[i].A.Key;yMax=Math.Max(yMax,finalY+i*82+CardHeight+8);}
        foreach(var e in visibleEdges)
        {
            if(!_positions.TryGetValue(e.From,out var from)||!_positions.TryGetValue(e.To,out var to))continue;
            var forward=from.X<to.X;var x1=forward?from.Right:from.Left;var x2=forward?to.Left:to.Right;var middle=(x1+x2)/2;
            var geometry=Geometry.Parse(FormattableString.Invariant($"M{x1},{from.Y+CardHeight/2} L{middle},{from.Y+CardHeight/2} L{middle},{to.Y+CardHeight/2} L{x2},{to.Y+CardHeight/2}"));
            var highlight=_highlight&&e.TeamKey==_favorite;
            _canvas.Children.Add(new System.Windows.Shapes.Path{Data=geometry,Stroke=Ui.Brush(highlight?"AccentInk":"LineBrush"),StrokeThickness=highlight?2:1.2,IsHitTestVisible=false});
        }
        var unclassified=nodes.Where(n=>!_positions.ContainsKey(n.Id)).ToArray();
        for(var i=0;i<unclassified.Length;i++){_positions[unclassified[i].Id]=new(X(i%columns),yMax+32+i/columns*76,CardWidth,CardHeight);_topTeams[unclassified[i].Id]=unclassified[i].A.Key;}
        if(unclassified.Length>0)yMax=_positions.Values.Max(p=>p.Bottom)+8;
        foreach(var node in nodes)
        {
            var rect=_positions[node.Id];var button=Card(node);_cards[node.Id]=button;Put(button,rect.X,rect.Y);
        }
        if(_snapshot?.ChampionKey is {} champion&&finals.Length==1&&_positions.TryGetValue(finals[0].Id,out var final))
        {
            var team=finals[0].A.Key==champion?finals[0].A:finals[0].B;var text=Ui.Text("总冠军 · "+team.Name,10);text.Margin=new(0);text.Foreground=Ui.Brush("AccentInk");
            Put(new Border{Child=text,Background=Ui.Brush("AccentTint"),CornerRadius=new(4),Padding=new(6,3,6,3)},final.X,Math.Max(28,final.Y-25));
        }
        _canvas.Width=Math.Max(width+origin,available);_canvas.Height=yMax;
        RenderDrawer();Dispatcher.BeginInvoke(()=>{if(IsVisible){_scroll.ScrollToHorizontalOffset(horizontal);_scroll.ScrollToVerticalOffset(vertical);}},DispatcherPriority.Loaded);
    }
    private void Put(UIElement element,double x,double y){Canvas.SetLeft(element,x);Canvas.SetTop(element,y);_canvas.Children.Add(element);}
    private (TeamIdentity First,TeamIdentity Second,int Wins,int Losses) Order(PlayoffBracketSeries n)=>_topTeams.GetValueOrDefault(n.Id)==n.B.Key?(n.B,n.A,n.WinsB,n.WinsA):(n.A,n.B,n.WinsA,n.WinsB);
    private Button Card(PlayoffBracketSeries n)
    {
        var order=Order(n);var rows=new StackPanel();rows.Children.Add(TeamRow(n,order.First,order.Wins));rows.Children.Add(TeamRow(n,order.Second,order.Losses));
        var button=Ui.Button("",()=>SelectSeries(n.Id));button.Content=rows;button.Width=CardWidth;button.Height=CardHeight;button.MinWidth=0;button.MinHeight=0;button.Padding=new(5,4,5,4);button.Margin=new(0);button.Tag=n.Id;
        button.Background=Ui.Brush("CardBrush");button.BorderBrush=Ui.Brush(n.Id==_selected||_highlight&&n.HasTeam(_favorite)?"AccentInk":"LineBrush");button.BorderThickness=new(n.Id==_selected?2:1);
        button.ToolTip=$"{n.Round} · {n.A.FullName} {n.Score} {n.B.FullName} · {n.Status}";
        System.Windows.Automation.AutomationProperties.SetName(button,$"{n.Round} {n.A.Name} {n.Score} {n.B.Name} {n.Status}");return button;
    }
    private Border TeamRow(PlayoffBracketSeries n,TeamIdentity team,int wins)
    {
        var eliminated=n.WinnerKey is not null&&n.WinnerKey!=team.Key;var winner=n.WinnerKey==team.Key;
        var grid=new Grid{Height=22};foreach(var width in new[]{11d,19,1,18})grid.ColumnDefinitions.Add(new(){Width=width==1?new(1,GridUnitType.Star):new(width)});
        var seed=team.Key==n.A.Key?n.SeedA:n.SeedB;grid.Children.Add(new TextBlock{Text=n.Stage==0?seed?.ToString(CultureInfo.InvariantCulture)??"—":"",FontSize=9,VerticalAlignment=VerticalAlignment.Center,Foreground=Ui.Brush("MutedInk")});
        var logo=new TeamLogo{Team=team.LogoTeam,Width=16,Height=16,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(logo,1);grid.Children.Add(logo);
        var name=new TextBlock{Text=team.Name,FontSize=11.5,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,FontWeight=winner?FontWeights.SemiBold:FontWeights.Normal,Foreground=Ui.Brush(eliminated?"MutedInk":"Ink")};Grid.SetColumn(name,2);grid.Children.Add(name);
        var score=new TextBlock{Text=n.ScoreReliable?wins.ToString(CultureInfo.InvariantCulture):"—",FontSize=13,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center,FontWeight=winner?FontWeights.SemiBold:FontWeights.Normal,Foreground=Ui.Brush(eliminated?"MutedInk":"Ink")};Grid.SetColumn(score,3);grid.Children.Add(score);
        return new(){Child=grid,CornerRadius=new(3),Background=_highlight&&team.Key==_favorite?Ui.Brush("AccentTint"):Brushes.Transparent};
    }
    internal void SelectSeries(string key)
    {
        if(_snapshot?.Series.Any(n=>n.Id==key)!=true)return;
        var node=_snapshot.Series.Single(n=>n.Id==key);if(node.Conference is "东部" or "西部")_preferred=node.Conference;
        if(_selected!=key)_selectedGame="";_selected=key;_expanded=true;_hint="";Render();
    }
    private void CollapseDetail(){_expanded=false;_selectedGame="";Render();if(_cards.TryGetValue(_selected,out var card))card.Focus();}
    private void RenderDrawer()
    {
        var node=_snapshot?.Series.FirstOrDefault(n=>n.Id==_selected);
        var gameOffset=_drawerSeries==_selected?_gamesScroll?.HorizontalOffset??0:0;_drawerSeries=_selected;_gamesScroll=null;
        if(!_expanded||node is null){var expand=Ui.Button(node is null?"点击任一对阵，查看系列赛详情  ⌃":"展开系列赛详情  ⌃",()=>{if(node is not null){_expanded=true;Render();}});expand.HorizontalContentAlignment=HorizontalAlignment.Left;expand.Margin=new(0);expand.IsEnabled=node is not null;_drawer.Child=expand;return;}
        var order=Order(node);var content=new StackPanel();var header=new DockPanel();var collapse=Ui.Button("收起  ⌄",CollapseDetail);collapse.Margin=new(0);DockPanel.SetDock(collapse,Dock.Right);header.Children.Add(collapse);
        var identity=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        identity.Children.Add(new TeamLogo{Team=order.First.LogoTeam,Width=24,Height=24,Margin=new(0,0,8,0)});
        var title=Ui.Text(order.First.Name+"  "+(node.ScoreReliable?$"{order.Wins} : {order.Losses}":"— : —")+"  "+order.Second.Name,20);title.Margin=new(0);identity.Children.Add(title);
        identity.Children.Add(new TeamLogo{Team=order.Second.LogoTeam,Width=24,Height=24,Margin=new(8,0,0,0)});header.Children.Add(identity);content.Children.Add(header);
        var description=Ui.Text(node.Round+" · "+node.Status+" · 比分顺序："+order.First.Name+" : "+order.Second.Name,10);description.Margin=new(0,4,0,8);content.Children.Add(description);
        var games=new DockPanel();var actions=new StackPanel{Orientation=Orientation.Horizontal};
        foreach(var team in new[]{order.First,order.Second}){var key=team.Key;var button=Ui.Button(team.Name+"统计",()=>TeamRequested?.Invoke(key));button.Margin=new(6,0,0,0);button.Padding=new(10,7,10,7);actions.Children.Add(button);}DockPanel.SetDock(actions,Dock.Right);games.Children.Add(actions);
        var chips=new StackPanel{Orientation=Orientation.Horizontal};
        foreach(var game in node.Games)
        {
            var label=new StackPanel();var number=Ui.Text(game.GameNumber is {} n?$"G{n}":"比赛",10);number.Margin=new(0);number.TextAlignment=TextAlignment.Center;label.Children.Add(number);
            var score=Ui.Text(game.Status==GameStatus.Scheduled?"待定／未开始":Score(game,order.First.Key),12);score.Margin=new(0);score.TextAlignment=TextAlignment.Center;label.Children.Add(score);
            var button=Ui.Button("",()=>{_selectedGame=game.Id;RenderDrawer();});button.Content=label;button.Width=104;button.Margin=new(0,0,8,0);button.Padding=new(7);button.ToolTip=Description(game,order.First,order.Second);if(game.Id==_selectedGame)button.BorderBrush=Ui.Brush("AccentInk");chips.Children.Add(button);
        }
        var gameScroll=new ScrollViewer{Content=chips,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};_gamesScroll=gameScroll;
        games.Children.Add(gameScroll);content.Children.Add(games);
        if(node.Games.FirstOrDefault(g=>g.Id==_selectedGame) is {} selected){var line=Ui.Text(Description(selected,order.First,order.Second),11);line.Margin=new(0,8,0,0);content.Children.Add(line);}
        _drawer.Child=Ui.Card(content,new(12));
        Dispatcher.BeginInvoke(()=>gameScroll.ScrollToHorizontalOffset(gameOffset),DispatcherPriority.Loaded);
    }
    private static string Score(TeamGame g,string first)=>g.TeamPoints is {} a&&g.OpponentPoints is {} b?first==g.Team.Key?$"{a} : {b}":$"{b} : {a}":"—";
    private static string Description(TeamGame g,TeamIdentity first,TeamIdentity second)=>g.Tipoff.ToOffset(TimeSpan.FromHours(8)).ToString("MM月dd日 HH:mm",CultureInfo.InvariantCulture)+" 北京时间 · "+first.Name+((g.Team.Key==first.Key?g.Home:!g.Home)?"主场":"客场")+" · "+first.Name+" "+Score(g,first.Key)+" "+second.Name;
    internal void LocateFavorite()
    {
        var nodes=_snapshot?.Series.Where(n=>n.HasTeam(_favorite)).ToArray()??[];
        if(nodes.Length==0){_hint=_favorite=="unknown"?"请先在设置中选择主队":"该季主队没有已确认的季后赛对阵";UpdateNote();return;}
        var node=nodes.OrderByDescending(n=>n.Stage).First();_highlight=true;_selected=node.Id;_expanded=false;_hint="已高亮主队的已确认晋级路径";
        _preferred=node.Conference is "东部" or "西部"?node.Conference:nodes.FirstOrDefault(n=>n.Conference is "东部" or "西部")?.Conference??_preferred;_requestedRegion=ActualWidth>=1000?"全景":_preferred;Render();
        Dispatcher.BeginInvoke(()=>{if(_positions.TryGetValue(node.Id,out var rect)){_scroll.ScrollToVerticalOffset(Math.Max(0,rect.Y-_scroll.ViewportHeight/2));if(_cards.TryGetValue(node.Id,out var button))button.Focus();}},DispatcherPriority.Loaded);
    }
    private void OnKey(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&_expanded){CollapseDetail();e.Handled=true;return;}
        if(Keyboard.FocusedElement is not Button button||button.Tag is not string id||!_positions.TryGetValue(id,out var current))return;
        if(e.Key==Key.Enter){SelectSeries(id);if(_cards.TryGetValue(id,out var selected))selected.Focus();e.Handled=true;return;}
        var candidates=_positions.Where(p=>p.Key!=id).Where(p=>e.Key switch{Key.Left=>p.Value.X<current.X-1,Key.Right=>p.Value.X>current.X+1,Key.Up=>p.Value.Y<current.Y-1,Key.Down=>p.Value.Y>current.Y+1,_=>false});
        var next=candidates.OrderBy(p=>e.Key is Key.Left or Key.Right?Math.Abs(p.Value.X-current.X)+2*Math.Abs(p.Value.Y-current.Y):Math.Abs(p.Value.Y-current.Y)+2*Math.Abs(p.Value.X-current.X)).FirstOrDefault();
        if(next.Key is not null&&_cards.TryGetValue(next.Key,out var target)){target.Focus();target.BringIntoView();e.Handled=true;}
    }
}
