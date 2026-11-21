using CravingForecast.Core;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace CravingForecast.Mobile.Pages;

/// <summary>Salus palette: light oak, warm wood and forest green. Risk runs sage → honey → walnut.</summary>
public static class Theme
{
    public static readonly Color Ground = Color.FromArgb("#F4EDE2");   // page background, light oak
    public static readonly Color Oak = Color.FromArgb("#E9DCC6");      // splash, illustration panels
    public static readonly Color Card = Color.FromArgb("#FFFCF7");
    public static readonly Color Border = Color.FromArgb("#E4D8C6");
    public static readonly Color Chip = Color.FromArgb("#E8DCCB");
    public static readonly Color Wood = Color.FromArgb("#EBDFCB");     // prompt cards
    public static readonly Color WoodBorder = Color.FromArgb("#D6C4A8");
    public static readonly Color Track = Color.FromArgb("#D8C7AA");
    public static readonly Color Disabled = Color.FromArgb("#BDB3A4");

    public static readonly Color Ink = Color.FromArgb("#1E2A24");
    public static readonly Color Body = Color.FromArgb("#4A4238");
    public static readonly Color Muted = Color.FromArgb("#5E5448");

    public static readonly Color Green = Color.FromArgb("#3F6B4E");    // buttons, highlights
    public static readonly Color GreenText = Color.FromArgb("#2F5A3E");
    public static readonly Color GreenSoft = Color.FromArgb("#E3EDE1");
    public static readonly Color Forest = Color.FromArgb("#24382D");   // dark nudge card
    public static readonly Color ForestRaised = Color.FromArgb("#31483B");
    public static readonly Color ForestIcon = Color.FromArgb("#4F7D5D");
    public static readonly Color OnForestMuted = Color.FromArgb("#C9D6C8");
    public static readonly Color OnForestBody = Color.FromArgb("#E4EBE2");

    public static readonly Color Calm = Color.FromArgb("#BCD0BA");
    public static readonly Color Watch = Color.FromArgb("#D4A95A");
    public static readonly Color Danger = Color.FromArgb("#8A5A34");
    public static readonly Color WatchText = Color.FromArgb("#8A6A2A");
    public static readonly Color DangerText = Color.FromArgb("#7A4B26");
}

public enum Weight { Regular, SemiBold, Bold, ExtraBold }

/// <summary>Stroke icons on a 24-unit grid, drawn as MAUI paths.</summary>
public static class Icons
{
    public const string Sprout = "M12 21V11 M12 11C12 7 9 4 4 4c0 4 3 7 8 7z M12 14c0-3 2.5-6 7-6 0 3.5-2.5 6-7 6z";
    public const string Chart = "M3 3v18h18 M7 15l4-4 3 3 5-6";
    public const string Clock = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0 M12 7v5l3 2";
    public const string Plus = "M12 5v14 M5 12h14";
    public const string Close = "M6 6l12 12 M18 6 6 18";
    public const string Back = "M15 18l-6-6 6-6";
    public const string Check = "M20 6 9 17l-5-5";
    public const string Bell = "M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9 M10.3 21a1.94 1.94 0 0 0 3.4 0";
    public const string Moon = "M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z";
    public const string Calendar = "M5 5h14a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2H5a2 2 0 0 1 -2 -2V7a2 2 0 0 1 2 -2z M16 3v4 M8 3v4 M3 11h18";
}

/// <summary>Small helpers so pages read as layout, not property soup.</summary>
internal static class UI
{
    /// <summary>Danger matches the engine's zone threshold, so a red-flagged bar always means a real zone.</summary>
    public const double WatchThreshold = 0.35;

    public static string Font(Weight w) => w switch
    {
        Weight.SemiBold => "JakartaSemiBold",
        Weight.Bold => "JakartaBold",
        Weight.ExtraBold => "JakartaExtraBold",
        _ => "Jakarta"
    };

