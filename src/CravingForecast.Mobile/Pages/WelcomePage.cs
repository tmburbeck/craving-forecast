using Microsoft.Maui.Controls.Shapes;

namespace CravingForecast.Mobile.Pages;

/// <summary>Three short slides that introduce the app before the quiz. Swipe or tap Next.</summary>
public sealed class WelcomePage : ContentPage
{
    private static readonly (string Title, string Body)[] Slides =
    [
        ("Know your cravings before they arrive",
            "Rate how hungry or tempted you feel now and then. Salus learns the hours you are most at risk, like a weather forecast for cravings."),
        ("A gentle nudge, right on time",
            "Thirty minutes before a danger zone you get one idea that fits: a filling snack or a quick distraction."),
        ("It learns you, privately",
            "Start with a one-minute quiz. Your own check-ins take over from there, and your details stay coarse or on your phone.")
    ];

    private int _slide;

    public WelcomePage() => Render();

    private void Go(int slide)
    {
        if (slide < 0 || slide >= Slides.Length) return;
        _slide = slide;
        Render();
    }

    private void Render()
    {
        var brand = new HorizontalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            Children = { UI.Tile(Icons.Sprout, Theme.Green, Theme.Ground, 30, 10, 18), UI.Text("Salus", 17, Weight.ExtraBold) }
        };
        ((View)brand.Children[1]).VerticalOptions = LayoutOptions.Center;
        var top = new Grid { Padding = new Thickness(20, 16, 12, 0) };
        top.Add(brand);
        var skip = UI.Plain("Skip", App.Instance.Show<OnboardingPage>);
        skip.HorizontalOptions = LayoutOptions.End;
        top.Add(skip);

        var (title, body) = Slides[_slide];
        var panel = new Border
        {
            HeightRequest = 320,
            Padding = 28,
            BackgroundColor = Theme.Oak,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 28 },
            Content = _slide switch { 0 => ForecastArt(), 1 => NudgeArt(), _ => PrivacyArt() }
        };

        var dots = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Center };
        for (var i = 0; i < Slides.Length; i++)
            dots.Children.Add(new BoxView { HeightRequest = 8, WidthRequest = i == _slide ? 24 : 8, CornerRadius = 4, Color = i == _slide ? Theme.Green : Theme.Track });
        SemanticProperties.SetDescription(dots, $"Slide {_slide + 1} of {Slides.Length}");

        var last = _slide == Slides.Length - 1;
        var action = UI.Primary(last ? "Get started" : "Next", () =>
        {
            if (last) App.Instance.Show<OnboardingPage>();
            else Go(_slide + 1);
        });

        var grid = new Grid { RowSpacing = 0 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(top, 0, 0);
        grid.Add(new ScrollView
        {
            Content = UI.Stack(28, panel, UI.Stack(10, UI.Heading(title), UI.Text(body, 15, Weight.Regular, Theme.Body)))
        }.WithPadding(new Thickness(20, 24, 20, 0)), 0, 1);
        grid.Add(UI.Stack(18, dots, action).WithPadding(new Thickness(20, 12, 20, 24)), 0, 2);

        var left = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
        left.Swiped += (_, _) => Go(_slide + 1);
        var right = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
        right.Swiped += (_, _) => Go(_slide - 1);
        grid.GestureRecognizers.Add(left);
        grid.GestureRecognizers.Add(right);
        Content = grid;
    }

    private static View ForecastArt()
    {
        double[] risk = [0.15, 0.22, 0.3, 0.62, 0.78, 0.45, 0.38, 0.7, 0.85];
        var bars = new Grid { ColumnSpacing = 6, HeightRequest = 120 };
        for (var i = 0; i < risk.Length; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            bars.Add(new Border
            {
                HeightRequest = 16 + risk[i] * 96,
                VerticalOptions = LayoutOptions.End,
                BackgroundColor = UI.RiskColor(risk[i]),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 5 }
            }, i);
        }
        var axis = new Grid
        {
            Children =
            {
                UI.Text("Morning", 11, Weight.Regular, Theme.Muted),
                UI.Text("Afternoon", 11, Weight.Regular, Theme.Muted).Also(l => l.HorizontalOptions = LayoutOptions.Center),
                UI.Text("Night", 11, Weight.Regular, Theme.Muted).Also(l => l.HorizontalOptions = LayoutOptions.End)
            }
        };
        var card = UI.Card(UI.Stack(12, UI.Text("Today's craving risk", 13, Weight.Bold), bars, axis), outline: false, radius: 20);
        card.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetDescription(card, "Example chart of craving risk rising in the afternoon and at night");
        return card;
    }

    private static View NudgeArt()
    {
        var header = new Grid();
        header.Add(UI.Text("Salus", 13, Weight.Bold));
        header.Add(UI.Text("2:30 PM", 12, Weight.Regular, Theme.Muted).Also(l => l.HorizontalOptions = LayoutOptions.End));
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var icon = UI.Tile(Icons.Sprout, Theme.Green, Theme.Ground, 38, 12, 20);
        icon.VerticalOptions = LayoutOptions.Start;
        row.Add(icon, 0);
        row.Add(UI.Stack(2, header,
            UI.Text("Afternoon slump in 30 minutes", 14, Weight.SemiBold),
            UI.Text("Greek yogurt with berries now will carry you through it.", 13, Weight.Regular, Theme.Body)), 1);
        var notification = UI.Card(row, outline: false, radius: 20, padding: new Thickness(14));
        var behind = UI.Card(UI.Text("Was last night accurate? One tap helps.", 13, Weight.Regular, Theme.Body), outline: false, radius: 20, padding: new Thickness(14));
        behind.Opacity = 0.6;
        behind.Margin = new Thickness(14, 0);
        var stack = UI.Stack(10, notification, behind);
        stack.VerticalOptions = LayoutOptions.Center;
        return stack;
    }

    private static View PrivacyArt()
    {
        static View Row(string icon, string text)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            g.Add(UI.Icon(icon, Theme.Green, 22), 0);
            g.Add(UI.Text(text, 14, Weight.SemiBold).Also(l => l.VerticalOptions = LayoutOptions.Center), 1);
            return UI.Card(g, outline: false, radius: 18, padding: new Thickness(16, 14));
        }
        var stack = UI.Stack(10,
            Row(Icons.Moon, "Sleep shared only as good, okay or poor"),
            Row(Icons.Calendar, "Calendar details stay on your phone"),
            Row(Icons.Chart, "Fully personal by day 14"));
        stack.VerticalOptions = LayoutOptions.Center;
        return stack;
    }
}

internal static class ViewExtensions
{
    public static T Also<T>(this T view, Action<T> configure) { configure(view); return view; }

    public static T WithPadding<T>(this T view, Thickness padding) where T : View
    {
        switch (view)
        {
            case Layout l: l.Padding = padding; break;
            case ScrollView s: s.Padding = padding; break;
            case Border b: b.Padding = padding; break;
            default: view.Margin = padding; break;
        }
        return view;
    }
}
