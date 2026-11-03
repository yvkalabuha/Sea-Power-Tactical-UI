using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-310: ContactIconThickness of the game, read without the game's error report and only in a mission
    /// (UI_Parameters._mapValues is filled by LoadTacticalUI). Default 1; retries at most once per RetryS until a value.</summary>
    internal sealed class IconThicknessReader
    {
        public const double Default = 1.0;
        public const double RetryS = 1.0;
        private readonly Func<double?> _read;
        private bool _done;
        private double _nextT = double.NegativeInfinity;

        /// <param name="read">TryMapValue: a value &gt; 0 or null (not loaded yet / no key). Throws MissingMemberException when the member is absent.</param>
        public IconThicknessReader(Func<double?> read) { _read = read; Value = Default; }

        public double Value { get; private set; }
        public bool Unavailable { get; private set; }

        /// <summary>Per frame. Returns true when Value changed.</summary>
        public bool Tick(bool inMission, double realT)
        {
            if (!inMission) { _done = false; _nextT = double.NegativeInfinity; return false; }
            if (_done || Unavailable || realT < _nextT) return false;
            double? v = null;
            try { v = _read(); }
            catch (MissingMemberException) { Unavailable = true; return false; }
            catch (Exception) { v = null; }
            if (!v.HasValue || !(v.Value > 0)) { _nextT = realT + RetryS; return false; }
            _done = true;
            bool changed = Math.Abs(v.Value - Value) > 1e-6;
            Value = v.Value;
            return changed;
        }

        public static string SelfTest()
        {
            int calls = 0; double? ret = null; Exception thr = null;
            var r = new IconThicknessReader(() => { calls++; if (thr != null) throw thr; return ret; });
            r.Tick(false, 0); if (calls != 0) return "read called outside a mission";
            if (r.Tick(true, 10) || calls != 1 || r.Value != Default) return "null must keep Default";
            r.Tick(true, 10.5); if (calls != 1) return "retry earlier than RetryS";
            r.Tick(true, 11.1); if (calls != 2) return "no retry after RetryS";
            ret = 0; r.Tick(true, 12.2); if (r.Value != Default || calls != 3) return "0 must keep Default";
            ret = -2; r.Tick(true, 13.3); if (r.Value != Default) return "negative must keep Default";
            thr = new InvalidOperationException(); r.Tick(true, 14.4); int c = calls; if (r.Unavailable) return "generic exception must not mark Unavailable";
            r.Tick(true, 14.5); if (calls != c) return "generic exception must throttle";
            thr = null; ret = 1.5;
            if (!r.Tick(true, 16) || r.Value != 1.5) return "value 1.5 not applied";
            c = calls; r.Tick(true, 20); if (calls != c) return "read called after success";
            r.Tick(false, 21); if (r.Value != 1.5) return "Value lost on mission exit";
            if (r.Tick(true, 22) || calls != c + 1) return "same value must not report change / must re-read";
            var u = new IconThicknessReader(() => { calls++; throw new MissingMemberException("x", "y"); });
            c = calls; u.Tick(true, 0); u.Tick(true, 5);
            if (!u.Unavailable || calls != c + 1) return "MissingMemberException must set Unavailable once";
            return null;
        }
    }
}
