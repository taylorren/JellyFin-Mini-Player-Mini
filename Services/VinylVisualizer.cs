using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RoundSoundMimic.Services
{
    /// <summary>
    /// Owns the purely visual parts of the vinyl record: the radial EQ spikes that
    /// ring the progress ring, and the "band" (the illuminated arc shape) drawn over
    /// the inner circle. Keeping this out of the window class separates rendering
    /// concerns from the window / interaction logic.
    /// </summary>
    public class VinylVisualizer
    {
        private const int EqSpikeCount = 48;

        private readonly List<Line> _eqSpikes = new();

        private Canvas? _eqCanvas;
        private Ellipse? _progressRing;
        private FrameworkElement? _innerCircle;
        private Path? _bandPath;
        private DropShadowEffect? _eqGlowEffect;
        private Brush? _eqSpikeBrush;
        private IAudioProcessingService? _audioService;

        /// <summary>
        /// Binds the visualizer to the controls it needs. Call once (let us say, in
        /// the window constructor) before any of the Update/On* methods are invoked.
        /// </summary>
        public void Bind(Canvas? eqCanvas, Ellipse? progressRing, FrameworkElement? innerCircle,
                         Path? bandPath, DropShadowEffect? eqGlowEffect, Brush? eqSpikeBrush,
                         IAudioProcessingService? audioService)
        {
            _eqCanvas = eqCanvas;
            _progressRing = progressRing;
            _innerCircle = innerCircle;
            _bandPath = bandPath;
            _eqGlowEffect = eqGlowEffect;
            _eqSpikeBrush = eqSpikeBrush;
            _audioService = audioService;
        }

        public void OnCanvasLoaded()
        {
            InitializeEqSpikes();
            UpdateBandGeometry();
            UpdateEqGeometry();
        }

        public void OnCanvasSizeChanged()
        {
            UpdateBandGeometry();
            UpdateEqGeometry();
        }

        public void OnInnerCircleSizeChanged()
        {
            UpdateBandGeometry();
        }

        public void OnEqValuesUpdated()
        {
            UpdateEqGeometry();
        }

        public void Update()
        {
            UpdateEqGeometry();
        }

        public void InitializeEqSpikes()
        {
            if (_eqCanvas is null)
            {
                return;
            }

            _eqCanvas.Children.Clear();
            _eqSpikes.Clear();

            var stroke = _eqSpikeBrush ?? Brushes.Orange;
            for (var i = 0; i < EqSpikeCount; i++)
            {
                var line = new Line
                {
                    Stroke = stroke,
                    StrokeThickness = 5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _eqCanvas.Children.Add(line);
                _eqSpikes.Add(line);
            }
        }

        private void UpdateEqGeometry()
        {
            var spikes = _eqSpikes;
            var audioService = _audioService;
            if (spikes is null || spikes.Count == 0 || audioService is null)
            {
                return;
            }

            var progressRing = _progressRing;
            if (progressRing is null)
            {
                return;
            }

            var ringWidth = progressRing.ActualWidth > 0 ? progressRing.ActualWidth : progressRing.Width;
            var ringHeight = progressRing.ActualHeight > 0 ? progressRing.ActualHeight : progressRing.Height;
            if (ringWidth <= 0 || ringHeight <= 0)
            {
                return;
            }

            var ringLeft = Canvas.GetLeft(progressRing);
            var ringTop = Canvas.GetTop(progressRing);
            if (double.IsNaN(ringLeft))
            {
                ringLeft = 0;
            }

            if (double.IsNaN(ringTop))
            {
                ringTop = 0;
            }

            var ringRadius = Math.Min(ringWidth, ringHeight) / 2.0;
            var centerX = ringLeft + ringWidth / 2.0;
            var centerY = ringTop + ringHeight / 2.0;
            var baseRadius = ringRadius + progressRing.StrokeThickness / 2.0 + 4;
            var minSpike = Math.Max(3, ringRadius * 0.04);
            var maxSpike = Math.Max(8, ringRadius * 0.14);

            // Get current EQ values from the service
            var eqValues = audioService.GetCurrentEqValues();

            // Batch updates to reduce rendering overhead
            var count = Math.Min(Math.Min(spikes.Count, eqValues.Length), EqSpikeCount);
            var totalIntensity = 0.0;

            for (var i = 0; i < count; i++)
            {
                var angle = (Math.PI * 2.0 * i) / count;
                var sin = Math.Sin(angle);
                var cos = Math.Cos(angle);

                var intensity = eqValues[i];
                totalIntensity += intensity;

                // Exponential scaling for more visual impact on high peaks
                var scaledIntensity = Math.Pow(intensity, 1.2);
                var spikeLength = minSpike + scaledIntensity * (maxSpike - minSpike);

                var x1 = centerX + cos * baseRadius;
                var y1 = centerY + sin * baseRadius;
                var x2 = centerX + cos * (baseRadius + spikeLength);
                var y2 = centerY + sin * (baseRadius + spikeLength);

                var line = spikes[i];
                // Only update if values have changed significantly to reduce rendering overhead
                if (Math.Abs(line.X1 - x1) > 0.1 || Math.Abs(line.Y1 - y1) > 0.1 ||
                    Math.Abs(line.X2 - x2) > 0.1 || Math.Abs(line.Y2 - y2) > 0.1)
                {
                    line.X1 = x1;
                    line.Y1 = y1;
                    line.X2 = x2;
                    line.Y2 = y2;
                }

                // Enhance visual appearance: color gradient and dynamic thickness
                // Base color is Orange (#FF8A2A) to Light Orange (#FFC166)
                var r = (byte)255;
                var g = (byte)(138 + (193 - 138) * intensity);
                var b = (byte)(42 + (102 - 42) * intensity);

                line.StrokeThickness = 3 + intensity * 6; // Dynamic thickness
                line.Opacity = 0.6 + intensity * 0.4;    // More opaque when active

                // Reuse or update brush efficiently
                if (line.Stroke is not SolidColorBrush scb || scb.Color.G != g)
                {
                    line.Stroke = new SolidColorBrush(Color.FromRgb(r, g, b));
                }
            }

            // Update the overall canvas glow based on average intensity
            if (_eqGlowEffect is not null)
            {
                var avgIntensity = totalIntensity / count;
                _eqGlowEffect.BlurRadius = 15 + avgIntensity * 30;
                _eqGlowEffect.Opacity = 0.4 + avgIntensity * 0.6;

                // Color shifts slightly towards lighter orange at high intensity
                var gr = (byte)255;
                var gg = (byte)(138 + (193 - 138) * avgIntensity);
                var gb = (byte)(42 + (102 - 42) * avgIntensity);
                _eqGlowEffect.Color = Color.FromRgb(gr, gg, gb);
            }
        }

        private void UpdateBandGeometry()
        {
            if (_bandPath is null || _innerCircle is null)
            {
                return;
            }

            var innerLeft = Canvas.GetLeft(_innerCircle);
            var innerTop = Canvas.GetTop(_innerCircle);
            if (double.IsNaN(innerLeft))
            {
                innerLeft = 0;
            }

            if (double.IsNaN(innerTop))
            {
                innerTop = 0;
            }

            var innerWidth = _innerCircle.ActualWidth > 0 ? _innerCircle.ActualWidth : _innerCircle.Width;
            var innerHeight = _innerCircle.ActualHeight > 0 ? _innerCircle.ActualHeight : _innerCircle.Height;
            if (innerWidth <= 0 || innerHeight <= 0)
            {
                return;
            }

            const double widthPaddingRatio = 12.0 / 520.0;
            const double heightRatio = 140.0 / 520.0;

            var rectWidth = Math.Max(0, innerWidth * (1.0 - widthPaddingRatio));
            var rectHeight = Math.Max(0, innerHeight * heightRatio);

            var rectX = innerLeft + (innerWidth - rectWidth) / 2.0;
            var rectY = innerTop + (innerHeight - rectHeight) / 2.0;
            var rectRadius = rectHeight / 2.0;

            var rectGeom = new RectangleGeometry(new Rect(rectX, rectY, rectWidth, rectHeight), rectRadius, rectRadius);

            var centerX = innerLeft + innerWidth / 2.0;
            var centerY = innerTop + innerHeight / 2.0;
            var ellipseGeom = new EllipseGeometry(new Point(centerX, centerY), innerWidth / 2.0, innerHeight / 2.0);

            // Only update if the geometry has changed significantly to reduce rendering overhead
            var newGeometry = Geometry.Combine(rectGeom, ellipseGeom, GeometryCombineMode.Intersect, null);

            // Check if the new geometry is different from the current one before updating
            if (_bandPath.Data == null || !_bandPath.Data.Equals(newGeometry))
            {
                _bandPath.Data = newGeometry;
            }
        }
    }
}