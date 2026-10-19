using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShellFlow.App.Services;
using ShellFlow.Core.Persistence;
using Windows.Graphics;

namespace ShellFlow.App;

/// <summary>
/// The top-level window. Uses a custom title bar (content extends into the caption area) while keeping native
/// caption buttons, snap layouts, drag and double-click-to-maximize behavior.
/// </summary>
public sealed class MainWindow : Window
{
    private const int MinWidth = 640;
    private const int MinHeight = 400;

    private readonly AppServices _services;
    private readonly MainView _view;
    private bool _closeConfirmed;
    private bool _closing;

    public MainWindow(AppServices services)
    {
        _services = services;
        Title = "ShellFlow";
        _view = new MainView(services);
        Content = _view;

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(_view.TitleBarElement);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ShellFlow.ico"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinWidth;
            presenter.PreferredMinimumHeight = MinHeight;
        }

        services.Theme.Attach(_view, AppWindow);
        services.Theme.Apply(services.Settings.Appearance);
        RestorePlacement();

        _view.InteractiveRegionsChanged += (_, _) => UpdateDragRegions();
        _view.SizeChanged += (_, _) => UpdateDragRegions();
        _view.Loaded += async (_, _) =>
        {
            if (services.Theme.FluidMotion)
            {
                Motion.PopIn(_view, TimeSpan.FromMilliseconds(420));
            }

            UpdateDragRegions();
            _view.XamlRoot.Changed += (_, _) => UpdateDragRegions();
            await _view.StartAsync();
        };

        AppWindow.Closing += OnClosing;
    }

    public void ShowError(string message) => _view.ShowNotice(message, InfoBarSeverity.Error);

    /// <summary>A second launch was forwarded here: come to the front and open what it asked for.</summary>
    public void HandleLaunch(Core.StartupArguments request)
    {
        Log.Info($"Forwarded launch received (mode {request.Mode}).");
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));

        if (request.RequestsTerminal)
        {
            _view.OpenFromLaunch(request);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (!await _view.ConfirmCloseAsync())
            {
                return;
            }

            SavePlacement();
            await _view.ShutdownAsync();
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>Marks interactive title-bar elements as pass-through so they receive input instead of dragging.</summary>
    private void UpdateDragRegions()
    {
        if (_view.XamlRoot is null)
        {
            return;
        }

        var scale = _view.XamlRoot.RasterizationScale;
        _view.SetCaptionInset(AppWindow.TitleBar.RightInset / scale);

        // Click-through areas must never reach the minimize / maximize / close buttons, or those stop responding.
        var captionLeft = AppWindow.ClientSize.Width - AppWindow.TitleBar.RightInset;
        var rects = new List<RectInt32>();
        foreach (var element in _view.InteractiveTitleBarElements)
        {
            if (element.ActualWidth <= 0 || element.Visibility != Visibility.Visible)
            {
                continue;
            }

            var transform = element.TransformToVisual(null);
            var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var left = (int)Math.Round(bounds.X * scale);
            var right = Math.Min((int)Math.Round((bounds.X + bounds.Width) * scale), captionLeft);
            if (right <= left)
            {
                continue;
            }

            rects.Add(new RectInt32(left, (int)Math.Round(bounds.Y * scale), right - left, (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    private void RestorePlacement()
    {
        try
        {
            var placement = _services.Workspaces.LoadState().Window;
            if (placement is not null && placement.Width >= MinWidth && placement.Height >= MinHeight)
            {
                var rect = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
                // Only restore onto a monitor that still exists; otherwise let Windows choose.
                if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
                {
                    AppWindow.MoveAndResize(rect);
                }

                if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }

                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not restore window placement: {ex.Message}");
        }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1280, (int)(area.Width * 0.85));
        var height = Math.Min(800, (int)(area.Height * 0.85));
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    private void SavePlacement()
    {
        try
        {
            var state = _services.Workspaces.LoadState();
            var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            var minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (!minimized)
            {
                state.Window = maximized && state.Window is not null
                    ? new WindowPlacement { X = state.Window.X, Y = state.Window.Y, Width = state.Window.Width, Height = state.Window.Height, Maximized = true }
                    : new WindowPlacement { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height, Maximized = maximized };
                _services.Workspaces.SaveState(state);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save window placement: {ex.Message}");
        }
    }
}
