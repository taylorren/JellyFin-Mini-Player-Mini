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
        
        private readonly float[] _fftBuffer = new float[FftSize];
        private readonly Complex[] _fftComplex = new Complex[FftSize];
        private readonly double[] _fftMagnitudes = new double[FftSize / 2];
        private readonly float[] _fftWindow = new float[FftSize];
        private int _fftPos;
        private WasapiLoopbackCapture? _capture;
        private double[] _eqValues = Array.Empty<double>();
        private double[] _eqTargets = Array.Empty<double>();
        private double[] _eqSnapshot = new double[EqSpikeCount];
        private readonly object _eqLock = new();
        
        public event Action<double[]>? EqValuesUpdated;

        public AudioProcessingService()
        {
            InitializeFftWindow();
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
                for (var i = 0; i < sampleCount; i += channelCount)
                {
                    var sample = 0f;
                    for (var channel = 0; channel < channelCount; channel++)
                    {
                        sample += floatBuffer[i + channel];
                    }
                    AddSample(sample / channelCount);
                }
            }
            else
            {
                for (var i = 0; i < e.BytesRecorded; i += bytesPerSample * channelCount)
                {
                    var sample = 0f;
                    for (var channel = 0; channel < channelCount; channel++)
                    {
                        var offset = i + channel * bytesPerSample;
                        sample += BitConverter.ToInt16(e.Buffer, offset) / 32768f;
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

            for (var i = 0; i < FftSize; i++)
            {
                _fftComplex[i].X = _fftBuffer[i] * _fftWindow[i];
                _fftComplex[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FftM, _fftComplex);
            for (var i = 0; i < _fftMagnitudes.Length; i++)
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
            var maxBin = _fftMagnitudes.Length - 1;
            if (maxBin <= 0) return;

            var maxMagnitude = 0.0;
            for (var i = 0; i <= maxBin; i++)
            {
                if (_fftMagnitudes[i] > maxMagnitude)
                {
                    maxMagnitude = _fftMagnitudes[i];
                }
            }

            if (maxMagnitude <= 1e-8) return;

            lock (_eqLock)
            {
                for (var band = 0; band < EqSpikeCount; band++)
                {
                    var start = (int)Math.Floor(Math.Pow(maxBin, band / (double)EqSpikeCount));
                    var end = (int)Math.Floor(Math.Pow(maxBin, (band + 1) / (double)EqSpikeCount));
                    start = Math.Clamp(start, 1, maxBin);
                    end = Math.Clamp(end, start + 1, maxBin);

                    var sum = 0.0;
                    for (var i = start; i < end; i++)
                    {
                        sum += _fftMagnitudes[i];
                    }

                    var avg = sum / (end - start);
                    var normalized = avg / maxMagnitude;
                    var scaled = Math.Pow(normalized, 0.5);
                    _eqTargets[band] = Math.Clamp(scaled, 0, 1);
                }
            }
        }

        public double[] GetCurrentEqValues()
        {
            lock (_eqLock)
            {
                return _eqValues;
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

            for (var i = 0; i < _eqSnapshot.Length; i++)
            {
                var current = _eqValues[i];
                var target = _eqSnapshot[i];
                _eqValues[i] = current + (target - current) * 0.2;
            }

            EqValuesUpdated?.Invoke(_eqValues);
        }
    }
}