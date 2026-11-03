using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075: the part of the ASW / SAR controller that applies the stages of the air threat service to its groups:
    // the retreat of the aircraft under the cover of a ship with SAMs, no launches on stage 2, the resume, a lost aircraft.
    internal sealed partial class AswController
    {
        /// <summary>The air threat service; null = without protection (the old behaviour).</summary>
        internal IAirThreats AirGuard;
        /// <summary>The shared retreat helper (also used by the strike group, T-173).</summary>
        internal AirRetreatSteer AirSteer;

        private sealed class RetreatRec { public string Group, Mode; public double Since; public bool Landing; public int CoverUid; public GeoPt Pt; public string Note, CoverName; }

        private readonly Dictionary<int, RetreatRec> _airRet = new Dictionary<int, RetreatRec>();
        private readonly Dictionary<string, int> _airLostPeople = new Dictionary<string, int>();

        /// <summary>Aircraft of the group that fly a retreat now (diagnostics, tests).</summary>
        internal int AirRetreating(string group)
        {
            int n = 0;
            foreach (var r in _airRet.Values) if (r.Group == group) n++;
            return n;
        }

        /// <summary>People lost with aircraft of a SAR group (lost:cause), for the counter "lost".</summary>
        internal int AirLostPeople(string group) { int n; return _airLostPeople.TryGetValue(group, out n) ? n : 0; }

        /// <summary>The groups for the service: the members, the area (target position / zone corners / rescue area) and the decks the group launches from.</summary>
        /// <summary>T-179: our surface ships (name, position) for the ship pick of the CAP window (the alert group's "Protect"); no view is built.</summary>
        internal IList<AswTargetOption> OwnShipTargets()
        {
            try { return _game.CourseTargets(); } catch (Exception) { return null; }
        }

        internal IList<AirGuardGroup> AirGuardGroups()
        {
            var res = new List<AirGuardGroup>();
            foreach (var g in _groups)
            {
                if (g.Order == "none") continue;
                var d = new AirGuardGroup { Window = "asw", Name = g.Name, Kind = g.Order };
                foreach (int u in g.Members)
                {
                    d.Uids.Add(u);
                    AswMember m;
                    if (!_live.TryGetValue(u, out m)) continue;
                    if (m.Helicopter) d.Helicopter = true;
                    if (m.DeckUid != 0 && !d.DeckUids.Contains(m.DeckUid)) d.DeckUids.Add(m.DeckUid);
                }
                if (g.AnchorDeck != 0 && !d.DeckUids.Contains(g.AnchorDeck)) d.DeckUids.Add(g.AnchorDeck);
                if (g.Order == "course")
                {
                    var t = _game.Target(g.TargetUid);
                    if (t != null) d.AreaPts.Add(Pos(t));
                }
                else if (g.Order == "area" && g.Rect != null && g.Rect.Length >= 4) AddRect(d, g.Rect);
                else if (g.Order == "strike")
                {
                    var rs = g.St.RefUid != 0 ? _game.Target(g.St.RefUid) : null;
                    if (rs != null) d.AreaPts.Add(Pos(rs));
                }
                else if (g.Order == "sar")
                {
                    if (g.SarArea.Kind == "rect" && g.SarArea.Rect != null && g.SarArea.Rect.Length >= 4) AddRect(d, g.SarArea.Rect);
                    else if (g.SarArea.Kind == "circle") d.AreaPts.Add(g.SarArea.Center);
                }
                res.Add(d);
            }
            return res;
        }

        private static void AddRect(AirGuardGroup d, double[] r)
        {
            d.AreaPts.Add(new GeoPt(r[0], r[1])); d.AreaPts.Add(new GeoPt(r[0], r[3])); d.AreaPts.Add(new GeoPt(r[2], r[1])); d.AreaPts.Add(new GeoPt(r[2], r[3]));
            d.AreaPts.Add(new GeoPt((r[0] + r[2]) * 0.5, (r[1] + r[3]) * 0.5));
        }

        /// <summary>Stage 2 of the group (the deck or the area): no launch, the plan "launch" is carried out as "prepare" (the task is ordered with allowLaunch = false, a prepared one is not allowed).</summary>
        private bool AirLaunchBlocked(Grp g)
        {
            var s = AirGuard;
            return s != null && s.P.Mode == "retreat" && s.GroupStage("asw", g.Name) == AirStage.Retreat;
        }

        /// <summary>T-180: fills the retreat part of a group view: the rows ("retreat -&gt; ship"), the group chip data, the map arrows. Null Retreat = nothing to show.</summary>
        private void RetreatView(Grp g, AswGroupView gv)
        {
            var s = AirGuard;
            if (s == null) return;
            var rv = new AswRetreatView { LaunchBlocked = AirLaunchBlocked(g), ResumeWhen = "beyond", ResumeNm = s.P.RetreatNm + s.P.ClearMarginNm };
            int firstUid = 0;
            foreach (var row in gv.MemberRows)
            {
                RetreatRec rec;
                if (!_airRet.TryGetValue(row.Uid, out rec) || rec.Group != g.Name) continue;
                row.RetreatTo = rec.Mode == "away" ? "" : (rec.CoverName ?? "");
                rv.Members++;
                if (firstUid == 0) { firstUid = row.Uid; rv.Cover = rec.Mode == "away" ? null : rec.CoverName; }
                if (row.HasPos) gv.RetreatArrows.Add(new AswRetreatArrow { Uid = row.Uid, FromLat = row.Lat, FromLon = row.Lon, ToLat = rec.Pt.Lat, ToLon = rec.Pt.Lon, Away = rec.Mode == "away" });
            }
            foreach (int u in g.Members) { AswMember lm; if (_live.TryGetValue(u, out lm) && lm.Helicopter) { rv.ResumeWhen = "recede"; rv.ResumeNm = 0; break; } }
            if (rv.Members == 0 && !rv.LaunchBlocked) return;
            AirUnitThreat th = null;
            if (firstUid != 0) th = s.ForUnit(firstUid);
            else foreach (int u in g.Members) { th = s.ForUnit(u); if (th != null) break; }
            if (th != null) { rv.Nm = th.Nm; rv.Why = th.Why ?? "unknown"; } else rv.Why = "unknown";
            gv.Retreat = rv;
        }

        private AswRetreatView RetreatOfGroup(Grp g)
        {
            foreach (var gv in _view.Groups) if (gv.Name == g.Name) return gv.Retreat;
            return null;
        }

        private static string RetreatJson(AswRetreatView r)
        {
            if (r == null) return "null";
            return Obj("members", r.Members, "launch_blocked", r.LaunchBlocked, "nm", Math.Round(r.Nm, 1), "cover", r.Cover, "why", r.Why, "resume_when", r.ResumeWhen, "resume_nm", r.ResumeNm);
        }

        /// <summary>The members of the group that retreat (the strike group and the window read it).</summary>
        internal bool AirRetreatOf(int uid) { return _airRet.ContainsKey(uid); }

        /// <summary>true = the aircraft flies a retreat (the tactic is skipped this tick).</summary>
        private bool AirRetreat(Grp g, AswMember m)
        {
            var s = AirGuard; var steer = AirSteer;
            if (s == null || steer == null || s.P.Mode != "retreat") { if (_airRet.Count > 0) AirReleaseAll(g); return false; }
            RetreatRec rec;
            bool had = _airRet.TryGetValue(m.Uid, out rec);
            var th = s.ForUnit(m.Uid);
            if (th == null || th.Stage != AirStage.Retreat)
            {
                if (had) AirResumeUnit(g, m, rec, th);
                return false;
            }
            var cov = s.CoverFor(m.Uid);
            if (cov == null) return false;
            if (!had)
            {
                rec = new RetreatRec { Group = g.Name, Mode = cov.Mode, Since = _now };
                _airRet[m.Uid] = rec;
                CancelRoute(g, m.Uid); g.Jobs.Remove(m.Uid);   // the tactic stops; the route of the tactic and the undropped buoys of its package go
                foreach (var cr in g.Contacts) if (cr.Holder == m.Uid) cr.Holder = 0;   // ADR-0075 item 5: the holder of the contact lets go (the contact goes to the lost ring)
                if (g.Order == "sar" && Sar != null) Sar.ReleaseUnit(m.Uid, "air_threat");   // the rafts are assigned again after the resume
            }
            if (!had || !AirGuardMath.SameCover(HeldCover(rec), cov, s.P))   // T-192: a shift of the point within CoverMoveNm is the same command
            {
                rec.Mode = cov.Mode; rec.CoverUid = cov.ShipUid; rec.Pt = cov.Point; rec.Note = cov.Note; rec.CoverName = cov.ShipName;
                _game.Log("[AirGuard] retreat grp=" + g.Name + " uid=" + I(m.Uid) + " mode=" + cov.Mode + " cover=" + (cov.ShipName ?? "-") + " pt=" + cov.Point.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + cov.Point.Lon.ToString("0.0000", CultureInfo.InvariantCulture) + " track=" + I(th.TrackId) + " why=" + (cov.Note ?? th.Why ?? "ok"));
                _game.Event("air_retreat", Obj("group", g.Name, "uid", m.Uid, "mode", cov.Mode, "cover_uid", cov.ShipUid, "on", true));
                if (cov.Note == "no_cover") Warn(g, "no_cover", m.Name, 120);
                else if (cov.Note == "cover_path_risk" || cov.Note == "cover_radar_off") Warn(g, cov.Note, m.Name, 120);
            }
            if (cov.Point.Lat != rec.Pt.Lat || cov.Point.Lon != rec.Pt.Lon) cov = new AirCover { Mode = cov.Mode, ShipUid = cov.ShipUid, ShipName = cov.ShipName, Point = rec.Pt, Note = cov.Note };   // T-192: the steer keeps the issued point
            if (g.Order == "sar" && cov.Mode == "land")
            {
                if (!rec.Landing) { rec.Landing = true; _sarAir.AirLand(g, m); }
                return true;
            }
            // fuel for the speed step: the nominal cruise gives the miles
            double kn = m.Helicopter ? 100 : 250;
            double reserve = Math.Max(0, m.FuelFrac * m.EnduranceS - m.TransitS) * kn / 3600.0, max = m.EnduranceS * kn / 3600.0;
            steer.Steer(m.Uid, cov, m.Helicopter, reserve, max, _now);
            return true;
        }

        private static AirCover HeldCover(RetreatRec rec) { return new AirCover { Mode = rec.Mode, ShipUid = rec.CoverUid, ShipName = rec.CoverName, Point = rec.Pt, Note = rec.Note }; }

        private void AirResumeUnit(Grp g, AswMember m, RetreatRec rec, AirUnitThreat th)
        {
            _airRet.Remove(m.Uid);
            SarRetake(m.Uid, g.Name);
            string e = AirSteer == null ? null : AirSteer.Release(m.Uid);
            g.Dirty = true;   // the tactic is laid again from where the aircraft is; the rows go on ahead of the covered water (RowsAhead, ADR-0063)
            _game.Log("[AirGuard] resume grp=" + g.Name + " uid=" + I(m.Uid) + " retreat_s=" + N(Math.Round(_now - rec.Since)) + " why=" + (th != null && th.Receding ? "receding" : "clear") + " track=" + (th != null ? I(th.TrackId) : "-") + " restore -> " + (e ?? "ok"));
            _game.Event("air_retreat", Obj("group", g.Name, "uid", m.Uid, "mode", rec.Mode, "cover_uid", rec.CoverUid, "on", false));
        }

        /// <summary>T-318: a SAR helicopter whose retreat ended is handed to SAR afresh (claim, HeloRun and the TakenByPlayer base go; the rafts are assigned again).</summary>
        private void SarRetake(int uid, string groupName)
        {
            var sg = Find(groupName);
            if (sg != null && sg.Order == "sar" && Sar != null) Sar.ReleaseUnit(uid, "air_resume");
        }

        private void AirReleaseAll(Grp g)
        {
            foreach (int u in new List<int>(_airRet.Keys)) { string gn = _airRet[u].Group; _airRet.Remove(u); SarRetake(u, gn); if (AirSteer != null) AirSteer.Release(u); }
            g.Dirty = true;
        }

        /// <summary>Once per tick and group: the records of aircraft that landed or left the group are dropped.</summary>
        private void AirResumeCheck(Grp g)
        {
            if (_airRet.Count == 0) return;
            foreach (int u in new List<int>(_airRet.Keys))
            {
                if (_airRet[u].Group != g.Name) continue;
                AswMember m;
                if (g.Members.Contains(u) && _live.TryGetValue(u, out m) && m.Where == "air") continue;
                _airRet.Remove(u);
                if (AirSteer != null) AirSteer.Release(u);
            }
        }

        /// <summary>From AirLossTracker: the aircraft is lost (cause: shot_down | crash | fuel | unknown). The member leaves the group with "lost:&lt;cause&gt;", not as "landed".</summary>
        internal void AirLost(int uid, string cause, int aboard = 0)
        {
            var g = GroupOf(uid);
            _airRet.Remove(uid);
            if (AirSteer != null) AirSteer.Release(uid);
            if (g == null) return;
            string name = g.Refs.ContainsKey(uid) ? g.Refs[uid].Name : I(uid);
            _game.Log("asw member lost " + g.Name + " uid=" + I(uid) + " " + name + " reason=lost:" + cause + (aboard > 0 ? " aboard=" + I(aboard) : ""));
            Warn(g, "member_lost", name + ": lost:" + cause + (aboard > 0 ? " (" + I(aboard) + ")" : ""), 30);
            if (g.Order == "sar" && aboard > 0) { int n; _airLostPeople.TryGetValue(g.Name, out n); _airLostPeople[g.Name] = n + aboard; }
            RemoveMember(g, uid, false); _mem.Remove(uid);
            _lastKnown.Remove(uid);
        }
    }
}
