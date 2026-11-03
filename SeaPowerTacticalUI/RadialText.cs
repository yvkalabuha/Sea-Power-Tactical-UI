using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>
    /// Ring result texts in the UI language (T-103 M2). Pure C#: built from the structured fields of a reply, not from the voice texts
    /// (ActionJournal.ShortRu, summary_ru), which stay Russian for the voice mod.
    /// </summary>
    internal static class RadialText
    {
        private static string Tx(string key) { return UiText.Get(key); }
        private static string Tx(string key, params object[] a) { return UiText.Fmt(key, a); }

        private static string ReasonText(string code)
        {
            string k = "radial.reason." + code;
            return UiText.Has(UiText.DefaultSection, k) ? UiText.Get(k) : code;
        }

        /// <summary>T-243: an error code of a ring command in the UI language (radial.err.code), else the code itself.</summary>
        public static string ErrorText(string code)
        {
            if (code == null) return Tx("radial.err.error");
            string k = "radial.err." + code;
            return UiText.Has(UiText.DefaultSection, k) ? UiText.Get(k) : code;
        }

        /// <summary>"partial: no decoys" / "no effect: no decoys"; "" for a full result.</summary>
        public static string NotDone(string result, IList<string> notDone)
        {
            if (result != "partial" && result != "noop") return "";
            var reasons = new List<string>();
            if (notDone != null)
                foreach (var s in notDone)
                {
                    int i = s.IndexOf(": ", StringComparison.Ordinal);
                    string code = i >= 0 ? s.Substring(i + 2) : s;
                    if (!reasons.Contains(code)) reasons.Add(code);
                }
            string head = Tx(result == "partial" ? "radial.result.partial" : "radial.result.noop");
            string why = string.Join(", ", reasons.Select(ReasonText).ToArray());
            return why.Length > 0 ? head + ": " + why : head;
        }

        private static readonly string[] Parts = { "roe", "route", "speed", "emcon", "radars" };

        private static string ActionOf(Dictionary<string, object> reply, string part)
        {
            object o;
            if (reply == null || !reply.TryGetValue(part, out o)) return null;
            var d = o as Dictionary<string, object>;
            if (d != null) { object a; return d.TryGetValue("action", out a) ? a as string : null; }
            return o as string;
        }

        private static string Join(List<string> items)
        {
            if (items.Count == 1) return items[0];
            return Tx("radial.restore.and", string.Join(", ", items.GetRange(0, items.Count - 1).ToArray()), items[items.Count - 1]);
        }

        /// <summary>
        /// One line for the player from the decisions of release_control (roe/route as {action}, speed/emcon/radars as strings).
        /// null = the reply carries none of these fields (the caller shows a plain "done").
        /// </summary>
        public static string RestoreSummary(Dictionary<string, object> reply)
        {
            if (reply == null) return null;
            var inner = reply.ContainsKey("data") ? reply["data"] as Dictionary<string, object> : null;
            if (inner != null) reply = inner;
            bool any = false;
            var done = new List<string>(); var changed = new List<string>();
            foreach (var p in Parts)
            {
                string act = ActionOf(reply, p);
                if (act != null) any = true;
                if (act == "restore") done.Add(p);
                else if (act == "player_changed") changed.Add(p);
            }
            if (!any) return null;
            Func<List<string>, string> names = l => Join(l.Select(x => Tx("radial.restore.name." + x)).ToList());
            if (done.Count > 0)
            {
                string head = names(done);
                head = char.ToUpperInvariant(head[0]) + head.Substring(1);
                bool one = done.Count == 1 && done[0] != "radars";
                return Tx(one ? "radial.restore.done.one" : "radial.restore.done.many", head);
            }
            if (changed.Count > 0) return Tx("radial.restore.changed", names(changed));
            return Tx("radial.restore.nothing");
        }

        private static Dictionary<string, object> Reply(string roe, string route, string speed = null, string emcon = null, string radars = null)
        {
            var d = new Dictionary<string, object>();
            if (roe != null) d["roe"] = new Dictionary<string, object> { { "action", roe } };
            if (route != null) d["route"] = new Dictionary<string, object> { { "action", route } };
            if (speed != null) d["speed"] = speed;
            if (emcon != null) d["emcon"] = emcon;
            if (radars != null) d["radars"] = radars;
            return d;
        }

        /// <summary>T-328: an admission / failure code of a group move in the UI language. in_group:X -> "in group X"; then the table of the window (cap.reason / asw.excl),
        /// the table of the other window, radial.group.why.*, the code itself.</summary>
        public static string GroupWhy(string window, string code)
        {
            if (code == null) return Tx("radial.err.error");
            if (code.StartsWith("in_group:", StringComparison.Ordinal)) return Tx("cap.reason.in_group", code.Substring(9));
            string own = (window == "asw" ? "asw.excl." : "cap.reason.") + code, other = (window == "asw" ? "cap.reason." : "asw.excl.") + code, fb = "radial.group.why." + code;
            // the own table first, but the explicit radial.group.why.* wins over the foreign window's table
            if (UiText.Has(UiText.DefaultSection, own)) return UiText.Get(own);
            if (UiText.Has(UiText.DefaultSection, fb)) return UiText.Get(fb);
            if (UiText.Has(UiText.DefaultSection, other)) return UiText.Get(other);
            if (UiText.Has(UiText.DefaultSection, "sar.reason." + code)) return UiText.Get("sar.reason." + code);
            return code;
        }

        private static string Str(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) ? o as string : null; }
        private static bool Flag(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) && o is bool && (bool)o; }
        private static string GName(string v) { if (v == null) return null; int i = v.IndexOf(':'); return i >= 0 ? v.Substring(i + 1) : v; }
        private static string GWin(string v) { if (v == null) return null; int i = v.IndexOf(':'); return i >= 0 ? v.Substring(0, i) : null; }

        /// <summary>T-328: the result line of a ring group command (reply: ok, error, from, to as "win:name", rolled_back, rollback_failed). label = the group name when the reply has none.</summary>
        public static string GroupResult(Dictionary<string, object> reply, string label)
        {
            string from = Str(reply, "from"), to = Str(reply, "to"), fromName = GName(from), toName = GName(to) ?? label;
            if (Flag(reply, "ok"))
            {
                if (to == null) return Tx("radial.group.done.leave", fromName ?? label);
                return fromName != null ? Tx("radial.group.done.move", fromName, toName) : Tx("radial.group.done.add", toName);
            }
            string err = Str(reply, "error");
            string why = GroupWhy(GWin(to) ?? GWin(from), err);
            string head = toName != null ? toName + ": " + why : why;
            if (Flag(reply, "rolled_back") && fromName != null) return Tx("radial.group.kept", head, fromName);
            if (Flag(reply, "rollback_failed") && fromName != null) return Tx("radial.group.rollback_failed", head, fromName);
            return head;
        }

        public static string SelfTest()
        {
            string err = null;
            Action<string, string, string> eq = (name, got, want) => { if (err == null && got != want) err = name + ": '" + got + "' != '" + want + "'"; };
            var nd = new List<string> { "launch_decoy: no_decoy", "launch_decoy: not_ready" };
            using (UiText.Push("ru"))
            {
                eq("ru full", NotDone("ok", null), "");
                eq("ru noop", NotDone("noop", nd), "без эффекта: нет ловушек, перезарядка");
                eq("ru partial", NotDone("partial", new List<string> { "x: no_decoy" }), "частично: нет ловушек");
                eq("ru unknown code", NotDone("partial", new List<string> { "weird" }), "частично: weird");
                eq("ru both", RestoreSummary(Reply("restore", "restore")), "ROE и маршрут возвращены");
                eq("ru roe", RestoreSummary(Reply("restore", "nothing_saved")), "ROE возвращён");
                eq("ru route", RestoreSummary(Reply("nothing_saved", "restore")), "Маршрут возвращён");
                eq("ru nothing", RestoreSummary(Reply("nothing_saved", "nothing_saved")), "Возвращать нечего — автоматика снята");
                eq("ru player roe", RestoreSummary(Reply("player_changed", "nothing_saved")), "Игрок уже сменил ROE — не трогаю");
                eq("ru player both", RestoreSummary(Reply("player_changed", "player_changed")), "Игрок уже сменил ROE и маршрут — не трогаю");
                eq("ru mixed", RestoreSummary(Reply("restore", "player_changed")), "ROE возвращён");
                eq("ru radars", RestoreSummary(Reply("nothing_saved", "nothing_saved", "nothing_saved", "nothing_saved", "restore")), "Радары возвращены");
                eq("ru speed", RestoreSummary(Reply("nothing_saved", "nothing_saved", "restore")), "Ход возвращён");
                eq("ru all", RestoreSummary(Reply("restore", "restore", "restore", "restore", "restore")), "ROE, маршрут, ход, EMCON и радары возвращены");
                eq("ru player speed+emcon", RestoreSummary(Reply("nothing_saved", "nothing_saved", "player_changed", "player_changed")), "Игрок уже сменил ход и EMCON — не трогаю");
            }
            using (UiText.Push("en"))
            {
                eq("en noop", NotDone("noop", nd), "no effect: no decoys, recharging");
                eq("en partial", NotDone("partial", new List<string> { "x: no_decoy" }), "partial: no decoys");
                eq("en both", RestoreSummary(Reply("restore", "restore")), "ROE and route restored");
                eq("en nothing", RestoreSummary(Reply("nothing_saved", "nothing_saved")), "Nothing to restore: automation is off");
                eq("en player", RestoreSummary(Reply("player_changed", "player_changed")), "Player already changed ROE and route: leaving alone");
                eq("en all", RestoreSummary(Reply("restore", "restore", "restore", "restore", "restore")), "ROE, route, speed, EMCON and radars restored");
            }
            using (UiText.Push("ru")) { eq("ru error text", ErrorText("formation_leader"), "юнит — лидер формации"); eq("ru unknown error", ErrorText("weird"), "weird"); }
            using (UiText.Push("en")) { eq("en error text", ErrorText("not_in_flight"), "the aircraft is not airborne"); }
            if (err == null && RestoreSummary(new Dictionary<string, object>()) != null) err = "no fields -> null";
            // T-328: group move texts
            Func<string, string, string, bool, bool, bool, string, Dictionary<string, object>> gr = (from, to, error, ok, rb, rbf, x) => new Dictionary<string, object> {
                { "ok", ok }, { "error", error }, { "from", from }, { "to", to }, { "rolled_back", rb }, { "rollback_failed", rbf } };
            using (UiText.Push("ru"))
            {
                eq("ru why cap", GroupWhy("cap", "strike_loadout"), "ударная загрузка");
                eq("ru why asw", GroupWhy("asw", "no_asw_load"), "нет ПЛО-загрузки");
                eq("ru why in_group", GroupWhy("asw", "in_group:ПЛО-1"), "в группе ПЛО-1");
                eq("ru why fallback", GroupWhy("cap", "unfit"), "не подходит для группы");
                eq("ru why other window table", GroupWhy("cap", "no_asw_load"), "нет ПЛО-загрузки");
                eq("ru why unknown", GroupWhy("cap", "weird"), "weird");
                eq("ru add", GroupResult(gr(null, "asw:ПЛО-2", null, true, false, false, null), null), "Добавлен в ПЛО-2");
                eq("ru move", GroupResult(gr("cap:ПВО-1", "asw:ПЛО-2", null, true, false, false, null), null), "Переведён: ПВО-1 → ПЛО-2");
                eq("ru leave", GroupResult(gr("cap:ПВО-1", null, null, true, false, false, null), null), "Убран из ПВО-1");
                eq("ru refused", GroupResult(gr("cap:ПВО-1", "asw:ПЛО-2", "no_asw_load", false, false, false, null), null), "ПЛО-2: нет ПЛО-загрузки");
                eq("ru kept", GroupResult(gr("cap:ПВО-1", "asw:ПЛО-2", "add_failed", false, true, false, null), null), "ПЛО-2: группа не приняла борт — остался в ПВО-1");
                eq("ru rollback failed", GroupResult(gr("cap:ПВО-1", "asw:ПЛО-2", "add_failed", false, false, true, null), null), "ПЛО-2: группа не приняла борт; вернуть в ПВО-1 не удалось");
            }
            using (UiText.Push("en"))
            {
                eq("en why cap", GroupWhy("cap", "strike_loadout"), "strike loadout");
                eq("en move", GroupResult(gr("cap:C1", "asw:A2", null, true, false, false, null), null), "Moved: C1 → A2");
                eq("en add", GroupResult(gr(null, "asw:A2", null, true, false, false, null), null), "Added to A2");
                eq("en leave", GroupResult(gr("cap:C1", null, null, true, false, false, null), null), "Removed from C1");
                eq("en kept", GroupResult(gr("cap:C1", "asw:A2", "add_failed", false, true, false, null), null), "A2: the group refused the aircraft — stays in C1");
            }
            return err;
        }
    }
}
