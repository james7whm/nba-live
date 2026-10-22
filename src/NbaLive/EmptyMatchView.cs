using System.Windows.Data;

namespace NbaLive;

/// <summary>The unselected overlay is an action, rather than three repeated status messages.</summary>
internal sealed class EmptyMatchView : Grid
{
    private readonly Viewbox _icon = new() { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, Child = new MatchGlyph() };
    private readonly TextBlock _heading = new() { Text = "还未选择比赛", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _hint = new() { Text = "比分与文字动态，随时看", FontSize = 10.5, Foreground = Ink(126, 135, 146), Margin = new(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _choose;

    internal EmptyMatchView(Action choose)
    {
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new() { Width = new(42) });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Children.Add(_icon);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0), Cursor = Cursors.Hand };
        copy.Children.Add(_heading); copy.Children.Add(_hint);
        copy.MouseLeftButtonUp += (_, e) => { choose(); e.Handled = true; };
        _icon.Cursor = Cursors.Hand;
        _icon.MouseLeftButtonUp += (_, e) => { choose(); e.Handled = true; };
        SetColumn(copy, 1); Children.Add(copy);
        _choose = ActionButton(new TextBlock { Text = "选择比赛", FontSize = 11 }, choose);
        SetColumn(_choose, 2); Children.Add(_choose);
    }

    internal void Apply(double fontSize, bool compact)
    {
        _heading.FontSize = Math.Clamp(fontSize + 1, 13, 16);
        _hint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _icon.Width = _icon.Height = compact ? 20 : 32;
        ColumnDefinitions[0].Width = new(compact ? 28 : 42);
        _choose.Height = compact ? 22 : 30;
        _choose.Padding = new(compact ? 8 : 12, 0, compact ? 8 : 12, 0);
    }

    internal static Button Capsule(Action choose)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = new(24) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new Viewbox { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Child = new MatchGlyph() });
        var label = new TextBlock { Text = "选择一场比赛", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(label, 1); row.Children.Add(label);
        var arrow = new TextBlock { Text = "›", FontSize = 16, Foreground = Ink(126, 135, 146), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(arrow, 2); row.Children.Add(arrow);
        var button = ActionButton(row, choose);
        button.Height = 24; button.Padding = new(0); button.Background = Brushes.Transparent; button.BorderThickness = new(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private static Button ActionButton(UIElement content, Action choose)
    {
        var button = new Button
        {
            Content = content, Padding = new(12, 0, 12, 0), Margin = new(0), Height = 30,
            Background = Ink(230, 234, 239), BorderBrush = Ink(207, 214, 223), BorderThickness = new(1),
            Foreground = Ink(75, 80, 88), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false, IsTabStop = false, FocusVisualStyle = null, Cursor = Cursors.Hand
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "选择比赛");
        var chrome = new FrameworkElementFactory(typeof(Border)) { Name = "Chrome" };
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        void Bind(DependencyProperty target, string source) => chrome.SetBinding(target, new Binding(source) { RelativeSource = RelativeSource.TemplatedParent });
        Bind(Border.BackgroundProperty, nameof(Control.Background)); Bind(Border.BorderBrushProperty, nameof(Control.BorderBrush));
        Bind(Border.BorderThicknessProperty, nameof(Control.BorderThickness)); Bind(Border.PaddingProperty, nameof(Control.Padding));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(HorizontalAlignmentProperty, new Binding(nameof(Control.HorizontalContentAlignment)) { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Ink(220, 226, 233), "Chrome"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Ink(182, 194, 207), "Chrome"));
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Ink(208, 217, 228), "Chrome"));
        template.Triggers.Add(hover); template.Triggers.Add(pressed);
        button.Template = template;
        button.Click += (_, _) => choose();
        return button;
    }
    private static Brush Ink(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)); brush.Freeze(); return brush;
    }
    private sealed class MatchGlyph : FrameworkElement
    {
        private static readonly Brush Fill = Ink(226, 231, 237);
        private static readonly Pen Stroke = new(Ink(136, 148, 163), 1.25);
        internal MatchGlyph() { Width = Height = 32; }
        protected override void OnRender(DrawingContext context)
        {
            context.DrawRoundedRectangle(Fill, null, new Rect(0, 0, 32, 32), 8, 8);
            context.DrawRoundedRectangle(null, Stroke, new Rect(8, 9, 16, 15), 2, 2);
            context.DrawLine(Stroke, new(8, 14), new(24, 14));
            context.DrawLine(Stroke, new(12, 7), new(12, 11));
            context.DrawLine(Stroke, new(20, 7), new(20, 11));
            context.DrawLine(Stroke, new(12, 18), new(15, 18));
            context.DrawLine(Stroke, new(18, 18), new(21, 18));
            context.DrawLine(Stroke, new(12, 21), new(15, 21));
        }
    }
}
