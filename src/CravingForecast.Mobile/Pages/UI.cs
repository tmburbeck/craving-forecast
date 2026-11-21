namespace CravingForecast.Mobile.Pages;

/// <summary>Small helpers so pages read as layout, not property soup.</summary>
internal static class UI
{
    public static Label Title(string text) => new() { Text = text, FontSize = 26, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.WordWrap };
    public static Label H(string text) => new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold };
    public static Label P(string text, bool muted = false, double size = 15) =>
        new() { Text = text, FontSize = size, TextColor = muted ? Theme.Muted : null, LineBreakMode = LineBreakMode.WordWrap };

    public static Button Chip(string text, bool selected, Action onTap)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 15,
            CornerRadius = 12,
            Padding = new Thickness(14, 8),
            Margin = new Thickness(0, 0, 8, 8),
            MinimumHeightRequest = 44,
            BorderWidth = 1,
            BorderColor = selected ? Theme.Tomato : Color.FromArgb("#EADFD8"),
            BackgroundColor = selected ? Theme.Apricot : Colors.White,
            TextColor = Colors.Black
        };
        b.Clicked += (_, _) => onTap();
        return b;
    }

    public static Button Primary(string text, Action onTap, bool enabled = true)
    {
        var b = new Button { Text = text, BackgroundColor = Theme.Tomato, TextColor = Colors.White, CornerRadius = 12, MinimumHeightRequest = 48, IsEnabled = enabled, FontAttributes = FontAttributes.Bold };
        b.Clicked += (_, _) => onTap();
        return b;
    }

    public static FlexLayout Wrap(IEnumerable<View> children)
    {
        var f = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var c in children) f.Children.Add(c);
        return f;
    }

    public static Border Card(View content, bool hot = false) => new()
    {
        Content = content,
        Padding = 18,
        Margin = new Thickness(0, 0, 0, 16),
        StrokeThickness = hot ? 0 : 1,
        Stroke = Color.FromArgb("#EADFD8"),
        BackgroundColor = hot ? Theme.Soft : Colors.White,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 }
    };

    public static VerticalStackLayout Stack(params View[] views)
    {
        var s = new VerticalStackLayout { Spacing = 8 };
        foreach (var v in views) s.Children.Add(v);
        return s;
    }

    public static string Hour(int h) { h = ((h % 24) + 24) % 24; return $"{(h % 12 == 0 ? 12 : h % 12)} {(h < 12 ? "AM" : "PM")}"; }
}
