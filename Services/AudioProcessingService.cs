using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;

namespace RoundSoundMimic.Services
{
    public class AudioProcessingService : IAudioProcessingService
    {
        private const int EqSpikeCount = 48;
        private const int FftSize = 2048;
        private const int FftM = 11;
        private const int HalfFftSize = FftSize / 2;
        
        // Pre-allocated buffers to avoid allocations during processing
        private readonly float[] _fftBuffer = new float[FftSize];
        private readonly Complex[] _fftComplex = new Complex[FftSize];
        private readonly double[] _fftMagnitudes = new double[HalfFftSize];
        private readonly float[] _fftWindow = new float[FftSize];
        private readonly double[] _logFreqMapping = new double[EqSpikeCount]; // Pre-calculated frequency mapping
        private int _fftPos;
        private WasapiLoopbackCapture? _capture;
        private double[] _eqValues = Array.Empty<double>();
        private double[] _eqTargets = Array.Empty<double>();
        private double[] _eqSnapshot = new double[EqSpikeCount];
        private readonly double[] _eqFallRates = new double[EqSpikeCount]; // Track falling speed for gravity effect
        private readonly object _eqLock = new();
        
        public event Action<double[]>? EqValuesUpdated;

        public AudioProcessingService()
        {
            InitializeFftWindow();
            InitializeFrequencyMapping();
            _eqValues = new double[EqSpikeCount];
            lock (_eqLock)
            {
                _eqTargets = new double[EqSpikeCount];
                _eqSnapshot = new double[EqSpikeCount];
            }
        }

        private void InitializeFftWindow()
        {
            for (var i = 0; i < _fftWindow.Length; i++)
            {
                _fftWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
            }
        }

        // Pre-calculate logarithmic frequency mapping to avoid repeated calculations
        private void InitializeFrequencyMapping()
        {
            var maxBin = HalfFftSize - 1;
            for (var band = 0; band < EqSpikeCount; band++)
            {
                _logFreqMapping[band] = Math.Pow(maxBin, band / (double)EqSpikeCount);
            }
        }

        public void StartCapture()
        {
            if (_capture is not null) return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                _capture.DataAvailable += OnAudioDataAvailable;
                _capture.RecordingStopped += OnAudioRecordingStopped;
                _capture.StartRecording();
            }
            catch
            {
                _capture = null;
            }
        }

        public void StopCapture()
        {
            var capture = _capture;
            if (capture is null) return;

            _capture = null;
            capture.DataAvailable -= OnAudioDataAvailable;
            capture.RecordingStopped -= OnAudioRecordingStopped;
            capture.StopRecording();
            capture.Dispose();
        }

