using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase B: the view of the window (StrikeView), built from the same state as ViewJson. Read only.
    internal sealed partial class StrikeController
    {
        public bool Ready = true;   // false while a save restore waits

        public StrikeView View()
        {
            bool bonusKnown; Bonus(out bonusKnown);
            var v = new StrikeView { Calib = _g.CalibSource, Estimate = StrikeCalib.Estimate, CalibId = StrikeCalib.CalibId, Ready = Ready, Now = _g.Now, KillEstimate = StrikeCalib.KillEstimate, KillThreshold = _p.KillThreshold, BonusKnown = bonusKnown, Ledger = LedgerOn() };
            var ships = _g.Ships() ?? new List<StrikeShip>();
            var air = _g.Aircraft() ?? new List<StrikeAircraft>();
            var deck = _g.DeckItems() ?? new List<StrikeDeckItem>();
            foreach (var a in air) if (a != null) v.Air.Add(AirRow(a));
            foreach (var it in deck) if (it != null) v.Air.Add(DeckRow(it));
            v.Air.Sort((x, y) => (x.Why == null ? 0 : 1).CompareTo(y.Why == null ? 0 : 1));
            ShipRows(v, ships);
            foreach (var g in _groups) v.Groups.Add(GroupView(g, ships, air));
            return v;
        }

        private StrikeEwRow EwRow(StrikeEwPlan e, StrikeEwState s, IList<StrikeShip> ships, IList<StrikeAircraft> air, double now)
        {
            var a = air.FirstOrDefault(x => x != null && x.Uid == e.Uid);
            var r = new StrikeEwRow
            {
                Uid = e.Uid, Name = a == null ? null : a.Name, Type = a == null || a.Ew == null ? null : a.Ew.StationName, State = s == null ? "plan" : s.State, Why = s != null && s.Why != null ? s.Why : (e.Why == "ok" ? null : e.Why),
                On = s != null && s.On, ConeDeg = e.ConeDeg, BearingDeg = e.BearingDeg, LateS = e.LateS, ShiftS = e.ShiftS, Cover = new List<int>(e.CoverIds), Uncovered = new List<int>(e.Uncovered), BandsMissing = new List<string>(e.BandsMissing),
                TOnS = e.TOn > 0 ? e.TOn - now : double.NaN,
                ClearLeftS = s != null && s.Why == "clear_wait" && s.LastThreatT >= 0 ? Math.Max(0, _p.EwReturnClearS - (now - s.LastThreatT)) : -1,
            };
            if (e.Why == "ok" || s != null)
            {
                r.Station = e.StationPt; r.Aim = e.AimPt; r.Route.AddRange(e.Route);
                if (a != null) r.DistToStationNm = AswMath.DistNm(new GeoPt(a.Lat, a.Lon), e.StationPt);
            }
            foreach (int id in e.Uncovered) { var sh = ships.FirstOrDefault(x => x != null && x.TrackId == id); if (sh != null) r.UncoveredPts.Add(new GeoPt(sh.Lat, sh.Lon)); }
            foreach (int id in e.CoverIds) { var sh = ships.FirstOrDefault(x => x != null && x.TrackId == id); if (sh != null) r.CoverPts.Add(new GeoPt(sh.Lat, sh.Lon)); }
            r.Role = e.Role ?? "ships";
            if (s != null) EwFighterRow(r, s, a);
            return r;
        }

        private StrikeAirRow AirRow(StrikeAircraft a)
        {
            var r = new StrikeAirRow { Uid = a.Uid, Type = a.Type, Name = a.Name ?? a.Type, Airborne = a.Airborne, Where = a.Airborne ? "air" : "deck", Lat = a.Lat, Lon = a.Lon, FuelPct = a.FuelFracLive ? Math.Round(a.FuelFrac * 100, 0) : -1 };
            foreach (var m in a.Missiles.Where(StrikeAirMath.IsStrikeMissile)) r.Missiles.Add((m.Name ?? m.Ammo) + " x" + I(m.Count));
            var grp = GroupOf(a.Uid);
            if (grp != null) r.Group = grp.Name;
            string role = StrikeAirMath.RoleOf(a);
            if (role == "ew") r.Ew = true;
            string why = role == "ew" ? null : (a.Ew != null && (a.Why == "ew_no_api" || a.Why == "ew_inoperable") ? a.Why : StrikeAirMath.AircraftWhy(a));
            if (why != null) { r.Why = why; return r; }
            if (grp == null)
            {
                string owner = OwnerOf == null ? null : OwnerOf(a.Uid);
                if (owner != null) r.Why = "owned:" + owner;
            }
            return r;
        }

        private StrikeAirRow DeckRow(StrikeDeckItem it)
        {
            var r = new StrikeAirRow { Uid = 0, DeckKey = it.Key, Type = it.Type, Name = it.Type, Where = "deck", ReadyInS = it.RemainS, Lat = it.Lat, Lon = it.Lon };
            foreach (var m in it.Missiles.Where(StrikeAirMath.IsStrikeMissile)) r.Missiles.Add((m.Name ?? m.Ammo) + " x" + I(m.Count));
            var holder = _groups.FirstOrDefault(x => x.DeckKeys.Contains(it.Key));
            if (holder != null) r.Group = holder.Name;
            r.Why = it.Why; r.Ew = it.Ew != null;
            if (r.Why == null && it.State == "cooldown") r.Why = "deck_not_ready";
            return r;
        }

        private void ShipRows(StrikeView v, IList<StrikeShip> ships)
        {
            var valid = StrikePlanMath.Prioritize(ships, StrikeCalib.TonBandFrac);
            double prevK = double.NaN; int prio = 0;
            foreach (var s in valid)
            {
                var row = ShipRow(s);
                row.Prio = ++prio;
                row.Need = s.Ad.Ksub > 0 ? StrikeAdMath.Need(s.Ad.Ksub, s.Ad.Ssub, _p.PkThreshold) : 1;
                row.Why = StrikePlanMath.LowPriorityRole(s.Role) ? "low_role" : (!double.IsNaN(prevK) && prevK > 0 && s.Ad.Ksub >= prevK * (1 - StrikeCalib.TonBandFrac) - 1e-12 ? "tons" : "ad");
                prevK = prevK > 0 && !double.IsNaN(prevK) && row.Why == "tons" ? prevK : s.Ad.Ksub;
                v.Ships.Add(row);
            }
            foreach (var s in ships)
            {
                if (s == null || s.Side == "friendly" || valid.Contains(s)) continue;
                var row = ShipRow(s);
                row.Excl = StrikePlanMath.Exclusion(s) ?? "not_hostile";
                v.Ships.Add(row);
            }
        }

        private StrikeShipRow ShipRow(StrikeShip s)
        {
            return new StrikeShipRow
            {
                Id = s.TrackId, ClassName = s.ClassName, Role = s.Role, Tons = s.DisplacementT, SamNm = s.SamMaxNm, Ksub = s.Ad.Ksub, Ssub = s.Ad.Ssub, AdSource = s.Ad.Source ?? "none",
                HorizonNm = s.SamMaxNm > 0 ? Math.Min(s.SamMaxNm, StrikeGeoMath.HorizonNm(s.RadarFt > 0 ? s.RadarFt : 100, 50)) : 0, Lat = s.Lat, Lon = s.Lon, Hostile = s.Hostile,
            };
        }

        /// <summary>null = the plan can be launched now; else why not: no_plan | executing | gate | launch_land | wait_no_sea.</summary>
        private string LaunchWhy(StrikeGroup g)
        {
            var tl = g.Timeline;
            if (tl != null && tl.Status == "executing") return "executing";
            if (tl == null || g.Variants == null || g.Variants.Count == 0 || g.Chosen < 0 || g.Chosen >= g.Variants.Count) return "no_plan";
            if (tl.Status != "draft" && tl.Status != "stale" && tl.Status != "armed") return "no_plan";
            foreach (string w in g.Variants[g.Chosen].Warnings)
            {
                if (w.StartsWith("gate:", StringComparison.Ordinal)) return "gate";
                if (w.StartsWith("launch_land:", StringComparison.Ordinal)) return "launch_land";
            }
            if (g.WaitWhy == "no_sea" || g.WaitWhy == "no_point") return "wait_no_sea";
            return null;
        }

        private StrikeGroupView GroupView(StrikeGroup g, IList<StrikeShip> ships, IList<StrikeAircraft> air)
        {
            double now = _g.Now;
            var gv = new StrikeGroupView
            {
                Name = g.Name, Seq = g.Seq, Status = g.Timeline == null ? "idle" : g.Timeline.Status, StaleWhy = g.Timeline == null ? null : g.Timeline.StaleWhy, WaitWhy = g.WaitWhy, Gather = g.Gather,
                Goal = g.Goal ?? "sink", Chosen = g.Chosen, Mode = g.Target.Mode, MainId = g.Target.MainId, WaitPt = g.WaitPt, WaitR = g.WaitR, DeckHeldS = DeckHeldS(g),
            };
            gv.Members.AddRange(g.Members); gv.Targets.AddRange(g.Target.Ships); gv.DeckKeys.AddRange(g.DeckKeys);
            foreach (var l in g.Left) gv.Left.Add(l.Uid.ToString(CultureInfo.InvariantCulture) + ":" + l.Why);
            var main = ships.FirstOrDefault(s => s != null && s.TrackId == g.Target.MainId);
            if (main != null) gv.TargetPt = new GeoPt(main.Lat, main.Lon);
            int idx = 0;
            foreach (var v in g.Variants ?? new List<StrikeVariant>())
            {
                var c = new StrikeCardView { Kind = v.Kind, Goal = v.Goal ?? "breach", KillEstimate = StrikeCalib.KillEstimate, Missiles = v.MissilesUsed, Targets = v.TargetsHit, T = v.T - now, FuelMin = v.Assignments.Count == 0 ? -1 : Math.Round(v.FuelMinFrac * 100, 0), Estimate = v.Estimate, Index = idx++ };
                foreach (var t in v.PerTarget) c.PerTarget.Add(new StrikeCardTarget { Id = t.TrackId, N = t.N, P = t.P, ClearedBy = t.ClearedBy, SpreadS = t.SpreadS, Name = t.ClassName, H1 = t.Goal == "sink" ? t.H1 : -1, MSink = t.Goal == "sink" ? t.MSink : -1, PSunk = t.Goal == "sink" ? t.PSunk : -1, PBreach = t.Goal == "sink" ? t.PBreach : -1, Short = t.Short, ShortBy = t.ShortBy, Stage = t.Stage, PIfAlive = t.PIfAlive });
                var mp = v.PerTarget.FirstOrDefault(t => t.TrackId == g.Target.MainId) ?? v.PerTarget.FirstOrDefault();
                c.ClearNeighbour = v.ClearNeighbour; c.TStage2 = v.TStage2 >= 0 ? v.TStage2 - now : -1;
                if (v.ClearNeighbour != 0) mp = v.PerTarget.FirstOrDefault(t => t.TrackId == g.Target.MainId && t.Stage == 2) ?? mp;
                c.MainP = mp == null ? 0 : mp.P;
                c.Red = mp == null || (v.Goal == "sink" ? v.Red || mp.P < _p.KillThreshold - 1e-9 : mp.P < _p.PkThreshold - 1e-9);
                foreach (var a in v.Assignments)
                {
                    var ac = air.FirstOrDefault(x => x != null && x.Uid == a.Uid);
                    var m = ac == null ? null : ac.Missiles.FirstOrDefault(x => x.Ammo == a.Ammo);
                    c.Carriers.Add(new StrikeCardCarrier { Uid = a.Uid, Name = ac == null ? null : ac.Name, Ammo = a.Ammo, AmmoName = m == null ? a.Ammo : (m.Name ?? m.Ammo), Salvo = a.Salvo, TargetId = a.TargetId });
                }
                foreach (var ac in air)
                {
                    if (ac == null) continue;
                    c.Names["uid:" + I(ac.Uid)] = ac.Name ?? ("#" + I(ac.Uid));
                    foreach (var mm in ac.Missiles) c.Names["ammo:" + mm.Ammo] = mm.Name ?? mm.Ammo;
                }
                c.Warnings.AddRange(v.Warnings); c.Why.AddRange(v.Why);
                foreach (var ep in v.Ew) c.Ew.Add(EwRow(ep, null, ships, air, now));
                gv.Cards.Add(c);
            }
            var tl = g.Timeline;
            gv.Exec = tl != null && (tl.Status == "executing" || tl.Status == "cancelled") ? tl.Status : (g.ExecStatus ?? "none");   // a cancelled strike is still counting what flies
            gv.Wave = g.Wave; gv.Roe = tl != null && tl.Roe != null ? tl.Roe : EffectiveRoe;
            var resSrc = tl != null && IsExecStatus(tl.Status) ? tl.Results : g.Results;
            foreach (var tr in resSrc) gv.Results.Add(new StrikeResultRow { Id = tr.TrackId, Name = tr.ClassName, State = tr.State, Fired = tr.Fired, Hits = tr.Hits, SpreadS = tr.SpreadS, Arrived = tr.Arrived, ShotDown = tr.ShotDown });
            foreach (var es in g.EwExec) if (es.Plan != null) gv.Ew.Add(EwRow(es.Plan, es, ships, air, now));
            if (gv.Ew.Count == 0 && g.Chosen >= 0 && g.Variants != null && g.Chosen < g.Variants.Count) foreach (var ep in g.Variants[g.Chosen].Ew) gv.Ew.Add(EwRow(ep, null, ships, air, now));
            foreach (var o in g.DeckOrders)
                gv.DeckOrders.Add(new StrikeDeckOrderRow { Key = o.Key, DeckName = o.DeckName, Type = o.Type, Loadout = o.Loadout, Display = o.Display, Ammo = o.Ammo, State = o.State, Err = o.Err, Missiles = o.Missiles, ReadyS = Math.Max(0, o.OrderedT + o.ReadyS - now), Live = o.Live });
            gv.DeckBlock = g.DeckBlock;
            if (g.Deck != null)
            {
                var d = g.Deck;
                gv.HasDeckPlan = d.Kind != null; gv.DeckKind = d.Kind; gv.ShortAfterAll = d.ShortAfterAll; gv.DeckAircraft = d.Aircraft; gv.DeckMissiles = d.Missiles; gv.DeckLastReadyS = d.LastReadyS;
                gv.NowN = d.Now.N; gv.NowP = d.Now.P; gv.NowOk = d.Now.Ok; gv.WithN = d.With.N; gv.WithP = d.With.P; gv.WithOk = d.With.Ok; gv.AllN = d.All.N; gv.AllP = d.All.P; gv.AllOk = d.All.Ok;
                gv.DeckStock.AddRange(d.StockRows); gv.DeckRejected.AddRange(d.Rejected); gv.DeckNotes.AddRange(d.Notes);
            }
            foreach (var t in g.FighterThreats)
            {
                var jam = g.EwExec.FirstOrDefault(x => x.JamKind == "object" && (x.FighterId == t.F.TrackId || x.FighterCover.Contains(t.F.TrackId)));
                gv.FighterThreats.Add(new StrikeFighterRow { Id = t.F.TrackId, Cls = t.F.ClassName, Pt = new GeoPt(t.F.Lat, t.F.Lon), RNm = t.F.RNm, DistNm = t.DistNm, EtaS = double.IsInfinity(t.EtaS) ? -1 : t.EtaS, IrOnly = t.F.IrOnly, JammedBy = jam == null ? 0 : jam.Uid });
            }
            gv.FighterUncovered.AddRange(g.FighterUncovered); gv.EwNotes.AddRange(g.EwNotes);
            gv.AutoOn = g.Auto.On; gv.AutoWave = g.Auto.Waves; gv.AutoMax = _p.AutoWaves; gv.AutoState = g.Auto.State; gv.AutoWhy = g.Auto.Why; gv.AutoStopWhy = g.Auto.StopWhy; gv.AutoNotes.AddRange(g.Auto.Notes);
            if (g.Clear != null)
            {
                var cl = g.Clear;
                gv.ClearStage = cl.Stage; gv.ClearHow = cl.How; gv.ClearNeighbour = cl.NeighbourId; gv.ClearMain = cl.MainId; gv.ClearRewaves = cl.Rewaves; gv.ClearWaitS = Math.Max(0, now - cl.WaitStartT);
                gv.ClearTimeoutS = cl.TimeoutT >= 0 ? Math.Max(0, cl.TimeoutT - now) : -1; gv.ClearFuelS = cl.FuelT >= 0 ? Math.Max(0, cl.FuelT - now) : -1;
                var nb = ships.FirstOrDefault(x => x != null && x.TrackId == cl.NeighbourId);
                gv.ClearName = nb == null ? null : nb.ClassName;
            }
            gv.LaunchWhy = LaunchWhy(g);
            gv.CanLaunch = gv.LaunchWhy == null;
            double lead0 = StrikeTotMath.TurnLeadS(0, _p);
            IList<StrikeExecRow> rows = tl != null && tl.Exec.Count > 0 ? tl.Exec : (tl == null ? g.LastExec : null);
            if (rows != null && rows.Count > 0)
            {
                foreach (var r in rows.OrderBy(x => x.Uid).ThenBy(x => x.Order))
                {
                    var ac = air.FirstOrDefault(x => x != null && x.Uid == r.Uid);
                    gv.Timeline.Add(new StrikeTimelineRow
                    {
                        Uid = r.Uid, Name = ac == null ? null : ac.Name, Ammo = r.Ammo, Salvo = r.Salvo, Target = r.TargetId, State = r.State, Why = r.Why, TGo = r.TGo - now, TIssue = r.TLaunch - lead0 - now, TLaunch = r.TLaunch - now, TArrive = r.TArrive - now,
                        FactIssue = r.IssuedT < 0 ? -1 : r.IssuedT - now, FactLaunch = r.LaunchedT < 0 ? -1 : r.LaunchedT - now, LateS = r.LateS, DtOrderS = r.DtOrderS, InSamNm = r.InSamNm, InSamS = r.InSamS, UnderHorizon = r.UnderHorizon,
                    });
                    if (!StrikeExecMath.Terminal(r.State) || r.State == "issued")
                        gv.Routes.Add(new StrikeRoutePrim { Uid = r.Uid, TargetId = r.TargetId, Descent = r.DescentAtStart ? (GeoPt?)null : r.Descent, Gate = r.Gate, Launch = r.Launch, InSamNm = r.InSamNm, InSamS = r.InSamS, UnderHorizon = r.UnderHorizon });
                }
                foreach (var r in rows) gv.Launches.Add(new StrikeLaunchView { Uid = r.Uid, Name = null, Pt = r.Launch, TargetId = r.TargetId });
            }
            if (g.Chosen >= 0 && g.Chosen < (g.Variants == null ? 0 : g.Variants.Count))
            {
                var v = g.Variants[g.Chosen];
                gv.AxisDeg = v.AxisDeg;
                if (gv.Timeline.Count == 0 || (tl != null && tl.Status != "executing"))
                {
                    gv.Timeline.Clear(); gv.Launches.Clear(); gv.Routes.Clear();
                    foreach (var a in v.Assignments.OrderBy(x => x.Uid).ThenBy(x => x.Order))
                    {
                        var ac = air.FirstOrDefault(x => x != null && x.Uid == a.Uid);
                        gv.Timeline.Add(new StrikeTimelineRow
                        {
                            Uid = a.Uid, Name = ac == null ? null : ac.Name, Ammo = a.Ammo, Salvo = a.Salvo, Target = a.TargetId, TGo = a.TGo - now, TIssue = a.TIssue - now, TLaunch = a.TLaunch - now, TArrive = a.TArrive - now,
                            InSamNm = a.InSamNm, InSamS = a.InSamS, UnderHorizon = a.UnderHorizon,
                        });
                        gv.Launches.Add(new StrikeLaunchView { Uid = a.Uid, Name = ac == null ? null : ac.Name, Pt = a.LaunchPt, TargetId = a.TargetId, Stage = a.Stage });
                        gv.Routes.Add(new StrikeRoutePrim { Uid = a.Uid, TargetId = a.TargetId, Stage = a.Stage, Descent = a.DescentAtStart ? (GeoPt?)null : a.DescentPt, Gate = a.GatePt, Launch = a.LaunchPt, InSamNm = a.InSamNm, InSamS = a.InSamS, UnderHorizon = a.UnderHorizon });
                    }
                }
            }
            return gv;
        }
    }
}
