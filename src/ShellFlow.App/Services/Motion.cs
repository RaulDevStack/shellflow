using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace ShellFlow.App.Services;

/// <summary>
/// Hyprland-inspired motion built on composition animations. Everything animates UIElement facade properties
/// (Scale, Translation, Opacity) on the compositor thread, so typing, shell output and resizing are never blocked.
/// Layout is always applied immediately at its final size; animations only move pixels toward it.
/// </summary>
public static class Motion
{
    public static readonly TimeSpan PopInDuration = TimeSpan.FromMilliseconds(340);
    public static readonly TimeSpan PopOutDuration = TimeSpan.FromMilliseconds(170);
    public static readonly TimeSpan LayoutDuration = TimeSpan.FromMilliseconds(380);
    public static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(420);

    /// <summary>"overshot" curve, the classic Hyprland bezier (0.05, 0.9, 0.1, 1.05): fast start, soft settle with a hint of overshoot.</summary>
    private static CompositionEasingFunction Overshoot(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.9f), new Vector2(0.1f, 1.05f));

    private static CompositionEasingFunction EaseIn(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

    // Never use ElementCompositionPreview.GetElementVisual here: it switches the element to hand-off mode, after
    // which WinUI rejects the facade animations (Scale/Translation/Opacity) and *Transition properties used below.
    private static Compositor CompositorFor(UIElement element) => Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();

    /// <summary>The element grows from 85 % with a fade, from its center.</summary>
    public static void PopIn(FrameworkElement element, TimeSpan? duration = null)
    {
        var c = CompositorFor(element);
        var time = duration ?? PopInDuration;
        element.OpacityTransition = null;
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        element.Scale = Vector3.One;
        element.Opacity = 1;

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.Duration = time;
        scale.InsertKeyFrame(0f, new Vector3(0.85f, 0.85f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, Overshoot(c));

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = TimeSpan.FromMilliseconds(time.TotalMilliseconds * 0.6);
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);

        element.StartAnimation(scale);
        element.StartAnimation(fade);
    }

    /// <summary>The element shrinks slightly and fades out. Completes when the animation is done.</summary>
    public static Task PopOutAsync(FrameworkElement element, TimeSpan? duration = null)
    {
        var c = CompositorFor(element);
        var time = duration ?? PopOutDuration;
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.Duration = time;
        scale.InsertKeyFrame(1f, new Vector3(0.9f, 0.9f, 1f), EaseIn(c));

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = time;
        fade.InsertKeyFrame(1f, 0f, EaseIn(c));

        element.StartAnimation(scale);
        element.StartAnimation(fade);
        return Task.Delay(time);
    }

    /// <summary>
    /// "FLIP": the element has already been arranged at <paramref name="to"/>; it starts visually at
    /// <paramref name="from"/> and glides into place. The terminal inside is resized once, not every frame.
    /// </summary>
    public static void Glide(FrameworkElement element, Rect from, Rect to, TimeSpan? duration = null)
    {
        if (to.Width <= 1 || to.Height <= 1 || from.Width <= 1 || from.Height <= 1)
        {
            return;
        }

        var c = CompositorFor(element);
        var time = duration ?? LayoutDuration;
        element.CenterPoint = Vector3.Zero;
        element.Scale = Vector3.One;
        element.Translation = Vector3.Zero;

        var scale = c.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.Duration = time;
        scale.InsertKeyFrame(0f, new Vector3((float)(from.Width / to.Width), (float)(from.Height / to.Height), 1f));
        scale.InsertKeyFrame(1f, Vector3.One, Overshoot(c));

        var move = c.CreateVector3KeyFrameAnimation();
        move.Target = "Translation";
        move.Duration = time;
        move.InsertKeyFrame(0f, new Vector3((float)(from.X - to.X), (float)(from.Y - to.Y), 0f));
        move.InsertKeyFrame(1f, Vector3.Zero, Overshoot(c));

        element.StartAnimation(scale);
        element.StartAnimation(move);
    }

    /// <summary>
    /// Workspace-style slide: <paramref name="incoming"/> enters from the side (or from above/below when
    /// <paramref name="vertical"/>), <paramref name="outgoing"/> leaves the opposite way.
    /// </summary>
    /// <param name="direction">+1 when moving to a tab further right (or down), -1 to the left (or up).</param>
    /// <param name="distance">How far to travel: the container's width, or its height when vertical.</param>
    public static Task SlideAsync(UIElement? outgoing, UIElement incoming, int direction, double distance, bool vertical = false, TimeSpan? duration = null)
    {
        var c = CompositorFor(incoming);
        var time = duration ?? SlideDuration;
        var offset = (float)Math.Max(distance, 1) * Math.Sign(direction == 0 ? 1 : direction);
        var start = vertical ? new Vector3(0, offset, 0) : new Vector3(offset, 0, 0);

        var enter = c.CreateVector3KeyFrameAnimation();
        enter.Target = "Translation";
        enter.Duration = time;
        enter.InsertKeyFrame(0f, start);
        enter.InsertKeyFrame(1f, Vector3.Zero, Overshoot(c));
        incoming.Translation = Vector3.Zero;
        incoming.StartAnimation(enter);

        if (outgoing is not null)
        {
            var leave = c.CreateVector3KeyFrameAnimation();
            leave.Target = "Translation";
            leave.Duration = time;
            leave.InsertKeyFrame(0f, Vector3.Zero);
            leave.InsertKeyFrame(1f, -start, Overshoot(c));
            outgoing.StartAnimation(leave);
        }

        return Task.Delay(time);
    }
}
