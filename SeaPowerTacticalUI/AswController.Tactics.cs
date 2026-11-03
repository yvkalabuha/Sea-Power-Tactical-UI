using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;


namespace VoiceBotBridge
{
    // T-121: part of the ASW controller (see AswController.cs).

    internal sealed partial class AswController
    {
        // ---- plan helpers ------------------------------------------------------------------------------------------

        /// <summary>For tests and diagnostics: the parameters of a group (null = no such group).</summary>
        internal AswParams ParamsOf(string group) { var g = Find(group); return g == null ? null : g.P; }

        /// <summary>Width of a formation across its course (stations + 2 nm), 0 = unknown or not a formation leader.</summary>
        private double FormationWidth(int leaderUid)
        {
            try
            {
                foreach (var o in _game.CourseTargets()) if (o.Kind == "formation" && o.Uid == leaderUid) return o.WidthNm;
            }
            catch (Exception e) { _game.Log("asw formation width error: " + e.Message); }
            return 0;
        }

        private bool PickFor(AswMember m, out string ammo, out BuoyType bt)
        {
            ammo = null; bt = null;
            var all = _game.BuoyTypes(m.Uid);
            var use = new List<BuoyType>();
            foreach (var b in all)
            {
                if (b.Vlad && m.Passive > 0) continue;
                if (m.BuoysByAmmo != null && CountOf(m, b.Ammo, b) <= 0) continue;   // T-172 B4: the very type is out (the game checks the count per type)
                use.Add(b);
                _rByAmmo[b.Ammo] = AswMath.REff(b, _defaults).Nm;
            }
            ammo = AswMath.PickBuoy(use, false, _defaults);
            if (ammo == null) return false;
            foreach (var b in use) if (b.Ammo == ammo) bt = b;
            return bt != null;
        }

        /// <summary>T-172 R2: effective range of every buoy type seen (by ammo), for the merge gap against buoys of another type.</summary>
        private readonly Dictionary<string, double> _rByAmmo = new Dictionary<string, double>();

        /// <summary>
        /// T-172 B4: how many buoys of this very type the aircraft has: the game's list by ammunition when it is known (a type missing from it = none),
        /// else the count of its kind (VLAD / active / passive).
        /// </summary>
        private int CountOf(AswMember m, string ammo, BuoyType bt)
        {
            if (m.BuoysByAmmo != null && ammo != null)
            {
                int n;
                return m.BuoysByAmmo.TryGetValue(ammo, out n) ? Math.Max(0, n) : 0;
            }
            return bt == null ? BuoyCount(m) : bt.Vlad ? m.Vlad : bt.Active ? m.ActiveBuoys : m.Passive;
        }

        private int AvailFor(AswMember m, BuoyType bt) { return CountOf(m, bt.Ammo, bt); }

        /// <summary>Buoys of the type of this package that the aircraft still has: the drop in this count since the package was given = what fell.</summary>
        private int LeftOf(AswMember m, Job j) { return j.Ammo == null ? BuoyCount(m) : CountOf(m, j.Ammo, j.Kind); }

        private double GroupR(Grp g, out string src)
        {
            double best = 0; src = "default";
            foreach (int u in g.Members)
            {
                AswMember m;
                if (!_live.TryGetValue(u, out m) || BuoyCount(m) <= 0) continue;
                string ammo; BuoyType bt;
                if (!PickFor(m, out ammo, out bt)) continue;
                var r = AswMath.REff(bt, g.P);
                if (best == 0 || r.Nm < best) { best = r.Nm; src = r.Source; }
            }
            if (best == 0) best = 2.7;
            g.RNm = best; g.RSource = src;
            return best;
        }

        private bool JobPending(Grp g, AswMember m)
        {
            Job j;
            if (!g.Jobs.TryGetValue(m.Uid, out j)) return false;
            j.Ticks++;
            int now = LeftOf(m, j);
            if (j.IsDrop && j.BuoysBefore - now >= j.Count) { g.Jobs.Remove(m.Uid); return false; }
            if (_now >= j.DoneAt)
            {
                // T-150 R1: the estimate is over, but the game may still fly the queue: the next package is laid only behind it (at most 10 min longer)
                int q = j.NoQueue ? 0 : _game.QueuedTasks(m.Uid);
                if (!(q > 0 && _now < j.DoneAt + 600)) { g.Jobs.Remove(m.Uid); return false; }
            }
            // ADR-0063: "blocked by time compression" only when the aircraft should already be over the first point (was: after 2 ticks, so every
            // package in transit was laid again every 2 s at x4 and its points piled up in the group's buoy list)
            if (j.IsDrop && j.Ticks >= 2 && _now >= j.FirstAt && _game.TimeScale > 1.0 && now >= j.BuoysBefore)
            {
                Warn(g, "tc_blocked", "x" + N(_game.TimeScale), 60);
                TrimDrop(g, m, j);
                if (j.Undo != null) j.Undo();
                g.Jobs.Remove(m.Uid);
                return false;
            }
            return true;
        }

