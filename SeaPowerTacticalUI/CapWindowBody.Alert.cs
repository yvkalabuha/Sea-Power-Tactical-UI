// T-179 / ADR-0079: the "Alert" mode of the CAP window - the mode row, the fields of the alert kind (the visible ones and the folded "More"), the ship to protect
// (list + "On the map", ADR-0077 section 3) and the "Auto (fastest)" loadout text. Pure: no Unity, no game; the mouse on the map is CapWindowGui.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed partial class CapWindowBody
    {
        // ---------------------------------------------------------------- the mode row

        private void ModeRow(UiKit k, CapGroupView sel)
        {
            var d = _draft;
            int cur = d.Kind == "alert" ? 1 : 0;
            k.Row(Tx("cap.lbl.mode"));
            int now = k.Radio("cap.mode", new[] { Tx("cap.mode.patrol"), Tx("cap.mode.alert") }, cur, new[] { Tx("cap.tip.mode_patrol"), Tx("cap.tip.mode_alert") });
            if (now < 0 || now == cur) return;
            d.Kind = now == 1 ? "alert" : "patrol";
            if (_drawing) { _drawing = false; }
            if (_pick.Active) CancelShipPick("mode");
            _env.Log("cap window press mode." + now + " -> " + d.Kind + " group=" + GroupName(sel));
        }

        // ---------------------------------------------------------------- ship to protect (map pick)

        private readonly UiMapPick _pick = new UiMapPick();
        /// <summary>The ships of the running map pick and their lat/lon (parallel lists), for the Gui.</summary>
        internal List<ShipItem> PickItems = new List<ShipItem>();
        internal List<double[]> PickGeo = new List<double[]>();
        internal UiMapPick MapPick { get { return _pick; } }
        /// <summary>The window waits for a click on an own ship on the map.</summary>
        public bool WantShipPick { get { return _pick.Active; } }

        internal void CancelShipPick(string reason)
        {
            if (!_pick.Active) return;
            _pick.Cancel(reason);
            Say(Tx("cap.say.pick_cancelled"), UiTone.Dim);
        }

        /// <summary>The left click of the map pick landed on an own ship.</summary>
        public void OnShipPicked(int uid)
        {
            if (uid == 0) return;
            string name = null;
            foreach (var it in PickItems) if (it.Uid == uid) name = it.Name;
            if (_draft != null && _draft.Alert != null) _draft.Alert.RefUid = uid;
            Say(Tx("cap.say.protect_set", name ?? uid.ToString(CultureInfo.InvariantCulture)), UiTone.Ok);
            _env.Log("cap window pick protect uid=" + uid);
        }

        // ---------------------------------------------------------------- fields of the alert kind

        private static readonly double[] RadiusList = { 40, 60, 80, 100, 120, 150 };
        private static readonly double[] LeashList = { 30, 40, 60, 80, 100 };

        private void AlertRows(UiKit k, CapGroupView sel, CapOrderDraft d)
        {
            var a = d.Alert;
            var dflt = CapOrderPanel.DefaultAlertSpec;

            // the ship to protect
            k.Row(Tx("cap.lbl.protect"), Tx("cap.tip.protect"));
            var geo = new List<double[]>();
            var items = _env.OwnShips(geo) ?? new List<ShipItem>();
            bool map;
            int uid = k.ShipPick("cap.protect", items, a.RefUid, Tx("cap.tip.protect"), out map);
            if (uid != a.RefUid) a.RefUid = uid;
            if (map)
            {
                PickItems = items; PickGeo = geo; _pick.Start("cap"); _drawing = false;
                Say(Tx("ui.ship.hint"), UiTone.Accent);
                _env.Log("cap window press cap.protect.map -> pick on the map");
            }
            if (_pick.Active)
            {
                PickItems = items; PickGeo = geo;
                if (k.Button("cap.protect.cancel", Tx("ui.map.cancel"), Tx("ui.map.cancel.tip"))) CancelShipPick("button");   // i18n-allow: control id
            }

            // ready machines and how many go to a raid
            k.Row(Tx("cap.lbl.ready_n"));
            a.Ready = (int)PickNum(k, "cap.alert.ready", Range(1, 4), a.Ready, v => Inv(v), Tx("cap.tip.ready_n"), Math.Max(1, Math.Min(4, dflt.Ready)));
            k.Unit(Tx("cap.lbl.per_raid"));
            var vals = new List<int> { 1, 2 };
            if (a.PerRaid > 2) vals.Add(a.PerRaid);
            vals.Add(0);
            var labels = new List<string>(); var tips = new List<string>();
            foreach (int v in vals) { labels.Add(v == 0 ? Tx("cap.val.per_raid_all") : v.ToString(CultureInfo.InvariantCulture)); tips.Add(Tx("cap.tip.per_raid")); }
            int pi = Math.Max(0, vals.IndexOf(a.PerRaid));
            a.PerRaid = vals[k.Radio("cap.alert.per_raid", labels, pi, tips)];

            // the radius of the reaction
            k.Row(Tx("cap.lbl.radius"));
            a.RadiusNm = PickNum(k, "cap.alert.radius", RadiusList, a.RadiusNm, NmText, Tx("cap.tip.radius"), dflt.RadiusNm, 2);

            // what to do after the raid
            k.Row(Tx("cap.lbl.after"));
            int ai = a.After == "alert" ? 0 : 1;
            int an = k.Radio("cap.alert.after", new[] { Tx("cap.val.after_alert"), Tx("cap.val.after_patrol") }, ai, new[] { Tx("cap.tip.after_alert"), Tx("cap.tip.after_patrol") });
            a.After = an == 0 ? "alert" : "patrol";

            if (sel != null && sel.Line != null && sel.Line.Warns != null && sel.Line.Warns.Contains("alert_unavailable")) k.Text(Tx("cap.ui.alert_unavailable"), UiTone.Warn, true, 1);

            // the folded "More": readiness, targets, leash, dash speed, ROE, fill from the deck
            if (k.Section("alert_more", Tx("cap.sec.more"), null, false, AlertSummary(a)))
            {
                k.LabelColumn(Tx("cap.lbl.readiness"), Tx("cap.lbl.targets"), Tx("cap.lbl.leash"), Tx("cap.lbl.dash"), Tx("cap.ui.roe"));
                k.Row(Tx("cap.lbl.readiness"));
                int li = a.Level == "hangar" ? 1 : 0;
                int ln = k.Radio("cap.alert.level", new[] { Tx("cap.val.level_deck"), Tx("cap.val.level_hangar") }, li, new[] { Tx("cap.tip.level_deck"), Tx("cap.tip.level_hangar") });
                a.Level = ln == 1 ? "hangar" : "deck";

                k.Row(Tx("cap.lbl.targets"));
                var on = new[] { (a.Targets & AlertTargets.Air) != 0, (a.Targets & AlertTargets.Helo) != 0, (a.Targets & AlertTargets.Unknown) != 0 };
                if (k.Toggles("cap.alert.targets", new[] { Tx("cap.val.target_air"), Tx("cap.val.target_helo"), Tx("cap.val.target_unknown") }, on,
                              new[] { Tx("cap.tip.target_air"), Tx("cap.tip.target_helo"), Tx("cap.tip.target_unknown") }))
                    a.Targets = (on[0] ? AlertTargets.Air : 0) | (on[1] ? AlertTargets.Helo : 0) | (on[2] ? AlertTargets.Unknown : 0);

                k.Row(Tx("cap.lbl.leash"));
                a.PursuitNm = PickNum(k, "cap.alert.leash", LeashList, a.PursuitNm, NmText, Tx("cap.tip.leash"), dflt.PursuitNm, 2);

                k.Row(Tx("cap.lbl.dash"));
                int si = a.Speed == "cruise" ? 1 : 0;
                int sn = k.Radio("cap.alert.speed", new[] { Tx("cap.val.dash_max"), Tx("cap.val.dash_cruise") }, si, new[] { Tx("cap.tip.dash_max"), Tx("cap.tip.dash_cruise") });
                a.Speed = sn == 1 ? "cruise" : "max";

                RoeRow(k, sel, a.Roe, r => a.Roe = r);

                k.Row(null);
                a.Fill = k.Check("cap.alert.fill", a.Fill, Tx("cap.lbl.fill"), Tx("cap.tip.fill"));
            }
        }

        /// <summary>"on the deck · aircraft · 60 miles · max. · tight": the summary of the folded "More".</summary>
        internal static string AlertSummary(CapAlertSpec a)
        {
            var parts = new List<string>
            {
                Tx(a.Level == "hangar" ? "cap.val.level_hangar" : "cap.val.level_deck"),
                TargetsText(a.Targets),
                NmText(a.PursuitNm),
                Tx(a.Speed == "cruise" ? "cap.val.dash_cruise_short" : "cap.val.dash_max_short"),
                a.Roe,
            };
            return string.Join(" · ", parts.ToArray());
        }

        private static string TargetsText(AlertTargets t)
        {
            var l = new List<string>();
            if ((t & AlertTargets.Air) != 0) l.Add(Tx("cap.val.target_air"));
            if ((t & AlertTargets.Helo) != 0) l.Add(Tx("cap.val.target_helo"));
            if ((t & AlertTargets.Unknown) != 0) l.Add(Tx("cap.val.target_unknown"));
            return string.Join("+", l.ToArray()).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- the loadout "Auto" of an alert group = the fastest one

        /// <summary>"Auto (fastest: AirToAir · ~15 min)": for the alert kind "auto" is the loadout that is ready soonest.</summary>
        private string AlertAutoText(string type)
        {
            var names = _env.LoadoutNames(type);
            string best = null; double bm = double.MaxValue;
            if (names != null)
                foreach (string n in names)
                {
                    double m = -1;
                    try { m = _env.LoadoutReadyMin(type, n); } catch { }
                    if (m >= 0 && m < bm) { bm = m; best = n; }
                }
            if (best == null) return Tx("cap.ui.loadout_auto");
            string shown = null;
            try { shown = _env.LoadoutLabel(type, best); } catch { }
            return Tx("cap.val.auto_fastest", CapWindowModel.LoadoutText(best, shown), Math.Round(bm).ToString("0", CultureInfo.InvariantCulture));
        }
    }
}
