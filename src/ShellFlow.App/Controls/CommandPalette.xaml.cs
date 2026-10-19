using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ShellFlow.Core.Commands;
using Windows.System;

namespace ShellFlow.App.Controls;

/// <summary>A row in the command palette. Rows run an action; the palette never implements commands itself.</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public string Glyph { get; init; } = "";

    public string? Shortcut { get; init; }

    public string? Keywords { get; init; }

    public required Action Invoke { get; init; }

    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ShortcutVisibility => string.IsNullOrEmpty(Shortcut) ? Visibility.Collapsed : Visibility.Visible;

    public string AutomationName => Shortcut is null ? Title : $"{Title}, shortcut {Shortcut}";

    internal int Score { get; set; }
}

public sealed partial class CommandPalette : UserControl
{
    private IReadOnlyList<PaletteItem> _items = [];

    public CommandPalette()
    {
        InitializeComponent();
    }

    public event EventHandler? Closed;

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Open(IReadOnlyList<PaletteItem> items, string? mode, string placeholder, bool animate, bool fluid = false)
    {
        _items = items;
        ModeChip.Visibility = mode is null ? Visibility.Collapsed : Visibility.Visible;
        ModeText.Text = mode ?? string.Empty;
        QueryBox.PlaceholderText = placeholder;
        QueryBox.Text = string.Empty;
        Filter(string.Empty);

        Visibility = Visibility.Visible;
        if (fluid)
        {
            Card.OpacityTransition = null;
            Card.TranslationTransition = null;
            Card.Translation = new Vector3(0, 0, 32);
            Card.Opacity = 0;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                Services.Motion.PopIn(Card, TimeSpan.FromMilliseconds(280));
                QueryBox.Focus(FocusState.Programmatic);
            });
            return;
        }

        Card.OpacityTransition = animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) } : null;
        Card.TranslationTransition = animate ? new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) } : null;
        Card.Opacity = 0;
        Card.Translation = new Vector3(0, animate ? -8 : 0, 32);
        DispatcherQueue.TryEnqueue(() =>
        {
            Card.Opacity = 1;
            Card.Translation = new Vector3(0, 0, 32);
            QueryBox.Focus(FocusState.Programmatic);
        });
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        _items = [];
        Results.ItemsSource = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Filter(string query)
    {
        List<PaletteItem> matches;
        if (string.IsNullOrWhiteSpace(query))
        {
            matches = [.. _items];
        }
        else
        {
            matches = [];
            foreach (var item in _items)
            {
                var score = FuzzyMatcher.Score(query, item.Title);
                if (item.Keywords is not null && FuzzyMatcher.Score(query, item.Keywords) is { } keywordScore)
                {
                    score = Math.Max(score ?? int.MinValue, keywordScore - 50);
                }

                if (score is { } s)
                {
                    item.Score = s;
                    matches.Add(item);
                }
            }

            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        Results.ItemsSource = matches;
        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Count > 0)
        {
            Results.SelectedIndex = 0;
        }
    }

    private void Run(PaletteItem? item)
    {
        if (item is null)
        {
            return;
        }

        Close();
        // Run after the palette has closed so commands that move focus (or open another palette) behave normally.
        DispatcherQueue.TryEnqueue(() => item.Invoke());
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Filter(QueryBox.Text);

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var count = (Results.ItemsSource as IList<PaletteItem>)?.Count ?? 0;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                Run(Results.SelectedItem as PaletteItem);
                e.Handled = true;
                break;
            case VirtualKey.Down when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex + 1) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.Up when count > 0:
                Results.SelectedIndex = (Results.SelectedIndex - 1 + count) % count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.PageDown when count > 0:
                Results.SelectedIndex = Math.Min(count - 1, Results.SelectedIndex + 8);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case VirtualKey.PageUp when count > 0:
                Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 8);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void OnResultClicked(object sender, ItemClickEventArgs e) => Run(e.ClickedItem as PaletteItem);

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => Close();

    private void OnCardTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
}
