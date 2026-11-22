using CravingForecast.Core.Contracts;
using CravingForecast.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CravingForecast.Mobile.Pages;

/// <summary>The Insights tab: how much of today is personal, the archetypes, today's signals and rule trust.</summary>
public sealed class InsightsView : ContentView
{
    private readonly ForecastStore _store;
    private readonly AuthService _auth;
    private readonly NudgeScheduler _nudges;

    public InsightsView(ForecastStore store, AuthService auth, NudgeScheduler nudges)
    {
        _store = store;
        _auth = auth;
        _nudges = nudges;
        store.Changed += () => MainThread.BeginInvokeOnMainThread(Render);
        Render();
    }

    private void Render()
    {
        var f = _store.Forecast;
        if (f is null) { Content = null; return; }

        var page = UI.Stack(16,
            UI.Stack(4, UI.Heading("How it learns you", 30), UI.Text("What today's forecast is built from.", 14, Weight.Regular, Theme.Muted)),
            PersonalShare(f),
            Section("Your archetypes", Archetypes(f.Archetypes)),
            Section("Today's signals", Signals(f.ContextFactors)),
            Section("Rules and how much I trust them", Rules(f.Rules)),
            UI.Text("Rules below 40% trust are phased out. Your \"Was this accurate?\" answers move these.", 12, Weight.Regular, Theme.Muted),
            UI.Plain("Help & FAQ", async () => await Launcher.OpenAsync(new Uri(new Uri(ApiConfig.BaseUrl), "help")))
                .Also(b => b.HorizontalOptions = LayoutOptions.Center),
            UI.Plain("Sign out", SignOut, Theme.Muted).Also(b => b.HorizontalOptions = LayoutOptions.Center),
            UI.Plain("Delete my account", DeleteAccount, Theme.Muted).Also(b => { b.HorizontalOptions = LayoutOptions.Center; b.FontSize = 13; }));
        page.Padding = new Thickness(20, 16, 20, 28);
        Content = new ScrollView { Content = page };
    }

