// T-148 C2: the draft of a CAP order (ADR-0049 decision Q3): N / K / ROE / zone / anchor / loadout are edited in a draft and go out only with
// "Start" / "Apply". Pure model: no Unity, no game. Texts are UiText keys cap.btn.* / cap.hint.* / cap.fld.* (lang/*/cap_window.ini).
// UiCmd and UiButtonState (shared with the ASW panel) live in UiOrderTypes.cs.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class CapOrderDraft
    {
        public string NText = "2", KText = "", Roe = "tight";
        public string ZoneSource;                 // null = the zone of the group | "rect" (drawn) | "polar" (from a ship)
        public double[] Zone;                     // rect: lat0, lon0, lat1, lon1
        public string BearingText = "0", DistText = "40", SizeText = "40", WidthText = "20";
        public string AltText = "0";              // T-166: patrol altitude of the group, ft (0 = the game's default)
        public int SeenSeq;                       // CapGroupView.OrderSeq the draft was made from
        public bool Anchor;
        public int AnchorUid;                     // ship the zone was anchored to by this window (0 = unknown); set by the body after an accepted order
        public Dictionary<string, string> Loadouts = new Dictionary<string, string>();   // type -> loadout ("auto" or a name)
        public string ForGroup;
        // T-179 / ADR-0079: the kind of the order (patrol | alert), the settings of the alert kind, "accept alert aircraft" of the patrol kind
        public string Kind = "patrol";
        public CapAlertSpec Alert = new CapAlertSpec();
        public bool AcceptAlert = true;
        public CapInterceptSpec Icpt = new CapInterceptSpec();   // T-333 (ADR-0129): the zone intercept settings of the patrol kind
    }

    internal static class CapOrderPanel
    {
        /// <summary>T-179: the [Cap] defaults of an alert group (AlertCount, AlertPerRaid, AlertLevel, AlertNm, ...) and of "accept alert aircraft"; set by CapFeature.</summary>
        public static CapAlertSpec DefaultAlertSpec = new CapAlertSpec();
        public static bool DefaultAcceptAlert = true;
        /// <summary>T-333: the [Cap] Intercept* defaults of a patrol group without an order of its own; set by CapFeature.</summary>
        public static CapInterceptSpec DefaultIcptSpec = new CapInterceptSpec();
        /// <summary>T-179 (ADR-0076 section 7.3): the last alert order of this window; the "Alert" mode of a group without one starts from it.</summary>
        public static CapAlertSpec LastAlertSpec;
        /// <summary>T-178 C1/C2/C4: the [Cap] defaults the lists and sliders mark (DefaultOnStation, PatrolAltFt, MissilePct, FuelMarginS, FuelFloorPct); set by CapFeature.</summary>
        public static int DefaultN = 2;
        public static double DefaultAltFt = 0, DefaultMissilePct = 0.5, DefaultFuelMarginS = 600, DefaultFuelFloorPct = 0, DefaultRingMarginNm = 5;   // T-330: DefaultRingMarginNm is the ring margin slider

        /// <summary>The group has an order: a zone (patrol) or the alert settings (alert kind has no zone).</summary>
        private static bool HasOrder(CapGroupView g) { return g != null && g.Ordered; }

        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        // ---- what differs from the group's order ---------------------------------------------------------------------------------------
        private sealed class Diff { public bool Kind, N, K, Roe, Zone, Anchor, Loadout, Alt, Accept, Alert, BadN, BadK, BadAlt, BadTargets, Icpt; }

        private static string KindOf(CapGroupView g) { return g != null && g.Ordered && g.Kind == "alert" ? "alert" : "patrol"; }

        /// <summary>Altitude of the field: empty = 0 (the game's default); null = not a whole number 0..60000.</summary>
        internal static int? AltOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int? v = CapWindowModel.Int(text);
            return v.HasValue && v.Value >= 0 && v.Value <= 60000 ? v : null;
        }

        private static string Name(CapGroupView g) { return g != null && g.Line != null ? g.Line.Group : null; }

        private static bool SameRect(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length < 4 || b.Length < 4) return false;
            for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - b[i]) > 1e-6) return false;
            return true;
        }

        private static bool HasZone(CapGroupView g) { return g.Rect != null && g.Rect.Length >= 4; }

        private static Diff Compare(CapGroupView g, CapOrderDraft d)
        {
            var f = new Diff();
            f.Kind = HasOrder(g) && d.Kind != KindOf(g);
            foreach (var kv in d.Loadouts)
                if (!string.Equals(kv.Value ?? "auto", CapWindowModel.LoadoutOf(g, kv.Key), StringComparison.OrdinalIgnoreCase)) f.Loadout = true;
            if (d.Kind == "alert")
            {
                f.BadTargets = d.Alert == null || d.Alert.Targets == AlertTargets.None;
                f.Alert = !(KindOf(g) == "alert" && g.AlertSpec != null && d.Alert != null && d.Alert.Same(g.AlertSpec));
                return f;
            }
            int? n = CapWindowModel.Int(d.NText);
            f.BadN = !n.HasValue || n.Value < 1;
            f.N = f.BadN || n.Value != g.N;
            int k = -1;
            if (!string.IsNullOrWhiteSpace(d.KText))
            {
                int? kv = CapWindowModel.Int(d.KText);
                if (!kv.HasValue || kv.Value < 0) f.BadK = true; else k = kv.Value;
            }
            f.K = f.BadK || k != g.K;
            int? alt = AltOf(d.AltText);
            f.BadAlt = !alt.HasValue;
            f.Alt = f.BadAlt || alt.Value != (int)g.AltFt;
            f.Roe = !string.Equals(d.Roe, string.IsNullOrEmpty(g.Roe) ? "tight" : g.Roe, StringComparison.Ordinal);
            f.Zone = d.ZoneSource != null && !(d.ZoneSource == "rect" && SameRect(d.Zone, g.Rect));
            f.Anchor = d.Anchor != (g.Anchor == "unit");
            f.Accept = d.AcceptAlert != g.AcceptAlert;
            f.Icpt = !SameIcpt(d.Icpt, g.Icpt ?? DefaultIcptSpec);
            return f;
        }

        internal static bool SameIcpt(CapInterceptSpec a, CapInterceptSpec b)
        {
            if (a == null || b == null) return a == b;
            return a.On == b.On && Math.Abs(a.MarginNm - b.MarginNm) < 1e-6 && a.PerTarget == b.PerTarget && a.Keep == b.Keep
                && string.Equals(a.Targets, b.Targets, StringComparison.Ordinal) && string.Equals(a.Speed, b.Speed, StringComparison.Ordinal);
        }

        /// <summary>The intercept fields of a patrol cap_order (CapCommands.ReadIcpt names).</summary>
        internal static void AddIcpt(Dictionary<string, object> o, CapInterceptSpec a)
        {
            if (o == null || a == null) return;
            var targets = new List<object>();
            foreach (string t in (a.Targets ?? "").Split(',')) { string x = t.Trim(); if (x.Length > 0) targets.Add(x); }
            o["intercept"] = a.On; o["icpt_margin_nm"] = a.MarginNm; o["per_target"] = (double)a.PerTarget; o["keep"] = (double)a.Keep;
            o["icpt_targets"] = targets; o["icpt_speed"] = a.Speed;
        }

        /// <summary>The draft of the selected group. The draft of the same group is kept as the user edits it; another group (or none yet) starts from the group's order.</summary>
        public static CapOrderDraft DraftFrom(CapGroupView g, CapOrderDraft keep)
        {
            string name = Name(g);
            if (name == null) return keep ?? new CapOrderDraft();
            if (keep != null && keep.ForGroup == name && keep.SeenSeq == g.OrderSeq) return keep;   // an order changed from outside (a dev command, a voice) -> re-read it
            var d = new CapOrderDraft { ForGroup = name, SeenSeq = g.OrderSeq };
            if (keep != null) { d.BearingText = keep.BearingText; d.DistText = keep.DistText; d.SizeText = keep.SizeText; d.WidthText = keep.WidthText; }
            d.AltText = ((int)g.AltFt).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (g.HasPolar)
            {
                d.BearingText = CapWindowModel.Fmt(g.PolarBearing); d.DistText = CapWindowModel.Fmt(g.PolarDistNm);
                d.SizeText = CapWindowModel.Fmt(g.PolarSizeNm); d.WidthText = CapWindowModel.Fmt(g.PolarWNm > 0 ? g.PolarWNm : g.PolarSizeNm);
            }
            else if (HasZone(g))
            {
                d.SizeText = CapWindowModel.Fmt((g.Rect[3] - g.Rect[1]) * 60.0 * AswMath.LonK(g.Rect[0], g.Rect[2])); d.WidthText = CapWindowModel.Fmt((g.Rect[2] - g.Rect[0]) * 60.0);
            }
            d.NText = (g.N > 0 ? g.N : DefaultN).ToString(System.Globalization.CultureInfo.InvariantCulture);
            d.KText = g.K >= 0 ? g.K.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            d.Roe = string.IsNullOrEmpty(g.Roe) ? "tight" : g.Roe;
            d.Anchor = g.Anchor == "unit";
            d.Kind = KindOf(g);
            d.AcceptAlert = HasOrder(g) ? g.AcceptAlert : DefaultAcceptAlert;
            d.Icpt = (g.Icpt ?? DefaultIcptSpec).Clone();
            d.Alert = KindOf(g) == "alert" && g.AlertSpec != null ? g.AlertSpec.Clone() : (LastAlertSpec ?? DefaultAlertSpec).Clone();
            if (KindOf(g) != "alert" || d.Alert.RefUid == 0) d.Alert.RefUid = CapWindowModel.OriginShip(g, 0);   // the default ship to protect: the deck of most members
            foreach (string type in CapWindowModel.TypesOf(g)) d.Loadouts[type] = CapWindowModel.LoadoutOf(g, type);
            return d;
        }

        /// <summary>The draft equals the group's order (the group has one). A group without an order never equals a draft.</summary>
        public static bool SameAsOrder(CapGroupView g, CapOrderDraft d)
        {
            if (g == null || d == null) return true;
            if (!HasOrder(g)) return false;
            var f = Compare(g, d);
            if (d.Kind == "alert") return !(f.Kind || f.Alert || f.Loadout);
            return !(f.Kind || f.N || f.K || f.Roe || f.Zone || f.Anchor || f.Loadout || f.Alt || f.Accept || f.Icpt);
        }

        /// <summary>"zone, ROE" for the hint "changed - press Apply".</summary>
        public static string DirtyFields(CapGroupView g, CapOrderDraft d)
        {
            if (g == null || d == null) return "";
            var f = Compare(g, d);
            var l = new List<string>();
            if (f.Kind) l.Add(T("cap.fld.mode"));
            if (d.Kind == "alert")
            {
                var o = KindOf(g) == "alert" ? g.AlertSpec : null; var a = d.Alert;
                if (f.Alert && a != null)
                {
                    if (o == null) l.Add(T("cap.fld.alert_settings"));
                    else
                    {
                        if (a.RefUid != o.RefUid) l.Add(T("cap.fld.protect"));
                        if (a.Ready != o.Ready) l.Add(T("cap.fld.ready_n"));
                        if (a.PerRaid != o.PerRaid) l.Add(T("cap.fld.per_raid"));
                        if (a.Level != o.Level) l.Add(T("cap.fld.readiness"));
                        if (Math.Abs(a.RadiusNm - o.RadiusNm) > 1e-6) l.Add(T("cap.fld.radius"));
                        if (a.Targets != o.Targets) l.Add(T("cap.fld.targets"));
                        if (Math.Abs(a.PursuitNm - o.PursuitNm) > 1e-6) l.Add(T("cap.fld.leash"));
                        if (a.Speed != o.Speed) l.Add(T("cap.fld.dash"));
                        if (a.Roe != o.Roe) l.Add(T("cap.fld.roe"));
                        if (a.After != o.After) l.Add(T("cap.fld.after"));
                        if (a.Fill != o.Fill) l.Add(T("cap.fld.fill"));
                    }
                }
                if (f.Loadout) l.Add(T("cap.fld.loadout"));
                return string.Join(", ", l);
            }
            if (f.N) l.Add(T("cap.fld.n"));
            if (f.K) l.Add(T("cap.fld.k"));
            if (f.Roe) l.Add(T("cap.fld.roe"));
            if (f.Zone) l.Add(T("cap.fld.zone"));
            if (f.Anchor) l.Add(T("cap.fld.anchor"));
            if (f.Loadout) l.Add(T("cap.fld.loadout"));
            if (f.Alt) l.Add(T("cap.fld.alt"));
            if (f.Accept) l.Add(T("cap.fld.accept_alert"));
            if (f.Icpt) l.Add(T("cap.fld.intercept"));
            return string.Join(", ", l);
        }

        private static string HintText(string code, CapGroupView g, CapOrderDraft d)
        {
            switch (code)
            {
                case "need_group": return T("cap.hint.need_group");
                case "need_zone": return T("cap.hint.need_zone");
                case "bad_ready": return T("cap.hint.bad_ready");
                case "bad_number": return T("cap.hint.bad_number");
                case "bad_targets": return T("cap.hint.bad_targets");
                case "dirty": return T("cap.hint.dirty", DirtyFields(g, d));
                default: return null;
            }
        }

        /// <summary>need_group | need_zone | bad_ready | bad_number | dirty | null - the first reason that matters for the main button.</summary>
        public static string Hint(CapGroupView g, CapOrderDraft d, int selectedShip)
        {
            if (g == null || g.Line == null || d == null) return "need_group";
            var f = Compare(g, d);
            if (d.Kind == "alert") { if (f.BadTargets) return "bad_targets"; }
            else
            {
                if (f.BadK) return "bad_ready";
                if (f.BadN || f.BadAlt) return "bad_number";
                if (d.ZoneSource == null && !HasZone(g)) return "need_zone";
            }
            if (HasOrder(g) && !SameAsOrder(g, d)) return "dirty";
            return null;
        }

        /// <summary>The main button: "start" for a group without an order, "apply" for one that has it. Grey: no group / no zone / bad number / the draft equals the order.</summary>
        public static UiButtonState Action(CapGroupView g, CapOrderDraft d)
        {
            var s = new UiButtonState { Id = HasOrder(g) ? "apply" : "start" };
            s.Label = T(s.Id == "start" ? (d != null && d.Kind == "alert" ? "cap.btn.start_alert" : "cap.btn.start") : "cap.btn.apply");   // T-197
            string code = Hint(g, d, 0);
            if (code == null || code == "dirty")
            {
                bool same = HasOrder(g) && SameAsOrder(g, d);
                s.Enabled = !same;
                s.Tip = same ? T("cap.hint.same") : code == "dirty" ? HintText(code, g, d) : null;
                return s;
            }
            s.Enabled = false;
            s.Tip = HintText(code, g, d);
            return s;
        }

        public static string HintTextFor(string code, CapGroupView g, CapOrderDraft d) { return HintText(code, g, d); }

        /// <summary>The commands of "Start" / "Apply": cap_loadout for every changed type first (the re-plan then takes the new loadout), then cap_order when anything of the order changed
        /// (or the group has none). err: need_group | need_zone | bad_ready | bad_number | no_origin.</summary>
        public static List<UiCmd> Commands(CapGroupView g, CapOrderDraft d, int selectedShip, GeoPt? origin, out string err)
        {
            err = null;
            var cmds = new List<UiCmd>();
            string group = Name(g);
            if (group == null || d == null) { err = "need_group"; return cmds; }
            var f = Compare(g, d);
            if (d.Kind == "alert") { if (f.BadTargets) { err = "bad_targets"; return cmds; } }
            else
            {
                if (f.BadK) { err = "bad_ready"; return cmds; }
                if (f.BadN || f.BadAlt) { err = "bad_number"; return cmds; }
            }
            var types = CapWindowModel.TypesOf(g);
            foreach (string t in d.Loadouts.Keys) if (!types.Contains(t)) types.Add(t);
            var changedLo = new Dictionary<string, string>();
            foreach (string type in types)
            {
                string want;
                if (!d.Loadouts.TryGetValue(type, out want)) continue;
                if (!string.Equals(want ?? "auto", CapWindowModel.LoadoutOf(g, type), StringComparison.OrdinalIgnoreCase)) changedLo[type] = want ?? "auto";
            }
            bool orderChanged = d.Kind == "alert" ? (f.Kind || f.Alert) : (f.Kind || f.N || f.K || f.Roe || f.Zone || f.Anchor || f.Alt || f.Accept || f.Icpt);
            if (HasOrder(g) && !orderChanged)
            {
                // only loadouts changed on a running group: the change on the fly (cap_loadout), as before
                foreach (var kv in changedLo) cmds.Add(new UiCmd { Name = "cap_loadout", Args = CapWindowModel.LoadoutCmd(group, kv.Key, kv.Value) });
                return cmds;
            }
            Dictionary<string, object> order;
            if (d.Kind == "alert") order = AlertOrder(group, d.Alert);
            else
            {
                bool zoneless = d.ZoneSource == null && !HasZone(g);
                if (zoneless) { err = "need_zone"; return new List<UiCmd>(); }
                int altFt = AltOf(d.AltText).Value;
                int n = CapWindowModel.Int(d.NText).Value;
                int k = string.IsNullOrWhiteSpace(d.KText) ? -1 : CapWindowModel.Int(d.KText).Value;
                int anchorUid = d.Anchor ? (selectedShip != 0 ? selectedShip : d.AnchorUid) : 0;
                if (d.ZoneSource == "polar")
                {
                    double? b = CapWindowModel.Num(d.BearingText), dist = CapWindowModel.Num(d.DistText), size = CapWindowModel.Num(d.SizeText), width = CapWindowModel.Num(d.WidthText);
                    if (!b.HasValue || !dist.HasValue || !size.HasValue) { err = "bad_number"; return new List<UiCmd>(); }
                    if (selectedShip == 0 && !origin.HasValue) { err = "no_origin"; return new List<UiCmd>(); }
                    order = CapWindowModel.OrderPolar(group, b.Value, dist.Value, size.Value, width ?? 0, selectedShip, selectedShip == 0 ? origin : null, n, k, d.Roe, anchorUid, altFt);
                }
                else
                {
                    double[] zone = d.ZoneSource == "rect" ? d.Zone : g.Rect;
                    if (zone == null || zone.Length < 4) { err = "need_zone"; return new List<UiCmd>(); }
                    order = CapWindowModel.OrderRect(group, zone, n, k, d.Roe, anchorUid, altFt);
                }
                if (order == null) { err = "need_zone"; return new List<UiCmd>(); }
                order["kind"] = "patrol";
                order["accept_alert"] = d.AcceptAlert;
                AddIcpt(order, d.Icpt);   // T-333
            }
            // ADR-0075 item 8: one order carries the loadouts (no separate cap_loadout before the first deck request)
            if (changedLo.Count > 0) { var lo = new Dictionary<string, object>(); foreach (var kv in changedLo) lo[kv.Key] = kv.Value; order["loadouts"] = lo; }
            cmds.Add(new UiCmd { Name = "cap_order", Args = order });
            return cmds;
        }

        /// <summary>The order of the alert kind (ADR-0079 section 9): every setting of the draft, one command.</summary>
        internal static Dictionary<string, object> AlertOrder(string group, CapAlertSpec a)
        {
            var targets = new List<object>();
            string csv = CapAlertMath.TargetsCsv(a.Targets);
            if (csv.Length > 0) foreach (string t in csv.Split(',')) targets.Add(t);
            var o = new Dictionary<string, object>
            {
                { "group", group }, { "kind", "alert" }, { "ready", (double)a.Ready }, { "per_raid", (double)a.PerRaid }, { "level", a.Level }, { "radius_nm", a.RadiusNm },
                { "targets", targets }, { "pursuit_nm", a.PursuitNm }, { "speed", a.Speed }, { "roe", a.Roe }, { "after", a.After }, { "fill", a.Fill },
            };
            if (a.RefUid != 0) o["ref_uid"] = (double)a.RefUid;
            return o;
        }

        /// <summary>"Stop CAP-1": grey without a group or when the group has no order.</summary>
        public static UiButtonState Stop(CapGroupView g)
        {
            var s = new UiButtonState { Id = "stop" };
            string name = Name(g);
            s.Label = name == null ? T("cap.btn.stop") : T("cap.btn.stop_group", name);
            s.Enabled = name != null && HasOrder(g);
            s.Tip = name == null ? T("cap.hint.need_group") : HasOrder(g) ? null : T("cap.hint.not_ordered");
            return s;
        }

        // ---- selftest ------------------------------------------------------------------------------------------------------------------
        private static CapGroupView Grp(string name, bool ordered, double[] rect, int n, int k, string roe, string anchor, params string[] types)
        {
            var g = new CapGroupView { Line = new CapGroupLine { Group = name }, Ordered = ordered, Rect = rect, N = n, K = k, Roe = roe, Anchor = anchor };
            int uid = 100;
            foreach (string t in types) g.Members.Add(new CapMemberView { Uid = uid++, Name = t + uid, Type = t });
            return g;
        }

        public static string SelfTest()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string r = Cases();
                    if (r != null) return lang + ": " + r;
                }
            return null;
        }

        private static string Cases()
        {
            string err;
            var zone = new[] { 35.0, 20.0, 35.5, 20.5 };
            var zone2 = new[] { 36.0, 21.0, 36.5, 21.5 };
            // DraftFrom
            var g1 = Grp("G1", true, zone, 3, 1, "free", "geo", "F-14A", "F-14A", "E-2C");
            g1.Loadouts["F-14A"] = "AAM8";
            var d = DraftFrom(g1, null);
            if (d.NText != "3" || d.KText != "1" || d.Roe != "free" || d.Anchor || d.ZoneSource != null || d.ForGroup != "G1") return "draft fields";
            if (d.Loadouts["F-14A"] != "AAM8" || d.Loadouts["E-2C"] != "auto" || d.Loadouts.Count != 2) return "draft loadouts";
            d.ZoneSource = "rect"; d.Zone = zone2; d.BearingText = "77";
            if (DraftFrom(g1, d) != d) return "same group keeps the draft";
            var g2 = Grp("G2", false, null, 0, -1, null, "geo", "F-4");
            var d2 = DraftFrom(g2, d);
            if (d2.ForGroup != "G2" || d2.ZoneSource != null || d2.Zone != null || d2.NText != "2" || d2.KText != "" || d2.Roe != "tight") return "another group resets the zone";
            if (d2.BearingText != "77") return "polar fields survive the group change";
            if (DraftFrom(null, d2) != d2) return "no group keeps the draft";

            // SameAsOrder
            d = DraftFrom(g1, null);
            if (!SameAsOrder(g1, d)) return "fresh draft equals the order";
            d.Roe = "hold"; if (SameAsOrder(g1, d)) return "roe differs"; d.Roe = "free";
            d.Loadouts["F-14A"] = "AAM6"; if (SameAsOrder(g1, d)) return "loadout differs"; d.Loadouts["F-14A"] = "AAM8";
            d.NText = "4"; if (SameAsOrder(g1, d)) return "n differs"; d.NText = "3";
            d.KText = ""; if (SameAsOrder(g1, d)) return "k differs"; d.KText = "1";
            d.Anchor = true; if (SameAsOrder(g1, d)) return "anchor differs"; d.Anchor = false;
            d.ZoneSource = "rect"; d.Zone = (double[])zone.Clone(); if (!SameAsOrder(g1, d)) return "same rect is the same";
            d.Zone = zone2; if (SameAsOrder(g1, d)) return "other rect differs";
            if (SameAsOrder(g2, DraftFrom(g2, null))) return "a group without an order is never the same";
            if (DirtyFields(g1, d) != UiText.Get("cap.fld.zone")) return "dirty fields: " + DirtyFields(g1, d);
            d.Roe = "hold";
            if (!DirtyFields(g1, d).Contains(UiText.Get("cap.fld.roe")) || !DirtyFields(g1, d).Contains(UiText.Get("cap.fld.zone"))) return "dirty fields list";

            // Action / Hint
            var a = Action(null, new CapOrderDraft());
            if (a.Enabled || a.Id != "start" || Hint(null, new CapOrderDraft(), 0) != "need_group" || a.Tip != UiText.Get("cap.hint.need_group")) return "action: no group";
            var dn = DraftFrom(g2, null);
            a = Action(g2, dn);
            if (a.Enabled || Hint(g2, dn, 0) != "need_zone" || a.Tip != UiText.Get("cap.hint.need_zone")) return "action: no zone";
            dn.ZoneSource = "rect"; dn.Zone = zone;
            a = Action(g2, dn);
            if (!a.Enabled || a.Id != "start" || a.Label != UiText.Get("cap.btn.start") || Hint(g2, dn, 0) != null) return "action: start with a drawn zone";
            dn.ZoneSource = "polar";
            if (!Action(g2, dn).Enabled) return "action: start with a polar zone";
            dn.Kind = "alert";   // T-197: the first order of the alert kind has its own label
            if (Action(g2, dn).Label != UiText.Get("cap.btn.start_alert") || UiText.Get("cap.btn.start_alert") == UiText.Get("cap.btn.start") || Action(g2, dn).Id != "start") return "action: start label of the alert kind";
            dn.Kind = "patrol";
            d = DraftFrom(g1, null);
            a = Action(g1, d);
            if (a.Enabled || a.Id != "apply" || a.Label != UiText.Get("cap.btn.apply") || a.Tip != UiText.Get("cap.hint.same") || Hint(g1, d, 0) != null) return "action: apply grey when the draft equals the order";
            d.Roe = "hold";
            a = Action(g1, d);
            if (!a.Enabled || Hint(g1, d, 0) != "dirty" || a.Tip == null || !a.Tip.Contains(UiText.Get("cap.fld.roe"))) return "action: apply after a change";
            d = DraftFrom(g1, null); d.KText = "abc";
            a = Action(g1, d);
            if (a.Enabled || Hint(g1, d, 0) != "bad_ready" || a.Tip != UiText.Get("cap.hint.bad_ready")) return "action: bad K";
            d = DraftFrom(g1, null); d.NText = "0";
            if (Action(g1, d).Enabled || Hint(g1, d, 0) != "bad_number") return "action: bad N";

            // Commands
            d = DraftFrom(g1, null); d.Roe = "hold";
            var c = Commands(g1, d, 0, null, out err);
            if (err != null || c.Count != 1 || c[0].Name != "cap_order") return "commands roe only: " + err + " " + c.Count;
            var po = CapCommands.ParseOrder(c[0].Args, out err);
            if (po == null || po.Roe != "hold" || po.OnStation != 3 || po.Ready != 1 || po.Zone.Rect == null || !SameRect(po.Zone.Rect, zone) || po.Zone.Anchor != "geo") return "commands roe only parse: " + err;
            d = DraftFrom(g1, null); d.Loadouts["F-14A"] = "AAM6";
            c = Commands(g1, d, 0, null, out err);
            if (err != null || c.Count != 1 || c[0].Name != "cap_loadout") return "commands loadout only";
            string cg, ct, cl;
            if (!CapCommands.ParseLoadout(c[0].Args, out cg, out ct, out cl, out err) || cg != "G1" || ct != "F-14A" || cl != "AAM6") return "commands loadout parse: " + err;
            d.NText = "4";
            c = Commands(g1, d, 0, null, out err);
            if (err != null || c.Count != 1 || c[0].Name != "cap_order") return "commands loadout + n order: one order";
            var pl = CapCommands.ParseOrder(c[0].Args, out err);
            if (pl == null || pl.OnStation != 4 || pl.Loadouts == null || pl.Loadouts["F-14A"] != "AAM6") return "commands n value + loadouts in the order " + err;
            d = DraftFrom(g1, null);
            c = Commands(g1, d, 0, null, out err);
            if (err != null || c.Count != 0) return "commands: nothing changed on an ordered group";
            d = DraftFrom(g2, null); d.ZoneSource = "rect"; d.Zone = zone2; d.Roe = "free";
            c = Commands(g2, d, 0, null, out err);
            if (err != null || c.Count != 1 || c[0].Name != "cap_order" || !SameRect(CapCommands.ParseOrder(c[0].Args, out err).Zone.Rect, zone2)) return "commands start with a drawn zone";
            d = DraftFrom(g2, null);
            Commands(g2, d, 0, null, out err);
            if (err != "need_zone") return "commands start without a zone: " + err;
            d = DraftFrom(g1, null); d.ZoneSource = "polar"; d.BearingText = "90"; d.DistText = "40"; d.SizeText = "30"; d.WidthText = "20";
            Commands(g1, d, 0, null, out err);
            if (err != "no_origin") return "commands polar without origin: " + err;
            c = Commands(g1, d, 0, new GeoPt(35, 20), out err);
            var pp = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pp == null || !pp.Polar || pp.OriginUid != 0 || !pp.Origin.HasValue || pp.Bearing != 90 || pp.DistNm != 40 || pp.SizeNm != 30 || pp.WNm != 20) return "commands polar from a point: " + err;
            c = Commands(g1, d, 9, null, out err);
            pp = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pp == null || !pp.Polar || pp.OriginUid != 9 || pp.Zone.Anchor != "geo") return "commands polar from a ship: " + err;
            d.Anchor = true;
            c = Commands(g1, d, 9, null, out err);
            pp = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pp == null || pp.Zone.Anchor != "unit" || pp.Zone.AnchorUid != 9) return "commands polar anchored: " + err;
            d.BearingText = "x";
            Commands(g1, d, 9, null, out err);
            if (err != "bad_number") return "commands polar bad number: " + err;
            d = DraftFrom(g1, null); d.KText = "abc";
            Commands(g1, d, 0, null, out err);
            if (err != "bad_ready") return "commands bad K";
            Commands(null, d, 0, null, out err);
            if (err != "need_group") return "commands no group";

            // T-166: altitude and the fields of the current order
            var g3 = Grp("G3", true, zone, 2, -1, "tight", "geo", "F-14A");
            g3.AltFt = 12000; g3.HasPolar = true; g3.PolarBearing = 300; g3.PolarDistNm = 40; g3.PolarSizeNm = 30; g3.PolarWNm = 20; g3.OrderSeq = 1;
            d = DraftFrom(g3, null);
            if (d.AltText != "12000" || d.BearingText != "300" || d.DistText != "40" || d.SizeText != "30" || d.WidthText != "20") return "draft shows the polar order";
            if (!SameAsOrder(g3, d)) return "alt draft equals the order";
            d.AltText = "9000"; if (SameAsOrder(g3, d) || !DirtyFields(g3, d).Contains(UiText.Get("cap.fld.alt"))) return "alt differs";
            c = Commands(g3, d, 0, null, out err);
            if (err != null || c.Count != 1 || CapCommands.ParseOrder(c[0].Args, out err).AltFt != 9000) return "commands alt only: " + err;
            d.AltText = "x"; Commands(g3, d, 0, null, out err); if (err != "bad_number" || Hint(g3, d, 0) != "bad_number") return "bad alt";
            d.AltText = ""; if (AltOf(d.AltText) != 0) return "empty alt is auto";
            g3.OrderSeq = 2; g3.PolarBearing = 45;
            if (DraftFrom(g3, DraftFrom(g3, null) ).BearingText != "45") return "same draft";
            var stale = DraftFrom(g3, null); g3.OrderSeq = 3; g3.PolarBearing = 120; g3.AltFt = 5000;
            var fresh = DraftFrom(g3, stale);
            if (fresh == stale || fresh.BearingText != "120" || fresh.AltText != "5000") return "an outside order change refreshes the fields";
            var g4 = Grp("G4", true, zone, 2, -1, "tight", "geo", "F-4");   // rect order: size from the rect
            var d4 = DraftFrom(g4, null);
            if (d4.SizeText != CapWindowModel.Fmt(30) || d4.WidthText != CapWindowModel.Fmt(30)) return "rect order size: " + d4.SizeText + "x" + d4.WidthText;

            // T-179 / ADR-0079: the kind of the order, the alert settings, one command for a change of the kind
            d = DraftFrom(g1, null);
            if (d.Kind != "patrol" || !d.AcceptAlert || !SameAsOrder(g1, d)) return "kind draft default";
            d.AcceptAlert = false;
            if (SameAsOrder(g1, d) || !DirtyFields(g1, d).Contains(UiText.Get("cap.fld.accept_alert"))) return "accept_alert differs";
            c = Commands(g1, d, 0, null, out err);
            var pk = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pk == null || pk.Kind != "patrol" || pk.AcceptAlert != 0 || pk.Zone.Rect == null) return "accept_alert order: " + err;
            d = DraftFrom(g1, null); d.Kind = "alert";
            d.Alert.RefUid = 5; d.Alert.Ready = 3; d.Alert.Targets = AlertTargets.Air | AlertTargets.Helo; d.Alert.After = "alert";
            if (SameAsOrder(g1, d) || !DirtyFields(g1, d).Contains(UiText.Get("cap.fld.mode"))) return "kind differs";
            a = Action(g1, d);
            if (!a.Enabled || a.Id != "apply" || Hint(g1, d, 0) != "dirty") return "kind change: apply";
            c = Commands(g1, d, 9, null, out err);
            var pa = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pa == null || pa.Kind != "alert" || pa.Zone.Rect != null || pa.AlertSpec.Ready != 3 || pa.AlertSpec.RefUid != 5 || pa.AlertSpec.After != "alert" || pa.AlertSpec.Targets != (AlertTargets.Air | AlertTargets.Helo)
                || pa.AlertGiven.Count != 11) return "kind change is one cap_order kind=alert " + err + " " + c.Count;
            d.Alert.Targets = AlertTargets.None;
            if (Hint(g1, d, 0) != "bad_targets" || Action(g1, d).Enabled || Action(g1, d).Tip != UiText.Get("cap.hint.bad_targets")) return "targets never empty";
            Commands(g1, d, 0, null, out err);
            if (err != "bad_targets") return "commands bad targets: " + err;
            // a group of the alert kind: the draft takes its settings, "Changed" shows what differs
            var ga = Grp("A1", true, null, 2, -1, "tight", "geo", "F-14A", "F-14A");
            ga.Kind = "alert"; ga.AlertSpec = new CapAlertSpec { RefUid = 7, Ready = 2, PerRaid = 1, Level = "hangar", RadiusNm = 100, PursuitNm = 80, Speed = "cruise", Roe = "hold", After = "alert", Fill = true, Targets = AlertTargets.Air | AlertTargets.Unknown };
            d = DraftFrom(ga, null);
            if (d.Kind != "alert" || !d.Alert.Same(ga.AlertSpec) || !SameAsOrder(ga, d)) return "alert draft from the group";
            a = Action(ga, d);
            if (a.Enabled || a.Id != "apply" || a.Tip != UiText.Get("cap.hint.same")) return "alert: apply grey when equal";
            if (!Stop(ga).Enabled) return "alert group has an order: stop";
            d.Alert.Level = "deck"; d.Alert.RadiusNm = 80;
            string dirty = DirtyFields(ga, d);
            if (!dirty.Contains(UiText.Get("cap.fld.readiness")) || !dirty.Contains(UiText.Get("cap.fld.radius")) || dirty.Contains(UiText.Get("cap.fld.mode"))) return "alert dirty fields: " + dirty;
            c = Commands(ga, d, 0, null, out err);
            pa = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pa == null || pa.Kind != "alert" || pa.AlertSpec.Level != "deck" || pa.AlertSpec.RadiusNm != 80 || pa.AlertSpec.Roe != "hold") return "alert apply order " + err;
            d = DraftFrom(ga, null); d.Loadouts["F-14A"] = "AAM6";
            c = Commands(ga, d, 0, null, out err);
            if (c.Count != 1 || c[0].Name != "cap_loadout") return "alert: only a loadout changed -> cap_loadout";
            // alert -> patrol: one command, the zone is needed
            d = DraftFrom(ga, null); d.Kind = "patrol";
            Commands(ga, d, 0, null, out err);
            if (err != "need_zone" || Hint(ga, d, 0) != "need_zone") return "alert -> patrol needs a zone " + err;
            d.ZoneSource = "rect"; d.Zone = zone2;
            c = Commands(ga, d, 0, null, out err);
            pk = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pk == null || pk.Kind != "patrol" || !SameRect(pk.Zone.Rect, zone2)) return "alert -> patrol order " + err;
            // the memory of the last alert order starts the alert mode of a group without one
            var keepLast = LastAlertSpec;
            LastAlertSpec = new CapAlertSpec { Ready = 4, RadiusNm = 150 };
            d = DraftFrom(g2, null); d.Kind = "alert";
            if (d.Alert.Ready != 4 || d.Alert.RadiusNm != 150) { LastAlertSpec = keepLast; return "last alert order is the start"; }
            LastAlertSpec = null;
            d = DraftFrom(g2, null);
            if (d.Alert.Ready != DefaultAlertSpec.Ready) { LastAlertSpec = keepLast; return "[Cap] defaults without a last order"; }
            LastAlertSpec = keepLast;
            // the first order of a group: the alert kind needs no zone
            d = DraftFrom(g2, null); d.Kind = "alert";
            a = Action(g2, d);
            if (!a.Enabled || a.Id != "start" || Hint(g2, d, 0) != null) return "alert kind: start without a zone";
            c = Commands(g2, d, 0, null, out err);
            pa = c.Count == 1 ? CapCommands.ParseOrder(c[0].Args, out err) : null;
            if (pa == null || pa.Kind != "alert") return "alert kind: first order " + err;
            // the default ship to protect: the deck of most members
            var gd = Grp("D", false, null, 2, -1, "tight", "geo", "F-14A", "F-14A");
            foreach (var mm in gd.Members) mm.ShipUid = 77;
            if (DraftFrom(gd, null).Alert.RefUid != 77) return "default protected ship = the deck of the members";

            // Stop
            var s = Stop(null);
            if (s.Enabled || s.Id != "stop" || s.Label != UiText.Get("cap.btn.stop")) return "stop: no group";
            s = Stop(g2);
            if (s.Enabled || !s.Label.Contains("G2") || s.Tip != UiText.Get("cap.hint.not_ordered")) return "stop: no order";
            s = Stop(g1);
            if (!s.Enabled || !s.Label.Contains("G1") || s.Tip != null) return "stop: ordered";
            return null;
        }
    }
}
