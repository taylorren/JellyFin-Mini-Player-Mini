using System;

namespace RoundSoundMimic
{
    public static class StartupLogger
    {
        private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoundSoundMimic_startup_debug.log");
        public static void Log(string text)
        {
            try
            {
                System.IO.File.AppendAllText(Path, DateTime.UtcNow.ToString("o") + " " + text + "\n");
            }
            catch { }
        }
    }
}