        private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_capture is null) return;

            var bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
            if (bytesPerSample <= 0) return;

            var channelCount = _capture.WaveFormat.Channels;
            if (channelCount <= 0) return;

            var sampleCount = e.BytesRecorded / bytesPerSample;
            if (sampleCount <= 0) return;

            if (_capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var waveBuffer = new WaveBuffer(e.Buffer);
                var floatBuffer = waveBuffer.FloatBuffer;
                
                // Process samples in chunks to improve cache locality
                for (var i = 0; i < sampleCount; i += channelCount)
                {
                    var sample = 0f;
                    var endIndex = Math.Min(i + channelCount, sampleCount);
                    
                    for (var channel = 0; channel < channelCount; channel++)
                    {
                        if (i + channel < floatBuffer.Length)
                        {
                            sample += floatBuffer[i + channel];
                        }
                    }
                    AddSample(sample / channelCount);
                }
            }
            else
            {
                // Process samples in chunks to improve cache locality
                for (var i = 0; i < e.BytesRecorded; i += bytesPerSample * channelCount)
                {
                    var sample = 0f;
                    
                    for (var channel = 0; channel < channelCount; channel++)
                    {
                        var offset = i + channel * bytesPerSample;
                        if (offset + 1 < e.Buffer.Length)
                        {
                            sample += BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                        }
                    }
                    AddSample(sample / channelCount);
                }
            }
        }

        private void OnAudioRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (sender is not WasapiLoopbackCapture capture) return;

            capture.DataAvailable -= OnAudioDataAvailable;
            capture.RecordingStopped -= OnAudioRecordingStopped;
            capture.Dispose();
            if (ReferenceEquals(_capture, capture))
            {
                _capture = null;
            }
        }

        private void AddSample(float sample)
        {
            _fftBuffer[_fftPos] = sample;
            _fftPos++;
            if (_fftPos < FftSize)
            {
                return;
            }

            // Apply window function and prepare complex array
            for (var i = 0; i < FftSize; i++)
            {
                _fftComplex[i].X = _fftBuffer[i] * _fftWindow[i];
                _fftComplex[i].Y = 0;
            }

            // Perform FFT
            FastFourierTransform.FFT(true, FftM, _fftComplex);
            
            // Calculate magnitudes (optimized loop)
            var halfSize = HalfFftSize;
            for (var i = 0; i < halfSize; i++)
            {
                var x = _fftComplex[i].X;
                var y = _fftComplex[i].Y;
                _fftMagnitudes[i] = Math.Sqrt(x * x + y * y);
            }

            UpdateEqTargetsFromFft();
            _fftPos = 0;
        }

        private void UpdateEqTargetsFromFft()
        {
            var maxBin = HalfFftSize - 1;
            if (maxBin <= 0) return;

            // Find maximum magnitude efficiently
            var maxMagnitude = 0.0;
            for (var i = 0; i <= maxBin; i++)
            {
                var mag = _fftMagnitudes[i];
                if (mag > maxMagnitude)
                {
                    maxMagnitude = mag;
                }
            }

            if (maxMagnitude <= 1e-8) return;

            lock (_eqLock)
            {
                // Use pre-calculated frequency mapping to avoid repeated Math.Pow calls
                for (var band = 0; band < EqSpikeCount; band++)
                {
                    var start = (int)_logFreqMapping[band];
                    var end = (int)_logFreqMapping[Math.Min(band + 1, EqSpikeCount - 1)];
                    
                    start = Math.Clamp(start, 1, maxBin);
                    end = Math.Clamp(end, start + 1, maxBin);

                    // Sum magnitudes in the frequency range
                    var sum = 0.0;
                    for (var i = start; i < end; i++)
                    {
                        sum += _fftMagnitudes[i];
                    }

                    var avg = sum / (end - start);
                    var normalized = avg / maxMagnitude;
                    var scaled = Math.Sqrt(normalized); // Using sqrt instead of Math.Pow(x, 0.5) for performance
                    _eqTargets[band] = Math.Clamp(scaled, 0, 1);
                }
            }
        }

        public double[] GetCurrentEqValues()
        {
            lock (_eqLock)
            {
                // Return a copy to avoid external modification
                var result = new double[_eqValues.Length];
                Array.Copy(_eqValues, result, _eqValues.Length);
                return result;
            }
        }

        public void UpdateEqAnimation()
        {
            lock (_eqLock)
            {
                if (_eqTargets.Length == EqSpikeCount)
                {
                    Array.Copy(_eqTargets, _eqSnapshot, EqSpikeCount);
                }
            }

            // Enhanced physics-based animation: Fast rise, slow gravity-based fall
            const double riseFactor = 0.6;   // Fast response to peaks
            const double gravity = 0.005;   // Gravity constant for falling
            const double drag = 0.92;       // Air resistance for falling spikes

            for (var i = 0; i < _eqSnapshot.Length; i++)
            {
                var target = _eqSnapshot[i];
                var current = _eqValues[i];

                if (target > current)
                {
                    // Fast rise: linear interpolation towards target
                    _eqValues[i] = current + (target - current) * riseFactor;
                    _eqFallRates[i] = 0; // Reset fall rate when rising
                }
                else
                {
                    // Gravity fall: apply acceleration and velocity
                    _eqFallRates[i] += gravity;
                    _eqFallRates[i] *= drag;
                    _eqValues[i] = Math.Max(0, current - _eqFallRates[i]);
                    
                    // Optional: add a tiny bit of random jitter for "organic" feel
                    if (_eqValues[i] > 0.1)
                    {
                        var jitter = (Random.Shared.NextDouble() - 0.5) * 0.002;
                        _eqValues[i] = Math.Clamp(_eqValues[i] + jitter, 0, 1);
                    }
                }
            }

            EqValuesUpdated?.Invoke(_eqValues);
        }
    }
}