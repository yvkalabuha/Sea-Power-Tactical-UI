using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// Target pick mode of the smart attack (T-102, ADR-0038 section 10): the player clicks the target after choosing the ring sector.
    /// Pure logic, no Unity: the click is already resolved to a track by the game layer. Esc or 15 s cancel the mode.
    /// </summary>
    internal sealed class SmartPickModel
    {
        private bool _active;
        private double _startT;

        public double TimeoutS = 15;
        public int SubUid { get; private set; }
        public bool Active { get { return _active; } }
        /// <summary>T-154 E2: detail of the dry check for the click being handled (set by the caller before OnClick); and the text of the last refusal.</summary>
        public string AllowDetail;
        public string LastRefusal { get; private set; }

        public void Start(int subUid, double now)
        {
            SubUid = subUid; _startT = now; _active = true;
        }

        /// <summary>
        /// "picked" | "hint:own|neutral|unknown|not_allowed|no_track|bearing_only" | null (mode not active: the click does not count).
        /// allowError = the reason the attack path would refuse this target with the available weapons (null = allowed).
        /// </summary>
        public string OnClick(TrackInfo t, string allowError, out int? targetId)
        {
            targetId = null;
            if (!_active) return null;
            if (t == null) return "hint:no_track";
            switch (t.Relation)
            {
                case "friendly": return "hint:own";
                case "neutral": return "hint:neutral";
                case "unknown": return "hint:unknown";   // the attack path takes hostile tracks only
            }
            if (t.State == "bearing_only") return "hint:bearing_only";
            if (t.Relation != "hostile") return "hint:unknown";
            if (allowError != null)
            {
                LastRefusal = SmartAttackMath.RefusalText(allowError, AllowDetail);   // the mode stays open: the player picks another target
                return "hint:refused";
            }
            targetId = t.Id;
            _active = false;
            return "picked";
        }

        public string OnEsc()
        {
            if (!_active) return null;
            _active = false;
            return "cancel_esc";
        }

        public string Tick(double now)
        {
            if (!_active || now - _startT < TimeoutS) return null;
            _active = false;
            return "cancel_timeout";
        }

        public static string HintText(string hint)
        {
            switch (hint)
            {
                case "hint:own": case "hint:neutral": case "hint:unknown": case "hint:no_track": case "hint:bearing_only":
                    return UiText.Get("smart.hint." + hint.Substring(5));
                default: return null;
            }
        }

        public static string SelfTest()
        {
            string fail = null;
            Action<string, bool> eq = (n, ok) => { if (!ok && fail == null) fail = n; };
            Func<string, string, bool, TrackInfo> tr = (rel, st, same) => new TrackInfo { Id = 12, Relation = rel, State = st, Domain = "Surface", Cls = "x", SameTile = same };
            int? id;
            var m = new SmartPickModel();
            eq("click before Start does not count", m.OnClick(tr("hostile", "live", true), null, out id) == null && id == null && !m.Active);
            m.Start(5, 100);
            eq("active after Start", m.Active && m.SubUid == 5);
            eq("own track", m.OnClick(tr("friendly", "live", true), null, out id) == "hint:own" && id == null && m.Active);
            eq("neutral track", m.OnClick(tr("neutral", "live", true), null, out id) == "hint:neutral" && m.Active);
            eq("unknown track (attack needs hostile)", m.OnClick(tr("unknown", "live", true), null, out id) == "hint:unknown" && m.Active);
            eq("no track", m.OnClick(null, null, out id) == "hint:no_track" && m.Active);
            eq("hostile but weapon cannot", m.OnClick(tr("hostile", "live", true), "target_not_allowed", out id) == "hint:refused" && id == null && m.Active && m.LastRefusal != null);
            m.AllowDetail = "37.0 nm, max 20.0 nm";
            eq("out of range keeps the mode open with the numbers", m.OnClick(tr("hostile", "live", true), "out_of_range", out id) == "hint:refused" && id == null && m.Active && m.LastRefusal.Contains("37.0") && m.LastRefusal.Contains("max 20.0"));
            m.AllowDetail = null;
            eq("bearing only", m.OnClick(tr("hostile", "bearing_only", true), null, out id) == "hint:bearing_only" && m.Active);
            eq("hostile ok", m.OnClick(tr("hostile", "live", true), null, out id) == "picked" && id == 12 && !m.Active);
            eq("click after pick does not count", m.OnClick(tr("hostile", "live", true), null, out id) == null);
            m.Start(5, 200);
            eq("Esc cancels", m.OnEsc() == "cancel_esc" && !m.Active && m.OnEsc() == null);
            m.Start(5, 300);
            eq("no timeout at 14.9 s", m.Tick(314.9) == null && m.Active);
            eq("15 s timeout", m.Tick(315) == "cancel_timeout" && !m.Active && m.Tick(400) == null);
            eq("hints have texts", HintText("hint:own") == "Это свой корабль" && HintText("hint:bearing_only") != null && HintText("hint:zzz") == null);
            using (UiText.Push("en")) eq("hint en", HintText("hint:own") == "This is a friendly ship");
            return fail;
        }
    }
}
