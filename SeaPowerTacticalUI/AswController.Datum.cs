using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115: the groups the "sub launch area" card can send. Only the answer is built here; the order itself is the usual asw_search_area.
    internal sealed partial class AswController
    {
        /// <summary>Main thread. Every group with its order, the count of Capable &amp;&amp; Usable members and the min ETA to (lat, lon); area groups carry Rect / Tactics / Depth.</summary>
        public List<SubDatumGroupIn> DatumGroups(double lat, double lon)
        {
            var res = new List<SubDatumGroupIn>();
            var c = new GeoPt(lat, lon);
            foreach (var g in _groups)
            {
                var gi = new SubDatumGroupIn { Name = g.Name, Order = g.Order };
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (!_live.TryGetValue(u, out m) || !Capable(m) || !AswMath.Usable(m)) continue;
                    gi.Usable++;
                    double eta = DatumEtaS(m, c);
                    if (eta < gi.EtaS) gi.EtaS = eta;
                }
                if (g.Order == "area")
                {
                    gi.Rect = g.Rect == null ? null : (double[])g.Rect.Clone();
                    gi.Tactics = new List<string>(g.Tactics);
                    gi.Depth = g.Depth;
                }
                res.Add(gi);
            }
            return res;
        }

        /// <summary>In the air: the straight run; on a deck: readiness + launch + the run from the deck's ship (no ship known: the launch only). A rough order, not a promise.</summary>
        private double DatumEtaS(AswMember m, GeoPt c)
        {
            if (m.Where == "air") return (m.Lat == 0 && m.Lon == 0) ? 0 : AswMath.DistNm(new GeoPt(m.Lat, m.Lon), c) / SpeedOf(m) * 3600;
            double run = 0;
            try
            {
                var ship = m.DeckUid != 0 ? _game.Target(m.DeckUid) : null;
                if (ship != null) run = AswMath.DistNm(Pos(ship), c) / SpeedOf(m) * 3600;
            }
            catch (Exception) { }
            return AswMath.ReadyS(m) + m.LaunchS + run;
        }
    }
}
