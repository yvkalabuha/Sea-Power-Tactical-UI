using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-138 / ADR-0054: who steers a unit. Pure core service: resolvers answer for the modules that own a unit
    /// by their own state (smart attack, SAR, evasion); a claim is the owner's explicit hold (intercept).</summary>
    internal sealed class UnitSteering
    {
        public const string Intercept = "intercept", SmartAttack = "smart_attack", Sar = "sar", Evasion = "evasion", AirEvasion = "air_evasion";

        private readonly List<KeyValuePair<string, Func<int, bool>>> _resolvers = new List<KeyValuePair<string, Func<int, bool>>>();
        private readonly Dictionary<int, string> _claims = new Dictionary<int, string>();

        /// <summary>Resolvers are asked in registration order; a throwing resolver counts as "does not hold".</summary>
        public void Register(string owner, Func<int, bool> holds)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentNullException("owner");
            if (holds == null) throw new ArgumentNullException("holds");
            _resolvers.Add(new KeyValuePair<string, Func<int, bool>>(owner, holds));
        }

        private readonly List<KeyValuePair<string, Func<int, bool>>> _preempts = new List<KeyValuePair<string, Func<int, bool>>>();

        /// <summary>T-100: a preempting holder (air evasion) is asked before the claims and beats them; a throwing one does not hold.</summary>
        public void RegisterPreempt(string owner, Func<int, bool> holds)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentNullException("owner");
            if (holds == null) throw new ArgumentNullException("holds");
            _preempts.Add(new KeyValuePair<string, Func<int, bool>>(owner, holds));
        }

        private string PreemptOther(int uid, string except)
        {
            foreach (var r in _preempts.ToArray()) if (r.Key != except && Holds(r.Value, uid)) return r.Key;
            return null;
        }

        private static bool Holds(Func<int, bool> f, int uid)
        {
            try { return f(uid); }
            catch (Exception) { return false; }
        }

        /// <summary>The claim first, then the first resolver that holds; null = free.</summary>
        public string Owner(int uid)
        {
            string c, pe = PreemptOther(uid, null);
            if (pe != null) return pe;
            if (_claims.TryGetValue(uid, out c)) return c;
            foreach (var r in _resolvers.ToArray()) if (Holds(r.Value, uid)) return r.Key;
            return null;
        }

        /// <summary>Resolvers and claims of others than <paramref name="owner"/>; null = nobody else.</summary>
        public string HolderOtherThan(int uid, string owner)
        {
            string c, pe = PreemptOther(uid, owner);
            if (pe != null) return pe;
            if (_claims.TryGetValue(uid, out c) && c != owner) return c;
            foreach (var r in _resolvers.ToArray()) if (r.Key != owner && Holds(r.Value, uid)) return r.Key;
            return null;
        }

        /// <summary>null = ok (also when the owner already holds the unit); "busy:&lt;owner&gt;" otherwise.</summary>
        public string Claim(int uid, string owner)
        {
            string other = HolderOtherThan(uid, owner);
            if (other != null) return "busy:" + other;
            _claims[uid] = owner;
            return null;
        }

        /// <summary>Drops only the claim of this owner.</summary>
        public void Release(int uid, string owner)
        {
            string c;
            if (_claims.TryGetValue(uid, out c) && c == owner) _claims.Remove(uid);
        }

        /// <summary>The mission was left: every claim is void (resolvers stay).</summary>
        public void ClearClaims() { _claims.Clear(); }

        public int Count { get { return _claims.Count; } }

        public static string SelfTest()
        {
            try
            {
                var s = new UnitSteering();
                bool sa = false, sar = false, boom = false;
                s.Register(Evasion, u => { if (boom) throw new InvalidOperationException("x"); return u == 9; });
                s.Register(SmartAttack, u => sa && u == 1);
                s.Register(Sar, u => sar && u == 1);
                if (s.Owner(1) != null) return "steering: free unit has owner " + s.Owner(1);
                if (s.Claim(1, Intercept) != null) return "steering: claim of a free unit failed";
                if (s.Owner(1) != Intercept || s.Count != 1) return "steering: owner after claim = " + s.Owner(1);
                if (s.Claim(1, Intercept) != null) return "steering: re-claim by the owner must be ok";
                if (s.Claim(1, Sar) != "busy:" + Intercept) return "steering: claim of a held unit = " + s.Claim(1, Sar);
                if (s.HolderOtherThan(1, Intercept) != null) return "steering: nobody else yet";
                if (s.HolderOtherThan(1, Sar) != Intercept) return "steering: other than sar = " + s.HolderOtherThan(1, Sar);
                s.Release(1, Sar);                                     // not the owner: nothing happens
                if (s.Owner(1) != Intercept) return "steering: release by a stranger dropped the claim";
                sa = true;
                if (s.HolderOtherThan(1, Intercept) != SmartAttack) return "steering: resolver smart_attack not seen";
                sar = true;
                if (s.HolderOtherThan(1, Intercept) != SmartAttack) return "steering: first resolver must win";
                s.Release(1, Intercept);
                if (s.Count != 0) return "steering: release left a claim";
                if (s.Owner(1) != SmartAttack) return "steering: resolver owner = " + s.Owner(1);
                if (s.Claim(1, Intercept) != "busy:" + SmartAttack) return "steering: claim must be refused by a resolver";
                sa = false;
                if (s.Owner(1) != Sar) return "steering: second resolver owner = " + s.Owner(1);
                sar = false;
                boom = true;
                if (s.Owner(9) != null) return "steering: a throwing resolver must count as false";
                if (s.Claim(9, Intercept) != null) return "steering: claim behind a throwing resolver failed";
                boom = false;
                if (s.Owner(9) != Intercept) return "steering: claim wins over a resolver";
                if (s.HolderOtherThan(9, Evasion) != Intercept) return "steering: other than evasion";
                s.Claim(2, Intercept);
                s.ClearClaims();
                if (s.Count != 0 || s.Owner(2) != null) return "steering: ClearClaims";
                if (s.Owner(9) != Evasion) return "steering: resolvers must survive ClearClaims";
                bool pre = false, preBoom = false;
                s.RegisterPreempt(AirEvasion, u => { if (preBoom) throw new InvalidOperationException("x"); return pre && u == 5; });
                s.Claim(5, Intercept);
                if (s.Owner(5) != Intercept) return "steering: preempt not held must not show";
                pre = true;
                if (s.Owner(5) != AirEvasion) return "steering: preempt must beat the claim, owner = " + s.Owner(5);
                if (s.HolderOtherThan(5, Intercept) != AirEvasion) return "steering: other than intercept = " + s.HolderOtherThan(5, Intercept);
                if (s.HolderOtherThan(5, AirEvasion) != Intercept) return "steering: preempt must not count against itself";
                if (s.Claim(5, Sar) != "busy:" + AirEvasion) return "steering: claim vs preempt = " + s.Claim(5, Sar);
                pre = false;
                if (s.Owner(5) != Intercept) return "steering: claim must return after the preempt lets go";
                pre = true; preBoom = true;
                if (s.Owner(5) != Intercept) return "steering: a throwing preempt must not hold";
                s.Release(5, Intercept);
                return null;
            }
            catch (Exception e) { return "steering: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
