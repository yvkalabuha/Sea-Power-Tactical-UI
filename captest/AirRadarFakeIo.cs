using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-241: fake radar IO of the stands (captest; aswtest compiles it from ../captest). Models the warm-up of a radar and the derived EMCON.
    internal sealed class AirRadarFakeIo : IAirRadarIo
    {
        internal sealed class Unit
        {
            public bool Known = true, HasAir = true, HasSurface;   // HasAir: an operable air-list radar (Air or SurfaceAndAir)
            public bool AirOn, SurfOn;
            public double AirAt = -1, SurfAt = -1;
        }

        public double NowS { get; set; }
        public readonly Dictionary<int, Unit> Units = new Dictionary<int, Unit>();
        public readonly List<string> Calls = new List<string>();   // "set <uid> <list> on|off"
        public double WarmS = 2;                                   // seconds until a Set(on) shows On
        public bool Unsupported;

        public Unit Add(int uid, bool hasAir = true, bool hasSurface = false, bool airOn = false, bool surfOn = false)
        {
            var u = new Unit { HasAir = hasAir, HasSurface = hasSurface, AirOn = airOn, SurfOn = surfOn };
            Units[uid] = u; return u;
        }

        public int SetCount(int uid, string tail = null)
        {
            int n = 0; string head = "set " + uid + " ";
            foreach (var c in Calls) if (c.StartsWith(head) && (tail == null || c.EndsWith(tail))) n++;
            return n;
        }

        private void Warm(Unit u)
        {
            if (u.AirAt >= 0 && NowS >= u.AirAt) { u.AirOn = true; u.AirAt = -1; }
            if (u.SurfAt >= 0 && NowS >= u.SurfAt) { u.SurfOn = true; u.SurfAt = -1; }
        }

        public AirRadarRead Read(int uid, string want)
        {
            Unit u; if (!Units.TryGetValue(uid, out u) || !u.Known) return new AirRadarRead();
            Warm(u);
            bool surf = want == "search_surface" && u.HasSurface;
            return new AirRadarRead { Known = true, HasRadar = surf || u.HasAir, On = surf ? u.SurfOn : u.AirOn, Emcon = !u.AirOn && !u.SurfOn, List = surf ? "surface" : "air" };
        }

        public string Set(int uid, string list, bool on)
        {
            Unit u; if (Unsupported) return "unsupported";
            if (!Units.TryGetValue(uid, out u)) return "unknown";
            Calls.Add("set " + uid + " " + list + " " + (on ? "on" : "off"));
            if (list == "surface") { if (on) u.SurfAt = NowS + WarmS; else { u.SurfOn = false; u.SurfAt = -1; } }
            else { if (on) u.AirAt = NowS + WarmS; else { u.AirOn = false; u.AirAt = -1; } }
            return null;
        }

        /// <summary>The player (or a trigger) switches the radar off.</summary>
        public void PlayerOff(int uid) { Unit u = Units[uid]; u.AirOn = u.SurfOn = false; u.AirAt = u.SurfAt = -1; }
    }
}
