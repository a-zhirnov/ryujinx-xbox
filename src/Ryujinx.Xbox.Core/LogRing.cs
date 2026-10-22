using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Ryujinx.Xbox.Core
{
    /// <summary>
    /// Keeps the newest log lines in memory so the shell can show them without file access,
    /// and counts guest markers ("XBC_" lines written by the test homebrew through svcOutputDebugString).
    /// </summary>
    internal sealed class LogRing : ILogTarget
    {
        private const string GuestMarkerPrefix = "XBC_";

        private readonly Queue<string> _lines = new();
        private readonly int _capacity;
        private readonly Lock _lock = new();
        private int _guestMarkers;

        public string Name => "xbc-ring";

        public int GuestMarkers => Volatile.Read(ref _guestMarkers);

        public LogRing(int capacity)
        {
            _capacity = capacity;
        }

        public void Log(object sender, LogEventArgs args)
        {
            if (args.Message != null && args.Message.Contains(GuestMarkerPrefix))
            {
                Interlocked.Increment(ref _guestMarkers);
            }

            Add($"{args.Time:hh\\:mm\\:ss\\.fff} |{args.Level}| {args.Message}");
        }

        public void Add(string line)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > _capacity)
                {
                    _lines.Dequeue();
                }
            }
        }

        public string Snapshot()
        {
            StringBuilder builder = new();
            lock (_lock)
            {
                foreach (string line in _lines)
                {
                    builder.AppendLine(line);
                }
            }

            return builder.ToString();
        }

        public void Dispose() { }
    }
}
