using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using ShellFlow.App.Services;
using ShellFlow.Core.Layout;
using ShellFlow.Core.Profiles;
using ShellFlow.Core.Workspaces;

namespace ShellFlow.App.Workspace;

/// <summary>The open workspace: its tabs, the selected tab, and debounced persistence of its structure.</summary>
public sealed class WorkspaceController
{
    private readonly AppServices _services;
    private readonly Grid _hostContainer;
    private readonly DispatcherQueueTimer _autosave;
    private TabController? _selected;

    public WorkspaceController(AppServices services, Grid hostContainer)
    {
        _services = services;
        _hostContainer = hostContainer;
        _autosave = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _autosave.Interval = TimeSpan.FromSeconds(1.5);
        _autosave.IsRepeating = false;
        _autosave.Tick += (_, _) => Save();
        Tabs.CollectionChanged += (_, _) => ScheduleSave();
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? StatusChanged;

    public event EventHandler? TabsEmptied;

    public event EventHandler<(PaneController Pane, string CommandId)>? PaneCommandRequested;

    public event EventHandler<string>? CopyRequested;

    public event EventHandler<TerminalHost.SearchResultEventArgs>? SearchResultsChanged;

    public ObservableCollection<TabController> Tabs { get; } = [];

    public string Id { get; private set; } = WorkspaceDocument.NewId();

    public string Name { get; private set; } = "Default";

    public TabController? SelectedTab
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            var previous = _selected;
            var slide = previous is not null && value is not null && _services.Theme.FluidMotion && _hostContainer.ActualWidth > 0;
            if (previous is not null)
            {
                if (slide)
                {
                    previous.BeginHide();
                }
                else
                {
                    previous.IsSelected = false;
                }
            }

            _selected = value;
            if (_selected is not null)
            {
                _selected.IsSelected = true;
                _selected.Focus();
                if (slide)
                {
                    // Like switching Hyprland workspaces: the new tab enters from the side it sits on.
                    var direction = Tabs.IndexOf(_selected) >= Tabs.IndexOf(previous!) ? 1 : -1;
                    _ = SlideAsync(previous!, _selected, direction);
                }
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    public PaneController? FocusedPane => _selected?.FocusedPane;

    public bool HasRunningCommands => Tabs.Any(t => t.HasRunningCommand);

    public int RunningCommandCount => Tabs.Sum(t => t.RunningCommandCount);

    /// <summary>Opens a normalized workspace document, starting a fresh shell for every pane.</summary>
    public void Load(WorkspaceDocument document)
    {
        Id = document.Id;
        Name = document.Name;
        foreach (var tabDocument in document.Tabs)
        {
            AddTab(new TabController(_services, tabDocument));
        }

        SelectedTab = Tabs.Count > 0 ? Tabs[Math.Clamp(document.SelectedTabIndex, 0, Tabs.Count - 1)] : null;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string name)
    {
        Name = WorkspaceValidator.NormalizeName(name);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    public TabController NewTab(ShellProfile? profile = null, string? startingDirectory = null, string? script = null)
    {
        var leaf = new PaneLeaf { ProfileId = (profile ?? _services.Profiles.Default).Id, StartingDirectory = startingDirectory, Script = script };
        var tab = new TabController(_services, new TabDocument { Root = leaf, FocusedPaneId = leaf.Id });
        var index = _selected is null ? Tabs.Count : Tabs.IndexOf(_selected) + 1;
        AddTab(tab, index);
        SelectedTab = tab;
        return tab;
    }

    public async Task CloseTabAsync(TabController tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (ReferenceEquals(tab, _selected))
        {
            var next = Tabs.Count > 1 ? Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1] : null;
            SelectedTab = next;
        }

        Tabs.RemoveAt(index);
        _hostContainer.Children.Remove(tab.Host);
        await tab.CloseAllAsync();

        if (Tabs.Count == 0)
        {
            TabsEmptied?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectRelative(int offset)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        var index = _selected is null ? 0 : Tabs.IndexOf(_selected);
        SelectedTab = Tabs[(((index + offset) % Tabs.Count) + Tabs.Count) % Tabs.Count];
    }

    public WorkspaceDocument ToDocument() => new()
    {
        Id = Id,
        Name = Name,
        Tabs = Tabs.Select(t => t.ToDocument()).ToList(),
        SelectedTabIndex = _selected is null ? 0 : Math.Max(0, Tabs.IndexOf(_selected)),
    };

    public void ScheduleSave()
    {
        _autosave.Stop();
        _autosave.Start();
    }

    public void Save()
    {
        _autosave.Stop();
        try
        {
            _services.Workspaces.Save(ToDocument());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not save workspace '{Name}'", ex);
        }
    }

    /// <summary>Stops every shell in this workspace and removes its tabs (the saved document is kept).</summary>
    public async Task UnloadAsync()
    {
        _autosave.Stop();
        var tabs = Tabs.ToList();
        _selected = null;
        Tabs.Clear();
        _hostContainer.Children.Clear();
        await Task.WhenAll(tabs.Select(t => t.CloseAllAsync()));
    }

    public void ApplyRendererOptions(Core.Bridge.RendererOptions options)
    {
        foreach (var tab in Tabs)
        {
            tab.ApplyRendererOptions(options);
        }
    }

    private async Task SlideAsync(TabController outgoing, TabController incoming, int direction)
    {
        await Motion.SlideAsync(outgoing.Host, incoming.Host, direction, _hostContainer.ActualWidth);
        outgoing.FinishHide();
    }

    private void AddTab(TabController tab, int? index = null)
    {
        tab.IsSelected = false;
        tab.Emptied += async (_, _) => await CloseTabAsync(tab);
        tab.StructureChanged += (_, _) => ScheduleSave();
        tab.StatusChanged += (_, _) =>
        {
            if (ReferenceEquals(tab, _selected))
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        tab.PaneCommandRequested += (_, e) => PaneCommandRequested?.Invoke(this, e);
        tab.CopyRequested += (_, text) => CopyRequested?.Invoke(this, text);
        tab.SearchResultsChanged += (_, e) =>
        {
            if (ReferenceEquals(tab, _selected))
            {
                SearchResultsChanged?.Invoke(this, e);
            }
        };

        _hostContainer.Children.Add(tab.Host);
        if (index is { } i && i >= 0 && i <= Tabs.Count)
        {
            Tabs.Insert(i, tab);
        }
        else
        {
            Tabs.Add(tab);
        }
    }
}