        private List<GeoPt> ToGeoList(GeoPt origin, double axis, IList<NmPt> pts, double dx = 0)
        {
            var l = new List<GeoPt>();
            foreach (var p in pts) l.Add(AswMath.ToGeo(origin, axis, new NmPt(p.X + dx, p.Y)));
            return l;
        }

        private const double AircraftKn = 100;

        private static GeoPt FromOf(AswMember m, IList<GeoPt> pts)
        {
            return (m.Lat != 0 || m.Lon != 0) ? new GeoPt(m.Lat, m.Lon) : pts[0];
        }

        /// <summary>ADR-0063: the merge gap of a barrier pattern for this buoy type: MinGapFrac * spacing(R_eff of the type).</summary>
        private double GapFor(Grp g, BuoyType bt) { return AswMath.MinGapNm(AswMath.REff(bt, g.P).Nm); }

        /// <summary>
        /// ADR-0063: buoys that count as "already in the water" for the merge gap: the group's records still alive (dropped, or queued in a package
        /// that is still flown) and every own sonobuoy the game shows (any group, the player's own): a buoy covers the water whoever dropped it.
        /// The undropped rest of the last package of <paramref name="replacing"/> is left out: the new package of that aircraft cancels it.
        /// </summary>
        private List<AswMath.BuoyAt> LiveBuoys(Grp g, AswMember replacing, double rNew, double minRemainS = 0)
        {
            HashSet<BuoyRec> skip = null;
            Job last;
            if (replacing != null && g.LastDrop.TryGetValue(replacing.Uid, out last) && last.Recs != null)
            {
                int fell = Math.Max(0, Math.Min(last.Recs.Count, last.BuoysBefore - LeftOf(replacing, last)));
                skip = new HashSet<BuoyRec>();
                for (int i = fell; i < last.Recs.Count; i++) skip.Add(last.Recs[i]);
            }
            var l = new List<AswMath.BuoyAt>();
            foreach (var b in g.Buoys)
                if (b.LifeS - (_now - b.DroppedS) > Math.Max(0, minRemainS) && (skip == null || !skip.Contains(b))) l.Add(new AswMath.BuoyAt { P = new GeoPt(b.Lat, b.Lon), R = RangeOf(b.Ammo, rNew) });
            try
            {
                var own = _game.OwnBuoys();
                if (own != null) foreach (var b in own) if (b.RemainingS != 0 && !(b.RemainingS > 0 && b.RemainingS < minRemainS)) l.Add(new AswMath.BuoyAt { P = new GeoPt(b.Lat, b.Lon), R = RangeOf(b.Ammo, rNew) });   // T-172 R6: a buoy with no life left covers nothing
            }
            catch (Exception e) { _game.Log("asw own buoys error: " + e.Message); }
            return l;
        }

        /// <summary>T-172 R2: effective range of a buoy by its ammo; unknown = that of the new buoy (the old single-range rule).</summary>
        private double RangeOf(string ammo, double rNew)
        {
            double r;
            return ammo != null && _rByAmmo.TryGetValue(ammo, out r) ? r : rNew;
        }

