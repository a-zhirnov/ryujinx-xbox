using System.Threading;

namespace ARMeilleure.Diagnostics
{
    /// <summary>
    /// Cheap progress counters for hosts without a debugger (the Xbox core): they show whether guest threads reach
    /// the JIT and whether translation finishes.
    /// </summary>
    public static class JitDiagnostics
    {
        private static int _executeEntered;
        private static int _executeLoopEntered;
        private static int _translationsStarted;
        private static int _translationsCompleted;
        private static long _lastTranslationAddress;

        public static int ExecuteEntered => Volatile.Read(ref _executeEntered);
        public static int ExecuteLoopEntered => Volatile.Read(ref _executeLoopEntered);
        public static int TranslationsStarted => Volatile.Read(ref _translationsStarted);
        public static int TranslationsCompleted => Volatile.Read(ref _translationsCompleted);
        public static ulong LastTranslationAddress => (ulong)Volatile.Read(ref _lastTranslationAddress);

        internal static void OnExecuteEntered() => Interlocked.Increment(ref _executeEntered);
        internal static void OnExecuteLoopEntered() => Interlocked.Increment(ref _executeLoopEntered);

        internal static void OnTranslationStarted(ulong address)
        {
            Interlocked.Exchange(ref _lastTranslationAddress, (long)address);
            Interlocked.Increment(ref _translationsStarted);
        }

        internal static void OnTranslationCompleted() => Interlocked.Increment(ref _translationsCompleted);
    }
}
