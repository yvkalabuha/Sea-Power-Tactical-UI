// T-148 C3: layout (ru/en, scale, width, data) and clicks (which click sends which command) of the CAP window body on the RecordPainter.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static partial class CapWindowTests
    {
        private sealed class State : IUiWindowState, IUiWindowValues
        {
            public readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
            public readonly Dictionary<string, string> Vals = new Dictionary<string, string>();
            public string Value(string id, string dflt) { string v; return Vals.TryGetValue(id, out v) ? v : dflt; }
            public void SetValue(string id, string v) { Vals[id] = v; }
            public bool SectionOpen(string id, bool dflt) { bool v; return Open.TryGetValue(id, out v) ? v : dflt; }
            public void SetSectionOpen(string id, bool open) { Open[id] = open; }
        }

        private sealed class Env : ICapWindowEnv
        {
            public int Ship; public GeoPt? Ref = new GeoPt(35, 20); public double Time; public List<bool> Restores = new List<bool>(); public List<string> Logs = new List<string>();
            public int SelectedShip() { return Ship; }
            public string ShipName(int uid) { return "Kirov"; }
            public GeoPt? RefPoint() { return Ref; }
            public IList<string> LoadoutNames(string type) { return new[] { "AirToAir", "AAM8" }; }
            public string LoadoutLabel(string type, string name) { return name == "AirToAir" ? "Air to Air (4x AIM-54)" : null; }
            public double LoadoutReadyMin(string type, string name) { return name == "AirToAir" ? 15 : name == "AAM8" ? 30 : -1; }
            public void ResolveRestore(bool yes) { Restores.Add(yes); }
            public List<int> Focused = new List<int>(); public string FocusErr;
            public string Focus(int uid) { Focused.Add(uid); return FocusErr; }
            public double Now() { return Time; }
            public void Log(string m) { Logs.Add(m); }
            public List<ShipItem> Ships = new List<ShipItem> { new ShipItem { Uid = 7, Name = "Yorktown", DistNm = 4.3, Own = true, Icon = "ntds:Surface:Own" }, new ShipItem { Uid = 9, Name = "Kirov", DistNm = 12, Own = true, Icon = "ntds:Surface:Own" } };
            public List<ShipItem> OwnShips(List<double[]> geo) { foreach (var sh in Ships) geo.Add(new[] { 35.0 + sh.Uid / 100.0, 20.0 }); return new List<ShipItem>(Ships); }
            public double? Course;
            public double? ShipCourse(int uid) { return Course; }
        }

        private sealed class Sent { public string Name; public Dictionary<string, object> Args; }

        private sealed class Rig
        {
            public RecordPainter P; public UiKit K; public CapWindowBody B; public Env E = new Env(); public State S = new State();
            public List<Sent> Out = new List<Sent>(); public Func<string, string> Ack = n => "{\"ok\":true}";
            public float W; public CapView V;
            public float H;
            public Rig(double s, float w, CapView v, UiDensity d = UiDensity.Compact)
            {
                P = new RecordPainter(s); K = new UiKit(P, UiTheme.For(s, d), S, "cap"); W = w; V = v;
                B = new CapWindowBody((n, a) => { Out.Add(new Sent { Name = n, Args = a }); return Ack(n); }, E);
            }
            public void Frame()
            {
                P.NextFrame(); K.Begin(W); B.Draw(K, V); H = K.End();
            }
            public void Click(string id) { P.Click(id); Frame(); }
            /// <summary>Opens the list of a drop-down, picks the item and runs the frame that delivers the pick.</summary>
            public void Pick(string id, int item) { P.Click(id); Frame(); P.Click(id + ".i." + item); Frame(); Frame(); }
        }

        // ---------------------------------------------------------------- data
        // T-351: deterministic member uids (string.GetHashCode is randomized per process in .NET Core)
        private static readonly Dictionary<string, int> _groupBase = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int GroupBase(string name)
        {
            int b;
            if (!_groupBase.TryGetValue(name, out b)) { b = 1000 + 20 * _groupBase.Count; _groupBase[name] = b; }
            return b;
        }

        /// <summary>null, or a description of a uid shared by two members or by a member and a candidate.</summary>
        internal static string UidClash(CapView v)
        {
            var seen = new Dictionary<int, string>();
            foreach (var g in v.Groups)
                foreach (var m in g.Members)
                {
                    string other;
                    if (seen.TryGetValue(m.Uid, out other)) return "uid " + m.Uid + ": two members";
                    seen[m.Uid] = g.Line.Group;
                }
            foreach (var c in v.Candidates)
                if (seen.ContainsKey(c.Uid)) return "uid " + c.Uid + ": member of " + seen[c.Uid] + " and candidate";
            return null;
        }

        private static CapGroupView Group(string name, bool ordered, int members, string[] types, bool zone)
        {
            var g = new CapGroupView
            {
                Line = new CapGroupLine { Group = name, WNm = 40, HNm = 20, OnStation = ordered ? 2 : 0, N = 2, Ready = 2, NextReliefS = 754, Warns = ordered ? new List<string> { "continuity_gap" } : null },
                Ordered = ordered, N = 2, K = -1, Roe = "tight", Anchor = "geo", Rect = zone ? new[] { 35.0, 20.0, 35.5, 20.5 } : null,
            };
            if (members >= 20) throw new ArgumentException("members >= 20");
            for (int i = 0; i < members; i++)
                g.Members.Add(new CapMemberView { Uid = GroupBase(name) + i + 1, Name = "F-14A " + (300 + i), Type = types[i % types.Length], TypeLabel = types[i % types.Length], ShipUid = 7, ShipName = "Yorktown", Air = i % 2 == 0, Slot = ordered ? i : -1, Where = i % 2 == 0 ? "zone" : "deck_prep",
                                                  ReadyInS = 330, AamNow = 6, AamStart = 8, FuelPct = 54, LeaveInS = 4320 });
            return g;
        }

        private static CapView Data(int kind)
        {
            var v = new CapView();
            if (kind == 0) return v;
            if (kind == 3)   // the set of the plan T-155 s.1.3: three groups, the third (selected by the test) has two aircraft, nothing free
            {
                v.Groups.Add(Group("ПВО-1", true, 6, new[] { "F-14A" }, true)); v.Groups.Add(Group("ПВО-2", true, 4, new[] { "F-14A" }, true)); v.Groups.Add(Group("ПВО-3", false, 2, new[] { "F-14A" }, false));
                return v;
            }
            if (kind == 4)   // nothing free, three busy: the line with the reasons
            {
                v.Groups.Add(Group("ПВО-1", true, 2, new[] { "F-14A" }, true));
                v.Candidates.Add(new CapCandidateView { Uid = 9201, Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "hangar", Reason = "in_group:ПВО-1" });
                v.Candidates.Add(new CapCandidateView { Uid = 9202, Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "hangar", Reason = "in_group:ПВО-1" });
                v.Candidates.Add(new CapCandidateView { Uid = 9203, Name = "Invader 4", Type = "usn_f-14a", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "zone", Air = true, Reason = "player" });
                return v;
            }
            if (kind == 1)
            {
                v.Groups.Add(Group("ПВО-1", true, 2, new[] { "F-14A" }, true)); v.Groups.Add(Group("ПВО-2", true, 2, new[] { "F-14A", "F-4J" }, true)); v.Groups.Add(Group("ПВО-3", false, 2, new[] { "F-4J" }, false));
                v.Candidates.Add(new CapCandidateView { Uid = 9001, Name = "F-14A 401", Type = "F-14A", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "hangar" });
                v.Candidates.Add(new CapCandidateView { Uid = 9002, Name = "F-14A 402", Type = "F-14A", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "deck_ready" });
                v.Candidates.Add(new CapCandidateView { Uid = 9003, Name = "E-2C 1", Type = "E-2C", Where = "hangar", Reason = "hidden" });
                v.Candidates.Add(new CapCandidateView { Uid = 9004, Name = "F-4J 9", Type = "F-4J", Where = "zone", Reason = "in_group:ПВО-1" });
                return v;
            }
            for (int i = 0; i < 8; i++)
            {
                var g = Group("Патруль воздушной обороны авианосной группы " + (i + 1), i % 2 == 0, 4, new[] { "F-14A Tomcat Block 135", "F-4J" }, true);
                v.Groups.Add(g);
            }
            for (int i = 0; i < 8; i++) v.Candidates.Add(new CapCandidateView { Uid = 8000 + i, Name = "F-14A Tomcat eskadrilya " + i, Type = "F-14A Tomcat Block 135", Where = "deck_prep", Reason = i % 3 == 0 ? "in_group:Патруль воздушной обороны авианосной группы 1" : null });
            return v;
        }

        // ---------------------------------------------------------------- layout
        public static string Layout()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                    foreach (UiDensity den in new[] { UiDensity.Compact, UiDensity.Normal })
                        foreach (double s in new[] { 1.0, 1.33 })
                            foreach (float bw in den == UiDensity.Compact ? new[] { 420f, 500f, 760f } : new[] { 500f, 580f, 760f })
                                for (int kind = 0; kind < 5; kind++)
                                    foreach (bool all in new[] { false, true })
                                    {
                                        float w = (float)(bw * s);
                                        var cv = Data(kind);
                                        string clash = UidClash(cv); if (clash != null) return lang + " data=" + kind + ": " + clash;
                                        var r = new Rig(s, w, cv, den);
                                        if (all) { r.S.Open["cap.thresholds"] = true; r.S.Open["cap.loadout"] = true; }
                                        string tag = lang + " " + den + " s=" + s + " w=" + bw + " data=" + kind + (all ? " all" : "");
                                        if (kind == 1 && all) { r.E.Ship = 7; r.Frame(); r.Click("cap.anchor"); }
                                        if (kind == 3) { r.Frame(); r.Click("cap.grp.2"); }
                                        r.Frame(); r.Frame();
                                        float maxH = kind == 2 && all ? 100000f : (float)(0.95 * 1080 * s);   // the stress case with every section open scrolls by design
                                        bool dense = den == UiDensity.Compact && kind == 3 && !all && bw == 500f;
                                        if (dense) maxH = (float)(640 * s);   // T-175: the alert row; T-178: the compass is a part of the form
                                        string bad = r.P.CheckLayout(w, maxH);
                                        if (bad != null) return tag + ": " + bad;
                                        if (kind < 2 && r.K.Overflows.Count > 0) return tag + ": overflow " + r.K.Overflows[0];
                                        if (kind == 0 && r.P.FindText(UiText.Get("cap.hint.no_groups")) == null) return tag + ": no hint for the empty window";
                                        // no "Excluded" section at all (ADR-0052)
                                        foreach (var it in r.P.Items)
                                            if (it.Id != null && (it.Id.StartsWith("cap.excl", StringComparison.Ordinal) || it.Id.IndexOf("excluded", StringComparison.Ordinal) >= 0)) return tag + ": excluded section " + it;
                                        // the loadout is closed by default and says what is chosen in the bar
                                        if (kind == 1 && !all)
                                        {
                                            if (r.P.Find("cap.lo.F-14A.next") != null) return tag + ": loadout is open by default";
                                            if (r.P.FindText("F-14A: " + UiText.Get("cap.ui.loadout_auto")) == null) return tag + ": no loadout summary in the bar";
                                        }
                                        if (kind == 4)
                                        {
                                            string why = CapWindow.In(lang, () => CapWindowModel.RefusalSummary(new List<CapCandidateView> { new CapCandidateView { Reason = "in_group:x" }, new CapCandidateView { Reason = "in_group:x" }, new CapCandidateView { Reason = "player" } }));
                                            var line = r.P.FindText(why);
                                            if (line == null) return tag + ": no reasons line '" + why + "'";
                                            if (line.Tip == null || !line.Tip.Contains("Invader 4") || line.Tip.Split('\n').Length < 3 || !line.Tip.Contains("F-14A\u00b7")) return tag + ": reasons tip " + line.Tip;
                                        }
                                        if (kind == 3 && !all && den == UiDensity.Compact)
                                        {
                                            // the task: four form rows (count, ROE, zone, bearing) and the row of buttons; the bearing row is one line at the default width
                                            var n = r.P.Find("cap.n"); var roe = r.P.Find("cap.roe.0"); var zs = r.P.Find("cap.zsrc.0"); var br = r.P.Find("cap.bearing.p.0"); var dist = r.P.Find("cap.dist"); var ap = r.P.Find("start");
                                            if (n == null || roe == null || zs == null || br == null || dist == null || ap == null) return tag + ": task controls missing";
                                            if (!(n.R.Y < roe.R.Y && roe.R.Y < zs.R.Y && zs.R.Y < br.R.Y && br.R.Y < dist.R.Y && dist.R.Y < ap.R.Y)) return tag + ": task rows not in order";
                                            if (bw >= 500f && s == 1.0)
                                            {
                                                var sz = r.P.Find("cap.size"); var wd = r.P.Find("cap.width");
                                                if (sz == null || wd == null || Math.Abs(sz.R.Y - wd.R.Y) > 1f) return tag + ": size and width are not on one row";
                                            }
                                            foreach (var it in r.P.Items) if (it.Kind == "Field") return tag + ": a text field in the CAP window: " + it.Id;
                                        }
                                    }
            return null;
        }

        // ---------------------------------------------------------------- clicks
        private static string Expect(Rig r, string what, params string[] names)
        {
            if (r.Out.Count != names.Length) return what + ": expected " + names.Length + " commands, got " + r.Out.Count + (r.Out.Count > 0 ? " first " + r.Out[0].Name : "");
            for (int i = 0; i < names.Length; i++) if (r.Out[i].Name != names[i]) return what + ": command " + i + " is " + r.Out[i].Name + ", expected " + names[i];
            return null;
        }

        // ---------------------------------------------------------------- T-200: foreign hangar slots in "Available"
        private static CapView T200Data(bool withFree)
        {
            var v = new CapView();
            v.Groups.Add(Group("ПВО-1", true, 2, new[] { "F-14A" }, true));
            if (withFree) v.Candidates.Add(new CapCandidateView { Uid = 9301, Name = "F-14A·1", Type = "F-14A", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "hangar", HangarK = 1, HangarN = 2 });
            v.Candidates.Add(new CapCandidateView { Uid = 9302, Name = "F-14A·2", Type = "F-14A", TypeLabel = "F-14A", ShipUid = 7, ShipName = "Yorktown", Where = "hangar", HangarK = 2, HangarN = 2, Reason = "in_group:ASW-2", OwnerWindow = "asw" });
            v.Candidates.Add(new CapCandidateView { Uid = 9303, Name = "F-14A 7", Type = "F-14A", Where = "zone", Air = true, Reason = "in_group:ПВО-1" });
            v.Candidates.Add(new CapCandidateView { Uid = 9304, Name = "Invader", Type = "F-14A", Where = "zone", Air = true, Reason = "player" });
            return v;
        }

        public static string T200Available()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    var v = T200Data(true);
                    string clash = UidClash(v); if (clash != null) return lang + ": " + clash;
                    var rows = CapWindowModel.AvailableRows(v);
                    if (rows.Count != 2 || rows[0].Uid != 9301 || rows[1].Uid != 9302) return lang + ": rows " + rows.Count;
                    if (!CapWindowModel.ForeignSlot(rows[1]) || CapWindowModel.ForeignSlot(rows[0]) || CapWindowModel.ForeignSlot(v.Candidates[2])) return lang + ": ForeignSlot";
                    string want = lang == "ru" ? "в группе ASW-2" : "in group ASW-2";
                    if (CapWindowModel.ForeignCells(rows[1], "x")[CapWindowModel.CandidateWhere] != want) return lang + ": ForeignCells " + CapWindowModel.ForeignCells(rows[1], "x")[CapWindowModel.CandidateWhere];
                    var free = new List<CapCandidateView>(); var excl = new List<CapCandidateView>();
                    CapWindowModel.SplitCandidates(v, free, excl);
                    var bad = CapWindowModel.Refused(new HashSet<int> { 9302 }, free, excl);
                    if (bad.Count != 1 || bad[0].Uid != 9302) return lang + ": Refused";
                    // drawn: two rows, the foreign one is dim and cannot be taken
                    var r = new Rig(1.0, 580, v); r.Frame();
                    if (r.P.Find("cap.avail.row.1") == null || r.P.Find("cap.avail.row.2") != null) return lang + ": table rows";
                    var cell = r.P.FindText(want);
                    if (cell == null || cell.Tone != UiTone.Dim) return lang + ": foreign row not dim";
                    r.Click("cap.avail.row.1");
                    if (r.B.Note == null || !r.B.Note.Contains(want) || r.B.NoteTone != UiTone.Warn) return lang + ": click note " + r.B.Note;
                    r.Click("cap.avail.row.0");
                    r.Click("cap.avail.row.1");
                    r.Click("add");
                    foreach (var o in r.Out) if (o.Args != null && o.Args.ContainsKey("uids") && ((List<object>)o.Args["uids"]).Exists(x => (double)x == 9302)) return lang + ": the foreign uid was sent";
                    // nothing free, foreign slots only: the reasons line and the table
                    var r2 = new Rig(1.0, 580, T200Data(false)); r2.Frame();
                    if (r2.P.Find("cap.avail.row.0") == null) return lang + ": no table without free machines";
                    if (r2.P.Items.Exists(it => it.Text != null && it.Text.Contains(UiText.Get("cap.ui.no_aircraft")))) return lang + ": empty hint";
                }
            return null;
        }

        // T-325: the "Loadout" column of "Available" and the dimmed strike-loadout rows (a click only says why)
        public static string T325Loadout()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    var v = T200Data(true);
                    string clash = UidClash(v); if (clash != null) return lang + ": " + clash;
                    v.Candidates[0].LoadoutText = "AIM-9\u00D72";
                    v.Candidates.Add(new CapCandidateView { Uid = 9305, Name = "Hornet", Type = "F-14A", Where = "to_zone", Air = true, Reason = "strike_loadout", LoadoutText = "AIM-9\u00D72, Mk82\u00D74" });
                    string why = UiText.Get("cap.reason.strike_loadout");
                    var r = new Rig(1.0, 700, v); r.Frame();
                    if (r.P.FindText(UiText.Get("cap.col.loadout")) == null) return lang + ": no loadout column";
                    if (r.P.FindText("AIM-9\u00D72, Mk82\u00D74") == null || r.P.FindText("AIM-9\u00D72") == null) return lang + ": no loadout text";
                    var cell = r.P.Items.Find(it => it.Text != null && it.Text.Contains(why) && it.Text.Contains(" \u00B7 "));
                    if (cell == null || cell.Tone != UiTone.Dim) return lang + ": strike row not dim";
                    int idx = -1;
                    var rows = CapWindowModel.AvailableRows(v);
                    for (int i = 0; i < rows.Count; i++) if (rows[i].Uid == 9305) idx = i;
                    if (idx < 0) return lang + ": strike row missing";
                    r.Click("cap.avail.row." + idx);
                    if (r.B.Note == null || !r.B.Note.Contains(why) || r.B.NoteTone != UiTone.Warn) return lang + ": click note " + r.B.Note;
                    r.Click("add");
                    foreach (var o in r.Out) if (o.Args != null && o.Args.ContainsKey("uids") && ((List<object>)o.Args["uids"]).Exists(x => (double)x == 9305)) return lang + ": the strike uid was sent";
                }
            return null;
        }

        public static string Clicks()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string e = ClicksIn(lang);
                    if (e != null) return lang + ": " + e;
                }
            return null;
        }

        // T-155 C3: links centre the map without touching the ticks; the row toggles the tick; refusals say why
        private static string LinkClicks()
        {
            string e;
            var r = new Rig(1.0, 500, Data(1)); r.Frame();
            var g = r.V.Groups[0]; var air = g.Members[0]; var deck = g.Members[1];
            if (!air.Air || deck.Air) return "links: test data";
            r.Click("cap.members.link." + 0 + ".0");
            if (r.E.Focused.Count != 1 || r.E.Focused[0] != air.Uid) return "links: aircraft in the air -> its uid, got " + string.Join(",", r.E.Focused);
            r.Click("cap.members.link.1.0");
            if (r.E.Focused.Count != 2 || r.E.Focused[1] != 7) return "links: aircraft on a deck -> its ship";
            r.Click("cap.members.link.0.1");
            if (r.E.Focused.Count != 3 || r.E.Focused[2] != 7) return "links: ship -> ship";
            r.Click("remove");
            if ((e = Expect(r, "links did not tick anything")) != null) return e;
            if (r.P.Find("cap.members.chk." + air.Uid) == null || r.P.Find("cap.members.chk." + air.Uid).Selected) return "links: ticked";
            // the rest of the row ticks, no centring
            r.Click("cap.members.row.0");
            r.Click("remove");
            if ((e = Expect(r, "row click then remove", "cap_group")) != null) return e;
            if (((List<object>)r.Out[0].Args["uids"]).Count != 1 || (double)((List<object>)r.Out[0].Args["uids"])[0] != air.Uid) return "row click: the picked uid";
            if (r.E.Focused.Count != 3) return "row click centred the map";
            // the link of the available list
            r.Click("cap.avail.link.0.0");
            if (r.E.Focused.Count != 4 || r.E.Focused[3] != 7) return "available link: deck aircraft -> ship";
            // a map error goes to the hint line as a warning
            r = new Rig(1.0, 500, Data(1)); r.E.FocusErr = "Map closed"; r.Frame();
            r.Click("cap.members.link.0.1");
            if (r.B.Note != "Map closed" || r.B.NoteTone != UiTone.Warn) return "focus error note: " + r.B.Note;
            // a deck aircraft whose ship is unknown: nothing to show, a warning, the ship cell is not a link
            var lone = Data(1); lone.Groups[0].Members[1].ShipUid = 0; lone.Groups[0].Members[1].ShipName = null;
            r = new Rig(1.0, 500, lone); r.Frame();
            if (r.P.Find("cap.members.link.1.1") != null) return "no ship: the cell is a link";
            r.Click("cap.members.link.1.0");
            if (r.E.Focused.Count != 0 || r.B.NoteTone != UiTone.Warn) return "no ship: aircraft link";
            return null;
        }

        // 0.28.1: the loadout rows name the type as the game shows it (F-14A, not usn_f-14a) and the loadout by its display name
        private static string LoadoutNames()
        {
            var v = new CapView();
            var g = Group("ПВО-1", true, 2, new[] { "usn_f-14a" }, true);
            foreach (var m in g.Members) m.TypeLabel = "F-14A";
            v.Groups.Add(g);
            var r = new Rig(1.0, 580, v); r.S.Open["cap.loadout"] = true; r.Frame(); r.Frame();
            foreach (var it in r.P.Items) if (it.Text != null && it.Text.Contains("usn_f-14a")) return "loadout: raw type id shown: " + it;
            if (r.P.FindText("F-14A") == null) return "loadout: no type label row";
            r.Click("cap.lo.usn_f-14a.next"); r.Frame();
            if (r.P.FindText("Air to Air (4x AIM-54)") == null) return "loadout: no display name of the picked loadout";
            r.Click("cap.lo.usn_f-14a.next"); r.Frame();
            if (r.P.FindText("AAM8") == null) return "loadout: a loadout without a display name shows its name";
            return null;
        }

        private static string Refusals()
        {
            string e;
            // nothing free, three busy: [+] and "To group" send nothing, say why, write the journal
            foreach (string btn in new[] { "new", "add" })
            {
                var r = new Rig(1.0, 500, Data(4)); r.Frame();
                r.Click(btn);
                if ((e = Expect(r, "refused " + btn)) != null) return e;
                if (r.B.Note == null || !r.B.Note.StartsWith(UiText.Fmt("cap.say.refused", "").Trim(), StringComparison.Ordinal) || r.B.NoteTone != UiTone.Warn) return "refused note " + btn + ": " + r.B.Note;
                if (!r.B.Note.Contains("F-14A\u00b7") || !r.B.Note.Contains(UiText.Get("cap.reason.in_group").Replace("{0}", "ПВО-1"))) return "refused note text: " + r.B.Note;
                if (!r.E.Logs.Contains("cap window refused 9201 reason=in_group:ПВО-1") || !r.E.Logs.Contains("cap window refused 9203 reason=player")) return "refused journal: " + string.Join("|", r.E.Logs);
            }
            // nothing at all: the button is grey and its tip is the answer
            var z = new Rig(1.0, 500, Data(0)); z.Frame();
            if (z.P.Find("new") == null || z.P.Find("new").Enabled) return "[+] with no aircraft at all is not grey";
            // a picked aircraft that became unavailable: refused, the stale pick is dropped, the next click goes through
            var s = new Rig(1.0, 500, Data(1)); s.Frame();
            s.Click("cap.avail.chk.9001"); s.Click("cap.avail.chk.9002");
            s.V.Candidates[0].Reason = "player";
            s.Frame();
            s.Click("new");
            if ((e = Expect(s, "stale pick refused")) != null) return e;
            if (!s.E.Logs.Contains("cap window refused 9001 reason=player")) return "stale pick journal: " + string.Join("|", s.E.Logs);
            s.Click("new");
            if ((e = Expect(s, "after the refusal", "cap_group")) != null) return e;
            if (((List<object>)s.Out[0].Args["uids"]).Count != 1) return "after the refusal: uids";
            return null;
        }

        private static string ClicksIn(string lang)
        {
            string e;
            var zone = new[] { 36.0, 21.0, 36.5, 21.5 };
            // 1. editing the draft sends nothing
            var r = new Rig(1.0, 580, Data(1));
            r.Frame();
            r.Pick("cap.n", 2);        // 1..12: item 2 = 3
            r.Pick("cap.k", 2);        // Auto, 0, 1: item 2 = 1
            r.S.Open["cap.loadout"] = true; r.Frame();
            r.Click("cap.roe.1");
            r.Click("cap.lo.F-14A.next");
            r.Click("cap.draw");
            if (!r.B.WantRect) return "draw mode not on";
            r.B.OnRect(zone);
            r.Frame();
            r.Click("cap.anchor");
            if ((e = Expect(r, "draft edits")) != null) return e;
            // 2. apply: one order that carries the loadout (T-175, ADR-0075 item 8)
            r.Click("apply");
            if ((e = Expect(r, "apply", "cap_order")) != null) return e;
            string perr;
            var po = CapCommands.ParseOrder(r.Out[0].Args, out perr);
            if (po == null || po.Loadouts == null || po.Loadouts.Count != 1 || po.OnStation != 3 || po.Ready != 1 || po.Roe != "free" || po.Zone.Rect == null || Math.Abs(po.Zone.Rect[0] - 36.0) > 1e-9) return "apply order content " + perr;
            // 3. unchanged draft: apply is grey, the click says why
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("apply");
            if ((e = Expect(r, "grey apply")) != null) return e;
            if (r.B.Note != UiText.Get("cap.hint.same")) return "grey apply note: " + r.B.Note;
            // 4. only ROE changed: one cap_order with the zone of the group; the "Changed: ... press Apply" line has reserved room:
            //    the buttons stay where they were before the edit (0.28.1)
            float stopY0 = r.P.Find("stop").R.Y, applyY0 = r.P.Find("apply").R.Y;
            r.Click("cap.roe.2"); r.Frame();
            if (r.P.Of("Label").Find(it => it.Tone == UiTone.Warn) == null) return "no dirty hint after an edit";
            if (Math.Abs(r.P.Find("stop").R.Y - stopY0) > 0.01f || Math.Abs(r.P.Find("apply").R.Y - applyY0) > 0.01f) return "buttons moved when the dirty hint appeared: " + stopY0 + " -> " + r.P.Find("stop").R.Y;
            r.Click("apply");
            if ((e = Expect(r, "roe only", "cap_order")) != null) return e;
            // 5. start of a group without a zone: grey with the reason; then a drawn zone
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("cap.grp.2"); r.Frame();
            // T-178 C5: a group without an order starts with a zone by bearing: "Start" works at once
            if (r.P.Find("start") == null || !r.P.Find("start").Enabled) return "first open: Start is not available";
            r.Click("cap.zsrc.0");   // the player chose "Draw": no zone yet
            r.Click("start");
            if ((e = Expect(r, "start without zone")) != null) return e;
            if (r.B.Note != UiText.Get("cap.hint.need_zone")) return "start note: " + r.B.Note;
            r.B.OnRect(zone);   // Q2: a group without an order gets the order at once
            if ((e = Expect(r, "zone drawn for an unordered group", "cap_order")) != null) return e;
            if (r.B.DraftZone != null) return "draft zone after the auto order";
            r.Frame(); r.Click("start");   // the stand's view still shows the group without an order: the refilled draft (zone by bearing) is startable
            if ((e = Expect(r, "start after the auto order", "cap_order", "cap_order")) != null) return e;
            // 5b. zone by bearing: the origin is the group's carrier (Yorktown, uid 7), not the ship selected in the game (99); the window names it
            r = new Rig(1.0, 580, Data(1)); r.E.Ship = 99; r.Frame();
            if (r.P.FindText(UiText.Fmt("cap.ui.origin", "Yorktown")) == null) return "origin ship is not named in the window";
            r.Click("cap.zsrc.1");
            r.Click("cap.bearing.p.2"); r.Pick("cap.dist", 2); r.Pick("cap.size", 1);   // East, 30 miles, 20 miles
            r.Click("apply");
            if ((e = Expect(r, "polar from the carrier", "cap_order")) != null) return e;
            if ((double)r.Out[0].Args["bearing"] != 90 || (double)r.Out[0].Args["dist_nm"] != 30 || (double)r.Out[0].Args["size_nm"] != 20) return "polar order values: " + r.Out[0].Args["bearing"] + "/" + r.Out[0].Args["dist_nm"] + "/" + r.Out[0].Args["size_nm"];
            if (!r.Out[0].Args.ContainsKey("origin_uid") || (double)r.Out[0].Args["origin_uid"] != 7) return "polar origin is not the carrier: " + (r.Out[0].Args.ContainsKey("origin_uid") ? r.Out[0].Args["origin_uid"] : "none");
            // 6. the answer of a rejected command goes to the note
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("cap.roe.1");
            r.Ack = n => "!no_zone";
            r.Click("apply");
            if ((e = Expect(r, "rejected", "cap_order")) != null) return e;
            if (r.B.NoteTone != UiTone.Error || r.B.Note == null || !r.B.Note.Contains("no_zone")) return "rejected note: " + r.B.Note;
            // 7. stop
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("stop");
            if ((e = Expect(r, "stop group", "cap_stop")) != null) return e;
            if ((string)r.Out[0].Args["group"] != "ПВО-1") return "stop group arg";
            r.Out.Clear(); r.Click("stop_all");
            if ((e = Expect(r, "stop all", "cap_stop")) != null) return e;
            if (!r.Out[0].Args.ContainsKey("all") || r.Out[0].Args.ContainsKey("group")) return "stop all arg";
            // 8. thresholds
            r = new Rig(1.0, 580, Data(1)); r.S.Open["cap.thresholds"] = true; r.Frame();
            r.Click("cap.thr.missile.inc");
            r.Click("apply_thr");
            if ((e = Expect(r, "thresholds", "cap_params")) != null) return e;
            if (Math.Abs((double)r.Out[0].Args["missile_pct"] - 0.55) > 1e-9) return "thresholds: missile_pct " + r.Out[0].Args["missile_pct"];
            // 9. groups: new / disband / remove / add
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("new");
            if ((e = Expect(r, "new without picks")) != null) return e;
            if (r.B.Note != UiText.Get("cap.ui.err.missing_uids")) return "new without picks note: " + r.B.Note;
            r.Click("cap.avail.chk.9001"); r.Click("cap.avail.chk.9002");
            r.Click("new");
            if ((e = Expect(r, "new", "cap_group")) != null) return e;
            if ((string)r.Out[0].Args["op"] != "create" || ((List<object>)r.Out[0].Args["uids"]).Count != 2) return "new args";
            r.Out.Clear(); r.Frame();
            r.Click("cap.avail.chk.9001");
            r.Click("add");
            if ((e = Expect(r, "add", "cap_group")) != null) return e;
            if ((string)r.Out[0].Args["op"] != "add" || (string)r.Out[0].Args["group"] != "ПВО-1") return "add args";
            r.Out.Clear();
            r.Click("disband");
            if ((e = Expect(r, "disband", "cap_group")) != null) return e;
            if ((string)r.Out[0].Args["op"] != "disband") return "disband args";
            r.Out.Clear();
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            r.Click("remove");
            if ((e = Expect(r, "remove without picks")) != null) return e;
            r.Click("cap.members.chk." + r.V.Groups[0].Members[0].Uid);
            r.Click("remove");
            if ((e = Expect(r, "remove", "cap_group")) != null) return e;
            if ((string)r.Out[0].Args["op"] != "remove") return "remove args";
            // 10. restore question
            r = new Rig(1.0, 580, Data(1)); r.V.RestorePending = true; r.Frame();
            r.Click("restore.yes");
            if (r.E.Restores.Count != 1 || !r.E.Restores[0]) return "restore yes";
            if (r.Out.Count != 0) return "restore sends a command";
            if ((e = LinkClicks()) != null) return e;
            if ((e = Refusals()) != null) return e;
            if ((e = LoadoutNames()) != null) return e;
            // 11. polar zone from a ship: no carrier (no deck data), no selected ship and no reference point; then the selected ship is the fallback
            var nodeck = Data(1); foreach (var m in nodeck.Groups[0].Members) { m.ShipUid = 0; m.ShipName = null; }
            r = new Rig(1.0, 580, nodeck); r.E.Ref = null; r.Frame();
            if (r.P.FindText(UiText.Fmt("cap.ui.origin", UiText.Get("cap.ui.origin_view"))) == null) return "no origin: the window does not say map centre";
            r.Click("cap.zsrc.1"); r.Click("apply");
            if ((e = Expect(r, "polar without origin")) != null) return e;
            r.E.Ship = 5; r.Click("apply");
            if ((e = Expect(r, "polar with ship", "cap_order")) != null) return e;
            var pp = CapCommands.ParseOrder(r.Out[0].Args, out perr);
            if (pp == null || !pp.Polar || pp.OriginUid != 5) return "polar order " + perr;
            // 12. the summary and the warnings
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            if (r.B.Summary(r.V) != UiText.Fmt("cap.sum.group", "ПВО-1", 2, 2)) return "summary: " + r.B.Summary(r.V);
            if (r.B.Warnings(r.V).Count != 1) return "warnings";
            if (new Rig(1.0, 580, Data(0)).B.Summary(Data(0)) != UiText.Get("cap.sum.none")) return "summary without groups";
            // 13. T-154 live state: a group without an order (Rect empty, hangar, no loadouts) -> no_order line; the zone goes out at once (Q2); the log has the lines
            var live = new CapView();
            var lg = Group("CAP-3", false, 2, new[] { "F-4J" }, false);
            lg.Rect = new double[0]; lg.Loadouts.Clear(); foreach (var m in lg.Members) m.Where = "hangar";
            live.Groups.Add(lg);
            r = new Rig(1.0, 580, live); r.Frame();
            string line = CapWindow.TaskLine(lg.Line, lg.Ordered);
            if (!line.Contains(UiText.Fmt("cap.line.no_order", "CAP-3").Substring(0, 5)) || line.Contains("0×0") || line.Contains("0x0")) return "no order line: " + line;
            r.Click("cap.zsrc.0"); r.Click("cap.draw");
            r.B.OnRect(zone);
            if ((e = Expect(r, "live: zone drawn", "cap_order")) != null) return e;
            if (!r.E.Logs.Exists(l => l.Contains("cap window zone drawn"))) return "log: zone drawn";
            if (!r.E.Logs.Exists(l => l.Contains("cap_order"))) return "log: cap_order";
            // 13b. no carrier, no reference point and a polar zone: the error is logged, nothing sent
            r = new Rig(1.0, 580, nodeck); r.E.Ref = null; r.Frame();
            r.Click("cap.zsrc.1"); r.Click("apply");
            if (!r.E.Logs.Exists(l => l.Contains("press apply"))) return "log: press";
            if (!r.E.Logs.Exists(l => l.Contains("err no_origin"))) return "log: err no_origin: " + string.Join("|", r.E.Logs);
            // 14. an ordered group keeps the draft: the drawn zone is a DraftZone until Apply
            r = new Rig(1.0, 580, Data(1)); r.Frame();
            if (r.B.DraftZone != null) return "draft zone at start";
            r.Click("cap.draw"); r.B.OnRect(zone); r.Frame();
            if ((e = Expect(r, "ordered group: draw only")) != null) return e;
            if (r.B.DraftZone == null || Math.Abs(r.B.DraftZone[0] - 36.0) > 1e-9) return "draft zone missing";
            r.Click("cap.grp.2"); r.Frame();
            if (r.B.DraftZone != null) return "draft zone after a group change";
            r.Click("cap.grp.1"); r.Frame();
            r.Click("cap.draw"); r.B.OnRect(zone); r.Frame();
            r.Click("apply");
            if ((e = Expect(r, "apply of the drawn zone", "cap_order")) != null) return e;
            if (r.B.DraftZone != null) return "draft zone after apply";
            return null;
        }

        // ---------------------------------------------------------------- T-175: ROE lines, loadout readiness (the alert pair itself moved to T-179, CapAlertWindowTests.cs)
        private static CapView AlertData(bool airOnly, string roe, int alert, bool down)
        {
            var v = new CapView();
            var g = Group("ПВО-1", true, 3, new[] { "F-14A" }, true);
            g.Roe = roe; g.AirOnly = airOnly;
            if (down) g.Line.Warns = new List<string> { "alert_unavailable" };
            foreach (var m in g.Members) m.TypeLabel = "F-14A";
            v.Groups.Add(g);
            return v;
        }

        public static string RoeLines()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string e = RoeLinesIn();
                    if (e != null) return lang + ": " + e;
                }
            return null;
        }

        private static string RoeLinesIn()
        {
            // the lines: free warning, air-only off
            var rw = new Rig(1.0, 580, AlertData(false, "free", 0, false)); rw.Frame(); rw.Frame();
            if (rw.P.FindText(UiText.Get("cap.ui.roe_free_warn")) == null) return "no free warning";
            if (rw.P.FindText(UiText.Get("cap.ui.air_only_off")) == null) return "no air-only-off line";
            var rt = new Rig(1.0, 580, AlertData(true, "tight", 0, false)); rt.Frame(); rt.Frame();
            if (rt.P.FindText(UiText.Get("cap.ui.roe_free_warn")) != null || rt.P.FindText(UiText.Get("cap.ui.air_only_off")) != null) return "warnings without a reason";
            // readiness of the loadouts
            var rl = new Rig(1.0, 580, AlertData(true, "tight", 0, false)); rl.S.Open["cap.loadout"] = true; rl.Frame(); rl.Frame();
            rl.Click("cap.lo.F-14A.next"); rl.Frame();
            if (!rl.P.Items.Exists(it => it.Text != null && it.Text.Contains(UiText.Fmt("cap.ui.loadout_ready", "15")))) return "no readiness text: " + string.Join(" | ", rl.P.Items.FindAll(it => it.Text != null && it.Text.Contains("AAM")).ConvertAll(it => it.Text).ToArray());
            return null;
        }
    }
}
