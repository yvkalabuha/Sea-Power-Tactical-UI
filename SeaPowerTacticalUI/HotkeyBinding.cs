using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-144: result of a dry run over the specs in the settings draft.</summary>
    internal enum HotkeyDraftState { Off, Ok, BadFormat, ModConflict, GameNext, AllTaken, Unknown }

    internal struct HotkeyDraftRow { public string Id, Spec; }   // Id = "Section.Key"

    internal sealed class HotkeyDraftResult
    {
        public HotkeyDraftState State;
        public string Picked;         // the candidate that would win (Ok, GameNext; tentative for Unknown)
        public string ConflictWith;   // ModConflict: Id of the other row; GameNext: Id of the row that holds the first candidate (if any)
        public string GameAction;     // GameNext / AllTaken: name of the game action that holds the first candidate
        public string Error;          // BadFormat
    }

    /// <summary>The game's key table: "free" | "taken" | "unknown" (GameApi.OverviewKeyCheck).</summary>
    internal interface IHotkeyGame { string Check(string[] keys, out string conflict); }

    /// <summary>Held/Down by Unity KeyCode name; Typing = a text field has the focus.</summary>
    internal interface IKeyInput { bool Held(string keyName); bool Down(string keyName); bool Typing { get; } }

    /// <summary>All bindings of the mod: a combo picked by one binding is taken for the others.</summary>
    internal sealed class HotkeyRegistry
    {
        private readonly List<HotkeyBinding> _items = new List<HotkeyBinding>();

        /// <summary>T-144: the settings window records a key; Pressed/Held of every binding report false meanwhile.</summary>
        public bool Suspended { get; set; }

        /// <summary>T-144: every binding forgets its pick (unknown, or off with no candidates) and resolves again on the next Update.</summary>
        public void ResetAll() { foreach (var b in _items.ToArray()) b.ResetPick(); }

        internal void Add(HotkeyBinding b) { if (!_items.Contains(b)) _items.Add(b); }

        /// <summary>T-174: Label of the first binding of this owner ("asw", "cap"), "" when none or nothing is picked (tooltips of the game-panel buttons).</summary>
        public string LabelOf(string owner)
        {
            foreach (var b in _items) if (b.Owner == owner) return b.Label;
            return "";
        }

        /// <summary>combo like "LeftAlt+U"; compared after ParseHotkey + Join("+").</summary>
        public bool TakenByOther(string combo, HotkeyBinding self)
        {
            string e;
            var k = CoverageMath.ParseHotkey(combo, out e);
            string norm = k != null ? string.Join("+", k) : combo;
            foreach (var b in _items)
            {
                if (ReferenceEquals(b, self) || b.Picked == null || b.Keys == null) continue;
                if (string.Join("+", b.Keys) == norm) return true;
            }
            return false;
        }
    }

    /// <summary>"A|B|C" hotkey candidates: the first one the game and the other modules do not use wins (was AswHotkeys + two copies in UpdateAsw/UpdateCap).</summary>
    internal sealed class HotkeyBinding
    {
        private readonly string _owner, _configKey;
        private string _spec;
        private readonly HotkeyRegistry _registry;
        private readonly Action<string> _logInfo, _logWarn;
        private readonly List<string> _candidates = new List<string>();
        private double _next;

        /// <summary>owner = log prefix ("asw", "cap"); configKey = "Asw.Hotkey" (for the "empty ..." line).</summary>
        public HotkeyBinding(string owner, string configKey, string spec, HotkeyRegistry registry, Action<string> logInfo, Action<string> logWarn)
        {
            _owner = owner; _configKey = configKey; _spec = spec; _registry = registry; _logInfo = logInfo; _logWarn = logWarn;
            foreach (var x in (spec ?? "").Split('|')) { string t = x.Trim(); if (t.Length > 0 && !_candidates.Contains(t)) _candidates.Add(t); }
            if (_candidates.Count == 0)
            {
                State = "off";
                if (_logWarn != null) _logWarn(owner + ": empty " + configKey + ", hotkey off");
            }
            else State = "unknown";
            if (registry != null) registry.Add(this);
        }

        /// <summary>Unified format: "A|B" candidates, each Unity KeyCode names joined by "+"; "" = off (no candidates, no error). error = the first bad candidate and why.</summary>
        public static bool TryParseSpec(string spec, out List<string[]> candidates, out string error)
        {
            candidates = new List<string[]>();
            error = null;
            foreach (var x in (spec ?? "").Split('|'))
            {
                string t = x.Trim();
                if (t.Length == 0) continue;
                string e;
                var keys = CoverageMath.ParseHotkey(t, out e);
                if (keys == null) { candidates = new List<string[]>(); error = "bad hotkey candidate '" + t + "' (" + e + ")"; return false; }
                candidates.Add(keys);
            }
            return true;
        }

        public HotkeyRegistry Registry { get { return _registry; } }

        public string Owner { get { return _owner; } }

        /// <summary>T-144: a new spec at run time (the settings window). Candidates are replaced, the pick is forgotten, State = unknown | off.</summary>
        public void Respec(string spec)
        {
            _spec = spec;
            _candidates.Clear();
            foreach (var x in (spec ?? "").Split('|')) { string t = x.Trim(); if (t.Length > 0 && !_candidates.Contains(t)) _candidates.Add(t); }
            if (_candidates.Count == 0 && _logWarn != null) _logWarn(_owner + ": empty " + _configKey + ", hotkey off");
            ResetPick();
        }

        internal void ResetPick()
        {
            State = _candidates.Count == 0 ? "off" : "unknown";
            Picked = null; Keys = null; _next = 0;
        }

        public IList<string> Candidates { get { return _candidates; } }
        public string State { get; private set; }     // "off" | "unknown" | "free" | "taken"
        public string Picked { get; private set; }    // null until picked
        public string[] Keys { get; private set; }

        /// <summary>Short text of the picked combo for legends ("Alt+V"); "" while none is picked (off, unknown, every candidate taken).</summary>
        public string Label { get { return State == "free" && Keys != null ? ShortLabel(Keys) : ""; } }

        internal static string ShortLabel(string[] keys)
        {
            var p = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                p[i] = k == "LeftAlt" || k == "RightAlt" ? "Alt" : k == "LeftShift" || k == "RightShift" ? "Shift" : k == "LeftControl" || k == "RightControl" ? "Ctrl"
                    : k.StartsWith("Alpha", StringComparison.Ordinal) && k.Length == 6 ? k.Substring(5) : k;
            }
            return string.Join("+", p);
        }

        /// <summary>Once per 2 s while State == "unknown" and a mission runs. "unknown" from the game = ask again later.</summary>
        public void Resolve(double realTime, bool inMission, IHotkeyGame game)
        {
            if (State != "unknown" || !inMission || realTime < _next) return;
            _next = realTime + 2.0;
            bool unknown = false;
            string picked = null;
            string[] pickedKeys = null;
            foreach (var c in _candidates)
            {
                string e, conflict;
                var keys = CoverageMath.ParseHotkey(c, out e);
                if (keys == null) { if (_logWarn != null) _logWarn(_owner + ": bad hotkey candidate '" + c + "' (" + e + ")"); continue; }
                if (_registry != null && _registry.TakenByOther(string.Join("+", keys), this)) continue;   // another module has it
                string v = game.Check(keys, out conflict);
                if (v == "unknown") { unknown = true; continue; }
                if (v != "free") { if (_logInfo != null) _logInfo(_owner + ": hotkey " + c + " taken by " + (conflict ?? "?")); continue; }
                picked = c; pickedKeys = keys;
                break;
            }
            if (unknown) return;
            State = picked != null ? "free" : "taken";
            Picked = picked; Keys = pickedKeys;
            if (_logInfo != null) _logInfo(_owner + ": hotkey " + (picked ?? "none free of " + _spec + ", hotkey off") + (picked != null ? " selected" : ""));
        }

        /// <summary>All modifiers held, the last key went down this frame, no text field has the focus.</summary>
        public bool Pressed(IKeyInput input)
        {
            if (State != "free" || Keys == null || (_registry != null && _registry.Suspended)) return false;
            int last = Keys.Length - 1;
            for (int i = 0; i < last; i++) if (!input.Held(Keys[i])) return false;
            if (ExtraModifier(input)) return false;
            return input.Down(Keys[last]) && !input.Typing;
        }

        /// <summary>T-290: Ctrl / Shift / Alt is held but the combo does not declare that modifier (Ctrl+F8 of the profiler must not fire F8).</summary>
        private bool ExtraModifier(IKeyInput input)
        {
            foreach (var fam in ModFamilies)
            {
                bool declared = false;
                foreach (var k in Keys) if (k == fam[0] || k == fam[1]) declared = true;
                if (!declared && (input.Held(fam[0]) || input.Held(fam[1]))) return true;
            }
            return false;
        }

        private static readonly string[][] ModFamilies = {
            new[] { "LeftControl", "RightControl" }, new[] { "LeftShift", "RightShift" }, new[] { "LeftAlt", "RightAlt" } };

        /// <summary>Hold semantics (Radial): the picked combo is resolved free, all its keys are held, no text field has the focus.</summary>
        public bool Held(IKeyInput input)
        {
            if (State != "free" || Keys == null || input.Typing || (_registry != null && _registry.Suspended)) return false;
            for (int i = 0; i < Keys.Length; i++) if (!input.Held(Keys[i])) return false;
            return true;
        }

        /// <summary>T-144 (plan 3.6): what Resolve would do for these specs, without touching any binding. Rows in registry order.
        /// A combo picked by a row above is taken. A row whose every candidate is held by mod rows is a ModConflict (both rows).
        /// Outside a mission or when the game answers "unknown" the result is Unknown (the first mod-free candidate is held tentatively).</summary>
        internal static int DryRunCalls;   // T-239: test counter (stand only)

        public static IList<HotkeyDraftResult> DryRun(IList<HotkeyDraftRow> rows, IHotkeyGame game, bool inMission)
        {
            DryRunCalls++;
            var res = new List<HotkeyDraftResult>();
            var holder = new Dictionary<string, int>();   // normalised combo -> row index
            for (int i = 0; i < rows.Count; i++)
            {
                var r = new HotkeyDraftResult();
                res.Add(r);
                List<string[]> cands; string err;
                if (!TryParseSpec(rows[i].Spec, out cands, out err)) { r.State = HotkeyDraftState.BadFormat; r.Error = err; continue; }
                if (cands.Count == 0) { r.State = HotkeyDraftState.Off; continue; }
                var texts = new List<string>();
                foreach (var x in (rows[i].Spec ?? "").Split('|')) { string t = x.Trim(); if (t.Length > 0) texts.Add(t); }
                int firstHolder = -1;
                var free = new List<int>();   // candidate indexes not held by a mod row
                for (int c = 0; c < cands.Count; c++)
                {
                    int h;
                    if (holder.TryGetValue(string.Join("+", cands[c]), out h)) { if (c == 0 || firstHolder < 0) firstHolder = h; }
                    else free.Add(c);
                }
                if (free.Count == 0)
                {
                    int h0 = -1;
                    holder.TryGetValue(string.Join("+", cands[0]), out h0);
                    r.State = HotkeyDraftState.ModConflict; r.ConflictWith = rows[h0].Id;
                    if (res[h0].State != HotkeyDraftState.BadFormat) { res[h0].State = HotkeyDraftState.ModConflict; res[h0].ConflictWith = rows[i].Id; }
                    continue;
                }
                bool unknown = false;
                int pick = -1;
                string firstAction = null;
                foreach (int c in free)
                {
                    string v = "unknown", conflict = null;
                    if (inMission && game != null) v = game.Check(cands[c], out conflict);
                    if (v == "unknown") { unknown = true; continue; }
                    if (v != "free") { if (c == 0 && firstAction == null) firstAction = conflict ?? "?"; continue; }
                    pick = c; break;
                }
                if (unknown)
                {
                    r.State = HotkeyDraftState.Unknown;
                    r.Picked = texts[free[0]];
                    holder[string.Join("+", cands[free[0]])] = i;
                    continue;
                }
                if (pick < 0) { r.State = HotkeyDraftState.AllTaken; r.GameAction = firstAction; continue; }
                r.Picked = texts[pick];
                holder[string.Join("+", cands[pick])] = i;
                if (pick == 0) r.State = HotkeyDraftState.Ok;
                else
                {
                    r.State = HotkeyDraftState.GameNext; r.GameAction = firstAction;
                    if (firstHolder >= 0 && firstAction == null) r.ConflictWith = rows[firstHolder].Id;
                }
            }
            return res;
        }

        private sealed class FakeGame : IHotkeyGame
        {
            public readonly Dictionary<string, string> Verdict = new Dictionary<string, string>();
            public int Calls;
            public string Check(string[] keys, out string conflict)
            {
                Calls++;
                string k = string.Join("+", keys), v;
                conflict = "Some Action";
                return Verdict.TryGetValue(k, out v) ? v : "free";
            }
        }

        private sealed class FakeInput : IKeyInput
        {
            public readonly HashSet<string> HeldKeys = new HashSet<string>(), DownKeys = new HashSet<string>();
            public bool TypingNow;
            public bool Held(string k) { return HeldKeys.Contains(k); }
            public bool Down(string k) { return DownKeys.Contains(k); }
            public bool Typing { get { return TypingNow; } }
        }

        // T-144 R5: Respec, Suspended, ResetAll, DryRun
        private static string SelfTestSettings()
        {
            var reg = new HotkeyRegistry();
            var warn = new List<string>();
            var a = new HotkeyBinding("asw", "Asw.Hotkey", "LeftAlt+U|F7", reg, null, warn.Add);
            var g = new FakeGame();
            a.Resolve(0, true, g);
            if (a.State != "free" || a.Picked != "LeftAlt+U" || !ReferenceEquals(a.Registry, reg)) return "hotkey: setup";
            a.Respec("F8");
            if (a.State != "unknown" || a.Picked != null || a.Keys != null || a.Candidates.Count != 1 || a.Candidates[0] != "F8") return "hotkey: Respec resets to unknown";
            a.Resolve(0, true, g);   // _next was reset: asks at once
            if (a.Picked != "F8") return "hotkey: Respec then Resolve, got " + a.Picked;
            a.Respec("");
            if (a.State != "off" || warn.Count != 1 || warn[0] != "asw: empty Asw.Hotkey, hotkey off") return "hotkey: Respec empty -> off";
            a.Respec("F8");
            a.Resolve(0, true, g);
            var inp = new FakeInput(); inp.DownKeys.Add("F8");
            if (!a.Pressed(inp)) return "hotkey: Pressed before Suspended";
            reg.Suspended = true;
            inp.HeldKeys.Add("F8");
            if (a.Pressed(inp) || a.Held(inp)) return "hotkey: Suspended must silence Pressed/Held";
            reg.Suspended = false;
            if (!a.Pressed(inp) || !a.Held(inp)) return "hotkey: Suspended off";
            var b = new HotkeyBinding("cap", "Cap.Hotkey", "", reg, null, null);
            reg.ResetAll();
            if (a.State != "unknown" || a.Picked != null || b.State != "off") return "hotkey: ResetAll " + a.State + "/" + b.State;

            // DryRun
            Func<string, string, HotkeyDraftRow> row = (id, spec) => new HotkeyDraftRow { Id = id, Spec = spec };
            var gm = new FakeGame();
            var rr = DryRun(new[] { row("A.K", "Bad+Key+"), row("B.K", ""), row("C.K", "F1|F2") }, gm, true);
            if (rr[0].State != HotkeyDraftState.BadFormat || rr[0].Error == null) return "hotkey: DryRun BadFormat";
            if (rr[1].State != HotkeyDraftState.Off) return "hotkey: DryRun Off";
            if (rr[2].State != HotkeyDraftState.Ok || rr[2].Picked != "F1") return "hotkey: DryRun Ok";
            // two rows with the same single combo: both conflict
            rr = DryRun(new[] { row("A.K", "F1"), row("B.K", "F1") }, gm, true);
            if (rr[0].State != HotkeyDraftState.ModConflict || rr[0].ConflictWith != "B.K" || rr[1].State != HotkeyDraftState.ModConflict || rr[1].ConflictWith != "A.K") return "hotkey: DryRun ModConflict both rows";
            // a candidate is left: not an error, the second row takes the next one
            rr = DryRun(new[] { row("A.K", "F1"), row("B.K", "F1|F2") }, gm, true);
            if (rr[1].State != HotkeyDraftState.GameNext || rr[1].Picked != "F2" || rr[1].ConflictWith != "A.K") return "hotkey: DryRun mod holds the first candidate";
            // game holds the first
            gm.Verdict["F1"] = "taken";
            rr = DryRun(new[] { row("A.K", "F1|F2") }, gm, true);
            if (rr[0].State != HotkeyDraftState.GameNext || rr[0].Picked != "F2" || rr[0].GameAction != "Some Action") return "hotkey: DryRun GameNext";
            gm.Verdict["F2"] = "taken";
            rr = DryRun(new[] { row("A.K", "F1|F2") }, gm, true);
            if (rr[0].State != HotkeyDraftState.AllTaken || rr[0].Picked != null || rr[0].GameAction != "Some Action") return "hotkey: DryRun AllTaken";
            // unknown from the game, and outside a mission
            gm.Verdict["F1"] = "unknown";
            rr = DryRun(new[] { row("A.K", "F1|F2") }, gm, true);
            if (rr[0].State != HotkeyDraftState.Unknown) return "hotkey: DryRun Unknown from the game";
            gm.Calls = 0;
            rr = DryRun(new[] { row("A.K", "F5"), row("B.K", "F5") }, gm, false);
            if (gm.Calls != 0 || rr[0].State != HotkeyDraftState.ModConflict || rr[1].State != HotkeyDraftState.ModConflict) return "hotkey: DryRun outside a mission still sees mod conflicts without asking the game";
            rr = DryRun(new[] { row("A.K", "F5") }, gm, false);
            if (rr[0].State != HotkeyDraftState.Unknown) return "hotkey: DryRun outside a mission = Unknown";
            rr = DryRun(new[] { row("A.K", "LeftAlt+O|F9") }, null, true);
            if (rr[0].State != HotkeyDraftState.Unknown) return "hotkey: DryRun without a game = Unknown";
            return null;
        }

        public static string SelfTest()
        {
            try
            {
                var info = new List<string>(); var warn = new List<string>();
                var reg = new HotkeyRegistry();
                var a = new HotkeyBinding("asw", "Asw.Hotkey", "LeftAlt+U|LeftAlt+J|F7", reg, info.Add, warn.Add);
                if (string.Join(",", new List<string>(a.Candidates).ToArray()) != "LeftAlt+U,LeftAlt+J,F7") return "hotkey: candidates";
                var dup = new HotkeyBinding("x", "X.Hotkey", " F7 | |F7|F8 ", null, null, null);
                if (string.Join(",", new List<string>(dup.Candidates).ToArray()) != "F7,F8") return "hotkey: trim/dedupe";
                if (a.State != "unknown" || a.Picked != null) return "hotkey: initial state " + a.State;
                var g = new FakeGame();
                g.Verdict["LeftAlt+U"] = "taken";
                a.Resolve(10, false, g);
                if (g.Calls != 0) return "hotkey: no resolve outside a mission";
                g.Verdict["LeftAlt+J"] = "unknown";
                a.Resolve(10, true, g);
                if (a.State != "unknown" || a.Picked != null) return "hotkey: unknown must stay unknown";
                int calls = g.Calls;
                a.Resolve(11.9, true, g);
                if (g.Calls != calls) return "hotkey: retry before 2 s";
                g.Verdict["LeftAlt+J"] = "free";
                a.Resolve(12.0, true, g);
                if (a.State != "free" || a.Picked != "LeftAlt+J" || string.Join("+", a.Keys) != "LeftAlt+J") return "hotkey: pick second, state " + a.State + " " + a.Picked;
                if (!info.Contains("asw: hotkey LeftAlt+U taken by Some Action")) return "hotkey: missing 'taken by' line";
                if (!info.Contains("asw: hotkey LeftAlt+J selected")) return "hotkey: missing 'selected' line";
                int n = g.Calls;
                a.Resolve(100, true, g);
                if (g.Calls != n) return "hotkey: resolved binding must not ask again";
                if (warn.Count != 0) return "hotkey: unexpected warnings " + string.Join("|", warn.ToArray());

                // the second binding skips the combo of the first one silently, a bad candidate is reported
                var info2 = new List<string>(); var warn2 = new List<string>();
                var c = new HotkeyBinding("cap", "Cap.Hotkey", "LeftAlt+J|Bad+Key+|LeftAlt+P", reg, info2.Add, warn2.Add);
                var g2 = new FakeGame();
                c.Resolve(0, true, g2);
                if (c.Picked != "LeftAlt+P") return "hotkey: cap must skip the ASW combo, got " + c.Picked;
                if (g2.Calls != 1) return "hotkey: taken-by-other must not ask the game (calls " + g2.Calls + ")";
                if (info2.Exists(s => s.Contains("LeftAlt+J"))) return "hotkey: taken-by-other must not log";
                if (warn2.Count != 1 || !warn2[0].StartsWith("cap: bad hotkey candidate 'Bad+Key+' (")) return "hotkey: bad candidate line " + string.Join("|", warn2.ToArray());
                if (!reg.TakenByOther("LeftAlt+P", a) || reg.TakenByOther("LeftAlt+P", c) || reg.TakenByOther("F9", a)) return "hotkey: TakenByOther";

                // nothing free
                var info3 = new List<string>();
                var n3 = new HotkeyBinding("asw", "Asw.Hotkey", "F6|F7", null, info3.Add, null);
                var g3 = new FakeGame(); g3.Verdict["F6"] = "taken"; g3.Verdict["F7"] = "taken";
                n3.Resolve(0, true, g3);
                if (n3.State != "taken" || n3.Picked != null) return "hotkey: none free state " + n3.State;
                if (!info3.Contains("asw: hotkey none free of F6|F7, hotkey off")) return "hotkey: none free line " + string.Join("|", info3.ToArray());
                if (n3.Pressed(new FakeInput())) return "hotkey: taken binding must not fire";

                // empty spec
                var w4 = new List<string>();
                var e4 = new HotkeyBinding("cap", "Cap.Hotkey", "  ", reg, null, w4.Add);
                if (e4.State != "off" || w4.Count != 1 || w4[0] != "cap: empty Cap.Hotkey, hotkey off") return "hotkey: empty spec " + e4.State + " " + string.Join("|", w4.ToArray());
                e4.Resolve(0, true, new FakeGame());
                if (e4.State != "off") return "hotkey: off must stay off";

                // Pressed
                var inp = new FakeInput();
                if (a.Pressed(inp)) return "hotkey: nothing held";
                inp.DownKeys.Add("J");
                if (a.Pressed(inp)) return "hotkey: modifier not held";
                inp.HeldKeys.Add("LeftAlt");
                if (!a.Pressed(inp)) return "hotkey: must fire";
                inp.TypingNow = true;
                if (a.Pressed(inp)) return "hotkey: typing blocks";
                inp.TypingNow = false; inp.DownKeys.Clear();
                if (a.Pressed(inp)) return "hotkey: key not down this frame";
                inp.HeldKeys.Remove("LeftAlt");
                var f7 = new HotkeyBinding("x", "X.Hotkey", "F7", null, null, null);
                f7.Resolve(0, true, new FakeGame());
                inp.DownKeys.Add("F7");
                if (!f7.Pressed(inp)) return "hotkey: single key";
                inp.HeldKeys.Add("LeftControl");   // T-290: Ctrl+F7 is not F7
                if (f7.Pressed(inp)) return "hotkey: undeclared Ctrl must block F7";
                inp.HeldKeys.Remove("LeftControl"); inp.HeldKeys.Add("RightShift");
                if (f7.Pressed(inp)) return "hotkey: undeclared Shift must block F7";
                inp.HeldKeys.Remove("RightShift");
                if (!f7.Pressed(inp)) return "hotkey: F7 after modifiers released";
                inp.DownKeys.Clear(); inp.DownKeys.Add("J"); inp.HeldKeys.Add("LeftAlt"); inp.HeldKeys.Add("LeftShift");
                if (a.Pressed(inp)) return "hotkey: Alt+J with extra Shift";
                inp.HeldKeys.Remove("LeftShift");
                if (!a.Pressed(inp)) return "hotkey: Alt+J after Shift released";
                inp.HeldKeys.Remove("LeftAlt"); inp.DownKeys.Clear();

                // TryParseSpec / Held (T-123 C1)
                List<string[]> cs; string perr;
                if (!TryParseSpec("", out cs, out perr) || cs.Count != 0 || perr != null) return "hotkey: empty spec parse";
                if (!TryParseSpec("LeftAlt+O|F9", out cs, out perr) || cs.Count != 2 || string.Join("+", cs[0]) != "LeftAlt+O") return "hotkey: two candidates";
                if (!TryParseSpec("Q", out cs, out perr) || cs.Count != 1) return "hotkey: single candidate";
                if (TryParseSpec("F9|Bad+Key+", out cs, out perr) || cs.Count != 0 || perr == null || !perr.Contains("Bad+Key+")) return "hotkey: bad candidate error " + perr;
                var q = new HotkeyBinding("radial", "Radial.Hotkey", "LeftShift+Q", null, null, null);
                var inp2 = new FakeInput();
                if (q.Held(inp2)) return "hotkey: held before resolve";
                q.Resolve(0, true, new FakeGame());
                if (q.Held(inp2)) return "hotkey: nothing held";
                inp2.HeldKeys.Add("LeftShift");
                if (q.Held(inp2)) return "hotkey: combo half held";
                inp2.HeldKeys.Add("Q");
                if (!q.Held(inp2)) return "hotkey: combo held";
                inp2.TypingNow = true;
                if (q.Held(inp2)) return "hotkey: typing blocks hold";
                return SelfTestSettings();
            }
            catch (Exception e) { return "hotkey: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
