using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-266 / ADR-0106: the game side of ShipMobility. Reads only; any failed read = that input unknown.
    internal sealed partial class GameApi
    {
        private readonly Dictionary<object, double> _mobSlowSince = new Dictionary<object, double>(new RefEq());

        /// <summary>T-266: ShipMobility.Immobile of an own ship (mission time; any read failure = that input unknown). Never throws.</summary>
        internal string ShipImmobileWhy(object u) { bool w; int? tg; return ShipImmobileWhy(u, out w, out tg); }

        /// <summary>T-274: also tells whether the ship stands under a telegraph that asks for way (ShipMobility.WayPending) and the telegraph.</summary>
        internal string ShipImmobileWhy(object u, out bool wayPending, out int? telegraph)
        {
            wayPending = false; telegraph = null;
            try
            {
                if (u == null) return null;
                bool destroyed = false;
                try { destroyed = BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed)); } catch { }
                bool? sinking = null, flag = null;
                try
                {
                    var c = Get(u, EvasionSpecs.ObjCompartments);
                    if (c != null)
                    {
                        var s = Get(c, EvasionSpecs.CompSinking); if (s is bool) sinking = (bool)s;
                        var f = Get(c, EvasionSpecs.CompImmobile); if (f is bool) flag = (bool)f;
                    }
                }
                catch { }
                double kn = double.NaN;
                try { kn = Math.Abs(D(Call(u, AirOpsSpecs.ObjVelocityKn)) ?? double.NaN); } catch { }
                double? mt = null;
                try { mt = Frame.MissionTimeS; } catch { }
                double now = mt.HasValue ? mt.Value : double.NaN;
                double since;
                if (!_mobSlowSince.TryGetValue(u, out since)) since = -1;
                telegraph = Telegraph(u);
                wayPending = ShipMobility.WayPending(kn, telegraph);
                string why = ShipMobility.Immobile(destroyed, sinking, flag, kn, telegraph, now, ref since);
                if (since < 0) _mobSlowSince.Remove(u); else _mobSlowSince[u] = since;
                return why;
            }
            catch (Exception) { return null; }
        }

        internal void ShipMobilityLeave() { _mobSlowSince.Clear(); }
    }
}
