using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace RoundSoundMimic
{
    /// <summary>
    /// High-performance frame rate limiter and performance monitor for WPF applications
    /// Provides smooth, consistent frame rates while monitoring system performance
    /// </summary>
    public sealed class PerformanceMonitor : IDisposable
    {
        private readonly DispatcherTimer _renderTimer;
        private readonly Queue<long> _frameTimeQueue;
        private readonly Stopwatch _frameStopwatch;
        private readonly Stopwatch _totalStopwatch;
        
        // Performance metrics
        private int _frameCount;
        private long _totalFrameTime;
        private long _lastFrameTime;
        private double _targetFps;
        private double _currentFps;
        private double _averageFrameTime;
        
        // Performance thresholds
        private const int MaxFrameHistory = 120; // Keep 2 seconds of frame history at 60 FPS
        private const long TargetFrameTimeNs = 16_666_666; // 60 FPS in nanoseconds
        
        public event Action<PerformanceStats>? PerformanceUpdated;
        
        public bool IsRunning { get; private set; }
        public double TargetFps 
        { 
            get => _targetFps;
            set
            {
                if (Math.Abs(_targetFps - value) > 0.1)
                {
                    _targetFps = Math.Clamp(value, 15.0, 120.0);
                    UpdateTimerInterval();
                }
            }
        }
        
        public PerformanceMonitor()
        {
            _frameTimeQueue = new Queue<long>(MaxFrameHistory);
            _frameStopwatch = new Stopwatch();
            _totalStopwatch = new Stopwatch();
            _renderTimer = new DispatcherTimer();
            
            TargetFps = 60.0; // Default to 60 FPS
        }
        
        /// <summary>
        /// Starts performance monitoring with specified target FPS
        /// </summary>
        public void Start(double targetFps = 60.0)
        {
            if (IsRunning) return;
            
            TargetFps = targetFps;
            
            // Reset metrics
            _frameCount = 0;
            _totalFrameTime = 0;
            _currentFps = 0;
            _averageFrameTime = 0;
            _frameTimeQueue.Clear();
            
            _totalStopwatch.Restart();
            _frameStopwatch.Restart();
            
            UpdateTimerInterval();
            
            _renderTimer.Tick += OnRenderFrame;
            _renderTimer.Start();
            
            IsRunning = true;
        }
        
        /// <summary>
        /// Stops performance monitoring
        /// </summary>
        public void Stop()
        {
            if (!IsRunning) return;
            
            _renderTimer.Stop();
            _renderTimer.Tick -= OnRenderFrame;
            
            IsRunning = false;
        }
        
        /// <summary>
        /// Forces immediate frame update (bypasses frame rate limiting)
        /// Use for critical updates that must happen immediately
        /// </summary>
        public void ForceUpdate()
        {
            if (!IsRunning) return;
            OnRenderFrame(this, EventArgs.Empty);
        }
        
        /// <summary>
        /// Gets current performance statistics
        /// </summary>
        public PerformanceStats GetCurrentStats()
        {
            return new PerformanceStats
            {
                CurrentFps = _currentFps,
                TargetFps = _targetFps,
                AverageFrameTime = _averageFrameTime,
                FrameCount = _frameCount,
                TotalRunTime = _totalStopwatch.Elapsed,
                CpuUsage = EstimateCpuUsage(),
                MemoryUsage = GC.GetTotalMemory(false)
            };
        }
        
        private void UpdateTimerInterval()
        {
            var frameIntervalMs = 1000.0 / _targetFps;
            _renderTimer.Interval = TimeSpan.FromMilliseconds(frameIntervalMs);
        }
        
        private void OnRenderFrame(object? sender, EventArgs e)
        {
            var now = _frameStopwatch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency;
            var frameDelta = now - _lastFrameTime;
            _lastFrameTime = now;
            
            // Enforce minimum frame time to prevent runaway
            if (frameDelta < TargetFrameTimeNs / 2)
            {
                return; // Skip frame to maintain target FPS
            }
            
            // Record frame time
            _frameStopwatch.Restart();
            _frameCount++;
            
            // Update frame time history
            _frameTimeQueue.Enqueue(frameDelta);
            if (_frameTimeQueue.Count > MaxFrameHistory)
            {
                _frameTimeQueue.Dequeue();
            }
            
            // Calculate performance metrics
            UpdatePerformanceMetrics(frameDelta);
            
            // Raise performance update event
            PerformanceUpdated?.Invoke(GetCurrentStats());
        }
        
        private void UpdatePerformanceMetrics(long frameDelta)
        {
            // Calculate average frame time from history
            if (_frameTimeQueue.Count > 0)
            {
                var sum = 0L;
                foreach (var time in _frameTimeQueue)
                {
                    sum += time;
                }
                _averageFrameTime = (sum / _frameTimeQueue.Count) / 1_000_000.0; // Convert to milliseconds
            }
            
            // Calculate current FPS based on recent frames
            var recentFrames = Math.Min(_frameTimeQueue.Count, 60); // Last 60 frames max
            if (recentFrames >= 2)
            {
                var recentTotalTime = 0L;
                var recentQueue = _frameTimeQueue.ToArray();
                for (int i = recentQueue.Length - recentFrames; i < recentQueue.Length; i++)
                {
                    recentTotalTime += recentQueue[i];
                }
                _currentFps = (recentFrames - 1) * 1_000_000_000.0 / recentTotalTime;
            }
            
            _totalFrameTime += frameDelta;
        }
        
        private double EstimateCpuUsage()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.TotalProcessorTime.TotalMilliseconds / process.TotalProcessorTime.TotalMilliseconds * 100.0;
            }
            catch
            {
                return 0.0;
            }
        }
        
        public void Dispose()
        {
            Stop();
            _frameTimeQueue.Clear();
        }
    }
    
    /// <summary>
    /// Performance statistics snapshot
    /// </summary>
    public sealed class PerformanceStats
    {
        public double CurrentFps { get; set; }
        public double TargetFps { get; set; }
        public double AverageFrameTime { get; set; } // In milliseconds
        public int FrameCount { get; set; }
        public TimeSpan TotalRunTime { get; set; }
        public double CpuUsage { get; set; } // In percent
        public long MemoryUsage { get; set; } // In bytes
        
        public override string ToString()
        {
            return $"FPS: {CurrentFps:F1}/{TargetFps:F1} | Frame: {AverageFrameTime:F1}ms | CPU: {CpuUsage:F1}% | Memory: {MemoryUsage / 1024 / 1024:F1}MB";
        }
        
        /// <summary>
        /// Returns performance efficiency rating (0.0 to 1.0)
        /// </summary>
        public double GetEfficiencyRating()
        {
            var fpsEfficiency = Math.Min(CurrentFps / TargetFps, 1.0);
            var frameTimeEfficiency = TargetFps > 0 ? Math.Min(1000.0 / (TargetFps * AverageFrameTime), 1.0) : 1.0;
            return (fpsEfficiency + frameTimeEfficiency) / 2.0;
        }
    }
}