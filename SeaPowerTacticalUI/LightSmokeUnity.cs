using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-165 / ADR-0062: IParticleAccess on UnityEngine.ParticleSystem. Writes emission rates, max particles, start lifetime and the burst counts;
    /// duration, trails, light, size and colour are never touched. One reused Burst[] buffer, no allocation on the Reused path.
    /// </summary>
    internal sealed class UnityParticleAccess : IParticleAccess
    {
        private ParticleSystem.Burst[] _buf = new ParticleSystem.Burst[8];
        private static readonly List<object> Scratch = new List<object>();

        public int Id(object ps) { return ((ParticleSystem)ps).GetInstanceID(); }

        public bool IsDead(object ps) { return (UnityEngine.Object)ps == null; }

        public LightSmokeValues Read(object ps)
        {
            var p = (ParticleSystem)ps;
            var main = p.main;
            var em = p.emission;
            var v = new LightSmokeValues
            {
                RateOverTime = em.rateOverTimeMultiplier,
                RateOverDistance = em.rateOverDistanceMultiplier,
                MaxParticles = main.maxParticles,
                StartLifetime = main.startLifetimeMultiplier,
            };
            int n = em.burstCount;
            if (n > 0)
            {
                if (_buf.Length < n) _buf = new ParticleSystem.Burst[n];
                n = em.GetBursts(_buf);
                v.Bursts = new LightSmokeBurst[n];
                for (int i = 0; i < n; i++)
                {
                    var c = _buf[i].count;
                    v.Bursts[i] = new LightSmokeBurst
                    {
                        Mode = (LsCurveMode)(int)c.mode,
                        Constant = c.constant, Min = c.constantMin, Max = c.constantMax, CurveMultiplier = c.curveMultiplier,
                    };
                }
            }
            return v;
        }

        public void Write(object ps, LightSmokeValues v)
        {
            var p = (ParticleSystem)ps;
            var main = p.main;
            main.maxParticles = v.MaxParticles;
            main.startLifetimeMultiplier = v.StartLifetime;
            var em = p.emission;
            em.rateOverTimeMultiplier = v.RateOverTime;
            em.rateOverDistanceMultiplier = v.RateOverDistance;
            int n = v.Bursts == null ? 0 : v.Bursts.Length;
            if (n == 0) return;
            if (_buf.Length < n) _buf = new ParticleSystem.Burst[n];
            int got = em.GetBursts(_buf);
            if (got != n) return;   // the burst list changed under us: leave the bursts alone
            for (int i = 0; i < n; i++)
            {
                var b = v.Bursts[i];
                var c = _buf[i].count;
                switch (b.Mode)
                {
                    case LsCurveMode.Constant: c.constant = b.Constant; break;
                    case LsCurveMode.TwoConstants: c.constantMin = b.Min; c.constantMax = b.Max; break;
                    default: c.curveMultiplier = b.CurveMultiplier; break;
                }
                _buf[i].count = c;
            }
            em.SetBursts(_buf, n);
        }

        /// <summary>root.GetComponentsInChildren&lt;ParticleSystem&gt;(true) as a list of handles; empty for null. The list is reused: main thread, use before the next call.</summary>
        public static IList<object> Systems(GameObject root)
        {
            Scratch.Clear();
            if (root == null) return Scratch;
            var arr = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < arr.Length; i++) Scratch.Add(arr[i]);
            return Scratch;
        }
    }
}