        /// <summary>
        /// ADR-0063: the points of the last package of this aircraft that the game did not drop leave the group's buoy list (they are no buoys);
        /// how many fell = the drop in its buoy count since the package was given. The aircraft is gone: the records stay.
        /// </summary>
        private void TrimDrop(Grp g, AswMember m, Job j)
        {
            if (j == null || j.Recs == null) return;
            if (m != null)
            {
                int left = LeftOf(m, j);
                // the package is long over, or the aircraft was re-armed since (landed and flew again): nothing of it is queued any more
                int fell = left >= j.BuoysBefore && _now > j.DoneAt + 600 || left > j.BuoysBefore || m.Where != "air" ? j.Recs.Count : Math.Max(0, Math.Min(j.Recs.Count, j.BuoysBefore - left));
                for (int i = fell; i < j.Recs.Count; i++) g.Buoys.Remove(j.Recs[i]);
                // T-172 O1: what was cut off is laid again - a corridor / area node is open again at once (a live buoy covers it, nothing else does);
                // the cut-off buoys of a barrier line go back to the line
                if (fell < j.Recs.Count && j.BarrierSeq >= 0)
                    foreach (var row in g.Plan.Band.Rows)
                        if (row.Seq == j.BarrierSeq && !row.Passed) { for (int i = fell; i < j.Recs.Count; i++) row.Open.Add(new GeoPt(j.Recs[i].Lat, j.Recs[i].Lon)); row.Open.Sort((p, q) => AswMath.ToLocal(row.Center, row.AxisDeg, p).Y.CompareTo(AswMath.ToLocal(row.Center, row.AxisDeg, q).Y)); }
                if (fell < j.Recs.Count) g.NextCrossAt = 0;
                if (fell < j.Recs.Count) _game.Log("asw drop " + g.Name + " cancelled uid=" + I(m.Uid) + " fell=" + I(fell) + " of " + I(j.Recs.Count));
            }
            j.Recs = null;
        }

        /// <summary>The route of the aircraft is cleared (a new package, a re-plan, other tactics): the undropped points of its last package go too.</summary>
        private void CancelRoute(Grp g, int uid)
        {
            Job last;
            if (g.LastDrop.TryGetValue(uid, out last)) { AswMember m; TrimDrop(g, _live.TryGetValue(uid, out m) ? m : null, last); g.LastDrop.Remove(uid); }
            _game.ClearRoute(uid);
        }

        /// <summary>T-172 B3: an order the game refused is not repeated every tick: the aircraft waits 30 s (the undo of the caller already ran).</summary>
        private void PauseJob(Grp g, AswMember m, string tactic)
        {
            g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = tactic, Count = 0, DoneAt = _now + RefusedPauseS, NoQueue = true };
        }

        private const double RefusedPauseS = 30;

        /// <summary>A contact cross point is left out only when a live buoy is closer than this (nm) to it.</summary>
        private const double ContactDupNm = 1.0;

        private static string Pts(IList<GeoPt> pts)
        {
            var sb = new StringBuilder();
            foreach (var p in pts) { if (sb.Length > 0) sb.Append(';'); sb.Append(p.Lat.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',').Append(p.Lon.ToString("0.0000", CultureInfo.InvariantCulture)); }
            return sb.ToString();
        }

        private static string Nm2(double x) { return x >= double.MaxValue / 2 ? "-" : x.ToString("0.00", CultureInfo.InvariantCulture); }

