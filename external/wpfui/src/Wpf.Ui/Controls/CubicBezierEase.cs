using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Wpf.Ui.Controls
{
    public sealed class CubicBezierEase : EasingFunctionBase
    {
        private const double Tolerance = 0.000001d;

        private const int Refinements = 8;

        public static readonly DependencyProperty X1Property = DependencyProperty.Register(
            nameof(X1),
            typeof(double),
            typeof(CubicBezierEase),
            new PropertyMetadata(0.4d));

        public static readonly DependencyProperty Y1Property = DependencyProperty.Register(
            nameof(Y1),
            typeof(double),
            typeof(CubicBezierEase),
            new PropertyMetadata(0d));

        public static readonly DependencyProperty X2Property = DependencyProperty.Register(
            nameof(X2),
            typeof(double),
            typeof(CubicBezierEase),
            new PropertyMetadata(0.2d));

        public static readonly DependencyProperty Y2Property = DependencyProperty.Register(
            nameof(Y2),
            typeof(double),
            typeof(CubicBezierEase),
            new PropertyMetadata(1d));

        public CubicBezierEase()
        {
            EasingMode = EasingMode.EaseIn;
        }

        public double X1
        {
            get => (double)GetValue(X1Property);
            set => SetValue(X1Property, value);
        }

        public double Y1
        {
            get => (double)GetValue(Y1Property);
            set => SetValue(Y1Property, value);
        }

        public double X2
        {
            get => (double)GetValue(X2Property);
            set => SetValue(X2Property, value);
        }

        public double Y2
        {
            get => (double)GetValue(Y2Property);
            set => SetValue(Y2Property, value);
        }

        protected override double EaseInCore(double normalizedTime)
        {
            if (normalizedTime <= 0d)
            {
                return 0d;
            }

            if (normalizedTime >= 1d)
            {
                return 1d;
            }

            double curveTime = SolveForTime(normalizedTime, X1, X2);
            return Evaluate(curveTime, Y1, Y2);
        }

        protected override Freezable CreateInstanceCore() => new CubicBezierEase();

        private static double Evaluate(double t, double first, double second)
        {
            double inverse = 1d - t;
            return (3d * inverse * inverse * t * first) + (3d * inverse * t * t * second) + (t * t * t);
        }

        private static double Slope(double t, double first, double second)
        {
            double inverse = 1d - t;
            return (3d * inverse * inverse * first)
                + (6d * inverse * t * (second - first))
                + (3d * t * t * (1d - second));
        }

        private static double SolveForTime(double x, double first, double second)
        {
            double t = x;

            for (int index = 0; index < Refinements; index++)
            {
                double error = Evaluate(t, first, second) - x;
                if (Math.Abs(error) < Tolerance)
                {
                    break;
                }

                double slope = Slope(t, first, second);
                if (Math.Abs(slope) < Tolerance)
                {
                    break;
                }

                t -= error / slope;
            }

            return t < 0d ? 0d : t > 1d ? 1d : t;
        }
    }
}
