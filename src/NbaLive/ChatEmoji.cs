using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace NbaLive;

internal static class ChatEmoji
{
    internal static readonly (string Symbol, string Name)[] Choices =
    [
        ("😀", "开心"), ("😂", "笑哭"), ("😊", "微笑"), ("😍", "喜欢"), ("🤔", "思考"), ("😮", "惊讶"),
        ("😭", "大哭"), ("😅", "无奈"), ("😎", "酷"), ("🙌", "庆祝"), ("👏", "鼓掌"), ("👍", "赞"),
        ("👎", "踩"), ("🔥", "火热"), ("❤️", "爱心"), ("🏀", "篮球"), ("🎉", "庆祝"), ("💪", "加油"),
        ("👀", "关注"), ("🤣", "笑翻"), ("😤", "生气"), ("🥲", "感动"), ("🤯", "震惊"), ("🙏", "祈祷"),
        ("😃", "大笑"), ("😄", "笑脸"), ("😁", "咧嘴笑"), ("😆", "眯眼笑"), ("😉", "眨眼"), ("😘", "飞吻"),
        ("😗", "亲亲"), ("😋", "好吃"), ("😛", "吐舌"), ("😜", "眨眼吐舌"), ("🤪", "搞怪"), ("🙂", "浅笑"),
        ("🙃", "倒脸"), ("🤗", "拥抱"), ("🤭", "偷笑"), ("🤫", "嘘"), ("🤨", "疑惑"), ("😑", "无语"),
        ("😒", "不满"), ("😔", "低落"), ("😪", "困倦"), ("🤤", "流口水"), ("😴", "睡觉"), ("🙄", "翻白眼"),
        ("😬", "尴尬"), ("😢", "流泪"), ("😠", "恼火"), ("😡", "愤怒"), ("😇", "天使"), ("🥰", "心动"),
        ("🤩", "星星眼"), ("🤬", "骂人"), ("😈", "坏笑"), ("🤑", "发财")
    ];

    private static readonly Dictionary<string, BitmapSource> Images = new(StringComparer.Ordinal);
    internal static string AssetName(string symbol)
    {
        var codepoints = new List<string>();
        for (var i = 0; i < symbol.Length; i++)
        {
            var value = char.ConvertToUtf32(symbol, i);
            if (char.IsHighSurrogate(symbol[i])) i++;
            codepoints.Add(value.ToString("x"));
        }
        return string.Join("-", codepoints);
    }

    internal static Image Picture(string symbol, double size, bool animated = false)
    {
        if (!Images.TryGetValue(symbol, out var source))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit();
            bitmap.UriSource = new Uri("pack://application:,,,/NbaLive;component/Assets/Emoji/" + AssetName(symbol) + ".png");
            bitmap.DecodePixelWidth = 64; bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
            Images[symbol] = source = bitmap;
        }
        var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(.5, .7), ToolTip = Choices.FirstOrDefault(c => c.Symbol == symbol).Name };
        if (animated)
        {
            var rotation = new RotateTransform(); image.RenderTransform = rotation;
            void UpdateAnimation()
            {
                if (image.IsVisible && image.IsLoaded)
                    rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-9, 9, TimeSpan.FromMilliseconds(420))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                else rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            image.Loaded += (_, _) => UpdateAnimation(); image.Unloaded += (_, _) => UpdateAnimation();
            image.IsVisibleChanged += (_, _) => UpdateAnimation();
        }
        return image;
    }

    internal static TextBlock Message(string text)
    {
        var budget = 12;
        return MessageWithBudget(text, ref budget);
    }

    internal static TextBlock MessageWithBudget(string text, ref int motionBudget)
    {
        var block = Ui.Text("", 14); block.Margin = new(0); block.TextWrapping = TextWrapping.Wrap;
        System.Windows.Automation.AutomationProperties.SetName(block, text);
        var plain = new System.Text.StringBuilder();
        void Flush() { if (plain.Length > 0) { block.Inlines.Add(new Run(plain.ToString())); plain.Clear(); } }
        for (var offset = 0; offset < text.Length;)
        {
            var dynamicPrefix = text.IndexOf("[动:", offset, StringComparison.Ordinal) == offset;
            var start = dynamicPrefix ? offset + 3 : offset;
            var match = Choices.Select(c => c.Symbol).OrderByDescending(s => s.Length).FirstOrDefault(s =>
                start + s.Length <= text.Length && string.CompareOrdinal(text, start, s, 0, s.Length) == 0 &&
                (!dynamicPrefix || start + s.Length < text.Length && text[start + s.Length] == ']') &&
                (dynamicPrefix || start + s.Length == text.Length ||
                    text[start + s.Length] != '\u200d' && text[start + s.Length] != '\ufe0f' &&
                    !(char.IsHighSurrogate(text[start + s.Length]) && start + s.Length + 1 < text.Length &&
                      char.IsLowSurrogate(text[start + s.Length + 1]) && char.ConvertToUtf32(text, start + s.Length) is >= 0x1f3fb and <= 0x1f3ff)));
            if (match is null) { plain.Append(text[offset++]); continue; }
            var animate = dynamicPrefix && motionBudget > 0;
            if (animate) motionBudget--;
            Flush(); block.Inlines.Add(new InlineUIContainer(Picture(match, dynamicPrefix ? 30 : 21, animate)) { BaselineAlignment = BaselineAlignment.Center });
            offset = start + match.Length + (dynamicPrefix ? 1 : 0);
        }
        Flush(); return block;
    }

}