        /// <summary>
        /// One buoy package. ADR-0063: points closer than <paramref name="gapNm"/> to a buoy in the water or to a point kept before them are left out
        /// (re-plans, a second contact at the same place and other groups never pile buoys up); land / air-defence points are left out (never moved,
        /// so nothing collapses onto one spot). Everything left out = no drop, no undo (the water is covered or cannot be reached).
        /// </summary>
        private bool IssueDrop(Grp g, AswMember m, string ammo, BuoyType bt, List<GeoPt> pts, string tactic, Action undo, string lineKind, double gapNm, bool keepOrder = false, double capGapNm = -1)
        {
            if (pts.Count == 0) return false;
            int planned = pts.Count, near;
            double rNew = bt != null ? AswMath.REff(bt, g.P).Nm : 2.7;
            var existing = LiveBuoys(g, m, rNew, keepOrder ? _defaults.Pat.ReseedLeadS : 0);   // T-176: a designed pattern ignores buoys that go out within ReseedLeadS (they are laid again)
            if (tactic == "contact")
            {
                // live 0.29.0: the cross over a fresh contact was suppressed whole by the buoys of the search field around it (near_existing=4 kept=0).
                // The cross is a fix, not a barrier: only a buoy practically on the point stops it (ContactDupNm); the points among themselves keep the gap.
                var ex = new List<GeoPt>(); foreach (var e in existing) ex.Add(e.P);
                int n1, n2;
                pts = AswMath.Thin(AswMath.Thin(pts, ex, ContactDupNm, out n1), null, gapNm, out n2);
                near = n1 + n2;
            }
            else pts = AswMath.ThinPair(pts, rNew, existing, out near, capGapNm);   // T-172 R2: the gap to a buoy of another type is 0.56 (Ra + Rb)
            if (!keepOrder && (m.Lat != 0 || m.Lon != 0)) pts = AswMath.BestOrder(new GeoPt(m.Lat, m.Lon), pts);   // shortest transit, never back over the group
            if (pts.Count > AswMath.MaxDropPoints)
            {
                // T-172 B3: the game takes at most 40 points per order (Asw.cs AswMaxPoints); the callers cap their packages, this is the last line
                _game.Log("asw drop " + g.Name + " " + tactic + " uid=" + I(m.Uid) + " package cut " + I(pts.Count) + " -> " + I(AswMath.MaxDropPoints));
                pts = pts.GetRange(0, AswMath.MaxDropPoints);
            }
            pts = Guard(g, pts, "buoy", bt != null ? bt.SonarDepthFt : 0, m, true);   // T-176 C4: land / shallow forever, hazard / air deferred
            double r = bt != null ? rNew : 0;
            _game.Log("asw drop " + g.Name + " " + tactic + " uid=" + I(m.Uid) + " ammo=" + ammo + " r_eff=" + Nm2(r) + " spacing=" + Nm2(AswMath.Spacing(r)) + " gap=" + Nm2(gapNm)
                + " planned=" + I(planned) + " near_existing=" + I(near) + " land=" + I(_skip.Land) + " shallow=" + I(_skip.Shallow) + " hazard=" + I(_skip.Hazard) + " air=" + I(_skip.Air) + " kept=" + I(pts.Count) + SkipCirclesNote()
                + " min_pair=" + Nm2(AswMath.MinPairNm(pts)) + " pts=" + Pts(pts));
            if (pts.Count == 0)
            {
                g.Jobs[m.Uid] = new Job { Uid = m.Uid, Tactic = tactic, Count = 0, DoneAt = _now + 30, NoQueue = true };   // nothing to fly: try again a little later
                return false;
            }
            CancelRoute(g, m.Uid);   // T-150 R1: the new package replaces the old route, it is not appended behind it
            string err = _game.Drop(m.Uid, ammo, pts);
            if (err != null)
            {
                Warn(g, err == "not_enough" || err == "no_sonobuoy" ? "buoys_low" : "drop_failed", err);
                if (undo != null) undo();
                PauseJob(g, m, tactic);
                return false;
            }
            var from = FromOf(m, pts);
            var job = new Job { Uid = m.Uid, Tactic = tactic, Count = pts.Count, Ammo = ammo, Kind = bt, BuoysBefore = bt != null ? CountOf(m, ammo, bt) : BuoyCount(m), DoneAt = _now + AswRouteGuard.JobSeconds(from, pts, AircraftKn, 60), IsDrop = true, Undo = undo,
                                FirstAt = _now + AswRouteGuard.JobSeconds(from, new[] { pts[0] }, AircraftKn, 0) + 60, Recs = new List<BuoyRec>() };
            g.Jobs[m.Uid] = job;
            g.LastDrop[m.Uid] = job;
            foreach (var p in pts) { var rec = new BuoyRec { Lat = p.Lat, Lon = p.Lon, DroppedS = _now, LifeS = bt != null ? bt.LifeS : 28800, Ammo = ammo }; g.Buoys.Add(rec); job.Recs.Add(rec); }
            var line = new AswLineView { Kind = lineKind, Lane = g.Members.IndexOf(m.Uid), State = "done" };
            line.Points.AddRange(pts);
            g.Lines.Add(line);
            if (g.Lines.Count > 12) g.Lines.RemoveAt(0);
            _game.Event("asw_barrier_laid", Obj("group", g.Name, "tactic", tactic, "uid", m.Uid, "aircraft", m.Name, "count", pts.Count));
            return true;
        }

        /// <summary>The by-course tactics (T-176): corridor, barrier, dipping chain. The snake of MAD by course is gone (ADR-0076 s.3.4), rows / cross are the old names of corridor / barrier.</summary>
        private void CourseTactic(Grp g, AswMember m, AswTarget t, string tac)
        {
            if (tac == "corridor" || tac == "barrier") AheadCheck(g, m, t, tac);   // T-334 / ADR-0130
            switch (tac)
            {
                case "corridor": LayCorridor(g, m, t); break;
                case "barrier": LayBarrier(g, m, t); break;
                case "dip": DipChain(g, m, t); break;
            }
        }

