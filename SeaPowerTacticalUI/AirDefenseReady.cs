using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-098 (ADR-0037 section 2 items 1-5): pure rules of the one-shot "Air defense ready" setting. No game, no Unity.
    // The game layer (DoctrineGame.PumpDoctrine) calls OnTick at 1 Hz per player ship and executes the steps.

    /// <summary>Per-unit tracker. LastResolved == null means "first seen" (after a mission reset or a load).</summary>
    internal sealed class AdrUnit { public bool? LastResolved; public object LastFormation; public bool Applied; }

    /// <summary>Field: emcon | air_radar | roe. Value: text as in Common/RestoreControl.cs FieldValue (true/false) or WeaponStatusName (tight|hold|free).</summary>
    internal sealed class AdrStep { public string Field; public string Value; }

    internal static class AirDefenseReadyRules
    {
        public const string Emcon = "emcon", AirRadar = "air_radar", Roe = "roe";

        /// <summary>apply | restore | none. Applies on first sight with true, on false->true and when a true unit changes group; restores on true->false only if something was applied.</summary>
        public static string OnTick(AdrUnit s, bool resolved, object formation)
        {
            string act = "none";
            if (resolved)
            {
                if (s.LastResolved != true || !ReferenceEquals(formation, s.LastFormation)) { act = "apply"; s.Applied = true; }
            }
            else if (s.LastResolved == true && s.Applied) { act = "restore"; s.Applied = false; }
            s.LastResolved = resolved;
            s.LastFormation = formation;
            return act;
        }

        /// <summary>hold -> tight; tight/free/unknown -> null (leave as is).</summary>
        public static string RoeFloor(string roe)
        {
            return string.Equals(roe, "hold", StringComparison.OrdinalIgnoreCase) ? "tight" : null;
        }

        private static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        /// <summary>Targets: emcon "false" (emissions allowed), air_radar "true", ROE raised to the floor. Fields already right or unknown (null) are skipped.</summary>
        public static List<AdrStep> ApplyPlan(string emcon, string airRadar, string roe)
        {
            var p = new List<AdrStep>();
            if (emcon != null && !Is(emcon, "false")) p.Add(new AdrStep { Field = Emcon, Value = "false" });
            if (airRadar != null && !Is(airRadar, "true")) p.Add(new AdrStep { Field = AirRadar, Value = "true" });
            string floor = RoeFloor(roe);
            if (floor != null) p.Add(new AdrStep { Field = Roe, Value = floor });
            return p;
        }

        /// <summary>
        /// Call order around executing a plan (so ControlMemory can restore later): Remember(...) BEFORE the commands
        /// (RememberField/RememberRoe keep the first value), then NoteApplied(...) AFTER they succeeded
        /// (NoteFieldSet/NoteRoeSet record what the mod set, which is what DecideField/DecideRoe compare with).
        /// </summary>
        public static void Remember(ControlMemory m, object unit, List<AdrStep> plan, Func<string, string> current, string currentRoe)
        {
            foreach (var st in plan)
            {
                if (st.Field == Roe) m.RememberRoe(unit, currentRoe);
                else m.RememberField(unit, st.Field, current(st.Field));
            }
        }

        public static void NoteApplied(ControlMemory m, object unit, List<AdrStep> plan)
        {
            foreach (var st in plan)
            {
                if (st.Field == Roe) m.NoteRoeSet(unit, st.Value);
                else m.NoteFieldSet(unit, st.Field, st.Value);
            }
        }

        /// <summary>Untouched fields go back to the saved value; a field the player changed meanwhile (player_changed) is skipped.</summary>
        public static List<AdrStep> RestorePlan(ControlEntry e, Func<string, string> current, string currentRoe)
        {
            var p = new List<AdrStep>();
            if (e == null) return p;
            foreach (var k in new[] { Emcon, AirRadar })
                if (ControlMemory.DecideField(e, k, current(k)) == "restore") p.Add(new AdrStep { Field = k, Value = e.Fields[k].Saved });
            if (ControlMemory.DecideRoe(e, currentRoe, true) == "restore") p.Add(new AdrStep { Field = Roe, Value = e.SavedRoe });
            return p;
        }

        private static string Fmt(List<AdrStep> p)
        {
            var parts = new List<string>();
            foreach (var s in p) parts.Add(s.Field + "=" + s.Value);
            return string.Join(",", parts.ToArray());
        }

        public static string SelfTest()
        {
            if (RoeFloor("hold") != "tight" || RoeFloor("HOLD") != "tight") return "roe floor hold";
            if (RoeFloor("tight") != null || RoeFloor("free") != null || RoeFloor(null) != null || RoeFloor("zzz") != null) return "roe floor others";

            var u = new AdrUnit(); var g1 = new object(); var g2 = new object();
            if (OnTick(u, false, g1) != "none") return "tick: first seen false";
            if (OnTick(u, true, g1) != "apply") return "tick: false->true";
            if (OnTick(u, true, g1) != "none") return "tick: true->true";
            if (OnTick(u, true, g2) != "apply") return "tick: new group";
            if (OnTick(u, false, g2) != "restore") return "tick: true->false after apply";
            if (OnTick(u, false, g2) != "none") return "tick: false->false";
            var f = new AdrUnit();
            if (OnTick(f, true, g1) != "apply") return "tick: first seen true";
            f = new AdrUnit(); f.LastResolved = true;   // true without a recorded apply (e.g. state restored)
            if (OnTick(f, false, g1) != "none") return "tick: true->false without apply";

            if (Fmt(ApplyPlan("true", "false", "hold")) != "emcon=false,air_radar=true,roe=tight") return "plan: all three";
            if (ApplyPlan("false", "true", "tight").Count != 0) return "plan: already right";
            if (Fmt(ApplyPlan("false", "false", "free")) != "air_radar=true") return "plan: roe free untouched";
            if (ApplyPlan(null, null, null).Count != 0) return "plan: unknown fields skipped";
            if (Fmt(ApplyPlan("TRUE", "True", null)) != "emcon=false") return "plan: case-insensitive";

            // memory round trip: untouched fields come back, the player's change is left alone
            var m = new ControlMemory(); var ship = new object();
            var cur = new Dictionary<string, string> { { "emcon", "true" }, { "air_radar", "false" } };
            string curRoe = "hold";
            var plan = ApplyPlan(cur["emcon"], cur["air_radar"], curRoe);
            Remember(m, ship, plan, k => cur[k], curRoe);
            NoteApplied(m, ship, plan);
            cur["emcon"] = "false"; cur["air_radar"] = "true"; curRoe = "tight";   // the game now shows what the mod set
            var rp = RestorePlan(m.Peek(ship), k => cur[k], curRoe);
            if (Fmt(rp) != "emcon=true,air_radar=false,roe=hold") return "restore: untouched -> " + Fmt(rp);

            cur["air_radar"] = "false";   // the player switched the radar off after the apply
            var u2 = new AdrUnit(); OnTick(u2, true, g1);
            if (OnTick(u2, true, g1) != "none") return "player_changed: mod does not pull back";
            if (ControlMemory.DecideField(m.Peek(ship), "air_radar", cur["air_radar"]) != "player_changed") return "player_changed: decision";
            rp = RestorePlan(m.Peek(ship), k => cur[k], curRoe);
            if (Fmt(rp) != "emcon=true,roe=hold") return "restore: skips player_changed -> " + Fmt(rp);

            curRoe = "free";   // the player changed ROE too
            if (Fmt(RestorePlan(m.Peek(ship), k => cur[k], curRoe)) != "emcon=true") return "restore: roe player_changed";

            if (RestorePlan(null, k => null, null).Count != 0) return "restore: no entry";
            var m2 = new ControlMemory(); var u3 = new object();
            m2.RememberField(u3, "emcon", "true");   // remembered but the command never succeeded -> nothing_saved
            if (RestorePlan(m2.Peek(u3), k => "true", "tight").Count != 0) return "restore: nothing_saved";
            return null;
        }
    }
}
