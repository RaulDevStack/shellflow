using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ShellFlow.App.Controls;
using ShellFlow.App.Services;
using ShellFlow.Core.Layout;
using ShellFlow.Core.Profiles;
using ShellFlow.Core.Workspaces;
using Windows.UI;

namespace ShellFlow.App.Workspace;

/// <summary>A tab: its split tree, its panes, and which pane has focus. Also the view model for the tab strip.</summary>
public sealed partial class TabController : INotifyPropertyChanged
{
    private const double KeyboardResizeStep = 0.04;

    private readonly AppServices _services;
    private readonly Dictionary<string, PaneController> _panes = new(StringComparer.Ordinal);
    private string? _customTitle;
    private string _focusedPaneId;
    private bool _isSelected;

    public TabController(AppServices services, TabDocument document)
    {
        _services = services;
        Id = document.Id;
        _customTitle = document.Title;
        Root = document.Root ?? new PaneLeaf { ProfileId = services.Profiles.Default.Id };
        _focusedPaneId = document.FocusedPaneId ?? PaneTree.Leaves(Root).First().Id;
        Host = new PaneHostPanel { Root = Root, ShouldAnimateLayout = () => _services.Theme.FluidMotion };
        Host.LayoutEdited += (_, _) => StructureChanged?.Invoke(this, EventArgs.Empty);

        foreach (var leaf in PaneTree.Leaves(Root))
        {
            AddPane(leaf, animate: false);
        }

        SetFocusedPane(_focusedPaneId, focusTerminal: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The last pane closed; the owner should remove this tab.</summary>
    public event EventHandler? Emptied;

    /// <summary>Layout, focus or titles changed in a way worth persisting.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Something the status bar shows changed (focus, state, size, directory).</summary>
    public event EventHandler? StatusChanged;

    public event EventHandler<(PaneController Pane, string CommandId)>? PaneCommandRequested;

    public event EventHandler<string>? CopyRequested;

    public event EventHandler<TerminalHost.SearchResultEventArgs>? SearchResultsChanged;

    public string Id { get; }

    public PaneNode Root { get; private set; }

    public PaneHostPanel Host { get; }

    public IReadOnlyCollection<PaneController> Panes => _panes.Values;

    public int PaneCount => _panes.Count;

    public PaneController FocusedPane => _panes[_focusedPaneId];

    public string? CustomTitle
    {
        get => _customTitle;
        set
        {
            _customTitle = string.IsNullOrWhiteSpace(value) ? null : WorkspaceValidator.NormalizeName(value);
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(AutomationName));
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Title => _customTitle ?? FocusedPane.DisplayTitle;

    public string Glyph => ProfileGlyphs.For(FocusedPane.Profile);

    public Brush AccentBrush =>
        ThemeService.ParseHex(FocusedPane.Profile.Accent) is { } color
            ? new SolidColorBrush(color)
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xA0, 0xA6, 0xB5));

    public string PaneCountText => _panes.Count > 1 ? _panes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    public Visibility PaneCountVisibility => _panes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExitedVisibility => _panes.Values.Any(p => p.State is PaneState.Exited or PaneState.Failed) ? Visibility.Visible : Visibility.Collapsed;

    public string AutomationName => _panes.Count > 1 ? $"{Title}, {_panes.Count} panes" : Title;

    public string ToolTip => $"{Title}\n{FocusedPane.Profile.Name}" + (_panes.Count > 1 ? $" · {_panes.Count} panes" : string.Empty);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            // Hidden tabs stay "visible" to WebView2 (opacity 0) so their renderers keep processing output at
            // full speed; collapsing them would let Chromium throttle background timers and stall busy shells.
            Host.Opacity = value ? 1 : 0;
            Host.IsHitTestVisible = value;
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(Host, value ? 1 : 0);
            if (value)
            {
                Host.Refresh();
            }

            OnPropertyChanged(nameof(IndicatorOpacity));
        }
    }

    /// <summary>
    /// Deselects the tab but keeps it visible so it can slide out; call <see cref="FinishHide"/> when the slide ends.
    /// </summary>
    public void BeginHide()
    {
        _isSelected = false;
        Host.IsHitTestVisible = false;
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(Host, 0);
        OnPropertyChanged(nameof(IndicatorOpacity));
    }

    public void FinishHide()
    {
        if (!_isSelected)
        {
            Host.Opacity = 0;
            Host.Translation = System.Numerics.Vector3.Zero;
        }
    }

    /// <summary>Accent underline under the selected tab (a non-color cue in addition to the raised background).</summary>
    public double IndicatorOpacity => _isSelected ? 1 : 0;

    /// <summary>Width of this tab in the strip; tabs shrink together when there are many (set by the view).</summary>
    public double TabWidth
    {
        get => _tabWidth;
        set
        {
            if (Math.Abs(_tabWidth - value) > 0.5)
            {
                _tabWidth = value;
                OnPropertyChanged();
            }
        }
    }

    private double _tabWidth = 200;

    public bool HasRunningCommand => _panes.Values.Any(p => p.HasRunningCommand);

    public int RunningCommandCount => _panes.Values.Count(p => p.HasRunningCommand);

    public void Focus() => FocusedPane.Focus();

    /// <param name="directory">Folder for the new pane; null opens it where the focused pane currently is.</param>
    public PaneController Split(PaneArrangement arrangement, ShellProfile? profile = null, string? directory = null)
    {
        var source = FocusedPane;
        var leaf = new PaneLeaf
        {
            ProfileId = (profile ?? source.Profile).Id,
            // A new pane opens in the folder the focused pane is in right now (reported by shell integration),
            // falling back to the folder that pane started in.
            StartingDirectory = directory ?? source.CurrentFolder ?? source.Leaf.StartingDirectory,
        };

        Root = PaneTree.Split(Root, source.Id, arrangement, leaf);
        Host.Root = Root;
        var pane = AddPane(leaf, animate: _services.Theme.AnimationsEnabled);
        SetFocusedPane(pane.Id, focusTerminal: true);
        RaiseTabProperties();
        StructureChanged?.Invoke(this, EventArgs.Empty);
        return pane;
    }

    /// <summary>Removes a pane and stops its shell. Other panes keep running.</summary>
    public async Task ClosePaneAsync(string paneId)
    {
        if (!_panes.Remove(paneId, out var pane))
        {
            return;
        }

        var neighbor = PaneTree.FindNeighbor(Root, paneId, FocusDirection.Left)
            ?? PaneTree.FindNeighbor(Root, paneId, FocusDirection.Up)
            ?? PaneTree.NextLeafId(Root, paneId, 1);

        var newRoot = PaneTree.Remove(Root, paneId);
        var animate = _services.Theme.FluidMotion && newRoot is not null && _isSelected;
        _ = pane.CloseAsync(); // The shell stops right away; only the pixels linger for the exit animation.
        if (animate)
        {
            Host.DetachPane(paneId);
            _ = RemoveAfterPopOutAsync(pane);
        }
        else
        {
            Host.RemovePane(paneId);
        }

        if (newRoot is null || _panes.Count == 0)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
            return;
        }

        Root = newRoot;
        Host.Root = Root; // Remaining panes glide into the freed space.
        var lostFocus = _focusedPaneId == paneId || !_panes.ContainsKey(_focusedPaneId);
        var target = lostFocus ? (neighbor is not null && _panes.ContainsKey(neighbor) ? neighbor : _panes.Keys.First()) : _focusedPaneId;
        SetFocusedPane(target, focusTerminal: lostFocus && _isSelected); // Also refreshes active-pane borders.

        RaiseTabProperties();
        StructureChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    public void SetFocusedPane(string paneId, bool focusTerminal)
    {
        if (!_panes.ContainsKey(paneId))
        {
            return;
        }

        var changed = _focusedPaneId != paneId;
        _focusedPaneId = paneId;
        foreach (var pane in _panes.Values)
        {
            pane.IsActive = pane.Id == paneId && _panes.Count > 1;
            if (pane.Id != paneId)
            {
                pane.View.RunAction("blur");
            }
        }

        if (focusTerminal)
        {
            _panes[paneId].Focus();
        }

        if (changed)
        {
            RaiseTabProperties();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool MoveFocus(FocusDirection direction)
    {
        var target = PaneTree.FindNeighbor(Root, _focusedPaneId, direction);
        if (target is null)
        {
            return false;
        }

        SetFocusedPane(target, focusTerminal: true);
        return true;
    }

    public void CycleFocus(int offset)
    {
        var target = PaneTree.NextLeafId(Root, _focusedPaneId, offset);
        if (target is not null)
        {
            SetFocusedPane(target, focusTerminal: true);
        }
    }

    public bool ResizeFocused(FocusDirection direction)
    {
        if (!PaneTree.Resize(Root, _focusedPaneId, direction, KeyboardResizeStep))
        {
            return false;
        }

        Host.Refresh(animate: true);
        StructureChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Equalize()
    {
        PaneTree.Equalize(Root);
        Host.Refresh(animate: true);
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyRendererOptions(Core.Bridge.RendererOptions options)
    {
        foreach (var pane in _panes.Values)
        {
            pane.ApplyRendererOptions(options);
        }
    }

    public TabDocument ToDocument() => new()
    {
        Id = Id,
        Title = _customTitle,
        Root = PaneTree.Clone(Root),
        FocusedPaneId = _focusedPaneId,
    };

    public async Task CloseAllAsync()
    {
        var panes = _panes.Values.ToList();
        _panes.Clear();
        foreach (var pane in panes)
        {
            Host.RemovePane(pane.Id);
        }

        await Task.WhenAll(panes.Select(p => p.CloseAsync()));
    }

    public void RaiseTabProperties()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(PaneCountText));
        OnPropertyChanged(nameof(PaneCountVisibility));
        OnPropertyChanged(nameof(ExitedVisibility));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(ToolTip));
    }

    private PaneController AddPane(PaneLeaf leaf, bool animate)
    {
        var pane = new PaneController(leaf, _services);
        _panes[leaf.Id] = pane;
        Host.AddPane(leaf.Id, pane.Container);

        pane.Activated += (_, _) => SetFocusedPane(pane.Id, focusTerminal: false);
        pane.Changed += (_, _) =>
        {
            if (pane.Id == _focusedPaneId)
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            RaiseTabProperties();
        };
        pane.CloseRequested += (_, _) => _ = ClosePaneAsync(pane.Id);
        pane.CommandRequested += (_, id) =>
        {
            SetFocusedPane(pane.Id, focusTerminal: false);
            PaneCommandRequested?.Invoke(this, (pane, id));
        };
        pane.CopyRequested += (_, text) => CopyRequested?.Invoke(this, text);
        pane.SearchResultsChanged += (_, e) =>
        {
            if (pane.Id == _focusedPaneId)
            {
                SearchResultsChanged?.Invoke(this, e);
            }
        };

        pane.Container.Loaded += (_, _) => pane.Reveal(animate, animate && _services.Theme.FluidMotion);
        _ = InitializePaneAsync(pane);
        return pane;
    }

    private async Task RemoveAfterPopOutAsync(PaneController pane)
    {
        await Motion.PopOutAsync(pane.Container);
        Host.RemoveDetached(pane.Container);
    }

    private static async Task InitializePaneAsync(PaneController pane)
    {
        try
        {
            await pane.InitializeAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Error("Terminal renderer initialization failed", ex);
        }
    }

    /// <summary>Hyprland "dwindle"-style choice: split a wide pane side by side, a tall one top to bottom.</summary>
    public PaneArrangement AutoArrangement()
    {
        var container = FocusedPane.Container;
        return container.ActualWidth >= container.ActualHeight * 1.6 ? PaneArrangement.SideBySide : PaneArrangement.Stacked;
    }

    /// <summary>Whether the focused pane has room to be split in this direction without creating unusably small panes.</summary>
    public bool CanSplit(PaneArrangement arrangement) =>
        PaneCount < WorkspaceValidator.MaxPanesPerTab && Host.CanSplit(_focusedPaneId, arrangement);

    /// <summary>The preferred direction if it fits, otherwise the other one, otherwise null.</summary>
    public PaneArrangement? FittingArrangement(PaneArrangement preferred)
    {
        if (CanSplit(preferred))
        {
            return preferred;
        }

        var other = preferred == PaneArrangement.SideBySide ? PaneArrangement.Stacked : PaneArrangement.SideBySide;
        return CanSplit(other) ? other : null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ProfileGlyphs
{
    public static string For(ShellProfile profile) => profile.Icon switch
    {
        ProfileIcons.PowerShell => "",
        ProfileIcons.Cmd => "",
        ProfileIcons.Bash => "",
        ProfileIcons.Linux => "",
        _ => "",
    };
}
