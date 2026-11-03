using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-161: one own ship / submarine for the rescue section of the SAR window.</summary>
    internal sealed class SarShipRow
    {
        public int Uid; public string Name, Type, Kind;   // Kind: ship | sub
        public bool Active, Surfaced;                      // on a rescue run; sub on the surface
        public int Done, Total;                            // rafts picked up / in the area (Active only)
        public double RadiusNm;                            // radius of the run, 0 = all known (Active only)
        public string Busy;                                // null = free; busy:asw_group | busy:smart_attack | ext_sar | game_sar_zone (not for Active)
        public string Group;                               // name of the ASW group when Busy = busy:asw_group
    }

    /// <summary>T-161: model of the "Ships and submarines" section of the SAR tab (pure C#, no game). Sorting, texts, buttons, commands and the one-line answer.</summary>
    internal static class SarShipPanel
    {
        /// <summary>Radius segments; 0 = all known rafts.</summary>
        public static readonly double[] Radii = { 5, 10, 20, 0 };

        private static string T(string key) { return UiText.Get(key); }

        private static string F(string key, params object[] a) { return UiText.Fmt(key, a); }

        public static List<string> RadiusLabels()
        {
            var l = new List<string>();
            foreach (double r in Radii) l.Add(r > 0 ? r.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : T("sar.ships.all"));
            return l;
        }

        private static int Rank(SarShipRow r) { return r.Active ? 0 : r.Busy == null ? 1 : 2; }

        /// <summary>Active runs first, then free, then busy; by name inside.</summary>
        public static List<SarShipRow> Sorted(IList<SarShipRow> rows)
        {
            var l = new List<SarShipRow>(rows ?? new SarShipRow[0]);
            l.Sort((a, b) =>
            {
                int c = Rank(a).CompareTo(Rank(b));
                if (c != 0) return c;
                c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Uid.CompareTo(b.Uid);
            });
            return l;
        }

        public static string StateText(SarShipRow r)
        {
            if (r.Active) return F("sar.caption", r.Done, r.Total) + (r.Surfaced ? " " + T("sar.ships.state.surfaced") : "");
            if (r.Busy != null) return BusyText(r.Busy, r.Group);
            return T("sar.ships.state.free");
        }

        public static UiTone RowTone(SarShipRow r) { return !r.Active && r.Busy != null ? UiTone.Dim : UiTone.Normal; }

        /// <summary>"2 on SAR" | row count | null when there are no rows.</summary>
        public static string Counter(IList<SarShipRow> rows)
        {
            if (rows == null || rows.Count == 0) return null;
            int n = 0;
            foreach (var r in rows) if (r.Active) n++;
            return n > 0 ? F("sar.ships.counter", n) : rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool Fits(string op, SarShipRow r) { return op == "stop" ? r.Active : !r.Active && r.Busy == null; }

        private static bool AnyPicked(string op, IList<SarShipRow> rows, ICollection<int> picked)
        {
            if (rows == null || picked == null) return false;
            foreach (var r in rows) if (picked.Contains(r.Uid) && Fits(op, r)) return true;
            return false;
        }

        public static SarButton Start(IList<SarShipRow> rows, ICollection<int> picked)
        {
            return new SarButton { Id = "ship_start", Label = T("sar.ships.btn.start"), Tip = T("sar.ships.tip.start"), Enabled = AnyPicked("start", rows, picked) };
        }

        public static SarButton Stop(IList<SarShipRow> rows, ICollection<int> picked)
        {
            return new SarButton { Id = "ship_stop", Label = T("sar.ships.btn.stop"), Tip = T("sar.ships.tip.stop"), Enabled = AnyPicked("stop", rows, picked) };
        }

        public static string Hint(IList<SarShipRow> rows, ICollection<int> picked)
        {
            if (rows == null || rows.Count == 0) return T("sar.ships.hint.none");
            if (picked != null) foreach (var r in rows) if (picked.Contains(r.Uid)) return null;
            return T("sar.ships.hint.pick");
        }

        /// <summary>op = start|stop: commands for the picked rows that fit the op (start: free; stop: active). radiusNm: 5|10|20|0.</summary>
        public static List<Dictionary<string, object>> Commands(string op, IList<SarShipRow> rows, ICollection<int> picked, double radiusNm)
        {
            var l = new List<Dictionary<string, object>>();
            if (rows == null || picked == null || (op != "start" && op != "stop")) return l;
            foreach (var r in Sorted(rows))
            {
                if (!picked.Contains(r.Uid) || !Fits(op, r)) continue;
                var c = new Dictionary<string, object> { { "uid", (double)r.Uid }, { "op", op } };
                if (op == "start") c["radius_nm"] = radiusNm;
                l.Add(c);
            }
            return l;
        }

        /// <summary>Picked busy rows that "Start" skips: (row, a refusal ack), so the answer line can name the reason.</summary>
        public static List<KeyValuePair<SarShipRow, string>> Skipped(IList<SarShipRow> rows, ICollection<int> picked)
        {
            var l = new List<KeyValuePair<SarShipRow, string>>();
            if (rows == null || picked == null) return l;
            foreach (var r in Sorted(rows))
                if (picked.Contains(r.Uid) && !r.Active && r.Busy != null)
                    l.Add(new KeyValuePair<SarShipRow, string>(r, "{\"ok\":false,\"error\":" + Json.Str(r.Busy) + "}"));
            return l;
        }

        public static string BusyText(string code) { return BusyText(code, null); }

        /// <summary>busy:asw_group -> "busy: group ASW-1"; ext_sar / game_sar_zone use the sar.reason.* texts; unknown codes are shown as they are.</summary>
        public static string BusyText(string code, string group)
        {
            if (string.IsNullOrEmpty(code)) return "";
            if (code.StartsWith("!", StringComparison.Ordinal)) code = code.Substring(1);
            switch (code)
            {
                case "busy:asw_group": return string.IsNullOrEmpty(group) ? T("sar.ships.busy.asw_group_noname") : F("sar.ships.busy.asw_group", group);
                case "busy:smart_attack": return T("sar.ships.busy.smart_attack");
                case "busy:sar_ship": return T("sar.ships.busy.sar_ship");
                default: return SarPanel.ErrText(code);
            }
        }

        private static string AckError(string ack)
        {
            Dictionary<string, object> d = null;
            try { d = string.IsNullOrEmpty(ack) ? null : Json.Parse(ack) as Dictionary<string, object>; }
            catch (FormatException) { }
            if (d == null) return "bad_ack";
            object ok;
            if (d.TryGetValue("ok", out ok) && ok is bool && (bool)ok) return null;
            object e;
            return d.TryGetValue("error", out e) && e is string ? (string)e : "failed";
        }

        /// <summary>One line: "Rescue started: 3 of 4; Kirov — busy: group ASW-1". op = start|stop. Tone: Ok all, Warn some, Error none.</summary>
        public static string Summary(string op, IList<KeyValuePair<SarShipRow, string>> results, out UiTone tone)
        {
            int ok = 0, total = results == null ? 0 : results.Count;
            string first = null;
            if (results != null)
                foreach (var kv in results)
                {
                    string err = AckError(kv.Value);
                    if (err == null) { ok++; continue; }
                    if (first == null) first = F("sar.ships.say.refused", kv.Key == null ? "?" : kv.Key.Name, BusyText(err, kv.Key == null ? null : kv.Key.Group));
                }
            tone = total == 0 || ok == 0 ? UiTone.Error : ok == total ? UiTone.Ok : UiTone.Warn;
            string head = op == "stop" ? F("sar.ships.say.stopped", ok) : F("sar.ships.say.started", ok, total);
            return first == null ? head : head + "; " + first;
        }

        public static string SelfTest()
        {
            foreach (string lg in new[] { "ru", "en" })
                using (UiText.Push(lg))
                {
                    bool ru = lg == "ru";
                    var rows = new List<SarShipRow>
                    {
                        new SarShipRow { Uid = 1, Name = "Yorktown", Type = "CG-47", Kind = "ship" },
                        new SarShipRow { Uid = 2, Name = "Spruance", Type = "DD-963", Kind = "ship", Busy = "busy:asw_group", Group = "ASW-1" },
                        new SarShipRow { Uid = 3, Name = "Kirov", Type = "CGN", Kind = "ship", Active = true, Done = 2, Total = 5 },
                        new SarShipRow { Uid = 4, Name = "Alpha", Type = "SSN", Kind = "sub", Active = true, Surfaced = true, Done = 0, Total = 1 },
                        new SarShipRow { Uid = 5, Name = "Bravo", Type = "DD", Kind = "ship", Busy = "ext_sar" },
                        new SarShipRow { Uid = 6, Name = "Charlie", Type = "FF", Kind = "ship", Busy = "busy:asw_group" },
                    };
                    var s = Sorted(rows);
                    string order = "";
                    foreach (var r in s) order += r.Uid;
                    if (order != "431562") return "sorted " + order + " " + lg;
                    if (rows[0].Uid != 1) return "sorted must copy " + lg;
                    if (StateText(rows[2]) != (ru ? "SAR: 2/5 плотов" : "SAR: 2/5 rafts")) return "state active " + StateText(rows[2]);
                    if (StateText(rows[3]) != (ru ? "SAR: 0/1 плотов · в надводном" : "SAR: 0/1 rafts · surfaced")) return "state surfaced " + StateText(rows[3]);
                    if (StateText(rows[0]) != (ru ? "свободен" : "free")) return "state free " + lg;
                    if (StateText(rows[1]) != (ru ? "занят: группа ASW-1" : "busy: group ASW-1")) return "state busy " + StateText(rows[1]);
                    if (StateText(rows[5]) != (ru ? "занят: группа ПЛО" : "busy: ASW group")) return "state busy noname " + StateText(rows[5]);
                    if (RowTone(rows[1]) != UiTone.Dim || RowTone(rows[0]) != UiTone.Normal || RowTone(rows[2]) != UiTone.Normal) return "tone " + lg;
                    if (Counter(rows) != (ru ? "2 на SAR" : "2 on SAR")) return "counter " + Counter(rows);
                    if (Counter(new[] { rows[0], rows[1] }) != "2" || Counter(new SarShipRow[0]) != null || Counter(null) != null) return "counter free " + lg;
                    var rl = RadiusLabels();
                    if (rl.Count != 4 || rl[0] != "5" || rl[2] != "20" || rl[3] != (ru ? "Все" : "All")) return "radius labels " + lg;

                    var none = new HashSet<int>();
                    if (Start(rows, none).Enabled || Stop(rows, none).Enabled) return "buttons none " + lg;
                    var pickFree = new HashSet<int> { 1 };
                    if (!Start(rows, pickFree).Enabled || Stop(rows, pickFree).Enabled) return "buttons free " + lg;
                    var pickAct = new HashSet<int> { 3 };
                    if (Start(rows, pickAct).Enabled || !Stop(rows, pickAct).Enabled) return "buttons active " + lg;
                    var pickBusy = new HashSet<int> { 2 };
                    if (Start(rows, pickBusy).Enabled || Stop(rows, pickBusy).Enabled) return "buttons busy " + lg;
                    if (Start(rows, pickFree).Id != "ship_start" || Stop(rows, pickAct).Id != "ship_stop") return "button ids";
                    if (Hint(new SarShipRow[0], none) != (ru ? "Нет своих кораблей и ПЛ" : "No own ships or submarines")) return "hint none " + lg;
                    if (Hint(rows, none) != (ru ? "Отметьте корабли или ПЛ" : "Tick ships or submarines")) return "hint pick " + lg;
                    if (Hint(rows, pickFree) != null) return "hint null " + lg;

                    var all = new HashSet<int> { 1, 2, 3, 4, 5 };
                    var cs = Commands("start", rows, all, 10);
                    if (cs.Count != 1 || (double)cs[0]["uid"] != 1 || (string)cs[0]["op"] != "start" || (double)cs[0]["radius_nm"] != 10) return "cmd start " + cs.Count;
                    cs = Commands("start", rows, new HashSet<int> { 1, 6 }, 0);
                    if (cs.Count != 1 || (double)cs[0]["radius_nm"] != 0) return "cmd start all " + cs.Count;
                    cs = Commands("stop", rows, all, 20);
                    if (cs.Count != 2 || (double)cs[0]["uid"] != 4 || (double)cs[1]["uid"] != 3 || cs[0].ContainsKey("radius_nm") || (string)cs[0]["op"] != "stop") return "cmd stop " + cs.Count;
                    if (Commands("bogus", rows, all, 5).Count != 0) return "cmd bogus";

                    if (Skipped(rows, all).Count != 2) return "skipped all";
                    var sk = Skipped(rows, new HashSet<int> { 1, 2, 3 });
                    if (sk.Count != 1 || sk[0].Key.Uid != 2) return "skipped " + sk.Count;
                    string okAck = "{\"type\":\"ack\",\"ok\":true,\"data\":{}}";
                    var res = new List<KeyValuePair<SarShipRow, string>> { new KeyValuePair<SarShipRow, string>(rows[0], okAck), new KeyValuePair<SarShipRow, string>(rows[4], okAck) };
                    UiTone tone;
                    string say = Summary("start", res, out tone);
                    if (say != (ru ? "Спасение начато: 2 из 2" : "Rescue started: 2 of 2") || tone != UiTone.Ok) return "summary ok " + say;
                    res.Add(sk[0]);
                    say = Summary("start", res, out tone);
                    if (say != (ru ? "Спасение начато: 2 из 3; Spruance — занят: группа ASW-1" : "Rescue started: 2 of 3; Spruance — busy: group ASW-1") || tone != UiTone.Warn) return "summary part " + say;
                    say = Summary("start", new List<KeyValuePair<SarShipRow, string>> { sk[0] }, out tone);
                    if (tone != UiTone.Error || !say.StartsWith(ru ? "Спасение начато: 0 из 1" : "Rescue started: 0 of 1", StringComparison.Ordinal)) return "summary none " + say;
                    say = Summary("stop", new List<KeyValuePair<SarShipRow, string>> { new KeyValuePair<SarShipRow, string>(rows[2], okAck), new KeyValuePair<SarShipRow, string>(rows[3], okAck) }, out tone);
                    if (say != (ru ? "Спасение прекращено: 2" : "Rescue stopped: 2") || tone != UiTone.Ok) return "summary stop " + say;
                    Summary("start", null, out tone);
                    if (tone != UiTone.Error) return "summary null " + lg;

                    foreach (string code in new[] { "busy:asw_group", "busy:smart_attack", "busy:sar_ship", "ext_sar", "game_sar_zone", "!ext_sar", "unknown_unit", "bad_unit", "not_active" })
                    {
                        string t = BusyText(code);
                        if (string.IsNullOrEmpty(t) || t.StartsWith("SeaPowerTacticalUI.", StringComparison.Ordinal)) return "busy text " + code + " " + lg;
                    }
                    if (BusyText("ext_sar") != SarWindow.ReasonText("ext_sar")) return "busy ext_sar " + lg;
                    foreach (string t in new[] { T("sar.ships.sec"), T("sar.ships.col.ship"), T("sar.ships.col.type"), T("sar.ships.col.state"), T("sar.ships.lbl.radius"), T("sar.ships.tip.row"), Start(rows, none).Label, Stop(rows, none).Label, Start(rows, none).Tip, Stop(rows, none).Tip })
                        if (string.IsNullOrEmpty(t) || t.StartsWith("SeaPowerTacticalUI.", StringComparison.Ordinal)) return "text missing " + lg + " " + t;
                }
            return null;
        }
    }
}