    private async void SignOut()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is not null && !await page.DisplayAlertAsync("Sign out?", "Your forecast stays saved to your account. Nudges stop on this device until you sign in again.", "Sign out", "Cancel"))
            return;
        _nudges.CancelAll();
        await _auth.SignOutAsync();
    }

    private async void DeleteAccount()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;
        if (!await page.DisplayAlertAsync("Delete your account?",
                "This erases your account, check-ins, quiz answers and everything Salus learned about you, for good. It can't be undone.",
                "Continue", "Keep my account"))
            return;
        var password = await page.DisplayPromptAsync("Confirm with your password", "Enter your password to delete your account.",
            "Delete my account", "Cancel", keyboard: Keyboard.Default);
        if (string.IsNullOrEmpty(password)) return;

        if (await _auth.DeleteAccountAsync(password) is { } error)
        {
            await page.DisplayAlertAsync("Couldn't delete the account", error, "OK");
            return;
        }
        _nudges.CancelAll();
        await page.DisplayAlertAsync("Your account is deleted", "Everything Salus kept about you is gone. Take good care of yourself.", "OK");
    }

    private static View Section(string title, View content) => UI.Stack(10, UI.Caps(title), content);

    private static View PersonalShare(ForecastDto f)
    {
        var ring = new Grid { WidthRequest = 84, HeightRequest = 84, VerticalOptions = LayoutOptions.Center };
        ring.Add(new GraphicsView { Drawable = new RingDrawable(f.PersonalShare), WidthRequest = 84, HeightRequest = 84 });
        ring.Add(UI.Text($"{f.PersonalShare:P0}", 20, Weight.ExtraBold).Also(l => { l.HorizontalOptions = LayoutOptions.Center; l.VerticalOptions = LayoutOptions.Center; }));
        SemanticProperties.SetDescription(ring, $"{f.PersonalShare:P0} of today's forecast comes from your own data");

        var top = new Grid { ColumnSpacing = 16 };
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        top.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        top.Add(ring, 0);
        top.Add(UI.Stack(4,
            UI.Text("Personal today", 16, Weight.Bold),
            UI.Text("The rest comes from your archetypes. Your own check-ins take over fully by day 14.", 13, Weight.Regular, Theme.Body))
            .Also(s => s.VerticalOptions = LayoutOptions.Center), 1);

        var day = Math.Clamp(f.Day, 1, 14);
        var days = new Grid();
        days.Add(UI.Text("Day 1", 12, Weight.Regular, Theme.Muted));
        days.Add(UI.Text($"Day {f.Day}", 12, Weight.Bold).Also(l => l.HorizontalOptions = LayoutOptions.Center));
        days.Add(UI.Text("Day 14", 12, Weight.Regular, Theme.Muted).Also(l => l.HorizontalOptions = LayoutOptions.End));

        return UI.Card(UI.Stack(14,
            top,
            UI.Stack(6, days, UI.Bar((day - 1) / 13.0, Theme.Green, Theme.Chip)),
            UI.Text($"Today's ceiling is {f.Ceiling:P0}. Each hour only reaches it once it has enough of your check-ins.", 12, Weight.Regular, Theme.Muted)));
    }

    private static View Archetypes(IReadOnlyList<ArchetypeDto> archetypes)
    {
        var list = UI.Stack(10);
        foreach (var a in archetypes)
        {
            var (match, color) = a.Strength >= 0.66 ? ("Strong match", Theme.GreenText) : a.Strength >= 0.33 ? ("Partial match", Theme.Muted) : ("Light match", Theme.Muted);
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            head.Add(UI.Text(a.Name, 15, Weight.Bold), 0);
            head.Add(UI.Text(match, 12, Weight.Bold, color).Also(l => l.VerticalOptions = LayoutOptions.Center), 1);
            list.Children.Add(UI.Card(UI.Stack(4, head, UI.Text(a.Description, 13, Weight.Regular, Theme.Body)), radius: 18, padding: new Thickness(16, 14)));
        }
        return list;
    }

    private static View Signals(IReadOnlyList<FactorDto> factors)
    {
        if (factors.Count == 0)
            return UI.Text("No signals yet today. They arrive with your morning sync.", 14, Weight.Regular, Theme.Muted);

        var list = UI.Stack(8);
        foreach (var x in factors)
        {
            var (dot, background, text) = x.Multiplier > 1.001 ? (Theme.Danger, Theme.Wood, Theme.DangerText)
                : x.Multiplier < 0.999 ? (Theme.Green, Theme.GreenSoft, Theme.GreenText)
                : (Theme.Muted, Theme.Chip, Theme.Ink);
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.Add(new Ellipse { WidthRequest = 8, HeightRequest = 8, Fill = new SolidColorBrush(dot), VerticalOptions = LayoutOptions.Center }, 0);
            row.Add(UI.Text(x.Label, 13, Weight.SemiBold, text), 1);
            list.Children.Add(UI.Card(row, background, outline: false, radius: 14, padding: new Thickness(14, 10)));
        }
        return list;
    }

    private static View Rules(IReadOnlyList<RuleDto> rules)
    {
        var list = UI.Stack(0);
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            var nameColor = r.PhasedOut ? Theme.Muted : Theme.Ink;
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            head.Add(new Label
            {
                FormattedText = new FormattedString
                {
                    Spans =
                    {
                        new Span { Text = r.Name, FontFamily = UI.Font(Weight.Bold), FontSize = 14, TextColor = nameColor },
                        new Span { Text = $" · {r.StartsAt}", FontFamily = UI.Font(Weight.Regular), FontSize = 14, TextColor = Theme.Muted }
                    }
                }
            }, 0);
            head.Add(UI.Text(r.PhasedOut ? "Phased out" : $"{r.Trust:P0}", 13, Weight.Bold, nameColor), 1);
            var fill = r.PhasedOut ? Theme.Track : r.Trust >= 0.6 ? Theme.Green : Theme.Watch;
            list.Children.Add(UI.Stack(8, head, UI.Bar(r.Trust, fill, Theme.Chip, 6)).WithPadding(new Thickness(0, 12)));
            if (i < rules.Count - 1) list.Children.Add(new BoxView { HeightRequest = 1, Color = Theme.Border });
        }
        return UI.Card(list, radius: 18, padding: new Thickness(16, 6));
    }
}

/// <summary>A progress ring for the personal share of the forecast.</summary>
internal sealed class RingDrawable(double value) : IDrawable
{
    public void Draw(ICanvas canvas, RectF bounds)
    {
        const float width = 10;
        var rect = new RectF(bounds.X + width / 2, bounds.Y + width / 2, bounds.Width - width, bounds.Height - width);
        canvas.StrokeSize = width;
        canvas.StrokeColor = Theme.Chip;
        canvas.DrawEllipse(rect);
        if (value <= 0) return;

        canvas.StrokeColor = Theme.Green;
        canvas.StrokeLineCap = LineCap.Round;
        if (value >= 0.999) canvas.DrawEllipse(rect);
        else canvas.DrawArc(rect, 90, 90 - (float)(value * 360), true, false); // clockwise from 12 o'clock
    }
}
