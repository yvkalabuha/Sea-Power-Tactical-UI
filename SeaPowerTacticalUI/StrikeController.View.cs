using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class StrikeController
    {
        private const string EmptyVeto = "{\"mode\":\"veto\",\"chain\":[],\"vetoed_n\":0,\"last\":null}";

        /// <summary>The section "strike" of ui_state (ADR-0132 section 10): groups, veto, calib. Lists are cut to limit entries (default 20).</summary>
        public string ViewJson(int limit)
        {
            if (limit <= 0) limit = 20;
            var ships = _g.Ships() ?? new List<StrikeShip>();
            var air = _g.Aircraft() ?? new List<StrikeAircraft>();
            double now = _g.Now;
            var sb = new StringBuilder("{\"groups\":[");
            int gi = 0;
            foreach (var g in _groups)
            {
                if (gi++ >= limit) break;
                if (gi > 1) sb.Append(',');
                GroupJson(sb, g, ships, air, now, limit);
            }
            sb.Append("],\"veto\":");
            string veto = null;
            try { veto = VetoJson == null ? null : VetoJson(); } catch (Exception) { }
            sb.Append(string.IsNullOrEmpty(veto) ? EmptyVeto : veto);
            sb.Append(",\"ew_capable\":").Append(Json.Bool(_g.EwCapable)).Append(",\"calib\":{\"source\":").Append(Q(_g.CalibSource)).Append(",\"formula_version\":").Append(StrikeCalib.FormulaVersion.ToString(CultureInfo.InvariantCulture))
              .Append(",\"estimate\":").Append(Json.Bool(StrikeCalib.Estimate))
              .Append(",\"difficulty\":").Append(N(Diff())).Append(",\"diff_src\":").Append(Q(DiffSrcOf())).Append(",\"calib_id\":").Append(Q(StrikeCalib.CalibId)).Append(",\"k_scale\":").Append(N(StrikeCalib.KScale))
              .Append(",\"dmg_bonus\":").Append(N(_bonus)).Append(",\"bonus_src\":").Append(Q(_bonusKnown ? "game" : "default")).Append(",\"kill_threshold\":").Append(N(_p.KillThreshold))
              .Append(",\"kill_estimate\":").Append(Json.Bool(StrikeCalib.KillEstimate)).Append(",\"ledger\":").Append(Q(LedgerOn() ? "on" : "off")).Append("}}");
            return sb.ToString();
        }

        private static string SinkNum(StrikeTargetPlan pt, int v) { return pt == null || pt.Goal != "sink" || v < 0 ? "null" : I(v); }

        private bool LedgerOn() { try { return _g.HitsAvailable; } catch (Exception) { return false; } }

        private void GroupJson(StringBuilder sb, StrikeGroup g, IList<StrikeShip> ships, IList<StrikeAircraft> air, double now, int limit)
        {
            string status = g.Timeline == null ? "idle" : g.Timeline.Status;
            sb.Append("{\"name\":").Append(Q(g.Name)).Append(",\"seq\":").Append(I(g.Seq)).Append(",\"goal\":").Append(Q(g.Goal ?? "sink")).Append(",\"status\":").Append(Q(status)).Append(",\"members\":[");
            int n = 0;
            foreach (int uid in g.Members)
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                var a = air.FirstOrDefault(x => x != null && x.Uid == uid);
                if (a == null) { sb.Append("{\"uid\":").Append(I(uid)).Append(",\"role\":\"lost\",\"airborne\":false,\"missiles\":[],\"why\":null}"); continue; }
                string why = StrikeAirMath.RoleOf(a) == "ew" ? null : StrikeAirMath.AircraftWhy(a);
                sb.Append("{\"uid\":").Append(I(uid)).Append(",\"role\":").Append(Q(StrikeAirMath.RoleOf(a))).Append(",\"airborne\":").Append(Json.Bool(a.Airborne)).Append(",\"missiles\":[");
                int k = 0;
                foreach (var m in a.Missiles.Where(StrikeAirMath.IsStrikeMissile))
                {
                    if (k++ > 0) sb.Append(',');
                    sb.Append("{\"ammo\":").Append(Q(m.Ammo)).Append(",\"n\":").Append(I(m.Count)).Append('}');
                }
                sb.Append("],\"why\":").Append(why == null ? "null" : Q(why)).Append('}');
            }
            sb.Append("],\"target\":{\"mode\":").Append(Q(g.Target.Mode)).Append(",\"main\":").Append(g.Target.MainId == 0 ? "null" : I(g.Target.MainId)).Append(",\"ships\":[");
            var best = g.Variants != null && g.Variants.Count > 0 ? g.Variants[Math.Max(0, Math.Min(g.Chosen, g.Variants.Count - 1))] : null;
            var ts = new List<StrikeShip>();
            foreach (int id in g.Target.Ships) { var s = ships.FirstOrDefault(x => x != null && x.TrackId == id); if (s != null) ts.Add(s); }
            var ordered = StrikePlanMath.Prioritize(ts, StrikeCalib.TonBandFrac);
            n = 0;
            foreach (var s in ordered)
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                var pt = best == null ? null : best.PerTarget.FirstOrDefault(t => t.TrackId == s.TrackId);
                sb.Append("{\"id\":").Append(I(s.TrackId)).Append(",\"prio\":").Append(I(n)).Append(",\"n\":").Append(pt == null ? "null" : I(pt.N)).Append(",\"p\":").Append(pt == null ? "null" : N(pt.P))
                  .Append(",\"h\":").Append(SinkNum(pt, pt == null ? 0 : pt.H)).Append(",\"h1\":").Append(SinkNum(pt, pt == null ? 0 : pt.H1)).Append(",\"m_sink\":").Append(SinkNum(pt, pt == null ? 0 : pt.MSink))
                  .Append(",\"p_sunk\":").Append(pt == null || pt.PSunk < 0 ? "null" : N(pt.PSunk)).Append(",\"budget_left\":").Append(pt == null || pt.BudgetLeft < 0 ? "null" : N(pt.BudgetLeft))
                  .Append(",\"why\":").Append(Q("k=" + F1(s.Ad.Ksub) + " s=" + F1(s.Ad.Ssub) + " tons=" + F1(s.DisplacementT) + " src=" + (s.Ad.Source ?? "none"))).Append('}');
            }
            sb.Append("]},\"plan\":");
            if (g.Timeline == null) sb.Append("null");
            else
            {
                sb.Append("{\"id\":").Append(I(g.Timeline.PlanId)).Append(",\"status\":").Append(Q(g.Timeline.Status)).Append(",\"stale_why\":").Append(g.Timeline.StaleWhy == null ? "null" : Q(g.Timeline.StaleWhy))
                  .Append(",\"t_detect\":").Append(g.Timeline.TDetect < 0 ? "null" : N(g.Timeline.TDetect)).Append(",\"chosen\":").Append(g.Chosen < 0 ? "null" : I(g.Chosen)).Append(",\"tof_src\":").Append(Q(g.TofSrc)).Append(",\"variants\":[");
                n = 0;
                foreach (var v in g.Variants)
                {
                    if (n++ >= limit) break;
                    if (n > 1) sb.Append(',');
                    sb.Append(VariantJson(v, now));
                }
                sb.Append("],\"timeline\":[");
                n = 0;
                double lead0 = StrikeTotMath.TurnLeadS(0, _p);
                var exec = g.Timeline.Exec.OrderBy(x => x.Uid).ThenBy(x => x.Order).ToList();
                if (exec.Count > 0)
                {
                    foreach (var r in exec)
                    {
                        if (n++ >= limit) break;
                        if (n > 1) sb.Append(',');
                        sb.Append("{\"uid\":").Append(I(r.Uid)).Append(",\"tgt\":").Append(I(r.TargetId)).Append(",\"ammo\":").Append(Q(r.Ammo)).Append(",\"salvo\":").Append(I(r.Salvo))
                          .Append(",\"state\":").Append(Q(r.State)).Append(",\"why\":").Append(r.Why == null ? "null" : Q(r.Why))
                          .Append(",\"t_go\":").Append(N(r.TGo)).Append(",\"t_issue\":").Append(N(r.TLaunch - lead0)).Append(",\"t_launch\":").Append(N(r.TLaunch)).Append(",\"t_arrive\":").Append(N(r.TArrive))
                          .Append(",\"fact\":{\"issue\":").Append(r.IssuedT < 0 ? "null" : N(r.IssuedT)).Append(",\"launched\":").Append(r.LaunchedT < 0 ? "null" : N(r.LaunchedT))
                          .Append(",\"late_s\":").Append(N(r.LateS)).Append(",\"dt_order_s\":").Append(r.DtOrderS < 0 ? "null" : N(r.DtOrderS)).Append("}}");
                    }
                }
                else if (g.Chosen >= 0 && g.Chosen < g.Variants.Count)
                {
                    foreach (var a in g.Variants[g.Chosen].Assignments.OrderBy(x => x.Uid).ThenBy(x => x.Order))
                    {
                        if (n++ >= limit) break;
                        if (n > 1) sb.Append(',');
                        sb.Append("{\"uid\":").Append(I(a.Uid)).Append(",\"tgt\":").Append(I(a.TargetId)).Append(",\"ammo\":").Append(Q(a.Ammo)).Append(",\"salvo\":").Append(I(a.Salvo))
                          .Append(",\"state\":null,\"why\":null,\"t_go\":").Append(N(a.TGo)).Append(",\"t_issue\":").Append(N(a.TIssue)).Append(",\"t_launch\":").Append(N(a.TLaunch)).Append(",\"t_arrive\":").Append(N(a.TArrive))
                          .Append(",\"fact\":{\"issue\":null,\"launched\":null,\"late_s\":0,\"dt_order_s\":null}}");
                    }
                }
                sb.Append("]}");
            }
            sb.Append(",\"wait_pt\":");
            if (g.WaitPt.HasValue) sb.Append("{\"lat\":").Append(N(g.WaitPt.Value.Lat)).Append(",\"lon\":").Append(N(g.WaitPt.Value.Lon)).Append('}'); else sb.Append("null");
            sb.Append(",\"wait_why\":").Append(g.WaitWhy == null ? "null" : Q(g.WaitWhy)).Append(",\"gather\":").Append(Q(g.Gather)).Append(",\"left\":[");
            n = 0;
            foreach (var l in g.Left)
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                sb.Append("{\"uid\":").Append(I(l.Uid)).Append(",\"why\":").Append(Q(l.Why)).Append(",\"t\":").Append(N(l.T)).Append('}');
            }
            sb.Append("],\"deck\":[");
            n = 0;
            foreach (string k in g.DeckKeys)
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                sb.Append("{\"key\":").Append(Q(k)).Append(",\"allowed\":").Append(Json.Bool(_deckAllowed.Contains(k))).Append('}');
            }
            sb.Append("],").Append(DeckJson(g)).Append(",\"ew\":[");
            n = 0;
            foreach (var es in g.EwExec)
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                sb.Append(EwStateJson(g, es, air.FirstOrDefault(x => x != null && x.Uid == es.Uid)));
            }
            sb.Append("],").Append(FighterGroupJson(g)).Append(",\"auto\":").Append(AutoJson(g)).Append(",\"clear\":").Append(ClearJson(g)).Append(",\"wave\":").Append(I(g.Wave)).Append(",\"exec\":{\"status\":").Append(Q(g.Timeline != null && (g.Timeline.Status == "executing" || g.Timeline.Status == "cancelled") ? g.Timeline.Status : (g.ExecStatus ?? "none")))
              .Append(",\"roe\":").Append(Q(g.Timeline != null && g.Timeline.Roe != null ? g.Timeline.Roe : EffectiveRoe)).Append(",\"wave\":").Append(I(g.Wave))
              .Append(",\"start_t\":").Append(g.Timeline != null && g.Timeline.StartT >= 0 ? N(g.Timeline.StartT) : "null").Append("},\"results\":[");
            n = 0;
            foreach (var tr in (g.Timeline != null && IsExecStatus(g.Timeline.Status) ? g.Timeline.Results : g.Results))
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                sb.Append("{\"id\":").Append(I(tr.TrackId)).Append(",\"state\":").Append(Q(tr.State)).Append(",\"fired\":").Append(I(tr.Fired)).Append(",\"hits\":").Append(I(tr.Hits))
                  .Append(",\"arrived\":").Append(I(tr.Arrived)).Append(",\"shot_down\":").Append(I(tr.ShotDown)).Append(",\"spread_s\":").Append(N(tr.SpreadS)).Append('}');
            }
            sb.Append("],\"spread\":[");
            n = 0;
            foreach (var tr in (g.Timeline != null && IsExecStatus(g.Timeline.Status) ? g.Timeline.Results : g.Results).Where(x => x.Arrived > 1))
            {
                if (n++ >= limit) break;
                if (n > 1) sb.Append(',');
                sb.Append("{\"tgt\":").Append(I(tr.TrackId)).Append(",\"n\":").Append(I(tr.Arrived)).Append(",\"s\":").Append(N(tr.SpreadS)).Append('}');
            }
            sb.Append("]}");
        }
    }
}
