using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>The game side of the loss tracker (AirLossGame.cs; a fake in the airlosstest stand).</summary>
    internal interface IAirLossGame
    {
        double Now { get; }                    // mission time, s
        double RealNow { get; }                // real time, s (the journal line)
        IList<AirOwn> OwnAirborne();           // own aircraft and helicopters in the air (the whole side of the player)
        AirLossFacts Facts(int uid);           // the facts of an aircraft that left the list (cached state of the last tick); never null
        int Aboard(int uid);                   // people on board at the last tick (SAR), 0 = none
        IList<string> NearHostiles(int uid, int max);   // "class@nm" of the hostile tracks nearest to the last position
        string Roe(int uid);                   // weapons status at the last tick
        void Event(string name, string dataJson);
        void Log(string line);
    }

    internal sealed class AirLossInfo
    {
        public int Uid, Aboard;
        public string Name, Type, Window, Group, Cause, Weapon, ByClass;
        public double Lat, Lon, AltM, Kn;
    }

    /// <summary>T-175 / ADR-0075 item 6: an own aircraft that left the list of the airborne without a landing is a loss: cause, journal, event, card, x1.</summary>
    internal sealed class AirLossTracker
    {
        private sealed class Hit { public double At; public string Weapon, ByClass; }

        private readonly IAirLossGame _g;
        private readonly Dictionary<int, AirOwn> _known = new Dictionary<int, AirOwn>();
        private readonly Dictionary<int, Hit> _hits = new Dictionary<int, Hit>();
        private readonly HashSet<int> _reported = new HashSet<int>();

        public AirLossTracker(IAirLossGame g) { _g = g; }

        public Action<AirLossInfo> Lost;
        public IAirTimeX1 TimeX1 { get; set; }

        public void Reset() { _known.Clear(); _hits.Clear(); _reported.Clear(); }

        /// <summary>A hit on an own aircraft (any shooter); the weapon and the class of the shooter only as the player knows them.</summary>
        public void OnHit(int uid, string weapon, string byClass, double now)
        {
            _hits[uid] = new Hit { At = now, Weapon = weapon, ByClass = byClass };
        }

        private static string N(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

        public void Tick(double now)
        {
            IList<AirOwn> owns = null;
            try { owns = _g.OwnAirborne(); } catch { }
            if (owns == null) return;
            var present = new HashSet<int>();
            foreach (var o in owns) { present.Add(o.Uid); _known[o.Uid] = o; _reported.Remove(o.Uid); }
            foreach (int uid in new List<int>(_known.Keys))
            {
                if (present.Contains(uid)) continue;
                var last = _known[uid];
                var f = _g.Facts(uid) ?? new AirLossFacts();
                if (f.Landed) { _known.Remove(uid); _hits.Remove(uid); continue; }
                if (f.Alive && !f.Destroyed) continue;   // still there (landing circuit, not in flight): not a loss, looked at again next tick
                _known.Remove(uid);
                if (!_reported.Add(uid)) continue;
                Hit h;
                if (_hits.TryGetValue(uid, out h)) { f.LastHitAgoS = Math.Max(0, now - h.At); f.HitWeapon = h.Weapon; f.HitByClass = h.ByClass; }
                _hits.Remove(uid);
                string weapon, by;
                string cause = AirLossMath.Cause(f, out weapon, out by);
                if (cause == null) continue;
                Report(last, f, cause, weapon, by, now);
            }
        }

        private void Report(AirOwn o, AirLossFacts f, string cause, string weapon, string by, double now)
        {
            var info = new AirLossInfo { Uid = o.Uid, Name = o.Name, Type = o.TypeLabel, Window = o.Window, Group = o.Group, Cause = cause, Weapon = weapon, ByClass = by, Lat = o.Lat, Lon = o.Lon, AltM = f.AltM >= 0 ? f.AltM : o.AltM, Kn = o.SpeedKn };
            try { info.Aboard = _g.Aboard(o.Uid); } catch { }
            string hostiles = "-";
            try { var l = _g.NearHostiles(o.Uid, 3); if (l != null && l.Count > 0) hostiles = string.Join(",", new List<string>(l).ToArray()); } catch { }
            string roe = null; try { roe = _g.Roe(o.Uid); } catch { }
            try
            {
                _g.Log("[Loss] uid=" + o.Uid + " name=" + (o.Name ?? "-") + " grp=" + (o.Group ?? "-") + " cause=" + cause + " weapon=" + (weapon ?? "-") + " by=" + (by ?? "-")
                    + " pos=" + o.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + o.Lon.ToString("0.0000", CultureInfo.InvariantCulture) + " alt_m=" + N(info.AltM) + " kn=" + N(o.SpeedKn)
                    + " mt=" + N(now) + " rt=" + N(_g.RealNow) + " hostiles=" + hostiles + " roe=" + (roe ?? "-") + (info.Aboard > 0 ? " aboard=" + info.Aboard : ""));
            }
            catch { }
            try
            {
                _g.Event("air_lost", "{\"uid\":" + o.Uid + ",\"name\":" + Json.Str(o.Name ?? "") + ",\"type\":" + Json.Str(o.TypeLabel ?? "") + ",\"group\":" + Json.Str(o.Group ?? "")
                    + ",\"cause\":" + Json.Str(cause) + (weapon != null ? ",\"weapon\":" + Json.Str(weapon) : "") + (by != null ? ",\"by_class\":" + Json.Str(by) : "")
                    + ",\"lat\":" + Json.Num(o.Lat) + ",\"lon\":" + Json.Num(o.Lon) + ",\"alt_m\":" + Json.Num(info.AltM) + (info.Aboard > 0 ? ",\"aboard\":" + info.Aboard : "") + "}");
            }
            catch { }
            var x = TimeX1;
            if (x != null) { try { x.Offer("loss", "loss:" + o.Uid, UiText.Fmt("airguard.tc.loss", o.Name ?? ("#" + o.Uid))); } catch { } }
            var cb = Lost;
            if (cb != null) { try { cb(info); } catch { } }
        }
    }
}
