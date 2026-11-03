using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-243: game side of the two ring sectors "Return to formation" (SW) and "Return to base" (E, aircraft). Both give the game's own orders:
    /// UnitFormation.ReturnToFormation (after CheckForPlayerAbort, like the Orders menu item) and the existing ReturnToBaseCmd.
    /// "Return to formation" first lets go of a unit the mod drives (release_control); "Return to base" never does: a unit of a mod group stays in
    /// its group, the group sees game_rtb and takes the unit again after landing (Q-T214-1).
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly Ui.FormationFacts _ringFacts = new Ui.FormationFacts();

        /// <summary>Uids of the units with a "mod drives it" label of this tick (filled by UnitActionFeature; empty in [Ui] UnitActionText=off).</summary>
        internal readonly HashSet<int> UaHeldUids = new HashSet<int>();

        /// <summary>FormationRing / RtbRing / ModHolds for MenuView. Never throws; a failure = the sector hidden.</summary>
        partial void RadialOrdersView(object unit, UnitView v)
        {
            if (unit == null) return;
            try
            {
                if (Capabilities.Contains("formation_return") && ReadFormation(unit, _ringFacts) && !(Present(FormationSpecs.ObjAcceptsOrdersFromPlayer) && !BoolOf(GetOwn(unit, FormationSpecs.ObjAcceptsOrdersFromPlayer))))
                    v.FormationRing = FormationMath.RingState(_ringFacts);
            }
            catch (Exception) { v.FormationRing = null; }
            try
            {
                if ((v.Kind == "air" || v.Kind == "helo") && Capabilities.Contains("return_to_base") && InFlight(unit))
                {
                    // the field _homeBase is what getHomeBase() returns; a missing optional member = let the command itself refuse
                    var home = Present(AirOpsSpecs.ObjHomeBase) ? GetOwn(unit, AirOpsSpecs.ObjHomeBase) : unit;
                    if (home == null || !IsAlive(home)) v.RtbRing = "no_base";
                    else if (OrderTypeIs(unit, "ReturnToBase") || IsReturnState(StateName(unit, "CurrentStateName"))) v.RtbRing = "returning";
                    else v.RtbRing = "ready";
                }
            }
            catch (Exception) { v.RtbRing = null; }
            try { v.ModHolds = ModHolds(unit); } catch (Exception) { v.ModHolds = false; }
        }

        /// <summary>The unit's current order has this type (Order.Type name). False on any failure.</summary>
        internal bool OrderTypeIs(object u, string orderType)
        {
            try
            {
                var o = RxGet(u, CapSpecs.ObjCurrentOrder);
                var t = o == null ? null : Get(o, CapSpecs.OrderType);
                return t != null && t.ToString() == orderType;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The mod drives the unit: ControlMemory entry, UiSteering owner, a unit_action label, or a group of the aviation modules (AirOwnership).</summary>
        internal bool ModHolds(object unit)
        {
            if (unit == null) return false;
            int uid = UidOf(unit);
            if (_ctlMem.Peek(unit) != null) return true;
            var st = UiSteering;
            if (st != null && uid > 0 && st.Owner(uid) != null) return true;
            if (uid > 0 && UaHeldUids.Contains(uid)) return true;
            var own = AgOwnership;
            string win;
            return own != null && uid > 0 && own.Owner(uid, out win) != null;
        }

        /// <summary>
        /// formation_return: checks, then [release_control if ModHolds], CheckForPlayerAbort, ReturnToFormation(u, true).
        /// Errors: not_in_formation | formation_leader | not_accepted | unsupported. Ack {"ok":true,"released":bool}.
        /// </summary>
        internal string FormationReturnRun(object unit, string source)
        {
            try
            {
                if (unit == null || !Present(FormationSpecs.FormationReturn) || !Present(FormationSpecs.ObjFormation)) return "{\"ok\":false,\"error\":\"unsupported\"}";
                var facts = new Ui.FormationFacts();
                if (!ReadFormation(unit, facts)) return "{\"ok\":false,\"error\":\"not_in_formation\"}";
                if (facts.IsLeader) return "{\"ok\":false,\"error\":\"formation_leader\"}";
                if (Present(FormationSpecs.ObjAcceptsOrdersFromPlayer) && !BoolOf(GetOwn(unit, FormationSpecs.ObjAcceptsOrdersFromPlayer))) return "{\"ok\":false,\"error\":\"not_accepted\"}";
                var f = GetOwn(unit, FormationSpecs.ObjFormation);
                if (f == null) return "{\"ok\":false,\"error\":\"not_in_formation\"}";
                bool released = false;
                if (ModHolds(unit))
                {
                    var def = FindAction("release_control");
                    if (def != null)
                    {
                        var c = new Dictionary<string, object> { { "name", def.Id }, { "uid", (double)UidOf(unit) } };
                        string rr = RunAction("\"" + source + "\"", def, c, source);
                        if (!rr.Contains("\"ok\":true")) { Log(source + " formation_return release_control failed uid=" + UidOf(unit) + " " + rr); return "{\"ok\":false,\"error\":\"release_failed\"}"; }
                        released = true;
                    }
                }
                if (Present(FormationSpecs.ObjCheckForPlayerAbort)) Call(unit, FormationSpecs.ObjCheckForPlayerAbort);
                Call(f, FormationSpecs.FormationReturn, unit, true);
                Log(source + " formation_return uid=" + UidOf(unit) + " released=" + released);
                return "{\"ok\":true,\"released\":" + (released ? "true" : "false") + "}";
            }
            catch (Exception e)
            {
                Log(source + " formation_return failed: " + e.GetType().Name + ": " + e.Message);
                return "{\"ok\":false,\"error\":\"unsupported\"}";
            }
        }

        /// <summary>air_rtb: the game's Return To Base order through ReturnToBaseCmd. No release_control (Q5 of T-243): a group of the mod keeps the unit.</summary>
        internal string AirRtbRun(object unit, string source)
        {
            try
            {
                if (unit == null) return "{\"ok\":false,\"error\":\"unsupported\"}";
                string r = ReturnToBaseCmd("\"" + source + "\"", new Dictionary<string, object> { { "uid", (double)UidOf(unit) } });
                Log(source + " air_rtb uid=" + UidOf(unit) + " -> " + (r.Contains("\"ok\":true") ? "ok" : r));
                return r;
            }
            catch (Exception e)
            {
                Log(source + " air_rtb failed: " + e.GetType().Name + ": " + e.Message);
                return "{\"ok\":false,\"error\":\"unsupported\"}";
            }
        }
    }
}
