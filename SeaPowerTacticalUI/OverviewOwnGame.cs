using System;
using System.Collections;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-242 (ADR-0101): game side of the Overview "Own" tab. Main thread, reflection through OverviewOwnSpecs / FormationSpecs only.
    /// Reads the player's own units (four lists of the player's taskforce) once per scan and selects one the way the game does.
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly Ui.FormationFacts _ovFormation = new Ui.FormationFacts();

        /// <summary>One pass over the 4 lists of the player's taskforce. Destroyed / dead objects and air units on a deck (OverviewOwnMath.Include) are skipped.</summary>
        internal Ui.OwnFrame BuildOwnUnits(string lang)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fr = new Ui.OwnFrame();
            var tf = GetStatic(OverviewOwnSpecs.PlayerTaskforce);
            if (tf == null) { fr.BuildMs = sw.Elapsed.TotalMilliseconds; return fr; }
            var own = AgOwnership;
            for (int li = 0; li < OverviewOwnSpecs.Lists.Length; li++)
            {
                var list = Get(tf, OverviewOwnSpecs.Lists[li]) as IEnumerable;
                if (list == null) continue;
                var kind = li == 0 ? Ui.OwnKind.Surface : li == 1 ? Ui.OwnKind.Submarine : li == 2 ? Ui.OwnKind.Air : Ui.OwnKind.Helicopter;
                foreach (var u in list)
                {
                    try
                    {
                        if (u == null || !IsAlive(u) || BoolOf(Get(u, OverviewOwnSpecs.ObjIsDestroyed))) continue;
                        var uid = D(Get(u, OverviewOwnSpecs.ObjUniqueId));
                        if (!uid.HasValue) continue;
                        var f = new Ui.OwnUnitFacts { Uid = (int)uid.Value, Kind = kind };
                        f.Name = RxGetOwn(u, OverviewOwnSpecs.ObjName) as string;
                        f.ClassName = Get(u, OverviewOwnSpecs.ObjClassNameShort) as string;
                        f.TypeAbbr = Get(u, OverviewOwnSpecs.ObjAbbreviation) as string;
                        var h = D(CallOwn(u, OverviewOwnSpecs.ObjHeightFt));
                        if (h.HasValue) f.AltFt = h.Value;
                        var sp = D(CallOwn(u, OverviewOwnSpecs.ObjVelocityKn));
                        if (sp.HasValue) f.SpeedKn = sp.Value;
                        f.Feedback = RxGetOwn(u, OverviewOwnSpecs.Feedback) as string;
                        f.OrderText = RxGetOwn(u, OverviewOwnSpecs.OrderText) as string;
                        if (kind == Ui.OwnKind.Air || kind == Ui.OwnKind.Helicopter)
                        {
                            var fl = Get(u, kind == Ui.OwnKind.Air ? OverviewOwnSpecs.AircraftInFlight : OverviewOwnSpecs.HelicopterInFlight);
                            f.InFlight = !(fl is bool) || (bool)fl;
                            f.Loadout = Get(Get(u, OverviewOwnSpecs.ObjObp), OverviewOwnSpecs.ObpLoadoutVariant) as string;
                        }
                        if (own != null) { string win; f.ModGroup = own.Owner(f.Uid, out win); }
                        if (ReadFormation(u, _ovFormation))
                        {
                            f.InFormation = true; f.FormationLeader = _ovFormation.IsLeader;
                            f.FormationName = _ovFormation.Name; f.FormationLeaderName = _ovFormation.LeaderName;
                            f.FormationUnnamed = string.IsNullOrEmpty(_ovFormation.Name);
                        }
                        if (!Ui.OverviewOwnMath.Include(f)) continue;
                        fr.Rows.Add(Ui.OverviewOwnMath.MakeRow(f, lang));
                        if (kind == Ui.OwnKind.Surface) fr.Ships++; else if (kind == Ui.OwnKind.Submarine) fr.Subs++; else fr.Air++;
                    }
                    catch (Exception) { }
                }
            }
            fr.BuildMs = sw.Elapsed.TotalMilliseconds;
            return fr;
        }

        /// <summary>T-324: a unit link in the CAP / ASW windows: selects the own unit the way F5 / a map click does (SelectOwnUnit), then centres the map
        /// (CenterMap). The selection failing does not stop the centring; its code goes to selectErr (null = selected). Returns OverviewOwnMath.FocusCode.</summary>
        internal string FocusOwnUnit(int uid, string source, out string selectErr)
        {
            SelectOwnUnit(uid, source, out selectErr);
            string cErr;
            var c = CenterMap(null, uid, null, null, source, out cErr);
            return Ui.OverviewOwnMath.FocusCode(c, cErr);
        }

        /// <summary>Selects an own unit the way F5 does: RenderPosition.switchToObject(u, false, false, false) (checkAirLeader=false: a wingman is not replaced by its leader).
        /// null + err = unknown_target | unsupported. Event overview_action {action:"select_own"}.</summary>
        internal string SelectOwnUnit(int uid, string source, out string err)
        {
            err = null;
            var u = FindOwnUnit(uid);
            if (u == null || !IsAlive(u)) { err = "unknown_target"; return null; }
            var rp = RenderPositionInstance();
            if (rp == null || !Present(OverviewOwnSpecs.SwitchTo)) { err = "unsupported"; return null; }
            try { Call(rp, OverviewOwnSpecs.SwitchTo, u, false, false, false); }
            catch (Exception) { err = "unsupported"; return null; }
            if (EventsAvailable)
                EvLine("overview_action", "{\"action\":\"select_own\",\"uid\":" + uid + ",\"source\":" + Json.Str(source) + "}");
            return "\"uid\":" + uid;
        }

        /// <summary>UniqueID of the selected own unit, 0 = none.</summary>
        internal int OverviewSelectedOwnUid()
        {
            try
            {
                var rp = RenderPositionInstance();
                if (rp == null) return 0;
                var o = Call(rp, OverviewSpecs.RenderSelected);
                if (!IsAlive(o) || !BoolOf(Get(o, OverviewSpecs.ObjIsPlayer))) return 0;
                var id = D(Get(o, OverviewOwnSpecs.ObjUniqueId));
                return id.HasValue ? (int)id.Value : 0;
            }
            catch (Exception) { return 0; }
        }
    }
}
