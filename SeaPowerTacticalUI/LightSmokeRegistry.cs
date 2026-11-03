using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>Access to one particle system by an opaque handle (UnityParticleAccess in the game, a fake in puretest).</summary>
    internal interface IParticleAccess
    {
        int Id(object ps);                          // GetInstanceID()
        bool IsDead(object ps);                     // Unity: (Object)ps == null
        LightSmokeValues Read(object ps);
        void Write(object ps, LightSmokeValues v);
    }

    internal enum LightSmokeOutcome { Reduced, Reused, Rescaled, Capped, Dead }

    /// <summary>T-165 / ADR-0062: originals of the particle systems we reduced, by instance id.</summary>
    internal sealed class LightSmokeRegistry
    {
        public const int PruneAbove = 512, Cap = 4096;
        public const double PruneEveryS = 30;

        private sealed class Entry
        {
            public object Ps;
            public LightSmokeValues Original;
            public double K;
            public LsProfile Profile;
        }

        private readonly IParticleAccess _a;
        private readonly Func<double> _clock;
        private readonly Dictionary<int, Entry> _map = new Dictionary<int, Entry>();
        private double _lastPrune = double.NegativeInfinity;

        public LightSmokeRegistry(IParticleAccess access, Func<double> clock) { _a = access; _clock = clock; }

        public int Count { get { return _map.Count; } }
        public long Reduced, Reused, Rescaled, Restored, Pruned, Capped, SkippedDead;

        /// <summary>Not in the registry -> Read, Write(Scale(orig,k)), add (Count >= Cap -> Capped, untouched).
        /// Same k (|dk| &lt; 1e-9) -> Reused, untouched. Other k -> Write(Scale(orig,k)) -> Rescaled. Dead -> Dead.</summary>
        public LightSmokeOutcome Apply(object ps, double k, LsProfile profile = LsProfile.Full)
        {
            if (_a.IsDead(ps)) return LightSmokeOutcome.Dead;
            int id = _a.Id(ps);
            Entry e;
            if (_map.TryGetValue(id, out e))
            {
                e.Ps = ps;
                if (Math.Abs(e.K - k) < 1e-9) { Reused++; return LightSmokeOutcome.Reused; }
                _a.Write(ps, LightSmokeMath.Scale(e.Original, k, e.Profile));
                e.K = k;
                Rescaled++;
                return LightSmokeOutcome.Rescaled;
            }
            if (_map.Count >= Cap) { Capped++; return LightSmokeOutcome.Capped; }
            var orig = _a.Read(ps);
            _a.Write(ps, LightSmokeMath.Scale(orig, k, profile));
            _map[id] = new Entry { Ps = ps, Original = orig, K = k, Profile = profile };
            Reduced++;
            return LightSmokeOutcome.Reduced;
        }

        /// <summary>Write the originals into every live entry (active or pooled), skip dead ones (SkippedDead), clear. Returns restored count.</summary>
        public int RestoreAll()
        {
            int n = 0;
            foreach (var e in _map.Values)
            {
                if (_a.IsDead(e.Ps)) { SkippedDead++; continue; }
                _a.Write(e.Ps, e.Original);
                n++;
            }
            _map.Clear();
            Restored += n;
            return n;
        }

        /// <summary>force: remove dead entries now (mission leave). Otherwise only when Count > PruneAbove and PruneEveryS passed since the last prune.
        /// Live entries are never dropped. Returns removed count.</summary>
        public int Prune(bool force)
        {
            double now = _clock();
            if (!force)
            {
                if (_map.Count <= PruneAbove) return 0;
                if (now - _lastPrune < PruneEveryS) return 0;
            }
            _lastPrune = now;
            List<int> dead = null;
            foreach (var kv in _map)
                if (_a.IsDead(kv.Value.Ps)) { if (dead == null) dead = new List<int>(); dead.Add(kv.Key); }
            if (dead == null) return 0;
            foreach (int id in dead) _map.Remove(id);
            Pruned += dead.Count;
            return dead.Count;
        }

        internal sealed class FakeAccess : IParticleAccess
        {
            public readonly Dictionary<int, LightSmokeValues> Vals = new Dictionary<int, LightSmokeValues>();
            public readonly HashSet<int> Dead = new HashSet<int>();
            public int Writes;
            public int Id(object ps) { return (int)ps; }
            public bool IsDead(object ps) { return Dead.Contains((int)ps); }
            public LightSmokeValues Read(object ps) { return Vals[(int)ps].Clone(); }
            public void Write(object ps, LightSmokeValues v) { Writes++; Vals[(int)ps] = v.Clone(); }
            public void Add(int id, float rate = 100, int max = 1000, float life = 4, float burst = 10)
            {
                Vals[id] = new LightSmokeValues
                {
                    RateOverTime = rate, MaxParticles = max, StartLifetime = life,
                    Bursts = new[] { new LightSmokeBurst { Mode = LsCurveMode.Constant, Constant = burst } },
                };
            }
        }

        public static string SelfTest()
        {
            double now = 0;
            var f = new FakeAccess();
            f.Add(1);
            var orig1 = f.Vals[1].Clone();
            var r = new LightSmokeRegistry(f, () => now);
            if (r.Apply(1, 0.35) != LightSmokeOutcome.Reduced || f.Writes != 1) return "first apply";
            if (!f.Vals[1].SameAs(LightSmokeMath.Scale(orig1, 0.35))) return "first apply values";
            if (r.Apply(1, 0.35) != LightSmokeOutcome.Reused || f.Writes != 1) return "reuse writes";
            if (r.Apply(1, 0.5) != LightSmokeOutcome.Rescaled || f.Writes != 2) return "rescale";
            if (!f.Vals[1].SameAs(LightSmokeMath.Scale(orig1, 0.5))) return "rescale is from the original";
            if (r.RestoreAll() != 1 || !f.Vals[1].SameAs(orig1) || r.Count != 0) return "restore";
            // dead
            f.Dead.Add(1);
            if (r.Apply(1, 0.35) != LightSmokeOutcome.Dead) return "dead";
            f.Dead.Clear();
            // prune
            var f2 = new FakeAccess(); now = 100;
            var r2 = new LightSmokeRegistry(f2, () => now);
            for (int i = 0; i < 513; i++) { f2.Add(i); r2.Apply(i, 0.35); }
            for (int i = 0; i < 5; i++) f2.Dead.Add(i);
            if (r2.Prune(false) != 5) return "prune over threshold " + r2.Count;
            if (r2.Count != 508) return "prune count " + r2.Count;
            for (int i = 5; i < 10; i++) f2.Dead.Add(i);
            now += 10;
            if (r2.Prune(false) != 0) return "prune too early";
            if (r2.Prune(true) != 5) return "force prune";
            now += 40;
            if (r2.Prune(false) != 0) return "no dead left";
            int w = f2.Writes;
            if (r2.RestoreAll() != 503 || f2.Writes != w + 503) return "restore live only";
            // cap
            var f3 = new FakeAccess();
            var r3 = new LightSmokeRegistry(f3, () => 0);
            for (int i = 0; i < Cap + 1; i++) f3.Add(i);
            for (int i = 0; i < Cap; i++) r3.Apply(i, 0.35);
            int w3 = f3.Writes;
            if (r3.Apply(Cap, 0.35) != LightSmokeOutcome.Capped || f3.Writes != w3 || r3.Capped != 1) return "cap";
            // profile is kept
            var f4 = new FakeAccess(); f4.Add(7);
            var r4 = new LightSmokeRegistry(f4, () => 0);
            r4.Apply(7, 0.35, LsProfile.KeepLifetime);
            if (f4.Vals[7].StartLifetime != 4) return "profile on first apply";
            r4.Apply(7, 0.5);
            if (f4.Vals[7].StartLifetime != 4) return "profile on rescale";
            return null;
        }
    }
}
