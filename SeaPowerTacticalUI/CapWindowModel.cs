// T-112 stream W: pure model of the CAP window GUI (command builders, cells, drawing maths). No Unity, no game: tested in captest ("windowmodel").
// All visible strings are UiText keys cap.* (lang/language_xx/sea_power_tactical_ui.cap.ini, T-103).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal static class CapWindowModel
    {
        public static readonly string[] Roes = { "tight", "free", "hold" };

        public static string Tx(string key, params object[] args) { return UiText.Fmt(key, args); }

        public static double? Num(string s)
        {
            double d;
            return double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d) ? (double?)d : null;
        }

        /// <summary>A number for a text field: at most one decimal, no trailing zeros ("300", "37.5").</summary>
        public static string Fmt(double v) { return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture); }

        public static int? Int(string s)
        {
            double? d = Num(s);
            return d.HasValue && Math.Abs(d.Value - Math.Round(d.Value)) < 1e-9 ? (int?)(int)Math.Round(d.Value) : null;
        }

        // ---- mouse drawing ------------------------------------------------------------------------------------------------------------
        public static bool NormalizeRect(double x0, double y0, double x1, double y1, double minPx, out double[] r)
        {
            r = new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1) };
            return r[2] - r[0] >= minPx && r[3] - r[1] >= minPx;
        }

        public static bool PixelToGeo(double[] aff, double x, double y, out double lat, out double lon)
        {
            lat = lon = 0;
            if (aff == null || aff.Length < 6) return false;
            double det = aff[0] * aff[4] - aff[1] * aff[3];
            if (Math.Abs(det) < 1e-12) return false;
            double px = x - aff[2], py = y - aff[5];
            lat = (px * aff[4] - aff[1] * py) / det;
            lon = (aff[0] * py - aff[3] * px) / det;
            return true;
        }

        /// <summary>Screen rectangle {x0,y0,x1,y1} -> zone {lat0, lon0, lat1, lon1} (min/max, the cap_order 4-number form); null when the projection is singular.</summary>
        public static double[] PixelRectToZone(double[] aff, double[] r)
        {
            double la0, lo0, la1, lo1;
            if (!PixelToGeo(aff, r[0], r[1], out la0, out lo0) || !PixelToGeo(aff, r[2], r[3], out la1, out lo1)) return null;
            return new[] { Math.Min(la0, la1), Math.Min(lo0, lo1), Math.Max(la0, la1), Math.Max(lo0, lo1) };
        }

        // ---- commands (same dictionaries as the link) ---------------------------------------------------------------------------------
        private static List<object> Uids(IEnumerable<int> uids) { var l = new List<object>(); foreach (int u in uids) l.Add((double)u); return l; }

        private static void Common(Dictionary<string, object> c, int n, int k, string roe, int anchorUid, int altFt = -1)
        {
            if (altFt >= 0) c["alt_ft"] = (double)altFt;
            if (n > 0) c["on_station"] = (double)n;
            if (k >= 0) c["ready"] = (double)k;
            if (!string.IsNullOrEmpty(roe)) c["roe"] = roe;
            if (anchorUid != 0) c["anchor_uid"] = (double)anchorUid;
        }

        public static Dictionary<string, object> OrderRect(string group, double[] zone, int n, int k, string roe, int anchorUid, int altFt = -1)
        {
            if (string.IsNullOrEmpty(group) || zone == null || zone.Length < 4) return null;
            var c = new Dictionary<string, object> { { "group", group }, { "rect", new List<object> { zone[0], zone[1], zone[2], zone[3] } } };
            Common(c, n, k, roe, anchorUid, altFt);
            return c;
        }

        /// <summary>Polar order: origin = ship (originUid != 0) or a point. size = east-west, width = north-south (0 = same as size).</summary>
        public static Dictionary<string, object> OrderPolar(string group, double bearing, double distNm, double sizeNm, double widthNm, int originUid, GeoPt? origin,
                                                            int n, int k, string roe, int anchorUid, int altFt = -1)
        {
            if (string.IsNullOrEmpty(group)) return null;
            var c = new Dictionary<string, object> { { "group", group }, { "bearing", bearing }, { "dist_nm", distNm }, { "size_nm", sizeNm } };
            if (widthNm > 0) c["w_nm"] = widthNm;
            if (originUid != 0) c["origin_uid"] = (double)originUid;
            else if (origin.HasValue) { c["origin_lat"] = origin.Value.Lat; c["origin_lon"] = origin.Value.Lon; }
            Common(c, n, k, roe, anchorUid, altFt);
            return c;
        }

        public static Dictionary<string, object> GroupCreate(IEnumerable<int> uids) { return new Dictionary<string, object> { { "op", "create" }, { "uids", Uids(uids) } }; }
        public static Dictionary<string, object> GroupAdd(string group, IEnumerable<int> uids) { return new Dictionary<string, object> { { "op", "add" }, { "group", group }, { "uids", Uids(uids) } }; }
        public static Dictionary<string, object> GroupRemove(string group, IEnumerable<int> uids) { return new Dictionary<string, object> { { "op", "remove" }, { "group", group }, { "uids", Uids(uids) } }; }
        public static Dictionary<string, object> GroupDisband(string group) { return new Dictionary<string, object> { { "op", "disband" }, { "group", group } }; }
        public static Dictionary<string, object> GroupRename(string group, string name) { return new Dictionary<string, object> { { "op", "rename" }, { "group", group }, { "name", name } }; }   // T-326

        /// <summary>T-326: the group name from the ack of a rename ({"ok":true,"group":new,...}); null = none (an error or a broken ack).</summary>
        public static string RenamedTo(string ack)
        {
            if (string.IsNullOrEmpty(ack) || ack[0] == '!') return null;
            try
            {
                var d = Json.Parse(ack) as Dictionary<string, object>;
                object g;
                return d != null && d.TryGetValue("group", out g) && g is string && ((string)g).Length > 0 ? (string)g : null;
            }
            catch (FormatException) { return null; }
        }

        public static Dictionary<string, object> StopCmd(string groupOrNull)
        {
            return string.IsNullOrEmpty(groupOrNull) ? new Dictionary<string, object> { { "all", true } } : new Dictionary<string, object> { { "group", groupOrNull } };
        }
        public static Dictionary<string, object> LoadoutCmd(string group, string type, string loadout)
        {
            return new Dictionary<string, object> { { "group", group }, { "type", type }, { "loadout", string.IsNullOrEmpty(loadout) ? "auto" : loadout } };
        }

        /// <summary>Thresholds from the text fields: missiles % (0..100) -> missile_pct 0..1, ring margin nm (0..500, T-330), fuel floor % (0..50) -> 0..0.5. Empty field = unchanged. err = bad_param.</summary>
        public static Dictionary<string, object> ParamsCmd(string group, string missilePct, string marginNm, string floorPct, bool needLong, bool dash, out string err)
        {
            err = null;
            var c = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(group)) c["group"] = group;
            if (!string.IsNullOrWhiteSpace(missilePct)) { double? v = Num(missilePct); if (!v.HasValue || v.Value < 0 || v.Value > 100) { err = "bad_param"; return null; } c["missile_pct"] = v.Value / 100.0; }
            if (!string.IsNullOrWhiteSpace(marginNm)) { double? v = Num(marginNm); if (!v.HasValue || v.Value < 0 || v.Value > 500) { err = "bad_param"; return null; } c["ring_margin_nm"] = v.Value; }
            if (!string.IsNullOrWhiteSpace(floorPct)) { double? v = Num(floorPct); if (!v.HasValue || v.Value < 0 || v.Value > 50) { err = "bad_param"; return null; } c["fuel_floor_pct"] = v.Value / 100.0; }
            c["need_long_range"] = needLong;
            c["dash_rtb"] = dash;
            return c;
        }

        /// <summary>Ack "{...ok...}" -> null, "!code" -> code, anything else -> "?".</summary>
        public static string AckError(string ack)
        {
            if (string.IsNullOrEmpty(ack)) return null;
            if (ack[0] == '!') return ack.Substring(1);
            return ack.Contains("\"ok\":false") ? "?" : null;
        }

        public static string CmdLogLine(string name, string ack) { return "cap cmd " + name + " -> " + (string.IsNullOrEmpty(ack) ? "ok" : (ack[0] == '!' ? ack : "ok")); }

        // ---- cells ---------------------------------------------------------------------------------------------------------------------
        // Columns of the member table: aircraft | ship | type | where | lane | missiles | fuel. Candidates: aircraft | ship | type | where.
        public const int CAircraft = 0, CShip = 1, CType = 2, CWhere = 3, CSlot = 4, CAam = 5, CFuel = 6, CandidateWhere = 3, CLoadout = 4;   // CLoadout: the 5th column of "Available" (T-325)

        /// <summary>The type for the table: the display name of the game when known, else the type id.</summary>
        public static string TypeText(string typeLabel, string type) { return !string.IsNullOrEmpty(typeLabel) ? typeLabel : (type ?? ""); }

        /// <summary>A loadout as the window shows it: the game's display name, else the internal name.</summary>
        public static string LoadoutText(string name, string display) { return !string.IsNullOrWhiteSpace(display) ? display.Trim() : (name ?? ""); }

        public static string ShipCell(int shipUid, string shipName) { return shipUid != 0 && !string.IsNullOrEmpty(shipName) ? shipName : "\u2014"; }

        // ---- T-179 / ADR-0079: the alert kind in the window ------------------------------------------------------------------------------

        /// <summary>"· Alert" of the group chip; null for a patrol.</summary>
        public static string ModeChip(CapGroupView g) { return g != null && g.Kind == "alert" && g.Ordered ? Tx("cap.chip.mode_alert") : null; }

        /// <summary>"ready 2/3 · airborne 1 · at CAP-1: 1" (the status line of an alert group); null for a patrol.</summary>
        public static string AlertLine(CapGroupView g)
        {
            if (g == null || g.Kind != "alert" || g.AlertSpec == null) return null;
            var parts = new List<string> { Tx("cap.line.alert_ready", g.ReadyNow, g.AlertSpec.Ready) };
            if (g.PrepN > 0) parts.Add(Tx("cap.line.alert_prep", g.PrepN, UiText.Duration(g.PrepInS)));   // T-204
            if (g.Airborne > 0) parts.Add(Tx("cap.line.alert_air", g.Airborne));
            var order = new List<string>(); var count = new Dictionary<string, int>();
            foreach (var kv in g.Lent) { int c; count.TryGetValue(kv.Value, out c); count[kv.Value] = c + 1; if (c == 0) order.Add(kv.Value); }
            foreach (string to in order) parts.Add(Tx("cap.line.lent", to, count[to]));
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>The tip of the "Scramble now" button: what it does, or why it is grey.</summary>
        public static string ScrambleTip(CapGroupView g)
        {
            if (g == null || g.Kind != "alert" || !g.Ordered) return Tx("cap.hint.scramble_not_alert");
            if (g.ScrambleBlock == "no_ready") return Tx("cap.hint.scramble_no_ready");
            return g.ScrambleBlock != null ? Tx("cap.hint.scramble_not_alert") : Tx("cap.tip.scramble");
        }

        /// <summary>T-333: the state of a bird in an intercept / holding fire, for the "Place" cell; null = none.</summary>
        public static string IcptCell(CapMemberView m)
        {
            if (m == null) return null;
            var l = new List<string>();
            if (m.Icpt != null)
            {
                string tg = "#" + m.IcptTarget.ToString(CultureInfo.InvariantCulture);
                if (m.Icpt == "lost") l.Add(Tx("cap.where.icpt_lost", CapWindow.Mmss(Math.Max(0, m.IcptLostS))));
                else if (m.Icpt == "fight" || m.Icpt == "handed") l.Add(Tx("cap.where.icpt_ai", tg));
                else l.Add(Tx("cap.where.icpt", tg, m.IcptEtaS >= 0 ? " (\u2248 " + CapWindow.Mmss(m.IcptEtaS) + ")" : ""));
            }
            if (m.Ff >= 0) l.Add(Tx("cap.where.ff_hold", CapWindow.Mmss(m.Ff)));
            return l.Count == 0 ? null : string.Join(" \u00B7 ", l.ToArray());
        }

        /// <summary>T-333: the group chip "intercept: birds N, targets M"; null = nobody intercepts.</summary>
        public static string IcptLine(CapGroupView g)
        {
            if (g == null || g.Kind == "alert" || g.IcptBirds <= 0) return null;
            return Tx("cap.line.icpt", g.IcptBirds, g.IcptTargets);
        }

        public static string[] MemberCells(CapMemberView m, string name)
        {
            string reason = m.Reason;
            if ((m.Where == "deck_prep" || m.Where == "deck_cool") && m.ReadyInS >= 0) reason = UiText.Duration(m.ReadyInS);
            else if (m.Where == "hangar") reason = m.ReadyKnown && m.ReadyInS > 0 ? UiText.Duration(m.ReadyInS) : null;   // live finding 0.27.2: the hangar row shows its prep time too; a guess (no CAP loadout) shows none
            string takeoff = m.TakeoffInS >= 0 && (m.Where == "deck_prep" || m.Where == "deck_ready") ? UiText.Duration(m.TakeoffInS) : null;
            return new[]
            {
                name ?? ("#" + m.Uid.ToString(CultureInfo.InvariantCulture)),
                ShipCell(m.ShipUid, m.ShipName),
                TypeText(m.TypeLabel, m.Type),
                CapWindow.WhereText(m.Where, reason, takeoff) + (m.LentFrom != null ? " · " + Tx("cap.where.lent_from", m.LentFrom) : "") + (m.AutoFill ? " · " + Tx("cap.where.auto") : "") + (m.AlertState == "scramble" ? " · " + Tx("cap.where.scramble") : "") + (IcptCell(m) != null ? " · " + IcptCell(m) : ""),
                m.Slot >= 0 ? (m.Slot + 1).ToString(CultureInfo.InvariantCulture) : "\u2014",
                CapWindow.AamCell(m.AamNow, m.AamStart, null),
                m.FuelPct >= 0 ? CapWindow.FuelCell(m.FuelPct, m.LeaveInS) : "\u2014",
            };
        }

        public static string[] CandidateCells(CapCandidateView c, string name)
        {
            return new[] { name ?? ("#" + c.Uid.ToString(CultureInfo.InvariantCulture)), ShipCell(c.ShipUid, c.ShipName), TypeText(c.TypeLabel, c.Type), CapWindow.WhereText(c.Where, null), string.IsNullOrEmpty(c.LoadoutText) ? "\u2014" : c.LoadoutText };   // T-325: + loadout
        }

        /// <summary>zone | to_zone | leaving | player | game_zone: the machine is in the air (its own unit on the map); the other places are a deck.</summary>
        public static bool IsAirWhere(string where)
        {
            return where == "zone" || where == "to_zone" || where == "leaving" || where == "player" || where == "game_zone";
        }

        /// <summary>T-186: tip of the aircraft cell of a hangar slot row: how many machines of the type are in the hangar; "" for other rows.</summary>
        internal static string HangarTip(int n) { return n > 0 ? Tx("cap.tip.hangar_n", n) : ""; }

        /// <summary>The unit the map is centred on by a click on the aircraft name: in the air - itself, on a deck - its ship (0 = nothing to show).</summary>
        public static int AircraftFocusUid(bool air, int uid, int shipUid) { return air ? uid : shipUid; }

        /// <summary>A row for the naming rule (members and candidates of the view in one list).</summary>
        internal sealed class NameRow { public int Uid, ShipUid, HangarK; public string Name, Serial, TypeKey; public bool Air; }

        private static bool RealName(string n) { return !string.IsNullOrEmpty(n) && n[0] != '#'; }   // "#uid" was the old placeholder

        /// <summary>uid -> the name shown in the "aircraft" column: in the air the unit's own name; else the serial number; else "type·N" (N = order among such machines of this type on this ship).</summary>
        internal static Dictionary<int, string> DisplayNames(IList<NameRow> rows)
        {
            var res = new Dictionary<int, string>();
            var need = new List<NameRow>();
            foreach (var r in rows)
            {
                if (res.ContainsKey(r.Uid)) continue;
                if (r.Air && RealName(r.Name)) res[r.Uid] = r.Name;
                else if (!string.IsNullOrEmpty(r.Serial)) res[r.Uid] = r.Serial;
                else if (RealName(r.Name)) res[r.Uid] = r.Name;
                else { res[r.Uid] = null; need.Add(r); }
            }
            need.Sort((a, b) => b.Uid.CompareTo(a.Uid));   // hangar rows: -(ship*10000 + slot*100 + k): the larger uid is the earlier machine
            var count = new Dictionary<string, int>();
            foreach (var r in need)
            {
                if (r.HangarK > 0 && !string.IsNullOrEmpty(r.TypeKey)) { res[r.Uid] = r.TypeKey; continue; }   // T-186: a slot has no stable number (ADR-0082 s.5): the type, the tip says how many
                string key = r.ShipUid.ToString(CultureInfo.InvariantCulture) + "|" + r.TypeKey;
                int n;
                count.TryGetValue(key, out n);
                count[key] = ++n;
                res[r.Uid] = (string.IsNullOrEmpty(r.TypeKey) ? "#" : r.TypeKey + "\u00b7") + n.ToString(CultureInfo.InvariantCulture);
            }
            return res;
        }

        internal static Dictionary<int, string> DisplayNames(CapView v)
        {
            var rows = new List<NameRow>();
            foreach (var g in v.Groups)
                foreach (var m in g.Members)
                    rows.Add(new NameRow { Uid = m.Uid, ShipUid = m.ShipUid, Name = m.Name, Serial = m.Serial, TypeKey = TypeText(m.TypeLabel, m.Type), Air = m.Air, HangarK = m.HangarK });
            foreach (var c in v.Candidates)
                rows.Add(new NameRow { Uid = c.Uid, ShipUid = c.ShipUid, Name = c.Name, Serial = c.Serial, TypeKey = TypeText(c.TypeLabel, c.Type), Air = c.Air, HangarK = c.HangarK });
            return DisplayNames(rows);
        }

        // ---- why a machine is not free (the "Excluded" section is gone, ADR-0052) -----------------------------------------------------------
        private static string ReasonClass(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "other";
            if (reason.StartsWith("in_group:", StringComparison.Ordinal)) return "in_group";
            return reason == "player" || reason == "game_cap_zone" || reason == "hidden" || reason == "ext_sar" || reason == "strike_loadout" ? reason : "other";
        }

        /// <summary>"2 in groups, 1 with the player" for the line of the empty "Available"; "" when nothing is excluded.</summary>
        public static string RefusalSummary(IList<CapCandidateView> excl)
        {
            if (excl == null || excl.Count == 0) return "";
            var order = new List<string>(); var n = new Dictionary<string, int>();
            foreach (var c in excl)
            {
                string k = ReasonClass(c.Reason);
                if (!n.ContainsKey(k)) { n[k] = 0; order.Add(k); }
                n[k]++;
            }
            var parts = new List<string>();
            foreach (string k in order) parts.Add(Tx("cap.why." + k, n[k]));
            return Tx("cap.hint.no_free_why", string.Join(", ", parts.ToArray()));
        }

        /// <summary>"name - reason" lines, at most max, then "...N more"; the tip of the summary line.</summary>
        public static List<string> RefusalList(IList<CapCandidateView> excl, int max, IDictionary<int, string> names)
        {
            var l = new List<string>();
            if (excl == null) return l;
            for (int i = 0; i < excl.Count && i < max; i++) l.Add(RefusalLine(excl[i], names));
            if (excl.Count > max) l.Add(Tx("cap.hint.more", excl.Count - max));
            return l;
        }

        public static string RefusalLine(CapCandidateView c, IDictionary<int, string> names)
        {
            string n;
            if (names == null || !names.TryGetValue(c.Uid, out n) || n == null) n = c.Name ?? ("#" + c.Uid.ToString(CultureInfo.InvariantCulture));
            return n + " \u2014 " + CapWindow.ReasonText(c.Reason);
        }

        /// <summary>What stops "New" / "To group": with nothing free - all the excluded; otherwise the picked machines that are no longer free (excluded, or gone: reason "gone").</summary>
        public static List<CapCandidateView> Refused(ISet<int> picked, IList<CapCandidateView> free, IList<CapCandidateView> excl)
        {
            var res = new List<CapCandidateView>();
            if (free.Count == 0) { res.AddRange(excl); return res; }
            var freeUids = new HashSet<int>();
            foreach (var c in free) freeUids.Add(c.Uid);
            var seen = new HashSet<int>();
            foreach (int u in picked)
            {
                if (freeUids.Contains(u) || !seen.Add(u)) continue;
                CapCandidateView hit = null;
                foreach (var c in excl) if (c.Uid == u) { hit = c; break; }
                res.Add(hit ?? new CapCandidateView { Uid = u, Reason = "gone" });
            }
            return res;
        }

        /// <summary>null = list has rows or the excluded list explains it; "cap.ui.no_aircraft" when free == 0 and excluded == 0.</summary>
        public static string AvailableHintKey(int free, int excluded)
        {
            return free == 0 && excluded == 0 ? "cap.ui.no_aircraft" : null;
        }

        /// <summary>Error code for New / to group: "no_aircraft" (nothing to pick at all) | "missing_uids" (nothing picked) | null.</summary>
        public static string PickError(int freeCount, int pickedCount)
        {
            if (freeCount == 0) return "no_aircraft";
            return pickedCount == 0 ? "missing_uids" : null;
        }

        /// <summary>T-200: a hangar slot taken by another window/group (HangarK > 0 and Reason in_group:...): shown in "Available" dimmed, cannot be taken (ADR-0082 s.5, Q3).</summary>
        internal static bool ForeignSlot(CapCandidateView c)
        {
            return c != null && c.HangarK > 0 && !string.IsNullOrEmpty(c.Reason) && c.Reason.StartsWith("in_group:", StringComparison.Ordinal);
        }

        /// <summary>T-325: a machine with a strike / anti-ship / ASW loadout: shown in "Available" dimmed with the reason, cannot be taken.</summary>
        internal static bool StrikeRow(CapCandidateView c) { return c != null && c.Reason == "strike_loadout"; }

        /// <summary>T-200 / T-325: a row shown dimmed in "Available" that a click only explains: a foreign hangar slot or a strike-loadout machine.</summary>
        internal static bool ShownRefused(CapCandidateView c) { return ForeignSlot(c) || StrikeRow(c); }

        /// <summary>T-200: rows of the "Available" table: the free machines, the foreign hangar slots and the strike-loadout machines, in the order of v.Candidates.</summary>
        internal static List<CapCandidateView> AvailableRows(CapView v)
        {
            var l = new List<CapCandidateView>();
            if (v == null) return l;
            foreach (var c in v.Candidates) if (string.IsNullOrEmpty(c.Reason) || ShownRefused(c)) l.Add(c);
            return l;
        }

        /// <summary>T-200: cells of a foreign slot row: as CandidateCells, the "Where" column says which group holds the slot.</summary>
        public static string[] ForeignCells(CapCandidateView c, string name)
        {
            var cells = CandidateCells(c, name);
            cells[CandidateWhere] = StrikeRow(c) ? CapWindow.WhereText(c.Where, null) + " \u00B7 " + CapWindow.ReasonText(c.Reason) : CapWindow.ReasonText(c.Reason);   // T-325: strike: the place and the reason
            return cells;
        }

        public static void SplitCandidates(CapView v, List<CapCandidateView> free, List<CapCandidateView> excluded)
        {
            foreach (var c in v.Candidates) (string.IsNullOrEmpty(c.Reason) ? free : excluded).Add(c);
        }

        /// <summary>Distinct member types of a group, in first-seen order.</summary>
        public static List<string> TypesOf(CapGroupView g)
        {
            var l = new List<string>();
            foreach (var m in g.Members) if (!string.IsNullOrEmpty(m.Type) && !l.Contains(m.Type)) l.Add(m.Type);
            return l;
        }

        /// <summary>Display name of a member type (the type id when the game gave none).</summary>
        public static string TypeLabelOf(CapGroupView g, string type)
        {
            foreach (var m in g.Members) if (m.Type == type && !string.IsNullOrEmpty(m.TypeLabel)) return m.TypeLabel;
            return null;
        }

        /// <summary>Origin ship of a zone "by bearing" (live 2026-10-21): the group's carrier = the deck most of its aircraft belong to
        /// (ties: the lowest uid); without deck data the ship selected in the game; 0 = none (the map centre).</summary>
        public static int OriginShip(CapGroupView g, int selectedShip)
        {
            var decks = new List<int>();
            if (g != null) foreach (var m in g.Members) if (m != null) decks.Add(m.ShipUid);
            return OriginShipOfDecks(decks, selectedShip);
        }

        /// <summary>The same rule over the members' deck uids (0 = unknown); also the default origin of a cap_order by bearing
        /// without origin_uid / origin_lat+lon (CapController.Order, live finding 37).</summary>
        public static int OriginShipOfDecks(IEnumerable<int> deckUids, int selectedShip)
        {
            var n = new Dictionary<int, int>();
            if (deckUids != null) foreach (int d in deckUids) if (d != 0) { int c; n.TryGetValue(d, out c); n[d] = c + 1; }
            int best = 0, bestN = 0;
            foreach (var kv in n) if (kv.Value > bestN || (kv.Value == bestN && kv.Key < best)) { best = kv.Key; bestN = kv.Value; }
            return best != 0 ? best : selectedShip;
        }

        /// <summary>The ship name the group's rows carry for this deck uid (null = none: ask the game).</summary>
        public static string ShipNameOf(CapGroupView g, int shipUid)
        {
            if (g == null || shipUid == 0) return null;
            foreach (var m in g.Members) if (m != null && m.ShipUid == shipUid && !string.IsNullOrEmpty(m.ShipName)) return m.ShipName;
            return null;
        }

        /// <summary>A label of an ordered zone on the map: the group name at the zone's north-west corner (+ the deck priority warning).</summary>
        internal sealed class ZoneLabel { public int Index; public double Lat, Lon; public string Text; public bool Prio; }

        public const int ZoneLabelKeyBase = -4000000;   // IMapExtraLabels keys: negative, apart from waypoint (-1e6), intercept (-2e6), range rings (-3e6)

        public static List<ZoneLabel> ZoneLabels(CapView v)
        {
            var res = new List<ZoneLabel>();
            if (v == null) return res;
            for (int gi = 0; gi < v.Groups.Count; gi++)
            {
                var g = v.Groups[gi];
                if (g == null || !g.Ordered || g.Rect == null || g.Rect.Length < 4 || g.Line == null || string.IsNullOrEmpty(g.Line.Group)) continue;
                bool prio = g.Line.DeckPriority;
                res.Add(new ZoneLabel { Index = gi, Lat = Math.Max(g.Rect[0], g.Rect[2]), Lon = Math.Min(g.Rect[1], g.Rect[3]), Prio = prio,
                                        Text = g.Line.Group + (prio ? " · " + CapWindow.WarnText("deck_priority") : "") });
            }
            return res;
        }

        public static string LoadoutOf(CapGroupView g, string type)
        {
            string s;
            return g.Loadouts != null && g.Loadouts.TryGetValue(type, out s) && !string.IsNullOrEmpty(s) ? s : "auto";
        }

        /// <summary>Next/previous entry of ["auto", names...] (wraps). Unknown current -> first.</summary>
        public static string CycleLoadout(string current, IList<string> names, int dir)
        {
            var all = new List<string> { "auto" };
            if (names != null) foreach (string n in names) if (!string.IsNullOrEmpty(n) && !string.Equals(n, "auto", StringComparison.OrdinalIgnoreCase)) all.Add(n);
            int i = all.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return all[0];
            return all[((i + dir) % all.Count + all.Count) % all.Count];
        }

        public static int AamOfType(CapGroupView g, string type)
        {
            int best = 0;
            foreach (var m in g.Members) if (m.Type == type && m.AamStart > best) best = m.AamStart;
            return best;
        }

        /// <summary>Rectangle -> lanes for the overlay (same maths as the controller; params default).</summary>
        public static List<CapLane> LanesOf(CapGroupView g, CapParams p)
        {
            if (g.Lanes != null) return g.Lanes;   // T-235: the lanes the controller flies (the group's own parameters and cover radius)
            if (g.Rect == null || g.Rect.Length < 4 || g.N < 1) return new List<CapLane>();
            var pp = p ?? new CapParams();
            return CapMath.Lanes(new CapZone { Rect = g.Rect, Anchor = g.Anchor ?? "geo" }, g.N, pp, g.CoverNm > 0 ? g.CoverNm : pp.CoverFallbackNm);
        }

        private static string AlertTexts()
        {
            var g = new CapGroupView { Kind = "alert", Ordered = true, AlertSpec = new CapAlertSpec { Ready = 3 }, ReadyNow = 2, Airborne = 1 };
            g.Lent.Add(new KeyValuePair<int, string>(5, "CAP-1"));
            if (ModeChip(g) != Tx("cap.chip.mode_alert") || ModeChip(new CapGroupView { Ordered = true }) != null || ModeChip(null) != null) return "mode chip";
            string line = AlertLine(g);
            if (line != Tx("cap.line.alert_ready", 2, 3) + " · " + Tx("cap.line.alert_air", 1) + " · " + Tx("cap.line.lent", "CAP-1", 1)) return "alert line: " + line;
            if (AlertLine(new CapGroupView()) != null) return "alert line of a patrol";
            if (ScrambleTip(g) != Tx("cap.tip.scramble")) return "scramble tip ok";
            g.ScrambleBlock = "no_ready";
            if (ScrambleTip(g) != Tx("cap.hint.scramble_no_ready")) return "scramble tip no_ready";
            if (ScrambleTip(new CapGroupView { Ordered = true }) != Tx("cap.hint.scramble_not_alert")) return "scramble tip patrol";
            var m = new CapMemberView { Uid = 1, Where = "hangar", LentFrom = "CAP-2", AutoFill = true, AlertState = "scramble" };
            string wh = MemberCells(m, "x")[3];
            if (!wh.Contains(Tx("cap.where.lent_from", "CAP-2")) || !wh.Contains(Tx("cap.where.auto")) || !wh.Contains(Tx("cap.where.scramble"))) return "member cell marks: " + wh;
            // T-333: the intercept state text and the group line
            var im = new CapMemberView { Uid = 1, Where = "zone", Icpt = "guide", IcptTarget = 10, IcptEtaS = 125 };
            if (IcptCell(im) != Tx("cap.where.icpt", "#10", " (\u2248 " + CapWindow.Mmss(125) + ")")) return "icpt guide cell: " + IcptCell(im);
            im.Icpt = "fight"; if (IcptCell(im) != Tx("cap.where.icpt_ai", "#10")) return "icpt fight cell";
            im.Icpt = "lost"; im.IcptLostS = 25; if (IcptCell(im) != Tx("cap.where.icpt_lost", CapWindow.Mmss(25))) return "icpt lost cell: " + IcptCell(im);
            im.Icpt = null; im.Ff = 12; if (IcptCell(im) != Tx("cap.where.ff_hold", CapWindow.Mmss(12))) return "ff cell: " + IcptCell(im);
            if (IcptCell(new CapMemberView { Ff = -1 }) != null || IcptLine(new CapGroupView()) != null) return "icpt cell none";
            if (IcptLine(new CapGroupView { IcptBirds = 3, IcptTargets = 2 }) != Tx("cap.line.icpt", 3, 2)) return "icpt line";
            if (!MemberCells(new CapMemberView { Uid = 2, Where = "zone", Icpt = "guide", IcptTarget = 11, IcptEtaS = -1 }, "x")[3].Contains(Tx("cap.where.icpt", "#11", ""))) return "icpt in member cell";
            return null;
        }

        // ---- selftest ------------------------------------------------------------------------------------------------------------------
        public static string SelfTest()
        {
            {
                // T-186: slot rows are named by the type (no "·N"), other nameless rows keep the order number; the tip names the count
                var rows = new List<NameRow>
                {
                    new NameRow { Uid = -10, ShipUid = 7, TypeKey = "F-14A", HangarK = 1 }, new NameRow { Uid = -20, ShipUid = 7, TypeKey = "F-14A", HangarK = 2 },
                    new NameRow { Uid = -30, ShipUid = 7, TypeKey = "F-14A" }, new NameRow { Uid = -31, ShipUid = 7, TypeKey = "F-14A" },
                };
                var dn = DisplayNames(rows);
                if (dn[-10] != "F-14A" || dn[-20] != "F-14A" || dn[-30] != "F-14A\u00b71" || dn[-31] != "F-14A\u00b72") return "slot names " + dn[-10] + " " + dn[-20] + " " + dn[-30] + " " + dn[-31];
                if (HangarTip(0) != "") return "hangar tip 0";
                using (UiText.Push("ru")) if (HangarTip(5) != "в ангаре: 5 машин этого типа") return "hangar tip ru: " + HangarTip(5);
                using (UiText.Push("en")) if (HangarTip(5) != "in the hangar: 5 machines of this type") return "hangar tip en: " + HangarTip(5);
            }
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string e = AlertTexts();
                    if (e != null) return lang + ": " + e;
                }
            {
                // origin of a zone by bearing: the group's carrier, not the selected ship
                var og0 = new CapGroupView();
                og0.Members.Add(new CapMemberView { Uid = 1, ShipUid = 7, ShipName = "Dwight D. Eisenhower" });
                og0.Members.Add(new CapMemberView { Uid = 2, ShipUid = 7, ShipName = "Dwight D. Eisenhower" });
                og0.Members.Add(new CapMemberView { Uid = 3, ShipUid = 9, ShipName = "Yorktown" });
                if (OriginShip(og0, 55) != 7 || ShipNameOf(og0, 7) != "Dwight D. Eisenhower") return "origin: the carrier of most members";
                if (OriginShip(new CapGroupView(), 55) != 55 || OriginShip(null, 0) != 0) return "origin: fallback to the selected ship";
                var zl = new CapView();
                zl.Groups.Add(new CapGroupView { Ordered = true, Rect = new[] { 35.0, 20.0, 35.5, 20.6 }, Line = new CapGroupLine { Group = "CAP-1" } });
                zl.Groups.Add(new CapGroupView { Ordered = false, Rect = new[] { 35.0, 20.0, 35.5, 20.6 }, Line = new CapGroupLine { Group = "CAP-2" } });
                var zls = ZoneLabels(zl);
                if (zls.Count != 1 || zls[0].Text != "CAP-1" || zls[0].Lat != 35.5 || zls[0].Lon != 20.0 || zls[0].Index != 0) return "zone labels";
            }
            {
                // projection: x = 100*lon - 3000, y = -100*lat + 3600
                double[] aff = { 0, 100, -3000, -100, 0, 3600 };
                double[] nr;
                if (!NormalizeRect(300, 200, 200, 100, 12, out nr) || nr[0] != 200 || nr[1] != 100 || nr[2] != 300 || nr[3] != 200) return "normalize";
                if (NormalizeRect(10, 10, 14, 40, 12, out nr)) return "tiny accepted";
                var z = PixelRectToZone(aff, new[] { 200.0, 100.0, 300.0, 200.0 });
                if (z == null || Math.Abs(z[0] - 34) > 1e-9 || Math.Abs(z[1] - 32) > 1e-9 || Math.Abs(z[2] - 35) > 1e-9 || Math.Abs(z[3] - 33) > 1e-9) return "pixel zone";
                if (PixelRectToZone(new double[] { 0, 0, 0, 0, 0, 0 }, new[] { 1.0, 1, 5, 5 }) != null) return "singular";

                // the commands must be accepted by the real parser
                string err;
                var o = CapCommands.ParseOrder(OrderRect("ПВО-1", z, 2, 1, "tight", 0), out err);
                if (o == null || o.Group != "ПВО-1" || o.OnStation != 2 || o.Ready != 1 || o.Roe != "tight" || o.Zone.Rect == null || o.Zone.Anchor != "geo") return "order rect parse: " + err;
                var oa = CapCommands.ParseOrder(OrderRect("G", z, -1, -1, null, 77), out err);
                if (oa == null || oa.Zone.Anchor != "unit" || oa.Zone.AnchorUid != 77 || oa.OnStation != -1) return "order anchor parse: " + err;
                var op = CapCommands.ParseOrder(OrderPolar("G", 90, 40, 30, 20, 5, null, 3, 0, "free", 5), out err);
                if (op == null || !op.Polar || op.OriginUid != 5 || op.WNm != 20 || op.Ready != 0 || op.Zone.AnchorUid != 5) return "order polar ship: " + err;
                var og = CapCommands.ParseOrder(OrderPolar("G", 10, 40, 30, 0, 0, new GeoPt(35, 20), -1, -1, null, 0), out err);
                if (og == null || og.Zone.Rect == null || og.Origin == null) return "order polar point: " + err;
                if (OrderRect("", z, 1, 1, null, 0) != null || OrderRect("G", null, 1, 1, null, 0) != null) return "order null guard";
                // no origin: accepted, the controller takes the group's carrier (live finding 37)
                var pn = CapCommands.ParseOrder(OrderPolar("G", 10, 40, 30, 0, 0, null, -1, -1, null, 0), out err);
                if (pn == null || pn.OriginUid != 0 || pn.Origin != null || pn.Zone.Rect != null) return "polar without origin " + err;
                if (OriginShipOfDecks(new[] { 0, 9, 7, 9 }, 3) != 9 || OriginShipOfDecks(new[] { 0, 0 }, 3) != 3 || OriginShipOfDecks(null, 0) != 0) return "origin: deck uids";

                var gc = CapCommands.ParseGroup(GroupCreate(new[] { 4, 5 }));
                if (gc.err != null || gc.op != "create" || gc.uids.Count != 2) return "group create " + gc.err;
                var ga = CapCommands.ParseGroup(GroupAdd("ПВО-1", new[] { 6 }));
                if (ga.err != null || ga.op != "add" || ga.group != "ПВО-1") return "group add " + ga.err;
                var gr = CapCommands.ParseGroup(GroupRemove("ПВО-1", new[] { 6 }));
                if (gr.err != null || gr.op != "remove") return "group remove " + gr.err;
                var grn = CapCommands.ParseGroup(GroupRename("ПВО-1", "Альфа"));   // T-326
                if (grn.err != null || grn.op != "rename") return "group rename " + grn.err;
                var grp = CapCommands.ParseRename(GroupRename("ПВО-1", "Альфа"));
                if (grp.err != null || grp.name != "Альфа" || grp.group != "ПВО-1") return "parse rename " + grp.err;
                if (RenamedTo("{\"ok\":true,\"group\":\"Альфа\",\"old\":\"ПВО-1\"}") != "Альфа" || RenamedTo("!name_taken") != null || RenamedTo(null) != null) return "renamed to";
                var gd = CapCommands.ParseGroup(GroupDisband("ПВО-1"));
                if (gd.err != null || gd.op != "disband") return "group disband " + gd.err;
                if (CapCommands.ParseStop(StopCmd(null), out err) != "all" || CapCommands.ParseStop(StopCmd("G"), out err) != "G") return "stop";
                string g2, t2, l2;
                if (!CapCommands.ParseLoadout(LoadoutCmd("G", "F-14A", null), out g2, out t2, out l2, out err) || l2 != "auto" || t2 != "F-14A") return "loadout auto " + err;
                if (!CapCommands.ParseLoadout(LoadoutCmd("G", "F-14A", "AirToAir"), out g2, out t2, out l2, out err) || l2 != "AirToAir") return "loadout named " + err;

                var pc = ParamsCmd("G", "20", "5", "5", true, false, out err);
                var into = new CapParams();
                if (pc == null || !CapCommands.ParseParams(pc, into, out err) || Math.Abs(into.MissilePct - 0.20) > 1e-9 || into.RingMarginNm != 5 || Math.Abs(into.FuelFloorPct - 0.05) > 1e-9 || pc.ContainsKey("fuel_margin_s") || !into.NeedLongRange || into.DashOnMissileRtb)
                    return "params parse: " + err;
                var pe = ParamsCmd("G", "", "", "", false, true, out err);
                if (pe == null || pe.ContainsKey("missile_pct") || pe.ContainsKey("ring_margin_nm")) return "params empty fields";
                if (ParamsCmd("G", "120", "", "", false, true, out err) != null || err != "bad_param") return "params pct range";
                if (ParamsCmd("G", "", "-1", "", false, true, out err) != null) return "params margin range";
                if (ParamsCmd("G", "", "", "60", false, true, out err) != null) return "params floor range";
                if (ParamsCmd("G", "abc", "", "", false, true, out err) != null) return "params nan";

                if (AckError("{\"ok\":true,\"group\":\"G\"}") != null || AckError("!bad_roe") != "bad_roe" || AckError(null) != null) return "ack";
                if (CmdLogLine("cap_order", "!bad_rect") != "cap cmd cap_order -> !bad_rect" || CmdLogLine("cap_stop", "{\"ok\":true}") != "cap cmd cap_stop -> ok") return "cmd log";

                if (Num("1,5") != 1.5 || Num("x").HasValue || Num("") .HasValue || Int("3") != 3 || Int("2.5").HasValue) return "num";

                // cells
                var mv = new CapMemberView { Uid = 3, Name = "F-14A 301", Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Air = true, Slot = 1, Where = "zone", AamNow = 6, AamStart = 8, FuelPct = 54, LeaveInS = 4320 };
                var cells = CapWindow.In("ru", () => MemberCells(mv, "F-14A 301"));
                if (cells.Length != 7 || cells[CAircraft] != "F-14A 301" || cells[CShip] != "Yorktown" || cells[CType] != "F-14A" || cells[CWhere] != "в зоне" || cells[CSlot] != "2" || cells[CAam] != "6/8" || cells[CFuel] != "54 % · 1:12")
                    return "member cells " + string.Join("|", cells);
                var mp = CapWindow.In("en", () => MemberCells(new CapMemberView { Uid = 9, Type = "usn_f-14a", Where = "deck_prep", ReadyInS = 330, Slot = -1, FuelPct = -1 }, null));
                if (mp[CAircraft] != "#9" || mp[CShip] != "—" || mp[CType] != "usn_f-14a" || mp[CWhere] != "preparing · 6 min" || mp[CSlot] != "—" || mp[CFuel] != "—") return "member cells deck " + string.Join("|", mp);
                var mt = CapWindow.In("ru", () => MemberCells(new CapMemberView { Uid = 9, Where = "deck_prep", ReadyInS = 1560, TakeoffInS = 1740, Slot = -1, FuelPct = -1 }, null));
                if (mt[CWhere] != "готовится · 26 мин · взлёт 29 мин") return "member cells takeoff " + mt[CWhere];
                var mh = CapWindow.In("ru", () => MemberCells(new CapMemberView { Uid = 9, Where = "hangar", ReadyInS = 900, Reason = "x", Slot = -1, FuelPct = -1 }, null));
                var mh0 = CapWindow.In("en", () => MemberCells(new CapMemberView { Uid = 9, Where = "hangar", ReadyInS = 0, Slot = -1, FuelPct = -1 }, null));
                if (mh[CWhere] != "в ангаре · 15 мин" || mh0[CWhere] != "in hangar") return "member cells hangar " + mh[CWhere] + " | " + mh0[CWhere];
                var mhu = CapWindow.In("ru", () => MemberCells(new CapMemberView { Uid = 9, Where = "hangar", ReadyInS = 900, ReadyKnown = false, Slot = -1, FuelPct = -1 }, null));
                if (mhu[CWhere] != "в ангаре") return "member cells hangar guess shows a time: " + mhu[CWhere];
                var mr = CapWindow.In("en", () => MemberCells(new CapMemberView { Uid = 9, Where = "deck_ready", ReadyInS = 0, Slot = -1, FuelPct = -1 }, null));
                if (mr[CWhere] != "on deck: ready") return "member cells ready " + mr[CWhere];
                var ml = CapWindow.In("en", () => MemberCells(new CapMemberView { Uid = 1, Where = "leaving", Reason = "missiles" }, "x"));
                if (ml[CWhere] != "leaving (missiles)") return "member leaving " + ml[CWhere];

                // T-155: names of the aircraft column
                if (!IsAirWhere("zone") || !IsAirWhere("to_zone") || !IsAirWhere("leaving") || !IsAirWhere("player") || !IsAirWhere("game_zone") || IsAirWhere("hangar") || IsAirWhere("deck_prep") || IsAirWhere(null)) return "is air";
                if (AircraftFocusUid(true, 5, 7) != 5 || AircraftFocusUid(false, -5, 7) != 7 || AircraftFocusUid(false, -5, 0) != 0) return "focus uid";
                var nrows = new List<NameRow>
                {
                    new NameRow { Uid = 1, ShipUid = 7, Name = "Invader 2", TypeKey = "F-14A", Air = true },
                    new NameRow { Uid = -10001, ShipUid = 7, Name = "#-10001", TypeKey = "F-14A" },
                    new NameRow { Uid = -10002, ShipUid = 7, TypeKey = "F-14A" },
                    new NameRow { Uid = -10003, ShipUid = 7, Serial = "AB-101", TypeKey = "F-14A" },
                    new NameRow { Uid = -20001, ShipUid = 8, TypeKey = "F-14A" },
                    new NameRow { Uid = -10101, ShipUid = 7, TypeKey = "F-4J" },
                    new NameRow { Uid = 2, ShipUid = 7, TypeKey = "F-4J", Air = true },
                };
                var dn = DisplayNames(nrows);
                if (dn[1] != "Invader 2" || dn[-10001] != "F-14A·1" || dn[-10002] != "F-14A·2" || dn[-10003] != "AB-101" || dn[-20001] != "F-14A·1" || dn[2] != "F-4J·1" || dn[-10101] != "F-4J·2") return "display names " + string.Join("|", new[] { dn[1], dn[-10001], dn[-10002], dn[-10003], dn[-20001], dn[-10101], dn[2] });
                var dv = new CapView();
                var dg = new CapGroupView(); dg.Members.Add(new CapMemberView { Uid = 4, Name = "#4", Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7 });
                dv.Groups.Add(dg); dv.Candidates.Add(new CapCandidateView { Uid = 5, Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7 });
                var dn2 = DisplayNames(dv);
                if (dn2[5] != "F-14A·1" || dn2[4] != "F-14A·2") return "display names view " + dn2[4] + "|" + dn2[5];
                if (CandidateCells(new CapCandidateView { Uid = 5, Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "hangar" }, "F-14A·1").Length != 5) return "candidate cells length";

                var v = new CapView();
                v.Candidates.Add(new CapCandidateView { Uid = 1, Name = "a", Type = "F-14A", Where = "hangar" });
                v.Candidates.Add(new CapCandidateView { Uid = 2, Name = "b", Type = "F-14A", Where = "zone", Reason = "in_group:ПЛО-1" });
                var free = new List<CapCandidateView>(); var exc = new List<CapCandidateView>();
                SplitCandidates(v, free, exc);
                if (AvailableHintKey(0, 0) != "cap.ui.no_aircraft" || AvailableHintKey(0, 1) != null || AvailableHintKey(2, 0) != null) return "available hint";
                if (PickError(0, 0) != "no_aircraft" || PickError(3, 0) != "missing_uids" || PickError(3, 1) != null) return "pick error";
                if (!UiText.Has(UiText.DefaultSection, "cap.ui.no_aircraft")) return "missing text no_aircraft";
                if (free.Count != 1 || free[0].Uid != 1 || exc.Count != 1 || CapWindow.In("ru", () => CapWindow.ReasonText(exc[0].Reason)) != "в группе ПЛО-1") return "split candidates";
                if (CapWindow.In("en", () => CandidateCells(free[0], "a"))[CandidateWhere] != "in hangar") return "candidate cells";

                // T-325: the loadout column and the strike-loadout rows
                if (CapWindow.In("en", () => CandidateCells(free[0], "a"))[CLoadout] != "\u2014") return "loadout cell empty";
                var sv = new CapView();
                sv.Candidates.Add(new CapCandidateView { Uid = 1, Name = "a", Type = "F-14A", Where = "to_zone", LoadoutText = "AIM-9\u00D72" });
                sv.Candidates.Add(new CapCandidateView { Uid = 2, Name = "b", Type = "F-14A", Where = "to_zone", Reason = "strike_loadout", LoadoutText = "AIM-9\u00D72, Mk82\u00D74" });
                sv.Candidates.Add(new CapCandidateView { Uid = 3, Name = "c", Type = "F-14A", Where = "zone", Reason = "player" });
                var srows = AvailableRows(sv);
                if (srows.Count != 2 || srows[1].Uid != 2 || !StrikeRow(srows[1]) || !ShownRefused(srows[1]) || StrikeRow(srows[0]) || ShownRefused(sv.Candidates[2])) return "strike rows";
                if (CandidateCells(srows[0], "a")[CLoadout] != "AIM-9\u00D72") return "loadout cell text";
                if (CapWindow.In("ru", () => ForeignCells(srows[1], "b"))[CandidateWhere] != CapWindow.In("ru", () => CapWindow.WhereText("to_zone", null) + " \u00B7 ударная загрузка")) return "strike where ru";
                if (CapWindow.In("en", () => ForeignCells(srows[1], "b"))[CandidateWhere] != CapWindow.In("en", () => CapWindow.WhereText("to_zone", null) + " \u00B7 strike loadout")) return "strike where en";
                if (CapWindow.In("ru", () => CapWindow.ReasonText("strike_loadout")) != "ударная загрузка" || CapWindow.In("en", () => CapWindow.ReasonText("strike_loadout")) != "strike loadout") return "strike reason text";
                var sx = new List<CapCandidateView> { srows[1] };
                if (CapWindow.In("ru", () => RefusalSummary(sx)) != "Свободных нет: 1 с ударной загрузкой") return "strike summary " + CapWindow.In("ru", () => RefusalSummary(sx));
                if (CapWindow.In("en", () => RefusalSummary(sx)) != "None free: 1 with a strike loadout") return "strike summary en " + CapWindow.In("en", () => RefusalSummary(sx));

                // T-155: why nothing is free (summary line, its tip, the refusal of "New")
                exc.Add(new CapCandidateView { Uid = 3, Name = "c", Reason = "in_group:ПВО-1" });
                exc.Add(new CapCandidateView { Uid = 4, Name = "d", Reason = "player" });
                if (UiText.Has(UiText.DefaultSection, "cap.hint.no_free_why") && CapWindow.In("ru", () => RefusalSummary(exc)) != "Свободных нет: 2 в группах, 1 у игрока" || RefusalSummary(new List<CapCandidateView>()) != "") return "refusal summary " + CapWindow.In("ru", () => RefusalSummary(exc));
                var many = new List<CapCandidateView>();
                for (int i = 0; i < 10; i++) many.Add(new CapCandidateView { Uid = 100 + i, Name = "n" + i, Reason = "player" });
                var rl = CapWindow.In("ru", () => RefusalList(many, 8, null));
                bool hasTx = UiText.Has(UiText.DefaultSection, "cap.hint.more");
                if (rl.Count != 9 || (hasTx && (rl[0] != "n0 — занят игроком" || rl[8] != "…ещё 2"))) return "refusal list " + rl.Count + " " + rl[rl.Count - 1];
                var refused = Refused(new HashSet<int>(), new List<CapCandidateView>(), exc);
                if (refused.Count != 3) return "refused with nothing free " + refused.Count;
                var freeOne = new List<CapCandidateView> { free[0] };
                if (Refused(new HashSet<int> { 1 }, freeOne, exc).Count != 0) return "refused: free picked";
                var rf = Refused(new HashSet<int> { 1, 4, 99 }, freeOne, exc);
                if (rf.Count != 2 || rf[0].Uid != 4 || rf[1].Reason != "gone") return "refused stale picks";
                if (CapWindow.In("ru", () => RefusalLine(exc[2], null)) != "d — занят игроком" && hasTx) return "refusal line";

                var gv = new CapGroupView { N = 2, K = 1, Anchor = "geo", Rect = new[] { 35.0, 20.0, 35.333, 20.667 } };
                gv.Members.Add(new CapMemberView { Type = "F-14A", AamStart = 8 }); gv.Members.Add(new CapMemberView { Type = "F-14A", AamStart = 6 }); gv.Members.Add(new CapMemberView { Type = "F-15C", AamStart = 4 });
                gv.Loadouts["F-15C"] = "AirToAir";
                var ty = TypesOf(gv);
                if (ty.Count != 2 || ty[0] != "F-14A" || ty[1] != "F-15C") return "types";
                if (LoadoutOf(gv, "F-14A") != "auto" || LoadoutOf(gv, "F-15C") != "AirToAir") return "loadout of";
                if (AamOfType(gv, "F-14A") != 8) return "aam of type";
                var names = new[] { "AirToAir", "AirToAirLongRange" };
                if (CycleLoadout("auto", names, 1) != "AirToAir" || CycleLoadout("AirToAirLongRange", names, 1) != "auto" || CycleLoadout("auto", names, -1) != "AirToAirLongRange"
                    || CycleLoadout("zzz", names, 1) != "auto" || CycleLoadout("auto", null, 1) != "auto") return "cycle loadout";
                var lanes = LanesOf(gv, null);
                if (lanes.Count != 2) return "lanes count " + lanes.Count;
                if (LanesOf(new CapGroupView { N = 2 }, null).Count != 0) return "lanes without rect";

                // text: formats work in both languages, a missing key is visible as its id
                if (CapWindow.In("ru", () => Tx("cap.ui.cmd_err", "bad_roe")) != "команда отклонена: bad_roe" || CapWindow.In("en", () => Tx("cap.ui.cmd_err", "x")) != "command rejected: x") return "tx format";
                if (CapWindow.In("en", () => Tx("cap.nonexistent")) != UiText.DefaultSection + ".cap.nonexistent") return "tx unknown";
                // error codes of the controller/parser all have a text
                foreach (string code in new[] { "bad_rect", "no_origin", "bad_on_station", "bad_ready", "bad_roe", "bad_param", "unknown_group", "missing_uids", "no_aircraft", "anchor_lost" })
                    if (!UiText.Has(UiText.DefaultSection, "cap.ui.err." + code)) return "missing err text " + code;
                return null;
            }
        }
    }
}
