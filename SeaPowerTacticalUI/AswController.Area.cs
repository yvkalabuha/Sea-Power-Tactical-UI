using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-176 C5 / ADR-0076 s.2: the area. A square lattice of buoys from the centre in rings, sectors per aircraft, the dipping lattice (leapfrog by rings), MAD strips from the centre.
    internal sealed partial class AswController
    {
        private NmPt LocalOf(Grp g, GeoPt p) { return AswMath.ToLocal(g.Plan.AreaC, 0, p); }
        private GeoPt GeoOf(Grp g, NmPt p) { return AswMath.ToGeo(g.Plan.AreaC, 0, p); }

        /// <summary>Waypoints of a search rectangle for the preview: the buoy lattice and the dipping lattice in route order, the MAD creeping line.</summary>
        private void AreaGeometry(Grp g, out List<GeoPt> all, out List<GeoPt> dip, out List<GeoPt> mad)
        {
            GeoPt c; double north, east;   // T-172 B1: the local frame is X = north, Y = east, whatever the order of the corners
            AswMath.RectSpans(g.Rect, out c, out north, out east);
            string src;
            double r = GroupR(g, out src);
            int nx, ny; double sn, se;
            var nodes = AswPatternMath.Lattice(north, east, r, out nx, out ny, out sn, out se);
            var idx = new List<int>(); for (int i = 0; i < nodes.Count; i++) idx.Add(i);
            all = new List<GeoPt>();
            foreach (int i in AswPatternMath.RingRoute(nodes, idx, new NmPt(0, 0))) all.Add(AswMath.ToGeo(c, 0, nodes[i].P));
            double s = AswMath.DipStep(DipRangeOf(g));
            var dn = AswPatternMath.Lattice(north, east, s / 1.6, out nx, out ny, out sn, out se);
            idx = new List<int>(); for (int i = 0; i < dn.Count; i++) idx.Add(i);
            dip = new List<GeoPt>();
            foreach (int i in AswPatternMath.RingRoute(dn, idx, new NmPt(0, 0))) dip.Add(AswMath.ToGeo(c, 0, dn[i].P));
            mad = ToGeoList(c, 0, AswMath.CreepingLine(north, east, g.P.MadLegNm));
        }

        private double DipRangeOf(Grp g)
        {
            double rd = 3.0;
            foreach (int u in g.Members) { double x = _game.DipRangeNm(u); if (x > 0) rd = x; }
            return rd;
        }

        private void BuildArea(Grp g)
        {
            var pl = g.Plan;
            GeoPt c; double north, east;
            AswMath.RectSpans(g.Rect, out c, out north, out east);
            pl.AreaC = c; pl.AreaNorth = north; pl.AreaEast = east;
            string src;
            double r = GroupR(g, out src);
            int nx, ny; double sn, se;
            pl.Nodes = AswPatternMath.Lattice(north, east, r, out nx, out ny, out sn, out se);
            pl.AreaStep = Math.Max(sn, se); pl.AreaStepMin = Math.Min(sn, se);
            int bnx = nx, bny = ny;
            pl.LastR = N(Math.Round(r, 2));
            pl.NodesDone.Clear(); pl.Sectors.Clear(); pl.SectorSig = "";
            double s = AswMath.DipStep(DipRangeOf(g));
            if (pl.DipNodes == null) { pl.DipNodes = AswPatternMath.Lattice(north, east, s / 1.6, out nx, out ny, out sn, out se); pl.DipVisited.Clear(); }
            int land = 0, shallow = 0;
            double minW = AswRouteGuard.MinWaterBuoyM(_defaults.Pat.MinWaterBuoyM, 0);
            foreach (var n in pl.Nodes)
            {
                double h = _game.HeightM(AswMath.ToGeo(c, 0, n.P)) - _defaults.LandM;
                if (h >= 0) land++; else if (-h < minW) shallow++;
            }
            g.AreaBuilt = true;
            _game.Log("asw area " + g.Name + " lattice nx=" + I(bnx) + " ny=" + I(bny) + " d=" + N(Math.Round(pl.AreaStep, 2)) + " R=" + N(Math.Round(r, 2)) + " nodes=" + I(pl.Nodes.Count) + " land=" + I(land) + " shallow=" + I(shallow));
            UpdateAreaView(g);
        }

        /// <summary>The buoy lattice is built for the smallest R of the group; when it changes, the rest is built again (a node that a live buoy covers is skipped at the package).</summary>
        private void CheckAreaR(Grp g)
        {
            string src;
            double r = GroupR(g, out src);
            double was;
            if (g.Plan.Nodes != null && double.TryParse(g.Plan.LastR, NumberStyles.Float, CultureInfo.InvariantCulture, out was) && Math.Abs(was - r) > 0.05)
            {
                _game.Log("asw area " + g.Name + " rebuild R " + g.Plan.LastR + " -> " + N(Math.Round(r, 2)));
                g.AreaDone = false;
                BuildArea(g);
            }
        }

        // ---- sectors -------------------------------------------------------------------------------------------------

        private double GroupBearing(Grp g)
        {
            AswTarget t = g.TargetUid != 0 ? _game.Target(g.TargetUid) : null;
            GeoPt p;
            if (t != null) p = Pos(t);
            else
            {
                double la = 0, lo = 0; int n = 0;
                foreach (int u in g.Members) { AswMember m; if (_live.TryGetValue(u, out m) && (m.Lat != 0 || m.Lon != 0)) { la += m.Lat; lo += m.Lon; n++; } }
                if (n == 0) return 180;
                p = new GeoPt(la / n, lo / n);
            }
            if (AswMath.DistNm(p, g.Plan.AreaC) < 0.1) return 180;
            return AswMath.BearingDeg(g.Plan.AreaC, p);
        }

        private double EtaOf(Grp g, AswMember m)
        {
            if (m.Where == "air" && (m.Lat != 0 || m.Lon != 0)) return AswMath.DistNm(new GeoPt(m.Lat, m.Lon), g.Plan.AreaC) / SpeedOf(m) * 3600;
            return AswMath.ReadyS(m) + m.LaunchS + m.TransitS;
        }

        private List<AswMember> HoldersOf(Grp g, string tac)
        {
            var res = new List<AswMember>();
            foreach (int u in g.Members)
            {
                AswMember m; string t;
                if (!_live.TryGetValue(u, out m) || !g.Assign.TryGetValue(u, out t) || t != tac) continue;
                res.Add(m);
            }
            res.Sort((a, b) => a.Uid.CompareTo(b.Uid));
            return res;
        }

        private List<AswMember> GridHolders(Grp g)
        {
            var res = new List<AswMember>();
            foreach (int u in g.Members)
            {
                AswMember m; string t;
                if (_live.TryGetValue(u, out m) && g.Assign.TryGetValue(u, out t) && (t == "grid" || t == "rows" || t == "cross")) res.Add(m);
            }
            res.Sort((a, b) => a.Uid.CompareTo(b.Uid));
            return res;
        }

        /// <summary>Angular sectors of the nodes that are still open, one per aircraft that holds the buoy tactic, sized by capacity; built again when the holders or R change.</summary>
        private void EnsureSectors(Grp g, List<AswMember> holders)
        {
            var pl = g.Plan;
            var sb = new System.Text.StringBuilder();
            foreach (var m in holders) sb.Append(m.Uid).Append(',');
            sb.Append('|').Append(pl.LastR);
            if (pl.SectorSig == sb.ToString()) return;
            pl.SectorSig = sb.ToString();
            pl.Sectors.Clear();
            var open = new List<int>(); var sub = new List<LatNode>();
            for (int i = 0; i < pl.Nodes.Count; i++) if (!pl.NodesDone.Contains(i)) { open.Add(i); sub.Add(pl.Nodes[i]); }
            if (holders.Count == 0 || sub.Count == 0) return;
            var cap = new List<int>(); var eta = new List<double>();
            foreach (var m in holders)
            {
                string ammo; BuoyType bt;
                int have = PickFor(m, out ammo, out bt) ? AvailFor(m, bt) : 0;
                cap.Add(AswPatternMath.Capacity(have, SpeedOf(m), AswMath.RemainS(m, null), pl.AreaStep));
                eta.Add(EtaOf(g, m));
            }
            var sec = AswPatternMath.Sectors(sub, GroupBearing(g), cap, eta);
            var sbl = new System.Text.StringBuilder();
            for (int k = 0; k < holders.Count; k++)
            {
                var l = new List<int>();
                foreach (int si in sec[k]) l.Add(open[si]);
                pl.Sectors[holders[k].Uid] = l;
                sbl.Append(holders[k].Uid).Append(':').Append(l.Count).Append(',');
            }
            _game.Log("asw area " + g.Name + " sectors=" + sbl.ToString().TrimEnd(',') + " open=" + I(open.Count) + " brg=" + N(Math.Round(GroupBearing(g))));
        }

        private static double CoverGap(double ra, double rb, double cap) { double g = AswMath.PairGapNm(ra, rb); return cap > 0 ? Math.Min(g, cap) : g; }

        /// <summary>Is the water around p covered by a live buoy that will last long enough (course: until the group passes + lead; area: lead)?</summary>
        private bool CoveredAt(Grp g, GeoPt p, double rNew, double passInS, double capGapNm = -1)
        {
            double lead = _defaults.Pat.ReseedLeadS;
            foreach (var b in g.Buoys)
            {
                double remain = b.LifeS - (_now - b.DroppedS);
                if (remain <= 0) continue;
                if (AswMath.DistNm(new GeoPt(b.Lat, b.Lon), p) < CoverGap(rNew, RangeOf(b.Ammo, rNew), capGapNm) - 1e-9 && AswPatternMath.NodeCovered(true, remain, passInS, lead)) return true;
            }
            try
            {
                var own = _game.OwnBuoys();
                if (own != null)
                    foreach (var b in own)
                    {
                        if (b.RemainingS == 0) continue;
                        double remain = b.RemainingS > 0 ? b.RemainingS : (b.LifeS > 0 && b.DroppedS >= 0 ? b.LifeS - (_now - b.DroppedS) : 1e9);
                        if (AswMath.DistNm(new GeoPt(b.Lat, b.Lon), p) < CoverGap(rNew, RangeOf(b.Ammo, rNew), capGapNm) - 1e-9 && AswPatternMath.NodeCovered(true, remain, passInS, lead)) return true;
                    }
            }
            catch (Exception e) { _game.Log("asw own buoys error: " + e.Message); }
            return false;
        }

        /// <summary>The next open nodes of the sector of this aircraft in ring order from its place (covered ones are closed on the way). Empty = nothing left.</summary>
        private List<int> NextGridNodes(Grp g, AswMember m, int n, double r)
        {
            var pl = g.Plan;
            var res = new List<int>();
            List<int> sec;
            if (!pl.Sectors.TryGetValue(m.Uid, out sec)) return res;
            var open = new List<int>();
            foreach (int i in sec) if (!pl.NodesDone.Contains(i)) open.Add(i);
            if (open.Count == 0) return res;
            NmPt from = (m.Lat != 0 || m.Lon != 0) ? LocalOf(g, new GeoPt(m.Lat, m.Lon)) : new NmPt(0, 0);
            var route = AswPatternMath.RingRoute(pl.Nodes, open, from);
            foreach (int i in route)
            {
                if (CoveredAt(g, GeoOf(g, pl.Nodes[i].P), r, 0, 0.9 * pl.AreaStepMin)) { pl.NodesDone.Add(i); continue; }
                res.Add(i);
                if (res.Count >= n) break;
            }
            return res;
        }

        /// <summary>
        /// ADR-0076 s.2.7 / s.5.4: a node whose buoy has gone out or goes out within ReseedLeadS becomes open again (the area refreshes itself). Looks every 30 s.
        /// </summary>
        private void AreaReseed(Grp g)
        {
            var pl = g.Plan;
            if (pl.Nodes == null || _now - pl.ReseedCheckS < 30) return;
            pl.ReseedCheckS = _now;
            double r; if (!double.TryParse(pl.LastR, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return;
            int expired = 0, expiring = 0;
            foreach (int i in new List<int>(pl.NodeLaid))
            {
                var p = GeoOf(g, pl.Nodes[i].P);
                if (CoveredAt(g, p, r, 0, 0.9 * pl.AreaStepMin)) continue;
                bool any = false;
                foreach (var b in g.Buoys) if (_now - b.DroppedS < b.LifeS && AswMath.DistNm(new GeoPt(b.Lat, b.Lon), p) < 0.5) any = true;
                if (any) expiring++; else expired++;
                pl.NodeLaid.Remove(i); pl.NodesDone.Remove(i);
            }
            if (expired + expiring == 0) return;
            g.AreaDone = false; pl.SectorSig = "";
            _game.Log("asw reseed " + g.Name + " nodes=" + I(expired + expiring) + " reason=" + (expired > 0 ? "expired" : "expiring"));
        }

        private void AreaDoneCheck(Grp g)
        {
            var pl = g.Plan;
            if (pl.Nodes == null) return;
            g.Covered = pl.Nodes.Count == 0 ? 1.0 : (double)pl.NodesDone.Count / pl.Nodes.Count;
            if (pl.NodesDone.Count >= pl.Nodes.Count && pl.Deferred.Count == 0 && !g.AreaDone)
            {
                g.AreaDone = true;
                _game.Event("asw_area_done", Obj("group", g.Name, "nodes", pl.Nodes.Count));
                _game.Event("asw_barrier_laid", Obj("group", g.Name, "tactic", "area", "complete", true));
                Warn(g, "area_done", null, 0);
            }
        }

        private void AreaGrid(Grp g, AswMember m, string dumpKind = null, int limit = 0)
        {
            var pl = g.Plan;
            string ammo; BuoyType bt;
            if (!PickFor(m, out ammo, out bt)) { Warn(g, "buoys_low", m.Name); return; }
            double rr = AswMath.REff(bt, g.P).Nm;
            EnsureSectors(g, GridHolders(g));
            int n = Math.Min(Math.Min(AvailFor(m, bt), AswMath.MaxDropPoints), limit > 0 ? limit : AswMath.MaxDropPoints);
            if (n <= 0) { Warn(g, "buoys_low", m.Name); return; }
            var idx = NextGridNodes(g, m, n, rr);
            AreaDoneCheck(g);
            if (idx.Count == 0) return;
            var pts = new List<GeoPt>();
            foreach (int i in idx) pts.Add(GeoOf(g, pl.Nodes[i].P));
            foreach (int i in idx) pl.NodesDone.Add(i);
            g.Covered = (double)pl.NodesDone.Count / pl.Nodes.Count;
            var from = (m.Lat != 0 || m.Lon != 0) ? LocalOf(g, new GeoPt(m.Lat, m.Lon)) : new NmPt(0, 0);
            var loc = new List<NmPt>(); foreach (int i in idx) loc.Add(pl.Nodes[i].P);
            _game.Log("asw route " + g.Name + " uid=" + I(m.Uid) + " tactic=grid pts=" + I(idx.Count) + " len_nm=" + N(Math.Round(AswPatternMath.PathNm(from, loc), 1)) + " lb_nm=" + N(Math.Round(AswPatternMath.LowerBoundNm(idx.Count, pl.AreaStep), 1))
                + " ratio=" + N(Math.Round(AswPatternMath.PathNm(from, loc) / Math.Max(0.01, AswPatternMath.LowerBoundNm(idx.Count, pl.AreaStep)), 2)));
            var done = new List<int>(idx);
            bool ok = IssueDrop(g, m, ammo, bt, pts, dumpKind ?? "area", () => { foreach (int i in done) pl.NodesDone.Remove(i); }, "area", GapFor(g, bt), true, 0.9 * pl.AreaStepMin);   // the lattice is already in ring order
            if (ok)
                foreach (int i in done)
                {
                    var np = GeoOf(g, pl.Nodes[i].P);
                    foreach (var b in g.Buoys) if (b.DroppedS == _now && AswMath.DistNm(new GeoPt(b.Lat, b.Lon), np) < 0.3) { pl.NodeLaid.Add(i); break; }
                }
            AreaDoneCheck(g);
        }

        // ---- dipping sonar in the area: the leapfrog by rings --------------------------------------------------------

        private void AreaDip(Grp g, AswMember m)
        {
            var pl = g.Plan;
            if (pl.DipNodes == null) return;
            NmPt from = (m.Lat != 0 || m.Lon != 0) ? LocalOf(g, new GeoPt(m.Lat, m.Lon)) : new NmPt(0, 0);
            for (int guard = 0; guard < pl.DipNodes.Count; guard++)
            {
                int i = AswPatternMath.NextAreaDip(pl.DipNodes, pl.DipVisited, null, from);
                if (i < 0) return;
                var pt = GeoOf(g, pl.DipNodes[i].P);
                pl.DipVisited.Add(i);
                var pts = Guard(g, new List<GeoPt> { pt }, "dip", 0, m);
                if (pts.Count == 0) continue;   // land / shallow / air defence: the next node
                CancelRoute(g, m.Uid);
                string derr = _game.Dip(m.Uid, pts);
                if (derr != null) { pl.DipVisited.Remove(i); DipRefused(g, m, derr); return; }
                g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = "dip", Count = pts.Count, DoneAt = _now + AswRouteGuard.JobSeconds(FromOf(m, pts), pts, AircraftKn, g.P.DipHoldS) };
                _game.Log("asw route " + g.Name + " uid=" + I(m.Uid) + " tactic=dip pts=1 ring=" + I(pl.DipNodes[i].Ring));
                return;
            }
        }

        // ---- MAD strips from the centre ------------------------------------------------------------------------------

        private void AreaMad(Grp g, AswMember m)
        {
            var pl = g.Plan;
            var holders = HoldersOf(g, "mad");
            var sb = new System.Text.StringBuilder();
            foreach (var h in holders) sb.Append(h.Uid).Append(',');
            if (pl.MadSig != sb.ToString())
            {
                pl.MadSig = sb.ToString();
                var w = new List<double>(); foreach (var h in holders) w.Add(AswPatternMath.Weight(SpeedOf(h), AswMath.RemainS(h, null)));
                List<int> order;
                var strips = AswPatternMath.MadStripsFromCentre(pl.AreaNorth, pl.AreaEast, w, g.P.MadLegNm, out order);
                // strips are handed out from the centre outward in the order of the holders; the creeping line of a strip is laid along the short side
                var oldIdx = new Dictionary<int, int>(pl.MadIdx);
                pl.MadPts.Clear(); pl.MadIdx.Clear();
                bool northLong = pl.AreaNorth >= pl.AreaEast;
                for (int k = 0; k < order.Count && k < holders.Count; k++)
                {
                    var st = strips[order[k]];
                    double len = st[1] - st[0], mid = (st[0] + st[1]) / 2;
                    var loc = northLong ? AswMath.CreepingLine(len, pl.AreaEast, g.P.MadLegNm) : AswMath.CreepingLine(pl.AreaNorth, len, g.P.MadLegNm);
                    var geo = new List<GeoPt>();
                    foreach (var q in loc) geo.Add(GeoOf(g, northLong ? new NmPt(q.X + mid, q.Y) : new NmPt(q.X, q.Y + mid)));
                    pl.MadPts[holders[k].Uid] = geo; pl.MadIdx[holders[k].Uid] = 0;
                }
            }
            List<GeoPt> all; int idx;
            if (!pl.MadPts.TryGetValue(m.Uid, out all) || !pl.MadIdx.TryGetValue(m.Uid, out idx) || idx >= all.Count) return;
            var raw = all.GetRange(idx, Math.Min(6, all.Count - idx));
            var pts = Guard(g, raw, "fly", 0, m);
            pl.MadIdx[m.Uid] = idx + raw.Count;
            if (pts.Count == 0) return;
            string gerr = _game.Goto(m.Uid, pts, 100);
            if (gerr != null) { pl.MadIdx[m.Uid] = idx; Warn(g, "mad_failed", gerr); PauseJob(g, m, "mad"); return; }
            g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = "mad", Count = pts.Count, DoneAt = _now + AswRouteGuard.JobSeconds(FromOf(m, pts), pts, AircraftKn, 0) + 60 };
        }

        private void AreaTactic(Grp g, AswMember m, string tac)
        {
            if (g.Plan.Nodes == null) return;
            if (JobPending(g, m)) return;
            CheckAreaR(g);
            AreaReseed(g);
            switch (tac)
            {
                case "rows": case "cross": case "grid": AreaGrid(g, m); break;
                case "dip": AreaDip(g, m); break;
                case "mad": AreaMad(g, m); break;
            }
        }

        /// <summary>
        /// g.AreaAll / AreaDip / AreaMad = the whole geometry in route order (set once, T-174 compat); Plan.Open* = the rest of the plan for the view (T-174 W3):
        /// open buoy nodes in ring order, unvisited dipping nodes, the rest of the MAD lines. Rebuilt after every change.
        /// </summary>
        private void UpdateAreaView(Grp g)
        {
            var pl = g.Plan;
            if (pl.Nodes == null) return;
            if (g.AreaAll == null)
            {
                var all = new List<int>(); for (int i = 0; i < pl.Nodes.Count; i++) all.Add(i);
                g.AreaAll = new List<GeoPt>();
                foreach (int i in AswPatternMath.RingRoute(pl.Nodes, all, new NmPt(0, 0))) g.AreaAll.Add(GeoOf(g, pl.Nodes[i].P));
                g.AreaDip = new List<GeoPt>();
                if (pl.DipNodes != null)
                {
                    var d = new List<int>(); for (int i = 0; i < pl.DipNodes.Count; i++) d.Add(i);
                    foreach (int i in AswPatternMath.RingRoute(pl.DipNodes, d, new NmPt(0, 0))) g.AreaDip.Add(GeoOf(g, pl.DipNodes[i].P));
                }
                g.AreaMad = ToGeoList(pl.AreaC, 0, AswMath.CreepingLine(pl.AreaNorth, pl.AreaEast, g.P.MadLegNm));
                g.AreaIdx = g.AreaDipIdx = g.AreaMadIdx = 0;
                pl.ViewSig = -1;
            }
            int sig = pl.NodesDone.Count * 1000003 + pl.DipVisited.Count * 1009 + pl.MadPts.Count;
            foreach (var kv in pl.MadIdx) sig += kv.Value;
            if (sig == pl.ViewSig) return;
            pl.ViewSig = sig;
            var open = new List<int>();
            for (int i = 0; i < pl.Nodes.Count; i++) if (!pl.NodesDone.Contains(i)) open.Add(i);
            pl.OpenAll = new List<GeoPt>();
            foreach (int i in AswPatternMath.RingRoute(pl.Nodes, open, new NmPt(0, 0))) pl.OpenAll.Add(GeoOf(g, pl.Nodes[i].P));
            pl.OpenDip = new List<GeoPt>();
            if (pl.DipNodes != null)
            {
                var o2 = new List<int>();
                for (int i = 0; i < pl.DipNodes.Count; i++) if (!pl.DipVisited.Contains(i)) o2.Add(i);
                foreach (int i in AswPatternMath.RingRoute(pl.DipNodes, o2, new NmPt(0, 0))) pl.OpenDip.Add(GeoOf(g, pl.DipNodes[i].P));
            }
            pl.OpenMad = new List<GeoPt>();
            if (pl.MadPts.Count == 0) pl.OpenMad.AddRange(g.AreaMad);
            else foreach (var kv in pl.MadPts) { int ix; pl.MadIdx.TryGetValue(kv.Key, out ix); for (int i = ix; i < kv.Value.Count; i++) pl.OpenMad.Add(kv.Value[i]); }
        }
    }
}
