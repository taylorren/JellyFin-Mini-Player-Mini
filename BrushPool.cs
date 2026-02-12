using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace RoundSoundMimic
{
    /// <summary>
    /// High-performance brush pooling and caching for WPF rendering optimization
    /// Eliminates real-time SolidColorBrush allocations and reuses pre-calculated color gradients
    /// </summary>
    public sealed class BrushPool : IDisposable
    {
        private const int ColorSteps = 256; // Fine-grained color steps for smooth gradients
        private const int PoolSize = 100; // Maximum brushes to keep in pool
        
        private readonly SolidColorBrush[] _colorGradientBrushes;
        private readonly Stack<SolidColorBrush> _availableBrushes;
        private readonly HashSet<SolidColorBrush> _activeBrushes;
        private readonly object _lock = new();
        
        // Pre-calculated gradient from dark orange (#FF8A2A) to light orange (#FFC166)
        private static readonly Color BaseColor = Color.FromRgb(255, 138, 42);
        private static readonly Color PeakColor = Color.FromRgb(255, 193, 102);
        
        public BrushPool()
        {
            _colorGradientBrushes = new SolidColorBrush[ColorSteps];
            _availableBrushes = new Stack<SolidColorBrush>(PoolSize);
            _activeBrushes = new HashSet<SolidColorBrush>();
            
            // Pre-calculate color gradient brushes
            PreCalculateGradientBrushes();
        }
        
        /// <summary>
        /// Gets or creates a brush for the given intensity (0.0 to 1.0)
        /// </summary>
        public SolidColorBrush GetBrush(double intensity)
        {
            if (intensity < 0.0) intensity = 0.0;
            if (intensity > 1.0) intensity = 1.0;
            
            // Map intensity to pre-calculated brush index
            var index = (int)(intensity * (ColorSteps - 1));
            return _colorGradientBrushes[index];
        }
        
        /// <summary>
        /// Gets a brush from the pool for custom colors
        /// </summary>
        public SolidColorBrush GetPooledBrush(Color color)
        {
            lock (_lock)
            {
                if (_availableBrushes.Count > 0)
                {
                    var brush = _availableBrushes.Pop();
                    brush.Color = color;
                    _activeBrushes.Add(brush);
                    return brush;
                }
            }
            
            // Pool exhausted, create new brush
            var newBrush = new SolidColorBrush(color);
            newBrush.Freeze(); // Freeze for better performance
            lock (_lock)
            {
                _activeBrushes.Add(newBrush);
            }
            return newBrush;
        }
        
        /// <summary>
        /// Returns a brush back to the pool for reuse
        /// </summary>
        public void ReturnBrush(SolidColorBrush brush)
        {
            if (brush == null) return;
            
            lock (_lock)
            {
                if (_activeBrushes.Remove(brush))
                {
                    if (_availableBrushes.Count < PoolSize)
                    {
                        _availableBrushes.Push(brush);
                    }
                }
            }
        }
        
        /// <summary>
        /// Pre-calculates all gradient brushes for the orange spectrum
        /// </summary>
        private void PreCalculateGradientBrushes()
        {
            for (int i = 0; i < ColorSteps; i++)
            {
                var intensity = i / (double)(ColorSteps - 1);
                var color = InterpolateColor(BaseColor, PeakColor, intensity);
                var brush = new SolidColorBrush(color);
                brush.Freeze(); // Freeze for thread safety and performance
                _colorGradientBrushes[i] = brush;
            }
        }
        
        /// <summary>
        /// Linear interpolation between two colors
        /// </summary>
        private static Color InterpolateColor(Color start, Color end, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            
            var r = (byte)(start.R + (end.R - start.R) * t);
            var g = (byte)(start.G + (end.G - start.G) * t);
            var b = (byte)(start.B + (end.B - start.B) * t);
            
            return Color.FromRgb(r, g, b);
        }
        
        /// <summary>
        /// Performance statistics
        /// </summary>
        public BrushPoolStats GetStats()
        {
            lock (_lock)
            {
                return new BrushPoolStats
                {
                    ActiveBrushes = _activeBrushes.Count,
                    AvailableBrushes = _availableBrushes.Count,
                    PreCalculatedBrushes = ColorSteps,
                    PoolHitRate = _availableBrushes.Count > 0 ? (double)(_availableBrushes.Count + _activeBrushes.Count) / _activeBrushes.Count : 0.0
                };
            }
        }
        
        public void Dispose()
        {
            lock (_lock)
            {
                // Clear pre-calculated brushes
                for (int i = 0; i < _colorGradientBrushes.Length; i++)
                {
                    _colorGradientBrushes[i] = null!;
                }
                
                // Clear active brushes
                _activeBrushes.Clear();
                
                // Clear available brushes
                _availableBrushes.Clear();
            }
        }
    }
    
    /// <summary>
    /// Performance statistics for the brush pool
    /// </summary>
    public sealed class BrushPoolStats
    {
        public int ActiveBrushes { get; set; }
        public int AvailableBrushes { get; set; }
        public int PreCalculatedBrushes { get; set; }
        public double PoolHitRate { get; set; }
        
        public override string ToString()
        {
            return $"Active: {ActiveBrushes}, Available: {AvailableBrushes}, Pre-calculated: {PreCalculatedBrushes}, Hit Rate: {PoolHitRate:P1}";
        }
    }
}