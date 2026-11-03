using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace VoiceBotBridge
{
    // T-120 (ADR-0043): the single description of every game member / Harmony target the UI mod touches.
    // Pure: reflection only, no Unity / Harmony / BepInEx, so that selfcheck ui and the offline stands can compile it.

    internal enum MemberKind { Type, Field, Property, Method, Ctor, Event }

    internal enum PatchKind { Prefix, Postfix, PrefixPostfix, PrefixPostfixFinalizer }

    /// <summary>One game member with its signature. Created once (static readonly field of a *Specs class); Index is the slot of the resolved MemberInfo cache.</summary>
    internal sealed class MemberSpec
    {
        private static int _next = -1;

        public readonly int Index;
        public readonly string Capability, Type, Member;   // Type = full name; Member = null for Kind.Type; Type = null for a duck
        public readonly MemberKind Kind;
        public readonly string[] Params;                   // required for Method/Ctor (empty = no parameters); null for fields/properties
        public readonly string Returns;                    // null = do not check
        public readonly bool Required, Static, OwnOnly, Duck;
        public readonly string Why;                        // duck only: why the type cannot be named
        public string[] AltParams { get; private set; }    // methods only: a second accepted signature (the game changed it between versions); null = none

        /// <summary>Accept this parameter list too when Params does not match (e.g. 0.8.6 abortLaunchTask(Guid) vs 0.8.7 (Guid,bool)). The caller adapts the arguments to MethodInfo.GetParameters().Length.</summary>
        public MemberSpec WithAltParams(string[] alt) { AltParams = alt; return this; }

        private MemberSpec(string cap, string type, string member, MemberKind kind, string[] ps, string returns, bool required, bool isStatic, bool ownOnly, bool duck, string why)
        {
            Index = Interlocked.Increment(ref _next);
            Capability = cap; Type = type; Member = member; Kind = kind; Params = ps; Returns = returns;
            Required = required; Static = isStatic; OwnOnly = ownOnly; Duck = duck; Why = why;
        }

        /// <summary>"SeaPower.FlightDeck.createLaunchTask(LaunchTaskParameters)": the text of Check.Member and of the log.</summary>
        public string Key
        {
            get
            {
                string head = Duck ? "~." + Member : Kind == MemberKind.Type ? Type : Kind == MemberKind.Ctor ? Type + "..ctor" : Type + "." + Member;
                return Params == null ? head : head + "(" + string.Join(",", Params) + ")";
            }
        }

        public static MemberSpec TypeOf(string cap, string type, bool required = true)
        {
            return new MemberSpec(cap, type, null, MemberKind.Type, null, null, required, false, false, false, null);
        }

        public static MemberSpec Field(string cap, string type, string name, bool required = true, bool isStatic = false, bool ownOnly = false, string returns = null)
        {
            return new MemberSpec(cap, type, name, MemberKind.Field, null, returns, required, isStatic, ownOnly, false, null);
        }

        public static MemberSpec Prop(string cap, string type, string name, bool required = true, bool isStatic = false, bool ownOnly = false, string returns = null)
        {
            return new MemberSpec(cap, type, name, MemberKind.Property, null, returns, required, isStatic, ownOnly, false, null);
        }

        public static MemberSpec Method(string cap, string type, string name, string[] ps, string returns = null, bool required = true, bool isStatic = false, bool ownOnly = false)
        {
            return new MemberSpec(cap, type, name, MemberKind.Method, ps, returns, required, isStatic, ownOnly, false, null);
        }

        /// <summary>A CLR event (add / remove accessors); resolves to an EventInfo.</summary>
        public static MemberSpec Event(string cap, string type, string name, bool required = true)
        {
            return new MemberSpec(cap, type, name, MemberKind.Event, null, null, required, false, false, false, null);
        }

        public static MemberSpec Ctor(string cap, string type, string[] ps, bool required = true)
        {
            return new MemberSpec(cap, type, null, MemberKind.Ctor, ps, null, required, false, false, false, null);
        }

        /// <summary>A member looked up on the type of the object at hand (Noesis elements of different types, ReactiveProperty value). Not checked by selfcheck. why is mandatory (UiRegistry.Validate).</summary>
        public static MemberSpec DuckMember(string cap, string name, MemberKind kind, string[] ps = null, string why = null)
        {
            return new MemberSpec(cap, null, name, kind, ps, null, false, false, false, true, why);
        }
    }

    /// <summary>One Harmony target: the exact method (by signature) and what the patch is.</summary>
    internal sealed class PatchSpec
    {
        public readonly string Id, Capability, Type, Method, Returns, When;   // When: always | tactics_owner | radial_input (manifest)
        public readonly string[] Params;
        public readonly PatchKind Kind;
        public readonly bool Required;   // false: without the patch the capability stays, the function takes its fallback path

        public PatchSpec(string id, string cap, string type, string method, string[] ps, string returns, PatchKind kind, string when, bool required = true)
        {
            if (returns == null) throw new ArgumentException("PatchSpec " + id + ": Returns is required");
            if (ps == null) throw new ArgumentException("PatchSpec " + id + ": Params is required");
            Id = id; Capability = cap; Type = type; Method = method; Params = ps; Returns = returns; Kind = kind; When = when; Required = required;
        }

        /// <summary>"SeaPower.ObjectBase.AddEngageTask" (manifest, as before).</summary>
        public string Target { get { return Type + "." + Method; } }

        /// <summary>Target with the parameter list: the text of Check.Member.</summary>
        public string Key { get { return Target + "(" + string.Join(",", Params) + ")"; } }
    }

    /// <summary>Finds a member by its exact signature. Never "the first overload by name".</summary>
    internal static class MemberResolver
    {
        private const BindingFlags Decl = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>null = not exactly one match; why = "type" | "missing" | "static mismatch" | "params: &lt;found&gt;" | "returns: &lt;found&gt;" | "ambiguous: &lt;found&gt;".</summary>
        public static MemberInfo Resolve(Type t, MemberSpec s, out string why)
        {
            why = null;
            if (s == null) { why = "no spec"; return null; }
            if (t == null) { why = "type"; return null; }
            switch (s.Kind)
            {
                case MemberKind.Type: return t;
                case MemberKind.Field: return ResolveData(t, s, false, out why);
                case MemberKind.Property: return ResolveData(t, s, true, out why);
                case MemberKind.Ctor: return ResolveCtor(t, s, out why);
                case MemberKind.Event: return ResolveEvent(t, s, out why);
                default:
                    var mi = ResolveMethod(t, s.Member, s.Params, s.Returns, s.Static, false, out why);
                    if (mi == null && s.AltParams != null)
                    {
                        string why2;
                        var alt = ResolveMethod(t, s.Member, s.AltParams, s.Returns, s.Static, false, out why2);
                        if (alt != null) { why = null; return alt; }
                    }
                    return mi;
            }
        }

        public static MethodInfo ResolvePatch(Type t, PatchSpec p, out string why)
        {
            why = null;
            if (t == null) { why = "type"; return null; }
            return ResolveMethod(t, p.Method, p.Params, p.Returns, false, true, out why) as MethodInfo;
        }

        private static MemberInfo ResolveData(Type t, MemberSpec s, bool prop, out string why)
        {
            why = null;
            var flags = Decl | (s.Static ? BindingFlags.Static : BindingFlags.Instance);
            var other = Decl | (s.Static ? BindingFlags.Instance : BindingFlags.Static);
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                MemberInfo m = prop ? (MemberInfo)SingleProp(cur, s.Member, flags) : cur.GetField(s.Member, flags);
                if (m == null) continue;
                Type mt = prop ? ((PropertyInfo)m).PropertyType : ((FieldInfo)m).FieldType;
                if (s.Returns != null && !ParamMatches(mt, s.Returns)) { why = "returns: " + Describe(m); return null; }
                return m;
            }
            for (var cur = t; cur != null; cur = cur.BaseType)
                if ((prop ? (MemberInfo)SingleProp(cur, s.Member, other) : cur.GetField(s.Member, other)) != null) { why = "static mismatch"; return null; }
            why = "missing";
            return null;
        }

        private static MemberInfo ResolveEvent(Type t, MemberSpec s, out string why)
        {
            why = null;
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var e = cur.GetEvent(s.Member, Decl | BindingFlags.Instance);
                if (e != null) return e;
            }
            why = "missing";
            return null;
        }

        private static PropertyInfo SingleProp(Type t, string name, BindingFlags f)
        {
            foreach (var p in t.GetProperties(f)) if (p.Name == name && p.GetIndexParameters().Length == 0) return p;
            return null;
        }

        private static MemberInfo ResolveCtor(Type t, MemberSpec s, out string why)
        {
            why = null;
            if (s.Params == null) { why = "params required"; return null; }
            var all = t.GetConstructors(Decl | BindingFlags.Instance);
            var hit = all.Where(c => ParamsMatch(c.GetParameters(), s.Params)).ToList();
            if (hit.Count == 1) return hit[0];
            why = hit.Count > 1 ? "ambiguous: " + Join(hit) : all.Length == 0 ? "missing" : "params: " + Join(all);
            return null;
        }

        private static MemberInfo ResolveMethod(Type t, string name, string[] ps, string returns, bool isStatic, bool anyStatic, out string why)
        {
            why = null;
            if (ps == null) { why = "params required"; return null; }
            var flags = Decl | (anyStatic ? BindingFlags.Static | BindingFlags.Instance : isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var found = new List<MethodInfo>();
            var retMiss = new List<MethodInfo>();
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var hit = new List<MethodInfo>();
                foreach (var m in cur.GetMethods(flags))
                {
                    if (m.Name != name) continue;
                    found.Add(m);
                    if (!ParamsMatch(m.GetParameters(), ps)) continue;
                    if (returns != null && !ParamMatches(m.ReturnType, returns)) { retMiss.Add(m); continue; }
                    hit.Add(m);
                }
                if (hit.Count == 1) return hit[0];
                if (hit.Count > 1) { why = "ambiguous: " + Join(hit); return null; }
            }
            if (retMiss.Count > 0) { why = "returns: " + Join(retMiss); return null; }
            if (found.Count > 0) { why = "params: " + Join(found); return null; }
            if (!isStatic || anyStatic)
            {
                // the same name with the other static-ness?
                var oflags = Decl | BindingFlags.Static | BindingFlags.Instance;
                for (var cur = t; cur != null; cur = cur.BaseType)
                    foreach (var m in cur.GetMethods(oflags)) if (m.Name == name && !anyStatic) { why = "static mismatch"; return null; }
            }
            why = "missing";
            return null;
        }

        private static bool ParamsMatch(ParameterInfo[] actual, string[] expected)
        {
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++) if (!ParamMatches(actual[i].ParameterType, expected[i])) return false;
            return true;
        }

        private static string Join(IEnumerable<MemberInfo> ms) { return string.Join(" | ", ms.Select(Describe).ToArray()); }

        /// <summary>FullName, or Name, or the C# alias (int float double bool string object ...), "T[]" and "T&amp;" (ref/out).</summary>
        public static bool ParamMatches(Type actual, string expected)
        {
            if (actual == null || expected == null) return false;
            expected = expected.Trim();
            if (expected.EndsWith("&")) return actual.IsByRef && ParamMatches(actual.GetElementType(), expected.Substring(0, expected.Length - 1));
            if (actual.IsByRef) return false;
            if (expected.EndsWith("[]")) return actual.IsArray && actual.GetArrayRank() == 1 && ParamMatches(actual.GetElementType(), expected.Substring(0, expected.Length - 2));
            Type alias = Alias(expected);
            if (alias != null) return actual == alias;
            return actual.FullName == expected || actual.Name == expected;
        }

        private static Type Alias(string a)
        {
            switch (a)
            {
                case "int": return typeof(int);
                case "uint": return typeof(uint);
                case "long": return typeof(long);
                case "short": return typeof(short);
                case "byte": return typeof(byte);
                case "char": return typeof(char);
                case "float": return typeof(float);
                case "double": return typeof(double);
                case "bool": return typeof(bool);
                case "string": return typeof(string);
                case "object": return typeof(object);
                case "void": return typeof(void);
                default: return null;
            }
        }

        /// <summary>"Type.name(P1,P2) : Ret" with full type names; the text of the log and of the member-sig command.</summary>
        public static string Describe(MemberInfo m)
        {
            if (m == null) return "(none)";
            var sb = new StringBuilder();
            sb.Append(m.DeclaringType == null ? "" : m.DeclaringType.FullName + ".").Append(m.Name);
            var mb = m as MethodBase;
            if (mb != null)
            {
                sb.Append('(').Append(string.Join(",", mb.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToArray())).Append(')');
                var mi = m as MethodInfo;
                if (mi != null) sb.Append(" : ").Append(mi.ReturnType.FullName ?? mi.ReturnType.Name);
                return sb.ToString();
            }
            var f = m as FieldInfo; if (f != null) return sb.Append(" : ").Append(f.FieldType.FullName ?? f.FieldType.Name).ToString();
            var p2 = m as PropertyInfo; if (p2 != null) return sb.Append(" : ").Append(p2.PropertyType.FullName ?? p2.PropertyType.Name).ToString();
            var ev = m as EventInfo; if (ev != null) return sb.Append(" : ").Append(ev.EventHandlerType == null ? "?" : ev.EventHandlerType.FullName ?? ev.EventHandlerType.Name).ToString();
            return m is Type ? ((Type)m).FullName : sb.ToString();
        }

        // ------------------------------------------------------------------ self-test

        // targets of the self-test; real names are taken with typeof(X).FullName
        internal class ResolverProbeBase
        {
            private int _hidden = 7;
            private static int _sHidden = 3;
            public int Vis;
            internal int HiddenValue() { return _hidden + _sHidden; }
            public virtual int Over(int a) { return a; }
        }

        internal sealed class ResolverProbe : ResolverProbeBase
        {
            public string Label { get; set; }
            public ResolverProbe() { }
            public ResolverProbe(int n) { Vis = n; }
            public ResolverProbe(string s) { Label = s; }
            internal struct ProbeEntry { public int N; }
            public ResolverProbe(in ProbeEntry e, bool b) { Vis = e.N; }   // T-307: the beta's MapLayer(owner, in Entry, bool)
            public int M(int a) { return a + 1; }
            public string M(string s) { return s + "!"; }
            public int Opt(int a, string b = "x") { return a; }
            public static int Inst(int a) { return a; }
            public int Inst2(int a) { return a; }
            public override int Over(int a) { return a + 100; }
            public float Fl(float f, double d, bool b, object o, string s, int[] arr) { return f; }
            public void Out(out int x) { x = 1; }
        }

        private static string Why(Type t, MemberSpec s) { string w; Resolve(t, s, out w); return w; }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            try
            {
                string probe = typeof(ResolverProbe).FullName;
                string w;
                Type pt = typeof(ResolverProbe);
                var mi = Resolve(pt, MemberSpec.Method("t", probe, "M", new[] { "int" }, "int"), out w) as MethodInfo;
                if (mi == null || mi.GetParameters()[0].ParameterType != typeof(int)) return "resolver: M(int) -> " + w;
                var ms = Resolve(pt, MemberSpec.Method("t", probe, "M", new[] { "System.String" }, "string"), out w) as MethodInfo;
                if (ms == null || ms.ReturnType != typeof(string)) return "resolver: M(string) -> " + w;
                if (Resolve(pt, MemberSpec.Method("t", probe, "M", new[] { "float" }), out w) != null) return "resolver: M(float) must not resolve";
                if (w == null || !w.StartsWith("params:") || !w.Contains("System.Int32") || !w.Contains("System.String")) return "resolver: M(float) why = " + w;
                if (Resolve(pt, MemberSpec.Method("t", probe, "M", new[] { "int" }, "string"), out w) != null || w == null || !w.StartsWith("returns:")) return "resolver: wrong Returns, why = " + w;
                if (Why(pt, MemberSpec.Method("t", probe, "Nope", new string[0])) != "missing") return "resolver: unknown method";
                if (Why(pt, MemberSpec.Method("t", probe, "M", null)) != "params required") return "resolver: Params are mandatory";
                // private field of the base class, found through the derived type
                var hf = Resolve(pt, MemberSpec.Field("t", probe, "_hidden"), out w) as FieldInfo;
                if (hf == null || hf.DeclaringType != typeof(ResolverProbeBase)) return "resolver: private base field -> " + w;
                if (Resolve(pt, MemberSpec.Field("t", probe, "_sHidden", true, true), out w) == null) return "resolver: private static base field -> " + w;
                if (Why(pt, MemberSpec.Field("t", probe, "_sHidden")) != "static mismatch") return "resolver: static vs instance field";
                if (Resolve(pt, MemberSpec.Prop("t", probe, "Label", true, false, false, "string"), out w) == null) return "resolver: property -> " + w;
                if (Why(pt, MemberSpec.Prop("t", probe, "Label", true, false, false, "int")) == null) return "resolver: property Returns";
                // static vs instance method
                if (Resolve(pt, MemberSpec.Method("t", probe, "Inst", new[] { "int" }, null, true, true), out w) == null) return "resolver: static method -> " + w;
                if (Why(pt, MemberSpec.Method("t", probe, "Inst", new[] { "int" })) != "static mismatch") return "resolver: static method called as instance";
                if (Resolve(pt, MemberSpec.Method("t", probe, "Inst2", new[] { "int" }), out w) == null) return "resolver: instance method -> " + w;
                // override: the most derived one, not ambiguous
                var ov = Resolve(pt, MemberSpec.Method("t", probe, "Over", new[] { "int" }), out w) as MethodInfo;
                if (ov == null || ov.DeclaringType != typeof(ResolverProbe)) return "resolver: override -> " + w;
                // constructors
                var c1 = Resolve(pt, MemberSpec.Ctor("t", probe, new[] { "int" }), out w) as ConstructorInfo;
                var c2 = Resolve(pt, MemberSpec.Ctor("t", probe, new[] { "string" }), out w) as ConstructorInfo;
                var c0 = Resolve(pt, MemberSpec.Ctor("t", probe, new string[0]), out w) as ConstructorInfo;
                if (c1 == null || c2 == null || c0 == null || c1.GetParameters()[0].ParameterType != typeof(int)) return "resolver: ctor -> " + w;
                if (Resolve(pt, MemberSpec.Ctor("t", probe, new[] { probe + "+ProbeEntry&", "bool" }), out w) as ConstructorInfo == null) return "resolver: in-struct ctor -> " + w;
                if (Resolve(pt, MemberSpec.Ctor("t", probe, new[] { probe + "+ProbeEntry", "bool" }), out w) != null) return "resolver: in-struct ctor without & must not resolve";
                if (Resolve(pt, MemberSpec.Ctor("t", probe, new[] { "double" }), out w) != null || !w.StartsWith("params:")) return "resolver: ctor(double) must not resolve";
                // aliases, arrays, ref
                if (Resolve(pt, MemberSpec.Method("t", probe, "Fl", new[] { "float", "double", "bool", "object", "string", "int[]" }, "float"), out w) == null) return "resolver: aliases -> " + w;
                if (Resolve(pt, MemberSpec.Method("t", probe, "Out", new[] { "int&" }, "void"), out w) == null) return "resolver: ref param -> " + w;
                if (!ParamMatches(typeof(float), "float") || !ParamMatches(typeof(float), "System.Single") || !ParamMatches(typeof(float), "Single")) return "resolver: ParamMatches float";
                if (ParamMatches(typeof(float), "double") || ParamMatches(typeof(int), "long") || ParamMatches(typeof(string), "object")) return "resolver: ParamMatches false positive";
                // type
                if (Resolve(pt, MemberSpec.TypeOf("t", probe), out w) != pt) return "resolver: type";
                if (Resolve(null, MemberSpec.TypeOf("t", probe), out w) != null || w != "type") return "resolver: null type";
                // patch: Returns is mandatory and compared; any static-ness
                var ps = new PatchSpec("p1", "t", probe, "M", new[] { "int" }, "int", PatchKind.Prefix, "always");
                if (ResolvePatch(pt, ps, out w) == null) return "resolver: patch -> " + w;
                if (ResolvePatch(pt, new PatchSpec("p2", "t", probe, "M", new[] { "int" }, "string", PatchKind.Prefix, "always"), out w) != null) return "resolver: patch with wrong Returns";
                if (ResolvePatch(pt, new PatchSpec("p3", "t", probe, "Inst", new[] { "int" }, "int", PatchKind.Postfix, "always"), out w) == null) return "resolver: patch of a static method -> " + w;
                bool threw = false;
                try { new PatchSpec("p4", "t", probe, "M", new[] { "int" }, null, PatchKind.Prefix, "always"); } catch (ArgumentException) { threw = true; }
                if (!threw) return "resolver: PatchSpec without Returns must throw";
                // keys and indices
                var k = MemberSpec.Method("cap", "A.B", "m", new[] { "int", "string" });
                if (k.Key != "A.B.m(int,string)") return "resolver: Key = " + k.Key;
                if (MemberSpec.Ctor("c", "A.B", new string[0]).Key != "A.B..ctor()") return "resolver: ctor Key";
                var a = MemberSpec.Field("c", "A.B", "f"); var b = MemberSpec.Field("c", "A.B", "f");
                if (a.Index == b.Index) return "resolver: Index must be unique";
                return null;
            }
            catch (Exception e) { return "resolver: " + e.GetType().Name + ": " + e.Message; }
        }
    }

    /// <summary>The specifications of one module (one *Specs.cs class): members, patches, derived capabilities.</summary>
    internal sealed class SpecSet
    {
        /// <summary>Name of the optional static method of a *Specs class: Derive(GameApi, ISet&lt;string&gt;).</summary>
        public const string DeriveName = "Derive";

        public readonly string Module;                          // Id of the IUiFeature
        public readonly IList<MemberSpec> Members;
        public readonly IList<PatchSpec> Patches;
        public readonly Action<GameApi, ISet<string>> Derive;   // null = none

        public SpecSet(string module, IList<MemberSpec> members, IList<PatchSpec> patches, Action<GameApi, ISet<string>> derive)
        {
            Module = module; Members = members ?? new List<MemberSpec>(); Patches = patches ?? new List<PatchSpec>(); Derive = derive;
        }

        /// <summary>All static readonly MemberSpec / PatchSpec fields of the class in declaration order, and its static Derive(GameApi, ISet&lt;string&gt;).</summary>
        public static SpecSet Of(string module, Type specsClass)
        {
            var fields = specsClass.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .OrderBy(f => f.MetadataToken).ToList();
            var members = new List<MemberSpec>();
            var patches = new List<PatchSpec>();
            foreach (var f in fields)
            {
                var v = f.GetValue(null);
                var ms = v as MemberSpec; if (ms != null) members.Add(ms);
                var ps = v as PatchSpec; if (ps != null) patches.Add(ps);
            }
            Action<GameApi, ISet<string>> derive = null;
            var dm = specsClass.GetMethod(DeriveName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(GameApi), typeof(ISet<string>) }, null);
            if (dm != null) derive = (Action<GameApi, ISet<string>>)Delegate.CreateDelegate(typeof(Action<GameApi, ISet<string>>), dm);
            return new SpecSet(module, members, patches, derive);
        }
    }
}
