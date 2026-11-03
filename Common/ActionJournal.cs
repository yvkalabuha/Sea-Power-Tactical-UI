using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    // T-091: step-by-step outcome of an XO/ring combo. Pure C# (no Unity/Harmony), so SelfTest runs offline.
    internal enum StepStatus { Ok, Skipped, Error }

    internal sealed class StepOutcome
    {
        public int Index;                        // 0-based position in the combo
        public string Cmd, ArgsShort;            // ArgsShort: "noisemaker", "air,on", "5"
        public bool Optional, ByWhen;            // ByWhen: skipped by its `when` (an alternative branch, not a shortfall)
        public StepStatus Status;
        public string Reason, Detail;
    }

    internal sealed class UnitSnap
    {
        public double? HeadingDeg, SpeedKn, CourseCmdDeg, TimeCompression;
        public int? Telegraph;
        public int Waypoints, Noisemakers, ChaffAmmo, ChaffReady;
        public bool? Emcon, AirRadar, SurfaceRadar, Paused;
        public string WeaponStatus;
        public bool AutoEvade, VetoNeutral;
    }

    internal static partial class ActionJournal
    {
        public const string Ok = "ok", Partial = "partial", Noop = "noop";

        /// <summary>Reasons that are not a shortfall: nothing to do / the player already changed it / a step cut off by stop_on_error.</summary>
        public static readonly HashSet<string> NeutralCodes = new HashSet<string> { "nothing_saved", "player_changed", "after_error" };

        /// <summary>Game conditions: an optional step failing with one of these is "skipped", not "error".</summary>
        public static readonly HashSet<string> ConditionCodes = new HashSet<string>
            { "no_decoy", "not_ready", "no_torpedo", "no_hostile_contact", "no_sensor", "message_pause", "no_control" };

        private static bool IsNeutral(StepOutcome o)
        {
            return o.ByWhen || (o.Status == StepStatus.Skipped && o.Reason != null && NeutralCodes.Contains(o.Reason));
        }

        public static StepOutcome Classify(int index, ActionStep st, bool byWhen, bool ok, string code, string detail)
        {
            var o = new StepOutcome { Index = index, Cmd = st.Cmd, Optional = st.Optional, ByWhen = byWhen, Detail = detail, ArgsShort = ArgsText(st.Args) };
            if (byWhen) { o.Status = StepStatus.Skipped; o.Reason = "when:" + st.When; return o; }
            if (ok) { o.Status = StepStatus.Ok; return o; }
            o.Reason = code ?? "error";
            if (NeutralCodes.Contains(o.Reason) || (st.Optional && ConditionCodes.Contains(o.Reason))) o.Status = StepStatus.Skipped;
            else o.Status = StepStatus.Error;
            return o;
        }

        private static string ArgsText(Dictionary<string, object> args)
        {
            if (args == null || args.Count == 0) return "";
            var parts = new List<string>();
            foreach (var v in args.Values)
            {
                if (v is bool) parts.Add((bool)v ? "on" : "off");
                else if (v is string) parts.Add((string)v);
                else if (v is double) parts.Add(Json.Num((double)v));
                else if (v != null) parts.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
            }
            return string.Join(",", parts.ToArray());
        }

        public static string Result(IList<StepOutcome> steps)
        {
            int counted = 0, okc = 0;
            foreach (var o in steps)
            {
                if (IsNeutral(o)) continue;
                counted++;
                if (o.Status == StepStatus.Ok) okc++;
            }
            if (counted == 0 || okc == counted) return Ok;
            return okc == 0 ? Noop : Partial;
        }

        public static List<string> NotDone(IList<StepOutcome> steps)
        {
            var r = new List<string>();
            foreach (var o in steps)
                if (o.Status != StepStatus.Ok && !IsNeutral(o)) r.Add(o.Cmd + ": " + (o.Reason ?? "error"));
            return r;
        }

        /// <summary>false only when a required step failed (the old rule); optional errors and skips never fail the ack.</summary>
        public static bool AckOk(IList<StepOutcome> steps)
        {
            return !steps.Any(o => o.Status == StepStatus.Error && !o.Optional);
        }

        public static string FirstReason(IList<StepOutcome> steps)
        {
            var o = steps.FirstOrDefault(x => x.Status == StepStatus.Error && !x.Optional);
            return o == null ? null : (o.Reason ?? "error");
        }

        public static string StepsJson(IList<StepOutcome> steps)
        {
            var rows = new List<string>();
            foreach (var o in steps)
            {
                bool ok = o.Status == StepStatus.Ok || o.ByWhen; // old contract: a `when` skip was ok:true,skipped:true
                var sb = new StringBuilder("{\"cmd\":" + Json.Str(o.Cmd) + ",\"ok\":" + Json.Bool(ok));
                if (!ok) sb.Append(",\"optional\":" + Json.Bool(o.Optional) + ",\"error\":" + Json.Str(o.Reason));
                if (o.ByWhen) sb.Append(",\"skipped\":true");
                sb.Append(",\"status\":" + Json.Str(o.Status == StepStatus.Ok ? "ok" : o.Status == StepStatus.Skipped ? "skipped" : "error"));
                if (o.Reason != null) sb.Append(",\"reason\":" + Json.Str(o.Reason));
                if (o.Detail != null) sb.Append(",\"detail\":" + Json.Str(o.Detail));
                rows.Add(sb.Append('}').ToString());
            }
            return "[" + string.Join(",", rows.ToArray()) + "]";
        }

        // ---- snapshots ----
        private static string F(double d)
        {
            return Math.Round(d, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static void DiffD(List<string> r, string n, double? a, double? b)
        {
            if (a == null || b == null) return;
            if (Math.Abs(a.Value - b.Value) < 0.05) return;
            r.Add(n + " " + F(a.Value) + "→" + F(b.Value));
        }

        private static void DiffI(List<string> r, string n, int? a, int? b)
        {
            if (a == null || b == null || a.Value == b.Value) return;
            r.Add(n + " " + a.Value + "→" + b.Value);
        }

        private static void DiffB(List<string> r, string n, bool? a, bool? b)
        {
            if (a == null || b == null || a.Value == b.Value) return;
            r.Add(n + " " + (a.Value ? "on" : "off") + "→" + (b.Value ? "on" : "off"));
        }

        /// <summary>"telegraph 3→5; noisemakers 2→1"; "" when nothing differs. Fields that are null on either side are left out.</summary>
        public static string SnapDiff(UnitSnap a, UnitSnap b)
        {
            if (a == null || b == null) return "";
            var r = new List<string>();
            DiffD(r, "heading", a.HeadingDeg, b.HeadingDeg);
            DiffD(r, "speed", a.SpeedKn, b.SpeedKn);
            DiffD(r, "course_cmd", a.CourseCmdDeg, b.CourseCmdDeg);
            DiffI(r, "telegraph", a.Telegraph, b.Telegraph);
            DiffI(r, "waypoints", a.Waypoints, b.Waypoints);
            DiffB(r, "emcon", a.Emcon, b.Emcon);
            DiffB(r, "air_radar", a.AirRadar, b.AirRadar);
            DiffB(r, "surface_radar", a.SurfaceRadar, b.SurfaceRadar);
            if (a.WeaponStatus != null && b.WeaponStatus != null && a.WeaponStatus != b.WeaponStatus)
                r.Add("weapon_status " + a.WeaponStatus + "→" + b.WeaponStatus);
            DiffI(r, "noisemakers", a.Noisemakers, b.Noisemakers);
            DiffI(r, "chaff_ammo", a.ChaffAmmo, b.ChaffAmmo);
            DiffI(r, "chaff_ready", a.ChaffReady, b.ChaffReady);
            DiffB(r, "auto_evade", a.AutoEvade, b.AutoEvade);
            DiffB(r, "veto_neutral", a.VetoNeutral, b.VetoNeutral);
            DiffD(r, "time_compression", a.TimeCompression, b.TimeCompression);
            DiffB(r, "paused", a.Paused, b.Paused);
            return string.Join("; ", r.ToArray());
        }

        private static string StepLine(StepOutcome o)
        {
            string head = "  " + (o.Index + 1) + " " + o.Cmd + (string.IsNullOrEmpty(o.ArgsShort) ? "" : "(" + o.ArgsShort + ")") + " ";
            switch (o.Status)
            {
                case StepStatus.Ok: return head + "ok" + (o.Detail != null ? " (" + o.Detail + ")" : "");
                case StepStatus.Skipped: return head + "skipped: " + (o.Reason ?? "") + (o.Detail != null && !o.ByWhen ? " (" + o.Detail + ")" : "");
                default: return head + "error: " + (o.Reason ?? "error") + (o.Detail != null ? " (" + o.Detail + ")" : "");
            }
        }

        public static List<string> DebugLines(string id, double uid, string source, string result, IList<StepOutcome> steps, UnitSnap before, UnitSnap after)
        {
            var r = new List<string>
            {
                "action " + id + " uid=" + ((long)uid).ToString(CultureInfo.InvariantCulture) + " src=" + source + " result=" + result
            };
            foreach (var o in steps) r.Add(StepLine(o));
            string diff = SnapDiff(before, after);
            r.Add("  before→after: " + (diff.Length == 0 ? "(no change)" : diff));
            return r;
        }

        private static List<string> Reasons(IList<string> notDone)
        {
            var r = new List<string>();
            if (notDone == null) return r;
            foreach (var s in notDone)
            {
                int i = s.IndexOf(": ", StringComparison.Ordinal);
                string reason = i >= 0 ? s.Substring(i + 2) : s;
                if (!r.Contains(reason)) r.Add(reason);
            }
            return r;
        }

        /// <summary>One log line: "menu action X -> ok", "-> ok (partial: a, b)", "-> ok (noop: a)", "-> error: a" (result "error").</summary>
        public static string Summary(string source, string id, string result, IList<string> notDone)
        {
            string head = source + " action " + id + " -> ";
            var why = string.Join(", ", Reasons(notDone).ToArray());
            if (result == "error") return head + "error" + (why.Length > 0 ? ": " + why : "");
            if ((result == Partial || result == Noop) && why.Length > 0) return head + "ok (" + result + ": " + why + ")";
            if (result == Partial || result == Noop) return head + "ok (" + result + ")";
            return head + "ok";
        }

        // ---- deterministic sample for the PROTOCOL.md check (selfcheck journal-sample) ----
        public static string SampleJson()
        {
            var s1 = new ActionStep { Cmd = "launch_decoy", Optional = true };
            s1.Args["kind"] = "noisemaker";
            var s2 = new ActionStep { Cmd = "launch_decoy", Optional = true };
            s2.Args["kind"] = "chaff";
            var steps = new List<StepOutcome>
            {
                Classify(0, s1, false, false, "no_decoy", "no noisemakers"),
                Classify(1, s2, false, false, "not_ready", null),
            };
            string result = Result(steps);
            var nd = NotDone(steps);
            var nds = string.Join(",", nd.Select(x => Json.Str(x)).ToArray());
            return "{\"type\":\"ack\",\"id\":1,\"ok\":" + Json.Bool(AckOk(steps)) + ",\"data\":{\"name\":\"decoy_salvo\",\"uid\":12,\"ok_steps\":0,\"result\":" +
                   Json.Str(result) + ",\"not_done\":[" + nds + "],\"steps\":" + StepsJson(steps) + "}}";
        }

        // ---- self test ----
        private static StepOutcome S(string cmd, bool optional, string kind, string status)
        {
            // status: "ok" | "when" | "after_error" | <error code>
            var st = new ActionStep { Cmd = cmd, Optional = optional, When = status == "when" ? "cap:auto_evade" : null };
            if (kind != null) st.Args["kind"] = kind;
            if (status == "ok") return Classify(0, st, false, true, null, null);
            if (status == "when") return Classify(0, st, true, true, null, null);
            return Classify(0, st, false, false, status, null);
        }

        public static string SelfTest()
        {
            // decoy_salvo with nothing launched
            var d = new List<StepOutcome> { S("launch_decoy", true, "noisemaker", "no_decoy"), S("launch_decoy", true, "chaff", "not_ready") };
            if (Result(d) != Noop) return "decoy_salvo result " + Result(d);
            if (!AckOk(d)) return "decoy_salvo ack must stay ok";
            if (NotDone(d).Count != 2) return "decoy_salvo not_done " + NotDone(d).Count;
            if (d[0].Status != StepStatus.Skipped) return "optional condition must be skipped";

            // torpedo_alert, typical outcome
            var t = new List<StepOutcome>
            {
                S("set_flags", false, null, "ok"), S("evade_torpedo", true, null, "when"),
                S("launch_decoy", true, "noisemaker", "no_decoy"), S("set_time_compression", true, null, "ok"),
            };
            if (Result(t) != Partial) return "torpedo_alert result " + Result(t);
            var tn = NotDone(t);
            if (tn.Count != 1 || tn[0] != "launch_decoy: no_decoy") return "torpedo_alert not_done " + string.Join("|", tn.ToArray());
            if (!AckOk(t)) return "torpedo_alert ack";

            // all ok plus a when-skip
            var a = new List<StepOutcome> { S("set_emcon", false, null, "ok"), S("evade_torpedo", true, null, "when"), S("set_roe", false, null, "ok") };
            if (Result(a) != Ok || NotDone(a).Count != 0) return "when-skip must not count: " + Result(a);

            // required failure + stop_on_error tail
            var b = new List<StepOutcome> { S("course_from_threat", false, null, "no_hostile_contact"), S("set_telegraph", false, null, "after_error"), S("launch_decoy", true, "chaff", "after_error") };
            if (Result(b) != Noop) return "break_contact result " + Result(b);
            if (AckOk(b)) return "break_contact ack must fail";
            if (FirstReason(b) != "no_hostile_contact") return "first reason " + FirstReason(b);
            if (b[0].Status != StepStatus.Error) return "required condition must be error";
            if (NotDone(b).Count != 1) return "after_error must stay out of not_done: " + NotDone(b).Count;

            // neutral reasons
            var n = new List<StepOutcome> { S("restore_control", false, null, "nothing_saved"), S("restore_control", false, null, "player_changed") };
            if (Result(n) != Ok || NotDone(n).Count != 0 || !AckOk(n)) return "neutral reasons must be ok";

            // non-condition code on an optional step is an error, but does not fail the ack
            var u = new List<StepOutcome> { S("set_time_compression", true, null, "unsupported"), S("set_roe", false, null, "ok") };
            if (u[0].Status != StepStatus.Error) return "optional non-condition must be error";
            if (Result(u) != Partial || !AckOk(u)) return "optional error result/ack";

            // steps json keeps the old fields and adds the new
            string js = StepsJson(d);
            if (!js.Contains("\"ok\":false,\"optional\":true") || !js.Contains("\"status\":\"skipped\"") || !js.Contains("\"reason\":\"no_decoy\"")) return "steps json " + js;
            string jw = StepsJson(a);
            if (!jw.Contains("\"skipped\":true") || !jw.Contains("\"reason\":\"when:cap:auto_evade\"")) return "steps json when " + jw;

            // snapshots
            var s0 = new UnitSnap { Telegraph = 3, Noisemakers = 2, Emcon = true, WeaponStatus = "tight", HeadingDeg = 90 };
            var s1 = new UnitSnap { Telegraph = 5, Noisemakers = 1, Emcon = false, WeaponStatus = "tight", HeadingDeg = 90.02 };
            if (SnapDiff(s0, new UnitSnap { Telegraph = 3, Noisemakers = 2, Emcon = true, WeaponStatus = "tight", HeadingDeg = 90 }) != "") return "equal snaps must diff to empty";
            string df = SnapDiff(s0, s1);
            if (df != "telegraph 3→5; emcon on→off; noisemakers 2→1") return "snap diff '" + df + "'";
            if (SnapDiff(new UnitSnap { Telegraph = 3 }, new UnitSnap()) != "") return "null fields must be left out";
            if (SnapDiff(null, s1) != "") return "null snap";

            var lines = DebugLines("decoy_salvo", 12, "menu", Noop, d, s0, s0);
            if (lines.Count != 4) return "debug lines count " + lines.Count;
            if (lines[0] != "action decoy_salvo uid=12 src=menu result=noop") return "debug header '" + lines[0] + "'";
            if (lines[1] != "  1 launch_decoy(noisemaker) skipped: no_decoy") return "debug step '" + lines[1] + "'";
            if (!lines[3].StartsWith("  before→after:")) return "debug diff line '" + lines[3] + "'";
            if (!DebugLines("x", 1, "ring", Partial, t, s0, s1)[5].Contains("telegraph 3→5")) return "debug diff content";

            // one-line summaries
            if (Summary("menu", "silent_run", Ok, new List<string>()) != "menu action silent_run -> ok") return "summary ok";
            if (Summary("menu", "torpedo_alert", Partial, tn) != "menu action torpedo_alert -> ok (partial: no_decoy)") return "summary partial '" + Summary("menu", "torpedo_alert", Partial, tn) + "'";
            if (Summary("menu", "decoy_salvo", Noop, NotDone(d)) != "menu action decoy_salvo -> ok (noop: no_decoy, not_ready)") return "summary noop '" + Summary("menu", "decoy_salvo", Noop, NotDone(d)) + "'";
            if (Summary("ring", "break_contact", "error", NotDone(b)) != "ring action break_contact -> error: no_hostile_contact") return "summary error '" + Summary("ring", "break_contact", "error", NotDone(b)) + "'";
            return null;
        }
    }
}
