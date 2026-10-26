using System.Globalization;
using System.Windows.Input;

namespace NbaLive;

internal sealed class PlayInView:DockPanel,IDisposable
{
    readonly string _favorite;
    readonly WrapPanel _tools=new();readonly Dictionary<string,Button> _regions=[];
    readonly ScrollViewer _scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly StackPanel _scene=new();readonly TextBlock _note=Ui.Text("",11);
    readonly Border _drawer=new(){Margin=new(0,10,0,0),Visibility=Visibility.Collapsed};
    readonly PlayInDetailCoordinator _coordinator;readonly PlayInDetailView _detail;
    readonly Dictionary<string,Button> _cards=[];
    TeamInsightsState? _state;PlayInSnapshot? _snapshot;TeamScope? _scope;
    string _requested="全景",_effective="全景",_preferred="西部",_selected="",_hint="";
    string _beforeDetailRegion="全景";double _beforeDetailOffset;
    bool _active,_expanded,_full,_highlight,_queued,_disposed;
    internal FrameworkElement Tools=>_tools;
    internal event Action<string>? PlayoffRequested;
    internal PlayInView(string favorite,IPlayInDetailSource? source)
    {
        _favorite=favorite;_coordinator=new(source);_detail=new(_coordinator);
        foreach(var name in new[]{"全景","东部","西部"}){var key=name;var button=Ui.Button(name,()=>{_requested=key;_hint="";Render();});button.Margin=new(0,0,4,0);button.Padding=new(10,6,10,6);_regions[name]=button;_tools.Children.Add(button);}
        var locate=Ui.Button("定位主队",LocateFavorite);locate.Margin=new(6,0,0,0);locate.Padding=new(10,6,10,6);_tools.Children.Add(locate);
        _note.TextWrapping=TextWrapping.Wrap;_note.Margin=new(0,0,0,8);SetDock(_note,Dock.Top);Children.Add(_note);
        SetDock(_drawer,Dock.Bottom);Children.Add(_drawer);_scroll.Content=_scene;Children.Add(_scroll);_drawer.Child=Ui.Card(_detail,new(14));
        _detail.CloseRequested+=Collapse;_detail.FullRequested+=()=>{_full=true;_detail.ShowPlayers();Render();};
        SizeChanged+=(_,_)=>QueueRender();IsVisibleChanged+=(_,_)=>{Demand();if(IsVisible)QueueRender();};PreviewKeyDown+=OnKey;
    }
    internal void Update(TeamInsightsState state)
    {
        if(_disposed)return;var changed=_scope!=state.Scope||!ReferenceEquals(_snapshot,state.Standings?.PlayIn);_state=state;
        if(_scope!=state.Scope){_scope=state.Scope;_selected="";_expanded=false;_full=false;_requested="全景";_highlight=false;_hint="";_scroll.ScrollToTop();_=_coordinator.Select(null,null);}
        _snapshot=state.Standings?.PlayIn;
        if(_selected.Length==0&&state.SelectedTeam?.Conference is "东部" or "西部")_preferred=state.SelectedTeam.Conference;
        if(_selected.Length>0&&_snapshot?.Games.Any(g=>g.Id==_selected)!=true){_selected="";_expanded=false;_full=false;_=_coordinator.Select(null,null);}
        if(changed&&_expanded&&_snapshot?.Games.FirstOrDefault(g=>g.Id==_selected) is {} selected){_detail.SetOutcome(PlayInBuilder.Outcome(_snapshot,selected));_=_coordinator.Select(_snapshot.Scope,selected);}
        if(changed)QueueRender();else UpdateNote();
    }
    internal void SetActive(bool active){_active=active;Demand();}
    void Demand()=>_=_coordinator.SetVisible(_active&&IsVisible&&_expanded);
    internal void Refresh(bool force=false){if(_expanded)_=_coordinator.Refresh(force);}
    internal void RefreshTheme(){_detail.RefreshTheme();QueueRender();}
    void QueueRender()
    {
        if(_queued||!IsVisible)return;_queued=true;Dispatcher.BeginInvoke(()=>{_queued=false;if(!_disposed&&IsVisible)Render();},DispatcherPriority.Loaded);
    }
    void UpdateNote(){_note.Text=string.Join(" · ",new[]{_hint,_snapshot?.Notice??""}.Where(s=>s.Length>0));_note.Visibility=_note.Text.Length>0?Visibility.Visible:Visibility.Collapsed;}
    void Render()
    {
        if(_disposed||ActualWidth<150)return;var offset=_scroll.VerticalOffset;_scene.Children.Clear();_cards.Clear();
        _effective=_requested=="全景"&&ActualWidth<1000?_preferred:_requested;
        foreach(var item in _regions){item.Value.IsEnabled=item.Key!="全景"||ActualWidth>=1000;item.Value.Background=Ui.Brush(item.Key==_effective?"AccentTint":"CardBrush");item.Value.Foreground=Ui.Brush(item.Key==_effective?"AccentInk":"Ink");}
        UpdateNote();_drawer.Visibility=_expanded?Visibility.Visible:Visibility.Collapsed;_drawer.MaxHeight=_full?Math.Max(150,ActualHeight-10):Math.Max(150,ActualHeight*.52);_drawer.Height=_full?Math.Max(150,ActualHeight-10):double.NaN;
        _scroll.Visibility=_full?Visibility.Collapsed:Visibility.Visible;
        if(_snapshot is null||_snapshot.Games.Count==0)
        {
            var text=Ui.Text(_state?.LoadingStandings==true?"正在读取附加赛对阵…":_snapshot?.Notice??(_state?.StandingsMessage.Length>0?_state.StandingsMessage:"附加赛资料暂未提供"),14);text.TextWrapping=TextWrapping.Wrap;text.Margin=new(20);_scene.Children.Add(text);Demand();return;
        }
        if(_snapshot.Legacy){var rule=Ui.Text("2019–20 特殊赛制：第8位赢1场即可，第9位需连赢2场。",12);rule.TextWrapping=TextWrapping.Wrap;var frame=Ui.Card(rule,new(14));frame.Background=Ui.Brush("AccentTint");frame.Margin=new(0,0,0,10);_scene.Children.Add(frame);}
        foreach(var conference in new[]{"西部","东部"}.Where(c=>_effective=="全景"||c==_effective))_scene.Children.Add(Conference(conference));
        Demand();Dispatcher.BeginInvoke(()=>{if(!_disposed)_scroll.ScrollToVerticalOffset(offset);},DispatcherPriority.Loaded);
    }
    FrameworkElement Conference(string conference)
    {
        var body=new DockPanel();var games=_snapshot!.Games.Where(g=>g.Team.Conference==conference).OrderBy(g=>g.Tipoff).ToArray();
        var header=new DockPanel{Margin=new(0,0,0,10)};var count=Ui.Text(games.Length+" 场 · 单场比分",10);count.Margin=new(0);DockPanel.SetDock(count,Dock.Right);header.Children.Add(count);var title=Ui.Text(conference+"附加赛",14);title.FontWeight=FontWeights.SemiBold;title.Margin=new(0);header.Children.Add(title);SetDock(header,Dock.Top);body.Children.Add(header);
        if(games.Length==0)body.Children.Add(Ui.Text(_snapshot.ConferenceNotice(conference),12));
        else if(ActualWidth<1000)
        {
            var stack=new StackPanel{HorizontalAlignment=HorizontalAlignment.Stretch};
            foreach(var game in games){var card=GameCard(game);card.Width=double.NaN;card.HorizontalAlignment=HorizontalAlignment.Stretch;card.Margin=new(0,0,0,8);stack.Children.Add(card);var note=Ui.Text(Outcome(game),11);note.TextWrapping=TextWrapping.Wrap;note.Margin=new(4,0,4,10);stack.Children.Add(note);}
            foreach(var berth in _snapshot.Berths.Where(b=>b.Conference==conference)){var seed=Seed(berth);seed.Margin=new(0,0,0,8);stack.Children.Add(seed);}
            if(_snapshot.Legacy&&games.Length==1)stack.Children.Add(Ui.Text(LegacyNext(games),11));body.Children.Add(stack);
        }
        else
        {
            var width=Math.Max(940,ActualWidth-46);var right=width-240;var middle=(right+222)/2-111;var scene=new Canvas{Width=width,Height=188};
            void Put(UIElement e,double x,double y){Canvas.SetLeft(e,x);Canvas.SetTop(e,y);scene.Children.Add(e);}
            void Link(string geometry,bool highlight=false){scene.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse(geometry),Stroke=Ui.Brush(highlight?"AccentInk":"LineBrush"),StrokeThickness=highlight?1.8:1.3,IsHitTestVisible=false});}
            void Label(string text,double x,double y){var t=Ui.Text(text,10);t.Margin=new(0);t.Background=Ui.Brush("CardBrush");t.Padding=new(3,1,3,1);Put(t,x,y);}
            TeamGame? Role(string role)=>games.FirstOrDefault(g=>g.RoundKey==role);
            var first=_snapshot.Legacy?games.First():Role("seven-eight");var second=_snapshot.Legacy?games.Skip(1).FirstOrDefault():Role("nine-ten");var final=Role("eighth");
            if(first is not null)Put(GameCard(first),0,0);else Put(Placeholder("7–8位赛 · 参赛球队待确认"),0,0);
            if(second is not null)Put(GameCard(second),0,104);else Put(Placeholder(_snapshot.Legacy?LegacyNext(games):"9–10位赛 · 参赛球队待确认"),0,104);
            if(!_snapshot.Legacy)
            {
                if(final is not null)Put(GameCard(final),middle,104);else Put(Placeholder("第8种子争夺战\n7–8位赛负者 vs 9–10位赛胜者"),middle,104);
                if(first is not null&&second is not null&&final is not null&&PlayInBuilder.Connects(first,second,final))
                {
                    Link(FormattableString.Invariant($"M222 68 L{middle-60} 68 L{middle-60} 130 L{middle} 130"));Label("负者",middle-56,86);
                    Link(FormattableString.Invariant($"M222 160 L{middle} 160"));Label("胜者",middle-56,146);
                }
            }
            foreach(var seed in new[]{7,8})
            {
                if(_snapshot.Legacy&&seed==7)continue;var berth=_snapshot.Berths.FirstOrDefault(b=>b.Conference==conference&&b.Seed==seed);
                var y=_snapshot.Legacy?4:seed==7?4:110;
                var slot=berth is null?Placeholder("季后赛第"+seed+"种子 · 待确认"):Seed(berth);slot.Width=240;Put(slot,right,y);
                if(berth is not null)
                {
                    var x=seed==7||_snapshot.Legacy?222:middle+222;var lineY=_snapshot.Legacy||seed==7?40:146;
                    Link(FormattableString.Invariant($"M{x} {lineY} L{right} {lineY}"),_highlight&&berth.Team.Key==_favorite);Label("胜者 · 第"+seed+"种子",(x+right)/2-45,lineY-12);
                }
            }
            var unverified=games.Where(g=>g!=first&&g!=second&&g!=final).ToArray();for(var i=0;i<unverified.Length;i++)Put(GameCard(unverified[i]),i%3*(222+14),206+i/3*100);if(unverified.Length>0)scene.Height=206+Math.Ceiling(unverified.Length/3d)*100;
            body.Children.Add(scene);
        }
        var frame=Ui.Card(body,new(12));frame.Margin=new(0,0,0,10);return frame;
    }
    Button GameCard(TeamGame game)
    {
        var root=new StackPanel();var cap=new DockPanel{Margin=new(0,0,0,4)};var status=Ui.Text(game.Status==GameStatus.Finished?"已结束":game.Status==GameStatus.Live?"进行中":"未开始",9);status.Margin=new(0);SetDock(status,Dock.Right);cap.Children.Add(status);var title=Ui.Text(game.RoundName+" · "+game.Tipoff.ToOffset(TimeSpan.FromHours(8)).ToString("MM/dd",CultureInfo.InvariantCulture),10);title.Margin=new(0);cap.Children.Add(title);root.Children.Add(cap);
        foreach(var pair in new[]{(game.Team,game.TeamPoints,game.Won==true),(game.Opponent,game.OpponentPoints,game.Won==false)})
        {
            var row=new Grid{Height=24};row.ColumnDefinitions.Add(new(){Width=new(22)});row.ColumnDefinitions.Add(new(){Width=new(26)});row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=new(42)});
            var seed=Ui.Text(_snapshot?.Participants.FirstOrDefault(r=>r.Team.Key==pair.Item1.Key)?.Rank.ToString(CultureInfo.InvariantCulture)??"—",10);seed.Margin=new(0);row.Children.Add(seed);
            var logo=new TeamLogo{Team=pair.Item1.LogoTeam,Width=20,Height=20};Grid.SetColumn(logo,1);row.Children.Add(logo);
            var name=Ui.Text(pair.Item1.Name,13);name.Margin=new(0);name.FontWeight=pair.Item3?FontWeights.SemiBold:FontWeights.Normal;name.TextTrimming=TextTrimming.CharacterEllipsis;Grid.SetColumn(name,2);row.Children.Add(name);
            var score=Ui.Text(pair.Item2?.ToString(CultureInfo.InvariantCulture)??"—",16);score.Margin=new(0);score.TextAlignment=TextAlignment.Right;score.FontWeight=pair.Item3?FontWeights.SemiBold:FontWeights.Normal;Grid.SetColumn(score,3);row.Children.Add(score);root.Children.Add(row);
        }
        var button=Ui.Button("",()=>SelectGame(game.Id));button.Content=root;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Width=222;button.Height=84;button.Padding=new(9,6,9,6);button.Margin=new(0);button.Tag=game.Id;button.BorderBrush=Ui.Brush(game.Id==_selected||_highlight&&(game.Team.Key==_favorite||game.Opponent.Key==_favorite)?"AccentInk":"LineBrush");button.ToolTip=game.Description+" · "+Outcome(game);_cards[game.Id]=button;System.Windows.Automation.AutomationProperties.SetName(button,game.RoundName+" "+game.Team.Name+" "+game.Score+" "+game.Opponent.Name);return button;
    }
    FrameworkElement Seed(PlayInBerth berth)
    {
        var root=new StackPanel();var title=Ui.Text("季后赛第 "+berth.Seed+" 种子",10);title.Margin=new(0,0,0,4);root.Children.Add(title);var identity=new DockPanel();var logo=new TeamLogo{Team=berth.Team.LogoTeam,Width=22,Height=22,Margin=new(0,0,8,0)};identity.Children.Add(logo);var name=Ui.Text(berth.Team.Name,15);name.Margin=new(0);name.FontWeight=FontWeights.SemiBold;identity.Children.Add(name);root.Children.Add(identity);var next=Ui.Text(berth.Opponent is {} o?"首轮 vs "+o.Name+"  ›":"查看季后赛对阵  ›",10);next.Margin=new(0,4,0,0);root.Children.Add(next);
        var button=Ui.Button("",()=>PlayoffRequested?.Invoke(berth.Team.Key));button.Content=root;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Padding=new(12,7,12,7);button.Margin=new(0);button.Height=78;if(_highlight&&berth.Team.Key==_favorite){button.BorderBrush=Ui.Brush("AccentInk");button.Background=Ui.Brush("AccentTint");}return button;
    }
    static FrameworkElement Placeholder(string label){var text=Ui.Text(label,11);text.TextWrapping=TextWrapping.Wrap;text.Margin=new(0);var card=Ui.Card(text,new(12));card.Width=222;card.Height=78;return card;}
    string LegacyNext(TeamGame[] games)=>_snapshot!.Berths.Any(b=>b.Conference==games[0].Team.Conference&&b.Seed==8)&&games.Length==1?"第2场 · 条件未触发":"第2场 · 条件场次／资料待确认";
    string Outcome(TeamGame game)
    {
        return _snapshot is {} data?PlayInBuilder.Outcome(data,game):"晋级关系待确认";
    }
    internal void SelectGame(string id)
    {
        var game=_snapshot?.Games.FirstOrDefault(g=>g.Id==id);if(game is null)return;
        if(!_expanded){_beforeDetailRegion=_requested;_beforeDetailOffset=_scroll.VerticalOffset;}
        if(_selected!=id)_detail.Reset();_selected=id;_expanded=true;_full=false;_preferred=game.Team.Conference;_requested=_preferred;
        _detail.SetOutcome(PlayInBuilder.Outcome(_snapshot!,game));_=_coordinator.Select(_snapshot!.Scope,game);Render();Demand();
    }
    void Collapse()
    {
        _expanded=false;_full=false;_requested=_beforeDetailRegion;_detail.Reset();Demand();Render();Dispatcher.BeginInvoke(()=>_scroll.ScrollToVerticalOffset(_beforeDetailOffset),DispatcherPriority.Loaded);
    }
    internal void LocateFavorite()
    {
        var games=_snapshot?.Games.Where(g=>g.Team.Key==_favorite||g.Opponent.Key==_favorite).ToArray()??[];
        if(games.Length==0){_hint="该季主队没有已确认的附加赛记录";UpdateNote();return;}
        if(_expanded)Collapse();
        _highlight=true;_hint="已高亮主队的实际附加赛路径";_preferred=games[0].Team.Conference;_requested=ActualWidth>=1000?"全景":_preferred;Render();
        Dispatcher.BeginInvoke(()=>{if(_cards.TryGetValue(games.Last().Id,out var card)){card.BringIntoView();card.Focus();}},DispatcherPriority.Loaded);
    }
    void OnKey(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&_expanded){Collapse();e.Handled=true;return;}
        if(Keyboard.FocusedElement is not Button b||b.Tag is not string id||!_cards.ContainsKey(id))return;
        if(e.Key==Key.Enter){SelectGame(id);e.Handled=true;return;}
        if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down){var keys=_cards.Keys.ToArray();var index=Array.IndexOf(keys,id);var next=(index+(e.Key is Key.Left or Key.Up?-1:1)+keys.Length)%keys.Length;_cards[keys[next]].Focus();_cards[keys[next]].BringIntoView();e.Handled=true;}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_detail.Dispose();_coordinator.Dispose();}
}
