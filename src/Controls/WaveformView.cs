using System;
using System.Windows;
using System.Windows.Media;

namespace Lumen.Controls
{
    internal sealed class WaveformView : FrameworkElement
    {
        private const int HistorySize = 72;
        private readonly float[] _levels = new float[HistorySize];
        private readonly Random _random = new Random();
        private bool _active;
        private int _pushCounter;

        public WaveformView()
        {
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        public void SetActive(bool active)
        {
            _active = active;
            InvalidateVisual();
        }

        public void Push(float peak)
        {
            float value = (float)Math.Sqrt(Math.Max(0f, Math.Min(1f, peak)));
            if (value > 1f) value = 1f;
            for (int i = 0; i < _levels.Length - 1; i++)
            {
                _levels[i] = _levels[i + 1];
            }
            _levels[_levels.Length - 1] = value;
            _pushCounter++;
            InvalidateVisual();
        }

        public void Clear()
        {
            for (int i = 0; i < _levels.Length; i++)
            {
                _levels[i] = 0;
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2 || height <= 2) return;

            double center = height * 0.52;
            int barCount = Math.Min(HistorySize, Math.Max(18, (int)(width / 9.0)));
            double slot = width / barCount;
            double barWidth = Math.Max(2.0, slot * 0.46);
            double maxHeight = height * 0.72;

            Pen baseline = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
            baseline.Freeze();
            drawingContext.DrawLine(baseline, new Point(0, center), new Point(width, center));

            LinearGradientBrush brush = new LinearGradientBrush();
            brush.MappingMode = BrushMappingMode.Absolute;
            brush.StartPoint = new Point(0, height);
            brush.EndPoint = new Point(width, 0);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 124, 92, 255), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 199, 105, 255), 0.38));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 111, 145), 0.72));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 188, 112), 1.0));
            brush.Freeze();

            SolidColorBrush glow = new SolidColorBrush(Color.FromArgb(24, 255, 124, 175));
            glow.Freeze();

            int start = HistorySize - barCount;
            double time = Environment.TickCount / 900.0;
            for (int i = 0; i < barCount; i++)
            {
                float level = _levels[start + i];
                if (_active)
                {
                    double idle = 0.022 + 0.012 * Math.Sin(time + i * 0.46);
                    if (level < idle) level = (float)idle;
                }

                double barHeight = 4 + level * maxHeight;
                if (barHeight > maxHeight + 4) barHeight = maxHeight + 4;
                double x = i * slot + (slot - barWidth) / 2.0;
                double y = center - barHeight / 2.0;

                Rect glowRect = new Rect(x - 2, y - 2, barWidth + 4, barHeight + 4);
                drawingContext.DrawRoundedRectangle(glow, null, glowRect, barWidth / 2 + 2, barWidth / 2 + 2);
                Rect barRect = new Rect(x, y, barWidth, barHeight);
                drawingContext.DrawRoundedRectangle(brush, null, barRect, barWidth / 2, barWidth / 2);
            }

            // A soft highlight that travels across the waveform.
            if (_active && _pushCounter > 0)
            {
                double phase = (Environment.TickCount / 24.0) % Math.Max(1.0, width);
                RadialGradientBrush highlight = new RadialGradientBrush();
                highlight.GradientStops.Add(new GradientStop(Color.FromArgb(30, 255, 230, 244), 0));
                highlight.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 230, 244), 1));
                drawingContext.DrawEllipse(highlight, null, new Point(phase, center), 42, maxHeight * 0.62);
            }
        }
    }
}
