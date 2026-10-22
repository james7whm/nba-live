using System.Windows.Controls.Primitives;

namespace NbaLive;

internal sealed class LineupView : StackPanel
{
    private readonly TeamRow _away = new();
    private readonly TeamRow _home = new();
    internal LineupView() { Children.Add(_away); Children.Add(_home); }
    internal void Render(GameInfo? game, SessionState live, StatisticsState? statistics, DateTimeOffset now)
    {
        var snapshot = statistics?.Snapshot is { } current && current.Game.Id == game?.Id ? current : null;
        _away.Render(LineupCards.Create(snapshot?.Away, game, live, now) with { Team = game?.Away ?? "客队" }, statistics, game?.Id);
        _home.Render(LineupCards.Create(snapshot?.Home, game, live, now) with { Team = game?.Home ?? "主队" }, statistics, game?.Id);
    }
    private sealed class TeamRow : Grid
    {
        private readonly TextBlock _team = new() { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _message = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly UniformGrid _players = new() { Columns = 5 };
        private readonly JerseyBadge[] _jerseys = new JerseyBadge[5];
        private readonly TextBlock[] _names = new TextBlock[5];
        private readonly StackPanel[] _slots = new StackPanel[5];
        internal TeamRow()
        {
            Height = 46;
            ColumnDefinitions.Add(new() { Width = new(36) });
            ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Children.Add(_team);
            SetColumn(_players, 1); Children.Add(_players);
            SetColumn(_message, 1); Children.Add(_message);
            for (var i = 0; i < 5; i++)
            {
                var slot = _slots[i] = new StackPanel { Margin = new(1, 0, 1, 0) };
                _jerseys[i] = new() { HorizontalAlignment = HorizontalAlignment.Center };
                _names[i] = new() { FontSize = 10, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Height = 15 };
                slot.Children.Add(_jerseys[i]); slot.Children.Add(_names[i]); _players.Children.Add(slot);
                ToolTipService.SetInitialShowDelay(slot, 400);
            }
        }
        internal void Render(LineupCard card, StatisticsState? statistics, string? gameId)
        {
            _team.Text = card.Team + (card.IsStarterReference ? "\n首发" : ""); _team.ToolTip = card.Team + "\n" + card.Details;
            _message.Text = card.Message; _message.ToolTip = card.Details;
            _message.Visibility = card.Players.Count == 5 ? Visibility.Collapsed : Visibility.Visible;
            _players.Visibility = card.Players.Count == 5 ? Visibility.Visible : Visibility.Collapsed;
            for (var i = 0; i < card.Players.Count; i++)
            {
                var player = card.Players[i];
                _jerseys[i].Number = player.JerseyNumber ?? "-";
                _jerseys[i].Presence = card.IsStarterReference ? CourtPresence.Starter : CourtPresence.OnCourt; _jerseys[i].TeamColor = card.Color;
                _names[i].Text = player.Name.StartsWith("演示：", StringComparison.Ordinal) ? player.Name[3..] : player.Name;
                var detail = PlayerCards.Create(new(player.Id, player.Name), statistics, gameId);
                _slots[i].ToolTip = $"{player.Name} · {player.JerseyNumber ?? "-"} 号\n{detail.FirstLine}\n{detail.Details}\n{card.Details}";
                System.Windows.Automation.AutomationProperties.SetName(_jerseys[i], $"{card.Team} · {player.Name} · {player.JerseyNumber ?? "-"} 号 · {(card.IsStarterReference ? "首发参考" : "在场")}");
            }
        }
    }
}
