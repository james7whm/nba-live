using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NbaLive;

internal sealed class TeamSeasonPicker : Button
{
    private readonly Popup _popup=new(){Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
    private readonly TextBox _search=new(){Margin=new(0,0,0,8),ToolTip="输入年份，例如 2023 或 2023-24"};
    private readonly ListBox _list=new(){MaxHeight=230,BorderThickness=new(0),Background=Brushes.Transparent};
    private readonly TextBlock _label=new(){VerticalAlignment=VerticalAlignment.Center,TextAlignment=TextAlignment.Center};
    private IReadOnlyList<TeamSeason> _seasons=[];
    private string _selected="";
    internal event Action<string>? Selected;
    internal bool IsOpen=>_popup.IsOpen;
    internal TeamSeasonPicker()
    {
        Style=(Style)Application.Current.FindResource(typeof(Button));
        Width=156;Margin=new(0,0,12,0);ToolTip="选择赛季 · 支持搜索和键盘";
        HorizontalContentAlignment=HorizontalAlignment.Stretch;
        var face=new Grid();face.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});face.ColumnDefinitions.Add(new(){Width=new(16)});
        face.Children.Add(_label);
        var arrow=new System.Windows.Shapes.Path{Data=Geometry.Parse("M 0 0 L 4 4 L 8 0"),StrokeThickness=1.3,StrokeLineJoin=PenLineJoin.Round,Width=8,Height=4,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        arrow.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding(nameof(Foreground)){Source=this});Grid.SetColumn(arrow,1);face.Children.Add(arrow);Content=face;
        System.Windows.Automation.AutomationProperties.SetName(this,"选择球队统计赛季");
        System.Windows.Automation.AutomationProperties.SetName(_search,"搜索赛季");
        var body=new StackPanel {Width=250};var label=Ui.Text("搜索赛季，例如 2023",11);label.Margin=new(0,0,0,6);body.Children.Add(label);body.Children.Add(_search);body.Children.Add(_list);
        _list.DisplayMemberPath=nameof(TeamSeason.Label);
        _popup.Child=Ui.Card(body,new(12));_popup.PlacementTarget=this;_popup.VerticalOffset=5;
        Click+=(_,_)=>{_popup.IsOpen=!_popup.IsOpen;if(_popup.IsOpen){_search.Text="";Filter();Dispatcher.BeginInvoke(()=>_search.Focus());}};
        _search.TextChanged+=(_,_)=>Filter();
        _search.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Down){_list.Focus();_list.SelectedIndex=Math.Max(0,_list.SelectedIndex);e.Handled=true;}else if(e.Key==Key.Escape){Close();e.Handled=true;}};
        _list.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter){Apply();e.Handled=true;}else if(e.Key==Key.Escape){Close();e.Handled=true;}};
        _list.PreviewMouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource is DependencyObject source&&ItemsControl.ContainerFromElement(_list,source) is ListBoxItem)Apply();};
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)Close();};
        Update([],"");
    }
    internal void Close()=>_popup.IsOpen=false;
    internal void Update(IReadOnlyList<TeamSeason> seasons,string season)
    {
        if(!ReferenceEquals(_seasons,seasons)){_seasons=seasons;Filter();}
        _selected=season;_label.Text=TeamScope.ValidSeason(season)?TeamScope.ShortSeason(season):"读取赛季…";IsEnabled=seasons.Count>0;
    }
    private void Filter()
    {
        var query=new string(_search.Text.Where(char.IsDigit).ToArray());
        var options=_seasons.Where(s=>query.Length==0||(query.Length<=4?s.Id.Substring(0,4).StartsWith(query,StringComparison.Ordinal):new string(s.Id.Where(char.IsDigit).ToArray()).StartsWith(query,StringComparison.Ordinal)||new string(TeamScope.ShortSeason(s.Id).Where(char.IsDigit).ToArray()).StartsWith(query,StringComparison.Ordinal))).ToArray();
        _list.ItemsSource=options;_list.SelectedItem=options.FirstOrDefault(s=>s.Id==_selected);
    }
    private void Apply()
    {
        if(_list.SelectedItem is not TeamSeason season)return;Close();Focus();Selected?.Invoke(season.Id);
    }
}
