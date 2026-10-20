using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ShellFlow.App.Controls;

public sealed record SearchRequest(string Query, bool Backwards, bool CaseSensitive, bool WholeWord, bool Regex, bool Incremental);

/// <summary>Find-in-terminal overlay. It only collects the query; the active pane's renderer performs the search.</summary>
public sealed partial class SearchBar : UserControl
{
    public SearchBar()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public event EventHandler<SearchRequest>? SearchRequested;

    public event EventHandler? Closed;

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Open(bool animate)
    {
        Visibility = Visibility.Visible;
        Card.TranslationTransition = animate ? new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        Card.Translation = new Vector3(0, animate ? -6 : 0, 24);
        DispatcherQueue.TryEnqueue(() =>
        {
            Card.Translation = new Vector3(0, 0, 24);
            QueryBox.Focus(FocusState.Programmatic);
            QueryBox.SelectAll();
        });

        if (QueryBox.Text.Length > 0)
        {
            Raise(backwards: false, incremental: true);
        }
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        CountText.Text = string.Empty;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void ShowResults(int index, int count)
    {
        if (QueryBox.Text.Length == 0)
        {
            CountText.Text = string.Empty;
            return;
        }

        CountText.Text = count switch
        {
            0 => "No results",
            _ when index < 0 => $"{count}+ found",
            _ => $"{index + 1} of {count}",
        };
    }

    private void Raise(bool backwards, bool incremental) =>
        SearchRequested?.Invoke(this, new SearchRequest(QueryBox.Text, backwards, CaseToggle.IsChecked == true, WordToggle.IsChecked == true, RegexToggle.IsChecked == true, incremental));

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (QueryBox.Text.Length == 0)
        {
            CountText.Text = string.Empty;
        }

        Raise(backwards: false, incremental: true);
    }

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                Raise(backwards: shift, incremental: false);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => Raise(backwards: false, incremental: true);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Raise(backwards: true, incremental: false);

    private void OnNextClick(object sender, RoutedEventArgs e) => Raise(backwards: false, incremental: false);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
