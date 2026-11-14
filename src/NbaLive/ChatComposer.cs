using System.Text;
using System.Windows.Documents;

namespace NbaLive;

internal sealed class ChatComposer : RichTextBox
{
    internal const int MaximumLength = 300;
    internal ChatComposer()
    {
        BorderThickness = new(0); Background = Brushes.Transparent; Foreground = Ui.Brush("Ink");
        FontSize = 14; Padding = new(0); Height = 26; AcceptsReturn = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Document = new FlowDocument(new Paragraph { Margin = new(0), LineHeight = 23 }) { PagePadding = new(0) };
        PreviewTextInput += (_, e) => { if (Value.Length - SelectionValue().Length + e.Text.Length > MaximumLength) e.Handled = true; };
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
            var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string ?? "";
            if (Value.Length - SelectionValue().Length + text.Length > MaximumLength) { e.CancelCommand(); return; }
            e.FormatToApply = DataFormats.UnicodeText;
        });
        DataObject.AddCopyingHandler(this, (_, e) =>
        {
            var text = SelectionValue();
            e.DataObject.SetData(DataFormats.UnicodeText, text);
        });
        System.Windows.Automation.AutomationProperties.SetName(this, "聊天消息");
    }

    internal string Value => Read(Document.ContentStart, Document.ContentEnd).TrimEnd('\n');
    private string SelectionValue() => Read(Selection.Start, Selection.End);
    internal void ClearValue() { Document.Blocks.Clear(); Document.Blocks.Add(new Paragraph { Margin = new(0), LineHeight = 23 }); }

    internal bool InsertEmoji(string symbol)
    {
        if (!IsEnabled || IsReadOnly || Value.Length - SelectionValue().Length + symbol.Length > MaximumLength) return false;
        Selection.Text = "";
        var image = ChatEmoji.Picture(symbol, 21); image.ToolTip = null;
        var inline = new InlineUIContainer(image, Selection.Start) { Tag = symbol, BaselineAlignment = BaselineAlignment.Center };
        CaretPosition = inline.ElementEnd.GetInsertionPosition(LogicalDirection.Forward) ?? inline.ElementEnd;
        Focus(); return true;
    }

    private static string Read(TextPointer start, TextPointer end)
    {
        var result = new StringBuilder(); var cursor = start;
        while (cursor.CompareTo(end) < 0)
        {
            var context = cursor.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var text = cursor.GetTextInRun(LogicalDirection.Forward);
                var count = Math.Min(text.Length, cursor.GetOffsetToPosition(end));
                result.Append(text.Substring(0, count)); cursor = cursor.GetPositionAtOffset(count)!; continue;
            }
            if (context == TextPointerContext.ElementStart && cursor.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer inline)
            {
                if (inline.Tag is string symbol) result.Append(symbol);
                cursor = inline.ElementEnd; continue;
            }
            if (context == TextPointerContext.ElementEnd && cursor.GetAdjacentElement(LogicalDirection.Forward) is Paragraph &&
                cursor.GetNextContextPosition(LogicalDirection.Forward) is { } next && next.CompareTo(end) < 0) result.Append('\n');
            cursor = cursor.GetNextContextPosition(LogicalDirection.Forward) ?? end;
        }
        return result.ToString();
    }
}
