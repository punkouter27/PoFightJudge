using Microsoft.Maui.Controls.Shapes;

namespace PoFightJudge.Mobile.Pages;

/// <summary>The few building blocks every page uses, so screens stay consistent (the web's shared components).</summary>
public static class Ui
{
    public static readonly Color Accent = Color.FromArgb("#4F46E5");
    public static readonly Color Good = Color.FromArgb("#15803D");
    public static readonly Color Warn = Color.FromArgb("#B45309");
    public static readonly Color Bad = Color.FromArgb("#B91C1C");

    public static Label H1(string text) => new() { Text = text, FontSize = 24, FontAttributes = FontAttributes.Bold };

    public static Label H2(string text) => new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };

    public static Label Text(string? text, double size = 15) => new() { Text = text, FontSize = size, LineBreakMode = LineBreakMode.WordWrap };

    public static Label Muted(string? text, double size = 13)
    {
        Label label = new() { Text = text, FontSize = size, Opacity = 0.7, LineBreakMode = LineBreakMode.WordWrap };
        return label;
    }

    public static Label Colored(string? text, Color color, double size = 13) => new() { Text = text, FontSize = size, TextColor = color, LineBreakMode = LineBreakMode.WordWrap };

    public static Button Primary(string text, Func<Task> onClick)
    {
        Button button = new() { Text = text, BackgroundColor = Accent, TextColor = Colors.White };
        button.Clicked += async (_, _) => await Guard(button, onClick);
        return button;
    }

    public static Button Secondary(string text, Func<Task> onClick)
    {
        Button button = new() { Text = text, BackgroundColor = Colors.Transparent, BorderColor = Accent, BorderWidth = 1, TextColor = Accent };
        button.Clicked += async (_, _) => await Guard(button, onClick);
        return button;
    }

    public static Button Danger(string text, Func<Task> onClick)
    {
        Button button = new() { Text = text, BackgroundColor = Colors.Transparent, BorderColor = Bad, BorderWidth = 1, TextColor = Bad };
        button.Clicked += async (_, _) => await Guard(button, onClick);
        return button;
    }

    // One tap at a time: a button is disabled while its work runs, so a double tap can't double-post.
    private static async Task Guard(Button button, Func<Task> work)
    {
        if (!button.IsEnabled)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await work();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    public static Border Card(View content, Func<Task>? onTap = null, Color? stripe = null)
    {
        Border card = new()
        {
            Padding = new Thickness(14, 12),
            StrokeThickness = 1,
            Stroke = stripe ?? Colors.Gray.WithAlpha(0.4f),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = content,
        };
        if (onTap is not null)
        {
            TapGestureRecognizer tap = new();
            tap.Tapped += async (_, _) => await onTap();
            card.GestureRecognizers.Add(tap);
        }

        return card;
    }

    public static VerticalStackLayout Stack(double spacing = 8, params IView[] children)
    {
        VerticalStackLayout stack = new() { Spacing = spacing };
        foreach (IView child in children)
        {
            stack.Add(child);
        }

        return stack;
    }

    public static HorizontalStackLayout Row(params IView[] children)
    {
        HorizontalStackLayout row = new() { Spacing = 8 };
        foreach (IView child in children)
        {
            row.Add(child);
        }

        return row;
    }

    /// <summary>A label on the left, a control on the right.</summary>
    public static Grid Field(string label, View control)
    {
        Grid grid = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        grid.Add(new Label { Text = label, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap });
        grid.Add(control, 1);
        return grid;
    }

    public static Switch Toggle(bool on, Func<bool, Task> changed)
    {
        Switch toggle = new() { IsToggled = on };
        bool busy = false;
        toggle.Toggled += async (_, e) =>
        {
            if (busy)
            {
                return;
            }

            busy = true;
            try
            {
                await changed(e.Value);
            }
            finally
            {
                busy = false;
            }
        };
        return toggle;
    }

    /// <summary>A row of buttons acting as tabs; returns the bar and lets the caller pick a page.</summary>
    public static HorizontalStackLayout Tabs(IReadOnlyList<string> names, string selected, Action<string> pick)
    {
        HorizontalStackLayout bar = new() { Spacing = 6 };
        foreach (string name in names)
        {
            bool on = name == selected;
            Button tab = new()
            {
                Text = name,
                Padding = new Thickness(12, 4),
                BackgroundColor = on ? Accent : Colors.Transparent,
                TextColor = on ? Colors.White : Accent,
                BorderColor = Accent,
                BorderWidth = 1,
                CornerRadius = 16,
            };
            tab.Clicked += (_, _) => pick(name);
            bar.Add(tab);
        }

        return bar;
    }

    public static async Task Toast(string message)
    {
        if (Application.Current?.Windows is { Count: > 0 } windows && windows[0].Page is { } page)
        {
            await page.DisplayAlertAsync("PoFightJudge", message, "OK");
        }
    }
}
