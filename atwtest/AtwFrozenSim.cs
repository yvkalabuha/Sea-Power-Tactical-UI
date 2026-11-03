using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>T-287 (ADR-0114): salvo at the pursuit point of a frozen torpedo track: TrackPursuit.Point, AtwTurnMath.FrozenAim / FrozenGate / FrozenFields.</summary>
    internal static class AtwFrozenSim
    {
        private static AtwFrozenIn In(double px, double py, double ownKn, double ownHdg, double pursuitKn, double salvoKn)
        {
            return new AtwFrozenIn { PX = px, PY = py, OwnKn = ownKn, OwnHdgDeg = ownHdg, PursuitKn = pursuitKn, SalvoKn = salvoKn };
        }

        private static AtwFrozenAim Aim(double pur, double aimNm, double brg, double leadS, double tti)
        {
            return new AtwFrozenAim { PursuitNm = pur, AimNm = aimNm, AimBrgDeg = brg, LeadS = leadS, TtiS = tti };
        }

        public static string SelfTest()
        {
            // F0: TrackPursuit.Point follows the frozen point
            var tp = new TrackPursuit();
            double lat, lon;
            if (tp.Point(1, 7, out lat, out lon)) return "F0: unknown pair must give false";
            tp.Update(1, 7, 0.1, 0, true, false, 0, 0, 0);
            if (!tp.Point(1, 7, out lat, out lon) || Math.Abs(lat - 0.1) > 1e-9 || Math.Abs(lon) > 1e-9) return "F0: first point is the plot";
            tp.Update(1, 7, 0.1, 0, false, false, 0, 0, 36);   // 40 kn default * 36 s = 0.4 nm toward the ship
            if (!tp.Point(1, 7, out lat, out lon) || Math.Abs(lat - (0.1 - 0.4 / 60.0)) > 1e-6) return "F0: the point moves toward the ship: " + lat.ToString("R", CultureInfo.InvariantCulture);
            if (Math.Abs(tp.PursuitNm(1, 7, 0, 0) - lat * 60.0) > 1e-6) return "F0: PursuitNm is the distance to Point";
            if (tp.Point(1, 8, out lat, out lon) || tp.Point(2, 7, out lat, out lon)) return "F0: other pair unknown";

            // F1: point 0.8 nm astern, ship stopped, 45 kn: (0.8 - InterceptNm) / 45 h
            AtwFrozenAim a = AtwTurnMath.FrozenAim(In(0, -0.8, 0, 0, 45, 600));
            double tti1 = (0.8 - TrackPursuit.InterceptNm) / 45.0 * 3600.0;
            if (Math.Abs(a.TtiS - tti1) > 1.0) return "F1: tti " + a.TtiS + " vs " + tti1;
            if (Math.Abs(a.PursuitNm - 0.8) > 1e-9) return "F1: pursuit nm";
            if (Math.Abs(a.AimNm - (0.8 - 45.0 * a.LeadS / 3600.0)) > 0.01) return "F1: aim nm " + a.AimNm + " lead " + a.LeadS;
            if (Math.Abs(AtwTurnMath.Norm180(a.AimBrgDeg - 180)) > 0.5) return "F1: aim bearing astern " + a.AimBrgDeg;
            if (Math.Abs(a.AimX) > 1e-6 || Math.Abs(a.AimY + a.AimNm) > 1e-6) return "F1: aim xy";

            // F2: ship runs away at 24 kn from a 45 kn point; faster than the point = never
            a = AtwTurnMath.FrozenAim(In(0, -0.8, 24, 0, 45, 600));
            double tti2 = (0.8 - TrackPursuit.InterceptNm) / 21.0 * 3600.0;
            if (Math.Abs(a.TtiS - tti2) > 1.5) return "F2: tti " + a.TtiS + " vs " + tti2;
            a = AtwTurnMath.FrozenAim(In(0, -0.8, 45, 0, 40, 600));
            if (!double.IsPositiveInfinity(a.TtiS)) return "F2: ship faster than the point must give +inf, got " + a.TtiS;

            // F3: point abeam to starboard, ship runs north at 20 kn: the aim is moved ahead
            a = AtwTurnMath.FrozenAim(In(0.6, 0, 20, 0, 45, 600));
            if (!(a.AimBrgDeg < 89 && a.AimBrgDeg > 0)) return "F3: aim ahead of the beam, brg " + a.AimBrgDeg;
            if (!(a.AimNm < 0.6)) return "F3: aim nearer than the point " + a.AimNm;

            // F4: salvo speed 0 -> default; lead = reaction + extra + flight
            a = AtwTurnMath.FrozenAim(In(0, -0.8, 0, 0, 45, 0));
            double lead4 = AntiTorpedoMath.DefaultReactionS + AtwTurnMath.FrozenExtraLeadS + a.AimNm / AntiTorpedoMath.DefaultSpeedKn * 3600.0;
            if (Math.Abs(a.LeadS - lead4) > 0.5) return "F4: lead " + a.LeadS + " vs " + lead4;
            AtwFrozenAim slow = AtwTurnMath.FrozenAim(In(0, -0.8, 0, 0, 45, 300));
            if (!(slow.LeadS > a.LeadS)) return "F4: slower salvo = longer lead";
            if (AtwTurnMath.FrozenAim(In(double.NaN, 0, 0, 0, 45, 600)) == null) return "F4: NaN input still returns an object";

            // F5: the gate
            var arcs = new List<AtwArc> { new AtwArc(20, 115), new AtwArc(-115, -20) };
            AtwFrozenAim ok = Aim(0.9, 0.5, 60, 15, 100);
            string g = AtwTurnMath.FrozenGate(ok, false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0);
            if (g != null) return "F5: clean case fires, got " + g;
            g = AtwTurnMath.FrozenGate(Aim(1.05, 0.5, 60, 15, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0);
            if (g != "far") return "F5: far, got " + g;
            if (AtwTurnMath.FrozenGate(ok, true, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "expired") return "F5: expired";
            if (AtwTurnMath.FrozenGate(ok, false, AtwTurnMath.FrozenMaxSalvos, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "salvos") return "F5: salvos";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 60, 15, double.PositiveInfinity), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "receding") return "F5: receding";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 60, 15, 19), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "late") return "F5: late";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 60, 15, 20.5), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != null) return "F5: just not late";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.12, 60, 15, 100), false, 0, 0.05, 3.0, 0.15, 0, arcs, false, 0) != "min_range") return "F5: min_range by safety";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.12, 60, 15, 100), false, 0, 0.19, 3.0, 0.05, 0, arcs, false, 0) != "min_range") return "F5: min_range by the mount";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 3.5, 60, 15, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "out_of_range") return "F5: out_of_range";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 180, 15, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "no_arc") return "F5: no_arc astern";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 112, 15, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, 0) != "no_arc") return "F5: no_arc inside the margin";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 60, 15, 100), false, 0, 0.19, 3.0, 0.15, 0, null, false, 0) != "no_arc") return "F5: no arcs known";
            // back block: now rel 100 (inside), swinging to -60 leaves the arc at the end of the turn
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 100, 5, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, true, -60) != "turn_back") return "F5: turn_back";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 100, 5, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, false, -60) != null) return "F5: no block without backBlock";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 100, 5, 25), false, 0, 0.19, 3.0, 0.15, 0, arcs, true, -60) != null) return "F5: turn_back lifted by tti < BackUrgentTtiS";
            if (AtwTurnMath.FrozenGate(Aim(0.9, 0.5, 60, 15, 100), false, 0, 0.19, 3.0, 0.15, 0, arcs, true, 20) != null) return "F5: stays in the arc on the whole turn";

            // F6: fields
            string f = AtwTurnMath.FrozenFields(Aim(0.9, 0.5, 100, 15, double.PositiveInfinity), 40);
            if (f != " pur_nm=0.90 aim_nm=0.50 aim_brg=100 rel=60 lead_s=15 tti=inf") return "F6: fields '" + f + "'";
            f = AtwTurnMath.FrozenFields(Aim(0.9, 0.5, 100, 15, 63.4), 40);
            if (!f.EndsWith(" tti=63")) return "F6: tti rounded '" + f + "'";
            return null;
        }
    }
}
