using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NbaLive.Core;

internal static class ChatRegression
{
    internal static async Task<int> Run(AppSettings settings)
    {
        var assembly = typeof(NbaLive.App).Assembly;
        var type = assembly.GetType("NbaLive.ChatWindow")!;
        string selectedTeam = "";
        Func<string, int, string, string?> save = (_, _, team) => { selectedTeam = team; return null; };
        var ranks = TeamCatalog.All.Select(t => new TeamStanding(t, t.Key == "76ers" ? 1 : t.Key == "knicks" ? 2 : 3, 0, 0, null, null, "", "", null, null, null, "")).ToArray();
        Func<CancellationToken, Task<TeamStandingsSnapshot>> getStandings = _ => Task.FromResult(new TeamStandingsSnapshot("2026-2027", ranks, DateTimeOffset.UtcNow));
        var window = (Window)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null,
            [settings with { ChatNickname = "球迷" }, save, getStandings, false], null)!;
        type.GetMethod("SetGame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
            [new GameInfo("123", "尼克斯", "76人", 93, 98, GameStatus.Finished, "已结束", new DateOnly(2026, 10, 31))]);
        window.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Find<Button>(window, b => System.Windows.Automation.AutomationProperties.GetName(b) == "我的资料").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var nickname = Find<TextBox>(window, t => System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("昵称"));
        var contentHost = Find<ScrollViewer>(nickname, s => s.Name == "PART_ContentHost");
        if (contentHost.ActualHeight < 20) throw new Exception("Nickname content host is clipped");
        Find<Button>(window, b => System.Windows.Automation.AutomationProperties.GetName(b) == "选择头像");
        var pickerType = assembly.GetType("NbaLive.ChatAvatarPicker")!;
        var picker = (Window)Activator.CreateInstance(pickerType, BindingFlags.NonPublic | BindingFlags.Instance, null, [0], null)!;
        picker.Show(); picker.UpdateLayout();
        Find<Button>(picker, b => System.Windows.Automation.AutomationProperties.GetName(b) == "头像球队 76人").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        picker.UpdateLayout();
        Find<Button>(picker, b => System.Windows.Automation.AutomationProperties.GetName(b) == "选择头像 詹姆斯").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if ((int)pickerType.GetProperty("SelectedAvatar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)! < 6) throw new Exception("Player avatar selection failed");
        picker.UpdateLayout(); await picker.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Snapshot(picker, settings.Theme + "-avatar-picker.png");
        var search = Find<TextBox>(picker, t => System.Windows.Automation.AutomationProperties.GetName(t) == "搜索球员或球队");
        search.Text = "库里"; picker.UpdateLayout();
        Find<Button>(picker, b => System.Windows.Automation.AutomationProperties.GetName(b) == "选择头像 库里");
        picker.Close();
        var teamGroups = (StackPanel)type.GetField("_teamGroups", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var grids = teamGroups.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().ToArray();
        if (grids.Length != 2 || grids.Any(g => g.Children.Count != 15)) throw new Exception("Expected fifteen team logos in each conference");
        var firstTeam = (Button)grids[0].Children[0];
        if (System.Windows.Automation.AutomationProperties.GetName(firstTeam) != "选择球队 76人") throw new Exception("Conference standings order was not used");
        Find<Button>(window, b => System.Windows.Automation.AutomationProperties.GetName(b) == "选择球队 76人").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        if (Count<ComboBox>(window) != 0) throw new Exception("Team dropdown should not appear");
        Snapshot(window, settings.Theme + "-profile.png");
        var messages = (System.Collections.IList)type.GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var messageType = assembly.GetType("NbaLive.ChatMessage")!;
        messages.Add(Activator.CreateInstance(messageType, [1L, "球迷甲", 0, "knicks", "最后一节真精彩 🏀😀", DateTimeOffset.Now.ToUnixTimeSeconds()])!);
        messages.Add(Activator.CreateInstance(messageType, [2L, "路人乙", 1, "", "下场比赛再见！[动:😀]", DateTimeOffset.Now.ToUnixTimeSeconds()])!);
        Find<Button>(window, b => Equals(b.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (selectedTeam != "76ers") throw new Exception("Team logo did not select 76ers");
        window.UpdateLayout();
        var composer = Find<RichTextBox>(window, t => System.Windows.Automation.AutomationProperties.GetName(t) == "聊天消息");
        var composerType = composer.GetType();
        string Value() => (string)composerType.GetProperty("Value", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composer)!;
        void Text(string value) { new System.Windows.Documents.TextRange(composer.Document.ContentStart, composer.Document.ContentEnd).Text = value; }
        composer.IsEnabled = true; Text("甲乙");
        var run = ((System.Windows.Documents.Paragraph)composer.Document.Blocks.FirstBlock).Inlines.OfType<System.Windows.Documents.Run>().First();
        composer.Focus(); composer.CaretPosition = run.ContentStart.GetPositionAtOffset(1)!; window.UpdateLayout();
        var emojiButton = Find<Button>(window, b => System.Windows.Automation.AutomationProperties.GetName(b) == "选择表情");
        if (!emojiButton.IsEnabled) throw new Exception("Emoji button did not follow input availability");
        emojiButton.Focus(); emojiButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var emojiPopup = (System.Windows.Controls.Primitives.Popup)type.GetField("_emojiPopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        if (!emojiPopup.IsOpen) throw new Exception("Emoji picker did not open");
        emojiPopup.Child.UpdateLayout();
        SnapshotElement((FrameworkElement)emojiPopup.Child, settings.Theme + "-emoji.png");
        var staticEmoji = Find<Button>(emojiPopup.Child, b => System.Windows.Automation.AutomationProperties.GetName(b) == "表情 开心");
        staticEmoji.Focus(); staticEmoji.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Value() != "甲😀乙" || emojiPopup.IsOpen || !((System.Windows.Documents.Paragraph)composer.Document.Blocks.FirstBlock).Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Any()) throw new Exception("Emoji insertion must preserve text, show image and close picker");
        emojiButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<Button>(emojiPopup.Child, b => Equals(b.Content, "动态")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        emojiPopup.Child.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var moving = Find<Button>(emojiPopup.Child, b => System.Windows.Automation.AutomationProperties.GetName(b) == "动效 开心");
        var animatedImage = (Image)moving.Content;
        if (animatedImage.RenderTransform is not RotateTransform rotating || !rotating.HasAnimatedProperties) throw new Exception("Motion emoji should animate while shown");
        var endpoint = Environment.GetEnvironmentVariable("NBALIVE_CHAT_TEST_ENDPOINT");
        using var http = new System.Net.Http.HttpClient();
        async Task<int> SentCount()
        {
            var json = await http.GetStringAsync(endpoint + "/v1/messages?room=123&after=0");
            return json.Split(new[] { "[动:😀]" }, StringSplitOptions.None).Length - 1;
        }
        var beforeSend = string.IsNullOrEmpty(endpoint) ? 0 : await SentCount();
        moving.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (rotating.HasAnimatedProperties) throw new Exception("Motion emoji should stop when picker closes");
        if (Value() != "甲😀乙") throw new Exception("Sending animated emoji must leave the draft unchanged");
        if (!string.IsNullOrEmpty(endpoint))
        {
            var sent = false;
            for (var attempt = 0; attempt < 30 && !sent; attempt++) { await Task.Delay(100); sent = await SentCount() == beforeSend + 1; }
            if (!sent || Value() != "甲😀乙") throw new Exception("Animated emoji must send immediately without sending or clearing the draft");
            await Task.Delay(2200); // Respect the service's two-second send limit.
            Find<Button>(window, b => Equals(b.Content, "发送")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var draftSent = false;
            for (var attempt = 0; attempt < 30 && !draftSent; attempt++)
            {
                await Task.Delay(100);
                var json = await http.GetStringAsync(endpoint + "/v1/messages?room=123&after=0");
                draftSent = json.Contains("甲😀乙") && Value() == "";
            }
            if (!draftSent) throw new Exception("Text with an inline emoji must send as Unicode and clear the draft");
        }
        var emojiHelper = composerType.GetMethod("InsertEmoji", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Text(new string('a', 299)); composer.CaretPosition = composer.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)!;
        if ((bool)emojiHelper.Invoke(composer, ["😀"])! || Value().Length != 299) throw new Exception("Emoji must not exceed limit or split surrogate pairs");
        run = ((System.Windows.Documents.Paragraph)composer.Document.Blocks.FirstBlock).Inlines.OfType<System.Windows.Documents.Run>().First();
        composer.Selection.Select(run.ContentStart.GetPositionAtOffset(297)!, run.ContentStart.GetPositionAtOffset(299)!);
        if (!(bool)emojiHelper.Invoke(composer, ["😀"])! || Value().Length != 299) throw new Exception("Emoji should replace selection at length limit");
        var render = assembly.GetType("NbaLive.ChatEmoji")!.GetMethod("Message", BindingFlags.Static | BindingFlags.NonPublic)!;
        var rich = (TextBlock)render.Invoke(null, ["前😀中[动:😀]后"])!;
        if (rich.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Count() != 2) throw new Exception("Static and animated emoji must render as images");
        var fallback = (TextBlock)render.Invoke(null, ["👍🏽"])!;
        if (fallback.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Any()) throw new Exception("Unsupported skin-tone sequences must remain intact");
        Text("输入测试：文字完整显示 "); emojiHelper.Invoke(composer, ["😀"]); window.UpdateLayout();
        if (Find<ScrollViewer>(composer, s => s.Name == "PART_ContentHost").ActualHeight < 20) throw new Exception("Composer content host is clipped");
        Snapshot(window, settings.Theme + "-chat.png");
        Find<Button>(window, b => System.Windows.Automation.AutomationProperties.GetName(b) == "我的资料").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Find<Button>(window, b => Equals(b.Content, "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Find<TextBlock>(window, t => t.Text.Contains("纯路人"));
        window.Close();
        Console.WriteLine("PASS Chat profile toggles and neutral team label: " + settings.Theme);
        return 1;
    }
    private static int Count<T>(DependencyObject root) where T : DependencyObject
    {
        var count = root is T ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) count += Count<T>(VisualTreeHelper.GetChild(root, i));
        return count;
    }
    private static void Snapshot(Window window, string name)
        => SnapshotElement(window, name);
    private static void SnapshotElement(FrameworkElement window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("NBALIVE_CHAT_UI_SNAPSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, name)); encoder.Save(file);
    }
    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T found && match(found)) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            try { return Find(VisualTreeHelper.GetChild(root, i), match); } catch (InvalidOperationException) { }
        throw new InvalidOperationException("Chat control not found: " + typeof(T).Name);
    }
}