    public static Label Text(string text, double size = 15, Weight weight = Weight.Regular, Color? color = null) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Font(weight),
        TextColor = color ?? Theme.Ink,
        LineBreakMode = LineBreakMode.WordWrap
    };

    public static Label Heading(string text, double size = 28) => Text(text, size, Weight.ExtraBold);

    public static Label Caps(string text) => Text(text.ToUpperInvariant(), 13, Weight.Bold, Theme.Muted);

    public static VerticalStackLayout Stack(double spacing, params View[] views)
    {
        var s = new VerticalStackLayout { Spacing = spacing };
        foreach (var v in views) s.Children.Add(v);
        return s;
    }

    public static Border Card(View content, Color? background = null, bool outline = true, double radius = 22, Thickness? padding = null) => new()
    {
        Content = content,
        Padding = padding ?? new Thickness(18),
        BackgroundColor = background ?? Theme.Card,
        Stroke = outline ? Theme.Border : Colors.Transparent,
        StrokeThickness = outline ? 1 : 0,
        StrokeShape = new RoundRectangle { CornerRadius = radius }
    };

    public static ShapePath Icon(string data, Color color, double size = 22, double stroke = 2) => new()
    {
        Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(data)!,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = stroke,
        StrokeLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Aspect = Stretch.Uniform,
        WidthRequest = size,
        HeightRequest = size,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center
    };

    /// <summary>A rounded square holding an icon, like the app's logo.</summary>
    public static Border Tile(string icon, Color background, Color foreground, double size, double radius, double iconSize, double stroke = 2) => new()
    {
        WidthRequest = size,
        HeightRequest = size,
        BackgroundColor = background,
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = radius },
        Content = Icon(icon, foreground, iconSize, stroke)
    };

    public static Button Pill(string text, bool selected, Action onTap)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 14,
            FontFamily = Font(Weight.SemiBold),
            CornerRadius = 22,
            Padding = new Thickness(16, 0),
            Margin = new Thickness(0, 0, 8, 8),
            MinimumHeightRequest = 44,
            BorderWidth = 1,
            BorderColor = selected ? Theme.Green : Theme.WoodBorder,
            BackgroundColor = selected ? Theme.GreenSoft : Theme.Card,
            TextColor = Theme.Ink
        };
        b.Clicked += (_, _) => onTap();
        return b;
    }

    public static Button Primary(string text, Action onTap, bool enabled = true, Color? background = null)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 16,
            FontFamily = Font(Weight.Bold),
            CornerRadius = 16,
            HeightRequest = 54,
            BackgroundColor = enabled ? background ?? Theme.Green : Theme.Disabled,
            TextColor = Colors.White,
            IsEnabled = enabled
        };
        b.Clicked += (_, _) => onTap();
        return b;
    }

    public static Button Plain(string text, Action onTap, Color? color = null)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 15,
            FontFamily = Font(Weight.SemiBold),
            BackgroundColor = Colors.Transparent,
            TextColor = color ?? Theme.GreenText,
            Padding = new Thickness(8, 0),
            MinimumHeightRequest = 44
        };
        b.Clicked += (_, _) => onTap();
        return b;
    }

    /// <summary>A square icon button (close, back) with a spoken label.</summary>
    public static Border IconButton(string icon, string label, Action onTap, Color? background = null)
    {
        var b = Tile(icon, background ?? Theme.Chip, Theme.Ink, 44, 14, 18, 2.2);
        SemanticProperties.SetDescription(b, label);
        b.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        return b;
    }

    /// <summary>A bottom action area with a hairline above it.</summary>
    public static View Footer(View content, Color? background = null) => new VerticalStackLayout
    {
        BackgroundColor = background ?? Theme.Ground,
        Children =
        {
            new BoxView { HeightRequest = 1, Color = Theme.Border },
            new ContentView { Content = content, Padding = new Thickness(20, 12, 20, 24) }
        }
    };

    public static FlexLayout Wrap(IEnumerable<View> children)
    {
        var f = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var c in children) f.Children.Add(c);
        return f;
    }

    /// <summary>A horizontal meter: <paramref name="value"/> of the track filled.</summary>
    public static Grid Bar(double value, Color fill, Color track, double height = 8)
    {
        value = Math.Clamp(value, 0, 1);
        var g = new Grid { HeightRequest = height, ColumnSpacing = 0 };
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(value, 0.0001), GridUnitType.Star)));
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(1 - value, 0.0001), GridUnitType.Star)));
        var background = new Border { BackgroundColor = track, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = height / 2 } };
        g.Add(background);
        Grid.SetColumnSpan(background, 2);
        if (value > 0) g.Add(new Border { BackgroundColor = fill, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = height / 2 } }, 0);
        return g;
    }

    public static Color RiskColor(double risk) =>
        risk >= ZoneDetector.Threshold ? Theme.Danger : risk >= WatchThreshold ? Theme.Watch : Theme.Calm;

    public static string Hour(int h) { h = ((h % 24) + 24) % 24; return $"{(h % 12 == 0 ? 12 : h % 12)} {(h < 12 ? "AM" : "PM")}"; }

    public static string ShortHour(int h) { h = ((h % 24) + 24) % 24; return $"{(h % 12 == 0 ? 12 : h % 12)}{(h < 12 ? "a" : "p")}"; }

    public static Task Alert(string title, string message) =>
        Application.Current?.Windows.FirstOrDefault()?.Page?.DisplayAlertAsync(title, message, "OK") ?? Task.CompletedTask;

    /// <summary>App-wide defaults so any label or button not built here still matches.</summary>
    public static ResourceDictionary Styles()
    {
        var r = new ResourceDictionary();
        r.Add(new Style(typeof(Label)) { Setters = { new Setter { Property = Label.FontFamilyProperty, Value = "Jakarta" }, new Setter { Property = Label.TextColorProperty, Value = Theme.Ink } } });
        r.Add(new Style(typeof(Button)) { Setters = { new Setter { Property = Button.FontFamilyProperty, Value = "JakartaSemiBold" } } });
        r.Add(new Style(typeof(ContentPage)) { ApplyToDerivedTypes = true, Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Theme.Ground } } });
        return r;
    }
}
