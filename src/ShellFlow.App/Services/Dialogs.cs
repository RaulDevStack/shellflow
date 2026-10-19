using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ShellFlow.App.Services;

/// <summary>Native ContentDialogs for confirmations and short text input. Only one dialog is shown at a time.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public sealed record ConfirmResult(bool Confirmed, bool DontAskAgain);

    public static async Task<ConfirmResult> ConfirmAsync(XamlRoot root, string title, string message, string primaryText, string closeText = "Cancel", string? dontAskAgainText = null, bool destructive = false)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 420 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        CheckBox? checkbox = null;
        if (dontAskAgainText is not null)
        {
            checkbox = new CheckBox { Content = dontAskAgainText };
            body.Children.Add(checkbox);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = body,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };

        var result = await ShowAsync(dialog);
        return new ConfirmResult(result == ContentDialogResult.Primary, checkbox?.IsChecked == true);
    }

    public static async Task<string?> PromptAsync(XamlRoot root, string title, string? initial, string primaryText, string placeholder = "")
    {
        var box = new TextBox { Text = initial ?? string.Empty, PlaceholderText = placeholder, MinWidth = 320, MaxLength = 64 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public static async Task ShowMessageAsync(XamlRoot root, string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        await ShowAsync(dialog);
    }

    public static bool IsOpen => Gate.CurrentCount == 0;

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (!await Gate.WaitAsync(0))
        {
            return ContentDialogResult.None; // Another dialog is already up; never stack modal dialogs.
        }

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
