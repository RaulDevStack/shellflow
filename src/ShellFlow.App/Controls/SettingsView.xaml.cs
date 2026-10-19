using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ShellFlow.App.Services;
using ShellFlow.App.Workspace;
using ShellFlow.Core.Profiles;
using ShellFlow.Core.Settings;
using Windows.Storage.Pickers;
using Windows.System;

namespace ShellFlow.App.Controls;

/// <summary>Settings overlay. Every control writes straight to <see cref="AppSettings"/> through AppServices, which validates and saves.</summary>
public sealed partial class SettingsView : UserControl
{
    public const string GeneralSection = "general";
    public const string ProfilesSection = "profiles";
    public const string ShortcutsSection = "shortcuts";

    private static readonly string[] SuggestedFonts =
    [
        "Cascadia Mono, Cascadia Code, Consolas, monospace",
        "Cascadia Code, Consolas, monospace",
        "Consolas, monospace",
        "Lucida Console, monospace",
        "Courier New, monospace",
    ];

    private AppServices? _services;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler? Closed;

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Initialize(AppServices services)
    {
        _services = services;
        services.Profiles.Changed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsOpen)
            {
                LoadValues();
            }
        });
        BuildAccentSwatches();
        foreach (var font in SuggestedFonts)
        {
            FontBox.Items.Add(font);
        }
    }

    public void Open(bool animate, string section = GeneralSection)
    {
        LoadValues();
        Visibility = Visibility.Visible;
        Root.OpacityTransition = animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) } : null;
        ContentHost.TranslationTransition = animate ? new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) } : null;
        Root.Opacity = 0;
        ContentHost.Translation = new Vector3(animate ? 12 : 0, 0, 0);
        DispatcherQueue.TryEnqueue(() =>
        {
            Root.Opacity = 1;
            ContentHost.Translation = Vector3.Zero;
        });

        var item = SectionList.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == section) ?? (ListViewItem)SectionList.Items[0];
        SectionList.SelectedItem = item;
        ShowSection(section);

        // Focus after the overlay is realized; otherwise keystrokes keep going to the terminal underneath.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => item.Focus(FocusState.Keyboard));
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Settings => _services!.Settings;

    private Brush R(string key) => ThemeResources.Brush(key, ActualTheme);

    private void Update(Action<AppSettings> change)
    {
        if (!_loading && _services is not null)
        {
            _services.UpdateSettings(change);
        }
    }

    private void LoadValues()
    {
        if (_services is null)
        {
            return;
        }

        _loading = true;
        try
        {
            var profiles = _services.Profiles.Visible.ToList();
            DefaultProfileBox.ItemsSource = profiles;
            DefaultProfileBox.SelectedItem = profiles.FirstOrDefault(p => p.Id == _services.Profiles.Default.Id);
            StartDirBox.Text = Settings.DefaultStartingDirectory ?? string.Empty;
            StartDirError.Visibility = Visibility.Collapsed;
            ConfirmCloseToggle.IsOn = Settings.Behavior.ConfirmCloseRunning;
            MultilineToggle.IsOn = Settings.Behavior.ConfirmMultilinePaste;
            CloseOnExitToggle.IsOn = Settings.Behavior.CloseOnCleanExit;
            IntegrationToggle.IsOn = Settings.Behavior.ShellIntegration;
            FolderCompletionToggle.IsOn = Settings.Behavior.FolderCompletion;

            SelectByTag(ThemeBox, Settings.Appearance.Theme.ToString());
            SelectByTag(MotionBox, Settings.Appearance.Animations.ToString());
            SelectByTag(MotionStyleBox, Settings.Appearance.MotionStyle.ToString());
            foreach (var swatch in AccentPanel.Children.OfType<RadioButton>())
            {
                swatch.IsChecked = (AccentColor)swatch.Tag == Settings.Appearance.Accent;
            }

            FontBox.Text = Settings.Terminal.FontFamily;
            FontSizeBox.Value = Settings.Terminal.FontSize;
            LineHeightBox.Value = Settings.Terminal.LineHeight;
            SelectByTag(CursorBox, Settings.Terminal.CursorStyle.ToString());
            BlinkBox.IsChecked = Settings.Terminal.CursorBlink;
            ScrollbackBox.Value = Settings.Terminal.Scrollback;
            GpuToggle.IsOn = Settings.Terminal.GpuAcceleration;
            CopyOnSelectToggle.IsOn = Settings.Terminal.CopyOnSelect;

            BuildProfileList();
            BuildShortcutList();
            BuildAbout();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SelectByTag(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

    private void ShowSection(string section)
    {
        GeneralPanel.Visibility = section == GeneralSection ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = section == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = section == ProfilesSection ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = section == ShortcutsSection ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is ListViewItem { Tag: string section })
        {
            ShowSection(section);
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !Dialogs.IsOpen)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ── General ──────────────────────────────────────────────────────────────

    private void OnDefaultProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultProfileBox.SelectedItem is ShellProfile profile)
        {
            Update(s => s.DefaultProfileId = profile.Id);
        }
    }

    private void OnStartDirCommitted(object sender, RoutedEventArgs e) => CommitStartDirectory(StartDirBox.Text);

    private void CommitStartDirectory(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            StartDirError.Visibility = Visibility.Collapsed;
            Update(s => s.DefaultStartingDirectory = null);
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (!Path.IsPathFullyQualified(expanded) || !Directory.Exists(expanded))
        {
            StartDirError.Text = "This folder does not exist. Shells will fall back to your user folder.";
            StartDirError.Visibility = Visibility.Visible;
        }
        else
        {
            StartDirError.Visibility = Visibility.Collapsed;
        }

        Update(s => s.DefaultStartingDirectory = value);
    }

    private async void OnBrowseStartDir(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            StartDirBox.Text = folder.Path;
            CommitStartDirectory(folder.Path);
        }
    }

    private void OnBehaviorToggled(object sender, RoutedEventArgs e) => Update(s =>
    {
        s.Behavior.ConfirmCloseRunning = ConfirmCloseToggle.IsOn;
        s.Behavior.ConfirmMultilinePaste = MultilineToggle.IsOn;
        s.Behavior.CloseOnCleanExit = CloseOnExitToggle.IsOn;
        s.Behavior.ShellIntegration = IntegrationToggle.IsOn;
        s.Behavior.FolderCompletion = FolderCompletionToggle.IsOn;
    });

    // ── Appearance ───────────────────────────────────────────────────────────

    private void BuildAccentSwatches()
    {
        var colors = new Dictionary<AccentColor, string>
        {
            [AccentColor.Periwinkle] = "#A6B7FF",
            [AccentColor.Sky] = "#91D8F7",
            [AccentColor.Mint] = "#A3DCC5",
            [AccentColor.Peach] = "#E8BFA6",
        };

        foreach (var (accent, hex) in colors)
        {
            var swatch = new RadioButton
            {
                GroupName = "Accent",
                Tag = accent,
                MinWidth = 0,
                Padding = new Thickness(6, 0, 0, 0),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(ThemeService.ParseHex(hex)!.Value), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = accent.ToString(), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 },
                    },
                },
            };
            AutomationProperties.SetName(swatch, $"{accent} accent");
            swatch.Checked += (_, _) => Update(s => s.Appearance.Accent = accent);
            AccentPanel.Children.Add(swatch);
        }
    }

    private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e) => Update(s =>
    {
        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string theme } && Enum.TryParse<ThemePreference>(theme, out var t))
        {
            s.Appearance.Theme = t;
        }

        if (MotionBox.SelectedItem is ComboBoxItem { Tag: string motion } && Enum.TryParse<AnimationPreference>(motion, out var m))
        {
            s.Appearance.Animations = m;
        }

        if (MotionStyleBox.SelectedItem is ComboBoxItem { Tag: string style } && Enum.TryParse<MotionStyle>(style, out var ms))
        {
            s.Appearance.MotionStyle = ms;
        }
    });

    // ── Terminal ─────────────────────────────────────────────────────────────

    private void OnFontSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        var sanitized = SettingsValidator.SanitizeFontFamily(args.Text);
        Update(s => s.Terminal.FontFamily = sanitized);
        sender.Text = sanitized;
        args.Handled = true;
    }

    private void OnFontSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FontBox.SelectedItem is string font)
        {
            Update(s => s.Terminal.FontFamily = font);
        }
    }

    private void OnTerminalNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        Update(s =>
        {
            if (!double.IsNaN(FontSizeBox.Value))
            {
                s.Terminal.FontSize = FontSizeBox.Value;
            }

            if (!double.IsNaN(LineHeightBox.Value))
            {
                s.Terminal.LineHeight = Math.Round(LineHeightBox.Value, 2);
            }

            if (!double.IsNaN(ScrollbackBox.Value))
            {
                s.Terminal.Scrollback = (int)ScrollbackBox.Value;
            }
        });
    }

    private void OnTerminalChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CursorBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<CursorStyle>(tag, out var style))
        {
            Update(s => s.Terminal.CursorStyle = style);
        }
    }

    private void OnTerminalToggled(object sender, RoutedEventArgs e) => Update(s =>
    {
        s.Terminal.CursorBlink = BlinkBox.IsChecked == true;
        s.Terminal.GpuAcceleration = GpuToggle.IsOn;
        s.Terminal.CopyOnSelect = CopyOnSelectToggle.IsOn;
    });

    // ── Profiles ─────────────────────────────────────────────────────────────

    private void BuildProfileList()
    {
        ProfileList.Children.Clear();
        foreach (var profile in _services!.Profiles.All)
        {
            var resolved = _services.Resolve(profile, null);
            var card = new Grid { Style = (Style)Application.Current.Resources["SfSettingCardStyle"] };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = ProfileGlyphs.For(profile),
                FontFamily = (FontFamily)Application.Current.Resources["SfIconFont"],
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeService.ParseHex(profile.Accent) is { } accent ? new SolidColorBrush(accent) : R("SfTextSecondaryBrush"),
            };
            card.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new TextBlock { Text = profile.Name });
            title.Children.Add(Badge(profile.Source == ProfileSource.Detected ? "Detected" : "Custom"));
            if (profile.Id == _services.Profiles.Default.Id)
            {
                title.Children.Add(Badge("Default"));
            }

            text.Children.Add(title);
            text.Children.Add(new TextBlock
            {
                Text = $"{resolved.ExecutablePath ?? profile.Executable} {profile.Arguments}".Trim(),
                Style = (Style)Application.Current.Resources["SfCaptionTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = true,
            });
            if (resolved.Issues.FirstOrDefault(i => i.IsError) is { } issue)
            {
                text.Children.Add(new TextBlock
                {
                    Text = issue.Message,
                    Style = (Style)Application.Current.Resources["SfCaptionTextStyle"],
                    Foreground = R("SfErrorBrush"),
                });
            }

            Grid.SetColumn(text, 1);
            card.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var edit = new Button { Content = profile.Source == ProfileSource.Detected ? "Customize…" : "Edit…" };
            edit.Click += async (_, _) => await EditProfileAsync(profile.Clone(), isNew: false);
            actions.Children.Add(edit);
            if (profile.Source == ProfileSource.User)
            {
                var delete = new Button { Content = "Remove" };
                AutomationProperties.SetName(delete, $"Remove {profile.Name}");
                delete.Click += async (_, _) => await RemoveProfileAsync(profile);
                actions.Children.Add(delete);
            }

            Grid.SetColumn(actions, 2);
            card.Children.Add(actions);
            ProfileList.Children.Add(card);
        }
    }

    private Border Badge(string text) => new()
    {
        Padding = new Thickness(6, 0, 6, 1),
        CornerRadius = new CornerRadius(4),
        Background = R("SfShortcutChipBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = R("SfTextSecondaryBrush") },
    };

    private async void OnAddProfile(object sender, RoutedEventArgs e) =>
        await EditProfileAsync(new ShellProfile { Id = string.Empty, Name = "New shell", Executable = string.Empty, Icon = ProfileIcons.Terminal }, isNew: true);

    private async Task EditProfileAsync(ShellProfile profile, bool isNew)
    {
        var name = new TextBox { Header = "Name", Text = profile.Name, MaxLength = 64 };
        var exe = new TextBox { Header = "Executable", Text = profile.Executable, PlaceholderText = @"C:\path\to\shell.exe or a name on PATH", MinWidth = 320 };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var args = new TextBox { Header = "Arguments", Text = profile.Arguments ?? string.Empty };
        var dir = new TextBox { Header = "Starting directory (optional)", Text = profile.StartingDirectory ?? string.Empty, PlaceholderText = "Uses the default starting directory" };
        var icon = new ComboBox { Header = "Icon", MinWidth = 160 };
        foreach (var key in new[] { ProfileIcons.Terminal, ProfileIcons.PowerShell, ProfileIcons.Cmd, ProfileIcons.Bash, ProfileIcons.Linux })
        {
            icon.Items.Add(key);
        }

        icon.SelectedItem = profile.Icon ?? ProfileIcons.Terminal;
        var error = new TextBlock { Foreground = R("SfErrorBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        browse.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                exe.Text = file.Path;
            }
        };

        var exeRow = new Grid { ColumnSpacing = 8 };
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        exeRow.Children.Add(exe);
        Grid.SetColumn(browse, 1);
        exeRow.Children.Add(browse);

        var form = new StackPanel { Spacing = 12, MinWidth = 420 };
        form.Children.Add(name);
        form.Children.Add(exeRow);
        form.Children.Add(args);
        form.Children.Add(dir);
        form.Children.Add(icon);
        form.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? "Add shell" : profile.Source == ProfileSource.Detected ? $"Customize {profile.Name}" : $"Edit {profile.Name}",
            Content = new ScrollViewer { Content = form, MaxHeight = 480 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            var resolved = ProfileResolver.ResolveExecutable(exe.Text, _services!.Profiles.Environment);
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                error.Text = "Enter a name.";
            }
            else if (resolved is null)
            {
                error.Text = $"\"{exe.Text}\" could not be found. Enter a full path to an .exe, or a program name on PATH.";
            }
            else
            {
                return;
            }

            error.Visibility = Visibility.Visible;
            e.Cancel = true;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var edited = profile.Clone();
        edited.Name = name.Text.Trim();
        edited.Executable = exe.Text.Trim();
        edited.Arguments = string.IsNullOrWhiteSpace(args.Text) ? null : args.Text.Trim();
        edited.StartingDirectory = string.IsNullOrWhiteSpace(dir.Text) ? null : dir.Text.Trim();
        edited.Icon = icon.SelectedItem as string;
        edited.Source = ProfileSource.User;
        if (isNew)
        {
            edited.Id = UniqueId(edited.Name);
        }

        _services!.UpdateSettings(s =>
        {
            s.Profiles.RemoveAll(p => string.Equals(p.Id, edited.Id, StringComparison.OrdinalIgnoreCase));
            s.Profiles.Add(edited);
        });
        BuildProfileList();
    }

    private string UniqueId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0 || !char.IsAsciiLetterOrDigit(slug[0]))
        {
            slug = "shell";
        }

        slug = slug.Length > 48 ? slug[..48] : slug;
        var id = slug;
        for (var i = 2; _services!.Profiles.Find(id) is not null; i++)
        {
            id = $"{slug}-{i}";
        }

        return id;
    }

    private async Task RemoveProfileAsync(ShellProfile profile)
    {
        var result = await Dialogs.ConfirmAsync(XamlRoot, $"Remove {profile.Name}?", "Open panes keep running. Saved workspaces that use this shell will open with the default shell instead.", "Remove", destructive: true);
        if (!result.Confirmed)
        {
            return;
        }

        _services!.UpdateSettings(s =>
        {
            s.Profiles.RemoveAll(p => p.Id == profile.Id);
            if (s.DefaultProfileId == profile.Id)
            {
                s.DefaultProfileId = null;
            }
        });
        LoadValues();
    }

    // ── Shortcuts ────────────────────────────────────────────────────────────

    private void BuildShortcutList()
    {
        ShortcutList.Children.Clear();
        var commands = _services!.Commands;
        foreach (var group in commands.Definitions.GroupBy(d => d.Category))
        {
            ShortcutList.Children.Add(new TextBlock { Text = group.Key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            foreach (var definition in group)
            {
                var row = new Grid { Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(6), Background = R("SfSurfaceBrush") };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = definition.Title, VerticalAlignment = VerticalAlignment.Center });

                var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var shortcuts = commands.GetShortcuts(definition.Id);
                if (shortcuts.Count == 0)
                {
                    chips.Children.Add(new TextBlock { Text = "—", Style = (Style)Application.Current.Resources["SfShortcutChipTextStyle"] });
                }

                foreach (var chord in shortcuts)
                {
                    chips.Children.Add(new Border
                    {
                        Padding = new Thickness(7, 2, 7, 2),
                        CornerRadius = new CornerRadius(5),
                        Background = R("SfShortcutChipBrush"),
                        Child = new TextBlock { Text = chord.ToString(), Style = (Style)Application.Current.Resources["SfShortcutChipTextStyle"] },
                    });
                }

                Grid.SetColumn(chips, 1);
                row.Children.Add(chips);
                AutomationProperties.SetName(row, shortcuts.Count == 0 ? $"{definition.Title}, no shortcut" : $"{definition.Title}, {string.Join(" or ", shortcuts)}");
                ShortcutList.Children.Add(row);
            }
        }
    }

    private void OnRevealSettingsFile(object sender, RoutedEventArgs e)
    {
        _services!.SaveSettingsNow();
        Reveal(_services.Paths.SettingsFile, select: true);
    }

    // ── About ────────────────────────────────────────────────────────────────

    private void BuildAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
        var webView = TerminalHost.TerminalWebEnvironment.GetRuntimeVersion() ?? "not installed";
        VersionText.Text = $"Version {version}\nWebView2 Runtime {webView}\n.NET {Environment.Version}\nWindows {Environment.OSVersion.Version}";
        DataFolderText.Text = _services!.Paths.Root;
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => Reveal(_services!.Paths.Root, select: false);

    private static void Reveal(string path, bool select)
    {
        try
        {
            if (!select)
            {
                Directory.CreateDirectory(path);
            }

            Process.Start(new ProcessStartInfo("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Warn($"Could not open Explorer: {ex.Message}");
        }
    }
}
