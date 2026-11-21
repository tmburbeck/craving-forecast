using CravingForecast.Core;
using CravingForecast.Mobile.Services;

namespace CravingForecast.Mobile.Pages;

/// <summary>The check-in sheet opened from the + button: a 1–10 rating with optional context.</summary>
public sealed class CheckInPage : ContentPage
{
    private static readonly string[] Words =
        ["Pick a number", "Barely there", "Barely there", "Mild", "Mild", "Noticeable", "Noticeable", "Strong", "Strong", "Very strong", "Hard to ignore"];

    private readonly ForecastStore _store;
    private readonly HashSet<Trigger> _tags = [];
    private int _level;
    private bool _done;
    private bool _busy;

    public CheckInPage(ForecastStore store)
    {
        _store = store;
        Render();
    }

    private async void Close()
    {
        if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync();
    }

    private void Render()
    {
        var header = new Grid { Padding = new Thickness(20, 16, 20, 8) };
        header.Add(UI.IconButton(Icons.Close, "Close", Close).Also(b => b.HorizontalOptions = LayoutOptions.Start));
        header.Add(UI.Text(DateTime.Now.ToString("h:mm tt"), 13, Weight.SemiBold, Theme.Muted).Also(l => { l.HorizontalOptions = LayoutOptions.Center; l.VerticalOptions = LayoutOptions.Center; }));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(header, 0, 0);

        if (_done)
        {
            var title = UI.Heading("Logged", 26);
            var text = UI.Text("Your forecast just updated. Check-ins at this hour now count for more of your personal pattern.", 15, Weight.Regular, Theme.Body);
            title.HorizontalTextAlignment = text.HorizontalTextAlignment = TextAlignment.Center;
            text.MaximumWidthRequest = 280;
            var back = UI.Primary("Back to today", Close, background: Theme.Forest);
            back.WidthRequest = 220;
            var tile = UI.Tile(Icons.Check, Theme.GreenSoft, Theme.GreenText, 72, 24, 34, 2.4);
            var done = UI.Stack(14, tile, title, text, back);
            foreach (var v in done.Children.OfType<View>()) v.HorizontalOptions = LayoutOptions.Center;
            done.VerticalOptions = LayoutOptions.Center;
            done.Padding = new Thickness(24, 0, 24, 60);
            grid.Add(done, 0, 1);
            Grid.SetRowSpan(done, 2);
            Content = grid;
            return;
        }

        var levelColor = _level >= 7 ? Theme.DangerText : _level >= 4 ? Theme.WatchText : Theme.GreenText;
        var number = UI.Text(_level > 0 ? _level.ToString() : "–", 72, Weight.ExtraBold, levelColor);
        var word = UI.Text(Words[_level], 15, Weight.Bold, Theme.Body);
        number.HorizontalTextAlignment = word.HorizontalTextAlignment = TextAlignment.Center;

        var levels = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var c = 0; c < 5; c++) levels.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        levels.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        levels.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var n = 1; n <= 10; n++)
        {
            var value = n;
            var on = _level == n;
            var b = new Button
            {
                Text = n.ToString(),
                HeightRequest = 52,
                Padding = 0,
                CornerRadius = 14,
                FontSize = 18,
                FontFamily = UI.Font(Weight.Bold),
                BorderWidth = 1,
                BorderColor = on ? Theme.Green : Theme.Border,
                BackgroundColor = on ? Theme.Green : Theme.Card,
                TextColor = on ? Colors.White : Theme.Ink
            };
            SemanticProperties.SetDescription(b, on ? $"Level {n}, selected" : $"Level {n}");
            b.Clicked += (_, _) => { _level = value; Render(); };
            levels.Add(b, (n - 1) % 5, (n - 1) / 5);
        }

        var context = new Label
        {
            FormattedText = new FormattedString
            {
                Spans =
                {
                    new Span { Text = "What's going on? ", FontFamily = UI.Font(Weight.Bold), FontSize = 15, TextColor = Theme.Ink },
                    new Span { Text = "Optional", FontFamily = UI.Font(Weight.Regular), FontSize = 15, TextColor = Theme.Muted }
                }
            }
        };
        var tags = UI.Wrap(Labels.Triggers.Select(t => (View)UI.Pill(t.Value, _tags.Contains(t.Key), () =>
        {
            if (!_tags.Remove(t.Key)) _tags.Add(t.Key);
            Render();
        })));

        var body = UI.Stack(24,
            UI.Stack(6,
                UI.Heading("How strong is the pull right now?"),
                UI.Text("Hunger or craving, whichever you feel. 1 is none, 10 is hard to ignore.", 14, Weight.Regular, Theme.Muted)),
            UI.Stack(4, number, word),
            levels,
            UI.Stack(10, context, tags));
        body.Padding = new Thickness(20, 8, 20, 20);
        grid.Add(new ScrollView { Content = body }, 0, 1);
        grid.Add(UI.Footer(UI.Primary(_busy ? "Logging…" : "Log it", Log, _level > 0 && !_busy)), 0, 2);
        Content = grid;
    }

    private async void Log()
    {
        if (_busy || _level == 0) return;
        _busy = true;
        Render();
        try
        {
            await _store.CheckInAsync(_level, _tags);
            _done = true;
        }
        catch (Exception ex) { await DisplayAlertAsync("Couldn't log that", ex.Message, "OK"); }
        finally
        {
            _busy = false;
            Render();
        }
    }
}
