using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VoiceBotBridge
{
    // T-102 / ADR-0038: the smart-attack state machine. No Unity: the game is behind ISmartAttackGame (fake in the smarttest stand).
    // Preflight -> Positioning -> Launch -> Leg -> Approach -> Final -> Terminal -> Done; exits Cancelled|WireLost|TrackLost|Failed.

    internal sealed class SubSnap
    {
        public int Uid; public string Name, Type; public bool Alive, IsSub, FreeWire; public SaVec Pos; public double DepthFt, BottomFt, SpeedKn;
        public int Telegraph; public double[] TelegraphKn /*SP._telegraphVelocities, index t+1*/; public double? LayerFt /*at own position, null = none*/; public string TileKey; public int? Preset;
        /// <summary>Depth the boat is ordered to (feet); null = unknown, then "reached" is judged against the layer.</summary>
        public double? OrderedDepthFt;
    }

    internal sealed class TorpSnap
    {
        public int Id; public bool Alive, OnWire, WireLost, SeekerActive, Terminal; public SaVec Pos; public double CourseDeg, SpeedKn, RangeLeftNm, MinRunFt;
        public int SpeedIndex; public double OrderedDepthFt;
        /// <summary>Ammunition file name (used to recognise the torpedo after a load); null = unknown.</summary>
        public string Ammo;
    }

    internal sealed class TrackInfo { public int Id; public string Relation /*hostile|neutral|unknown|friendly*/, Domain, Cls, State /*live|bearing_only|lost*/; public bool SameTile; }

    internal sealed class SmartQuestion
    {
        public string Id, Kind /*neutral_preflight|neutral_on_path|restore*/, Text; public string[] Options /*yes,no | continue,cancel*/; public double DeadlineT; public List<int> NeutralIds;
    }

    internal interface ISmartAttackGame
    {
        double Now { get; }
        bool Paused { get; }
        string LastSavePath { get; }
        string SaveDir { get; }
        SubSnap Sub(int uid);
        /// <summary>AmmunitionForTarget of the game, count &gt; 0, WireGuided. Empty list: err = target_not_allowed | no_wire_torpedo.</summary>
        IList<WireAmmo> WireAmmoFor(int uid, int trackId, out string err);
        TrackInfo Track(int trackId);
        TrackSample? Sample(int trackId);
        /// <summary>Hits of the player's weapons on the track from the game's weapon report (the hit_estimate ledger, ADR-0064); -1 when the counter is off.</summary>
        int HitsOn(int trackId);
        IList<NeutralTrack> Neutrals(int exceptTrackId);
        /// <summary>The existing NeutralNearHits of the direct corridor: null | neutral_near | neutral_check_failed.</summary>
        string DirectCorridor(int uid, int trackId, out string hitsJson);
        string SetTelegraph(int uid, int t);
        string SetOwnDepthPreset(int uid, int preset);
        string SetOwnDepthFt(int uid, double ft);
        int GetLaunchSpeed(int uid, string ammo);
        void SetLaunchSpeed(int uid, string ammo, int index);
        string Fire(int uid, int trackId, string ammo, bool confirmNeutral);
        ISet<int> WeaponIds(int uid);
        TorpSnap FindNewTorpedo(int uid, string ammo, ISet<int> before);
        TorpSnap Torpedo(int torpId);
        void SetWireDepth(int torpId, double ft);
        void SetWireSpeed(int torpId, int index);
        /// <summary>Aim-point shift in miles relative to the track; null = shift 0 and WireDogLeg.Direct.</summary>
        void SetAimShift(int torpId, SaVec? shiftNm);
        void RememberField(int uid, string key, string current);
        void NoteFieldSet(int uid, string key, string value);
        /// <summary>Current value of a remembered field as text: telegraph, sa_depth ("p:&lt;preset&gt;" or "f:&lt;feet&gt;").</summary>
        string FieldNow(int uid, string key);
        void Event(string name, string dataJson);
        void Message(string text);
        void Log(string line);
    }

    internal sealed partial class SmartAttackController
    {
        /// <summary>ADR-0038 section 10: every smart_attack_warn code.</summary>
        public static readonly string[] WarnCodes =
        {
            "no_layer", "depth_unknown", "leg_skipped", "fuel_short", "cone", "offset_unstable", "neutral_on_path", "neutral_avoid",
            "neutral_unavoidable", "wire_speed", "wire_seabed", "track_lost", "position_timeout",
        };

        private sealed class Attack
        {
            public string Id, State = "Preflight", Ammo, Result, Reason, Mode;
            public int Uid, TrackId;
            public WireAmmo A;
            public bool TargetIsSub, ConfirmNeutral, WasFinal;
            public string SubName, SubType;
            public SidePlan Plan;
            public DepthPlan Depth;
            public List<string> Warnings = new List<string>();
            public List<StepOutcome> Steps = new List<StepOutcome>();
            public List<TrackSample> Hist = new List<TrackSample>();
            public ControlMemory Mem = new ControlMemory();
            public object Token = new object();
            public ISet<int> Before;
            public int? TorpId, PrevLaunchSpeed;
            public double StartT, PosStartT, LaunchStartT, LaunchT, LastTick = double.NegativeInfinity, LegOdo, NextRecheck, LegNm, PhiDeg;
            public int Side, Stage;
            public double? LostSince, PrevDir, IntendedDepth, LastDistNm;
            public SaVec? LastTorpPos;
            public int HitsAtLaunch = -1;
            public int UnstableTicks;
            public double? HoldFromT;   // T-101: the submarine evades a torpedo since this time (Positioning waits)
            public bool ConeIn, ConeWarned, UnstableWarned, FuelShortWarned, WireSpeedWarned, SeabedWarned, DepthPlanned, AimDirect, FinalShortWarned;
            public HashSet<int> Known = new HashSet<int>(), Continued = new HashSet<int>(), WarnedTerminal = new HashSet<int>();
        }

        private readonly ISmartAttackGame _g;
        private readonly SmartParams _p;
        private bool _debug;
        /// <summary>Live: [SmartAttack] Debug from the settings window.</summary>
        internal bool Debug { get => _debug; set => _debug = value; }
        private readonly List<Attack> _active = new List<Attack>();
        /// <summary>T-101 / ADR-0071 section 4: true while the submarine evades a torpedo (UnitSteering holder "evasion"): no new attack, positioning waits,
        /// the restore of own fields waits. The torpedo (wire, depth, aim shift) is never held.</summary>
        public Func<int, bool> HeldByEvasion { get; set; }
        private readonly List<Attack> _deferredRestore = new List<Attack>();   // ended while the submarine evaded: the fields come back after that
        private readonly List<string> _recent = new List<string>();
        private Attack _pendingAttack;
        private SmartSaveData _pendingRestore;
        private int _seq, _qseq;

        public SmartAttackController(ISmartAttackGame g, SmartParams p, bool debug) { _g = g; _p = p ?? new SmartParams(); _debug = debug; }

        public SmartQuestion Pending { get; private set; }

        // ---- helpers ------------------------------------------------------------------------------------------------

        private static string N(double d) { return Json.Num(Math.Round(d, 3)); }
        private static string F1(double d) { return d.ToString("0.0", CultureInfo.InvariantCulture); }

        private void Log(string s) { try { _g.Log(s); } catch { } } // the game sink adds the "[SmartAttack] " prefix once

        private void Step(Attack a, string cmd, StepStatus st, string reason, string detail)
        {
            a.Steps.Add(new StepOutcome { Index = a.Steps.Count, Cmd = cmd, Status = st, Reason = reason, Detail = detail });
            Log(a.Id + " step " + cmd + " " + st + (reason != null ? " " + reason : "") + (detail != null ? " (" + detail + ")" : ""));
        }

        private void Ok(Attack a, string cmd, string detail = null) { Step(a, cmd, StepStatus.Ok, null, detail); }

        private void State(Attack a, string state, string reason)
        {
            a.State = state;
            _g.Event("smart_attack_state", "{\"attack_id\":" + Json.Str(a.Id) + ",\"state\":" + Json.Str(state) + ",\"reason\":" + Json.Str(reason) + "}");
        }

        private void Warn(Attack a, string code, string detail = null)
        {
            if (Array.IndexOf(WarnCodes, code) < 0) { Log("unknown warn code " + code); return; }
            if (!a.Warnings.Contains(code)) a.Warnings.Add(code);
            _g.Event("smart_attack_warn", "{\"attack_id\":" + Json.Str(a.Id) + ",\"code\":" + Json.Str(code) + (detail != null ? ",\"detail\":" + Json.Str(detail) : "") + "}");
            Log(a.Id + " warn " + code + (detail != null ? " " + detail : ""));
        }

        private static string Tx(string key, params object[] a)
        {
            return a == null || a.Length == 0 ? UiText.Get(key) : UiText.Fmt(key, a);
        }

        private static string WarnText(string code)
        {
            return Array.IndexOf(WarnTextCodes, code) >= 0 ? UiText.Get("smart.warn." + code) : code;
        }

        private static readonly string[] WarnTextCodes =
        {
            "no_layer", "depth_unknown", "leg_skipped", "fuel_short", "cone", "offset_unstable", "neutral_on_path", "neutral_avoid",
            "neutral_unavoidable", "wire_speed", "wire_seabed", "track_lost", "position_timeout", "no_wire_depth", "layer_too_deep",
        };

        private static SaVec Vel(TrackSample s) { return s.VelKn; }

        private SaVec Aim(SaVec torp, double kn, SaVec trackPos, SaVec vel, WireAmmo a) { return SmartAttackMath.Intercept(torp, kn, trackPos, vel, a.SeekerFovDeg); }

        // ---- Start / Preflight -----------------------------------------------------------------------------------

        /// <summary>Returns null and fills ackJson, or an error code of ADR-0038 section 3.</summary>
        public string Start(SmartAttackArgs args, out string ackJson)
        {
            ackJson = null;
            try { return StartInner(args, out ackJson); }
            catch (Exception e) { Log("start failed: " + e.GetType().Name + ": " + e.Message); return "geometry_error"; }
        }

        private sealed class PlanCtx
        {
            public SubSnap Sub; public TrackInfo Track; public Attack Best; public IList<NeutralTrack> Ns; public double D; public bool IsSub; public SaVec TPos;
        }

        /// <summary>
        /// T-154 E2: a dry check of "can this submarine start a smart attack on this track now", at pick-click time. The same checks as Start up to the
        /// positioning, no side effects (no attack is registered, no event, no question). null = possible, else the refusal code; detail may carry numbers.
        /// </summary>
        public string Preflight(int uid, int trackId, out string detail)
        {
            detail = null;
            try
            {
                PlanCtx c;
                return Select(new SmartAttackArgs { Uid = uid, TargetId = trackId }, out c, out detail);
            }
            catch (Exception e) { Log("preflight failed: " + e.GetType().Name + ": " + e.Message); return "geometry_error"; }
        }

        private string Select(SmartAttackArgs args, out PlanCtx ctx, out string detail)
        {
            ctx = null; detail = null;
            if (args == null || !args.TargetId.HasValue) return "bad_args";
            var sub = _g.Sub(args.Uid);
            if (sub == null || !sub.Alive) return "no_unit";
            if (!sub.IsSub) return "not_sub";
            if (_active.Any(x => x.Uid == args.Uid)) return "busy";
            if (Held(args.Uid)) return "evading";
            int trackId = args.TargetId.Value;
            var track = _g.Track(trackId);
            if (track == null) return "no_track";
            if (track.State == "bearing_only") return "track_bearing_only";
            var smp = _g.Sample(trackId);
            if (track.State != "live" || !smp.HasValue) return "no_track";
            if (track.Relation != "hostile") return "target_not_allowed";

            string err;
            IList<WireAmmo> raw = _g.WireAmmoFor(args.Uid, trackId, out err);
            if (raw == null || raw.Count == 0) return err ?? "no_wire_torpedo";
            var cands = new List<WireAmmo>();
            foreach (var w in raw)
            {
                string bad = SmartAttackMath.ValidateAmmo(w);
                if (bad != null) { Log("ammo_params_invalid " + w.File + " (" + bad + ")"); continue; }
                if (args.Ammo != null && w.File != args.Ammo) continue;
                cands.Add(w);
            }
            if (cands.Count == 0) return "no_wire_torpedo";
            if (!sub.FreeWire) return "no_free_wire";

            bool isSub = track.Domain == "Subsurface";
            var ns = _g.Neutrals(trackId) ?? new List<NeutralTrack>();
            SaVec tpos = smp.Value.Pos, tvel = Vel(smp.Value);
            double d = SaVec.Dist(sub.Pos, tpos);

            // best candidate: reaches the target with the lead-off, then the longest range at stage 0
            Attack best = null;
            string firstErr = null;
            double maxRange = 0;
            foreach (var w in cands.OrderByDescending(x => x.RangesNm[0]))
            {
                DepthPlan dp = SmartAttackMath.PlanDepths(isSub, sub.LayerFt, track.SameTile, smp.Value.DepthFt, sub.BottomFt, w, 0, sub.Preset, _p);
                if (dp.Error != null) { firstErr = firstErr ?? dp.Error; continue; }
                SidePlan[] sides = SmartAttackMath.PlanSides(sub.Pos, tpos, tvel, w, ns, args.Uid, _p);
                if (sides[0].Leg.OutOfRange) { firstErr = firstErr ?? "out_of_range"; maxRange = Math.Max(maxRange, w.RangesNm[0]); continue; }
                best = new Attack { A = w, Ammo = w.File, Depth = dp, Plan = sides[0] };
                break;
            }
            if (best == null)
            {
                if (firstErr == "out_of_range") detail = F1(d) + " nm, max " + F1(maxRange) + " nm";
                return firstErr ?? "no_wire_torpedo";
            }
            ctx = new PlanCtx { Sub = sub, Track = track, Best = best, Ns = ns, D = d, IsSub = isSub, TPos = tpos };
            return null;
        }

        private string StartInner(SmartAttackArgs args, out string ackJson)
        {
            ackJson = null;
            PlanCtx pc; string detail;
            string code = Select(args, out pc, out detail);
            if (code != null) { if (detail != null) Log("start refused " + code + ": " + detail); return code; }
            var sub = pc.Sub; var track = pc.Track; var best = pc.Best; var ns = pc.Ns; double d = pc.D; bool isSub = pc.IsSub;
            int trackId = args.TargetId.Value;

            var a = best;
            a.Id = "sa-" + (++_seq);
            a.Uid = args.Uid; a.TrackId = trackId; a.TargetIsSub = isSub; a.ConfirmNeutral = args.ConfirmNeutral;
            a.SubName = sub.Name; a.SubType = sub.Type; a.StartT = _g.Now;
            a.Side = a.Plan.Side; a.PhiDeg = a.Plan.PhiDeg; a.LegNm = a.Plan.Leg.Skipped ? 0 : a.Plan.Leg.LegNm;
            if (a.Plan.Leg.Skipped) a.Warnings.Add("leg_skipped");
            foreach (string w in a.Depth.Warnings) if (!a.Warnings.Contains(w)) a.Warnings.Add(w);
            Ok(a, "preflight", "ammo=" + a.Ammo + " D=" + F1(d));
            Ok(a, "plan", "side=" + (a.Side > 0 ? "right" : "left") + " phi=" + F1(a.PhiDeg) + " L=" + F1(a.LegNm));

            // neutrals: the path of the smart attack (both sides checked) and the direct corridor of `attack`
            var hitIds = new List<int>();
            foreach (var h in a.Plan.Hits) if (!hitIds.Contains(h.Id)) hitIds.Add(h.Id);
            string hitsJson;
            string direct = _g.DirectCorridor(args.Uid, trackId, out hitsJson);
            if (direct == "neutral_check_failed") return "neutral_check_failed";
            if (a.ConfirmNeutral) { foreach (int id in hitIds) a.Known.Add(id); }
            if (!a.ConfirmNeutral && (hitIds.Count > 0 || direct == "neutral_near"))
            {
                if (Pending != null) return "busy";
                Step(a, "neutral_guard", StepStatus.Ok, null, hitIds.Count > 0 ? "path" : "direct");
                AskNeutralPreflight(a, hitIds, ns, sub.Pos, hitsJson);
                _active.Add(a);
                State(a, "Preflight", "question");
                ackJson = "{\"result\":\"question\",\"attack_id\":" + Json.Str(a.Id) + "}";
                return null;
            }
            Step(a, "neutral_guard", StepStatus.Ok, null, null);
            _active.Add(a);
            BeginPositioning(a, sub);
            ackJson = AckJson(a);
            return null;
        }

        private void AskNeutralPreflight(Attack a, List<int> ids, IList<NeutralTrack> ns, SaVec subPos, string hitsJson)
        {
            string cls = "", miles = "";
            NeutralTrack nt = ids.Count > 0 ? ns.FirstOrDefault(x => x.Id == ids[0]) : null;
            if (nt != null) { cls = nt.Cls ?? ""; miles = F1(SaVec.Dist(subPos, nt.Pos)); }
            string text = ids.Count > 0
                ? Tx("smart.q.neutral_near", nt != null ? "#" + nt.Id + " " + cls : "", miles)
                : Tx("smart.q.neutral_line");
            Pending = new SmartQuestion
            {
                Id = "q" + (++_qseq), Kind = "neutral_preflight", Text = text, Options = new[] { "yes", "no" },
                DeadlineT = _g.Now + _p.NeutralConfirmTimeoutS, NeutralIds = new List<int>(ids),
            };
            _pendingAttack = a;
            _g.Message(text);
            _g.Event("smart_attack_question", "{\"attack_id\":" + Json.Str(a.Id) + ",\"kind\":\"neutral_preflight\",\"question_id\":" + Json.Str(Pending.Id) + "}");
        }

        private string AckJson(Attack a)
        {
            double r0 = a.A.RangesNm[0];
            double d = 0;
            var smp = _g.Sample(a.TrackId);
            var sub = _g.Sub(a.Uid);
            if (smp.HasValue && sub != null) d = SaVec.Dist(sub.Pos, smp.Value.Pos);
            bool fs;
            double f = 1.0 - a.LegNm / r0;
            int fin = SmartAttackMath.FinalStage(a.A, f, SmartAttackMath.PathAfterLeg(d, a.LegNm, a.PhiDeg, smp.HasValue ? smp.Value.VelKn.Len : 0, a.A.SpeedsKn[0]), _p, out fs);
            var sb = new StringBuilder("{\"attack_id\":" + Json.Str(a.Id));
            sb.Append(",\"plan\":{\"side\":" + Json.Str(a.Side > 0 ? "right" : "left") + ",\"phi\":" + N(a.PhiDeg) + ",\"leg_nm\":" + N(a.LegNm));
            sb.Append(",\"stages\":{\"leg_kn\":" + N(a.A.SpeedsKn[0]) + ",\"final_kn\":" + N(a.A.SpeedsKn[fin]) + ",\"fuel_short\":" + Json.Bool(fs) + "}");
            sb.Append(",\"ammo\":" + Json.Str(a.Ammo) + ",\"torp_depth_ft\":" + (a.Depth.TorpDepthFt.HasValue ? N(a.Depth.TorpDepthFt.Value) : "null") + "}");
            sb.Append(",\"warnings\":[" + string.Join(",", a.Warnings.Select(Json.Str).ToArray()) + "]}");
            return sb.ToString();
        }

        // ---- questions ----------------------------------------------------------------------------------------------

        public void Answer(string questionId, string option)
        {
            var q = Pending;
            if (q == null || q.Id != questionId) return;
            Pending = null;
            try
            {
                if (q.Kind == "neutral_preflight") AnswerPreflight(q, option);
                else if (q.Kind == "neutral_on_path") AnswerOnPath(q, option);
                else if (q.Kind == "restore") AnswerRestore(option);
            }
            catch (Exception e) { Log("answer failed: " + e.Message); }
        }

        private void AnswerPreflight(SmartQuestion q, string option)
        {
            var a = _pendingAttack;
            _pendingAttack = null;
            if (a == null) return;
            if (option != "yes") { Step(a, "neutral_guard", StepStatus.Error, "neutral_near", "answer=" + option); EndFail(a, "neutral_near"); return; }
            a.ConfirmNeutral = true;
            foreach (int id in q.NeutralIds) a.Known.Add(id);
            Step(a, "neutral_guard", StepStatus.Ok, null, "confirmed");
            var sub = _g.Sub(a.Uid);
            if (sub == null || !sub.Alive) { EndFail(a, "no_unit"); return; }
            BeginPositioning(a, sub);
        }

        private void AnswerOnPath(SmartQuestion q, string option)
        {
            var a = _pendingAttack;
            _pendingAttack = null;
            if (a == null || !_active.Contains(a)) return;
            string ids = string.Join(",", q.NeutralIds.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
            if (option == "continue")
            {
                foreach (int id in q.NeutralIds) { a.Continued.Add(id); a.Known.Add(id); }
                Step(a, "neutral_on_path", StepStatus.Ok, null, ids + ": continue");
            }
            else
            {
                Step(a, "neutral_on_path", StepStatus.Ok, null, ids + ": cancel");
                End(a, "cancelled", "neutral_cancel");
            }
        }

        private void QuestionTimeout()
        {
            var q = Pending;
            if (q == null) return;
            Pending = null;
            if (q.Kind == "neutral_preflight")
            {
                var a = _pendingAttack;
                _pendingAttack = null;
                if (a != null) { Step(a, "neutral_guard", StepStatus.Error, "neutral_near", "timeout"); EndFail(a, "neutral_near"); }
            }
            else if (q.Kind == "neutral_on_path")
            {
                var a = _pendingAttack;
                _pendingAttack = null;
                if (a == null || !_active.Contains(a)) return;
                string ids = string.Join(",", q.NeutralIds.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
                Step(a, "neutral_on_path", StepStatus.Ok, null, ids + ": timeout");
                AvoidOrCancel(a, q.NeutralIds);
            }
            else if (q.Kind == "restore")
            {
                AnswerRestore("no");
            }
        }

        private void AvoidOrCancel(Attack a, List<int> newIds)
        {
            var t = a.TorpId.HasValue ? _g.Torpedo(a.TorpId.Value) : null;
            var smp = _g.Sample(a.TrackId);
            if (t == null || !smp.HasValue) { End(a, "failed", "no_data_for_avoid"); return; }
            var ns = (_g.Neutrals(a.TrackId) ?? new List<NeutralTrack>()).Where(n => !a.Continued.Contains(n.Id)).ToList();
            double fuel = SmartAttackMath.FuelFraction(t.RangeLeftNm, SmartAttackMath.RangeAtNm(a.A, t.SpeedKn));
            var plan = SmartAttackMath.AvoidPlan(t.Pos, smp.Value.Pos, Vel(smp.Value), fuel, a.Stage, a.A, ns, newIds, _p);
            if (plan == null)
            {
                Warn(a, "neutral_unavoidable");
                Step(a, "neutral_avoid", StepStatus.Error, "neutral_unavoidable", null);
                End(a, "cancelled", "neutral_unavoidable");
                return;
            }
            foreach (int id in newIds) a.Known.Add(id);
            a.Side = plan.Side; a.PhiDeg = plan.PhiDeg; a.LegNm = plan.Leg.LegNm; a.LegOdo = 0; a.PrevDir = null; a.LastTorpPos = t.Pos;
            Warn(a, "neutral_avoid");
            Step(a, "neutral_avoid", StepStatus.Ok, null, "side=" + (plan.Side > 0 ? "right" : "left") + " L=" + F1(plan.Leg.LegNm));
            State(a, "Leg", "neutral_avoid");
        }

        // ---- Positioning ----------------------------------------------------------------------------------------------

        private void Remember(Attack a, string key, string current)
        {
            if (current == null) return;
            a.Mem.RememberField(a.Token, key, current);
            _g.RememberField(a.Uid, key, current);
        }

        private void NoteSet(Attack a, string key, string value)
        {
            a.Mem.NoteFieldSet(a.Token, key, value);
            _g.NoteFieldSet(a.Uid, key, value);
        }

        private bool Held(int uid)
        {
            try { return HeldByEvasion != null && HeldByEvasion(uid); }
            catch (Exception) { return false; }
        }

        private void BeginPositioning(Attack a, SubSnap sub)
        {
            State(a, "Positioning", "start");
            a.PosStartT = _g.Now;
            // own speed limit
            double limit = a.A.WireBreakKn - 1.0;
            double curKn = sub.TelegraphKn != null && sub.Telegraph + 1 >= 0 && sub.Telegraph + 1 < sub.TelegraphKn.Length ? sub.TelegraphKn[sub.Telegraph + 1] : sub.SpeedKn;
            if (curKn > limit && sub.TelegraphKn != null)
            {
                int best = -1;
                for (int t = 0; t + 1 < sub.TelegraphKn.Length; t++) if (t >= 0 && sub.TelegraphKn[t + 1] <= limit) best = t;
                if (best < 0) best = 0;
                Remember(a, "telegraph", sub.Telegraph.ToString(CultureInfo.InvariantCulture));
                string e = _g.SetTelegraph(a.Uid, best);
                if (e == null) { NoteSet(a, "telegraph", best.ToString(CultureInfo.InvariantCulture)); Ok(a, "own_speed_limit", "telegraph " + sub.Telegraph + "->" + best); }
                else Step(a, "own_speed_limit", StepStatus.Error, e, null);
            }
            else Step(a, "own_speed_limit", StepStatus.Skipped, "not_needed", null);

            // own depth
            if (a.Depth.OwnPreset.HasValue)
            {
                string cur = _g.FieldNow(a.Uid, "sa_depth");
                Remember(a, "sa_depth", cur);
                string e = _g.SetOwnDepthPreset(a.Uid, a.Depth.OwnPreset.Value);
                if (e == null) { NoteSet(a, "sa_depth", a.Depth.OwnPreset.Value.ToString(CultureInfo.InvariantCulture)); Ok(a, "own_depth", "preset " + a.Depth.OwnPreset.Value); }
                else { Step(a, "own_depth", StepStatus.Error, e, null); a.Depth.OwnPreset = null; }
            }
            else Step(a, "own_depth", StepStatus.Skipped, a.Depth.NoDive ? "no_dive" : "not_needed", null);
            foreach (string w in a.Depth.Warnings) if (w == "no_layer" || w == "depth_unknown" || w == "no_wire_depth" || w == "layer_too_deep") Warn(a, w);
            if (a.Plan.Leg.Skipped) Warn(a, "leg_skipped");
            _g.Message(Tx("smart.msg.started", a.TrackId, Tx(a.Side > 0 ? "smart.msg.side.right" : "smart.msg.side.left"), F1(a.PhiDeg), F1(a.LegNm), a.Ammo) +
                       (a.Warnings.Count > 0 ? Tx("smart.msg.started.warns", string.Join("; ", a.Warnings.Select(WarnText).ToArray())) : ""));
        }

        private bool PositionReady(Attack a, SubSnap sub)
        {
            double limit = a.A.WireBreakKn - 0.5;
            if (sub.SpeedKn > limit) return false;
            if (!a.Depth.OwnPreset.HasValue) return true;
            if (sub.OrderedDepthFt.HasValue) return Math.Abs(sub.DepthFt - sub.OrderedDepthFt.Value) <= _p.OwnDepthTolFt;
            if (!sub.LayerFt.HasValue) return true;
            return a.Depth.OwnPreset.Value == 4 ? sub.DepthFt >= sub.LayerFt.Value + _p.OwnDepthTolFt : sub.DepthFt <= sub.LayerFt.Value - _p.OwnDepthTolFt;
        }

        private void PositionTick(Attack a, double now)
        {
            var sub = _g.Sub(a.Uid);
            if (sub == null || !sub.Alive) { End(a, "failed", "no_unit"); return; }
            if (PositionReady(a, sub)) { Ok(a, "own_ready"); StartLaunch(a, now); return; }
            if (now - a.PosStartT >= _p.PositionTimeoutS)
            {
                Warn(a, "position_timeout");
                StartLaunch(a, now);
            }
        }

        // ---- Launch -----------------------------------------------------------------------------------------------------

        private void StartLaunch(Attack a, double now)
        {
            var sub = _g.Sub(a.Uid);
            if (sub == null || !sub.FreeWire) { Step(a, "launch", StepStatus.Error, "no_free_wire", null); EndFail(a, "no_free_wire"); return; }
            var smp = _g.Sample(a.TrackId);
            var tr = _g.Track(a.TrackId);
            if (!smp.HasValue || tr == null || tr.State != "live") { Step(a, "launch", StepStatus.Error, "no_track", null); EndFail(a, "no_track"); return; }
            // re-check the neutrals just before firing (ADR section 7): the player has not agreed to anything new
            string hj;
            string direct = a.ConfirmNeutral ? null : _g.DirectCorridor(a.Uid, a.TrackId, out hj);
            if (direct == "neutral_near" || direct == "neutral_check_failed") { Step(a, "neutral_guard", StepStatus.Error, direct, "before launch"); EndFail(a, direct); return; }
            a.Before = _g.WeaponIds(a.Uid);
            a.PrevLaunchSpeed = _g.GetLaunchSpeed(a.Uid, a.Ammo);
            Remember(a, "launch_speed", a.PrevLaunchSpeed.Value.ToString(CultureInfo.InvariantCulture));
            _g.SetLaunchSpeed(a.Uid, a.Ammo, 0);
            NoteSet(a, "launch_speed", "0");
            Ok(a, "launch_speed", a.PrevLaunchSpeed.Value + "->0");
            string err = _g.Fire(a.Uid, a.TrackId, a.Ammo, a.ConfirmNeutral);
            if (err != null) { Step(a, "launch", StepStatus.Error, err, null); EndFail(a, err); return; }
            a.LaunchStartT = now;
            State(a, "Launch", "fired");
            Ok(a, "launch", "ammo=" + a.Ammo);
        }

        private void RestoreLaunchSpeed(Attack a)
        {
            if (!a.PrevLaunchSpeed.HasValue) return;
            int prev = a.PrevLaunchSpeed.Value;
            a.PrevLaunchSpeed = null;
            try
            {
                var e = a.Mem.Peek(a.Token);
                string d = ControlMemory.DecideField(e, "launch_speed", _g.GetLaunchSpeed(a.Uid, a.Ammo).ToString(CultureInfo.InvariantCulture));
                if (d == "restore") _g.SetLaunchSpeed(a.Uid, a.Ammo, prev);
            }
            catch (Exception ex) { Log("launch speed restore failed: " + ex.Message); }
        }

        private void LaunchPump(Attack a, double now)
        {
            var t = _g.FindNewTorpedo(a.Uid, a.Ammo, a.Before);
            if (t == null)
            {
                if (now - a.LaunchStartT >= _p.LaunchTimeoutS)
                {
                    Step(a, "launch", StepStatus.Error, "launch_timeout", null);
                    EndFail(a, "launch_timeout");
                }
                return;
            }
            a.TorpId = t.Id; a.LaunchT = now;
            a.HitsAtLaunch = _g.HitsOn(a.TrackId);
            // same frame: honest depth first (closes the leak of the game's own pick), then stage 0, then give the launch speed back
            if (a.Depth.TorpDepthFt.HasValue && a.A.WireControlsDepth)
            {
                a.IntendedDepth = a.Depth.TorpDepthFt.Value;
                _g.SetWireDepth(t.Id, a.IntendedDepth.Value);
                Ok(a, "wire_depth", N(a.IntendedDepth.Value) + " ft");
            }
            else Step(a, "wire_depth", StepStatus.Skipped, a.A.WireControlsDepth ? "not_set" : "no_wire_depth", null);
            if (t.OnWire) _g.SetWireSpeed(t.Id, 0);
            a.Stage = 0;
            RestoreLaunchSpeed(a);
            if (!t.OnWire) { End(a, "wire_lost", "not_on_wire"); return; }
            a.LastTorpPos = t.Pos; a.LegOdo = 0; a.LastTick = now; a.NextRecheck = now + _p.NeutralRecheckS;
            if (a.LegNm <= 1e-9)
            {
                _g.SetAimShift(t.Id, null); a.AimDirect = true;
                Step(a, "leg_start", StepStatus.Skipped, "leg_skipped", null);
                State(a, "Approach", "no_leg");
            }
            else
            {
                Ok(a, "leg_start", "side=" + (a.Side > 0 ? "right" : "left") + " L=" + F1(a.LegNm));
                State(a, "Leg", "launched");
            }
        }

        // ---- Pump ---------------------------------------------------------------------------------------------------------

        public void Pump(double now)
        {
            if (Pending != null && now >= Pending.DeadlineT) QuestionTimeout();
            foreach (var d in _deferredRestore.ToArray())
            {
                if (Held(d.Uid)) continue;
                _deferredRestore.Remove(d);
                try { RestoreFields(d); } catch (Exception e) { Log("deferred restore failed: " + e.Message); }
            }
            foreach (var a in _active.ToArray())
            {
                if (!_active.Contains(a)) continue;
                try
                {
                    if (a.State == "Launch") LaunchPump(a, now);
                    if (!_active.Contains(a) || a.State == "Launch") continue;
                    if (_g.Paused) continue;
                    if (now - a.LastTick < 1.0) continue;
                    a.LastTick = now;
                    Record(a, now);
                    if (a.State == "Positioning")
                    {
                        if (Held(a.Uid))
                        {
                            if (!a.HoldFromT.HasValue) { a.HoldFromT = now; Ok(a, "evasion_hold", "positioning"); }
                            continue;
                        }
                        if (a.HoldFromT.HasValue) { a.PosStartT += now - a.HoldFromT.Value; a.HoldFromT = null; }
                        PositionTick(a, now);
                    }
                    else if (a.State == "Leg" || a.State == "Approach" || a.State == "Final" || a.State == "Terminal") Tick(a, now);
                }
                catch (Exception e)
                {
                    Log(a.Id + " tick failed: " + e.GetType().Name + ": " + e.Message);
                    if (_active.Contains(a)) End(a, "failed", "geometry_error");
                }
            }
        }

        private void Record(Attack a, double now)
        {
            var s = _g.Sample(a.TrackId);
            if (!s.HasValue) return;
            var v = s.Value;
            v.T = now;
            a.Hist.Add(v);
            while (a.Hist.Count > 0 && now - a.Hist[0].T > 300) a.Hist.RemoveAt(0);
        }

        private void Tick(Attack a, double now)
        {
            var sub = _g.Sub(a.Uid);
            if (sub == null || !sub.Alive) { End(a, "wire_lost", "sub_dead"); return; }
            var t = _g.Torpedo(a.TorpId.Value);
            if (t == null || !t.Alive) { Finish(a); return; }
            if (!t.OnWire || t.WireLost) { End(a, "wire_lost", "wire"); return; }

            // limits of the wire
            if (sub.SpeedKn > a.A.WireBreakKn - 1.0 + 0.5 && !a.WireSpeedWarned) { a.WireSpeedWarned = true; Warn(a, "wire_speed"); }
            if (a.A.SeafloorClearanceFt > 0 && sub.BottomFt - sub.DepthFt < a.A.SeafloorClearanceFt + 20 && !a.SeabedWarned) { a.SeabedWarned = true; Warn(a, "wire_seabed"); }

            // seeker first: from the tick it homes the mod never touches the depth again (the game climbs to the hull by itself)
            if (a.State != "Terminal" && t.SeekerActive && t.Terminal)
            {
                _g.SetAimShift(t.Id, null);
                Ok(a, "terminal");
                State(a, "Terminal", "seeker");
            }

            if (a.State == "Leg" || a.State == "Approach" || a.State == "Final")
                if (a.IntendedDepth.HasValue && a.A.WireControlsDepth && Math.Abs(t.OrderedDepthFt - a.IntendedDepth.Value) > 1.0)
                    _g.SetWireDepth(t.Id, a.IntendedDepth.Value);

            var info = _g.Track(a.TrackId);
            var smp = _g.Sample(a.TrackId);
            bool live = info != null && info.State == "live" && smp.HasValue;
            if (smp.HasValue) a.LastDistNm = SaVec.Dist(t.Pos, smp.Value.Pos);
            if (a.State == "Terminal")
            {
                RecheckNeutrals(a, t, smp, now);
                return;
            }
            if (!live)
            {
                if (!a.LostSince.HasValue) a.LostSince = now;
                if (now - a.LostSince.Value >= _p.TrackLostS)
                {
                    Warn(a, "track_lost");
                    _g.SetAimShift(t.Id, null);
                    End(a, "track_lost", "track");
                }
                return;
            }
            a.LostSince = null;
            var trk = smp.Value;
            double dist = SaVec.Dist(t.Pos, trk.Pos);
            double fuel = SmartAttackMath.FuelFraction(t.RangeLeftNm, SmartAttackMath.RangeAtNm(a.A, t.SpeedKn));

            // cone warning (once per entry)
            bool inCone = SmartAttackMath.InCone(t.CourseDeg, t.Pos, trk.Pos, _p.ConeDeg);
            if (inCone && !a.ConeIn && (a.State == "Approach" || a.State == "Final")) Warn(a, "cone");
            a.ConeIn = inCone;

            // the target noticed the torpedo: final dash (Q1: only this trigger)
            if ((a.State == "Leg" || a.State == "Approach") && !a.WasFinal)
            {
                Notice n = SmartAttackMath.Noticed(a.Hist, a.LaunchT, now, a.TargetIsSub, _p);
                if (n != null)
                {
                    Ok(a, "noticed", n.Kind + " " + F1(n.Delta));
                    EnterFinal(a, t, trk, fuel);
                    RecheckNeutrals(a, t, smp, now);
                    return;
                }
            }

            if (a.State == "Leg") LegTick(a, t, trk, dist, fuel, now);
            else if (a.State == "Final") FinalTick(a, t, trk, fuel);
            RecheckNeutrals(a, t, smp, now);
            if (_debug && _active.Contains(a)) Log(a.Id + " tick D=" + F1(dist) + " course=" + F1(t.CourseDeg) + " F=" + F1(fuel) + " stage=" + a.Stage + " depth=" + F1(t.OrderedDepthFt) + " state=" + a.State);
        }

        private void LegTick(Attack a, TorpSnap t, TrackSample trk, double dist, double fuel, double now)
        {
            if (a.LastTorpPos.HasValue) a.LegOdo += SaVec.Dist(t.Pos, a.LastTorpPos.Value);
            a.LastTorpPos = t.Pos;
            double v = Math.Max(t.SpeedKn, a.A.SpeedsKn[0]);
            // range check: leg stops when the fuel no longer allows the rest
            SaVec ic = Aim(t.Pos, v, trk.Pos, Vel(trk), a.A);
            double remain = SaVec.Dist(t.Pos, ic);
            bool rangeShort = (fuel - _p.FuelReservePct / 100.0) * SmartAttackMath.RangeAtNm(a.A, v) < _p.RangeMargin * remain;
            if (a.LegOdo >= a.LegNm || rangeShort)
            {
                _g.SetAimShift(t.Id, null); a.AimDirect = true;
                Ok(a, "leg_end", F1(a.LegOdo) + " nm" + (rangeShort ? " (range)" : ""));
                State(a, a.WasFinal ? "Final" : "Approach", rangeShort ? "range" : "leg_done");
                return;
            }
            double aNm = SmartAttackMath.CarrotDistance(dist, a.A, _p);
            double dir;
            SaVec carrot = SmartAttackMath.CarrotPoint(t.Pos, trk.Pos, a.Side, a.PhiDeg, aNm, a.PrevDir, _p, out dir);
            a.PrevDir = dir;
            SaVec shift = SmartAttackMath.ShiftFor(carrot, trk.Pos, Vel(trk), t.Pos, v, dist, _p);
            _g.SetAimShift(t.Id, shift);
            a.AimDirect = false;
            // actual lead-off angle against the line of sight
            double actual = Math.Abs(NormDiff(t.CourseDeg, SaVec.Bearing(t.Pos, trk.Pos)));
            if (a.LegOdo > 0.3 && dist > 2 * aNm)
            {
                if (Math.Abs(actual - a.PhiDeg) > _p.OffsetUnstableDeg) a.UnstableTicks++; else a.UnstableTicks = 0;
                if (a.UnstableTicks >= _p.OffsetUnstableTicks && !a.UnstableWarned) { a.UnstableWarned = true; Warn(a, "offset_unstable", F1(actual) + " deg"); }
            }
            if (_debug) Log(a.Id + " leg angle=" + F1(actual) + " shift=" + F1(shift.X) + "," + F1(shift.Y) + " A=" + F1(aNm));
        }

        private static double NormDiff(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180) d -= 360;
            if (d <= -180) d += 360;
            return d;
        }

        private void EnterFinal(Attack a, TorpSnap t, TrackSample trk, double fuel)
        {
            _g.SetAimShift(t.Id, null); a.AimDirect = true;
            a.WasFinal = true;
            double ftOrdered = 0;
            if (a.A.WireControlsDepth)
            {
                double depth;
                if (!a.TargetIsSub) depth = SmartAttackMath.ClampTorpDepth(_p.FinalShipDepthFt, t.MinRunFt, a.A.MaxDepthFt, false, _p);
                else depth = trk.DepthFt.HasValue ? SmartAttackMath.ClampTorpDepth(trk.DepthFt.Value, t.MinRunFt, a.A.MaxDepthFt, true, _p) : (a.IntendedDepth ?? t.OrderedDepthFt);
                a.IntendedDepth = depth; ftOrdered = depth;
                _g.SetWireDepth(t.Id, depth);
            }
            SaVec ic = Aim(t.Pos, a.A.SpeedsKn[0], trk.Pos, Vel(trk), a.A);
            bool fs;
            int stage = SmartAttackMath.FinalStage(a.A, fuel, SaVec.Dist(t.Pos, ic), _p, out fs);
            a.Stage = stage;
            _g.SetWireSpeed(t.Id, stage);
            if (fs && !a.FuelShortWarned) { a.FuelShortWarned = true; Warn(a, "fuel_short"); }
            Ok(a, "final", "stage=" + stage + " depth=" + N(ftOrdered));
            State(a, "Final", "noticed");
        }

        private void FinalTick(Attack a, TorpSnap t, TrackSample trk, double fuel)
        {
            if (a.TargetIsSub && a.A.WireControlsDepth && trk.DepthFt.HasValue)
            {
                double depth = SmartAttackMath.ClampTorpDepth(trk.DepthFt.Value, t.MinRunFt, a.A.MaxDepthFt, true, _p);
                if (!a.IntendedDepth.HasValue || Math.Abs(depth - a.IntendedDepth.Value) > 1.0) { a.IntendedDepth = depth; _g.SetWireDepth(t.Id, depth); }
            }
            SaVec ic = Aim(t.Pos, a.A.SpeedsKn[0], trk.Pos, Vel(trk), a.A);
            bool fs;
            int stage = SmartAttackMath.FinalStage(a.A, fuel, SaVec.Dist(t.Pos, ic), _p, out fs);
            if (stage < a.Stage) { a.Stage = stage; _g.SetWireSpeed(t.Id, stage); }
            if (fs && !a.FuelShortWarned) { a.FuelShortWarned = true; Warn(a, "fuel_short"); }
        }

        /// <summary>
        /// The torpedo is gone. A hit target often stays on the plot (a hit ship sinks slowly, the contact lingers), so "the track is still there" is no miss:
        /// the game's weapon report decides (a new hit on the track since the launch); without the counter, a torpedo that vanished while homing next to the target is a hit.
        /// </summary>
        private void Finish(Attack a)
        {
            var tr = _g.Track(a.TrackId);
            if (tr == null) { End(a, "hit", "torpedo_gone"); return; }
            int hits = a.HitsAtLaunch >= 0 ? _g.HitsOn(a.TrackId) : -1;
            if (hits >= 0) { End(a, hits > a.HitsAtLaunch ? "hit" : "miss", hits > a.HitsAtLaunch ? "hit_report" : "torpedo_gone"); return; }
            bool near = a.State == "Terminal" && a.LastDistNm.HasValue && a.LastDistNm.Value <= _p.HitNearNm;
            End(a, near ? "hit" : "miss", near ? "near_target" : "torpedo_gone");
        }

        // ---- neutrals on the remaining path --------------------------------------------------------------------------

        private void RecheckNeutrals(Attack a, TorpSnap t, TrackSample? smp, double now)
        {
            if (now < a.NextRecheck || !smp.HasValue) return;
            a.NextRecheck = now + _p.NeutralRecheckS;
            var trk = smp.Value;
            IList<NeutralTrack> ns = _g.Neutrals(a.TrackId) ?? new List<NeutralTrack>();
            SaVec start = t.Pos;
            SaVec legEnd = start;
            if (a.State == "Leg" && a.PrevDir.HasValue) legEnd = start + SaVec.FromBearing(a.PrevDir.Value, Math.Max(0, a.LegNm - a.LegOdo));
            SaVec ic = Aim(legEnd, Math.Max(t.SpeedKn, a.A.SpeedsKn[0]), trk.Pos, Vel(trk), a.A);
            var hits = SmartAttackMath.NeutralsOnPath(SmartAttackMath.Path(start, legEnd, ic, a.A, _p), ns, a.A, _p);
            var fresh = hits.Where(h => !a.Known.Contains(h.Id)).ToList();
            if (fresh.Count == 0) return;
            if (a.State == "Terminal")
            {
                foreach (var h in fresh) if (a.WarnedTerminal.Add(h.Id)) { Warn(a, "neutral_on_path", "#" + h.Id + " terminal"); Step(a, "neutral_on_path", StepStatus.Ok, null, "#" + h.Id + ": terminal"); }
                return;
            }
            if (Pending != null && !(Pending.Kind == "neutral_on_path" && _pendingAttack == a)) return;   // one dialog at a time: asked again at the next check
            foreach (var h in fresh)
            {
                if (Pending != null && Pending.NeutralIds.Contains(h.Id)) continue;
                if (Pending == null)
                {
                    Pending = new SmartQuestion
                    {
                        Id = "q" + (++_qseq), Kind = "neutral_on_path", Options = new[] { "continue", "cancel" },
                        DeadlineT = _g.Now + _p.NeutralRunTimeoutS, NeutralIds = new List<int>(),
                    };
                    _pendingAttack = a;
                }
                Pending.NeutralIds.Add(h.Id);
                Warn(a, "neutral_on_path", "#" + h.Id);
                _g.Event("smart_attack_warn", "{\"attack_id\":" + Json.Str(a.Id) + ",\"code\":\"neutral_on_path\",\"track_id\":" + h.Id.ToString(CultureInfo.InvariantCulture) + "}");
            }
            string list = string.Join(", ", Pending.NeutralIds.Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)).ToArray());
            double miles = fresh[0].AlongNm;
            Pending.Text = Tx("smart.q.neutral_path", list, F1(miles));
            _g.Message(Pending.Text);
        }

        // ---- end of an attack -------------------------------------------------------------------------------------------

        private void EndFail(Attack a, string code) { End(a, "failed", code); }

        private void RestoreFields(Attack a)
        {
            RestoreLaunchSpeed(a);
            var e = a.Mem.Peek(a.Token);
            if (e == null) return;
            foreach (string key in new[] { "telegraph", "sa_depth" })
            {
                if (!e.Fields.ContainsKey(key)) continue;
                string now = _g.FieldNow(a.Uid, key);
                string decision = ControlMemory.DecideField(e, key, now);
                string cmd = "restore_" + key;
                if (decision != "restore") { Step(a, cmd, StepStatus.Skipped, decision, null); continue; }
                string saved = e.Fields[key].Saved, err = null;
                if (key == "telegraph") { int tg; if (int.TryParse(saved, out tg)) err = _g.SetTelegraph(a.Uid, tg); }
                else if (saved != null && saved.StartsWith("f:", StringComparison.Ordinal)) err = _g.SetOwnDepthFt(a.Uid, double.Parse(saved.Substring(2), CultureInfo.InvariantCulture));
                else { int pr; if (int.TryParse(saved, out pr)) err = _g.SetOwnDepthPreset(a.Uid, pr); }
                if (err == null) Ok(a, cmd, saved); else Step(a, cmd, StepStatus.Error, err, null);
            }
            a.Mem.Take(a.Token);
        }

        private static string ResultText(string result, string reason)
        {
            switch (result)
            {
                case "hit": case "miss": case "wire_lost": case "track_lost": case "cancelled": return Tx("smart.result." + result);
                default: return Tx("smart.result.failed", reason);
            }
        }

        private void End(Attack a, string result, string reason)
        {
            if (!_active.Contains(a)) return;
            if (a.TorpId.HasValue && (result == "cancelled" || result == "track_lost" || result == "failed"))
            {
                try { _g.SetAimShift(a.TorpId.Value, null); } catch (Exception e) { Log("shift reset failed: " + e.Message); }
            }
            try
            {
                if (Held(a.Uid)) { RestoreLaunchSpeed(a); _deferredRestore.Add(a); Ok(a, "restore_deferred", "evasion"); }   // the submarine evades: its fields come back after that
                else RestoreFields(a);
            }
            catch (Exception e) { Log("restore failed: " + e.Message); }
            a.Result = result; a.Reason = reason;
            Step(a, "result", result == "failed" ? StepStatus.Error : StepStatus.Ok, result == "failed" ? reason : null, result == "failed" ? null : reason);
            _active.Remove(a);
            if (_pendingAttack == a) { _pendingAttack = null; if (Pending != null && Pending.Kind != "restore") Pending = null; }
            State(a, "Done", result + ":" + reason);
            var nd = ActionJournal.NotDone(a.Steps);
            string res = ActionJournal.Result(a.Steps);
            Log(ActionJournal.Summary("smart", "smart_attack", result == "failed" ? "error" : res, nd) + " result=" + result + " reason=" + reason);
            _g.Message(Tx("smart.msg.result", ResultText(result, reason)));
            _recent.Add(a.Id + ":" + result);
            if (_recent.Count > 8) _recent.RemoveAt(0);
            _lastResult[a.Id] = result;
            DeleteSaveFile();
        }

        private readonly Dictionary<string, string> _lastResult = new Dictionary<string, string>();

        /// <summary>The result of a finished attack ("hit", "miss", "wire_lost", ...), or null while it runs / unknown.</summary>
        public string ResultOf(string attackId) { string r; return _lastResult.TryGetValue(attackId ?? "", out r) ? r : null; }

        /// <summary>The Steps journal of an attack (running only).</summary>
        public string StepsJson(string attackId)
        {
            var a = _active.FirstOrDefault(x => x.Id == attackId);
            return a == null ? "[]" : ActionJournal.StepsJson(a.Steps);
        }

        public string Cancel(int? uid, string attackId, string reason)
        {
            var a = _active.FirstOrDefault(x => (attackId != null && x.Id == attackId) || (attackId == null && uid.HasValue && x.Uid == uid.Value));
            if (a == null) return "not_found";
            if (_pendingAttack == a) { Pending = null; _pendingAttack = null; }
            End(a, "cancelled", reason ?? "cancel");
            return "cancelled";
        }

        public void ReleaseUnit(int uid) { Cancel(uid, null, "release"); }

        /// <summary>True while an attack of this unit is running (SarController.Busy, T-109).</summary>
        public bool Busy(int uid) { return _active.Any(a => a.Uid == uid); }

        // ---- status -----------------------------------------------------------------------------------------------------

        private string AttackJson(Attack a)
        {
            return "{\"attack_id\":" + Json.Str(a.Id) + ",\"uid\":" + a.Uid.ToString(CultureInfo.InvariantCulture) + ",\"state\":" + Json.Str(a.State) +
                   ",\"track_id\":" + a.TrackId.ToString(CultureInfo.InvariantCulture) + ",\"ammo\":" + Json.Str(a.Ammo) +
                   ",\"side\":" + Json.Str(a.Side > 0 ? "right" : "left") + ",\"leg_nm\":" + N(a.LegNm) + ",\"leg_odo_nm\":" + N(a.LegOdo) +
                   ",\"stage\":" + a.Stage.ToString(CultureInfo.InvariantCulture) + ",\"warnings\":[" + string.Join(",", a.Warnings.Select(Json.Str).ToArray()) + "]}";
        }

        private string PendingJson()
        {
            var q = Pending;
            if (q == null) return "null";
            return "{\"id\":" + Json.Str(q.Id) + ",\"kind\":" + Json.Str(q.Kind) + ",\"text\":" + Json.Str(q.Text) + ",\"options\":[" + string.Join(",", q.Options.Select(Json.Str).ToArray()) + "],\"deadline_t\":" + N(q.DeadlineT) + "}";
        }

        public string StatusJson(int? uid)
        {
            var rows = _active.Where(a => !uid.HasValue || a.Uid == uid.Value).Select(AttackJson).ToArray();
            return "{\"attacks\":[" + string.Join(",", rows) + "],\"pending\":" + PendingJson() + ",\"recent\":[" + string.Join(",", _recent.Select(Json.Str).ToArray()) + "]}";
        }

        public string LinkStateJson()
        {
            return "{\"active\":" + _active.Count.ToString(CultureInfo.InvariantCulture) + ",\"attacks\":[" + string.Join(",", _active.Select(AttackJson).ToArray()) + "],\"pending\":" + PendingJson() + "}";
        }

        // ---- save / load (ADR-0038 section 12) ----------------------------------------------------------------------------

        private SaveSlotStore _slots;
        private string _slotsDir;

        /// <summary>T-122: slot store; same directory and key as before.</summary>
        private SaveSlotStore Slots
        {
            get
            {
                string dir = _g.SaveDir ?? ".";
                if (_slots == null || _slotsDir != dir) { _slots = new SaveSlotStore(dir, Log); _slotsDir = dir; }
                return _slots;
            }
        }

        private string _ownedSlot;   // path of the slot written/restored in the current mission

        private void DeleteSaveFile()
        {
            if (_active.Any(x => x.TorpId.HasValue)) return;
            // T-156: only a slot this mission wrote or restored; last_save may still point at a previous mission's file
            try { string sp = _g.LastSavePath; if (sp != null && sp == _ownedSlot) { Slots.Delete(sp); _ownedSlot = null; } } catch { }
        }

        /// <summary>T-198: true while the Harmony hook on the game save works; the mark observer then writes nothing.</summary>
        public bool SaveHookActive;

        /// <summary>The game saved <paramref name="lastSavePath"/>. <paramref name="force"/> = called by the save hook: the file is written (or the slot of that path dropped when no attack is running) with the save stamp.</summary>
        public void OnSaveMarker(string lastSavePath, double now, SavStamp stamp = null, bool force = false)
        {
            if (lastSavePath == null || (!force && SaveHookActive)) return;
            var a = _active.FirstOrDefault(x => x.TorpId.HasValue && x.State != "Done");
            if (a == null)
            {
                if (force) { try { Slots.Delete(lastSavePath); } catch (Exception ex) { Log("save failed: " + ex.Message); } }   // the game overwrote that .sav: a state of an older save is not valid for it
                return;
            }
            try
            {
                var t = _g.Torpedo(a.TorpId.Value);
                var d = new SmartSaveData
                {
                    AttackId = a.Id, State = a.State, SubUid = a.Uid, SubName = a.SubName, SubType = a.SubType, Ammo = a.Ammo, AmmoParams = a.A,
                    LaunchT = a.LaunchT, TorpLat = t != null ? t.Pos.Y : 0, TorpLon = t != null ? t.Pos.X : 0, TrackId = a.TrackId, Side = a.Side,
                    PhiDeg = a.PhiDeg, LegNm = a.LegNm, LegOdoNm = a.LegOdo, Stage = a.Stage, TorpDepthFt = a.IntendedDepth,
                    History = SmartAttackSave.Compress(a.Hist), MissionT = now, Sav = stamp,
                };
                var e = a.Mem.Peek(a.Token);
                if (e != null) foreach (var kv in e.Fields) if (kv.Value.Saved != null) d.Saved[kv.Key] = kv.Value.Saved;
                if (a.PrevLaunchSpeed.HasValue) d.Saved["launch_speed"] = a.PrevLaunchSpeed.Value.ToString(CultureInfo.InvariantCulture);
                d.AnsweredNeutrals.AddRange(a.Known);
                if (Slots.Write(lastSavePath, SmartAttackSave.ToJson(d), jtxt => { string perr; return SmartAttackSave.Parse(jtxt, out perr) == null ? (perr ?? "bad file") : null; })) { _ownedSlot = lastSavePath; Log(a.Id + " saved state for " + Path.GetFileName(Slots.PathFor(lastSavePath))); }
            }
            catch (Exception ex) { Log("save failed: " + ex.Message); }
        }

        /// <summary>T-130: the mission was left (menu or another mission): forget everything without touching the game.</summary>
        public void OnMissionLeft()
        {
            _active.Clear(); _deferredRestore.Clear(); Pending = null; _pendingAttack = null; _pendingRestore = null; _ownedSlot = null;
        }

        public void OnMissionChanged(string lastSavePath, double now) { OnMissionChanged(lastSavePath, now, null); }

        /// <summary>T-198 (ADR-0086): <paramref name="loaded"/> = header stamp of the loaded .sav, null = unknown (the T-122 rule: file time within 5 s of the mission).</summary>
        public void OnMissionChanged(string lastSavePath, double now, SavStamp loaded)
        {
            OnMissionLeft();   // the old mission's objects are gone
            if (lastSavePath == null) return;
            try
            {
                SmartSaveData d = null; string err = null, src;
                string text = Slots.Read(lastSavePath, t => { d = SmartAttackSave.Parse(t, out err); return d == null ? (err ?? "bad file") : null; }, out src);
                if (text == null) { if (err != null) { Slots.Delete(lastSavePath); Log("stale or broken save state removed (" + err + ")"); } return; }
                string why = SaveFreshness.Check(d.Sav, d.MissionT, loaded, now);
                if (why != null) { Log("save state is not for this mission (" + why + "), kept, not restored"); return; }
                double dt = SaveFreshness.Shift(d.MissionT, now);   // the game does not restore the mission clock
                d.LaunchT += dt; d.MissionT = now;
                for (int hi = 0; hi < d.History.Count; hi++) { var hs = d.History[hi]; hs.T += dt; d.History[hi] = hs; }   // T-156: a valid slot is never deleted for a stale time
                _ownedSlot = lastSavePath;
                if (_p.RestoreOnLoad == "off") { Slots.Delete(lastSavePath); RestoreAfterLoss(d, "restore_off"); return; }
                if (_p.RestoreOnLoad == "ask")
                {
                    _pendingRestore = d;
                    Pending = new SmartQuestion
                    {
                        Id = "q" + (++_qseq), Kind = "restore", Text = Tx("smart.q.restore"), Options = new[] { "yes", "no" },
                        DeadlineT = _g.Now + _p.NeutralConfirmTimeoutS, NeutralIds = new List<int>(),
                    };
                    _g.Message(Pending.Text);
                    return;
                }
                RestoreFromSave(d);
            }
            catch (Exception ex) { Log("restore failed: " + ex.Message); }
        }

        private void AnswerRestore(string option)
        {
            var d = _pendingRestore;
            _pendingRestore = null;
            if (d == null) return;
            if (option == "yes") RestoreFromSave(d); else RestoreAfterLoss(d, "restore_declined");
        }

        private Attack NewFromSave(SmartSaveData d)
        {
            var a = new Attack
            {
                Id = d.AttackId ?? "sa-" + (++_seq), Uid = d.SubUid, TrackId = d.TrackId, Ammo = d.Ammo, A = d.AmmoParams, SubName = d.SubName, SubType = d.SubType,
                LaunchT = d.LaunchT, Side = d.Side, PhiDeg = d.PhiDeg, LegNm = d.LegNm, LegOdo = d.LegOdoNm, Stage = d.Stage, IntendedDepth = d.TorpDepthFt,
                Depth = new DepthPlan(),
            };
            foreach (var kv in d.Saved) { a.Mem.RememberField(a.Token, kv.Key, kv.Value); }
            foreach (int id in d.AnsweredNeutrals) a.Known.Add(id);
            foreach (var s in d.History) a.Hist.Add(s);
            return a;
        }

        private void NoteCurrentAsSet(Attack a)
        {
            foreach (string key in new[] { "telegraph", "sa_depth" })
            {
                var e = a.Mem.Peek(a.Token);
                if (e != null && e.Fields.ContainsKey(key)) a.Mem.NoteFieldSet(a.Token, key, _g.FieldNow(a.Uid, key));
            }
        }

        private void RestoreAfterLoss(SmartSaveData d, string why)
        {
            var a = NewFromSave(d);
            if (a.A == null) a.A = new WireAmmo { File = d.Ammo };
            a.State = "Leg";
            _active.Add(a);
            if (d.Saved.ContainsKey("launch_speed")) { int v; if (int.TryParse(d.Saved["launch_speed"], out v)) { a.PrevLaunchSpeed = v; a.Mem.NoteFieldSet(a.Token, "launch_speed", _g.GetLaunchSpeed(a.Uid, a.Ammo).ToString(CultureInfo.InvariantCulture)); } }
            NoteCurrentAsSet(a);
            End(a, "wire_lost", why);
        }

        private void RestoreFromSave(SmartSaveData d)
        {
            var sub = _g.Sub(d.SubUid);
            TorpSnap found = null;
            if (sub != null && sub.Alive)
            {
                var live = new List<(int id, string ammo, double lat, double lon, bool onWire)>();
                var snaps = new List<TorpSnap>();
                foreach (int id in _g.WeaponIds(d.SubUid) ?? new HashSet<int>())
                {
                    var t = _g.Torpedo(id);
                    if (t == null || !t.Alive) continue;
                    snaps.Add(t);
                    live.Add((t.Id, t.Ammo ?? d.Ammo, t.Pos.Y, t.Pos.X, t.OnWire && !t.WireLost));
                }
                int i = SmartAttackSave.MatchTorpedo(d.Ammo, d.TorpLat, d.TorpLon, live);
                if (i >= 0) found = snaps[i];
            }
            if (found == null || d.AmmoParams == null || SmartAttackMath.ValidateAmmo(d.AmmoParams) != null) { RestoreAfterLoss(d, "wire_lost_on_load"); return; }
            var a = NewFromSave(d);
            a.TorpId = found.Id; a.TargetIsSub = false;
            a.HitsAtLaunch = _g.HitsOn(d.TrackId);
            var tr = _g.Track(d.TrackId);
            a.TargetIsSub = tr != null && tr.Domain == "Subsurface";
            a.State = d.State == "Done" ? "Approach" : d.State;
            a.LastTick = _g.Now; a.NextRecheck = _g.Now + _p.NeutralRecheckS; a.LastTorpPos = found.Pos; a.WasFinal = d.State == "Final";
            if (d.Saved.ContainsKey("launch_speed")) { int v; if (int.TryParse(d.Saved["launch_speed"], out v)) a.PrevLaunchSpeed = v; }
            NoteCurrentAsSet(a);
            _active.Add(a);
            // the game does not keep the aim-point shift in the .sav: the next Leg tick sets it again; other phases are direct
            if (a.State != "Leg") { _g.SetAimShift(found.Id, null); a.AimDirect = true; }
            State(a, a.State, "restored");
            Ok(a, "restore", "torpedo " + found.Id);
            _g.Message(Tx("smart.msg.resumed"));
        }

        // ---- log for selftest -------------------------------------------------------------------------------------------

        public static string SelfTestPlanLines()
        {
            var p = new SmartParams();
            var w = WireAmmo.FromIni("sample", SmartAttackMath.AdcapIni);
            var o = new SaVec(0, 0);
            var sides = SmartAttackMath.PlanSides(o, new SaVec(0, 10), new SaVec(0, 0), w, new NeutralTrack[0], 2, p);
            bool fs;
            double f = 1.0 - sides[0].Leg.LegNm / w.RangesNm[0];
            int stage = SmartAttackMath.FinalStage(w, f, SmartAttackMath.PathAfterLeg(10, sides[0].Leg.LegNm, sides[0].PhiDeg, 0, w.SpeedsKn[0]), p, out fs);
            return "smart_attack plan D=10 side=" + (sides[0].Side > 0 ? "right" : "left") + " phi=" + F1(sides[0].PhiDeg) + " leg=" + F1(sides[0].Leg.LegNm) + " final=" + w.SpeedsKn[stage] + "kn";
        }
    }
}
