using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ShellFlow.App.Services;
using ShellFlow.Core.Layout;
using Windows.Foundation;

namespace ShellFlow.App.Controls;

/// <summary>
/// Lays out a tab's panes from its split tree. Pane elements are never re-parented when the layout changes —
/// only re-arranged — so WebView2 renderers and shell sessions survive splits, closes and resizes untouched.
/// </summary>
public sealed partial class PaneHostPanel : Panel
{
    public const double Gap = 6;
    public const double MinPaneWidth = 160;
    public const double MinPaneHeight = 96;
    private const double SplitterHitPadding = 3;

    private readonly Dictionary<string, FrameworkElement> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<PaneSplit, SplitterHandle> _splitters = [];
    private readonly Dictionary<string, Rect> _arranged = new(StringComparer.Ordinal);
    private PaneNode? _root;
    private PaneLayout? _layout;
    private bool _animateNextArrange;

    /// <summary>Raised after the user finishes dragging a divider.</summary>
    public event EventHandler? LayoutEdited;

    /// <summary>Whether structural layout changes should glide (Hyprland-style). Evaluated at arrange time.</summary>
    public Func<bool>? ShouldAnimateLayout { get; set; }

    public PaneNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _animateNextArrange = true;
            SyncSplitters();
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    public void AddPane(string id, FrameworkElement element)
    {
        _panes[id] = element;
        Children.Add(element);
        InvalidateMeasure();
    }

    public void RemovePane(string id)
    {
        if (_panes.Remove(id, out var element))
        {
            Children.Remove(element);
            _arranged.Remove(id);
            InvalidateMeasure();
        }
    }

    /// <summary>Takes a pane out of the layout but keeps it on screen (for an exit animation) until <see cref="RemovePane"/>.</summary>
    public void DetachPane(string id)
    {
        if (_panes.Remove(id, out var element))
        {
            _arranged.Remove(id);
            element.IsHitTestVisible = false;
            Canvas.SetZIndex(element, -1);
            InvalidateMeasure();
        }
    }

    public void RemoveDetached(FrameworkElement element) => Children.Remove(element);

