using System;
using BepInEx.Logging;

namespace VoiceBotBridge.Dev
{
    /// <summary>Feeds the ring: the UI mod source, this mod, and Error/Fatal of any source.</summary>
    internal sealed class DevLogListener : ILogListener
    {
        private const string UiSource = "Sea Power Tactical UI";
        private readonly DevLogRing _ring;

        public DevLogListener(DevLogRing ring) { _ring = ring; }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            string src = eventArgs.Source == null ? "?" : eventArgs.Source.SourceName;
            bool bad = (eventArgs.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
            if (!bad && src != UiSource && src != DevExecutor.LogSource) return;
            _ring.Add(eventArgs.Level.ToString(), src, Convert.ToString(eventArgs.Data), DateTime.UtcNow);
        }

        public void Dispose() { }
    }
}