        /// <summary>
        /// T-172 R9: one active buoy on the way: the land / air-defence filter like every package, no other live active buoy within its merge gap,
        /// recorded in the group's buoy list (so it counts in the picture and in the merge gaps). False = nothing was ordered.
        /// </summary>
        private bool IssueActive(Grp g, AswMember m, GeoPt p, string tactic)
        {
            var types = _game.BuoyTypes(m.Uid);
            string aa = AswMath.PickBuoy(types, true, g.P);
            if (aa == null) return false;
            BuoyType bt = null;
            foreach (var b in types) if (b.Ammo == aa) bt = b;
            if (bt == null || CountOf(m, aa, bt) <= 0) return false;
            double ra = AswMath.REff(bt, g.P).Nm, gap = AswMath.MinGapNm(ra);
            _rByAmmo[aa] = ra;
            foreach (var b in g.Buoys)
                if (b.Ammo == aa && _now - b.DroppedS < b.LifeS && AswMath.DistNm(new GeoPt(b.Lat, b.Lon), p) < gap) return false;
            var pts = Guard(g, new List<GeoPt> { p });
            if (pts.Count == 0) return false;
            string err = _game.Drop(m.Uid, aa, pts);
            _game.Log("asw drop " + g.Name + " " + tactic + " uid=" + I(m.Uid) + " ammo=" + aa + " active r_eff=" + Nm2(ra) + " pts=" + Pts(pts) + " -> " + (err ?? "ok"));
            if (err != null) return false;
            g.Buoys.Add(new BuoyRec { Lat = pts[0].Lat, Lon = pts[0].Lon, DroppedS = _now, LifeS = bt.LifeS > 0 ? bt.LifeS : 28800, Ammo = aa });
            return true;
        }

        // ---- route preview (dry run, nothing goes to the game) -------------------------------------------------------

        public void ClearPreview()
        {
            if (_preview.Count == 0 && _previewUntil == 0) return;
            _preview.Clear(); _previewUntil = 0;
            RebuildView();
        }

        /// <summary>
        /// Dry run of a search order (area=false: course, true: area): builds the route the order would fly on a throwaway copy of the group
        /// frame and puts it into View().Preview for 30 s. Nothing is issued to the game, no group is created or changed.
        /// A bad command or no target gives an empty preview.
        /// </summary>
        public AswView Preview(bool area, Dictionary<string, object> c)
        {
            _preview.Clear(); _previewUntil = 0;
            try
            {
                RefreshLive();
                if (area) PreviewArea(c); else PreviewCourse(c);
            }
            catch (Exception e) { _game.Log("asw preview error: " + e.Message); _preview.Clear(); }
            if (_preview.Count > 0) _previewUntil = _game.Now + (c != null && c.ContainsKey("keep") && c["keep"] is bool && (bool)c["keep"] ? 86400.0 * 30 : PreviewS);   // keep: the window holds the preview while its route flag is on
            RebuildView();
            return _view;
        }

        /// <summary>Command wrapper for the window: {"area": bool, ...order arguments}; data JSON or "!code" like the other commands.</summary>
        public string PreviewCmd(Dictionary<string, object> c)
        {
            object o;
            bool area = c != null && c.TryGetValue("area", out o) && o is bool && (bool)o;
            var v = Preview(area, c ?? new Dictionary<string, object>());
            if (v.Preview.Count == 0) return Fail("no_route");
            return Obj("lines", v.Preview.Count, "until_s", v.PreviewUntilS);
        }

        public string PreviewClearCmd() { ClearPreview(); return Obj("cleared", true); }

        private const double PreviewS = 30;

        /// <summary>
        /// "Drop what is left" before an aircraft leaves (PlanRelief dump_buoys). T-172 B5: once per sortie, at most 6 buoys, inside the zone (the rest of the
        /// rows lattice from the cursor, or the next points of the area grid), never for an aircraft the game already sends home.
        /// </summary>
        private void DumpBuoys(Grp g, AswMember m, AswTarget t)
        {
            if (g.Dumped.Contains(m.Uid) || AswMath.IsLeaving(m) || JobPending(g, m)) return;
            string ammo; BuoyType bt;
            if (!PickFor(m, out ammo, out bt)) return;
            int n = Math.Min(AvailFor(m, bt), 6);
            if (n <= 0) return;
            g.Dumped.Add(m.Uid);
            if (g.Order == "area")
            {
                if (g.Plan.Nodes == null) return;
                AreaGrid(g, m, "dump", n);
                return;
            }
            if (t == null) return;
            string held;
            if (g.Assign.TryGetValue(m.Uid, out held) && held == "corridor") LayCorridor(g, m, t, n, "dump");
            else if (held == "barrier") LayBarrier(g, m, t, n, "dump");
        }
    }
}
