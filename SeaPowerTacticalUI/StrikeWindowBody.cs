using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal interface IStrikeWindowEnv
    {
        string FocusUnit(int uid);      // centres the map on an own aircraft (null = done, else a code)
        void StartPick();               // the map pick of a hostile ship
        void CancelPick(string why);
        bool PickActive { get; }
        double Now();                   // real time, s (the life of the note line)
    }

    /// <summary>
    /// T-341 phase B: the body of the strike window on UiKit (pure: no Unity, tested in strikewintest). Sections: groups, aircraft, target, plan cards, the course of the strike
    /// (a draft: the planned times only). Commands go through `send` (strike_group / strike_target / strike_plan / strike_cancel), answers are ack json or "!code".
    /// Texts: UiText keys strike.* (lang/*/strike_window.ini).
    /// </summary>
    internal sealed partial class StrikeWindowBody
    {
        private readonly Func<string, Dictionary<string, object>, string> _send;
        private readonly IStrikeWindowEnv _env;
        private string _sel, _renameText, _renameFor, _confirmDisband, _launchFor;
        private bool _launchConfirm;
        private int _modeIdx = -1;
        private string _modeFor;
        private readonly HashSet<int> _pickedAir = new HashSet<int>(), _pickedShips = new HashSet<int>();

        public string SelectedGroup { get { return _sel; } }
        public bool PickActive { get { return _env.PickActive; } }
        public string LastHint { get; private set; }
        public string Note { get; private set; }
        public UiTone NoteTone { get; private set; }
        public double NoteUntil { get; private set; }

        public StrikeWindowBody(Func<string, Dictionary<string, object>, string> send, IStrikeWindowEnv env) { _send = send; _env = env; }

        private static string Tx(string key, params object[] args) { return UiText.Fmt(key, args); }
        private static bool NoteWarn(string note, params string[] heads)
        {
            foreach (string h in heads) if (note.StartsWith(h, StringComparison.Ordinal)) return true;
            return false;
        }
        private static bool ExecRunning(StrikeGroupView g) { return g.Exec == "executing"; }
        private static string GoalName(string goal) { return Tx(goal == "breach" ? "strike.goal.breach" : "strike.goal.sink"); }
        private static string I(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        private void Say(string text, UiTone tone)
        {
            Note = text; NoteTone = tone; NoteUntil = _env.Now() + 6; LastHint = text;
        }

        private string Send(string name, Dictionary<string, object> args)
        {
            string r;
            try { r = _send(name, args); } catch (Exception e) { r = "!error|" + e.GetType().Name; }
            return r;
        }

        private static bool Err(string r) { return r == null || (r.Length > 0 && r[0] == '!'); }

        private StrikeGroupView Sel(StrikeView v) { return v.Groups.FirstOrDefault(g => g.Name == _sel); }

        private static List<object> Uids(IEnumerable<int> ids) { return ids.Select(x => (object)(double)x).ToList(); }

        /// <summary>The whole body. One pass per frame; the commands run inside the clicks and the next frame shows their result.</summary>
        public void Draw(UiKit k, StrikeView v)
        {
            if (v == null) v = new StrikeView();
            if (_sel == null || Sel(v) == null) { _sel = v.Groups.Count > 0 ? v.Groups[0].Name : null; _confirmDisband = null; }
            var g = Sel(v);
            if (_renameFor != _sel) { _renameFor = _sel; _renameText = _sel ?? ""; }
            if (g != null && _modeFor != g.Name) { _modeFor = g.Name; _modeIdx = g.Mode == "spread" ? 1 : 0; }
            if (!v.Ready) k.Hint(Tx("strike.hint.not_ready"), UiTone.Warn);
            DrawGroups(k, v, g);
            DrawAuto(k, g);
            DrawAir(k, v, g);
            DrawTarget(k, v, g);
            DrawPlans(k, v, g);
            DrawDeck(k, g);
            DrawTimeline(k, g);
            k.Hint(v.Estimate ? Tx("strike.hint.estimate") : Tx("strike.hint.calibrated", v.CalibId));
        }

        public string Summary(StrikeView v) { return v == null ? "" : Tx("strike.summary", v.Groups.Count); }

        /// <summary>Lines for the warning area of the frame: the stale plan of the selected group.</summary>
        public IList<string> Warnings(StrikeView v)
        {
            var res = new List<string>();
            var g = v == null ? null : Sel(v);
            if (g != null && g.Status == "stale") res.Add(Tx("strike.stale.line", StrikeWindow.StaleText(g.StaleWhy)));
            return res;
        }

        // ---------------------------------------------------------------- groups

        private void DrawGroups(UiKit k, StrikeView v, StrikeGroupView g)
        {
            k.Section("groups", Tx("strike.sec.groups"), I(v.Groups.Count), true, v.Groups.Count == 0 ? null : string.Join(", ", v.Groups.Select(x => x.Name)));
            if (v.Groups.Count == 0) k.Hint(Tx("strike.hint.no_groups"));
            else
            {
                var labels = v.Groups.Select(x => x.Name + " (" + I(x.Members.Count + x.DeckKeys.Count) + ")").ToList();
                var tips = v.Groups.Select(x => Tx("strike.tip.group_chip", x.Name, I(x.Members.Count))).ToList();
                int sel = v.Groups.FindIndex(x => x.Name == _sel);
                int hit = k.Chips("grp", labels, sel, null, tips);
                if (hit >= 0 && hit < v.Groups.Count && v.Groups[hit].Name != _sel) { _sel = v.Groups[hit].Name; _confirmDisband = null; _pickedAir.Clear(); }
            }
            if (g != null)
            {
                string t = k.Number("rename", _renameText, 12);
                if (t != null) _renameText = t;
                if (k.Button("rename.btn", Tx("strike.btn.rename"), null, g != null && !string.IsNullOrWhiteSpace(_renameText) && _renameText.Trim() != g.Name))
                {
                    string r = Send("strike_group", new Dictionary<string, object> { { "op", "rename" }, { "group", g.Name }, { "name", _renameText.Trim() } });
                    if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else { _sel = _renameText.Trim(); _renameFor = null; }
                }
            }
            string disbandText = g != null && _confirmDisband == g.Name ? Tx("strike.btn.disband_sure", g.Name) : Tx("strike.btn.disband");
            string pressed = k.Actions(new UiAction("disband", disbandText, null, g != null, _confirmDisband != null ? UiTone.Warn : UiTone.Normal), new UiAction("create", Tx("strike.btn.create")));
            if (pressed == "create")
            {
                string r = Send("strike_group", new Dictionary<string, object> { { "op", "create" } });
                if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error);
                else
                {
                    var o = Json.Parse(r) as Dictionary<string, object>;
                    object name;
                    if (o != null && o.TryGetValue("group", out name) && name is string) { _sel = (string)name; Say(Tx("strike.say.created", _sel), UiTone.Ok); }
                }
                _confirmDisband = null;
            }
            else if (pressed == "disband" && g != null)
            {
                if (_confirmDisband == g.Name)
                {
                    string r = Send("strike_group", new Dictionary<string, object> { { "op", "disband" }, { "group", g.Name } });
                    if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error);
                    _confirmDisband = null; _sel = null;
                }
                else _confirmDisband = g.Name;
            }
            if (g == null) { if (v.Groups.Count > 0) k.Hint(Tx("strike.hint.no_group_selected")); return; }
            string waitKey = g.WaitPt.HasValue ? g.WaitWhy : "none";
            k.Text(Tx("strike.group.status", StrikeWindow.GatherText(g.Gather), StrikeWindow.WaitText(waitKey), StrikeWindow.PlanText(g.Status)), UiTone.Dim, true, 2);
            // T-342: the goal of the strike. Grey and refused while the strike runs; a plan that exists goes stale (the button counts it again)
            {
                bool run = g.Exec == "executing";
                k.Text(Tx("strike.goal.label"), UiTone.Dim);
                var tones = new List<UiTone> { run ? UiTone.Dim : UiTone.Normal, run ? UiTone.Dim : UiTone.Normal };
                int hit = k.Chips("goal", new List<string> { Tx("strike.goal.breach"), Tx("strike.goal.sink") }, g.Goal == "breach" ? 0 : 1, tones, new List<string> { Tx("strike.goal.tip_breach"), Tx("strike.goal.tip_sink", Math.Round(v.KillThreshold * 100).ToString("0", CultureInfo.InvariantCulture)) });
                if (hit >= 0)
                {
                    string goal = hit == 0 ? "breach" : "sink";
                    if (run) Say(Tx("strike.say.goal_running"), UiTone.Warn);
                    else if (goal != g.Goal)
                    {
                        string r = Send("strike_goal", new Dictionary<string, object> { { "group", g.Name }, { "goal", goal } });
                        if (Err(r)) Say(StrikeWindow.ErrText(r), UiTone.Error); else Say(Tx("strike.say.goal", GoalName(goal)), UiTone.Ok);
                    }
                }
                if (g.Goal == "sink" && !v.BonusKnown) k.Hint(Tx("strike.warn.bonus_default"), UiTone.Warn);
                if (g.Goal == "sink" && !v.Ledger) k.Hint(Tx("strike.warn.no_ledger"), UiTone.Warn);
            }
            if (g.DeckHeldS >= 0) k.Hint(Tx("strike.deck.held", StrikeWindow.Mmss(g.DeckHeldS)), UiTone.Warn);
            if (g.Left.Count > 0) k.Hint(Tx("strike.left.line", string.Join(", ", g.Left.Select(l => StrikeLeftLabel(l)))), UiTone.Warn);
        }

        private static string StrikeLeftLabel(string l)
        {
            int i = l.IndexOf(':');
            return i < 0 ? l : (l.Substring(0, i) == "0" ? "" : "#" + l.Substring(0, i) + " ") + StrikeWindow.LeftText(l.Substring(i + 1));
        }
    }
}
