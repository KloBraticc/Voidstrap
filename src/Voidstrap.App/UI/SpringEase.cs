using System.Windows;
using System.Windows.Media.Animation;

namespace Voidstrap.UI;

public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(0.78));

    public static readonly DependencyProperty StiffnessProperty =
        DependencyProperty.Register(nameof(Stiffness), typeof(double), typeof(SpringEase), new PropertyMetadata(11.0));

    public SpringEase()
    {
        EasingMode = EasingMode.EaseIn;
    }

    public double Damping
    {
        get => (double)GetValue(DampingProperty);
        set => SetValue(DampingProperty, value);
    }

    public double Stiffness
    {
        get => (double)GetValue(StiffnessProperty);
        set => SetValue(StiffnessProperty, value);
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1)
            return 1;
        double zeta = Math.Clamp(Damping, 0.05, 0.99);
        double omega = Stiffness;
        double damped = omega * Math.Sqrt(1 - zeta * zeta);
        double decay = Math.Exp(-zeta * omega * normalizedTime);
        return 1 - decay * (Math.Cos(damped * normalizedTime) + zeta * omega / damped * Math.Sin(damped * normalizedTime));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
