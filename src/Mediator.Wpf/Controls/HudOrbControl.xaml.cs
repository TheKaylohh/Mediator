using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mediator.UI;

namespace Mediator.Wpf.Controls;

/// <summary>
/// The animated orb in the header, bound to <see cref="MediatorOverlayViewModel.ReactorState"/>:
/// <list type="bullet">
/// <item>Idle: slow rotation (10 s per turn), idle colour, the core breathes.</item>
/// <item>Active: fast counter-rotation (1.5 s per turn), active colour, the core pulses quickly.</item>
/// <item>AwaitingApproval: rotation slows (16 s), amber, a radial pulse repeats from the core.</item>
/// <item>Error: crimson, no motion, a steady glow.</item>
/// </list>
/// Only RenderTransform and Opacity are animated (composited on the GPU, no layout passes). With Windows'
/// "Show animations" turned off the orb shows the state colour without motion.
/// </summary>
public partial class HudOrbControl : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ReactorState), typeof(HudOrbControl),
        new FrameworkPropertyMetadata(ReactorState.Idle, (d, _) => ((HudOrbControl)d).ApplyState()));

    /// <summary>The current ring colour; points at the theme's Orb*Brush through a resource reference.</summary>
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(HudOrbControl), new FrameworkPropertyMetadata(Brushes.Transparent));

    public HudOrbControl()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyState();
        Unloaded += (_, _) => StopAll();
    }

    public ReactorState State
    {
        get => (ReactorState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    private void ApplyState()
    {
        SetResourceReference(RingBrushProperty, State switch
        {
            ReactorState.Active => "OrbActiveBrush",
            ReactorState.AwaitingApproval => "OrbApprovalBrush",
            ReactorState.Error => "OrbErrorBrush",
            _ => "OrbIdleBrush",
        });
        StopAll();
        bool motion = SystemParameters.ClientAreaAnimation && IsLoaded;
        switch (State)
        {
            case ReactorState.Idle:
                Glow.Opacity = 0.22;
                if (!motion) break;
                Spin(10);
                Breathe(CoreScale, 0.9, 1.05, TimeSpan.FromSeconds(3));
                break;
            case ReactorState.Active:
                Glow.Opacity = 0.35;
                if (!motion) break;
                Spin(1.5);
                Breathe(CoreScale, 0.8, 1.2, TimeSpan.FromSeconds(0.6));
                break;
            case ReactorState.AwaitingApproval:
                Glow.Opacity = 0.4;
                if (!motion) break;
                Spin(16);
                RadialPulse();
                break;
            case ReactorState.Error:
                Glow.Opacity = 0.65; // static warning glow, no motion
                break;
        }
    }

    /// <summary>Outer clockwise, middle counter-clockwise faster, inner clockwise slower: the counter-rotation.</summary>
    private void Spin(double secondsPerTurn)
    {
        Rotate(OuterSpin, secondsPerTurn, clockwise: true);
        Rotate(MiddleSpin, secondsPerTurn * 0.7, clockwise: false);
        Rotate(InnerSpin, secondsPerTurn * 1.4, clockwise: true);
    }

    private static void Rotate(RotateTransform transform, double secondsPerTurn, bool clockwise)
    {
        double from = transform.Angle % 360;
        var animation = new DoubleAnimation(from, clockwise ? from + 360 : from - 360, TimeSpan.FromSeconds(secondsPerTurn))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        animation.Freeze();
        transform.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private static void Breathe(ScaleTransform scale, double low, double high, TimeSpan half)
    {
        var animation = new DoubleAnimation(low, high, half)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Freeze();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void RadialPulse()
    {
        var period = TimeSpan.FromSeconds(1.4);
        var grow = new DoubleAnimation(1.0, 1.9, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(0.9, 0.0, period) { RepeatBehavior = RepeatBehavior.Forever };
        grow.Freeze();
        fade.Freeze();
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        PulseRing.BeginAnimation(OpacityProperty, fade);
        Breathe(CoreScale, 0.9, 1.1, TimeSpan.FromSeconds(0.7));
    }

    /// <summary>Removes every animation; the transforms keep their current values (rings stop where they are).</summary>
    private void StopAll()
    {
        foreach (var t in new[] { OuterSpin, MiddleSpin, InnerSpin })
        {
            double angle = t.Angle;
            t.BeginAnimation(RotateTransform.AngleProperty, null);
            t.Angle = angle;
        }
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CoreScale.ScaleX = CoreScale.ScaleY = 1;
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.Opacity = 0;
    }
}
