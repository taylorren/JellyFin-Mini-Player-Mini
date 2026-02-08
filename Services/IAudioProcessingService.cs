using System;

namespace RoundSoundMimic.Services
{
    public interface IAudioProcessingService
    {
        event Action<double[]> EqValuesUpdated;
        void StartCapture();
        void StopCapture();
        double[] GetCurrentEqValues();
        void UpdateEqAnimation();
    }
}