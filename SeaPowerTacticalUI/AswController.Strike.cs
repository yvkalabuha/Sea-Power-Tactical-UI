using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    // T-173 / ADR-0074: the strike group ("Удар"): the targets (eligibility), the assignment of shooters (greedy, second number, deck), the claims between strike
    // groups, the holder of the contact, the idle aircraft, the card "submarine detected". The attack of one target itself is AswController.Attack.cs.
    internal sealed partial class AswController
    {
        /// <summary>The order state of a strike group (Grp.St).</summary>
        private sealed class StrikeState
        {
            public int RefUid, Airborne;
            public double RadiusNm;
            public string Targets = "hostile_unknown";
            public HashSet<int> Skip = new HashSet<int>();
            public Dictionary<int, StrikeElig> Elig = new Dictionary<int, StrikeElig>();
            public Dictionary<int, string> LastElig = new Dictionary<int, string>();
            public Dictionary<int, double> IdleSince = new Dictionary<int, double>();
            public HashSet<int> Rtb = new HashSet<int>();
            public HashSet<int> NoHome = new HashSet<int>();                               // 2026-10-22: idle machines the game cannot land (no home base): they orbit
            public Dictionary<int, GeoPt> IdlePt = new Dictionary<int, GeoPt>();            // where a machine went idle: the orbit point when the group has no reference
            public Dictionary<int, double> HomeAt = new Dictionary<int, double>();
            public Dictionary<int, double> FixDipAt = new Dictionary<int, double>();
            public Dictionary<string, string> LastAssign = new Dictionary<string, string>();
            public Dictionary<int, string> DeckFor = new Dictionary<int, string>();
            public bool NoArmedLogged, OwnSubsLogged;
            public Dictionary<int, string> LastNoShooter = new Dictionary<int, string>();   // T-222: per target, the last "no shooter" signature (log once per change; the card reads it)
            public Dictionary<int, string> LastKept = new Dictionary<int, string>();         // T-231: per target, the last "shooter kept" signature (one line per shooter and reason)
            public List<StrikeAssign> Plan = new List<StrikeAssign>();
        }

        private readonly Dictionary<int, double> _liveSince = new Dictionary<int, double>();
        private readonly Dictionary<int, string> _claims = new Dictionary<int, string>();
        private readonly HashSet<int> _killedEmitted = new HashSet<int>();

        private static string FixName(string s) { return s ?? ""; }

        // ---- the order -----------------------------------------------------------------------------------------------

        /// <summary>asw_order {group, order:"strike", ref_uid?, radius_nm?, targets?, airborne?}: the group becomes a strike group (ADR-0074 item 2).</summary>
        public string Order(Dictionary<string, object> c)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            var cmd = AswCommandsParse.ParseOrder(c);
            if (cmd.Err != null) return Fail(cmd.Err);
            var g = Find(cmd.Group);
            if (g == null) return Fail("no_group");
            RefreshLive();
            ClearPreview();
            if (g.Order == "sar") _sarAir.Leave(g, false);
            g.Order = "strike"; g.Rect = null; g.TargetName = null; g.TargetUid = 0; g.Tactics = new List<string>();
            ResetPlan(g);
            var st = g.St = new StrikeState();
            st.Targets = cmd.Targets ?? _cfg.Strike.Targets;
            st.RadiusNm = cmd.RadiusNm.HasValue ? cmd.RadiusNm.Value : _cfg.Strike.RadiusNm;
            st.Airborne = cmd.Airborne.HasValue ? cmd.Airborne.Value : _cfg.Strike.Airborne;
            foreach (int u in new List<int>(g.Members))
            {
                AswMember m;
                if (!_live.TryGetValue(u, out m)) continue;
                string why = AswStrikeMath.StrikeExclusion(m, TorpedoLoadoutOn(m));
                if (why != null) { _game.Log("asw strike " + g.Name + " member " + m.Name + " leaves: " + why); RemoveMember(g, u, true); }
            }
            st.RefUid = cmd.RefUid != 0 ? cmd.RefUid : DefaultRef(g);
            g.Dirty = true;
            ReassignNow(g);
            AutoRename(g);
            _game.Log("asw strike " + g.Name + " order ref=" + I(st.RefUid) + " radius_nm=" + N(st.RadiusNm) + " targets=" + st.Targets + " airborne=" + I(st.Airborne) + " members=" + I(g.Members.Count));
            RebuildView();
            return Obj("group", g.Name, "order", "strike", "ref_uid", st.RefUid, "radius_nm", st.RadiusNm, "targets", st.Targets, "airborne", st.Airborne, "members", g.Members.Count);
        }

        private int DefaultRef(Grp g)
        {
            foreach (int u in g.Members) { AswMember m; if (_live.TryGetValue(u, out m) && m.ShipUid != 0) return m.ShipUid; }
            foreach (int u in g.Members) { AswMember m; if (_live.TryGetValue(u, out m) && m.DeckUid != 0) return m.DeckUid; }
            return 0;
        }

        /// <summary>asw_strike_skip / asw_strike_resume {contact}: the track leaves / re-enters the assignment of every strike group.</summary>
        public string StrikeSkip(Dictionary<string, object> c, bool skip)
        {
            if (_deferred != null) return Fail("restore_waiting");   // T-211
            object o;
            if (!c.TryGetValue("contact", out o) || !(o is double)) return Fail("bad_args");
            int id = (int)(double)o;
            int n = 0;
            foreach (var g in _groups)
            {
                if (g.Order != "strike") continue;
                n++;
                if (skip) g.St.Skip.Add(id); else g.St.Skip.Remove(id);
            }
            if (n == 0) return Fail("no_group");
            TargetRec r;
            if (_targets.TryGetValue(id, out r))
            {
                if (skip) { if (r.State != "done") Done(r, "skipped"); }
                else if (r.Done == "skipped" || r.Done == "limit") _targets.Remove(id);
            }
            _game.Log("asw strike " + (skip ? "skip" : "resume") + " contact=" + I(id));
            RebuildView();
            return Obj("contact", id, "skipped", skip);
        }

        private void StopStrikeTargets(string group)
        {
            foreach (var r in new List<TargetRec>(_targets.Values)) if (r.Group == group && r.State != "done") Done(r, "stopped");
        }

        // ---- loadouts ------------------------------------------------------------------------------------------------

        /// <summary>The loadouts of the machine's type on its deck that carry an ASW torpedo (the deck broker prepares exactly one of them).</summary>
        private List<string> TorpedoLoadoutNames(AswMember m)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(m.Type) || m.DeckUid == 0) return res;
            try
            {
                foreach (var lo in _game.SarLoadouts(m.DeckUid, m.Type))
                    if (AswMath.IsAswLoadout(lo, _defaults.Loadouts) && HasTorpedo(lo)) res.Add(lo.Name);
            }
            catch (Exception e) { _game.Log("asw strike loadouts error: " + e.Message); }
            return res;
        }

        private static bool HasTorpedo(LoadoutOption lo)
        {
            if (lo.Weapons == null) return false;
            foreach (var kv in lo.Weapons)
            {
                if (kv.Value <= 0 || kv.Key == null) continue;
                string k = kv.Key.ToLowerInvariant();
                if (k.Contains("torp") || k.Contains("mk46") || k.Contains("mk50") || k.Contains("mk54") || k.Contains("mk-46") || k.Contains("mk-50") || k.Contains("mk-54") || k.Contains("a244") || k.Contains("uqq")) return true;
            }
            return false;
        }

        private bool TorpedoLoadoutOn(AswMember m)
        {
            if (m.Torpedoes > 0) return true;
            return m.Where != null && m.Where.StartsWith("deck", StringComparison.Ordinal) && TorpedoLoadoutNames(m).Count > 0;
        }

        // ---- the tick of the whole controller (before the groups) -------------------------------------------------

        /// <summary>Once per tick: the tracks, the kills (one event per track), how long each track is live, the claims between the strike groups.</summary>
        private void ScanSubs()
        {
            var subs = SubsTick();
            var present = new HashSet<int>();
            foreach (var c in subs)
            {
                present.Add(c.Id);
                if (StrikeLive(c)) { if (!_liveSince.ContainsKey(c.Id)) _liveSince[c.Id] = _now; } else _liveSince.Remove(c.Id);
                if (c.Killed && _killedEmitted.Add(c.Id)) EmitKilled(c);
            }
            foreach (int id in new List<int>(_liveSince.Keys)) if (!present.Contains(id)) _liveSince.Remove(id);
            foreach (var g in _groups) if (g.Order == "strike") g.St.Skip.RemoveWhere(id => !present.Contains(id) && !_targets.ContainsKey(id));
            ResolveClaims();
        }

        private void EmitKilled(AswContact c)
        {
            TargetRec r; _targets.TryGetValue(c.Id, out r);
            string grp = r != null ? r.Group : null;
            string name = r != null ? r.Name : c.Name;
            if (grp == null) foreach (var g in _groups) foreach (var rec in g.Contacts) if (rec.Id == c.Id && grp == null) grp = g.Name;
            if (grp == null) return;
            if (r != null) SettleHits(r);   // T-231: the ledger of hits is the truth when the torpedo was still in the water
            double lat = c.Lat != 0 || c.Lon != 0 ? c.Lat : (r != null ? r.Lat : 0), lon = c.Lat != 0 || c.Lon != 0 ? c.Lon : (r != null ? r.Lon : 0);
            _game.Log("asw contact " + grp + " killed contact=" + I(c.Id) + " track=" + name + (r != null ? " torpedoes=" + I(r.Torps) + " hits=" + I(r.Hits) + " ledger=" + I(_game.HitsOn(c.Id)) : ""));
            _game.Event("asw_contact_killed", Obj("group", grp, "contact", c.Id, "track", name, "lat", lat, "lon", lon, "torpedoes", r != null ? (object)r.Torps : null, "hits", r != null ? (object)r.Hits : null));
            NoteAlert(c.Id, "killed");
        }

        /// <summary>T-222: the one "live" of the strike (target, run, LiveForS): live now or held after the last live fix, and not killed.</summary>
        private bool StrikeLive(AswContact c) { return !c.Killed && AswStrikeMath.HeldLive(c.Live, c.FixS, _now, AswStrikeMath.StrikeLiveHoldS); }

        private StrikeTrack ToTrack(AswContact c)
        {
            double since;
            return new StrikeTrack
            {
                Id = c.Id, Side = c.Side, MapPosture = c.MapPosture, VmPosture = c.VmPosture, ClassName = c.ClassName, Decoy = c.Decoy, Biologic = c.Biologic, Classified = c.Classified,
                Live = StrikeLive(c), Surfaced = c.Surfaced, Lat = c.Lat, Lon = c.Lon, LiveForS = _liveSince.TryGetValue(c.Id, out since) ? _now - since : 0, FixAgeS = -1
            };
        }

        private StrikePolicy PolicyOf(Grp g)
        {
            var st = g.St;
            var p = new StrikePolicy { Targets = st.Targets, UnknownConfirmS = _cfg.Strike.UnknownConfirmS, RadiusNm = st.RadiusNm };
            var rs = st.RefUid != 0 ? _game.Target(st.RefUid) : null;
            if (rs != null) { p.HasGuard = true; p.Guard = Pos(rs); }
            return p;
        }

        private HashSet<int> OwnTrackIds(IList<AswOwnSub> own)
        {
            var ids = new HashSet<int>();
            if (own != null) foreach (var o in own) if (o.TrackId != 0) ids.Add(o.TrackId);
            return ids;
        }

        // ---- shooters, claims ----------------------------------------------------------------------------------------

        private const double HeloKn = 100, PlaneKn = 250;

        private double Kn(AswMember m) { return m.Helicopter ? HeloKn : PlaneKn; }

        /// <summary>null = the machine is no shooter now (not armed, leaving, in a retreat, picked out, held by another group, too little fuel).</summary>
        private StrikeShooter ShooterOf(Grp g, AswMember m, AswContact c) { string why; return ShooterOf(g, m, c, out why); }

        /// <summary>T-222: same as above, plus why the machine is no shooter: excluded|leaving|bad_until|no_torpedo|retreat|fuel(..)|deck_blocked|no_loadout; null when it is one.</summary>
        private StrikeShooter ShooterOf(Grp g, AswMember m, AswContact c, out string why)
        {
            why = null;
            if (Excluded(m)) { why = "excluded"; return null; }
            if (AswMath.IsLeaving(m)) { why = "leaving"; return null; }
            double bad;
            if (_badUntil.TryGetValue(m.Uid, out bad) && _now < bad) { why = "bad_until"; return null; }
            var sh = new StrikeShooter { Uid = m.Uid };
            var datum = new GeoPt(c.Lat, c.Lon);
            bool madPass = m.Mad && _cfg.Loc.MadVerify && c.MadFixS != AswContact.MadNotRead;
            if (m.Where == "air")
            {
                if (m.Torpedoes <= 0) { why = "no_torpedo"; return null; }
                if (AirRetreatOf(m.Uid)) { why = "retreat"; return null; }
                sh.Air = true;
                double dist = m.Lat == 0 && m.Lon == 0 ? 0 : AswMath.DistNm(new GeoPt(m.Lat, m.Lon), datum);
                sh.TtwS = AswStrikeMath.TtwAir(dist - DropRange(m.Uid), Kn(m), madPass, _cfg.Loc.MadVerifyS);
                double remain, need, extra = 0;
                var home = m.ShipUid != 0 ? _game.Target(m.ShipUid) : m.DeckUid != 0 ? _game.Target(m.DeckUid) : null;   // T-223 D2: the way home from the drop point
                if (home != null && dist > 0.01)
                {
                    double t = Math.Max(0, Math.Min(1, (dist - DropRange(m.Uid)) / dist));   // the drop point on the straight run (short legs: linear)
                    var drop = new GeoPt(m.Lat + (c.Lat - m.Lat) * t, m.Lon + (c.Lon - m.Lon) * t);
                    extra = Math.Max(0, AswMath.DistNm(Pos(home), drop) - AswMath.DistNm(Pos(home), new GeoPt(m.Lat, m.Lon)));
                }
                if (!AswStrikeMath.StrikeFuelOk(m.FuelFrac, m.EnduranceS, m.TransitS, dist - DropRange(m.Uid), extra, Kn(m), out remain, out need)) { why = "fuel(remain=" + I((int)Math.Round(remain)) + " need=" + I((int)Math.Round(need)) + ")"; return null; }
                return sh;
            }
            if (m.Where == null || !m.Where.StartsWith("deck", StringComparison.Ordinal) || !TorpedoLoadoutOn(m)) { why = "no_loadout"; return null; }
            if (AirLaunchBlocked(g)) { why = "deck_blocked"; return null; }
            if (DeckOut(g, m.DeckUid)) { why = "deck_down"; return null; }   // T-231
            var ship = m.DeckUid != 0 ? _game.Target(m.DeckUid) : null;
            double d = ship == null ? 0 : AswMath.DistNm(Pos(ship), datum);
            sh.Air = false;
            sh.TtwS = m.ReadyInS < 0 ? -1 : AswStrikeMath.TtwDeck(AswMath.ReadyS(m), m.LaunchS, Math.Max(0, d - DropRange(m.Uid)), Kn(m));
            return sh;
        }

        /// <summary>T-222: the short reason of the card for a ShooterOf code: fuel | range | retreat | other.</summary>
        private static string WhyShort(string why)
        {
            if (why == null) return "other";
            if (why.StartsWith("fuel", StringComparison.Ordinal)) return "fuel";
            if (why.StartsWith("eta", StringComparison.Ordinal)) return "range";
            if (why == "retreat") return "retreat";
            return "other";
        }

        private bool IsHolder(int uid, int contactId)
        {
            foreach (var sg in _groups) foreach (var cr in sg.Contacts) if (cr.Id == contactId && cr.Holder == uid) return true;
            return false;
        }

        private double ShipDistNm(Grp g, GeoPt p, IList<AswShipPos> ships)
        {
            double best = double.MaxValue;
            if (ships != null) foreach (var s in ships) best = Math.Min(best, AswMath.DistNm(new GeoPt(s.Lat, s.Lon), p));
            if (best == double.MaxValue) { var rs = g.St.RefUid != 0 ? _game.Target(g.St.RefUid) : null; if (rs != null) best = AswMath.DistNm(Pos(rs), p); }
            return best == double.MaxValue ? 0 : best;
        }

        /// <summary>Eligibility of every track for every strike group and the claims: one strike group per target (the lowest time to the water; the holder stays unless the gain is large).</summary>
        private void ResolveClaims()
        {
            var strikes = new List<Grp>();
            foreach (var g in _groups) if (g.Order == "strike") strikes.Add(g);
            if (strikes.Count == 0) { _claims.Clear(); return; }
            var own = _game.OwnSubs();
            var ownIds = OwnTrackIds(own);
            var byTarget = new Dictionary<int, Dictionary<string, double>>();
            foreach (var g in strikes)
            {
                var st = g.St;
                st.Elig.Clear();
                var pol = PolicyOf(g);
                foreach (var c in SubsTick())
                {
                    if (c.Killed || _killedIds.Contains(c.Id)) continue;
                    var e = st.Skip.Contains(c.Id) ? new StrikeElig { Reason = "skipped" } : AswStrikeMath.Eligible(ToTrack(c), pol, own, ownIds);
                    st.Elig[c.Id] = e;
                    string rs = e.Ok ? "ok" : e.Reason;
                    string last;
                    if (!st.LastElig.TryGetValue(c.Id, out last) || last != rs)
                    {
                        st.LastElig[c.Id] = rs;
                        _game.Log("asw strike " + g.Name + " target contact=" + I(c.Id) + " ok=" + (e.Ok ? "1" : "0") + (e.Ok ? "" : " reason=" + e.Reason));
                    }
                    if (!e.Ok) continue;
                    double best = -1;
                    foreach (int u in g.Members)
                    {
                        AswMember m; if (!_live.TryGetValue(u, out m)) continue;
                        var sh = ShooterOf(g, m, c);
                        if (sh != null && sh.TtwS >= 0 && (best < 0 || sh.TtwS < best)) best = sh.TtwS;
                    }
                    Dictionary<string, double> d;
                    if (!byTarget.TryGetValue(c.Id, out d)) byTarget[c.Id] = d = new Dictionary<string, double>();
                    d[g.Name] = best;
                }
            }
            foreach (var kv in byTarget)
            {
                string holder; _claims.TryGetValue(kv.Key, out holder);
                TargetRec r;
                if (_targets.TryGetValue(kv.Key, out r) && r.State != "done" && r.State != "idle" && kv.Value.ContainsKey(r.Group)) { _claims[kv.Key] = r.Group; continue; }
                string w = AswStrikeMath.ClaimWinner(kv.Value, holder);
                if (w == null) { foreach (var k in kv.Value.Keys) { w = k; break; } }
                _claims[kv.Key] = w;
            }
            foreach (int id in new List<int>(_claims.Keys)) if (!byTarget.ContainsKey(id)) _claims.Remove(id);
        }

        // ---- the strike group's tick ---------------------------------------------------------------------------------

        private void TickStrike(Grp g)
        {
            var st = g.St;
            var subs = SubsTick();
            bool ownRead = _game.OwnSubs() != null;
            if (!ownRead && !st.OwnSubsLogged) { st.OwnSubsLogged = true; _game.Log("asw strike " + g.Name + " own submarines are not readable: fire is held for every target"); Warn(g, "own_subs_unreadable", null, 600); }
            if (ownRead) st.OwnSubsLogged = false;
            var ships = _game.OwnShips();
            // ROE and the retreat of the aircraft under an air threat
            foreach (int u in g.Members)
            {
                AswMember m; if (!_live.TryGetValue(u, out m) || m.Where != "air" || Excluded(m)) continue;
                Prepare(g, m);
                AirRetreat(g, m);
            }
            AirResumeCheck(g);
            // the targets of this group
            var targets = new List<StrikeTarget>();
            var byId = new Dictionary<int, AswContact>();
            foreach (var c in subs)
            {
                StrikeElig e;
                if (!st.Elig.TryGetValue(c.Id, out e)) continue;
                byId[c.Id] = c;
                string claim; bool mine = !_claims.TryGetValue(c.Id, out claim) || claim == g.Name;
                TargetRec r; _targets.TryGetValue(c.Id, out r);
                if (r != null && r.Group == g.Name && r.State != "done") r.NotTarget = !e.Ok && e.Reason != "not_live";   // a stale track is the reacquire case, not "not a target"
                if (!e.Ok || !mine) continue;
                if (r != null && r.State == "done" && (r.Done == "limit" || r.Done == "killed" || (r.Done == "no_torpedo" && _now - r.DoneS < 120))) continue;
                if (r == null || r.State == "done")
                {
                    r = new TargetRec { Id = c.Id, Name = c.Name, Group = g.Name, Lat = c.Lat, Lon = c.Lon, Unknown = e.Unknown, StateS = _now, AssignedS = _now };
                    _targets[c.Id] = r;
                }
                r.Group = g.Name; r.Unknown = e.Unknown; r.NotTarget = false;
                if (r.State == "idle" && r.Shooter == 0) r.Lat = c.Lat;
                targets.Add(new StrikeTarget { Id = c.Id, ShipNm = ShipDistNm(g, new GeoPt(c.Lat, c.Lon), ships), FixAgeS = Math.Max(0, FixAge(r, c)), Hostile = !e.Unknown });
            }
            // busy shooters keep their target; the free ones are planned per target (PlanAssign)
            var current = new Dictionary<int, int>();
            bool armedAny = false;
            foreach (int u in g.Members)
            {
                AswMember m; if (!_live.TryGetValue(u, out m)) continue;
                if (m.Torpedoes > 0 || TorpedoLoadoutOn(m)) armedAny = true;
                foreach (var r in _targets.Values)
                    if (r.Group == g.Name && r.State != "done" && r.Shooter == u && (r.State == "approach" || r.State == "hold")) current[r.Id] = u;
            }
            // per-target times: Assign takes one time per shooter, so the plan is made target by target with the shooter's time to that target
            var plan = PlanAssign(g, targets, byId, current);
            st.Plan = plan;
            ApplyPlan(g, plan, byId, targets);
            HolderTask(g, byId);
            IdleAircraft(g, plan);
            DeckPrepare(g, plan);
            if (!armedAny && targets.Count > 0) { if (!st.NoArmedLogged) { st.NoArmedLogged = true; _game.Log("asw strike " + g.Name + " no armed aircraft: the contact is held, nobody attacks"); Warn(g, "no_armed", null, 300); } }
            else st.NoArmedLogged = false;
        }

        /// <summary>The assignment of the group: Assign() works with one time per shooter, the time depends on the target, so each target is planned with the shooters' own time to it, in priority order.</summary>
        private List<StrikeAssign> PlanAssign(Grp g, List<StrikeTarget> targets, Dictionary<int, AswContact> byId, Dictionary<int, int> current)
        {
            var result = new List<StrikeAssign>();
            var order = new List<StrikeTarget>(targets);
            order.Sort(AswStrikeMath.Priority);
            var taken = new HashSet<int>();
            // busy shooters keep their target
            foreach (var r in _targets.Values)
                if (r.Group == g.Name && r.State != "done" && r.Shooter != 0 && (r.State == "engaged" || r.State == "in_water" || r.State == "assess" || r.State == "verify" || r.State == "wait") && LiveOf(r.Shooter) != null)
                { taken.Add(r.Shooter); result.Add(new StrikeAssign { Target = r.Id, Uid = r.Shooter, Role = "shooter", TtwS = 0 }); }
            var done = new HashSet<int>();
            foreach (var a in result) done.Add(a.Target);
            foreach (var t in order)
            {
                if (done.Contains(t.Id)) continue;
                var c = byId[t.Id];
                var sub = new List<StrikeShooter>();
                var whys = new List<string>();
                foreach (int u in g.Members)
                {
                    if (taken.Contains(u)) continue;
                    AswMember m; if (!_live.TryGetValue(u, out m)) continue;
                    string why;
                    var sh = ShooterOf(g, m, c, out why);
                    if (sh == null) { whys.Add(I(u) + ":" + why); continue; }
                    sh.Holder = IsHolder(u, t.Id);
                    sub.Add(sh);
                }
                int cur = 0; foreach (var kv in current) if (kv.Key == t.Id) cur = kv.Value;
                var one = AswStrikeMath.Assign(new List<StrikeTarget> { t }, sub, cur != 0 ? new Dictionary<int, int> { { t.Id, cur } } : new Dictionary<int, int>(), _cfg.Strike.MaxEtaS);
                foreach (var a in one) { if (a.Role == "second") continue; taken.Add(a.Uid); result.Add(a); done.Add(t.Id); break; }
                if (done.Contains(t.Id)) g.St.LastKept.Remove(t.Id);
                TargetRec kr; _targets.TryGetValue(t.Id, out kr);
                if (!done.Contains(t.Id) && kr != null && kr.Group == g.Name && kr.Shooter != 0 && (kr.State == "approach" || kr.State == "hold" || kr.State == "wait" || kr.State == "verify" || kr.State == "engaged") && LiveOf(kr.Shooter) != null)
                {
                    // T-231 (S9): the shooter is already on its way; a failed fresh check (fuel after "verify -> approach") is no "no shooter"
                    string kwhy = "eta", pre = I(kr.Shooter) + ":";
                    foreach (string w in whys) if (w.StartsWith(pre, StringComparison.Ordinal)) kwhy = w.Substring(pre.Length);
                    KeptShooterNote(g, kr, kwhy);
                }
                else NoShooterNote(g, t.Id, done.Contains(t.Id), sub, one, whys);
            }
            // second numbers: the free airborne shooters, one per target with a shooter, in the priority order
            var free = new List<StrikeShooter>();
            foreach (int u in g.Members)
            {
                if (taken.Contains(u)) continue;
                AswMember m; if (!_live.TryGetValue(u, out m) || m.Where != "air") continue;
                var any = ShooterOf(g, m, byId.Count > 0 ? FirstOf(byId) : new AswContact { Lat = m.Lat, Lon = m.Lon });
                if (any != null) free.Add(any);
            }
            int fi = 0;
            foreach (var t in order)
            {
                if (fi >= free.Count) break;
                if (!done.Contains(t.Id)) continue;
                TargetRec r; _targets.TryGetValue(t.Id, out r);
                if (r != null && r.Second != 0 && Armed(LiveOf(r.Second)) && !taken.Contains(r.Second)) { taken.Add(r.Second); result.Add(new StrikeAssign { Target = t.Id, Uid = r.Second, Role = "second", TtwS = 0 }); free.RemoveAll(f => f.Uid == r.Second); continue; }
                var s = free[fi++];
                result.Add(new StrikeAssign { Target = t.Id, Uid = s.Uid, Role = "second", TtwS = s.TtwS });
            }
            return result;
        }

        /// <summary>T-222: nobody was assigned to the target: the reason per machine, one log line per change of the signature; cleared at an assignment.</summary>
        private void NoShooterNote(Grp g, int target, bool assigned, List<StrikeShooter> cands, List<StrikeAssign> one, List<string> whys)
        {
            var st = g.St;
            if (assigned) { st.LastNoShooter.Remove(target); return; }
            var all = new List<string>(whys);
            foreach (var sh in cands) all.Add(I(sh.Uid) + ":eta(ttw=" + I((int)Math.Round(sh.TtwS)) + ")");
            if (all.Count == 0) { st.LastNoShooter.Remove(target); return; }   // an empty group: the no_armed line speaks
            string sig = string.Join(",", all.ToArray()), prev;
            string coarse = System.Text.RegularExpressions.Regex.Replace(sig, @"\([^)]*\)", "");   // the numbers (fuel, time) change every tick: only the reasons count as a change
            if (st.LastNoShooter.TryGetValue(target, out prev) && System.Text.RegularExpressions.Regex.Replace(prev, @"\([^)]*\)", "") == coarse) return;
            st.LastNoShooter[target] = sig;
            _game.Log("asw strike " + g.Name + " no shooter contact=" + I(target) + " why=" + sig);
        }

        /// <summary>T-231 (S9): the fresh shooter check failed for a machine that is already attacking this target: one line per (target, shooter, reason), no "no shooter" card.</summary>
        private void KeptShooterNote(Grp g, TargetRec r, string why)
        {
            var st = g.St;
            st.LastNoShooter.Remove(r.Id);
            string sig = I(r.Shooter) + "|" + System.Text.RegularExpressions.Regex.Replace(why ?? "", @"\([^)]*\)", ""), prev;
            if (st.LastKept.TryGetValue(r.Id, out prev) && prev == sig) return;
            st.LastKept[r.Id] = sig;
            _game.Log("asw strike " + g.Name + " shooter kept contact=" + I(r.Id) + " uid=" + I(r.Shooter) + " why=" + why);
        }

        private static AswContact FirstOf(Dictionary<int, AswContact> d) { foreach (var v in d.Values) return v; return null; }

        private void ApplyPlan(Grp g, List<StrikeAssign> plan, Dictionary<int, AswContact> byId, List<StrikeTarget> targets)
        {
            var st = g.St;
            var assigned = new HashSet<int>();
            foreach (var a in plan)
            {
                TargetRec r; if (!_targets.TryGetValue(a.Target, out r) || r.State == "done") continue;
                AswMember m = LiveOf(a.Uid); if (m == null) continue;
                string key = I(a.Target) + "|" + I(a.Uid) + "|" + a.Role;
                if (a.Role == "second")
                {
                    if (r.Second != a.Uid) r.Second = a.Uid;
                }
                else if (a.Role == "deck")
                {
                    if (r.Shooter == 0) { st.DeckFor[a.Uid] = I(a.Target); AssignLog(g, a, byId, targets, "deck"); LaunchDeck(g, m, a); }
                }
                else if (r.Shooter != a.Uid && !(r.State == "engaged" || r.State == "in_water" || r.State == "assess"))
                {
                    r.Second = r.Second == a.Uid ? 0 : r.Second;
                    AssignLog(g, a, byId, targets, "shooter");
                    StartStrikeRun(g.Name, a.Target, a.Uid, "shooter");
                    NoteAlert(a.Target, "assign");
                }
                assigned.Add(a.Target);
            }
            foreach (var r in _targets.Values)
            {
                if (r.Group != g.Name || r.State == "done" || assigned.Contains(r.Id)) continue;
                // nobody can take it now: the run waits (a busy shooter stays; an idle one is already idle)
                if (r.Shooter != 0 && (r.State == "approach" || r.State == "hold" || r.State == "wait") && !Armed(LiveOf(r.Shooter))) LoseShooter(r, "no_shooter", false);
            }
        }

        private void AssignLog(Grp g, StrikeAssign a, Dictionary<int, AswContact> byId, List<StrikeTarget> targets, string role)
        {
            string sig = I(a.Uid) + "|" + role;
            string key = I(a.Target);
            string last;
            if (g.St.LastAssign.TryGetValue(key, out last) && last == sig) return;
            bool re = g.St.LastAssign.ContainsKey(key);
            g.St.LastAssign[key] = sig;
            int prio = 0;
            var ord = new List<StrikeTarget>(targets); ord.Sort(AswStrikeMath.Priority);
            for (int i = 0; i < ord.Count; i++) if (ord[i].Id == a.Target) prio = i + 1;
            if (re) _game.Log("asw strike " + g.Name + " rebalance reason=" + (role == "deck" ? "deck_faster" : "better_shooter"));
            if (role == "deck") { var m = LiveOf(a.Uid); _game.Log("asw strike " + g.Name + " deck uid=" + I(a.Uid) + " ship=" + I(m == null ? 0 : m.DeckUid) + " reason=no_air_shooter ttw_s=" + N(Math.Round(a.TtwS))); }
            _game.Log("asw strike " + g.Name + " assign contact=" + I(a.Target) + " uid=" + I(a.Uid) + " role=" + role + " ttw_s=" + N(Math.Round(a.TtwS)) + " prio=" + I(prio));
            _game.Event("asw_strike_assign", Obj("group", g.Name, "contact", a.Target, "uid", a.Uid, "role", role == "deck" ? "shooter" : role, "ttw_s", Math.Round(a.TtwS)));
        }

        // ---- the deck ------------------------------------------------------------------------------------------------

        private string StrikeCsv(AswMember m)
        {
            var names = TorpedoLoadoutNames(m);
            return names.Count > 0 ? string.Join(",", names.ToArray()) : _defaults.Loadouts;
        }

        /// <summary>A deck machine takes off for a target (the broker: step "ASW with a contact"); stage 2 of the air threat holds the launch.</summary>
        private void LaunchDeck(Grp g, AswMember m, StrikeAssign a)
        {
            if (AirLaunchBlocked(g)) return;
            DeckAction(g, "launch", m.Uid, "strike", StrikeCsv(m));
        }

        /// <summary>The deck machines without a target are prepared (not launched): ready when a contact comes.</summary>
        private void DeckPrepare(Grp g, List<StrikeAssign> plan)
        {
            foreach (int u in g.Members)
            {
                AswMember m;
                if (!_live.TryGetValue(u, out m) || m.Where == null || !m.Where.StartsWith("deck", StringComparison.Ordinal) || !TorpedoLoadoutOn(m) || DeckOut(g, m.DeckUid)) continue;
                if (KeyIs(m, "task:") || KeyIs(m, "cool:") || KeyIs(m, "unit:")) continue;
                if (m.Where != "deck_service") continue;
                DeckAction(g, "prepare", u, "strike", StrikeCsv(m));
            }
            // airborne > 0: keep that many aircraft in the air while nothing is attacked
            int want = g.St.Airborne;
            if (want <= 0) return;
            int air = 0;
            foreach (int u in g.Members) { AswMember m; if (_live.TryGetValue(u, out m) && m.Where == "air" && !AswMath.IsLeaving(m)) air++; }
            if (air >= want || AirLaunchBlocked(g)) return;
            // T-226 (Q-T192-2): the machine that can take off soonest (a ready task before a hangar row)
            var rows = new List<AswMember>();
            foreach (int u in g.Members) { AswMember m; if (_live.TryGetValue(u, out m)) rows.Add(m); }
            var pick = AswMath.SoonestDeckLaunch(rows, m => TorpedoLoadoutOn(m) && !DeckOut(g, m.DeckUid));
            if (pick != null) DeckAction(g, "launch", pick.Uid, "strike", StrikeCsv(pick));
        }

        /// <summary>The launch / preparation of a deck machine through the broker (ADR-0042), for the relief of a tactic and for the strike group (T-173).
        /// csvOverride = the loadouts to ask for (the strike group asks for a torpedo loadout).</summary>
        private void DeckAction(Grp g, string kindArg, int uidArg, string taskArg, string csvOverride)
        {
            string key = kindArg + uidArg;
            if (_relief.Paused(key, _now, kindArg == "prepare" ? 900 : 120)) return;
            if (_relief.InFlight(key)) return;
            AswMember m; if (!_live.TryGetValue(uidArg, out m)) return;
            if (DeckOut(g, m.DeckUid)) return;   // T-231: a knocked-out deck takes no orders (no Mark: the first tick after the repair orders)
            if (kindArg == "prepare" && (KeyIs(m, "task:") || KeyIs(m, "cool:"))) return;   // already being prepared / cooling: nothing to order
            bool blocked = kindArg == "launch" && AirLaunchBlocked(g);   // T-175: stage 2 of the air threat: the launch is carried out as "prepare"
            if (blocked && AswMath.LaunchRoute(m) == "allow") return;      // a prepared task stays prepared; "allow" goes out right after the threat
            string route = kindArg == "launch" ? AswMath.LaunchRoute(m) ?? "create" : "create";
            if (route == "create" && DeckAwaiting(g, m.DeckUid))
            {
                // T-181 (ADR-0080 s.4): a task of the group waits for a place on this deck: a new order would only take a machine out of the hangar
                string fk = g.Name + "|deck_full" + I(m.DeckUid);
                double fl;
                if (!_warnAt.TryGetValue(fk, out fl) || _now - fl >= 60 || _now < fl) { _warnAt[fk] = _now; _game.Log("asw deck full " + g.Name + " deck=" + I(m.DeckUid) + " (own task in await_deck)"); }
                return;
            }
            _relief.Mark(key, _now);
            DeckRequest req;
            string rerr = null;
            if (route == "allow") req = AirRelief.Allow(m.DeckUid, m.DeckKey, "asw " + kindArg + " " + taskArg);
            else
            {
                string csv = csvOverride ?? AswMath.LoadoutCsvFor(g.Loadouts, m.Type, g.P.Loadouts);
                string lo = _game.ResolveLoadout(m.DeckUid, m.Type, csv, out rerr);
                if (lo == null && csv != g.P.Loadouts && (rerr == "no_ammo" || rerr == "unknown_loadout"))
                {
                    // T-142 K: the chosen loadout is not available on this deck (no ammunition): the configured list takes over, the window is told
                    Warn(g, "loadout_fallback", m.Type + ": " + csv, 600);
                    lo = _game.ResolveLoadout(m.DeckUid, m.Type, g.P.Loadouts, out rerr);
                }
                req = AirRelief.Order(m.DeckUid, m.Type, lo, null, kindArg == "launch" && !blocked);
                if (lo == null) rerr = rerr ?? "launch_failed";
            }
            string what = kindArg == "launch" ? "launch " : "prepare ";
            var kind = kindArg; var taskId = taskArg; int auid = uidArg;
            Action<DeckResult> body = res =>
            {
                string err = res.Err;
                _game.Log("asw " + what + g.Name + " uid=" + I(m.Uid) + " key=" + (m.DeckKey ?? "-") + " route=" + route + " -> " + (err ?? "ok") + AirRelief.HeldTag(res));
                if (err == null)
                {
                    if (route == "create" && res.Key != null && res.Key != m.DeckKey && g.Slots.ContainsKey(auid))
                    {
                        // T-181 (ADR-0080 s.3): the slot member becomes the order at once; its task row joins it when it is listed
                        HandOver(g, auid, res.Key, taskId, kind == "launch" && !blocked);
                    }
                    else if (route == "create" && res.Key != null && res.Key != m.DeckKey)
                    {
                        // T-150 P1: the new task is ours; its row joins the group (AdoptOwnTasks). A legacy stack row stays as the source of the next machines.
                        g.OwnKeys[res.Key] = _now;
                        g.OwnInfo[res.Key] = new OwnRec { Task = taskId, Type = m.Type, DeckUid = m.DeckUid };
                        g.Assign[auid] = "reserve";
                    }
                    else if (kind == "launch")
                    {
                        g.Assign[auid] = taskId;
                        if (route == "allow") g.Allowed.Add(auid);
                        var a0 = StartAdopt(g, auid, m, taskId, "launch");
                        a0.Known = new HashSet<int>(); foreach (var l in _live.Values) if (l.Where == "air") a0.Known.Add(l.Uid);
                    }
                    _game.Event("asw_relief", Obj("group", g.Name, "kind", kind, "uid", auid, "aircraft", m.Name, "task", taskId));
                }
                else if (err == "deck_inoperable") DeckOut(g, m.DeckUid);   // T-231: the deck was knocked out between the check and the order
                else Warn(g, "launch_failed", err, 120);
            };
            if (rerr != null)
            {
                _game.Log("asw " + what + g.Name + " uid=" + I(m.Uid) + " key=" + (m.DeckKey ?? "-") + " route=" + route + " -> " + rerr);
                Warn(g, "launch_failed", rerr, 120);
                return;
            }
            SubmitDeck(g, key, req, body);
        }

        // ---- idle aircraft -------------------------------------------------------------------------------------------

        private void IdleAircraft(Grp g, List<StrikeAssign> plan)
        {
            var st = g.St;
            var rs = st.RefUid != 0 ? _game.Target(st.RefUid) : null;
            int kept = 0;
            foreach (int u in g.Members)
            {
                AswMember m; if (!_live.TryGetValue(u, out m) || m.Where != "air" || Excluded(m)) continue;
                if (Attacking(u) || AirRetreatOf(u)) { st.IdleSince.Remove(u); continue; }
                bool assigned = false;
                foreach (var a in plan) if (a.Uid == u) assigned = true;
                if (assigned) { st.IdleSince.Remove(u); continue; }
                double since;
                if (!st.IdleSince.TryGetValue(u, out since)) { st.IdleSince[u] = _now; since = _now; if (m.Lat != 0 || m.Lon != 0) st.IdlePt[u] = new GeoPt(m.Lat, m.Lon); }
                if (AswMath.IsLeaving(m)) continue;
                if (st.Airborne > kept && !(st.Airborne == 0)) { kept++; HoverOverRef(g, m, rs); continue; }
                if (_now - since >= _cfg.Strike.IdleS && !st.Rtb.Contains(u) && !st.NoHome.Contains(u))
                {
                    // 2026-10-22: a land-based machine without a home base (P-3C of a mission without an air base) is not sent to land: it orbits
                    string err = AswStrikeMath.HasHome(m) ? _game.SarRtb(u) : "no_home_base";
                    if (err == null) st.Rtb.Add(u); else st.NoHome.Add(u);
                    _game.Log("asw strike " + g.Name + " idle uid=" + I(u) + (err == null ? " lands after " + N(Math.Round(_now - since)) + " s -> ok"
                        : " after " + N(Math.Round(_now - since)) + " s: no landing (" + err + ") -> orbits " + (rs != null ? "over ref" : "the standby point")));
                    if (err != null) HoverOverRef(g, m, rs, true);
                }
                else if (!st.Rtb.Contains(u)) HoverOverRef(g, m, rs);
            }
        }

        /// <summary>The idle machine waits over the reference (a machine that cannot land: over its standby point when there is none); force = the order now
        /// (the switch to orbiting), else at most every 300 s and only when it is more than 3 nm away.</summary>
        private void HoverOverRef(Grp g, AswMember m, AswTarget rs, bool force = false)
        {
            GeoPt pt;
            if (rs != null) pt = Pos(rs);
            else if (!g.St.NoHome.Contains(m.Uid) || !g.St.IdlePt.TryGetValue(m.Uid, out pt)) return;   // no reference: only a machine that cannot land holds its standby point
            double last;
            if (!force && g.St.HomeAt.TryGetValue(m.Uid, out last) && _now - last < 300) return;
            if (!force && m.Lat != 0 && AswMath.DistNm(new GeoPt(m.Lat, m.Lon), pt) <= 3) { g.St.HomeAt[m.Uid] = _now; return; }
            g.St.HomeAt[m.Uid] = _now;
            string err = _game.Goto(m.Uid, new List<GeoPt> { pt }, DropAlt(m.Uid));
            _game.Log("asw strike " + g.Name + " standby uid=" + I(m.Uid) + (rs != null ? " over ref" : " over the standby point") + " -> " + (err ?? "ok"));
        }

        // ---- the holder of the contact -------------------------------------------------------------------------------

        /// <summary>A target that needs a fresh point: the holder of the search group with this contact dips again at once; else a member of the group with a dipping sonar does, off the datum.</summary>
        private void HolderTask(Grp g, Dictionary<int, AswContact> byId)
        {
            foreach (var r in _targets.Values)
            {
                if (r.Group != g.Name || r.State == "done" || !r.WantFix) continue;
                bool done = false;
                foreach (var sg in _groups) if (sg.Order != "strike") foreach (var cr in sg.Contacts) if (cr.Id == r.Id) { cr.NextDipS = Math.Min(cr.NextDipS, _now); done = true; }
                if (done) continue;
                double last;
                if (g.St.FixDipAt.TryGetValue(r.Id, out last) && _now - last < _defaults.DipHoldS) continue;
                foreach (int u in g.Members)
                {
                    AswMember m;
                    if (u == r.Shooter || u == r.Second || !_live.TryGetValue(u, out m) || m.Where != "air" || !m.Dipping || Excluded(m)) continue;
                    AswContact c; byId.TryGetValue(r.Id, out c);
                    double ang = c == null ? double.NaN : c.CourseDeg;
                    var off = AswLocalizeMath.DipOffset(SpookR(_cfg.Loc), ang, 1);
                    var pt = AswMath.ToGeo(new GeoPt(r.Lat, r.Lon), 0, off);
                    g.St.FixDipAt[r.Id] = _now;
                    _game.Dip(m.Uid, new List<GeoPt> { pt });
                    _game.Log("asw strike " + g.Name + " fix contact=" + I(r.Id) + " dip uid=" + I(m.Uid));
                    break;
                }
            }
        }

        // ---- the card "submarine detected" ---------------------------------------------------------------------------

        private sealed class AlertRec
        {
            public int Id, Serial = 1, RefUid;
            public string Track, Side, Class, Source, RefName, Group, Line = "none", Episode = "detected";
            public string[] Args = new string[0];
            public double ChangedS, Brg, Dist;
            public bool Summary, Quiet;   // Quiet: seen at the load (covered by the summary card): no card of its own until an episode changes
        }

        private readonly Dictionary<int, AlertRec> _alerts = new Dictionary<int, AlertRec>();
        private readonly HashSet<int> _preSeen = new HashSet<int>();
        private readonly Dictionary<int, string> _pendingNote = new Dictionary<int, string>();

        /// <summary>An episode of the card changed (killed | lost | hold_own_sub | hold_neutral | hold_air_threat | surfaced). A new serial is a new show of the card.</summary>
        private void NoteAlert(int id, string what)
        {
            AlertRec a;
            if (!_alerts.TryGetValue(id, out a)) { _pendingNote[id] = what; return; }   // the card of this track is made later in the tick
            if (what == "killed" || what == "lost" || what == "hold_own_sub") a.Quiet = false;
            switch (what)
            {
                case "killed": a.Episode = "killed"; a.Line = "killed"; a.Serial++; break;
                case "lost": a.Episode = "lost"; a.Line = "lost"; a.Serial++; break;
                case "hold_own_sub": a.Episode = "hold_own_sub"; a.Serial++; break;
                case "hold_neutral": a.Episode = "hold_neutral"; break;
                case "hold_air_threat": a.Episode = "hold_air"; break;
                case "surfaced": a.Line = "surfaced"; break;
                default: return;
            }
            a.ChangedS = _now;
        }

        private StrikePolicy AlertPolicy() { return new StrikePolicy { Targets = "hostile_unknown", UnknownConfirmS = 0, RadiusNm = 0 }; }

        private void UpdateAlerts()
        {
            var own = _game.OwnSubs(); var ownIds = OwnTrackIds(own);
            var ships = _game.OwnShips();
            var seen = new HashSet<int>();
            foreach (var c in SubsTick())
            {
                if (c.Killed) continue;
                var e = AswStrikeMath.Eligible(ToTrack(c), AlertPolicy(), own, ownIds);
                if (!e.Ok) continue;
                seen.Add(c.Id);
                AlertRec a;
                bool isNew = !_alerts.TryGetValue(c.Id, out a);
                if (isNew) { a = new AlertRec { Id = c.Id, Track = c.Name, ChangedS = _now, Quiet = _preSeen.Contains(c.Id) }; _alerts[c.Id] = a; }
                a.Side = e.Unknown ? "unknown" : "hostile"; a.Class = c.ClassName; a.Source = c.Source;
                ShipRef(c, ships, a);
                string pend;
                if (_pendingNote.TryGetValue(c.Id, out pend)) { _pendingNote.Remove(c.Id); NoteAlert(c.Id, pend); }
                StatusLine(c, a, e);
                if (isNew && !a.Quiet)
                {
                    string src = c.Source ?? "track";
                    _game.Log("asw sub detected contact=" + I(c.Id) + " track=" + c.Name + " side=" + a.Side + " class=" + (c.ClassName ?? "-") + " src=" + src + " brg=" + N(Math.Round(a.Brg)) + " nm=" + N(Math.Round(a.Dist, 1)) + " ref=" + (a.RefName ?? "-") + " strike=" + (a.Group ?? "none") + " reason=" + a.Line);
                    _game.Event("asw_sub_detected", Obj("contact", c.Id, "track", c.Name, "side", a.Side, "class", c.ClassName, "source", src, "brg", Math.Round(a.Brg), "nm", Math.Round(a.Dist, 1), "ref_uid", a.RefUid, "strike_group", a.Group, "status", a.Line));
                }
            }
            _pendingNote.Clear();
            foreach (int id in new List<int>(_alerts.Keys))
            {
                var a = _alerts[id];
                if (a.Summary) { if (_now - a.ChangedS > 120) _alerts.Remove(id); continue; }
                if (!seen.Contains(id) && _now - a.ChangedS > 120) _alerts.Remove(id);
            }
        }

        private void ShipRef(AswContact c, IList<AswShipPos> ships, AlertRec a)
        {
            var cp = new GeoPt(c.Lat, c.Lon);
            GeoPt rp = cp; bool have = false; a.RefUid = 0; a.RefName = null;
            foreach (var g in _groups)
            {
                if (g.Order != "strike" || g.St.RefUid == 0) continue;
                var rs = _game.Target(g.St.RefUid);
                if (rs != null && (!have || (_targets.ContainsKey(c.Id) && _targets[c.Id].Group == g.Name))) { rp = Pos(rs); have = true; a.RefUid = rs.Uid; a.RefName = rs.Name; }
            }
            if (!have)
                foreach (var g in _groups)
                    foreach (var rec in g.Contacts)
                        if (rec.Id == c.Id && g.Order == "course") { var t = _game.Target(g.TargetUid); if (t != null) { rp = Pos(t); have = true; a.RefUid = t.Uid; a.RefName = t.Name; } }
            if (!have && ships != null && ships.Count > 0)
            {
                double best = double.MaxValue;
                foreach (var s in ships) { double d = AswMath.DistNm(new GeoPt(s.Lat, s.Lon), cp); if (d < best) { best = d; rp = new GeoPt(s.Lat, s.Lon); have = true; a.RefUid = s.Uid; a.RefName = s.Name; } }
            }
            if (have) { a.Brg = AswMath.BearingDeg(rp, cp); a.Dist = AswMath.DistNm(rp, cp); }
        }

        private static string Mmss(double s) { int t = (int)Math.Round(Math.Max(0, s)); return I(t / 60) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture); }

        /// <summary>The one line of the card by the priority of ADR-0074 item 1.4.</summary>
        private void StatusLine(AswContact c, AlertRec a, StrikeElig e)
        {
            if (a.Episode == "killed" || a.Episode == "lost") return;
            TargetRec r; _targets.TryGetValue(c.Id, out r);
            a.Group = null;
            if (r != null && r.State != "done")
            {
                a.Group = r.Group;
                AswMember sm = LiveOf(r.Shooter);
                if (r.State == "hold")
                {
                    if (r.Hold == "own_sub") { a.Line = "hold_ownsub"; a.Args = new[] { r.OwnSubName ?? "-", N(Math.Round(r.OwnSubNm, 1)) }; return; }
                    if (r.Hold == "neutral") { a.Line = "hold_neutral"; a.Args = new string[0]; return; }
                    if (r.Hold == "air_threat") { a.Line = "hold_air"; a.Args = new[] { AirThreatText(sm) }; return; }
                    if (r.Hold == "disabled") { a.Line = "disabled"; a.Args = new string[0]; return; }
                }
                if (sm != null)
                {
                    double eta = r.State == "in_water" ? Math.Max(0, 600 - (_now - r.DropS)) : AswStrikeMath.TtwAir(Math.Max(0, AswMath.DistNm(new GeoPt(sm.Lat, sm.Lon), new GeoPt(r.Lat, r.Lon)) - DropRange(sm.Uid)), Kn(sm), false, 0);
                    a.Line = "strike"; a.Args = new[] { r.Group, sm.Name, Mmss(eta) }; return;
                }
                if (r.Shooter == 0) { NoShooterLine(a, FindGroup(r.Group), c.Id); return; }
            }
            Grp any = null; int strikes = 0;
            foreach (var g in _groups) if (g.Order == "strike") { strikes++; if (any == null) any = g; }
            if (strikes == 0) { a.Line = "none"; a.Args = new string[0]; return; }
            foreach (var g in _groups)
            {
                if (g.Order != "strike") continue;
                StrikeElig ge;
                if (!g.St.Elig.TryGetValue(c.Id, out ge)) continue;
                if (ge.Ok) { a.Group = g.Name; if (_game.AllowAttack) NoShooterLine(a, g, c.Id); else { a.Line = "disabled"; a.Args = new string[0]; } return; }
                if (ge.Reason == "out_of_zone" && g.St.RadiusNm > 0)
                {
                    var rs = _game.Target(g.St.RefUid);
                    double d = rs == null ? 0 : AswMath.DistNm(Pos(rs), new GeoPt(c.Lat, c.Lon));
                    a.Line = "out_of_zone"; a.Args = new[] { g.Name, N(Math.Round(d)), N(Math.Round(g.St.RadiusNm)) }; return;
                }
                if (ge.Reason == "not_hostile") { a.Line = "hostile_only"; a.Args = new string[0]; return; }
                if (ge.Reason == "unknown_young") { a.Line = "young"; a.Args = new[] { N(Math.Round(_cfg.Strike.UnknownConfirmS)) }; return; }
            }
            a.Line = "none"; a.Args = new string[0];
        }

        private Grp FindGroup(string name) { return Find(name); }

        /// <summary>T-222: no shooter on the card: the reason of the first machine when the planner has one, else the old "no armed aircraft".</summary>
        private void NoShooterLine(AlertRec a, Grp g, int contactId)
        {
            string sig;
            if (!_game.AllowAttack) { a.Line = "disabled"; a.Args = new string[0]; return; }
            if (g != null && g.St != null && g.St.LastNoShooter.TryGetValue(contactId, out sig) && sig.Length > 0)
            {
                int i = sig.IndexOf(':'); string first = sig.Substring(i + 1); int j = first.IndexOf(','); if (j >= 0) first = first.Substring(0, j);
                a.Line = "no_shooter"; a.Args = new[] { UiText.Get("asw.toast.why." + WhyShort(first)) };
                return;
            }
            a.Line = "no_armed"; a.Args = new string[0];
        }

        private string AirThreatText(AswMember m)
        {
            try { var th = m == null || AirGuard == null ? null : AirGuard.ForUnit(m.Uid); if (th != null) return th.Raid ?? (I((int)Math.Round(th.Nm)) + " nm"); } catch { }
            return "-";
        }

        /// <summary>After a load: one summary card "Submarine targets: N" for the live tracks (no dedupe state is saved).</summary>
        private void SummaryAlert()
        {
            int n = 0;
            _preSeen.Clear();
            foreach (var c in SubsTick()) if (c.Live && !c.Killed && c.Decoy == null && c.Biologic == null) { n++; _preSeen.Add(c.Id); }
            if (n == 0) return;
            _alerts[-1] = new AlertRec { Id = -1, Track = null, Line = "summary", Episode = "summary", Args = new[] { I(n) }, ChangedS = _now, Summary = true };
        }

        // ---- the view ------------------------------------------------------------------------------------------------

        private void AlertsView(AswView v)
        {
            foreach (var a in _alerts.Values)
                if (!a.Quiet) v.SubAlerts.Add(new AswSubAlertView { ContactId = a.Id, RefUid = a.RefUid, Serial = a.Serial, Track = a.Track, Side = a.Side, Class = a.Class, Source = a.Source, RefName = a.RefName, Group = a.Group,
                    Line = a.Line, Episode = a.Episode, LineArgs = a.Args, BrgDeg = a.Brg, DistNm = a.Dist });
        }

        private static string StateCode(TargetRec r, double distToDrop)
        {
            switch (r.State)
            {
                case "idle": return r.Shooter != 0 ? "assigned" : "waiting";
                case "verify": return "verify";
                case "approach": return distToDrop <= 0.3 ? "drop" : "approach";
                case "engaged": return "drop";
                case "in_water": return "in_water";
                case "assess": return "assess";
                case "wait": return "repeat";
                case "hold": return r.Hold == "stale_fix" ? "need_fix" : "hold";
                case "reacquire": return "lost";
                default:
                    switch (r.Done) { case "killed": return "killed"; case "skipped": return "skipped"; case "limit": return "limit"; case "surfaced": return "surfaced"; case "lost": return "lost"; default: return "waiting"; }
            }
        }

        private void StrikeView(Grp g, AswGroupView gv)
        {
            var st = g.St;
            gv.RefUid = st.RefUid; gv.Targets = st.Targets; gv.StrikeRadiusNm = st.RadiusNm; gv.Airborne = st.Airborne;
            var rs = st.RefUid != 0 ? _game.Target(st.RefUid) : null;
            gv.RefName = rs == null ? null : rs.Name;
            var shown = new HashSet<int>();
            foreach (var r in _targets.Values)
            {
                if (r.Group != g.Name) continue;
                AswContact c; _subsById.TryGetValue(r.Id, out c);
                var sm = LiveOf(r.Shooter);
                double dd = sm == null ? 99 : Math.Max(0, AswMath.DistNm(new GeoPt(sm.Lat, sm.Lon), new GeoPt(r.Lat, r.Lon)) - DropRange(sm.Uid));
                string code = StateCode(r, dd);
                var tv = new AswStrikeTargetView { ContactId = r.Id, Track = r.Name, Side = r.Unknown ? "unknown" : "hostile", Status = "asw.strike.st." + code, StatusArg = code == "in_water" ? I(r.Torps) : null,
                    ShooterUid = r.Shooter, Shooter = sm == null ? null : sm.Name, Second = r.Second, Torpedoes = r.Torps, Hits = _game.HitsOn(r.Id) < 0 ? -1 : r.Hits, Skipped = r.Done == "skipped", Lat = r.Lat, Lon = r.Lon };
                gv.StrikeTargets.Add(tv); shown.Add(r.Id);
            }
            foreach (var kv in st.Elig)
            {
                if (shown.Contains(kv.Key)) continue;
                AswContact c; _subsById.TryGetValue(kv.Key, out c);
                bool skipped = st.Skip.Contains(kv.Key);
                if (c == null || (!kv.Value.Ok && !skipped)) continue;
                gv.StrikeTargets.Add(new AswStrikeTargetView { ContactId = c.Id, Track = c.Name, Side = kv.Value.Unknown ? "unknown" : "hostile", Status = "asw.strike.st." + (skipped ? "skipped" : "waiting"), Skipped = skipped, Lat = c.Lat, Lon = c.Lon });
            }
            gv.StrikeTargets.Sort((a, b) => a.ContactId.CompareTo(b.ContactId));
            gv.Status = g.Members.Count == 0 ? "empty" : gv.StrikeTargets.Count > 0 ? "contact" : "searching";
            // the aim lines: shooter -> the drop point
            foreach (var r in _targets.Values)
            {
                if (r.Group != g.Name || r.State == "done") continue;
                var sm = LiveOf(r.Shooter); if (sm == null || (sm.Lat == 0 && sm.Lon == 0)) continue;
                var line = new AswLineView { Kind = "strike_aim" };
                line.Points.Add(new GeoPt(sm.Lat, sm.Lon)); line.Points.Add(PointToward(new GeoPt(r.Lat, r.Lon), sm, DropRange(sm.Uid)));
                gv.Lines = new List<AswLineView>(gv.Lines) { line };
            }
            foreach (var row in gv.MemberRows)
            {
                row.StrikeTask = "idle";
                foreach (var r in _targets.Values)
                    if (r.Group == g.Name && r.State != "done" && (r.Shooter == row.Uid || r.Second == row.Uid)) { row.StrikeTask = "target"; row.StrikeArg = r.Name; }
                if (row.StrikeTask == "idle" && row.State != null && row.State.StartsWith("deck", StringComparison.Ordinal)) { row.StrikeTask = "deck"; row.StrikeArg = Mmss(row.ReadyInS); }
            }
        }

        /// <summary>The machines that could join a strike group and the ones that could not, with the reason.</summary>
        private void StrikeLists(AswView v)
        {
            foreach (var m in _live.Values)
            {
                var own = GroupOf(m.Uid);
                if (own != null) { v.ExcludedStrike.Add(new AswExcludedRow { Row = ToRow(m, own), Reason = "in_group:" + own.Name, OwnerWindow = "asw" }); continue; }   // 2026-10-22: as the other lists
                var row = ToRow(m, null);
                string reason = ForeignOwner(m.Uid) != null ? "in_group:" + ForeignOwner(m.Uid) : AswStrikeMath.StrikeExclusion(m, TorpedoLoadoutOn(m));
                if (reason == null && !AswMath.Usable(m)) reason = m.Where == "deck_launching" ? "launching" : "recovering";
                if (reason == null) v.AvailableStrike.Add(row); else v.ExcludedStrike.Add(new AswExcludedRow { Row = row, Reason = reason });
            }
        }
    }
}
