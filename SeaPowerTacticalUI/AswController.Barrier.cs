using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace VoiceBotBridge
{
    // T-229 / ADR-0094 (refines ADR-0076 s.3.3): the sliding barrier. The band is fixed on the ground at the place and axis of the ship when it was started; N rows stand ahead of the ship
    // (row k at (k+1)*I from the band origin; odd rows are staggered). When the ship reaches the nearest row it is passed and the next one is added behind the farthest. A steady turn
    // (BarrierResetDeg held for BarrierResetHoldS) or leaving the band sideways drops the plan and builds a new band. Buoys already in the water always live out their life.
    internal sealed partial class AswController
    {
        /// <summary>The band is started at the ship: origin, axis, and the first N rows.</summary>
        private void StartBand(Grp g, AswTarget t, double r)
        {
            var band = g.Plan.Band;
            // T-238 / ADR-0098: the band goes along the axis by the player's route (UpdateBandAxis). No route (src=course, ADR-0094 s.1): the axis of the track lags behind a turn
            // (it is the chord of the last minutes), so a new band goes along the heading when the ship moves
            double axis = g.BandAxisSet && g.BandAxisSrc != "course" ? g.BandAxis : t.SpeedKn >= _defaults.AxisMinKn ? AswMath.Norm360(t.CourseDeg) : g.RawAxis;
            band.Id = ++_bandIds;
            band.Set = true; band.O = Pos(t); band.AxisDeg = axis; band.T0 = _now; band.DevSinceS = 0; band.NextSeq = 0; band.Rows.Clear();
            FillBand(g, r);
        }

        /// <summary>Rows ahead are added until there are N of them.</summary>
        private void FillBand(Grp g, double r)
        {
            var band = g.Plan.Band; var spec = g.Plan.Course;
            int ahead = 0; foreach (var b in band.Rows) if (!b.Passed) ahead++;
            for (; ahead < Math.Max(1, spec.BarrierRows); ahead++) AddRow(g, r);
        }

        private void AddRow(Grp g, double r)
        {
            var band = g.Plan.Band; var spec = g.Plan.Course;
            int seq = band.NextSeq++;
            double x = AswPatternMath.BarrierRowX(spec.BarrierGapNm, spec.BarrierGapNm, seq), step;
            var ys = AswPatternMath.BarrierRowYs(spec.BarrierXNm, r, seq, _defaults.Pat.BarrierStagger, out step);
            var rec = new BarrierRec { Seq = seq, XNm = x, AxisDeg = band.AxisDeg, LaidS = _now, Buoys = ys.Count, Center = AswMath.ToGeo(band.O, band.AxisDeg, new NmPt(x, 0)) };
            double half = 0;
            foreach (double y in ys) { half = Math.Max(half, Math.Abs(y)); rec.Open.Add(AswMath.ToGeo(band.O, band.AxisDeg, new NmPt(x, y))); }
            rec.HalfNm = half > 0 ? half : spec.BarrierXNm / 2;
            band.Rows.Add(rec);
            _game.Log("asw pattern " + g.Name + " barrier row seq=" + I(seq) + " x=" + N(Math.Round(x, 1)) + " buoys=" + I(ys.Count) + " step=" + N(Math.Round(step, 2)) + " X=" + N(spec.BarrierXNm) + " I=" + N(spec.BarrierGapNm));
        }

        /// <summary>
        /// T-238 / ADR-0098: once per course tick, g.BandAxis from the player's route (held while the mod steers the ship). Logs "barrier axis=" on a source change or a shift over 5 degrees.
        /// </summary>
        private void UpdateBandAxis(Grp g, AswTarget t)
        {
            bool have = g.BandAxisSet;
            double prev = g.BandAxis;
            if (!have && g.Plan.Band.Set) { have = true; prev = g.Plan.Band.AxisDeg; }   // a band restored from a save keeps its own axis as the held one
            if (g.HoldAxis.HasValue) { have = true; prev = g.HoldAxis.Value; }   // T-251: the held one is the axis of the save, not the evasion point
            string src;
            double axis = AswRouteGuard.BandAxis(t.RouteBrg, t.ModSteered, have, prev, t.SavedCourseDeg, g.RawAxis, out src);
            if (g.HoldAxis.HasValue)
            {
                string why;
                double since = g.HoldSinceS;
                if (AswRouteGuard.LoadHold(g.HoldAxis.Value, axis, t.ModSteered, _now, _defaults.Pat.BarrierResetDeg, _defaults.Pat.BarrierResetHoldS, ref since, out why))
                { axis = g.HoldAxis.Value; src = "save"; g.HoldSinceS = since; }
                else
                {
                    g.HoldAxis = null; g.HoldSinceS = 0;
                    if (why == "confirmed" && g.Plan.Band.Set) g.Plan.Band.DevSinceS = since;   // the deviation already lasted holdS: the reset follows at once
                    _game.Log("asw pattern " + g.Name + " barrier hold end reason=" + why + " axis=" + N(Math.Round(axis)));
                }
            }
            g.BandAxis = axis; g.BandAxisSet = true;
            if (src != g.BandAxisSrc || AswMath.AngleDiff(axis, g.BandAxisLogged) > 5)
            {
                g.BandAxisSrc = src; g.BandAxisLogged = axis;
                _game.Log("asw pattern " + g.Name + " barrier axis=" + N(Math.Round(axis)) + " src=" + src);
            }
        }

        /// <summary>
        /// Once per course tick (after UpdateFrame): starts the band, resets it on a steady turn / leaving the band / a stop, marks the rows the ship has reached as passed and adds new ones,
        /// forgets the passed rows left behind. A fixed target (the ring) has no band.
        /// </summary>
        private void UpdateBand(Grp g, AswTarget t)
        {
            var pl = g.Plan; var band = pl.Band; var spec = pl.Course; var pat = _defaults.Pat;
            if (!g.Tactics.Contains("barrier") || !pl.Anchor.Set) { g.BandAxisSet = false; g.HoldAxis = null; return; }
            if (g.Frozen) return;   // T-266 / ADR-0106: an immobile ship holds the band (axis, rows); the passed rows are not marked while it drifts
            if (pl.Anchor.Stand)
            {
                // T-274 / ADR-0110: a stopped ship whose telegraph asks for way keeps the band until no_way freezes it (<= StandDeferMaxS)
                bool timedOut; double since = g.StandSinceS;
                bool keep = AswRouteGuard.StandDefer(band.Set, t.WayPending, _now, AswRouteGuard.StandDeferMaxS, ref since, out timedOut);
                g.StandSinceS = since;
                if (keep) return;
                if (timedOut) _game.Log("asw pattern " + g.Name + " barrier stand defer end reason=timeout s=" + N(Math.Round(_now - since)));
                g.BandAxisSet = false; g.HoldAxis = null; if (band.Set) ResetBand(g, "stand"); return;
            }
            g.StandSinceS = -1;
            UpdateBandAxis(g, t);
            string src;
            double r = GroupR(g, out src);
            var pos = Pos(t);
            if (!band.Set) { StartBand(g, t, r); return; }
            var loc = AswMath.ToLocal(band.O, band.AxisDeg, pos);
            string why = AswPatternMath.BarrierReset(band.AxisDeg, g.BandAxis, loc.Y, spec.BarrierXNm / 2, r, _now, pat.BarrierResetDeg, pat.BarrierResetHoldS, ref band.DevSinceS);
            if (why != null && _now - band.T0 >= _defaults.ReplanMinS)
            {
                ResetBand(g, why);
                StartBand(g, t, r);
                return;
            }
            bool changed = false;
            foreach (var row in band.Rows)
            {
                // T-334 / ADR-0130: less than the margin to the row and points still open -> nothing can be laid in time any more: they are closed, not left to be laid behind the ship
                if (!row.Passed && row.Open.Count > 0 && row.XNm - loc.X < AswPatternMath.AheadMarginNm)
                {
                    int n = row.Open.Count;
                    row.Skipped += n; row.Open.Clear(); g.NextCrossAt = 0;
                    Warn(g, "barrier_late", "row " + I(row.Seq) + ": " + I(n) + "/" + I(row.Buoys), 600);
                    _game.Log("asw pattern " + g.Name + " barrier row seq=" + I(row.Seq) + " closed skipped=" + I(row.Skipped));
                }
                if (row.Passed || !AswPatternMath.BarrierReached(loc.X, row.XNm)) continue;
                row.Passed = true; changed = true;
                int unlaid = row.Open.Count;
                row.Open.Clear();
                if (unlaid > 0) Warn(g, "barrier_late", "row " + I(row.Seq) + ": " + I(unlaid) + "/" + I(row.Buoys), 600);
                _game.Log("asw pattern " + g.Name + " barrier row seq=" + I(row.Seq) + " passed unlaid=" + I(unlaid));
                // an aircraft still flying to the passed row gets the next one: its route goes, the points it did not drop are not returned (the row is passed)
                foreach (int u in new List<int>(g.Jobs.Keys))
                {
                    Job j = g.Jobs[u];
                    if (j.BarrierSeq != row.Seq || !j.IsDrop || _now >= j.DoneAt) continue;
                    CancelRoute(g, u); g.Jobs.Remove(u);
                }
            }
            if (changed) { FillBand(g, r); g.NextCrossAt = 0; }
            for (int i = band.Rows.Count - 1; i >= 0; i--)
                if (band.Rows[i].Passed && band.Rows[i].XNm < loc.X - r) band.Rows.RemoveAt(i);
        }

        /// <summary>The plan of the band is dropped (the rows leave the plan and the map); the buoys in the water stay in the group's list and live on. Routes of the barrier holders are cancelled.</summary>
        private void ResetBand(Grp g, string why)
        {
            var band = g.Plan.Band;
            int rows = 0; foreach (var b in band.Rows) if (!b.Passed) rows++;
            _game.Log("asw pattern " + g.Name + " barrier reset reason=" + why + " rows=" + I(rows));
            band.Set = false; band.Rows.Clear(); band.DevSinceS = 0; band.NextSeq = 0; g.HoldAxis = null;
            _planLines.Remove(g);
            foreach (int u in new List<int>(g.Members))
            {
                AswMember am; string tac;
                if (!_live.TryGetValue(u, out am) || am.Where != "air" || !g.Assign.TryGetValue(u, out tac) || tac != "barrier") continue;
                CancelRoute(g, u); g.Jobs.Remove(u);
            }
            g.NextCrossAt = 0;
        }

        /// <summary>Test / dev: buoys of the group's list (the buoys of a reset band stay there and live on).</summary>
        internal int GroupBuoys(string group) { var g = Find(group); return g == null ? 0 : g.Buoys.Count; }
        /// <summary>T-238 test / dev: the axis of the band, -1 = no band.</summary>
        internal double BandAxisOf(string group) { var g = Find(group); return g == null || !g.Plan.Band.Set ? -1 : g.Plan.Band.AxisDeg; }
        /// <summary>Test / dev: the start time of the band (shifted by a load), -1 = no band.</summary>
        internal double BandT0(string group) { var g = Find(group); return g == null || !g.Plan.Band.Set ? -1 : g.Plan.Band.T0; }
        /// <summary>Test: the route of an aircraft is cancelled as the controller does it (the undropped points go back to their row).</summary>
        internal void CancelRouteOf(string group, int uid) { var g = Find(group); if (g == null) return; CancelRoute(g, uid); g.Jobs.Remove(uid); }

        /// <summary>Rows of the plan ahead of the ship (not passed), nearest first.</summary>
        private List<BarrierRec> RowsAhead(Grp g)
        {
            var res = new List<BarrierRec>();
            foreach (var b in g.Plan.Band.Rows) if (!b.Passed) res.Add(b);
            res.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            return res;
        }

        /// <summary>The row the aircraft lays next: the lowest Seq with open points (the nearest first, Q-T229-3). Null = none (all laid, or the ship is too far from the row for the life of the buoy).</summary>
        private BarrierRec NextRowFor(Grp g, AswMember m, double xShip, double life, double fly, out bool lifeShort)
        {
            lifeShort = false;
            foreach (var row in RowsAhead(g))
            {
                if (row.Open.Count == 0) continue;
                if (!AswPatternMath.BarrierLifeOk(row.XNm - xShip, AnchorKn(g.Plan.Anchor), life, fly)) { lifeShort = true; return null; }
                return row;
            }
            return null;
        }

        /// <summary>
        /// The part of the row this aircraft takes, in flight order (from its nearer end across the row): the buoys of one aircraft are enough -> the whole row; else the row is shared by the
        /// weights of the holders. Null / empty = nothing to take. fromLeft = the first point of the row is the first one of the path.
        /// </summary>
        private List<GeoPt> BarrierTake(Grp g, AswMember m, BarrierRec row, int avail, double d, int limit, out bool fromLeft)
        {
            fromLeft = true;
            var holders = HoldersOf(g, "barrier");
            double wAll = 0, wMe = 0;
            foreach (var h in holders)
            {
                if (row.Taken.Contains(h.Uid) && h.Uid != m.Uid) continue;
                double w = AswPatternMath.Weight(SpeedOf(h), AswMath.RemainS(h, null));
                wAll += w; if (h.Uid == m.Uid) wMe = w;
            }
            int rest = row.Open.Count;
            int share = wAll > 0 && wMe > 0 ? (int)Math.Ceiling(rest * wMe / wAll - 1e-9) : rest;
            int cap = AswPatternMath.Capacity(avail, SpeedOf(m), AswMath.RemainS(m, null), d);
            int n = Math.Min(Math.Min(share, avail), limit > 0 ? limit : Math.Max(cap, 1));
            if (avail >= rest && cap >= rest) n = Math.Max(n, rest);   // the whole row fits into this aircraft
            if (n <= 0) return null;
            n = Math.Min(n, rest);
            fromLeft = m.Lat == 0 && m.Lon == 0 || AswMath.DistNm(new GeoPt(m.Lat, m.Lon), row.Open[0]) <= AswMath.DistNm(new GeoPt(m.Lat, m.Lon), row.Open[rest - 1]);
            var take = new List<GeoPt>();
            if (fromLeft) take.AddRange(row.Open.GetRange(0, n)); else { for (int i = rest - 1; i >= rest - n; i--) take.Add(row.Open[i]); }
            return take;
        }

        private void LayBarrier(Grp g, AswMember m, AswTarget t, int limit = 0, string label = "barrier")
        {
            if (limit == 0 && JobPending(g, m)) return;
            if (limit == 0 && _now < g.NextCrossAt) return;
            var pl = g.Plan; var spec = pl.Course; var a = pl.Anchor; var band = pl.Band;
            string ammo; BuoyType bt;
            if (!PickFor(m, out ammo, out bt)) { Warn(g, "buoys_low", m.Name); return; }
            string src;
            double r = GroupR(g, out src), d = 1.6 * r;
            int avail = Math.Min(AvailFor(m, bt), AswMath.MaxDropPoints);
            if (avail <= 0) { Warn(g, "buoys_low", m.Name); return; }
            if (a.Stand) { LayBarrierRing(g, m, ammo, bt, r, avail); return; }
            if (!band.Set || t == null) return;
            double v = AnchorKn(a), fly = FlyToLeadS(g, m), life = bt.LifeS > 0 ? bt.LifeS : 28800;
            double xShip = AswMath.ToLocal(band.O, band.AxisDeg, Pos(t)).X;
            bool lifeShort = false;
            // T-334 / ADR-0130: the nearest row the aircraft can lay in time (at least half of the points it takes are still ahead of the ship when it drops them); else the next row;
            // none passes -> the row with the most points in time (the rest stays open for another aircraft, or is closed when the ship gets near)
            BarrierRec row = null, fbRow = null;
            List<GeoPt> take = null, fbTake = null; List<int> keepIdx = null, fbIdx = null; bool fromLeft = false, fbLeft = false; double xs = 0, fbX = 0;
            double fbSlack = 0, rowSlack = 0;
            foreach (var cand in RowsAhead(g))
            {
                if (cand.Open.Count == 0) continue;
                if (!AswPatternMath.BarrierLifeOk(cand.XNm - xShip, AnchorKn(a), life, fly)) { lifeShort = true; break; }
                bool left0;
                var tk = BarrierTake(g, m, cand, avail, d, limit, out left0);
                if (tk == null || tk.Count == 0) continue;
                double x1;
                var kept = AheadFilter(g, m, t, band.O, band.AxisDeg, tk, out x1);
                double ms = _lastMinSlack;
                if (AswPatternMath.RowTakeOk(kept.Count, tk.Count)) { row = cand; take = tk; keepIdx = kept; fromLeft = left0; xs = x1; rowSlack = ms; break; }
                if (kept.Count > 0 && (fbIdx == null || kept.Count > fbIdx.Count)) { fbRow = cand; fbTake = tk; fbIdx = kept; fbLeft = left0; fbX = x1; fbSlack = ms; }
            }
            if (row == null && fbRow != null) { row = fbRow; take = fbTake; keepIdx = fbIdx; fromLeft = fbLeft; xs = fbX; rowSlack = fbSlack; }
            if (row == null)
            {
                if (lifeShort) Warn(g, "life_short", N(Math.Round(spec.BarrierRows * spec.BarrierGapNm, 1)), 600);
                g.NextCrossAt = _now + 10;
                return;
            }
            WarnRowsShort(g);
            _lastMinSlack = rowSlack;
            LogAhead(g, m, keepIdx.Count, take.Count, xs);
            int rest = row.Open.Count;
            var kp = new List<GeoPt>(); foreach (int i in keepIdx) kp.Add(take[i]);
            // the points that were not kept stay in the row: the row keeps what is not in the package
            var taken = new HashSet<int>();
            foreach (int i in keepIdx) taken.Add(fromLeft ? i : rest - 1 - i);
            var left = new List<GeoPt>();
            for (int i = 0; i < rest; i++) if (!taken.Contains(i)) left.Add(row.Open[i]);
            take = kp;
            var was = row.Open; var wasTaken = new HashSet<int>(row.Taken);
            row.Open = left; row.Taken.Add(m.Uid);
            bool ok = IssueDrop(g, m, ammo, bt, take, label, () => { row.Open = was; row.Taken.Clear(); foreach (int u in wasTaken) row.Taken.Add(u); }, "barrier_line", GapFor(g, bt), true, 0.9 * Math.Max(0.5, d));
            if (ok)
            {
                if (g.Lines.Count > 0) { var tag = g.Lines[g.Lines.Count - 1]; tag.BandId = band.Id; tag.Seq = row.Seq; }   // T-322
                Job job;
                if (g.Jobs.TryGetValue(m.Uid, out job)) job.BarrierSeq = row.Seq;
                if (_defaults.Pat.BarrierActive && m.ActiveBuoys > 0) IssueActive(g, m, row.Center, "barrier");
            }
            bool anyOpen = false; foreach (var rw in RowsAhead(g)) if (rw.Open.Count > 0) anyOpen = true;
            if (!anyOpen) g.NextCrossAt = _now + 10;   // every row ahead is given to an aircraft: look again a little later
        }

        /// <summary>Fewer buoys in the group than the rows ahead need: the nearest rows are laid first (Q-T229-5), the warning says how many rows the buoys cover.</summary>
        private void WarnRowsShort(Grp g)
        {
            int buoys = 0;
            foreach (int u in g.Members) { AswMember am; if (_live.TryGetValue(u, out am) && am.Where == "air") buoys += BuoyCount(am); }
            int need = 0, rows = 0, covered = 0;
            bool fits = true;
            foreach (var row in RowsAhead(g))
            {
                if (row.Open.Count == 0) continue;
                rows++; need += row.Open.Count;
                if (fits && need <= buoys) covered++; else fits = false;
            }
            if (rows > 1 && covered >= 1 && covered < rows) Warn(g, "barrier_rows_short", I(covered) + "/" + I(rows), 600);
        }

        /// <summary>A fixed target: the ring of radius X/2 around it (points every d), the nodes a live buoy covers are skipped.</summary>
        private void LayBarrierRing(Grp g, AswMember m, string ammo, BuoyType bt, double r, int avail)
        {
            var spec = g.Plan.Course; var a = g.Plan.Anchor;
            double d = 1.6 * r;
            var ring = AswPatternMath.StandingRing(Math.Max(spec.BarrierXNm / 2, d), d);
            var pts = new List<GeoPt>();
            foreach (var q in ring) { var p = AswMath.ToGeo(a.O, 0, q); if (!CoveredAt(g, p, r, 0, 0.9 * d)) pts.Add(p); if (pts.Count >= avail) break; }
            if (pts.Count == 0) { g.NextCrossAt = _now + 30; return; }
            if (IssueDrop(g, m, ammo, bt, pts, "barrier", null, "barrier_line", GapFor(g, bt), true, 0.9 * d) && g.Lines.Count > 0)
            {
                var tag = g.Lines[g.Lines.Count - 1]; tag.BandId = -2; tag.Seq = ++_ringDrops;   // T-322
            }
        }

    

        // ---- T-322 / ADR-0126: stale barrier lines (view only: the plan, the window, the save and g.Lines stay as they are) ------------------------

        private int _bandIds, _ringDrops;
        private sealed class StaleMem { public double Since = -1, LiveAt; public bool Seen, Dropped; }   // Dropped (T-335): a buoy of this line was really in the water once
        private sealed class StaleRow { public string Key, Why; public float Alpha; }
        private readonly Dictionary<string, StaleMem> _stale = new Dictionary<string, StaleMem>();   // "<group>|<key>"
        private readonly Dictionary<Grp, List<StaleRow>> _staleLast = new Dictionary<Grp, List<StaleRow>>();
        private readonly HashSet<BuoyRec> _buoyGone = new HashSet<BuoyRec>();
        private readonly Dictionary<BuoyRec, int> _buoyMiss = new Dictionary<BuoyRec, int>();
        private List<AswBuoyObs> _ownObs = new List<AswBuoyObs>(); private double _goneReadAt = -1e9;

        private static string StaleKey(AswLineView l, bool drop)
        {
            if (l.BandId == -1 && l.Seq == -1) return "u" + RuntimeHelpers.GetHashCode(l).ToString(CultureInfo.InvariantCulture);
            string k = "b" + l.BandId.ToString(CultureInfo.InvariantCulture) + ":" + l.Seq.ToString(CultureInfo.InvariantCulture);
            return drop ? k + "@" + RuntimeHelpers.GetHashCode(l).ToString(CultureInfo.InvariantCulture) : k;
        }

        /// <summary>Called from PlanView: non-barrier lines pass through; a stale barrier line becomes a faded copy; a fully faded one is left out. Logs stale/fresh once per key.</summary>
        private List<AswLineView> FadeStale(Grp g, List<AswLineView> lines)
        {
            var rows = new List<StaleRow>();
            _staleLast[g] = rows;
            bool any = false;
            foreach (var l in lines) if (l.Kind == "barrier_line") { any = true; break; }
            string prefix = g.Name + "|";
            if (!any)
            {
                DropStaleMem(prefix, null);
                return lines;
            }
            ReadBuoysGone();
            var band = g.Plan.Band;
            bool barrier = g.Order == "course" && g.Tactics.Contains("barrier");
            var res = new List<AswLineView>(lines.Count);
            var seen = new HashSet<string>();
            foreach (var l in lines)
            {
                if (l.Kind != "barrier_line") { res.Add(l); continue; }
                bool drop = g.Lines.Contains(l);
                string key = StaleKey(l, drop);
                bool done = l.State == "done";
                var f = new AswBarrierFacts { Barrier = barrier, Laid = done };
                BarrierRec row = null;
                if (l.BandId == -1 && l.Seq == -1) f.InPlan = true;   // a draft row (no band yet): always a plan
                else if (l.BandId == -2) f.InPlan = g.Plan.Anchor.Stand;
                else
                {
                    f.InPlan = band.Set && l.BandId == band.Id;
                    if (f.InPlan) { foreach (var b in band.Rows) if (b.Seq == l.Seq) row = b; f.Passed = row == null || row.Passed || row.Skipped >= row.Buoys; }
                }
                StaleMem mem;
                if (!_stale.TryGetValue(prefix + key, out mem)) { mem = new StaleMem { LiveAt = _now }; _stale[prefix + key] = mem; }
                mem.Seen = true; seen.Add(key);
                if (done && l.Points.Count > 0) BuoysNear(g, l, ref f);
                // T-335 (N3): "laid" = a buoy was really dropped on the line; State "done" also holds for points deferred by a threat (nothing in the water yet)
                mem.Dropped = AswBarrierStale.LaidByFact(done, f.Matched, f.Live, mem.Dropped);
                f.Laid = mem.Dropped;
                if (f.Live > 0 || !f.Laid || _now < mem.LiveAt) mem.LiveAt = _now;
                f.NoLiveS = f.Live > 0 ? 0 : _now - mem.LiveAt;
                if (f.Laid && row != null && !f.Passed) f.TrackPassed = TrackPassedAll(g, row, band);
                string why = AswBarrierStale.Why(f);
                if (why != null)
                {
                    if (mem.Since < 0 || _now < mem.Since) { mem.Since = _now; _game.Log("asw view " + g.Name + " barrier stale key=" + key + " why=" + why); }
                }
                else if (mem.Since >= 0) { mem.Since = -1; _game.Log("asw view " + g.Name + " barrier fresh key=" + key); }
                float alpha = AswBarrierStale.Alpha(mem.Since, _now);
                rows.Add(new StaleRow { Key = key, Why = why, Alpha = alpha });
                if (why == null) res.Add(l);
                else if (alpha > 0f) res.Add(AswBarrierStale.Faded(l, alpha, why));
            }
            DropStaleMem(prefix, seen);
            return res;
        }

        private void DropStaleMem(string prefix, HashSet<string> keep)
        {
            List<string> del = null;
            foreach (var kv in _stale)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && (keep == null || !keep.Contains(kv.Key.Substring(prefix.Length)))) { if (del == null) del = new List<string>(); del.Add(kv.Key); }
            if (del != null) foreach (string k in del) _stale.Remove(k);
        }

        /// <summary>Buoys near the line: records of the group (Matched) and the live ones, own game buoys of any group count as live.</summary>
        private void BuoysNear(Grp g, AswLineView l, ref AswBarrierFacts f)
        {
            foreach (var b in g.Buoys)
            {
                if (!AswBarrierStale.NearLine(l.Points, new GeoPt(b.Lat, b.Lon), AswBarrierStale.BuoyTolNm)) continue;
                f.Matched++;
                if (!_buoyGone.Contains(b) && b.DroppedS + b.LifeS > _now) f.Live++;
            }
            foreach (var o in _ownObs)
                if (o.RemainingS != 0 && AswBarrierStale.NearLine(l.Points, new GeoPt(o.Lat, o.Lon), AswBarrierStale.BuoyTolNm)) f.Live++;
        }

        /// <summary>Every held contact has known course / speed, is not a lost ring and has crossed the row towards the ship and moves away. No contacts = false.</summary>
        private bool TrackPassedAll(Grp g, BarrierRec row, BarrierBand band)
        {
            if (g.Contacts.Count == 0) return false;
            foreach (var c in g.Contacts)
            {
                if (c.Ring) return false;
                double x = AswMath.ToLocal(band.O, band.AxisDeg, new GeoPt(c.Lat, c.Lon)).X;
                if (!AswBarrierStale.TrackPassed(row.XNm, x, c.CourseDeg, c.SpeedKn, band.AxisDeg)) return false;
            }
            return true;
        }

        /// <summary>Every GoneReadS: one OwnBuoys read; a record older than GoneMinAgeS, not in a running job, with no own game buoy (RemainingS != 0) within GoneMatchNm in 2 reads in a row = gone. Null / empty read = no information.</summary>
        private void ReadBuoysGone()
        {
            if (_now >= _goneReadAt && _now - _goneReadAt < AswBarrierStale.GoneReadS) return;
            _goneReadAt = _now;
            IList<AswBuoyObs> obs = null;
            try { obs = _game.OwnBuoys(); } catch (Exception) { }
            if (obs == null || obs.Count == 0) return;
            _ownObs = new List<AswBuoyObs>(obs);
            var alive = new HashSet<BuoyRec>();
            foreach (var g in _groups)
            {
                var running = new HashSet<BuoyRec>();
                foreach (var j in g.Jobs.Values) if (j.Recs != null && j.IsDrop) foreach (var r in j.Recs) running.Add(r);
                foreach (var b in g.Buoys)
                {
                    alive.Add(b);
                    if (_now - b.DroppedS < AswBarrierStale.GoneMinAgeS || b.DroppedS + b.LifeS <= _now || running.Contains(b)) continue;
                    bool match = false;
                    var p = new GeoPt(b.Lat, b.Lon);
                    foreach (var o in _ownObs) if (o.RemainingS != 0 && AswMath.DistNm(p, new GeoPt(o.Lat, o.Lon)) <= AswBarrierStale.GoneMatchNm) { match = true; break; }
                    int miss;
                    _buoyMiss.TryGetValue(b, out miss);
                    if (match) { _buoyMiss.Remove(b); _buoyGone.Remove(b); continue; }
                    _buoyMiss[b] = ++miss;
                    if (miss >= 2) _buoyGone.Add(b);
                }
            }
            var dead = new List<BuoyRec>();
            foreach (var b in _buoyGone) if (!alive.Contains(b)) dead.Add(b);
            foreach (var b in _buoyMiss.Keys) if (!alive.Contains(b)) dead.Add(b);
            foreach (var b in dead) { _buoyGone.Remove(b); _buoyMiss.Remove(b); }
        }

        /// <summary>Tests / dev: the barrier lines of the last view, "key:why|-:alpha F2;..." including the hidden ones.</summary>
        internal string StaleInfo(string group)
        {
            var g = Find(group); List<StaleRow> rows;
            if (g == null || !_staleLast.TryGetValue(g, out rows)) return "";
            var sb = new StringBuilder();
            foreach (var r in rows) { if (sb.Length > 0) sb.Append(';'); sb.Append(r.Key).Append(':').Append(r.Why ?? "-").Append(':').Append(r.Alpha.ToString("F2", CultureInfo.InvariantCulture)); }
            return sb.ToString();
        }

        /// <summary>Test hook (T-322): the buoy records of the group are forgotten (the buoys in the game stay); returns how many.</summary>
        internal int ForgetBuoys(string group) { var g = Find(group); if (g == null) return 0; int n = g.Buoys.Count; g.Buoys.Clear(); return n; }

        private string BarrierStaleJson(Grp g)
        {
            var sb = new StringBuilder("[");
            List<StaleRow> rows;
            if (_staleLast.TryGetValue(g, out rows))
            {
                bool first = true;
                foreach (var r in rows)
                {
                    if (r.Why == null) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(Obj("key", r.Key, "why", r.Why, "alpha", Math.Round(r.Alpha, 2)));
                }
            }
            return sb.Append(']').ToString();
        }
    }
}
