using System.Globalization;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;

namespace NbaLive;

/// <summary>A themed date selector. Browsing months is local; only committing a day changes SelectedDate.</summary>
public sealed class PremiumDatePicker : ContentControl
{
    public static readonly DependencyProperty SelectedDateProperty=DependencyProperty.Register(nameof(SelectedDate),typeof(DateTime?),typeof(PremiumDatePicker),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,DateChanged,(_,value)=>value is DateTime date?date.Date:null));
    public static readonly DependencyProperty IsDropDownOpenProperty=DependencyProperty.Register(nameof(IsDropDownOpen),typeof(bool),typeof(PremiumDatePicker),new PropertyMetadata(false,OpenChanged));
    public DateTime? SelectedDate {get=>(DateTime?)GetValue(SelectedDateProperty);set=>SetValue(SelectedDateProperty,value);}
    public bool IsDropDownOpen {get=>(bool)GetValue(IsDropDownOpenProperty);set=>SetValue(IsDropDownOpenProperty,value);}
    public event EventHandler<SelectionChangedEventArgs>? SelectedDateChanged;
    private readonly Button _trigger=new(),_previous=new(),_next=new(),_title=new();
    private readonly TextBlock _label=new() {FontSize=14,VerticalAlignment=VerticalAlignment.Center,Foreground=Ui.Brush("Ink")};
    private readonly TextBlock _chevron=new() {Text="⌄",FontSize=16,VerticalAlignment=VerticalAlignment.Center,Foreground=Ui.Brush("MutedInk")};
    private readonly Popup _popup=new() {Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
    private readonly Grid _days=new();
    private readonly Border _popupRoot;
    private DateTime _displayMonth=Today(),_focusDate=Today();
    private int _mode;
    private bool _focusAfterRender;
    private static DateTime Today()=>MatchTime.Today.ToDateTime(TimeOnly.MinValue);
    public PremiumDatePicker()
    {
        MinHeight=38; Focusable=false;
        var triggerContent=new Grid(); triggerContent.ColumnDefinitions.Add(new() {Width=new(21)}); triggerContent.ColumnDefinitions.Add(new());triggerContent.ColumnDefinitions.Add(new(){Width=new(16)});
        var glyph=new System.Windows.Shapes.Path {Data=Geometry.Parse("M2,5 L16,5 16,18 2,18Z M2,9 L16,9 M6,2 L6,7 M12,2 L12,7"),Stroke=Ui.Brush("MutedInk"),StrokeThickness=1.3,Width=16,Height=19,Stretch=Stretch.Uniform};
        triggerContent.Children.Add(glyph); Grid.SetColumn(_label,1);triggerContent.Children.Add(_label);Grid.SetColumn(_chevron,2);triggerContent.Children.Add(_chevron);
        _trigger.Content=triggerContent;_trigger.Margin=new(0);_trigger.Padding=new(10,6,10,6);_trigger.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        _trigger.Click+=(_,_)=>IsDropDownOpen=!IsDropDownOpen;
        System.Windows.Automation.AutomationProperties.SetName(_trigger,"选择比赛日期");
        var stack=new StackPanel();
        var heading=new Grid {Height=32,Margin=new(0,0,0,12)};
        heading.ColumnDefinitions.Add(new(){Width=new(30)});heading.ColumnDefinitions.Add(new());heading.ColumnDefinitions.Add(new(){Width=new(30)});
        Configure(_previous,"‹",()=>Navigate(-1));Configure(_next,"›",()=>Navigate(1));
        Configure(_title,"",()=>{_mode=(_mode+1)%3;RenderCalendar();});_title.FontSize=15;
        _previous.FontSize=_next.FontSize=23;
        heading.Children.Add(_previous);Grid.SetColumn(_title,1);heading.Children.Add(_title);Grid.SetColumn(_next,2);heading.Children.Add(_next);
        System.Windows.Automation.AutomationProperties.SetName(_previous,"上一月／年");System.Windows.Automation.AutomationProperties.SetName(_next,"下一月／年");
        stack.Children.Add(heading);stack.Children.Add(_days);
        var footer=new DockPanel {Margin=new(0,12,0,0)};
        var today=new Button();Configure(today,"回到今天",()=>Commit(Today()));today.Foreground=Ui.Brush("AccentInk");today.FontSize=12;
        DockPanel.SetDock(today,Dock.Left);footer.Children.Add(today);
        footer.Children.Add(new TextBlock {Text="选中即刷新赛程",Foreground=Ui.Brush("MutedInk"),FontSize=10,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right});
        stack.Children.Add(new Border {Child=footer,BorderBrush=Ui.Brush("LineBrush"),BorderThickness=new(0,1,0,0),Margin=new(0,10,0,0)});
        _popupRoot=Ui.Card(stack,new Thickness(16));_popupRoot.Width=304;
        _popupRoot.Effect=new DropShadowEffect {Color=Colors.Black,BlurRadius=12,ShadowDepth=3,Opacity=.22};
        _popupRoot.PreviewKeyDown+=CalendarKeyDown;
        _popup.Child=new Border {Child=_popupRoot,Padding=new(9),Background=Brushes.Transparent};
        var root=new Grid();root.Children.Add(_trigger);root.Children.Add(_popup);Content=root;_popup.PlacementTarget=_trigger;
        _popup.Opened+=(_,_)=>{_trigger.BorderBrush=Ui.Brush("AccentBrush");_chevron.Text="⌃";FocusDay();};
        _popup.Closed+=(_,_)=>{SetCurrentValue(IsDropDownOpenProperty,false);_trigger.BorderBrush=Ui.Brush("LineBrush");_chevron.Text="⌄";};
        _trigger.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)){IsDropDownOpen=true;e.Handled=true;}
            else if(e.Key==Key.Escape && IsDropDownOpen){Close();e.Handled=true;}
        };
        Unloaded+=(_,_)=>_popup.IsOpen=false;
        UpdateLabel();
    }
    private static void Configure(Button button,string label,Action click)
    {
        button.Style=(Style)Application.Current.FindResource("IconButton");button.Content=label;button.Margin=new(0);button.Padding=new(4);
        button.Click+=(_,_)=>click();
    }
    private static void DateChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var picker=(PremiumDatePicker)d;picker.UpdateLabel();
        if(e.NewValue is DateTime date){picker._displayMonth=new(date.Year,date.Month,1);picker._focusDate=date;}
        if(picker.IsDropDownOpen)picker.RenderCalendar();
        picker.SelectedDateChanged?.Invoke(picker,new(Selector.SelectionChangedEvent,e.OldValue is {} old?new[]{old}:Array.Empty<object>(),e.NewValue is {} next?new[]{next}:Array.Empty<object>()));
    }
    private static void OpenChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var picker=(PremiumDatePicker)d;
        if((bool)e.NewValue)
        {
            picker._mode=0;picker._focusDate=picker.SelectedDate??Today();picker._displayMonth=new(picker._focusDate.Year,picker._focusDate.Month,1);
            picker.RenderCalendar();picker._popup.HorizontalOffset=picker._trigger.ActualWidth-322;
        }
        picker._popup.IsOpen=(bool)e.NewValue;
    }
    private void UpdateLabel()
    {
        _label.Text=SelectedDate?.ToString("M月d日",CultureInfo.InvariantCulture)??"选择日期";
        _trigger.ToolTip=SelectedDate?.ToString("yyyy年M月d日",CultureInfo.InvariantCulture)??"选择比赛日期";
    }
    private void Commit(DateTime day){SetCurrentValue(SelectedDateProperty,day.Date);Close();}
    private void Close(){SetCurrentValue(IsDropDownOpenProperty,false);_trigger.Focus();}
    private void Navigate(int direction)
    {
        var count=_mode==0?direction:direction*(_mode==1?12:120);
        var index=(_displayMonth.Year-1)*12+_displayMonth.Month-1;
        index=RuntimeCompat.Clamp(index+count,0,9999*12-1);_displayMonth=new(index/12+1,index%12+1,1);RenderCalendar();
    }
    private void RenderCalendar()
    {
        _days.Children.Clear();_days.RowDefinitions.Clear();_days.ColumnDefinitions.Clear();
        _previous.IsEnabled=_displayMonth.Year>1||(_mode==0&&_displayMonth.Month>1);
        _next.IsEnabled=_displayMonth.Year<9999||(_mode==0&&_displayMonth.Month<12);
        if(_mode!=0){RenderPeriodChoices();return;}
        _title.Content=$"{_displayMonth.Year}年{_displayMonth.Month}月 ⌄";
        for(var i=0;i<7;i++)_days.ColumnDefinitions.Add(new());
        _days.RowDefinitions.Add(new(){Height=new(26)});
        var offset=((int)_displayMonth.DayOfWeek+6)%7;
        var rows=Math.Max(5,(offset+DateTime.DaysInMonth(_displayMonth.Year,_displayMonth.Month)+6)/7);
        for(var row=0;row<rows;row++)_days.RowDefinitions.Add(new(){Height=new(34)});
        string[] weekdays=["一","二","三","四","五","六","日"];
        for(var col=0;col<7;col++)
        {
            var label=new TextBlock {Text=weekdays[col],Foreground=Ui.Brush("MutedInk"),FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            Grid.SetColumn(label,col);_days.Children.Add(label);
        }
        for(var i=0;i<rows*7;i++)
        {
            var delta=i-offset;
            if(delta<0&&_displayMonth.Ticks<TimeSpan.TicksPerDay*-delta||delta>0&&DateTime.MaxValue.Ticks-_displayMonth.Ticks<TimeSpan.TicksPerDay*delta)continue;
            var day=_displayMonth.AddDays(delta);
            var button=new Button {Content=day.Day.ToString(CultureInfo.InvariantCulture),Style=(Style)Application.Current.FindResource("CalendarDay"),Margin=new(2),FontSize=12,
                Tag=SelectedDate==day?"selected":day==Today()?"today":day.Month!=_displayMonth.Month?"outside":"",ToolTip=day.ToString("yyyy年M月d日",CultureInfo.InvariantCulture)};
            System.Windows.Automation.AutomationProperties.SetName(button,button.ToolTip+(SelectedDate==day?"，已选":day==Today()?"，今天":""));
            button.Click+=(_,_)=>Commit(day);button.DataContext=day;
            Grid.SetRow(button,i/7+1);Grid.SetColumn(button,i%7);_days.Children.Add(button);
        }
        if(_focusAfterRender){_focusAfterRender=false;Dispatcher.BeginInvoke(FocusDay,DispatcherPriority.Loaded);}
    }
    private void RenderPeriodChoices()
    {
        for(var i=0;i<3;i++)_days.ColumnDefinitions.Add(new());for(var i=0;i<4;i++)_days.RowDefinitions.Add(new(){Height=new(44)});
        var first=Math.Max(1,(_displayMonth.Year/10)*10-1);
        var decade=(_displayMonth.Year/10)*10;
        _title.Content=_mode==1?$"{_displayMonth.Year}年 ⌄":$"{Math.Max(1,decade)}—{Math.Min(9999,decade+9)}年 ⌄";
        for(var index=0;index<12;index++)
        {
            var value=_mode==1?index+1:first+index;if(value>9999)break;
            var button=new Button {Style=(Style)Application.Current.FindResource("CalendarDay"),Content=_mode==1?$"{value}月":value.ToString(CultureInfo.InvariantCulture),Margin=new(3),FontSize=13};
            button.Click+=(_,_)=>{_displayMonth=_mode==1?new(_displayMonth.Year,value,1):new(value,_displayMonth.Month,1);_mode--;RenderCalendar();};
            Grid.SetColumn(button,index%3);Grid.SetRow(button,index/3);_days.Children.Add(button);
        }
    }
    private void FocusDay()
    {
        foreach(var child in _days.Children.OfType<Button>())if(child.DataContext is DateTime day&&day==_focusDate){child.Focus();break;}
    }
    private void CalendarKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){Close();e.Handled=true;return;}
        if(_mode!=0)return;
        if(e.Key is Key.Enter or Key.Space)
        {
            if(Keyboard.FocusedElement is Button {DataContext:DateTime day}){Commit(day);e.Handled=true;}
            return;
        }
        var delta=e.Key switch {Key.Left=>-1,Key.Right=>1,Key.Up=>-7,Key.Down=>7,Key.Home=>-((int)_focusDate.DayOfWeek+6)%7,Key.End=>6-((int)_focusDate.DayOfWeek+6)%7,_=>0};
        var next=_focusDate;
        try
        {
            if(e.Key is Key.PageUp or Key.PageDown)next=_focusDate.AddMonths(e.Key==Key.PageUp?-1:1);
            else if(delta!=0)next=_focusDate.AddDays(delta);
            else return;
        }
        catch(ArgumentOutOfRangeException){e.Handled=true;return;}
        _focusDate=next;_displayMonth=new(next.Year,next.Month,1);_focusAfterRender=true;RenderCalendar();e.Handled=true;
    }
}
