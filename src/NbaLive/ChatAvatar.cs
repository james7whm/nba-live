using System.Windows.Media.Imaging;

namespace NbaLive;

internal sealed record AvatarChoice(int Id, string Name, string Team, string Sheet, int Column, int Row, int Columns, int Rows);

internal static class ChatAvatar
{
    private static readonly Dictionary<string, BitmapSource> Sheets = new();
    internal static readonly IReadOnlyList<AvatarChoice> Choices = BuildChoices();

    private static IReadOnlyList<AvatarChoice> BuildChoices()
    {
        var result = new List<AvatarChoice>();
        for (var i = 0; i < 6; i++) result.Add(new(i, "默认头像 " + (i + 1), "", "ChatAvatars", i % 3, i / 3, 3, 2));
        void Sheet(int group, string sheet, int cols, int rows, params string[] entries)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].Length == 0) continue;
                var fields = entries[i].Split('|');
                result.Add(new(6 + group * 6 + i, fields[1], fields[0], sheet, i % cols, i / cols, cols, rows));
            }
        }
        Sheet(0,"warriors",3,1,"warriors|库里","warriors|巴特勒","warriors|格林");
        Sheet(1,"west",3,2,"nuggets|约基奇","nuggets|穆雷","timberwolves|爱德华兹","timberwolves|鲍尔","thunder|亚历山大","thunder|霍姆格伦");
        Sheet(2,"atlantic",3,2,"knicks|布伦森","knicks|唐斯","raptors|伦纳德","raptors|巴恩斯","bulls|吉迪","bulls|布泽利斯");
        Sheet(3,"east",3,2,"76ers|马克西","76ers|埃奇康姆","celtics|塔图姆","celtics|怀特","nets|兰德尔","nets|波特");
        Sheet(4,"central",3,2,"cavaliers|米切尔","cavaliers|哈登","pistons|坎宁安","pistons|杜伦","pacers|哈利伯顿","pacers|西亚卡姆");
        Sheet(5,"southeast",3,2,"bucks|希罗","bucks|特纳","hawks|约翰逊","hawks|丹尼尔斯","hornets|米勒","hornets|怀特");
        Sheet(6,"southwest",3,2,"magic|班凯罗","magic|瓦格纳","wizards|戴维斯","wizards|特雷·杨","mavericks|欧文","mavericks|弗拉格");
        Sheet(7,"northwest",3,2,"blazers|杨瀚森","blazers|莫兰特","blazers|利拉德","jazz|马尔卡宁","jazz|杰克逊","thunder|杰伦·威廉姆斯");
        Sheet(8,"pacific",3,2,"lakers|东契奇","lakers|里夫斯","clippers|英格拉姆","clippers|加兰","suns|布克","suns|杰伦·格林");
        Sheet(9,"texas",3,2,"rockets|杜兰特","rockets|申京","kings|萨博尼斯","kings|拉文","spurs|文班亚马","");
        Sheet(10,"south",2,2,"grizzlies|卡梅伦·布泽尔","grizzlies|埃迪","pelicans|锡安","pelicans|普尔");
        Sheet(11,"stars",5,1,"76ers|詹姆斯","76ers|杰伦·布朗","76ers|恩比德","heat|字母哥","heat|阿德巴约");
        return result;
    }

    private static BitmapSource LoadSheet(AvatarChoice choice)
    {
        if (Sheets.TryGetValue(choice.Sheet, out var existing)) return existing;
        var image = new BitmapImage(); image.BeginInit();
        var path = choice.Sheet == "ChatAvatars" ? "Assets/ChatAvatars.png" : "Assets/PlayerAvatars/" + choice.Sheet + ".png";
        image.UriSource = new Uri("pack://application:,,,/NbaLive;component/" + path, UriKind.Absolute);
        image.DecodePixelWidth = choice.Columns * 256; image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit(); image.Freeze(); Sheets[choice.Sheet] = image; return image;
    }

    internal static Border Create(int index, double size, bool circular = false)
    {
        var choice = Choices.FirstOrDefault(x => x.Id == index) ?? Choices[0];
        var image = LoadSheet(choice); var w = image.PixelWidth / choice.Columns; var h = image.PixelHeight / choice.Rows;
        var cropped = new CroppedBitmap(image, new Int32Rect(choice.Column * w, choice.Row * h, w, h)); cropped.Freeze();
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(circular ? size / 2 : 10),
            Background = new ImageBrush(cropped) { Stretch = choice.Sheet == "stars" ? Stretch.Uniform : Stretch.UniformToFill }, SnapsToDevicePixels = true };
    }
}
