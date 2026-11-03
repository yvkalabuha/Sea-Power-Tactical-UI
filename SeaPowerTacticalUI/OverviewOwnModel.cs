using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    // T-242 (ADR-0101): the "Own" tab of the Overview window. Pure model: raw facts of an own unit -> ready cell texts; no Unity, no game types.

    internal enum OwnKind { Surface, Submarine, Air, Helicopter }

    /// <summary>Raw facts of one own unit read by the game side (OverviewOwnGame.cs).</summary>
    internal sealed class OwnUnitFacts
    {
        public int Uid; public OwnKind Kind;
        public string Name, ClassName, TypeAbbr;          // Name.Value, ClassNameShort, TypeAbbreviation
        public string Feedback, OrderText;                // OrderFeedbackText / CurrentOrderText; null = unknown
        public double AltFt = double.NaN, SpeedKn = double.NaN;
        public bool InFlight = true;                      // air / helicopter only (_isInFlight)
        public string Loadout;                            // _obp._loadoutVariant, air only
        public string ModGroup;                           // AirOwnership.Owner(uid), null = none
        public bool InFormation, FormationLeader;
        public string FormationName, FormationLeaderName; // UnitFormation.Name, the leader's Name.Value
        public bool FormationUnnamed;                     // the name is empty or the game's "Unnamed formation"
    }

    /// <summary>One table row: every cell is ready text ("—" for unknown), so that OnGUI only draws.</summary>
    internal sealed class OwnRow
    {
        public int Uid; public OwnKind Kind;
        public string Icon;                               // "Surface/Allied" | "Submarine/Allied" | "Air/Allied" | "Helicopter/Allied"
        public string Type, Name, Task, Alt, Spd, Load, Grp;
        public bool Leader, ModGrp;
        public double SortAlt = double.NaN, SortSpd = double.NaN;
        public string SortGrp;                            // group text + "\u0001" + (leader ? "0" : "1") + name; null = no group (last)
    }

    internal sealed class OwnFrame
    {
        public List<OwnRow> Rows = new List<OwnRow>();
        public int Ships, Subs, Air;
        public double BuildMs; public int Version;
    }

    internal static class OverviewTabs
    {
        public const string Contacts = "contacts", Own = "own";
        /// <summary>Anything but "own" is the contacts tab.</summary>
        public static string Parse(string s) { return s != null && s.Trim().ToLowerInvariant() == Own ? Own : Contacts; }
    }

    internal static class OverviewOwnColumns
    {
        public static readonly string[] All = { "icon", "type", "name", "task", "alt", "spd", "load", "grp" };
        public const string DefaultSpec = "icon,type,name,task,alt,spd,load,grp";
        public const string DefaultSort = "grp:asc";
        private const string Dash = "—";

        /// <summary>Same error texts as OverviewColumns.ParseColumns; icon and name are required.</summary>
        public static string[] ParseColumns(string spec, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) { error = "columns: empty"; return null; }
            var parts = spec.Split(',');
            var cols = new List<string>();
            foreach (var p in parts) { string c = p.Trim().ToLowerInvariant(); if (c.Length > 0) cols.Add(c); }
            foreach (var c in cols) if (Array.IndexOf(All, c) < 0) { error = "columns: unknown '" + c + "'"; return null; }
            if (!cols.Contains("icon") || !cols.Contains("name")) { error = "columns: icon and name are required"; return null; }
            var seen = new HashSet<string>();
            foreach (var c in cols) if (!seen.Add(c)) { error = "columns: duplicate"; return null; }
            return cols.ToArray();
        }

        /// <summary>false = bad spec (column/asc keep grp/true).</summary>
        public static bool ParseSort(string spec, out string column, out bool asc)
        {
            column = "grp"; asc = true;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var p = spec.Trim().ToLowerInvariant().Split(':');
            if (p.Length > 2 || Array.IndexOf(All, p[0]) < 0 || p[0] == "icon") return false;
            if (p.Length == 2) { if (p[1] != "asc" && p[1] != "desc") return false; asc = p[1] == "asc"; }
            column = p[0];
            return true;
        }

        /// <summary>Column head text key; the icon column has no head.</summary>
        public static string HeadKey(string col) { return col == "icon" || Array.IndexOf(All, col) < 0 ? null : "overview.own.col." + col; }

        /// <summary>Pixels at scale 1; NaN = flexible (name and task share the rest 1:2).</summary>
        public static double BaseWidth(string col)
        {
            switch (col)
            {
                case "icon": return 26;
                case "type": return 120;
                case "alt": return 76;
                case "spd": return 52;
                case "load": return 104;
                case "grp": return 120;
            }
            return double.NaN;
        }

        public static bool LeftAligned(string col) { return col == "type" || col == "name" || col == "task" || col == "load" || col == "grp"; }

        private static bool Missing(string s) { return string.IsNullOrEmpty(s) || s == Dash; }

        private static int CompareKey(OwnRow a, OwnRow b, string column, bool asc)
        {
            int c;
            switch (column)
            {
                case "alt":
                case "spd":
                {
                    double x = column == "alt" ? a.SortAlt : a.SortSpd, y = column == "alt" ? b.SortAlt : b.SortSpd;
                    bool nx = double.IsNaN(x), ny = double.IsNaN(y);
                    if (nx || ny) return nx && ny ? a.Uid.CompareTo(b.Uid) : nx ? 1 : -1;
                    c = x.CompareTo(y);
                    break;
                }
                case "icon":
                    c = ((int)a.Kind).CompareTo((int)b.Kind);
                    break;
                default:
                {
                    string x, y;
                    switch (column)
                    {
                        case "type": x = a.Type; y = b.Type; break;
                        case "name": x = a.Name; y = b.Name; break;
                        case "task": x = a.Task; y = b.Task; break;
                        case "load": x = a.Load; y = b.Load; break;
                        default: x = a.SortGrp; y = b.SortGrp; break;
                    }
                    bool nx = Missing(x), ny = Missing(y);
                    if (nx || ny) return nx && ny ? a.Uid.CompareTo(b.Uid) : nx ? 1 : -1;
                    c = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }
            if (!asc) c = -c;
            return c != 0 ? c : a.Uid.CompareTo(b.Uid);
        }

        /// <summary>Total order (equal keys keep Uid order), so List.Sort is stable here; NaN / null / "—" last in both directions. No LINQ.</summary>
        public static void Sort(List<OwnRow> rows, string column, bool asc)
        {
            if (rows == null || rows.Count < 2) return;
            rows.Sort((a, b) => CompareKey(a, b, column, asc));
        }
    }

    internal static class OverviewOwnMath
    {
        public const double AirMinAltFt = 50, AirMinKn = 40;
        public const string LeaderGlyph = "★";   // i18n-allow: glyph; fallback "*" if the IMGUI font lacks it
        private const string Dash = "—";

        /// <summary>Surface / Submarine: always. Air / Helicopter: InFlight || AltFt > 50 || SpeedKn > 40 (HoldingPattern clears _isInFlight on the circuit; a deck bird is not listed).</summary>
        public static bool Include(OwnUnitFacts f)
        {
            if (f == null) return false;
            if (f.Kind == OwnKind.Surface || f.Kind == OwnKind.Submarine) return true;
            return f.InFlight || f.AltFt > AirMinAltFt || f.SpeedKn > AirMinKn;
        }

        /// <summary>The game's ObjectOrders rule: feedback when not empty, else the order text; empty -> "—". Trimmed, one line.</summary>
        public static string TaskText(string feedback, string orderText)
        {
            string t = !string.IsNullOrWhiteSpace(feedback) ? feedback : orderText;
            if (string.IsNullOrWhiteSpace(t)) return Dash;
            t = t.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (t.IndexOf("  ", StringComparison.Ordinal) >= 0) t = t.Replace("  ", " ");
            return t.Length == 0 ? Dash : t;
        }

        /// <summary>Air / Helicopter: feet. Submarine: depth text. Surface: "—". NaN: "—".</summary>
        public static string AltText(OwnKind k, double altFt, string lang)
        {
            if (double.IsNaN(altFt) || double.IsInfinity(altFt)) return Dash;
            switch (k)
            {
                case OwnKind.Air:
                case OwnKind.Helicopter: return OverviewMath.FmtFt(Math.Max(0, altFt), lang);
                case OwnKind.Submarine: return UiText.Fmt("overview.own.depth", OverviewMath.FmtFt(Math.Max(0, -altFt), lang));
            }
            return Dash;
        }

        public static string SpeedText(double kn, string lang)
        {
            return double.IsNaN(kn) || double.IsInfinity(kn) ? Dash : OverviewMath.FmtKn(Math.Abs(kn), lang);
        }

        /// <summary>Mod group wins (ADR-0101 item 5); else the game formation "F: name" (the leader's name when the formation is unnamed) + a star for the leader.</summary>
        public static string GroupText(OwnUnitFacts f, out bool leader, out bool mod)
        {
            leader = false; mod = false;
            if (f == null) return Dash;
            if (!string.IsNullOrWhiteSpace(f.ModGroup)) { mod = true; return f.ModGroup.Trim(); }
            if (!f.InFormation) return Dash;
            string name = f.FormationUnnamed || string.IsNullOrWhiteSpace(f.FormationName) ? f.FormationLeaderName : f.FormationName;
            if (string.IsNullOrWhiteSpace(name)) name = f.FormationLeaderName;
            if (string.IsNullOrWhiteSpace(name)) return Dash;
            leader = f.FormationLeader;
            return (leader ? LeaderGlyph + " " : "") + UiText.Fmt("overview.own.grp.formation", name.Trim());
        }

        /// <summary>"ClassName (TypeAbbr)", one of them when the other is empty, "—" when both are.</summary>
        public static string TypeText(OwnUnitFacts f)
        {
            bool c = !string.IsNullOrWhiteSpace(f.ClassName), t = !string.IsNullOrWhiteSpace(f.TypeAbbr);
            if (c && t) return f.ClassName.Trim() + " (" + f.TypeAbbr.Trim() + ")";
            if (c) return f.ClassName.Trim();
            if (t) return f.TypeAbbr.Trim();
            return Dash;
        }

        public static string IconOf(OwnKind k)
        {
            switch (k)
            {
                case OwnKind.Submarine: return "Submarine/Allied";
                case OwnKind.Air: return "Air/Allied";
                case OwnKind.Helicopter: return "Helicopter/Allied";
            }
            return "Surface/Allied";
        }

        public static OwnRow MakeRow(OwnUnitFacts f, string lang)
        {
            var r = new OwnRow { Uid = f.Uid, Kind = f.Kind, Icon = IconOf(f.Kind) };
            r.Type = TypeText(f);
            r.Name = string.IsNullOrWhiteSpace(f.Name) ? Dash : f.Name.Trim();
            r.Task = TaskText(f.Feedback, f.OrderText);
            r.Alt = AltText(f.Kind, f.AltFt, lang);
            r.SortAlt = f.Kind == OwnKind.Surface ? double.NaN : f.Kind == OwnKind.Submarine ? -f.AltFt : f.AltFt;   // submarines: deeper = larger
            r.Spd = SpeedText(f.SpeedKn, lang);
            r.SortSpd = double.IsNaN(f.SpeedKn) ? double.NaN : Math.Abs(f.SpeedKn);
            r.Load = (f.Kind == OwnKind.Air || f.Kind == OwnKind.Helicopter) && !string.IsNullOrWhiteSpace(f.Loadout) ? f.Loadout.Trim() : Dash;
            bool leader, mod;
            r.Grp = GroupText(f, out leader, out mod);
            r.Leader = leader; r.ModGrp = mod;
            if (r.Grp == Dash) r.SortGrp = null;
            else
            {
                string gname = mod ? f.ModGroup.Trim() : (f.FormationUnnamed || string.IsNullOrWhiteSpace(f.FormationName) ? f.FormationLeaderName : f.FormationName) ?? "";
                r.SortGrp = (mod ? "M" : "F") + gname.Trim() + "\u0001" + (leader ? "0" : "1") + r.Name;
            }
            return r;
        }

        /// <summary>Collapsed title of the own tab: "Own: 3 ships · 1 sub · 4 air".</summary>
        public static string Summary(OwnFrame fr)
        {
            int s = fr == null ? 0 : fr.Ships, u = fr == null ? 0 : fr.Subs, a = fr == null ? 0 : fr.Air;
            return UiText.Get("overview.own.sum.title") + ": " + s + " " + UiText.Get("overview.own.sum.ships") + " · " + u + " " + UiText.Get("overview.own.sum.subs") + " · " + a + " " + UiText.Get("overview.own.sum.air");
        }

        /// <summary>While the mouse is over the list: keep the order of the shown rows, refresh their cells from the fresh ones, drop gone ones, append new ones at the end.</summary>
        public static List<OwnRow> ApplyFrozen(List<OwnRow> fresh, List<OwnRow> shown)
        {
            if (shown == null || shown.Count == 0) return fresh;
            var byUid = new Dictionary<int, OwnRow>(fresh.Count);
            foreach (var r in fresh) byUid[r.Uid] = r;
            var res = new List<OwnRow>(fresh.Count);
            var used = new HashSet<int>();
            foreach (var s in shown)
            {
                OwnRow n;
                if (byUid.TryGetValue(s.Uid, out n) && used.Add(s.Uid)) res.Add(n);
            }
            foreach (var r in fresh) if (used.Add(r.Uid)) res.Add(r);
            return res;
        }

        /// <summary>T-324: the result of a window link from the CenterMap reply. null = done, "map_closed" = centred but the map is hidden,
        /// else the error code (centreErr, "unsupported" when it is null).</summary>
        public static string FocusCode(string centreReply, string centreErr)
        {
            if (centreReply == null) return centreErr ?? "unsupported";
            return centreReply.Contains("\"map_open\":false") ? "map_closed" : null;
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            var savedText = OverviewMath.Text; OverviewMath.Text = null;   // built-in ru/en units
            try
            {
                string e, col; bool asc;
                // columns
                var d = OverviewOwnColumns.ParseColumns(OverviewOwnColumns.DefaultSpec, out e);
                chk(d != null && d.Length == 8 && string.Join(",", d) == OverviewOwnColumns.DefaultSpec, "default spec");
                var t = OverviewOwnColumns.ParseColumns(" ICON , Name ,Task", out e);
                chk(t != null && t.Length == 3 && t[2] == "task", "trim lower");
                OverviewOwnColumns.ParseColumns("icon,name,foo", out e); chk(e == "columns: unknown 'foo'", "err unknown");
                OverviewOwnColumns.ParseColumns("icon,type", out e); chk(e == "columns: icon and name are required", "err required");
                OverviewOwnColumns.ParseColumns("name,type", out e); chk(e == "columns: icon and name are required", "err required icon");
                OverviewOwnColumns.ParseColumns("icon,name,grp,grp", out e); chk(e == "columns: duplicate", "err dup");
                OverviewOwnColumns.ParseColumns("", out e); chk(e == "columns: empty", "err empty");
                chk(OverviewOwnColumns.ParseSort("grp:asc", out col, out asc) && col == "grp" && asc, "sort grp");
                chk(OverviewOwnColumns.ParseSort("alt:desc", out col, out asc) && col == "alt" && !asc, "sort alt desc");
                chk(OverviewOwnColumns.ParseSort("task", out col, out asc) && col == "task" && asc, "sort task");
                chk(!OverviewOwnColumns.ParseSort("alt:up", out col, out asc) && col == "grp" && asc, "sort bad dir");
                chk(!OverviewOwnColumns.ParseSort("foo", out col, out asc) && !OverviewOwnColumns.ParseSort("", out col, out asc) && !OverviewOwnColumns.ParseSort("icon", out col, out asc), "sort bad col");
                chk(OverviewOwnColumns.HeadKey("icon") == null && OverviewOwnColumns.HeadKey("grp") == "overview.own.col.grp" && OverviewOwnColumns.HeadKey("dist") == null, "HeadKey");
                chk(double.IsNaN(OverviewOwnColumns.BaseWidth("name")) && double.IsNaN(OverviewOwnColumns.BaseWidth("task")) && OverviewOwnColumns.BaseWidth("alt") > 0, "widths");
                chk(OverviewOwnColumns.LeftAligned("task") && !OverviewOwnColumns.LeftAligned("alt"), "align");
                chk(OverviewTabs.Parse("own") == "own" && OverviewTabs.Parse("OWN") == "own" && OverviewTabs.Parse("x") == "contacts" && OverviewTabs.Parse(null) == "contacts", "tabs");

                // FocusCode (T-324)
                chk(FocusCode(null, "unknown_target") == "unknown_target" && FocusCode(null, null) == "unsupported", "focus err");
                chk(FocusCode("\"lat\":1,\"lon\":2,\"map_open\":false", null) == "map_closed" && FocusCode("\"lat\":1,\"lon\":2,\"map_open\":true", null) == null, "focus map");

                // Include
                chk(OverviewOwnMath.Include(new OwnUnitFacts { Kind = OwnKind.Air, InFlight = false, AltFt = 0, SpeedKn = 0 }) == false, "deck bird hidden");
                chk(OverviewOwnMath.Include(new OwnUnitFacts { Kind = OwnKind.Air, InFlight = false, AltFt = 300, SpeedKn = 0 }), "circuit bird shown");
                chk(OverviewOwnMath.Include(new OwnUnitFacts { Kind = OwnKind.Helicopter, InFlight = false, AltFt = double.NaN, SpeedKn = 60 }), "fast bird shown");
                chk(OverviewOwnMath.Include(new OwnUnitFacts { Kind = OwnKind.Helicopter, InFlight = true }), "flying bird shown");
                chk(OverviewOwnMath.Include(new OwnUnitFacts { Kind = OwnKind.Surface, InFlight = false }) && OverviewOwnMath.Include(new OwnUnitFacts { Kind = OwnKind.Submarine }), "ships shown");
                chk(!OverviewOwnMath.Include(null), "null");

                // TaskText
                chk(OverviewOwnMath.TaskText("", "Patrol") == "Patrol", "task order");
                chk(OverviewOwnMath.TaskText("AD-1: barrage", "Wait") == "AD-1: barrage", "task feedback wins");
                chk(OverviewOwnMath.TaskText(null, null) == "—" && OverviewOwnMath.TaskText("  ", "") == "—", "task none");
                chk(OverviewOwnMath.TaskText(null, " a\nb \r\nc ") == "a b c", "task one line");

                // AltText (text tables may be absent in a bare selfcheck: the depth text is checked only with them)
                chk(OverviewOwnMath.AltText(OwnKind.Surface, 12, "ru") == "—" && OverviewOwnMath.AltText(OwnKind.Air, double.NaN, "ru") == "—", "alt dash");
                chk(OverviewOwnMath.AltText(OwnKind.Air, 12000, "ru") == OverviewMath.FmtFt(12000, "ru") && OverviewOwnMath.AltText(OwnKind.Helicopter, -3, "ru") == OverviewMath.FmtFt(0, "ru"), "alt air");
                bool texts = UiText.Has(UiText.DefaultSection, "overview.own.depth") && UiText.Has(UiText.DefaultSection, "overview.own.grp.formation") && UiText.Has(UiText.DefaultSection, "overview.own.sum.title");
                if (texts)
                {
                    chk(OverviewOwnMath.AltText(OwnKind.Submarine, -150, "ru") == UiText.Fmt("overview.own.depth", OverviewMath.FmtFt(150, "ru")) && OverviewOwnMath.AltText(OwnKind.Submarine, -150, "ru").Contains("150"), "alt depth");
                }

                // GroupText
                bool lead, mod;
                var both = new OwnUnitFacts { ModGroup = "AD-1", InFormation = true, FormationLeader = true, FormationName = "Squad 1" };
                chk(OverviewOwnMath.GroupText(both, out lead, out mod) == "AD-1" && mod && !lead, "mod wins");
                chk(OverviewOwnMath.GroupText(new OwnUnitFacts(), out lead, out mod) == "—" && !mod && !lead, "no group");
                if (texts)
                {
                    var fl = new OwnUnitFacts { InFormation = true, FormationLeader = true, FormationName = "Squad 1", FormationLeaderName = "Nimitz" };
                    string g = OverviewOwnMath.GroupText(fl, out lead, out mod);
                    chk(g == OverviewOwnMath.LeaderGlyph + " " + UiText.Fmt("overview.own.grp.formation", "Squad 1") && lead && !mod, "formation leader");
                    var fm = new OwnUnitFacts { InFormation = true, FormationName = "Squad 1", FormationLeaderName = "Nimitz" };
                    chk(OverviewOwnMath.GroupText(fm, out lead, out mod) == UiText.Fmt("overview.own.grp.formation", "Squad 1") && !lead, "formation member");
                    var un = new OwnUnitFacts { InFormation = true, FormationUnnamed = true, FormationName = "Unnamed formation", FormationLeaderName = "Nimitz" };
                    chk(OverviewOwnMath.GroupText(un, out lead, out mod) == UiText.Fmt("overview.own.grp.formation", "Nimitz"), "unnamed -> leader name");
                    chk(OverviewOwnMath.Summary(new OwnFrame { Ships = 3, Subs = 1, Air = 4 }).Contains("3") && OverviewOwnMath.Summary(null).Contains("0"), "summary");
                }

                // TypeText
                chk(OverviewOwnMath.TypeText(new OwnUnitFacts { ClassName = "Burke", TypeAbbr = "DDG" }) == "Burke (DDG)", "type both");
                chk(OverviewOwnMath.TypeText(new OwnUnitFacts { ClassName = "Burke" }) == "Burke" && OverviewOwnMath.TypeText(new OwnUnitFacts { TypeAbbr = "DDG" }) == "DDG" && OverviewOwnMath.TypeText(new OwnUnitFacts()) == "—", "type one");

                // MakeRow
                var mr = OverviewOwnMath.MakeRow(new OwnUnitFacts { Uid = 7, Kind = OwnKind.Air, Name = "Viper 1", ClassName = "F-16", TypeAbbr = "FTR", OrderText = "CAP", AltFt = 20000, SpeedKn = 420, Loadout = "AAM", ModGroup = "AD-2" }, "ru");
                chk(mr.Icon == "Air/Allied" && mr.Name == "Viper 1" && mr.Task == "CAP" && mr.Load == "AAM" && mr.Grp == "AD-2" && mr.ModGrp && mr.SortAlt == 20000 && mr.SortSpd == 420, "row air");
                chk(mr.SortGrp != null && mr.SortGrp.IndexOf('\u0001') > 0, "row sort grp");
                var ms = OverviewOwnMath.MakeRow(new OwnUnitFacts { Uid = 8, Kind = OwnKind.Surface, Name = "Ship", Loadout = "x", AltFt = 5 }, "ru");
                chk(ms.Load == "—" && ms.Alt == "—" && double.IsNaN(ms.SortAlt) && ms.SortGrp == null && ms.Icon == "Surface/Allied", "row ship");
                var mb = OverviewOwnMath.MakeRow(new OwnUnitFacts { Uid = 9, Kind = OwnKind.Submarine, AltFt = -300 }, "ru");
                chk(mb.SortAlt == 300 && mb.Name == "—", "row sub");

                // Sort
                Func<int, string, string, string, double, double, OwnRow> R = (uid, grp, name, task, alt, spd) => new OwnRow { Uid = uid, Name = name, Task = task, SortGrp = grp, SortAlt = alt, SortSpd = spd };
                var rows = new List<OwnRow> { R(1, null, "c", "—", double.NaN, 5), R(2, "Fb\u00011x", "b", "t", 100, 5), R(3, "Fb\u00010y", "a", "t", 300, double.NaN), R(4, "Fa\u00011z", "d", "u", 100, 9), R(5, null, "e", "t", double.NaN, 1) };
                OverviewOwnColumns.Sort(rows, "grp", true);
                chk(Ids(rows) == "4,3,2,1,5", "sort grp asc (leader first, none last): " + Ids(rows));
                OverviewOwnColumns.Sort(rows, "grp", false);
                chk(Ids(rows) == "2,3,4,1,5", "sort grp desc, none last: " + Ids(rows));
                OverviewOwnColumns.Sort(rows, "alt", true);
                chk(Ids(rows) == "2,4,3,1,5", "sort alt asc, NaN last, equal by uid: " + Ids(rows));
                OverviewOwnColumns.Sort(rows, "alt", false);
                chk(Ids(rows) == "3,2,4,1,5", "sort alt desc, NaN last: " + Ids(rows));
                OverviewOwnColumns.Sort(rows, "spd", true);
                chk(Ids(rows) == "5,1,2,4,3", "sort spd asc, NaN last: " + Ids(rows));
                OverviewOwnColumns.Sort(rows, "task", true);
                chk(Ids(rows) == "2,3,5,4,1", "sort task, dash last: " + Ids(rows));
                OverviewOwnColumns.Sort(rows, "name", false);
                chk(Ids(rows) == "5,4,1,2,3", "sort name desc: " + Ids(rows));

                // ApplyFrozen
                var shown = new List<OwnRow> { R(3, null, "old3", "", 0, 0), R(1, null, "old1", "", 0, 0), R(9, null, "gone", "", 0, 0) };
                var fresh = new List<OwnRow> { R(1, null, "new1", "", 0, 0), R(2, null, "new2", "", 0, 0), R(3, null, "new3", "", 0, 0) };
                var fz = OverviewOwnMath.ApplyFrozen(fresh, shown);
                chk(Ids(fz) == "3,1,2" && fz[0].Name == "new3" && fz[1].Name == "new1", "frozen order and fresh cells");
                chk(ReferenceEquals(OverviewOwnMath.ApplyFrozen(fresh, new List<OwnRow>()), fresh), "frozen empty");
            }
            finally { OverviewMath.Text = savedText; }
            return err;
        }

        private static string Ids(List<OwnRow> rows)
        {
            var parts = new string[rows.Count];
            for (int i = 0; i < rows.Count; i++) parts[i] = rows[i].Uid.ToString();
            return string.Join(",", parts);
        }
    }
}
