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
        // ---- state, view -------------------------------------------------------------------------------------------

        public string StateJson()
        {
            var sb = new StringBuilder("{\"active\":").Append(Json.Bool(_view.Active)).Append(",\"groups\":[");
            bool first = true;
            foreach (var g in _groups)
            {
                if (!first) sb.Append(','); first = false;
                var contacts = new StringBuilder("[");
                for (int i = 0; i < g.Contacts.Count; i++)
                {
                    var c = g.Contacts[i];
                    if (i > 0) contacts.Append(',');
                    contacts.Append(Obj("id", c.Id, "track", c.Name, "lat", c.Lat, "lon", c.Lon));
                }
                contacts.Append(']');
                sb.Append(Obj("name", g.Name, "auto_name", g.AutoName, "order", g.Order, "target", g.TargetName, "tactics", new Raw(TacticsJson(g)), "members", g.Members.Count,
                               "r_eff_nm", g.RNm, "r_source", g.RSource, "buoys", g.Buoys.Count, "covered", g.Covered, "contacts", new Raw(contacts.ToString())
                               , "sar", g.Order != "sar" ? null : new Raw(Obj("area", g.SarArea.Kind, "on_complete", g.SarOnComplete, "status", g.SarStatus, "rafts", g.AreaRafts,
                                   "people", g.AreaPeople, "active", g.Active, "rescued", g.Rescued, "next_launch_s", g.NextLaunchS))
                               , "plan", g.Order == "course" || g.Order == "area" ? new Raw(PlanJson(PlanViewOf(g))) : null, "depth", g.Depth, "area_pattern", g.Order == "area" ? g.Plan.AreaPattern : null
                               , "strike", g.Order != "strike" ? null : new Raw(StrikeJson(g))
                               , "air_retreat", new Raw(RetreatJson(RetreatOfGroup(g)))   // T-180
                               , "member_dip", new Raw(MemberDipJson(g))   // T-188
                               , "radar", new Raw(RadarJson(g))   // T-241
                               , "barrier_stale", new Raw(BarrierStaleJson(g))   // T-322
                               ));
            }
            sb.Append("],\"proposals\":[],\"restore_waiting\":").Append(Json.Bool(_deferred != null)).Append(",\"alerts\":").Append(AlertsJson());   // "proposals" stays an empty list for old clients (the voice mod reads it)
            sb.Append(",\"warnings\":[");
            for (int i = 0; i < _warnings.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Str(_warnings[i].Text)); }
            sb.Append("],\"available\":[");
            for (int i = 0; i < _view.Available.Count; i++) { if (i > 0) sb.Append(','); sb.Append(RowJson(_view.Available[i], null)); }
            sb.Append("],\"excluded\":[");
            for (int i = 0; i < _view.Excluded.Count; i++) { if (i > 0) sb.Append(','); sb.Append(RowJson(_view.Excluded[i].Row, _view.Excluded[i].Reason, _view.Excluded[i].OwnerWindow)); }
            sb.Append(']');
            sb.Append(",\"available_sar\":[");
            for (int i = 0; i < _view.AvailableSar.Count; i++) { if (i > 0) sb.Append(','); sb.Append(RowJson(_view.AvailableSar[i], null)); }
            sb.Append("],\"excluded_sar\":[");
            for (int i = 0; i < _view.ExcludedSar.Count; i++) { if (i > 0) sb.Append(','); sb.Append(RowJson(_view.ExcludedSar[i].Row, _view.ExcludedSar[i].Reason, _view.ExcludedSar[i].OwnerWindow)); }
            sb.Append("],\"ships_sar\":").Append(_view.ShipsSarJson).Append(",\"rafts\":").Append(RaftsJson(_view.Rafts)).Append(",\"raft_scan\":").Append(_view.RaftScanJson ?? "null").Append(",\"rescued_by_mod\":").Append(I(Sar == null ? 0 : Sar.RescuedByMod));
            return sb.Append('}').ToString();
        }

        private static string RowJson(AswRow r, string reason, string ownerWindow = null)
        {
            return Obj("uid", r.Uid, "name", r.Name, "type", r.Type, "state", r.State, "ready_in_s", r.ReadyInS, "buoys", r.Passive + r.ActiveBuoys + r.Vlad, "torpedoes", r.Torpedoes, "reason", reason,
                       "onboard", r.Onboard, "capacity", r.Capacity, "target_raft", r.TargetRaft > 0 ? (object)r.TargetRaft : null,
                       "deck_key", r.DeckKey, "slot", r.Slot, "hangar_n", r.HangarN,   // T-181 (ADR-0080 s.8): additive
                       "owner_window", ownerWindow,
                       "dipping", r.Dipping, "dip_src", r.DipSrc, "dip_note", r.DipNote,   // T-188: additive
                       "hangar_prep_s", r.HangarPrepS);   // T-192: additive, -1 = not a hangar row   // T-186 (ADR-0082 s.5): additive; null when the reason is not in_group
        }

        public AswView View() { return _view; }

        /// <summary>T-188: per member, whether it can dip and where the answer comes from (type | guess | live | refused) and why the type's sonar is not loaded.</summary>
        private string MemberDipJson(Grp g)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (int u in g.Members)
            {
                AswMember m;
                if (!_live.TryGetValue(u, out m)) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append(Obj("uid", u, "type", m.Type, "dipping", m.Dipping, "src", m.DipSrc, "note", m.DipNote));
            }
            return sb.Append(']').ToString();
        }

        private string StrikeJson(Grp g)
        {
            var st = g.St;
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var r in _targets.Values)
            {
                if (r.Group != g.Name) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append(Obj("contact", r.Id, "track", r.Name, "state", r.State, "hold", r.Hold, "done", r.Done, "shooter", r.Shooter, "second", r.Second, "torpedoes", r.Torps, "hits", r.Hits));
            }
            sb.Append(']');
            return Obj("ref", st.RefUid, "radius_nm", st.RadiusNm, "targets", st.Targets, "airborne", st.Airborne, "targets_list", new Raw(sb.ToString()));
        }

        private string AlertsJson()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var a in _alerts.Values)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(Obj("contact", a.Id, "track", a.Track, "serial", a.Serial, "episode", a.Episode, "line", a.Line, "group", a.Group));
            }
            return sb.Append(']').ToString();
        }

        private void RebuildView()
        {
            var v = new AswView();
            foreach (var m in _live.Values)
            {
                if (v.RefLat == 0 && m.Lat != 0) { v.RefLat = m.Lat; v.RefLon = m.Lon; }
                var g = GroupOf(m.Uid);
                var row = ToRow(m, g);
                v.Aircraft.Add(row);
                // 2026-10-22 (customer: "P-3C is not in the ASW list"): a member of a group of this window (search, SAR, strike) is listed too,
                // as "in group X", so the other groups see where it is (the SAR list did it already)
                if (g != null) { v.Excluded.Add(new AswExcludedRow { Row = row, Reason = "in_group:" + g.Name, OwnerWindow = "asw" }); continue; }
                string reason = AswMath.ExclusionReason(m);
                string foreignWin;
                string foreign = ForeignOwner(m.Uid, out foreignWin);
                if (foreign != null) { if (!AirOpsMath.ShowForeignSlots && IsSlot(m)) continue; reason = "in_group:" + foreign; }   // T-186 (Q3): a slot of the other window is shown with the reason
                if (reason == null && !AswMath.Usable(m)) reason = m.Where == "deck_launching" ? "launching" : "recovering";
                if (reason == null) v.Available.Add(row);
                else v.Excluded.Add(new AswExcludedRow { Row = row, Reason = reason, OwnerWindow = foreign != null ? foreignWin : null });
            }
            foreach (var gr in _gone.Values) v.Excluded.Add(new AswExcludedRow { Row = gr.Row, Reason = gr.Reason });
            foreach (var m in _live.Values)   // which machines a SAR group could take (the window shows this list for a group with the order "sar")
            {
                var g = GroupOf(m.Uid);
                var row = ToRow(m, g);
                string foreignWinSar;
                string foreignSar = ForeignOwner(m.Uid, out foreignWinSar);
                string reason = g != null ? "in_group:" + g.Name : foreignSar != null ? "in_group:" + foreignSar : AswMath.SarExclusionReason(m);
                if (reason == null && !AswMath.Usable(m)) reason = m.Where == "deck_launching" ? "launching" : "recovering";
                if (reason == null) v.AvailableSar.Add(row); else v.ExcludedSar.Add(new AswExcludedRow { Row = row, Reason = reason, OwnerWindow = g != null ? "asw" : foreignSar != null ? foreignWinSar : null });
            }
            _sarAir.View(v);
            var targets = new List<AswTargetOption>();
            try { var ct = _game.CourseTargets(); if (ct != null) targets.AddRange(ct); } catch (Exception e) { _game.Log("asw course targets error: " + e.Message); }
            v.CourseTargets = targets;
            v.Defaults = CopyPat(_defaults.Pat); v.AreaSizeNm = _defaults.AreaSizeNm;
            try { var sel = _game.SelectedShip(); v.SelectedShipUid = sel == null ? 0 : sel.Uid; } catch (Exception e) { _game.Log("asw selected ship error: " + e.Message); }
            foreach (var g in _groups)
            {
                var gv = new AswGroupView { Name = g.Name, Order = g.Order, TargetName = g.TargetName, Depth = g.Depth, RSource = g.RSource, RNm = g.RNm,
                    AutoName = g.AutoName, Tactics = new List<string>(g.Tactics), Members = new List<int>(g.Members), Rect = g.Order == "area" ? g.Rect : null, CoveredFrac = g.Covered,
                    NextBarrierInS = g.NextCrossAt > _now ? g.NextCrossAt - _now : -1, ReliefInS = g.ReliefIn, Blink = g.Contacts.Count > 0, Lines = g.Lines,
                    DeckWaitS = Deck != null ? Deck.HeldS("asw", g.Name) : -1 };
                foreach (int mu in g.Members) { AswMember lm; if (_live.TryGetValue(mu, out lm)) gv.MemberRows.Add(ToRow(lm, g)); }
                gv.TargetUid = g.TargetUid; gv.TargetKind = g.TargetKind; gv.CourseTargets = targets;
                gv.AxisDeg = g.Order == "course" && g.FrameSet ? g.Axis : -1; gv.ReplanInS = g.ReplanInS;
                LoadoutRowsOf(g, gv);
                if (g.Order == "area" && g.AreaBuilt) gv.AreaPlan = AswDrawPlan.AreaPlan(g.Tactics, g.Plan.OpenAll, 0, g.Plan.OpenDip, 0, g.Plan.OpenMad, 0);   // T-174 W3
                PlanView(g, gv);   // T-176 C12
                RetreatView(g, gv);   // T-180
                gv.GapMin = g.ReliefIn > 0 ? g.ReliefIn : -1;
                gv.TargetText = g.Order == "course" ? g.TargetName : g.Order == "area" ? (g.TargetName != null ? UiText.Fmt("asw.target.near", g.TargetName) : UiText.Get("asw.target.area")) : null;
                gv.Status = g.Members.Count == 0 ? "empty" : g.Order == "none" ? "none" : g.Contacts.Count > 0 ? "contact" : g.Status == "no_target" ? "no_target" : g.ReliefIn > 0 ? "gap" : "searching";
                if (g.Order == "sar")
                {
                    gv.Status = g.Members.Count == 0 ? "empty" : (string.IsNullOrEmpty(g.SarStatus) ? "searching" : g.SarStatus);
                    gv.SarArea = g.SarArea.Kind; gv.SarOnComplete = g.SarOnComplete; gv.SarRect = g.SarArea.Rect;
                    gv.SarLat = g.SarArea.Center.Lat; gv.SarLon = g.SarArea.Center.Lon; gv.SarRadiusNm = g.SarArea.RadiusNm;
                    gv.SarRafts = g.AreaRafts; gv.SarPeople = g.AreaPeople; gv.SarActive = g.Active; gv.SarRescued = g.Rescued; gv.SarNextLaunchS = g.NextLaunchS;
                    gv.TargetText = null;
                }
                int alive = 0;
                foreach (var b in g.Buoys)
                {
                    double frac = 1 - (_now - b.DroppedS) / Math.Max(1, b.LifeS);
                    if (frac <= 0) continue;
                    alive++;
                    gv.Buoys.Add(new AswBuoyView { Lat = b.Lat, Lon = b.Lon, LifeFrac = frac, Ammo = b.Ammo, Pre = b.Pre });
                }
                gv.BuoysInWater = alive;
                v.BuoysInWaterTotal += alive;
                foreach (var c in g.Contacts) gv.Contacts.Add(new AswContactView { Id = c.Id, Name = c.Name, Lat = c.Lat, Lon = c.Lon, RNm = c.Ring ? c.RingR : g.RNm, Held = !c.Ring });
                if (g.Order == "strike") StrikeView(g, gv);
                if (g.Order != "none") v.Active = true;
                v.Groups.Add(gv);
            }
            AlertsView(v);
            StrikeLists(v);
            foreach (var w in _warnings) v.Warnings.Add(w.Text);
            if (_previewUntil > 0) { v.Preview.AddRange(_preview); v.PreviewUntilS = _previewUntil; }
            v.RestoreWaiting = _deferred != null;
            if (_pendingRestore != null) v.RestorePrompt = UiText.Fmt("asw.restore.prompt", I(_pendingRestore.Groups.Count));
            _view = v;
        }
    }
}
