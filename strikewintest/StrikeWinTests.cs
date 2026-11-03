using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class StrikeWinState : IUiWindowState, IUiWindowValues
    {
        public readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> _vals = new Dictionary<string, string>();
        public string Value(string id, string dflt) { string v; return _vals.TryGetValue(id, out v) ? v : dflt; }
        public void SetValue(string id, string v) { _vals[id] = v; }
        public bool SectionOpen(string id, bool dflt) { bool v; return Open.TryGetValue(id, out v) ? v : dflt; }
        public void SetSectionOpen(string id, bool open) { Open[id] = open; }
    }

    internal sealed class StrikeFakeEnv : IStrikeWindowEnv
    {
        public double Time; public List<int> Focused = new List<int>(); public int Picks; public string Cancelled; public bool Active;
        public string FocusUnit(int uid) { Focused.Add(uid); return null; }
        public void StartPick() { Picks++; Active = true; }
        public void CancelPick(string why) { Cancelled = why; Active = false; }
        public bool PickActive { get { return Active; } }
        public double Now() { return Time; }
    }

    internal sealed class StrikeWin
    {
        public RecordPainter P; public UiKit K; public StrikeWindowBody B; public StrikeView V; public StrikeFakeEnv Env = new StrikeFakeEnv();
        public List<string> Sent = new List<string>(); public List<Dictionary<string, object>> Args = new List<Dictionary<string, object>>();
        public Func<string, string> Ack = n => "{\"ok\":true}";
        public float W, H;

        public StrikeWin(double s, float width, StrikeView v)
        {
            P = new RecordPainter(s); K = new UiKit(P, UiTheme.For(s), new StrikeWinState(), "strike"); W = width; V = v;
            B = new StrikeWindowBody((n, a) => { Sent.Add(n); Args.Add(a); return Ack(n); }, Env);
        }

        public void Frame() { P.NextFrame(); K.Begin(W); B.Draw(K, V); H = K.End(); }
        public void Click(string id) { P.Click(id); Frame(); }
        public bool Has(string id) { return P.Find(id) != null; }
        public string Cmds() { return string.Join(",", Sent); }
        public bool Text(string t) { return P.Items.Any(i => i.Text != null && i.Text.Contains(t)); }
    }

    internal static class StrikeWinTests
    {
        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        private static StrikeAirRow Air(int uid, string name, string group = null, string why = null, string where = "air", params string[] missiles)
        {
            var r = new StrikeAirRow { Uid = uid, Name = name, Type = "usn_a-6e", Group = group, Why = why, Where = where, Airborne = where == "air", FuelPct = 92 };
            r.Missiles.AddRange(missiles);
            return r;
        }

        public static StrikeView Sample(bool cards = true, bool longNames = false)
        {
            var v = new StrikeView { Calib = "cache", Now = 1000 };
            var a = new StrikeGroupView { Name = "Удар-1", Seq = 1, Status = cards ? "draft" : "idle", Gather = "waiting", Mode = "single", MainId = 100, WaitPt = new GeoPt(39.25, 10), WaitR = 5, WaitWhy = "ok", Chosen = cards ? 0 : -1, TargetPt = new GeoPt(41, 10), AxisDeg = 0, CanLaunch = cards, LaunchWhy = cards ? null : "no_plan" };
            a.Members.Add(1); a.Members.Add(2); a.Targets.Add(100);
            var b = new StrikeGroupView { Name = "Удар-2", Seq = 2, Status = "idle", Gather = "idle", Mode = "single" };
            v.Groups.Add(a); v.Groups.Add(b);
            string n1 = longNames ? "A-6E Intruder with a very long name number 1" : "A-6E 1";
            v.Air.Add(Air(1, n1, "Удар-1", null, "air", "AGM-84C x4")); v.Air.Add(Air(2, "A-6E 2", "Удар-1", null, "air", "AGM-84C x4")); v.Air.Add(Air(4, "A-6E 4", null, null, "air", "AGM-84C x4"));
            v.Air.Add(new StrikeAirRow { Uid = 0, DeckKey = "task:5", Type = "usn_a-6e", Name = "usn_a-6e", Where = "deck", ReadyInS = 300, Missiles = { "AGM-84D x4" } });
            v.Air.Add(Air(3, "SH-3H 3", null, "helicopter", "air", "AM-39 x2")); v.Air.Add(Air(5, "EA-6B 5", null, "ew_later")); v.Air.Add(Air(6, "A-6E 6", null, "owned:cap:ПВО-1", "air", "AGM-84C x4"));
            v.Ships.Add(new StrikeShipRow { Id = 100, Prio = 1, ClassName = longNames ? "Ticonderoga with a really long name" : "Kidd", Ksub = 8.86, Ssub = 27.7, AdSource = "cache", Tons = 8300, SamNm = 90, HorizonNm = 21, Why = "ad", Need = 8, Hostile = true, Lat = 41, Lon = 10 });
            v.Ships.Add(new StrikeShipRow { Id = 101, Prio = 2, ClassName = "Perry", Ksub = 3.1, Ssub = 9.5, AdSource = "aaw", Tons = 4000, SamNm = 20, HorizonNm = 20, Why = "ad", Need = 5, Hostile = true, Lat = 41.1, Lon = 10.5 });
            v.Ships.Add(new StrikeShipRow { Id = 102, Prio = 3, ClassName = "Cargo", Tons = 20000, Why = "low_role", Role = "Transport", AdSource = "none", Hostile = true });
            v.Ships.Add(new StrikeShipRow { Id = 103, ClassName = "Neutral", Excl = "neutral", Tons = 8000, AdSource = "cache", Ksub = 5, Ssub = 10 });
            v.Ships.Add(new StrikeShipRow { Id = 104, ClassName = "Merchant", Excl = "civilian", Tons = 30000, AdSource = "none", Hostile = true });
            if (cards)
            {
                var c0 = new StrikeCardView { Kind = "min_missiles", Missiles = 8, Targets = 1, T = 520, FuelMin = 61, Estimate = true, Index = 0, MainP = 0.91 };
                c0.PerTarget.Add(new StrikeCardTarget { Id = 100, N = 8, P = 0.91, Name = "Kidd" });
                c0.Carriers.Add(new StrikeCardCarrier { Uid = 1, Name = "A-6E 1", Ammo = "usn_agm-84c", AmmoName = "AGM-84C", Salvo = 4, TargetId = 100 });
                c0.Carriers.Add(new StrikeCardCarrier { Uid = 2, Name = "A-6E 2", Ammo = "usn_agm-84c", AmmoName = "AGM-84C", Salvo = 4, TargetId = 100 });
                c0.Warnings.Add("deck_pending:1");
                var c1 = new StrikeCardView { Kind = "clearing", Missiles = 12, Targets = 2, T = 560, FuelMin = 55, Estimate = true, Index = 1, Red = true, MainP = 0.7 };
                c1.PerTarget.Add(new StrikeCardTarget { Id = 101, N = 4, P = 0.93, Name = "Perry" });
                c1.PerTarget.Add(new StrikeCardTarget { Id = 100, N = 8, P = 0.7, Name = "Kidd", ClearedBy = 101 });
                c1.Warnings.Add("short n=3 target=100"); c1.Warnings.Add("launch_land:2");
                c1.Why.Add("clearing_no_depletion");
                a.Cards.Add(c0); a.Cards.Add(c1);
                a.Timeline.Add(new StrikeTimelineRow { Uid = 1, Name = "A-6E 1", TGo = 12, TIssue = 330, TLaunch = 350, TArrive = 520 });
                a.Timeline.Add(new StrikeTimelineRow { Uid = 2, Name = "A-6E 2", TGo = 15, TIssue = 335, TLaunch = 355, TArrive = 520 });
                a.Launches.Add(new StrikeLaunchView { Uid = 1, Name = "A-6E 1", Pt = new GeoPt(40, 10), TargetId = 100 });
            }
            return v;
        }

        /// <summary>T-342 B: a group with three orders (one preparing, one launching, one failed), a short summary and the magazine.</summary>
        public static StrikeView DeckSample(string block = null, bool shortAll = false)
        {
            var v = SinkSample(true);
            var g = v.Groups[0];
            g.DeckOrders.Add(new StrikeDeckOrderRow { Key = "task:57", DeckName = "Kitty Hawk", Type = "usn_a-6e", Loadout = "AntiShipHeavy", Display = "Anti-Ship Heavy", Ammo = "usn_agm-84c", State = "preparing", Missiles = 4, ReadyS = 270, Live = true });
            g.DeckOrders.Add(new StrikeDeckOrderRow { Key = "task:58", DeckName = "Kitty Hawk", Type = "usn_a-6e", Loadout = "AntiShipHeavy", Display = "Anti-Ship Heavy", Ammo = "usn_agm-84c", State = "launch", Missiles = 4, ReadyS = 0, Live = true });
            g.DeckOrders.Add(new StrikeDeckOrderRow { Key = "task:59", DeckName = "America", Type = "usn_a-6e", Loadout = "AntiShip", Display = "Anti-Ship", Ammo = "usn_agm-84c", State = "failed", Err = "late", Missiles = 2, ReadyS = 0, Live = false });
            g.HasDeckPlan = true; g.DeckKind = "min_missiles"; g.DeckAircraft = 2; g.DeckMissiles = 8; g.DeckLastReadyS = 270; g.DeckBlock = block; g.ShortAfterAll = shortAll;
            g.NowN = 16; g.NowP = 0.42; g.NowOk = false; g.WithN = 24; g.WithP = shortAll ? 0.6 : 0.84; g.WithOk = !shortAll; g.AllN = 24; g.AllP = g.WithP; g.AllOk = !shortAll;
            g.DeckStock.Add("Kitty Hawk|usn_agm-84c|32|48"); g.DeckRejected.Add("America|usn_ea-6b|not_striker");
            g.DeckNotes.Add("deck_ordered:2:usn_a-6e:Anti-Ship Heavy:8:usn_agm-84c:Kitty Hawk:270");
            return v;
        }

        /// <summary>The layout check has a window height cap: the deck data replaces the long aircraft list and the course of the strike, which the scroll area of the real window carries.</summary>
        private static StrikeView Trim(StrikeView v) { v.Air.Clear(); v.Groups[0].Timeline.Clear(); v.Groups[0].Launches.Clear(); v.Ships.Clear(); return v; }

        /// <summary>T-342 C: a group with the auto mode on, a clear_pause card, the clearing running.</summary>
        public static StrikeView AutoSample(bool on = true, string clearStage = null, string stopWhy = null)
        {
            var v = SinkSample(true);
            var g = v.Groups[0];
            g.AutoOn = on; g.AutoWave = 2; g.AutoMax = 3; g.AutoState = on ? "wait" : "stop"; g.AutoWhy = "deck"; g.AutoStopWhy = stopWhy;
            g.AutoNotes.Add("auto_launch:1:min_missiles"); g.AutoNotes.Add("auto_red:0.42:0.8");
            if (clearStage != null)
            {
                g.ClearStage = clearStage; g.ClearNeighbour = 101; g.ClearMain = 100; g.ClearName = "Perry"; g.ClearWaitS = 80; g.ClearTimeoutS = 820; g.ClearFuelS = 1400;
            }
            var cp = new StrikeCardView { Kind = "clear_pause", Goal = "sink", Missiles = 18, Targets = 2, T = 600, TStage2 = 900, Index = 2, KillEstimate = true, ClearNeighbour = 101, MainP = 0.81 };
            cp.PerTarget.Add(new StrikeCardTarget { Id = 101, N = 6, P = 0.86, PSunk = 0.86, Name = "Perry", Stage = 1 });
            cp.PerTarget.Add(new StrikeCardTarget { Id = 100, N = 12, P = 0.81, PSunk = 0.81, Name = "Kidd", Stage = 2, PIfAlive = 0.40 });
            g.Cards.Add(cp);
            return v;
        }

        public static string BodyAuto()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var win = new StrikeWin(1.0, 520f, AutoSample());
                    win.Frame(); win.Frame();
                    if (!win.Has("strike.auto")) return tag + "the flag";
                    if (!win.Text(T("strike.auto.wave", 2, 3))) return tag + "the wave 2/3";
                    if (!win.Text(StrikeWindow.AutoStateText("wait")) || !win.Text(StrikeWindow.AutoWhyText("deck"))) return tag + "the state: waiting for the deck";
                    if (!win.Text(StrikeWindow.AutoNoteText("auto_red:0.42:0.8")) || !win.Text("42%")) return tag + "the note of the red launch";
                    win.Click("strike.auto");
                    if (win.Cmds() != "strike_auto" || (string)win.Args[0]["group"] != "Удар-1" || !(win.Args[0]["on"] is bool) || (bool)win.Args[0]["on"]) return tag + "the flag sends strike_auto off " + win.Cmds();
                    var off = new StrikeWin(1.0, 520f, AutoSample(false, null, "waves")); off.Frame(); off.Frame();
                    if (!off.Text(StrikeWindow.AutoStopText("waves"))) return tag + "the reason of the stop";
                    off.Click("strike.auto");
                    if (off.Cmds() != "strike_auto" || !(bool)off.Args[0]["on"]) return tag + "the flag sends strike_auto on";
                    if (StrikeWindow.AutoStopText("launch_err:gate").Contains("{0}") || StrikeWindow.AutoStopText("launch_err:gate").StartsWith("strike.")) return tag + "stop text with a detail";
                    foreach (string st in new[] { "off", "wait", "replan", "launch", "stop" }) if (StrikeWindow.AutoStateText(st).StartsWith("strike.")) return tag + "state " + st;
                    foreach (string w in new[] { "deck", "gather", "executing", "replan", "replan_gap", "clear" }) if (StrikeWindow.AutoWhyText(w).StartsWith("strike.")) return tag + "why " + w;
                    foreach (string w in new[] { "no_targets", "no_missiles", "no_plan", "empty_group", "cancel", "target_changed", "waves", "clear_timeout", "clear_fuel", "clear_lost", "recheck", "launch_err:x", "blocked:y" }) if (StrikeWindow.AutoStopText(w).StartsWith("strike.")) return tag + "stop " + w;
                }
            return null;
        }

        public static string CardClearPause()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var win = new StrikeWin(1.0, 520f, AutoSample()); win.Frame(); win.Frame();
                    if (!win.Text("Perry") || !win.Text("86%") || !win.Text("81%") || !win.Text("40%")) return tag + "two stages with P and PIfAlive";
                    if (!win.Text(T("strike.card.clear_cond"))) return tag + "the condition";
                    if (!win.Text(StrikeWindow.KindText("clear_pause")) || StrikeWindow.KindText("clear_pause").StartsWith("strike.")) return tag + "the kind";
                }
            return null;
        }

        public static string BodyClear()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var w = new StrikeWin(1.0, 520f, AutoSample(true, "wait_sunk")); w.Frame(); w.Frame();
                    if (!w.Text(StrikeWindow.Mmss(80)) || !w.Text(StrikeWindow.Mmss(820)) || !w.Text(StrikeWindow.Mmss(1400))) return tag + "the status of the wait with the timeout and the fuel";
                    foreach (string st in new[] { "timeout", "fuel", "lost" })
                    {
                        var e = new StrikeWin(1.0, 520f, AutoSample(false, st)); e.Frame(); e.Frame();
                        if (!e.Text(StrikeWindow.ClearEndText(st).Substring(0, 14))) return tag + "the choice after " + st;
                    }
                    var r = new StrikeWin(1.0, 520f, AutoSample(false, "red_wait")); r.Frame(); r.Frame();
                    if (!r.Text(T("strike.msg.clear_red"))) return tag + "red_wait";
                    var sw = new StrikeWin(1.0, 520f, AutoSample(false, "stale_wait", "clear_stale")); sw.Frame(); sw.Frame();
                    if (!sw.Text(T("strike.msg.clear_stale"))) return tag + "stale_wait";
                    if (StrikeWindow.AutoStopText("clear_stale").StartsWith("strike.")) return tag + "stop clear_stale";
                }
            return null;
        }

        private static StrikeView Empty() { return new StrikeView { Calib = "none" }; }

        private static IEnumerable<string> Langs() { return new[] { "ru", "en" }; }

        public static string Layout()
        {
            var sb = new StringBuilder();
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                    foreach (double s in new[] { 1.0, 1.33 })
                        foreach (float width in new[] { 440f, 520f, 800f })
                        {
                            float w = width * (float)s;
                            for (int di = 0; di < 8; di++)
                            {
                                string tag = lang + " s=" + s + " w=" + width + " data=" + di;
                                var v = di == 0 ? Empty() : di == 1 ? Sample() : di == 2 ? Sample(false) : di == 3 ? Sample(true, true) : di == 4 ? Trim(DeckSample()) : di == 5 ? Trim(DeckSample("player", true)) : di == 6 ? Trim(AutoSample(true, "wait_sunk")) : Trim(AutoSample(false, "timeout", "clear_timeout"));
                                var win = new StrikeWin(s, w, v);
                                win.Frame(); win.Frame();
                                string r = win.P.CheckLayout(w, (float)(0.85 * 1080 * s));
                                if (r != null) sb.Append(tag + ": " + r + "\n");
                                if (di == 0 && !win.Text(T("strike.hint.no_groups").Substring(0, 12))) sb.Append(tag + ": the empty window lacks the no-groups hint\n");
                                if (width >= 520f && win.K.Overflows.Count > 0 && di != 3) sb.Append(tag + ": overflow " + string.Join("|", win.K.Overflows) + "\n");
                            }
                        }
            return sb.Length == 0 ? null : sb.ToString();
        }

        public static string Dump(string lang)
        {
            var sb = new StringBuilder();
            using (UiText.Push(lang))
            {
                var win = new StrikeWin(1.0, 520f, Sample());
                win.Frame(); win.Frame();
                foreach (var it in win.P.Items) sb.Append(it).Append('\n');
            }
            return sb.ToString();
        }

        public static string Groups()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var win = new StrikeWin(1.0, 520f, Sample());
                    win.Frame(); win.Frame();
                    if (win.B.SelectedGroup != "Удар-1") return tag + "the first group is selected";
                    win.Click("grp.1");
                    if (win.B.SelectedGroup != "Удар-2") return tag + "chip selects " + win.B.SelectedGroup;
                    if (win.Sent.Count != 0) return tag + "a chip sends nothing";
                    win.Click("create");
                    if (win.Cmds() != "strike_group" || (string)win.Args[0]["op"] != "create" || win.Args[0].ContainsKey("group")) return tag + "create " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    win.P.Type("rename", "Молот"); win.Frame();
                    win.Click("rename.btn");
                    if (win.Cmds() != "strike_group" || (string)win.Args[0]["op"] != "rename" || (string)win.Args[0]["group"] != "Удар-2" || (string)win.Args[0]["name"] != "Молот") return tag + "rename " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    win.Click("grp.1"); win.Frame();   // the fake view did not rename the group: select the second one again
                    if (win.B.SelectedGroup != "Удар-2") return tag + "reselect " + win.B.SelectedGroup;
                    win.Click("disband"); win.Frame();
                    if (win.Sent.Count != 0) return tag + "the first click of Disband only asks";
                    var dis = win.P.Find("disband");
                    if (dis == null || dis.Text != T("strike.btn.disband_sure", "Удар-2")) return tag + "Disband asks for the confirmation";
                    win.Click("disband");
                    if (win.Cmds() != "strike_group" || (string)win.Args[0]["op"] != "disband" || (string)win.Args[0]["group"] != "Удар-2") return tag + "disband " + win.Cmds();
                    // an error answer is said, not thrown
                    win.Sent.Clear(); win.Args.Clear();
                    var w2 = new StrikeWin(1.0, 520f, Sample());
                    w2.Ack = n => "!group_exists";
                    w2.Frame(); w2.Click("create");
                    if (w2.B.Note == null || !w2.B.Note.Contains(StrikeWindow.ErrText("!group_exists"))) return tag + "an error is shown " + w2.B.Note;
                    if (w2.B.NoteTone != UiTone.Error) return tag + "error tone";
                }
            return null;
        }

        public static string Air()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = Sample();
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    // strikers first, grey rows after; the reasons are in the last column
                    if (!win.Text(StrikeWindow.WhyText("helicopter")) || !win.Text(StrikeWindow.WhyText("ew_later")) || !win.Text(StrikeWindow.WhyText("owned:cap:ПВО-1"))) return tag + "grey rows say why";
                    if (!win.Text("AGM-84C x4") || !win.Text(T("strike.where.deck_ready", "5:00"))) return tag + "missiles and the deck row";
                    // pick a free striker, add
                    win.Click("air.chk.4"); win.Click("air.add");
                    if (win.Cmds() != "strike_group" || (string)win.Args[0]["op"] != "add" || (string)win.Args[0]["group"] != "Удар-1") return tag + "add " + win.Cmds();
                    var uids = (List<object>)win.Args[0]["uids"];
                    if (uids.Count != 1 || (double)uids[0] != 4 || win.Args[0].ContainsKey("deck_keys")) return tag + "add uids";
                    win.Sent.Clear(); win.Args.Clear();
                    // a deck task goes as a key
                    int di = v.Air.FindIndex(x => x.DeckKey == "task:5");
                    win.Click("air.chk.-" + (di + 1)); win.Click("air.add");
                    if (win.Cmds() != "strike_group" || !win.Args[0].ContainsKey("deck_keys") || ((List<object>)win.Args[0]["deck_keys"]).Count != 1 || (string)((List<object>)win.Args[0]["deck_keys"])[0] != "task:5") return tag + "deck key " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    // a refused (grey) aircraft sends nothing and says why
                    win.Click("air.chk.3"); win.Click("air.add");
                    if (win.Sent.Count != 0 || win.B.Note == null || !win.B.Note.Contains(StrikeWindow.WhyText("helicopter"))) return tag + "a helicopter is refused with the reason: " + win.B.Note + " sent=" + win.Cmds();
                    // remove a member
                    win.Click("air.chk.3");   // unpick
                    win.Click("air.chk.1"); win.Click("air.remove");
                    if (win.Cmds() != "strike_group" || (string)win.Args[0]["op"] != "remove") return tag + "remove " + win.Cmds();
                    // a click on a name centres the map
                    var link = win.P.Items.FirstOrDefault(i => i.Kind == "Link" && i.Text == "A-6E 4");
                    if (link == null) return tag + "no link for A-6E 4";
                    win.Click(link.Id);
                    if (win.Env.Focused.Count != 1 || win.Env.Focused[0] != 4) return tag + "focus " + string.Join(",", win.Env.Focused);
                }
            return null;
        }

        public static string Target()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = Sample();
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    if (!win.Text(StrikeWindow.ExclText("neutral")) || !win.Text(StrikeWindow.ExclText("civilian"))) return tag + "excluded ships say why";
                    if (!win.Text(StrikeWindow.PrioText(v.Ships[0]))) return tag + "priority reason " + StrikeWindow.PrioText(v.Ships[0]);
                    if (!win.Text(StrikeWindow.AdCell(v.Ships[0])) || !win.Text(StrikeWindow.PrioText(v.Ships[2]))) return tag + "AA cell / low role";
                    win.Click("ships.chk.101"); win.Click("assign");
                    if (win.Cmds() != "strike_target" || (string)win.Args[0]["mode"] != "single" || (double)win.Args[0]["target_id"] != 101 || win.Args[0].ContainsKey("track_ids")) return tag + "single " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    // spread: the ticked ships, the first by priority is the main one
                    win.Click("mode.1");
                    win.Click("ships.chk.100"); win.Click("assign");
                    if (win.Cmds() != "strike_target" || (string)win.Args[0]["mode"] != "spread" || (double)win.Args[0]["target_id"] != 100) return tag + "spread " + win.Cmds() + " " + (win.Args.Count > 0 ? (string)win.Args[0]["mode"] : "");
                    var ids = (List<object>)win.Args[0]["track_ids"];
                    if (ids.Count != 2 || (double)ids[0] != 100 || (double)ids[1] != 101) return tag + "spread ids";
                    win.Sent.Clear(); win.Args.Clear();
                    // an excluded ship cannot be a target: nothing is sent, the reason is said
                    win.Click("ships.chk.101"); win.Click("ships.chk.100"); win.Click("ships.chk.103"); win.Click("assign");
                    if (win.Sent.Count != 0 || win.B.Note == null || !win.B.Note.Contains(StrikeWindow.ExclText("neutral"))) return tag + "excluded target " + win.Cmds() + " " + win.B.Note;
                    // the map pick
                    win.Click("pick"); win.Frame();
                    if (win.Env.Picks != 1 || !win.B.PickActive || !win.Text(T("strike.hint.pick_active"))) return tag + "pick mode";
                    win.B.OnShipPicked(100, v);
                    if (win.Cmds() != "strike_target" || (string)win.Args[0]["mode"] != "single" || (double)win.Args[0]["target_id"] != 100) return tag + "picked ship " + win.Cmds();
                    // a pick without a group
                    var w2 = new StrikeWin(1.0, 520f, new StrikeView { Ships = { new StrikeShipRow { Id = 1, Prio = 1 } } });
                    w2.Frame(); w2.B.OnShipPicked(1, w2.V);
                    if (w2.Sent.Count != 0 || w2.B.Note == null) return tag + "pick without a group";
                }
            return null;
        }

        /// <summary>Phase D: the jammers in the card and in the group rows: covered of all, late + the button that shifts T, a band the jammer does not cover, "no jamming" without a jammer.</summary>
        /// <summary>T-342 D: the lines of a jammer against the fighters (transit / attack / retreat / reserve / the ships nobody jams / the IR mark) and the notes.</summary>
        public static string EwFighter()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = Sample();
                    var g = v.Groups[0];
                    g.Ew.Add(new StrikeEwRow { Uid = 5, Name = "EA-6B 5", State = "to_station", On = true, DistToStationNm = 20, JamKind = "object", Mode = "transit", FighterId = 4021, FighterCls = "F-14A", FighterDistNm = 52.1, FighterRtNm = 38, FighterIrOnly = true });
                    g.Ew.Add(new StrikeEwRow { Uid = 6, Name = "EA-6B 6", State = "jamming", On = true, Role = "fighters", JamKind = "object", Mode = "transit", FighterId = 4022, FighterCls = "MiG-25PD", FighterDistNm = 60, FighterRtNm = 45, ShipsLost = { 100 } });
                    g.Ew.Add(new StrikeEwRow { Uid = 7, Name = "EA-6B 7", State = "jamming", On = true, Mode = "attack" });
                    g.Ew.Add(new StrikeEwRow { Uid = 8, Name = "EA-6B 8", State = "retreat", Mode = "retreat", FighterWhy = "no_cover:4030" });
                    g.FighterThreats.Add(new StrikeFighterRow { Id = 4030, Cls = "MiG-23MLD", RNm = 30, DistNm = 28, EtaS = 0 });
                    g.FighterUncovered.Add(4030); g.FighterUncovered.Add(4031);
                    g.EwNotes.Add("ewf_no_cover:8:MiG-23MLD"); g.EwNotes.Add("ewf_ships_lost:6:1");
                    var win = new StrikeWin(1.0, 520f, Trim(v));
                    win.Frame(); win.Frame();
                    if (!win.Text(T("strike.ew.mode.transit") + ": " + T("strike.ew.fighter.jams", "F-14A", "#4021", "52", "38"))) return tag + "transit: jams the fighter";
                    if (!win.Text(T("strike.ew.mode.attack"))) return tag + "attack: the ships only";
                    if (!win.Text(T("strike.ew.fighter.no_cover", "MiG-23MLD"))) return tag + "cannot cover";
                    if (!win.Text(T("strike.ew.role.fighters", "EA-6B 6"))) return tag + "the reserve";
                    if (!win.Text(T("strike.ew.fighter.ships_lost", "#100"))) return tag + "the ships without jamming";
                    if (!win.Text(T("strike.ew.fighter.uncovered", "2"))) return tag + "uncovered fighters";
                    if (!win.Text(T("strike.ew.fighter.ir_only"))) return tag + "the IR mark";
                    if (!win.Text(StrikeWindow.EwNoteText("ewf_ships_lost:6:1"))) return tag + "note";
                    string r = win.P.CheckLayout(520f, 900f);
                    if (r != null) return tag + "layout " + r;
                    if (StrikeWindow.WarnText("ew_fighter_reserve:6").StartsWith("strike.", StringComparison.Ordinal)) return tag + "reserve warning";
                }
            return null;
        }

        public static string Ew()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = Sample();
                    var c0 = v.Groups[0].Cards[0];
                    var e = new StrikeEwRow { Uid = 5, Name = "EA-6B 5", State = "plan", ConeDeg = 30, LateS = 200, ShiftS = 240, Station = new GeoPt(38, 10), Aim = new GeoPt(41, 10) };
                    e.Cover.Add(100); e.Cover.Add(101); e.Uncovered.Add(102); e.BandsMissing.Add("101:Ku");
                    c0.Ew.Add(e);
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    if (!win.Text(T("strike.ew.line", "EA-6B 5", "2", "3"))) return tag + "covered 2 of 3";
                    if (!win.Text(T("strike.ew.uncovered", "#102"))) return tag + "uncovered";
                    if (!win.Text(T("strike.ew.late", "4"))) return tag + "late by 4 minutes";
                    if (!win.Text(T("strike.ew.band", "101", "Ku"))) return tag + "band line";
                    var shift = win.P.Find("shift.0.5");
                    if (shift == null || !shift.Enabled || shift.Text != T("strike.btn.shift", "4")) return tag + "the shift button " + (shift == null ? "missing" : shift.Text);
                    win.Ack = n => "{\"ok\":true}";
                    win.Click("shift.0.5");
                    if (win.Cmds() != "strike_plan" || (double)win.Args[0]["shift_s"] != 240 || (string)win.Args[0]["group"] != "Удар-1") return tag + "shift sends strike_plan shift_s " + win.Cmds();
                    // no jammer: "no jamming" under every card
                    var v2 = Sample();
                    var w2 = new StrikeWin(1.0, 520f, v2); w2.Frame(); w2.Frame();
                    if (!w2.Text(T("strike.ew.none"))) return tag + "no jamming";
                    if (w2.P.Items.Any(i => i.Id != null && i.Id.StartsWith("shift.", StringComparison.Ordinal))) return tag + "no shift button without lateness";
                    // the rows of the group: state, miles to the station, the time to the switch on
                    var v3 = Sample();
                    v3.Groups[0].Ew.Add(new StrikeEwRow { Uid = 5, Name = "EA-6B 5", State = "jamming", On = true, DistToStationNm = 3, TOnS = -20 });
                    var w3 = new StrikeWin(1.0, 520f, v3); w3.Frame(); w3.Frame();
                    if (!w3.Text(StrikeWindow.EwStateText("jamming"))) return tag + "state row";
                    string r = w3.P.CheckLayout(520f, 900f);
                    if (r != null) return tag + "layout " + r;
                }
            return null;
        }

        // ---- T-342 phase A: the goal of the strike, the sinking cards, the confirmation of a red launch
        private static StrikeView SinkSample(bool red)
        {
            var v = Sample();
            var g = v.Groups[0];
            g.Goal = "sink"; v.KillThreshold = 0.8;
            g.Cards.Clear();
            var c0 = red
                ? new StrikeCardView { Kind = "damage_only", Goal = "sink", Missiles = 16, Targets = 0, T = 520, FuelMin = 61, Estimate = true, KillEstimate = true, Index = 0, Red = true, MainP = 0.42 }
                : new StrikeCardView { Kind = "min_missiles", Goal = "sink", Missiles = 13, Targets = 1, T = 520, FuelMin = 61, Estimate = true, KillEstimate = true, Index = 0, MainP = 0.84 };
            c0.PerTarget.Add(red
                ? new StrikeCardTarget { Id = 100, N = 16, P = 0.42, PSunk = 0.42, PBreach = 0.97, H1 = 4, MSink = 20, Short = true, ShortBy = 4, Name = "Ticonderoga" }
                : new StrikeCardTarget { Id = 100, N = 13, P = 0.84, PSunk = 0.84, PBreach = 0.99, H1 = 4, MSink = 13, Name = "Kidd" });
            c0.Carriers.Add(new StrikeCardCarrier { Uid = 1, Name = "A-6E 1", Ammo = "usn_agm-84c", AmmoName = "AGM-84C", Salvo = 4, TargetId = 100 });
            if (red) { c0.Warnings.Add("kill_short n=4 target=100"); c0.Warnings.Add("mix_late uid=2 ammo=wp_as-2"); }
            else c0.Warnings.Add("mixed_shift uid=2 s=66");
            g.Cards.Add(c0);
            g.Chosen = 0; g.CanLaunch = true; g.LaunchWhy = null;
            return v;
        }

        public static string Goal()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = SinkSample(false);
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    var b0 = win.P.Find("goal.0"); var b1 = win.P.Find("goal.1");
                    if (b0 == null || b1 == null || b0.Text != T("strike.goal.breach") || b1.Text != T("strike.goal.sink")) return tag + "the two goal buttons " + (b0 == null ? "none" : b0.Text);
                    if (!win.Text(T("strike.goal.label"))) return tag + "goal label";
                    if (b1.Tone == UiTone.Dim || b0.Tone == UiTone.Dim) return tag + "buttons are live outside a strike";
                    win.Click("goal.1");
                    if (win.Sent.Count != 0) return tag + "the goal that is set sends nothing";
                    win.Click("goal.0");
                    if (win.Cmds() != "strike_goal" || (string)win.Args[0]["group"] != "Удар-1" || (string)win.Args[0]["goal"] != "breach") return tag + "breach press " + win.Cmds();
                    if (win.B.Note == null || !win.B.Note.Contains(T("strike.goal.breach"))) return tag + "goal note " + win.B.Note;
                    // a refusal is told
                    var wr = new StrikeWin(1.0, 520f, SinkSample(false)); wr.Ack = n => "!executing"; wr.Frame(); wr.Frame(); wr.Click("goal.0");
                    if (wr.B.Note == null || !wr.B.Note.Contains(StrikeWindow.ErrText("!executing"))) return tag + "refusal " + wr.B.Note;
                    // during the strike the switch is grey and sends nothing
                    var vx = SinkSample(false); vx.Groups[0].Exec = "executing";
                    var wx = new StrikeWin(1.0, 520f, vx); wx.Frame(); wx.Frame();
                    if (wx.P.Find("goal.0").Tone != UiTone.Dim || wx.P.Find("goal.1").Tone != UiTone.Dim) return tag + "grey during the strike";
                    wx.Click("goal.0");
                    if (wx.Sent.Count != 0 || wx.B.Note == null || !wx.B.Note.Contains(T("strike.say.goal_running"))) return tag + "no command during the strike " + wx.Cmds();
                    // the notes: the difficulty bonus not read, the counter off (only for the goal sink)
                    var vn = SinkSample(false); vn.BonusKnown = false; vn.Ledger = false;
                    var wn = new StrikeWin(1.0, 520f, vn); wn.Frame(); wn.Frame();
                    if (!wn.Text(T("strike.warn.bonus_default")) || !wn.Text(T("strike.warn.no_ledger"))) return tag + "difficulty / counter notes";
                    vn.Groups[0].Goal = "breach";
                    var wb = new StrikeWin(1.0, 520f, vn); wb.Frame(); wb.Frame();
                    if (wb.Text(T("strike.warn.bonus_default"))) return tag + "no notes for breach";
                    if (StrikeWindow.StaleText("goal") == "strike.stale.goal" || StrikeWindow.ErrText("!red_confirm").StartsWith("strike.", StringComparison.Ordinal)) return tag + "texts of the goal";
                }
            return null;
        }

        public static string Sink()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = SinkSample(false);
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    var c = v.Groups[0].Cards[0];
                    if (win.P.Find("card.0").Text != StrikeWindow.CardTitle(c) || win.P.Find("card.0").Tone != UiTone.Accent) return tag + "the green sink card is chosen, not red";
                    if (!win.Text(T("strike.card.sink", "Kidd #100", "13", "4", StrikeWindow.ProbCell(0.84, true)))) return tag + "the sink line";
                    if (!StrikeWindow.ProbCell(0.84, true).Contains(lang == "ru" ? "оценка" : "estimate")) return tag + "the estimate mark on P(sunk)";
                    if (!win.Text(StrikeWindow.WarnText("mixed_shift uid=2 s=66")) || !StrikeWindow.WarnText("mixed_shift uid=2 s=66").Contains("66")) return tag + "mixed_shift";
                    // the red damage_only card: what is missing, both probabilities
                    var vr = SinkSample(true);
                    var wr = new StrikeWin(1.0, 520f, vr); wr.Frame(); wr.Frame();
                    var cr = vr.Groups[0].Cards[0];
                    if (wr.P.Find("card.0").Tone != UiTone.Error || wr.P.Find("card.0").Text != StrikeWindow.CardTitle(cr) || !wr.P.Find("card.0").Text.Contains(T("strike.kind.damage_only"))) return tag + "the red card " + wr.P.Find("card.0").Text;
                    if (!wr.Text(T("strike.card.short", "Ticonderoga #100", "4", StrikeWindow.ProbCell(0.42, true), StrikeWindow.ProbCell(0.97, false)))) return tag + "the damage_only line";
                    if (!wr.Text(StrikeWindow.WarnText("kill_short n=4 target=100")) || !wr.Text("wp_as-2") || !StrikeWindow.WarnText("mix_late uid=2 ammo=wp_as-2").Contains("wp_as-2")) return tag + "kill_short / mix_late";
                    if (StrikeWindow.WarnText("kill_unknown target=7").StartsWith("strike.", StringComparison.Ordinal) || !StrikeWindow.WarnText("kill_unknown target=7").Contains("7")) return tag + "kill_unknown";
                    // the plain v1 card under the goal breach still reads as before
                    var vb = Sample(); var wb = new StrikeWin(1.0, 520f, vb); wb.Frame(); wb.Frame();
                    if (!wb.Text(T("strike.card.target", "Kidd #100", "8", StrikeWindow.ProbCell(0.91, true)))) return tag + "the v1 line is unchanged";
                    // the jammer waits for the clear time
                    var ve = Sample(); ve.Groups[0].Ew.Add(new StrikeEwRow { Uid = 5, Name = "EA-6B 5", State = "retreat", Why = "clear_wait", ClearLeftS = 45 });
                    var we = new StrikeWin(1.0, 520f, ve); ((StrikeWinState)GetState(we)).Open["strike.timeline"] = true; we.Frame(); we.Frame();
                    if (!we.Text(T("strike.ew.clear_wait", "0:45"))) return tag + "clear_wait text " + string.Join("|", we.P.Items.Where(i => i.Text != null && i.Text.Contains("EA-6B")).Select(i => i.Text));
                }
            return null;
        }

        public static string RedConfirm()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = SinkSample(true);
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Ack = n => n == "strike_launch" ? "{\"ok\":true,\"rows\":2,\"roe\":\"veto\"}" : "{\"ok\":true}";
                    win.Frame(); win.Frame();
                    var launch = win.P.Find("launch");
                    if (launch == null || !launch.Enabled || launch.Tip == null || !launch.Tip.Contains(T("strike.confirm.red"))) return tag + "the tip of a red Launch " + (launch == null ? "none" : launch.Tip);
                    win.Click("launch");
                    if (win.Sent.Count != 0) return tag + "a red card without the confirmation does not launch " + win.Cmds();
                    if (win.B.Note == null || !win.B.Note.Contains(T("strike.confirm.red"))) return tag + "the question " + win.B.Note;
                    win.Frame();
                    if (win.P.Find("launch").Text != T("strike.btn.launch_confirm")) return tag + "the button asks";
                    win.Click("launch");
                    if (win.Cmds() != "strike_launch" || !win.Args[0].ContainsKey("confirm") || !(bool)win.Args[0]["confirm"]) return tag + "the second press sends confirm " + win.Cmds();
                    // another group: the confirmation is not carried over
                    // a green sink card: one press
                    var wg = new StrikeWin(1.0, 520f, SinkSample(false)); wg.Frame(); wg.Frame(); wg.Click("launch");
                    if (wg.Cmds() != "strike_launch" || wg.Args[0].ContainsKey("confirm")) return tag + "a green card launches at once " + wg.Cmds();
                    // a card that is red under the goal breach (v1) keeps the v1 flow: no extra question
                    var vb = Sample(); vb.Groups[0].Cards[0].Red = true;
                    var wb = new StrikeWin(1.0, 520f, vb); wb.Frame(); wb.Frame(); wb.Click("launch");
                    if (wb.Cmds() != "strike_launch" || wb.Args[0].ContainsKey("confirm")) return tag + "breach keeps the v1 flow " + wb.Cmds();
                    // the controller refuses a red launch without confirm: the window tells it
                    var wr = new StrikeWin(1.0, 520f, SinkSample(true)); wr.Ack = n => "!red_confirm"; wr.Frame(); wr.Frame(); wr.Click("launch"); wr.Click("launch");
                    if (wr.B.Note == null || !wr.B.Note.Contains(StrikeWindow.ErrText("!red_confirm"))) return tag + "the refusal " + wr.B.Note;
                }
            return null;
        }

        public static string DeckBlock()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var win = new StrikeWin(1.0, 520f, DeckSample());
                    win.Frame(); win.Frame();
                    if (!win.Text(T("strike.deck.title")) && !win.P.Items.Any(i => i.Text != null && i.Text.Contains(T("strike.deck.title")))) return tag + "the block title";
                    if (!win.Text(StrikeWindow.Mmss(270)) || !win.Text("Kitty Hawk")) return tag + "the row and the ready time";
                    if (!win.Text(StrikeWindow.DeckStateText("preparing")) || !win.Text(StrikeWindow.DeckStateText("launch")) || !win.Text(StrikeWindow.DeckErrText("late"))) return tag + "states and the error";
                    if (!win.Text("32") || !win.Text("48")) return tag + "the magazine";
                    if (!win.Text(StrikeWindow.DeckNoteText("deck_ordered:2:usn_a-6e:Anti-Ship Heavy:8:usn_agm-84c:Kitty Hawk:270"))) return tag + "the note of the order " + StrikeWindow.DeckNoteText("deck_ordered:2:usn_a-6e:Anti-Ship Heavy:8:usn_agm-84c:Kitty Hawk:270");
                    // a task that is launching has no cancel button; the one that is preparing has
                    if (!win.Has("deck.cancel.task:57") || win.Has("deck.cancel.task:58") || win.Has("deck.cancel.task:59")) return tag + "cancel buttons";
                    // no block: no "order again" (disabled); the group without orders and without a block shows no block at all
                    var reo = win.P.Find("deck.reorder");
                    if (reo == null || reo.Enabled) return tag + "order again is off without a block";
                    var empty = new StrikeWin(1.0, 520f, Sample()); empty.Frame(); empty.Frame();
                    if (empty.Text(T("strike.deck.title"))) return tag + "no deck block without orders";
                    // the block of the player / after a load
                    foreach (string blk in new[] { "player", "loaded" })
                    {
                        var wb = new StrikeWin(1.0, 520f, DeckSample(blk)); wb.Frame(); wb.Frame();
                        if (!wb.Text(T(blk == "loaded" ? "strike.deck.blocked.loaded" : "strike.deck.blocked.player"))) return tag + "block text " + blk;
                        var ro = wb.P.Find("deck.reorder");
                        if (ro == null || !ro.Enabled) return tag + "order again is live under a block " + blk;
                        wb.Click("deck.reorder");
                        if (wb.Cmds() != "strike_deck_reorder" || (string)wb.Args[0]["group"] != "Удар-1") return tag + "reorder " + wb.Cmds();
                    }
                    // the summary "even with every order ..."
                    var ws = new StrikeWin(1.0, 520f, DeckSample(null, true)); ws.Frame(); ws.Frame();
                    if (!ws.Text(T("strike.deck.short_all", StrikeWindow.ProbCell(0.6, true)))) return tag + "short after all";
                }
            return null;
        }

        public static string DeckCancel()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var win = new StrikeWin(1.0, 520f, DeckSample());
                    win.Frame(); win.Frame();
                    win.Click("deck.cancel.task:57");
                    if (win.Cmds() != "strike_deck_cancel" || (string)win.Args[0]["group"] != "Удар-1" || (string)win.Args[0]["key"] != "task:57") return tag + "cancel of a row " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    win.Click("deck.cancel_all");
                    if (win.Cmds() != "strike_deck_cancel" || win.Args[0].ContainsKey("key")) return tag + "cancel all sends no key " + win.Cmds();
                    // the refusal is told
                    var wr = new StrikeWin(1.0, 520f, DeckSample()); wr.Ack = n => "!launched|task:57"; wr.Frame(); wr.Frame(); wr.Click("deck.cancel.task:57");
                    if (wr.B.Note == null || !wr.B.Note.Contains(StrikeWindow.ErrText("!launched|task:57"))) return tag + "the refusal " + wr.B.Note;
                    // while the strike runs the cancel is off
                    var v = DeckSample(); v.Groups[0].Exec = "executing";
                    var we = new StrikeWin(1.0, 520f, v); we.Frame(); we.Frame();
                    var cb = we.P.Find("deck.cancel.task:57");
                    if (cb == null || cb.Enabled) return tag + "cancel is off during the strike";
                }
            return null;
        }

        public static string DeckCounts()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var win = new StrikeWin(1.0, 520f, DeckSample()); win.Frame(); win.Frame();
                    string line = T("strike.card.counts", "16", T("strike.deck.damage_only") + ", P " + StrikeWindow.ProbCell(0.42, true), "24", StrikeWindow.ProbCell(0.84, true));
                    if (!win.Text(line)) return tag + "two counts " + line;
                    var plain = new StrikeWin(1.0, 520f, SinkSample(true)); plain.Frame(); plain.Frame();
                    if (plain.Text(T("strike.card.counts", "0", "", "0", "").Substring(0, 6)) && plain.P.Items.Any(i => i.Text != null && i.Text.Contains("→"))) return tag + "no counts without a deck plan";
                    if (plain.Text(line)) return tag + "no counts without the deck";
                    // the warning of the card
                    var v = DeckSample(); v.Groups[0].Cards[0].Warnings.Add("deck_wait:270");
                    var ww = new StrikeWin(1.0, 520f, v); ww.Frame(); ww.Frame();
                    if (!ww.Text(T("strike.warn.deck_wait", StrikeWindow.Mmss(270)))) return tag + "deck_wait " + StrikeWindow.WarnText("deck_wait:270");
                }
            return null;
        }

        public static string Cards()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = Sample();
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    var c0 = v.Groups[0].Cards[0]; var c1 = v.Groups[0].Cards[1];
                    var b0 = win.P.Find("card.0"); var b1 = win.P.Find("card.1");
                    if (b0 == null || b1 == null || b0.Text != StrikeWindow.CardTitle(c0) || b0.Tone != UiTone.Accent || b1.Tone == UiTone.Accent) return tag + "cards drawn, the first chosen";
                    if (b1.Tone != UiTone.Error) return tag + "a card below the threshold is red";
                    if (!win.Text(StrikeWindow.ProbCell(0.91, true)) || !win.Text(T("strike.warn.clearing_no_depletion")) || !win.Text(StrikeWindow.WarnText("short n=3 target=100"))) return tag + "probability with the estimate mark, the clearing note, the shortage";
                    if (!win.Text(StrikeWindow.WarnText("deck_pending:1")) || !win.Text(StrikeWindow.WarnText("launch_land:2"))) return tag + "warnings";
                    if (!win.Text(T("strike.card.carrier", "A-6E 1", "AGM-84C", "4", "100"))) return tag + "carrier line";
                    if (!win.Text(T("strike.card.cleared", "101"))) return tag + "cleared by";
                    if (!StrikeWindow.ProbCell(0.91, true).Contains(lang == "ru" ? "оценка" : "estimate")) return tag + "estimate word " + StrikeWindow.ProbCell(0.91, true);
                    // Launch: on with a plan, one press sends the chosen card
                    var launch = win.P.Find("launch");
                    if (launch == null || !launch.Enabled || launch.Tip == null || !launch.Tip.Contains(T("strike.tip.launch"))) return tag + "Launch is on";
                    win.Ack = n => n == "strike_launch" ? "{\"ok\":true,\"rows\":2,\"roe\":\"veto\"}" : "{\"ok\":true}";
                    win.Click("launch");
                    if (win.Cmds() != "strike_launch" || (string)win.Args[0]["group"] != "Удар-1" || (double)win.Args[0]["variant"] != 0 || win.Args[0].ContainsKey("confirm")) return tag + "Launch sends the chosen card " + win.Cmds();
                    if (win.B.Note == null || !win.B.Note.Contains(StrikeWindow.RoeText("veto"))) return tag + "launch note " + win.B.Note;
                    win.Sent.Clear(); win.Args.Clear();
                    // a refusal of the controller is told, not hidden
                    var wr = new StrikeWin(1.0, 520f, Sample()); wr.Ack = n => "!gate|2"; wr.Frame(); wr.Frame(); wr.Click("launch");
                    if (wr.B.Note == null || !wr.B.Note.Contains(StrikeWindow.ErrText("!gate|2"))) return tag + "launch refusal " + wr.B.Note;
                    win.Click("card.1");
                    if (win.Cmds() != "strike_plan" || (double)win.Args[0]["choose"] != 1 || (string)win.Args[0]["group"] != "Удар-1") return tag + "choose " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    win.Click("card.0");   // the chosen one: no command
                    if (win.Sent.Count != 0) return tag + "the chosen card sends nothing";
                    win.Click("recalc");
                    if (win.Cmds() != "strike_plan" || win.Args[0].ContainsKey("choose")) return tag + "recalc " + win.Cmds();
                    win.Sent.Clear(); win.Args.Clear();
                    win.Click("cancel");
                    if (win.Cmds() != "strike_cancel" || (string)win.Args[0]["group"] != "Удар-1") return tag + "cancel " + win.Cmds();
                    // the recalculation says how many variants, or why none
                    var w2 = new StrikeWin(1.0, 520f, Sample());
                    w2.Ack = n => "{\"ok\":true,\"variants\":[]," + "\"why\":\"no_airborne\"}";
                    w2.Frame(); w2.Click("recalc");
                    if (w2.B.Note == null || !w2.B.Note.Contains("no_airborne")) return tag + "no variants " + w2.B.Note;
                    // a stale plan: the yellow line and the warning of the frame
                    var vs = Sample(); vs.Groups[0].Status = "stale"; vs.Groups[0].StaleWhy = "target_moved";
                    var w3 = new StrikeWin(1.0, 520f, vs); w3.Frame(); w3.Frame();
                    if (!w3.Text(T("strike.stale.line", StrikeWindow.StaleText("target_moved"))) || w3.B.Warnings(vs).Count != 1) return tag + "stale line";
                    // the timeline of the plan: mm:ss
                    var wt = new StrikeWin(1.0, 520f, Sample());
                    ((StrikeWinState)GetState(wt)).Open["strike.timeline"] = true;
                    wt.Frame(); wt.Frame();
                    if (!wt.Text(StrikeWindow.Mmss(520)) || !wt.Text(StrikeWindow.Mmss(330))) return tag + "timeline times " + string.Join("|", wt.P.Items.Where(i => i.Kind == "Label").Select(i => i.Text));
                    // no plan: the hint
                    var w5 = new StrikeWin(1.0, 520f, Sample(false)); w5.Frame(); w5.Frame();
                    if (!w5.Text(T("strike.hint.no_plan"))) return tag + "no plan hint";
                    var cancel = w5.P.Find("cancel");
                    if (cancel == null || cancel.Enabled) return tag + "Cancel is off without a plan";
                    var off = w5.P.Find("launch");
                    if (off == null || off.Enabled || off.Tip == null || !off.Tip.Contains(T("strike.launch.why.no_plan"))) return tag + "Launch is off without a plan, with the reason";
                    // a stale plan: the first press asks, the second sends with confirm
                    var ws = new StrikeWin(1.0, 520f, vs); ws.Frame(); ws.Frame();
                    var stale = ws.P.Find("launch");
                    if (stale == null || !stale.Enabled || stale.Tip == null || !stale.Tip.Contains(StrikeWindow.StaleText("target_moved"))) return tag + "stale launch tip";
                    ws.Click("launch");
                    if (ws.Sent.Count != 0 || ws.B.Note == null || !ws.B.Note.Contains(StrikeWindow.StaleText("target_moved"))) return tag + "the first press only asks " + ws.Cmds();
                    ws.Frame();
                    if (ws.P.Find("launch").Text != T("strike.btn.launch_confirm")) return tag + "the button asks for the confirmation";
                    ws.Click("launch");
                    if (ws.Cmds() != "strike_launch" || !ws.Args[0].ContainsKey("confirm") || !(bool)ws.Args[0]["confirm"]) return tag + "the second press confirms " + ws.Cmds();
                }
            return null;
        }

        private static StrikeView ExecSample()
        {
            var v = Sample();
            var g = v.Groups[0];
            g.Exec = "executing"; g.Status = "executing"; g.Roe = "veto"; g.Wave = 1; g.CanLaunch = false; g.LaunchWhy = "executing";
            g.Timeline.Clear();
            g.Timeline.Add(new StrikeTimelineRow { Uid = 1, Name = "A-6E 1", Ammo = "agm-84c", Salvo = 4, Target = 100, State = "launched", TGo = -120, TIssue = -90, TLaunch = -84, TArrive = 300, FactIssue = -90, FactLaunch = -85, LateS = 0, DtOrderS = 5, InSamNm = 12, InSamS = 95, UnderHorizon = true });
            g.Timeline.Add(new StrikeTimelineRow { Uid = 2, Name = "A-6E 2", Ammo = "agm-84c", Salvo = 4, Target = 100, State = "held", Why = "contact_lost", TGo = -100, TIssue = -80, TLaunch = -75, TArrive = 300, InSamNm = 12, InSamS = 95, UnderHorizon = false });
            g.Timeline.Add(new StrikeTimelineRow { Uid = 4, Name = "A-6E 4", Ammo = "agm-84c", Salvo = 4, Target = 101, State = "skipped", Why = "late_window", TGo = -100, TIssue = -80, TLaunch = -75, TArrive = 300 });
            g.Timeline.Add(new StrikeTimelineRow { Uid = 6, Name = "A-6E 6", Ammo = "agm-84c", Salvo = 2, Target = 101, State = "launched", TGo = -100, TIssue = -80, TLaunch = -75, TArrive = 300, FactIssue = -60, FactLaunch = -50, LateS = 31, DtOrderS = 10 });
            return v;
        }

        public static string Exec()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = ExecSample();
                    var win = new StrikeWin(1.0, 1100f, v);
                    ((StrikeWinState)GetState(win)).Open["strike.timeline"] = true;
                    win.Frame(); win.Frame();
                    var launch = win.P.Find("launch");
                    if (launch == null || launch.Enabled || launch.Tip == null || !launch.Tip.Contains(T("strike.launch.why.executing"))) return tag + "Launch is off while it goes on";
                    var cancel = win.P.Find("cancel");
                    if (cancel == null || !cancel.Enabled || !cancel.Tip.Contains(T("strike.tip.cancel_exec"))) return tag + "Cancel is on while it goes on";
                    var recalc = win.P.Find("recalc");
                    if (recalc == null || recalc.Enabled) return tag + "Recalculate is off while it goes on";
                    var rows = v.Groups[0].Timeline;
                    foreach (var t in rows) if (!win.Text(T("strike.state." + t.State))) return tag + "state text " + t.State;
                    if (!win.Text(StrikeWindow.RiskText(rows[0])) || !win.Text(StrikeWindow.RiskText(rows[1]))) return tag + "risk cells";
                    if (StrikeWindow.RiskText(rows[0]) == StrikeWindow.RiskText(rows[1])) return tag + "under / above the horizon differ";
                    if (StrikeWindow.RiskText(rows[2]) != T("strike.risk.none")) return tag + "no risk";
                    if (!StrikeWindow.RowStateText(rows[3]).Contains("31")) return tag + "late rows say how late " + StrikeWindow.RowStateText(rows[3]);
                    if (!StrikeWindow.RowStateText(rows[0]).Contains("5")) return tag + "the delay of the order";
                    if (!win.Text(T("strike.exec.roe", StrikeWindow.RoeText("veto"), "1"))) return tag + "roe line";
                    win.Click("cancel");
                    if (win.Cmds() != "strike_cancel" || (string)win.Args[0]["group"] != "Удар-1") return tag + "cancel " + win.Cmds();
                    if (win.B.Note == null || win.B.Note != T("strike.say.cancel_exec")) return tag + "cancel note " + win.B.Note;
                    // every state and reason has a text of its own
                    foreach (string st in new[] { "to_gate", "at_gate", "run_in", "held", "issued", "launched", "skipped", "cancelled", "failed" })
                        if (StrikeWindow.RowStateText(new StrikeTimelineRow { State = st }).StartsWith("strike.", StringComparison.Ordinal)) return tag + "state " + st;
                    foreach (string why in new[] { "contact_lost", "land", "hold_timeout", "target_gone", "late_window", "excluded", "released", "player", "lost", "no_launch" })
                        if (StrikeWindow.RowStateText(new StrikeTimelineRow { State = "skipped", Why = why }).Contains("strike.")) return tag + "reason " + why;
                }
            return null;
        }

        public static string Result()
        {
            foreach (string lang in Langs())
                using (UiText.Push(lang))
                {
                    string tag = lang + ": ";
                    var v = Sample();
                    var g = v.Groups[0];
                    g.Exec = "done"; g.Wave = 2; g.Status = "draft";
                    g.Results.Add(new StrikeResultRow { Id = 100, Name = "Kidd", State = "sunk", Fired = 8, Hits = 3, Arrived = 7, SpreadS = 4 });
                    g.Results.Add(new StrikeResultRow { Id = 101, Name = "Perry", State = "unknown", Fired = 4, Arrived = 3, SpreadS = 9 });
                    var win = new StrikeWin(1.0, 520f, v);
                    win.Frame(); win.Frame();
                    foreach (var r in g.Results)
                        if (!win.Text(T("strike.result.line", r.Name + " #" + r.Id, StrikeWindow.FateText(r.State), r.Fired.ToString(), r.Arrived.ToString(), StrikeWindow.Mmss(r.SpreadS)))) return tag + "result line " + r.Id;
                    if (!win.Text(T("strike.result.next_wave", "2"))) return tag + "next wave line";
                    // the cards of the next wave are there and the launch is on again
                    if (win.P.Find("card.0") == null || !win.P.Find("launch").Enabled) return tag + "cards and Launch of the next wave";
                    foreach (string st in new[] { "sunk", "damaged", "intact", "unknown" }) if (StrikeWindow.FateText(st).StartsWith("strike.", StringComparison.Ordinal)) return tag + "fate " + st;
                    // cancelled
                    g.Exec = "cancelled";
                    var w2 = new StrikeWin(1.0, 520f, v); w2.Frame(); w2.Frame();
                    if (!w2.Text(T("strike.exec.cancelled"))) return tag + "cancelled mark";
                }
            return null;
        }

        private static IUiWindowState GetState(StrikeWin w)
        {
            var f = typeof(UiKit).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (IUiWindowState)f.GetValue(w.K);
        }
    }
}
