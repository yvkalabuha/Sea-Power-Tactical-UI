using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-100: fake <see cref="IAirEvasionGame"/>. Every call lands in <see cref="Calls"/>; the interface has no other order,
    /// so "only state, speed, altitude, waypoint, chaff" is checked on the whole journal.</summary>
    internal sealed class AirEvasionFakeGame : IAirEvasionGame
    {
        public const int Uid = 7;

        public AeUnit U = new AeUnit
        {
            Uid = Uid, Alive = true, IsAircraft = true, InFlight = true, Lat = 0, Lon = 0, HeadingDeg = 0, Kn = 300,
            Telegraph = 2, MaxTelegraph = 4, Roe = "weapons_free", StateName = "default", ChaffReady = true,
        };
        public AeMissile M;
        public AeThreat Threat;
        public List<int> Auto = new List<int>();
        public bool NativeOkValue = true, ForceNativeResult = true, ForceSetsState = true, FinishNativeResult = true, RouteChanged;
        public bool? OptionOn;
        public List<double[]> SupersedeRoute;
        public List<double[]> RememberOverride;
        public AeSaved SavedGiven, RestoredWith;
        public string RestoreResult;
        public readonly List<string> Calls = new List<string>(), Logs = new List<string>(), Events = new List<string>(), Hints = new List<string>();

        public AirEvasionFakeGame() { MissileAt(0, 20, 600); }

        /// <summary>A missile <paramref name="nm"/> miles away on the given bearing from the unit (unit at lat 0), flying at the unit.</summary>
        public AeMissile MissileAt(double bearingDeg, double nm, double kn, string key = "m1")
        {
            double r = bearingDeg * Math.PI / 180.0;
            M = new AeMissile { Key = key, Lat = nm * Math.Cos(r) / 60.0, Lon = nm * Math.Sin(r) / 60.0, Kn = kn, CourseDeg = (bearingDeg + 180) % 360, TargetsUnit = true };
            return M;
        }

        public int Count(string prefix) { int n = 0; foreach (string c in Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

        public bool Logged(string part) { foreach (string l in Logs) if (l.Contains(part)) return true; return false; }

        public int HintCount(string key) { int n = 0; foreach (string h in Hints) if (h == key) n++; return n; }

        public AeUnit Own(int uid) { return uid == Uid ? U : null; }
        public AeMissile Missile(int uid) { return uid == Uid ? M : null; }
        public AeThreat NearestHostileAir(int uid, double maxNm) { return uid == Uid ? Threat : null; }
        public IList<int> AutoUnits() { return Auto; }
        public bool NativeOk { get { return NativeOkValue; } }

        public bool ForceNative(int uid)
        {
            Calls.Add("ForceNative");
            if (ForceNativeResult && ForceSetsState) U.StateName = "AdvancedMissileEvasion";
            return ForceNativeResult;
        }

        public bool FinishNative(int uid) { Calls.Add("FinishNative"); return FinishNativeResult; }
        public bool SetTelegraph(int uid, int step) { Calls.Add("SetTelegraph:" + step); return true; }
        public bool SetAltPreset(int uid, int preset) { Calls.Add("SetAltPreset:" + preset); return true; }

        public double[] PlaceCourse(int uid, double courseDeg, double legNm)
        {
            Calls.Add("PlaceCourse:" + Math.Round(courseDeg));
            return new[] { courseDeg, legNm };
        }

        public bool LaunchChaff(int uid) { Calls.Add("LaunchChaff"); return true; }

        public AeSaved Remember(int uid, List<double[]> routeOverride)
        {
            Calls.Add("Remember");
            RememberOverride = routeOverride;
            SavedGiven = new AeSaved { Telegraph = U.Telegraph, AltUnits = 5 };
            if (routeOverride != null) SavedGiven.Route = routeOverride;
            return SavedGiven;
        }

        public string Restore(int uid, AeSaved s) { Calls.Add("Restore"); RestoredWith = s; return RestoreResult; }
        public bool RouteChangedByOthers(int uid, double[] placed) { return RouteChanged; }

        public List<double[]> PostLaunchSupersede(int uid, bool restoreRoute)
        {
            Calls.Add("Supersede:" + (restoreRoute ? "true" : "false"));
            return SupersedeRoute;
        }

        public bool? OptionEvasionOn { get { return OptionOn; } }
        public void Hint(string key) { Hints.Add(key); }
        public void Log(string line) { Logs.Add(line); }
        public void Event(string name, string dataJson) { Events.Add(name + " " + dataJson); }
    }
}