    /// <summary>
    /// True when splitting the pane in this direction leaves both halves at least the minimum pane size.
    /// Panes that have not been laid out yet (hidden tab) are allowed.
    /// </summary>
    public bool CanSplit(string paneId, PaneArrangement arrangement)
    {
        if (!_arranged.TryGetValue(paneId, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return true;
        }

        return arrangement == PaneArrangement.SideBySide
            ? rect.Width >= (2 * MinPaneWidth) + Gap
            : rect.Height >= (2 * MinPaneHeight) + Gap;
    }

    /// <summary>Re-applies the current split ratios. <paramref name="animate"/> glides panes into place.</summary>
    public void Refresh(bool animate = false)
    {
        _animateNextArrange |= animate;
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;
        _layout = _root is null ? null : PaneLayoutEngine.Compute(_root, new LayoutRect(0, 0, width, height), Gap, MinPaneWidth, MinPaneHeight);

        foreach (var (id, element) in _panes)
        {
            var rect = _layout is not null && _layout.Panes.TryGetValue(id, out var r) ? r : default;
            element.Measure(new Size(Math.Max(0, rect.Width), Math.Max(0, rect.Height)));
        }

        foreach (var splitter in _splitters.Values)
        {
            splitter.Measure(new Size(Gap + (2 * SplitterHitPadding), Gap + (2 * SplitterHitPadding)));
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layout = _root is null ? null : PaneLayoutEngine.Compute(_root, new LayoutRect(0, 0, finalSize.Width, finalSize.Height), Gap, MinPaneWidth, MinPaneHeight);

        var animate = _animateNextArrange && (ShouldAnimateLayout?.Invoke() ?? false);
        _animateNextArrange = false;

        foreach (var (id, element) in _panes)
        {
            if (_layout is not null && _layout.Panes.TryGetValue(id, out var rect))
            {
                var target = new Rect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
                element.Arrange(target);
                if (animate && _arranged.TryGetValue(id, out var previous) && previous != target)
                {
                    Motion.Glide(element, previous, target);
                }

                _arranged[id] = target;
            }
            else
            {
                element.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        // Detached panes (playing their exit animation) keep their last rectangle.
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            if (child is not SplitterHandle && !_panes.ContainsValue(child))
            {
                child.Arrange(new Rect(child.ActualOffset.X, child.ActualOffset.Y, child.ActualWidth, child.ActualHeight));
            }
        }

        if (_layout is not null)
        {
            foreach (var splitterLayout in _layout.Splitters)
            {
                if (_splitters.TryGetValue(splitterLayout.Split, out var handle))
                {
                    handle.Layout = splitterLayout;
                    var b = splitterLayout.Bounds;
                    var hit = splitterLayout.Arrangement == PaneArrangement.SideBySide
                        ? new Rect(b.X - SplitterHitPadding, b.Y, b.Width + (2 * SplitterHitPadding), b.Height)
                        : new Rect(b.X, b.Y - SplitterHitPadding, b.Width, b.Height + (2 * SplitterHitPadding));
                    handle.Arrange(hit);
                }
            }
        }

        return finalSize;
    }

    private void SyncSplitters()
    {
        var splits = new HashSet<PaneSplit>();
        Collect(_root, splits);

        foreach (var stale in _splitters.Keys.Where(s => !splits.Contains(s)).ToList())
        {
            Children.Remove(_splitters[stale]);
            _splitters.Remove(stale);
        }

        foreach (var split in splits)
        {
            if (!_splitters.ContainsKey(split))
            {
                var handle = new SplitterHandle(this, split);
                _splitters[split] = handle;
                Children.Add(handle); // Added after panes, so splitters stay on top for hit testing.
            }
            else if (_splitters[split].Arrangement != split.Arrangement)
            {
                _splitters[split].UpdateCursor();
            }
        }

        static void Collect(PaneNode? node, HashSet<PaneSplit> into)
        {
            if (node is PaneSplit split)
            {
                into.Add(split);
                Collect(split.First, into);
                Collect(split.Second, into);
            }
        }
    }

    private void OnSplitterDragCompleted() => LayoutEdited?.Invoke(this, EventArgs.Empty);

    /// <summary>The draggable divider between two panes.</summary>
    private sealed partial class SplitterHandle : Grid
    {
        private readonly PaneHostPanel _owner;
        private readonly PaneSplit _split;
        private readonly Rectangle _line;
        private bool _dragging;

        public SplitterHandle(PaneHostPanel owner, PaneSplit split)
        {
            _owner = owner;
            _split = split;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Hit-testable but invisible.
            _line = new Rectangle
            {
                RadiusX = 1,
                RadiusY = 1,
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) },
            };
            Children.Add(_line);
            _line.Fill = ThemeResources.Brush("SfAccentBorderBrush", owner.ActualTheme);
            ActualThemeChanged += (_, _) => _line.Fill = ThemeResources.Brush("SfAccentBorderBrush", ActualTheme);
            AutomationProperties.SetName(this, "Pane divider");
            UpdateCursor();

            PointerEntered += (_, _) => _line.Opacity = 1;
            PointerExited += (_, _) =>
            {
                if (!_dragging)
                {
                    _line.Opacity = 0;
                }
            };
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerCaptureLost += (_, _) => EndDrag();
            DoubleTapped += (_, e) =>
            {
                // Double-click a divider to split the space evenly again.
                _split.Ratio = 0.5;
                _owner.Refresh();
                _owner.OnSplitterDragCompleted();
                e.Handled = true;
            };
        }

        public PaneArrangement Arrangement { get; private set; }

        public SplitterLayout? Layout { get; set; }

        public void UpdateCursor()
        {
            Arrangement = _split.Arrangement;
            var sideBySide = Arrangement == PaneArrangement.SideBySide;
            ProtectedCursor = InputSystemCursor.Create(sideBySide ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
            _line.Width = sideBySide ? 2 : double.NaN;
            _line.Height = sideBySide ? double.NaN : 2;
            _line.HorizontalAlignment = sideBySide ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            _line.VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Center;
            _line.Margin = sideBySide ? new Thickness(0, 10, 0, 10) : new Thickness(10, 0, 10, 0);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (CapturePointer(e.Pointer))
            {
                _dragging = true;
                _line.Opacity = 1;
                e.Handled = true;
            }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || Layout is null)
            {
                return;
            }

            var point = e.GetCurrentPoint(_owner).Position;
            var position = Arrangement == PaneArrangement.SideBySide ? point.X : point.Y;
            var ratio = Layout.RatioFromPosition(position);
            if (Math.Abs(ratio - _split.Ratio) > 0.0005)
            {
                _split.Ratio = ratio;
                _owner.Refresh();
            }

            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            ReleasePointerCapture(e.Pointer);
            EndDrag();
            e.Handled = true;
        }

        private void EndDrag()
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            _line.Opacity = 0;
            _owner.OnSplitterDragCompleted();
        }
    }
}
