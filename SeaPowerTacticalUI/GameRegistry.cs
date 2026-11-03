using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>The list of every module's SpecSet (T-120). Offline selfcheck and the registry test read it; in the game UiCore collects the same sets from the modules.</summary>
    internal static partial class UiRegistry
    {
        // The list itself lives in GameRegistryList.cs (one line per *Specs.cs, filled module by module, plan T-120 stream M). Keeping it out of this
        // file lets the core stand (coretest), which has no game, compile the registry without every *Specs.cs class: there the list stays empty.
        static partial void AddSets(List<SpecSet> sets);

        private static readonly SpecSet[] Sets = MakeSets();

        private static SpecSet[] MakeSets()
        {
            var list = new List<SpecSet>();
            AddSets(list);
            return list.ToArray();
        }

        public static IList<SpecSet> All() { return Sets; }

        // The one name that must never be a member of a spec (ADR-0045); built from pieces so that the scanner does not see a literal.
        private static readonly string Banned = "Exact" + "EnemyDepth";

        /// <summary>Static rules of a spec list; null = ok. Used by SelfTest and by tests of the sets.</summary>
        public static string Validate(IList<SpecSet> sets)
        {
            var ids = new HashSet<string>();
            foreach (var set in sets)
            {
                foreach (var p in set.Patches)
                    if (!ids.Add(p.Id)) return "registry: duplicate patch id '" + p.Id + "' (" + set.Module + ")";
                foreach (var m in set.Members)
                {
                    if ((m.Kind == MemberKind.Method || m.Kind == MemberKind.Ctor) && m.Params == null) return "registry: " + m.Key + " has no Params (" + set.Module + ")";
                    if (m.Duck && string.IsNullOrWhiteSpace(m.Why)) return "registry: duck member '" + m.Member + "' needs a reason (" + set.Module + ")";
                    if (!m.Duck && string.IsNullOrEmpty(m.Type)) return "registry: spec without a type (" + set.Module + ")";
                    if (m.Member != null && m.Member.IndexOf(Banned, StringComparison.OrdinalIgnoreCase) >= 0) return "registry: banned member in " + set.Module;
                }
            }
            return null;
        }

        private sealed class BadSpecs
        {
            public static readonly MemberSpec Duck = MemberSpec.DuckMember("t", "x", MemberKind.Property);
        }

        private sealed class DupSpecs
        {
            public static readonly PatchSpec A = new PatchSpec("same", "t", "T", "M", new string[0], "void", PatchKind.Prefix, "always");
            public static readonly PatchSpec B = new PatchSpec("same", "t", "T", "N", new string[0], "void", PatchKind.Prefix, "always");
        }

        public static string SelfTest()
        {
            try
            {
                string e = Validate(All());
                if (e != null) return e;
                e = Validate(new[] { SpecSet.Of("bad", typeof(BadSpecs)) });
                if (e == null || !e.Contains("needs a reason")) return "registry: a duck without a reason must fail, got " + e;
                e = Validate(new[] { SpecSet.Of("dup", typeof(DupSpecs)) });
                if (e == null || !e.Contains("duplicate patch id")) return "registry: duplicate patch ids must fail, got " + e;
                var noParams = new SpecSet("np", new[] { MemberSpec.Method("t", "T", "M", null) }, null, null);
                e = Validate(new[] { noParams });
                if (e == null || !e.Contains("no Params")) return "registry: a method without Params must fail, got " + e;
                var ban = new SpecSet("ban", new[] { MemberSpec.Prop("t", "T", Banned) }, null, null);
                if (Validate(new[] { ban }) == null) return "registry: the banned member must fail";
                return RegistryReport.SelfTest();
            }
            catch (Exception ex) { return "registry: " + ex.GetType().Name + ": " + ex.Message; }
        }
    }

    /// <summary>What the registry says about the installed game: one check per spec, capabilities from the base rule plus Derive, the capabilities that are missing.</summary>
    internal sealed class RegistryReport
    {
        public readonly List<GameApi.Check> Checks = new List<GameApi.Check>();
        public readonly HashSet<string> Capabilities = new HashSet<string>();
        public readonly HashSet<string> MissingCaps = new HashSet<string>();

        private sealed class Counter { public int Checks, Missing, Duck; public readonly List<string> MissingKeys = new List<string>(); }
        private readonly Dictionary<string, Counter> _modules = new Dictionary<string, Counter>();

        private sealed class CapState { public bool HasRequired, RequiredMissing, AnyMissing; }

        /// <summary>Real game: the checks of GameApi.Present / PatchTarget.</summary>
        public static RegistryReport Run(GameApi api, IList<SpecSet> sets)
        {
            return Build(sets, api, api.Present, p => api.PatchTarget(p) != null);
        }

        /// <summary>
        /// Base rule: a capability is present when all its required specs (members and patches) are present. A capability that has no
        /// required spec at all is present when all of its specs are. Derive of each set runs after that and may add or remove capabilities.
        /// Duck members are not checked (counted in duck=D).
        /// </summary>
        /// <summary>
        /// Capabilities that a Derive took away because the player switched the function off in the config (ADR-0043: not a degradation, not on the player line).
        /// Derive adds the name here next to caps.Remove; Build clears the set before the Derive calls and keeps these out of MissingCaps.
        /// </summary>
        internal static readonly HashSet<string> OffByConfig = new HashSet<string>();

        public static RegistryReport Build(IList<SpecSet> sets, GameApi api, Func<MemberSpec, bool> present, Func<PatchSpec, bool> patchPresent)
        {
            var r = new RegistryReport();
            var caps = new Dictionary<string, CapState>();
            foreach (var set in sets)
            {
                Counter c;
                if (!r._modules.TryGetValue(set.Module, out c)) r._modules[set.Module] = c = new Counter();
                foreach (var m in set.Members)
                {
                    if (m.Duck) { c.Duck++; continue; }
                    bool ok = present(m);
                    r.Add(c, caps, m.Capability, m.Key, m.Required, ok);
                }
                foreach (var p in set.Patches)
                {
                    bool ok = patchPresent(p);
                    r.Add(c, caps, p.Capability, p.Key, p.Required, ok);
                }
            }
            foreach (var kv in caps)
            {
                bool ok = kv.Value.HasRequired ? !kv.Value.RequiredMissing : !kv.Value.AnyMissing;
                if (ok) r.Capabilities.Add(kv.Key); else r.MissingCaps.Add(kv.Key);
            }
            OffByConfig.Clear();
            var beforeDerive = new List<string>(r.Capabilities);
            foreach (var set in sets)
                if (set.Derive != null) set.Derive(api, r.Capabilities);
            foreach (var c in beforeDerive)
                if (!r.Capabilities.Contains(c)) r.MissingCaps.Add(c);   // a capability that Derive took away is missing as well (UiDegradation sees it)
            r.MissingCaps.RemoveWhere(c => r.Capabilities.Contains(c) || OffByConfig.Contains(c));   // ... and one that Derive gave back is not
            return r;
        }

        private void Add(Counter c, Dictionary<string, CapState> caps, string cap, string key, bool required, bool ok)
        {
            Checks.Add(new GameApi.Check { Capability = cap, Member = key, Required = required, Present = ok });
            c.Checks++;
            if (!ok) { c.Missing++; c.MissingKeys.Add(key); }
            CapState s;
            if (!caps.TryGetValue(cap, out s)) caps[cap] = s = new CapState();
            if (required) s.HasRequired = true;
            if (!ok) { s.AnyMissing = true; if (required) s.RequiredMissing = true; }
        }

        /// <summary>"asw members ok checks=N missing=0 duck=D" or "asw members MISSING k1,k2 checks=N missing=M duck=D".</summary>
        public string ModuleLine(string module)
        {
            Counter c;
            if (!_modules.TryGetValue(module, out c)) return module + " members ok checks=0 missing=0 duck=0";
            string tail = " checks=" + c.Checks + " missing=" + c.Missing + " duck=" + c.Duck;
            return c.Missing == 0 ? module + " members ok" + tail : module + " members MISSING " + string.Join(",", c.MissingKeys.ToArray()) + tail;
        }

        public IEnumerable<string> Modules { get { return _modules.Keys; } }

        // ------------------------------------------------------------------ self-test

        private sealed class AltSpecs
        {
            public static readonly MemberSpec A = MemberSpec.Field("alpha", "T", "a");
            public static readonly MemberSpec B = MemberSpec.Prop("alpha", "T", "b");
            public static readonly MemberSpec C = MemberSpec.Field("beta", "T", "c");
            public static readonly MemberSpec D = MemberSpec.Field("beta", "T", "d", required: false);
            public static readonly PatchSpec P = new PatchSpec("pp", "gamma", "T", "M", new string[0], "void", PatchKind.Postfix, "always");
            public static readonly MemberSpec E = MemberSpec.Field("delta", "T", "e", required: false);
            public static readonly MemberSpec F = MemberSpec.Field("delta", "T", "f", required: false);
            public static readonly MemberSpec Dk = MemberSpec.DuckMember("alpha", "x", MemberKind.Property, null, "any Noesis element");
            public static void Derive(GameApi api, ISet<string> caps) { if (caps.Contains("beta")) caps.Add("beta_derived"); }
        }

        private sealed class StripSpecs
        {
            public static readonly MemberSpec A = MemberSpec.Field("eps", "T", "a");
            public static void Derive(GameApi api, ISet<string> caps) { caps.Remove("eps"); }
        }

        public static string SelfTest()
        {
            try
            {
                var set = SpecSet.Of("alt", typeof(AltSpecs));
                if (set.Members.Count != 7 || set.Patches.Count != 1) return "report: SpecSet.Of counts = " + set.Members.Count + "/" + set.Patches.Count;
                if (set.Members[0] != AltSpecs.A || set.Members[3] != AltSpecs.D || set.Members[6] != AltSpecs.Dk) return "report: SpecSet.Of must keep the declaration order";
                if (set.Derive == null) return "report: SpecSet.Of must find Derive";
                // everything present
                var all = Build(new[] { set }, null, s => true, p => true);
                if (all.MissingCaps.Count != 0 || !all.Capabilities.SetEquals(new[] { "alpha", "beta", "gamma", "delta", "beta_derived" })) return "report: all present -> " + string.Join(",", all.Capabilities.OrderBy(x => x).ToArray());
                if (all.ModuleLine("alt") != "alt members ok checks=7 missing=0 duck=1") return "report: ModuleLine = " + all.ModuleLine("alt");
                // one required member of alpha missing -> alpha gone and listed; the duck is never asked
                var noB = Build(new[] { set }, null, s => s != AltSpecs.B && !s.Duck, p => true);
                if (noB.Capabilities.Contains("alpha") || !noB.MissingCaps.Contains("alpha")) return "report: required member missing must drop the capability";
                if (!noB.ModuleLine("alt").StartsWith("alt members MISSING T.b ") || !noB.ModuleLine("alt").EndsWith("checks=7 missing=1 duck=1")) return "report: MISSING line = " + noB.ModuleLine("alt");
                // optional member missing in a capability that has a required one: still present
                var noD = Build(new[] { set }, null, s => s != AltSpecs.D, p => true);
                if (!noD.Capabilities.Contains("beta") || noD.MissingCaps.Contains("beta")) return "report: an optional member must not drop the capability";
                // the required patch of gamma
                var noP = Build(new[] { set }, null, s => true, p => false);
                if (noP.Capabilities.Contains("gamma") || !noP.MissingCaps.Contains("gamma")) return "report: required patch missing";
                // a capability of optional members only needs all of them
                var noE = Build(new[] { set }, null, s => s != AltSpecs.E, p => true);
                if (noE.Capabilities.Contains("delta") || !noE.MissingCaps.Contains("delta")) return "report: an optional-only capability needs all members";
                // Derive sees the base result
                var noC = Build(new[] { set }, null, s => s != AltSpecs.C, p => true);
                if (noC.Capabilities.Contains("beta_derived")) return "report: Derive ran on a missing capability";
                // Derive that removes a capability: it lands in MissingCaps
                var strip = Build(new[] { SpecSet.Of("strip", typeof(StripSpecs)) }, null, s => true, p => true);
                if (strip.Capabilities.Contains("eps") || !strip.MissingCaps.Contains("eps")) return "report: a capability removed by Derive must be listed as missing";
                var chk = all.Checks.Find(c => c.Member == "T.b");
                if (chk == null || chk.Capability != "alpha" || !chk.Required || !chk.Present) return "report: Check of T.b";
                return null;
            }
            catch (Exception e) { return "report: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
