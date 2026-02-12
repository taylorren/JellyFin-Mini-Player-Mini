using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace RoundSoundMimic.Services
{
    /// <summary>
    /// Performance-optimized audio processing service
    /// Focuses on eliminating allocations and reducing CPU overhead
    /// </summary>
    public class OptimizedAudioProcessingService : IAudioProcessingService
    {
        private const int EqSpikeCount = 48;
        private const int FftSize = 2048;
        private const int FftM = 11;
        private const int HalfFftSize = FftSize / 2;
        
        // Pre-allocated buffers to avoid allocations during processing
        private readonly float[] _fftBuffer;
        private readonly Complex[] _fftComplex;
        private readonly double[] _fftMagnitudes;
        private readonly float[] _fftWindow;
        private readonly double[] _logFreqMapping;
        
        // Lock-free structures to reduce thread contention
        private readonly AudioRingBuffer _audioRingBuffer;
        private readonly EqBuffer _eqValues;
        private readonly EqBuffer _eqTargets;
        private readonly EqBuffer _eqSnapshot;
        private readonly double[] _fallRates;
        
        private int _fftPos;
        private WasapiLoopbackCapture? _capture;
        private bool _isProcessing;
        
        public event Action<double[]>? EqValuesUpdated;

        public OptimizedAudioProcessingService()
        {
            _fftBuffer = new float[FftSize];
            _fftComplex = new Complex[FftSize];
            _fftMagnitudes = new double[HalfFftSize];
            _fftWindow = new float[FftSize];
            _logFreqMapping = new double[EqSpikeCount];
            
            // Initialize lock-free structures
            _audioRingBuffer = new AudioRingBuffer(FftSize * 4);
            _eqValues = new EqBuffer(EqSpikeCount);
            _eqTargets = new EqBuffer(EqSpikeCount);
            _eqSnapshot = new EqBuffer(EqSpikeCount);
            _fallRates = new double[EqSpikeCount];
            
            InitializeFftWindow();
            InitializeFrequencyMapping();
        }

        private void InitializeFftWindow()
        {
            // Pre-calculate Hann window function
            for (var i = 0; i < FftSize; i++)
            {
                _fftWindow[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
            }
        }

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
            if (_capture is null || _isProcessing) return;

            var bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
            if (bytesPerSample <= 0) return;

            var channelCount = _capture.WaveFormat.Channels;
            if (channelCount <= 0) return;

            // Optimized sample processing with minimal allocations
            ProcessAudioSamplesOptimized(e.Buffer, e.BytesRecorded, bytesPerSample, channelCount);
        }

        /// <summary>
        /// Optimized audio sample processing with reduced allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessAudioSamplesOptimized(byte[] buffer, int bytesRecorded, int bytesPerSample, int channelCount)
        {
            var sampleCount = bytesRecorded / (bytesPerSample * channelCount);
            
            if (_capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                ProcessFloatSamplesOptimized(buffer, sampleCount, channelCount);
            }
            else
            {
                ProcessInt16SamplesOptimized(buffer, sampleCount, bytesPerSample, channelCount);
            }
        }

        /// <summary>
        /// Optimized float sample processing with minimal allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessFloatSamplesOptimized(byte[] buffer, int sampleCount, int channelCount)
        {
            var waveBuffer = new WaveBuffer(buffer);
            var floatBuffer = waveBuffer.FloatBuffer;
            
            // Process samples in chunks to reduce overhead
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = 0f;
                var endIndex = Math.Min(i * channelCount + channelCount, floatBuffer.Length);
                
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var index = i * channelCount + channel;
                    if (index < endIndex)
                    {
                        sample += floatBuffer[index];
                    }
                }
                
                _audioRingBuffer.WriteSample(sample / channelCount);
            }
        }

        /// <summary>
        /// Optimized int16 sample processing with minimal allocations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessInt16SamplesOptimized(byte[] buffer, int sampleCount, int bytesPerSample, int channelCount)
        {
            // Process samples in chunks to reduce overhead
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = 0f;
                
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var offset = i * channelCount * bytesPerSample + channel * bytesPerSample;
                    if (offset + 1 < buffer.Length)
                    {
                        sample += BitConverter.ToInt16(buffer, offset) / 32768f;
                    }
                }
                
                _audioRingBuffer.WriteSample(sample / channelCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddSampleToProcessing(float sample)
        {
            // Optimized sample addition with minimal overhead
            _audioRingBuffer.WriteSample(sample);
            _fftPos++;
            if (_fftPos < FftSize) return;

            // Prepare FFT data
            for (var i = 0; i < FftSize; i++)
            {
                _fftComplex[i].X = _fftBuffer[i] * _fftWindow[i];
                _fftComplex[i].Y = 0;
            }

            // Perform FFT
            FastFourierTransform.FFT(true, FftM, _fftComplex);
            
            // Calculate magnitudes with optimized loop
            CalculateMagnitudesOptimized();
            
            UpdateEqTargetsFromFftOptimized();
            _fftPos = 0;
        }

        /// <summary>
        /// Optimized magnitude calculation with reduced branches
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CalculateMagnitudesOptimized()
        {
            // Optimized magnitude calculation
            for (var i = 0; i < HalfFftSize; i++)
            {
                var x = _fftComplex[i].X;
                var y = _fftComplex[i].Y;
                _fftMagnitudes[i] = Math.Sqrt(x * x + y * y);
            }
        }

        /// <summary>
        /// Optimized frequency binning with reduced allocations
        /// </summary>
        private void UpdateEqTargetsFromFftOptimized()
        {
            var maxBin = HalfFftSize - 1;
            if (maxBin <= 0) return;

            // Find maximum magnitude efficiently
            var maxMagnitude = 0.0;
            for (var i = 0; i <= maxBin; i++)
            {
                var mag = _fftMagnitudes[i];
                if (mag > maxMagnitude)
                    maxMagnitude = mag;
            }

            if (maxMagnitude <= 1e-8) return;

            // Lock-free update of targets using pre-allocated arrays
            var targets = _eqTargets.GetBuffer();
            
            // Use pre-calculated mapping for efficiency
            for (var band = 0; band < EqSpikeCount; band++)
            {
                var start = (int)_logFreqMapping[band];
                var end = (int)_logFreqMapping[Math.Min(band + 1, EqSpikeCount - 1)];
                
                start = Math.Clamp(start, 1, maxBin);
                end = Math.Clamp(end, start + 1, maxBin);

                // Sum magnitudes in range with minimal overhead
                var sum = 0.0;
                for (var i = start; i < end; i++)
                {
                    sum += _fftMagnitudes[i];
                }

                var avg = sum / (end - start);
                var normalized = avg / maxMagnitude;
                var scaled = Math.Sqrt(normalized);
                targets[band] = Math.Clamp(scaled, 0, 1);
            }

            _eqTargets.Swap();
        }

        public double[] GetCurrentEqValues()
        {
            return _eqValues.GetBuffer();
        }

        /// <summary>
        /// Optimized EQ animation with physics-based smoothing
        /// </summary>
        public void UpdateEqAnimation()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                var targets = _eqTargets.GetBuffer();
                var current = _eqValues.GetBuffer();
                var snapshot = _eqSnapshot.GetBuffer();

                // Copy targets to snapshot for batch processing
                Array.Copy(targets, snapshot, EqSpikeCount);

                // Enhanced physics-based animation with optimized loops
                const double riseFactor = 0.6;
                const double gravity = 0.005;
                const double drag = 0.92;

                for (var i = 0; i < EqSpikeCount; i++)
                {
                    var target = snapshot[i];
                    var currentVal = current[i];

                    if (target > currentVal)
                    {
                        // Fast rise
                        current[i] = currentVal + (target - currentVal) * riseFactor;
                        _fallRates[i] = 0;
                    }
                    else
                    {
                        // Gravity fall with optimized calculation
                        _fallRates[i] = _fallRates[i] * drag + gravity;
                        current[i] = Math.Max(0, currentVal - _fallRates[i]);
                    }

                    // Add jitter for organic feel
                    if (current[i] > 0.1)
                    {
                        var jitter = (Random.Shared.NextDouble() - 0.5) * 0.002;
                        current[i] = Math.Clamp(current[i] + jitter, 0, 1);
                    }
                }

                _eqValues.Swap();
                _eqSnapshot.Swap();
                EqValuesUpdated?.Invoke(_eqValues.GetBuffer());
            }
            finally
            {
                _isProcessing = false;
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
    }

    /// <summary>
    /// Lock-free ring buffer for audio samples
    /// </summary>
    public sealed class AudioRingBuffer
    {
        private readonly float[] _buffer;
        private volatile int _writePos;
        private volatile int _readPos;
        private readonly int _mask;

        public AudioRingBuffer(int size)
        {
            // Ensure power-of-2 size for efficient modulo
            var powerOf2 = 1;
            while (powerOf2 < size) powerOf2 <<= 1;

            _buffer = new float[powerOf2];
            _mask = powerOf2 - 1;
            _writePos = 0;
            _readPos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSample(float sample)
        {
            var pos = _writePos;
            _buffer[pos & _mask] = sample;
            _writePos = (pos + 1) & _mask;
        }

        public bool TryReadSample(out float sample)
        {
            var readPos = _readPos;
            if (readPos == _writePos)
            {
                sample = default;
                return false;
            }

            sample = _buffer[readPos & _mask];
            _readPos = (readPos + 1) & _mask;
            return true;
        }
    }

    /// <summary>
    /// Double-buffered array for lock-free EQ data operations
    /// </summary>
    public sealed class EqBuffer
    {
        private readonly double[] _frontBuffer;
        private readonly double[] _backBuffer;
        private volatile bool _frontActive;

        public EqBuffer(int size)
        {
            _frontBuffer = new double[size];
            _backBuffer = new double[size];
            _frontActive = true;
        }

        public double[] GetBuffer()
        {
            return _frontActive ? _frontBuffer : _backBuffer;
        }

        public void Swap()
        {
            _frontActive = !_frontActive;
        }
    }
}