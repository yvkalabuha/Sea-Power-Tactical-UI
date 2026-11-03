using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-101: scripted game for the controller stand. One submarine (uid 1); every order is recorded in Calls.</summary>
    internal sealed class SubEvasionFakeGame : ISubEvasionGame
    {
        public bool Flag = true, NativeOkV = true, OwnOkV = true, StartNativeResult = true, NativeHolds = true, FinishNativeResult = true, LaunchOk = true, UnitGone;
        public SubUnit U = new SubUnit { Uid = 1, Alive = true, StateName = "Default", StatePriority = 5, HeadingDeg = 0, Kn = 6, DepthFt = 200, Preset = 3, Telegraph = 2, CavForbidden = true, NoisemakersLeft = 4 };
        public SubThreat T = new SubThreat { TrackId = 7, BrgDeg = 30, DistNm = 3 };
        public SubSnapshot Snap = new SubSnapshot { Telegraph = 2, Preset = 3, DepthFt = 200, CavForbidden = true, PlayerOverride = false };
        public SubSnapshot LastRestored;
        public readonly List<string> Calls = new List<string>();
        public readonly List<string> Logs = new List<string>();
        public readonly List<string> Events = new List<string>();
        public double LastCourse = double.NaN;

        public bool AutoEvadeOn(int uid) { return Flag; }

        public SubUnit Sub(int uid) { return UnitGone || uid != 1 ? null : U; }

        public SubThreat Threat(int uid) { return T; }

        public bool NativeOk { get { return NativeOkV; } }

        public bool OwnOk { get { return OwnOkV; } }

        public bool StartNative(int uid)
        {
            Calls.Add("StartNative");
            if (!StartNativeResult) return false;
            if (NativeHolds) U.StateName = "TorpedoEvasion";
            else if (U.StateName == "PlayerOverride") U.StateName = "PlayerOverride";
            return true;
        }

        public bool FinishNative(int uid)
        {
            Calls.Add("FinishNative");
            if (FinishNativeResult) U.StateName = "Default";
            return FinishNativeResult;
        }

        public SubSnapshot Remember(int uid) { Calls.Add("Remember"); return Snap; }

        public string Restore(int uid, SubSnapshot s, bool full)
        {
            Calls.Add("Restore full=" + (full ? "true" : "false"));
            LastRestored = s;
            return full ? "route=kept tg=5->2 depth=5->3" : "partial";
        }

        public bool PlaceCourse(int uid, double courseDeg) { Calls.Add("PlaceCourse"); LastCourse = courseDeg; return true; }

        public bool SetTelegraph(int uid, int step) { Calls.Add("SetTelegraph " + step); return true; }

        public bool SetPreset(int uid, int preset) { Calls.Add("SetPreset " + preset); return true; }

        public bool SetCavitationForbidden(int uid, bool on) { Calls.Add("SetCavitationForbidden " + (on ? "true" : "false")); return true; }

        public bool LaunchNoisemaker(int uid)
        {
            Calls.Add("LaunchNoisemaker");
            if (LaunchOk && U.NoisemakersLeft > 0) U.NoisemakersLeft--;
            return LaunchOk;
        }

        public void SetUnitAutoEvade(int uid) { Calls.Add("SetUnitAutoEvade"); }

        public void TimeToNormal() { Calls.Add("TimeToNormal"); }

        public void Log(string line) { Logs.Add(line); }

        public void Event(string name, string dataJson) { Events.Add(name + " " + dataJson); }

        public int Count(string call) { int n = 0; foreach (var c in Calls) if (c == call) n++; return n; }

        public int CountPrefix(string prefix) { int n = 0; foreach (var c in Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

        public int LogCount(string part) { int n = 0; foreach (var l in Logs) if (l.Contains(part)) n++; return n; }
    }
}
