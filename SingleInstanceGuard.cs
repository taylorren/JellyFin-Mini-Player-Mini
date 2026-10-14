using System;
using System.Runtime.InteropServices;

namespace RoundSoundMimic
{
    /// <summary>
    /// Ensures only a single instance of the application runs at a time by
    /// holding a named OS mutex for the lifetime of the process. The mutex is
    /// automatically released by the OS if the process exits (even on a crash),
    /// so a stale lock can never block future launches.
    /// </summary>
    public static class SingleInstanceGuard
    {
        private static readonly string InstanceMutexName = "RoundSoundMimic.SingleInstance";

        private static IntPtr _mutexHandle;
        private static bool _acquired;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attributes, bool initialOwner, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>True once this process has claimed the single-instance mutex.</summary>
        public static bool Acquired => _acquired;

        /// <summary>
        /// Attempts to become the single running instance.
        /// </summary>
        /// <returns>true if this process is allowed to run, false if another instance is already active.</returns>
        public static bool Acquire()
        {
            if (_acquired)
            {
                return true;
            }

            // Hold the handle in a static field so it stays alive (and keeps the
            // mutex locked) for the entire lifetime of this process.
            _mutexHandle = CreateMutexW(IntPtr.Zero, true, InstanceMutexName);
            if (_mutexHandle == IntPtr.Zero)
            {
                StartupLogger.Log("SingleInstanceGuard: failed to create mutex, treating as already running.");
                return false;
            }

            // 183 = ERROR_ALREADY_EXISTS. Marshal.GetLastWin32Error() reliably
            // reads the error captured by the P/Invoke call above.
            if (Marshal.GetLastWin32Error() == 183)
            {
                CloseHandle(_mutexHandle);
                _mutexHandle = IntPtr.Zero;
                StartupLogger.Log("SingleInstanceGuard: another instance is already running.");
                return false;
            }

            _acquired = true;
            return true;
        }
    }
}
