using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>What the unit had before the first XO command changed it, and what the mod set (T-091, "Release control").</summary>
    internal sealed class ControlEntry
    {
        public bool HasRoe, HasRoute;
        public string SavedRoe, RoeSetByMod;                 // tight|hold|free
        public List<double[]> SavedRoute = new List<double[]>();
        public double? SavedCourseDeg;
        public double[] PlacedByMod;                         // {lat, lon} of the mod's last waypoint
        /// <summary>Other state the mod's commands change: telegraph, emcon, air_radar, surface_radar (values as text).</summary>
        public Dictionary<string, FieldMem> Fields = new Dictionary<string, FieldMem>();
    }

    internal sealed class FieldMem { public string Saved, SetByMod; }

    /// <summary>
    /// Per-unit memory of the ROE and route before the mod's first command (kept by reference). Pure C#: no game types,
    /// so SelfTest runs in the offline selfcheck. Main thread only.
    /// </summary>
    internal sealed partial class ControlMemory
    {
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        private readonly Dictionary<object, ControlEntry> _map = new Dictionary<object, ControlEntry>(new RefComparer());

        public int Count { get { return _map.Count; } }

        private ControlEntry Entry(object u)
        {
            ControlEntry e;
            if (!_map.TryGetValue(u, out e)) { e = new ControlEntry(); _map[u] = e; }
            return e;
        }

        /// <summary>Remember the ROE before the first mod change; later calls keep the first value.</summary>
        public void RememberRoe(object u, string current)
        {
            if (u == null || string.IsNullOrEmpty(current)) return;
            var e = Entry(u);
            if (e.HasRoe) return;
            e.HasRoe = true; e.SavedRoe = current;
        }

        public void NoteRoeSet(object u, string status)
        {
            if (u == null || string.IsNullOrEmpty(status)) return;
            ControlEntry e;
            if (_map.TryGetValue(u, out e) && e.HasRoe) e.RoeSetByMod = status;
        }

        /// <summary>Remember the waypoints and the demanded course before the first mod route; later calls keep the first.</summary>
        public void RememberRoute(object u, List<double[]> wps, double? courseDeg)
        {
            if (u == null) return;
            var e = Entry(u);
            if (e.HasRoute) return;
            e.HasRoute = true;
            e.SavedRoute = wps == null ? new List<double[]>() : new List<double[]>(wps);
            e.SavedCourseDeg = courseDeg;
        }

        public void NoteRoutePlaced(object u, double lat, double lon)
        {
            ControlEntry e;
            if (u != null && _map.TryGetValue(u, out e) && e.HasRoute) e.PlacedByMod = new[] { lat, lon };
        }

        /// <summary>Remember a field (telegraph, emcon, air_radar, surface_radar) before the first mod change; later calls keep the first.</summary>
        public void RememberField(object u, string key, string current)
        {
            if (u == null || key == null || current == null) return;
            var e = Entry(u);
            if (!e.Fields.ContainsKey(key)) e.Fields[key] = new FieldMem { Saved = current };
        }

        public void NoteFieldSet(object u, string key, string value)
        {
            ControlEntry e; FieldMem f;
            if (u != null && key != null && value != null && _map.TryGetValue(u, out e) && e.Fields.TryGetValue(key, out f)) f.SetByMod = value;
        }

        /// <summary>restore | nothing_saved | player_changed (current differs from what the mod set).</summary>
        public static string DecideField(ControlEntry e, string key, string current)
        {
            FieldMem f;
            if (e == null || key == null || !e.Fields.TryGetValue(key, out f) || f.SetByMod == null) return "nothing_saved";
            return string.Equals(current, f.SetByMod, StringComparison.OrdinalIgnoreCase) ? "restore" : "player_changed";
        }

        public ControlEntry Peek(object u)
        {
            ControlEntry e;
            return u != null && _map.TryGetValue(u, out e) ? e : null;
        }

        public ControlEntry Take(object u)
        {
            ControlEntry e;
            if (u == null || !_map.TryGetValue(u, out e)) return null;
            _map.Remove(u);
            return e;
        }

        public void Clear() { _map.Clear(); }

        /// <summary>restore | nothing_saved | player_changed | attack_disabled.</summary>
        public static string DecideRoe(ControlEntry e, string currentRoe, bool allowAttack)
        {
            if (e == null || !e.HasRoe || e.RoeSetByMod == null) return "nothing_saved";
            if (!string.Equals(currentRoe, e.RoeSetByMod, StringComparison.OrdinalIgnoreCase)) return "player_changed";
            if (e.SavedRoe == "free" && !allowAttack) return "attack_disabled";
            return "restore";
        }

        /// <summary>restore | nothing_saved | player_changed. No waypoints left (reached or cleared by the game) counts as "not touched by the player".</summary>
        public static string DecideRoute(ControlEntry e, double[] currentFirstWp, Func<double[], double[], double> distNm)
        {
            if (e == null || !e.HasRoute || e.PlacedByMod == null) return "nothing_saved";
            if (currentFirstWp == null) return "restore";
            return distNm(currentFirstWp, e.PlacedByMod) <= 0.2 ? "restore" : "player_changed";
        }

        /// <summary>null = ok, otherwise the first failed check.</summary>
        public static string SelfTest()
        {
            Func<double[], double[], double> dist = (a, b) => Math.Abs(a[0] - b[0]) * 60.0; // 1 deg lat = 60 nm
            var m = new ControlMemory();
            var u1 = new string('a', 3); var u2 = new string('a', 3); // equal by Equals, different references
            if (u1.Equals(u2) == false || ReferenceEquals(u1, u2)) return "test objects are not equal-but-distinct";

            m.RememberRoe(u1, "tight"); m.RememberRoe(u1, "hold");
            if (m.Peek(u1).SavedRoe != "tight") return "RememberRoe keeps the first value";
            m.NoteRoeSet(u1, "hold"); m.NoteRoeSet(u1, "tight"); m.NoteRoeSet(u1, "hold");
            if (m.Peek(u1).RoeSetByMod != "hold") return "NoteRoeSet overwrites";
            if (m.Peek(u2) != null) return "keys are by reference";
            m.NoteRoeSet(u2, "hold");
            if (m.Peek(u2) != null) return "NoteRoeSet without a remembered ROE creates nothing";

            if (DecideRoe(null, "hold", true) != "nothing_saved") return "roe: no entry";
            if (DecideRoe(new ControlEntry(), "hold", true) != "nothing_saved") return "roe: empty entry";
            if (DecideRoe(m.Peek(u1), "tight", true) != "player_changed") return "roe: player changed";
            if (DecideRoe(m.Peek(u1), "HOLD", true) != "restore") return "roe: restore (case-insensitive)";
            var free = new ControlEntry { HasRoe = true, SavedRoe = "free", RoeSetByMod = "hold" };
            if (DecideRoe(free, "hold", false) != "attack_disabled") return "roe: free with AllowAttack=false";
            if (DecideRoe(free, "hold", true) != "restore") return "roe: free with AllowAttack=true";
            var unset = new ControlEntry { HasRoe = true, SavedRoe = "tight" };
            if (DecideRoe(unset, "tight", true) != "nothing_saved") return "roe: step never succeeded";

            var wps = new List<double[]> { new[] { 10.0, 20.0 }, new[] { 11.0, 21.0 } };
            m.RememberRoute(u1, wps, 90.0); m.RememberRoute(u1, new List<double[]>(), 10.0);
            wps.Add(new[] { 99.0, 99.0 }); // later changes of the caller's list do not leak in
            var e = m.Peek(u1);
            if (e.SavedRoute.Count != 2 || e.SavedCourseDeg != 90.0) return "RememberRoute keeps the first value";
            if (DecideRoute(e, new[] { 50.0, 0.0 }, dist) != "nothing_saved") return "route: mod placed nothing yet";
            m.NoteRoutePlaced(u1, 30.0, 40.0);
            if (DecideRoute(null, null, dist) != "nothing_saved") return "route: no entry";
            if (DecideRoute(e, new[] { 30.0 + 0.1 / 60.0, 40.0 }, dist) != "restore") return "route: 0.1 nm -> restore";
            if (DecideRoute(e, new[] { 30.0 + 1.0 / 60.0, 40.0 }, dist) != "player_changed") return "route: 1 nm -> player_changed";
            if (DecideRoute(e, null, dist) != "restore") return "route: no waypoints left -> restore";

            m.RememberRoute(u2, null, null);
            if (m.Peek(u2).SavedRoute.Count != 0 || m.Peek(u2).SavedCourseDeg.HasValue) return "RememberRoute(null)";

            if (m.Take(u1) == null || m.Peek(u1) != null || m.Take(u1) != null) return "Take removes";
            m.Clear();
            if (m.Count != 0 || m.Peek(u2) != null) return "Clear";
            m.RememberRoe(null, "hold"); m.RememberRoute(null, null, null);
            if (m.Count != 0) return "null unit is ignored";

            var f = new ControlMemory(); var fu = new object();
            f.RememberField(fu, "telegraph", "3"); f.RememberField(fu, "telegraph", "5");
            if (f.Peek(fu).Fields["telegraph"].Saved != "3") return "RememberField keeps the first";
            if (DecideField(f.Peek(fu), "telegraph", "5") != "nothing_saved") return "field: not set by the mod yet";
            f.NoteFieldSet(fu, "telegraph", "5");
            if (DecideField(f.Peek(fu), "telegraph", "5") != "restore") return "field: restore";
            if (DecideField(f.Peek(fu), "telegraph", "2") != "player_changed") return "field: player_changed";
            if (DecideField(f.Peek(fu), "emcon", "true") != "nothing_saved") return "field: other key";
            if (DecideField(null, "emcon", "true") != "nothing_saved") return "field: no entry";
            f.RememberField(fu, "emcon", "false"); f.NoteFieldSet(fu, "emcon", "true");
            if (DecideField(f.Peek(fu), "emcon", "TRUE") != "restore") return "field: case-insensitive";
            f.NoteFieldSet(new object(), "emcon", "true");
            if (f.Count != 1) return "NoteFieldSet without entry creates nothing";
            return null;
        }
    }
}
