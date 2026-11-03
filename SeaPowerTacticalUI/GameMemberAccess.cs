using System;
using System.Collections.Generic;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-120 (ADR-0043): access to game members by descriptor. The member is found once by its exact signature (MemberResolver) and cached by
    /// MemberSpec.Index: no string building on the hot path. UI part of GameApi only: the voice mod does not compile this file.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>The specs of all modules; UiCore sets it before SelfCheck (collected from IUiFeature.SelfCheck). null = UiRegistry.All().</summary>
        internal IList<SpecSet> UiSpecs;

        /// <summary>The result of the last SelfCheckMod; null before the first SelfCheck.</summary>
        internal RegistryReport Registry { get; private set; }

        /// <summary>Is the object one of the player's own? OwnOnly members are served for such objects only (ADR-0045). null = nobody is own (safe default).</summary>
        private Func<object, bool> _isOwnObject;

        internal void SetOwnPredicate(Func<object, bool> isOwn) { _isOwnObject = isOwn; }

        private MemberInfo[] _resolved = new MemberInfo[256];
        private bool[] _resolvedDone = new bool[256];
        private Type[] _declType = new Type[256];   // T-125: the declaring type of the spec, filled in Member(); MemberFor reads it instead of FindType(s.Type)
        private readonly HashSet<int> _warned = new HashSet<int>();
        private readonly Dictionary<MemberSpec, Dictionary<Type, MemberInfo>> _duck = new Dictionary<MemberSpec, Dictionary<Type, MemberInfo>>();
        private readonly Dictionary<PatchSpec, MethodInfo> _patchTargets = new Dictionary<PatchSpec, MethodInfo>();

        // UniRx.ReactiveProperty<T>.Value for any T
        private static readonly MemberSpec RxValueSpec = MemberSpec.DuckMember("rx", "Value", MemberKind.Property, null, "ReactiveProperty<T>.Value: T differs per property");

        internal void SetRegistry(RegistryReport r) { Registry = r; }

        private void WarnOnce(int key, string line)
        {
            if (_warned.Add(key)) { try { Log("member: " + line); } catch { } }
        }

        // ---------- resolving ----------

        /// <summary>The resolved member, or null (logged once). For a duck spec use the object overloads.</summary>
        internal MemberInfo Member(MemberSpec s)
        {
            if (s == null || s.Duck) return null;
            int i = s.Index;
            if (i >= _resolved.Length)
            {
                int n = Math.Max(i + 1, _resolved.Length * 2);
                Array.Resize(ref _resolved, n);
                Array.Resize(ref _resolvedDone, n);
                Array.Resize(ref _declType, n);
            }
            if (_resolvedDone[i]) return _resolved[i];
            string why;
            var dt = FindType(s.Type);
            var m = MemberResolver.Resolve(dt, s, out why);
            _declType[i] = dt;
            _resolved[i] = m; _resolvedDone[i] = true;
            if (m == null) WarnOnce(i, s.Key + " unavailable (" + why + ")");
            return m;
        }

        internal bool Present(MemberSpec s) { return Member(s) != null; }

        /// <summary>The same resolver as selfcheck: the patch target or null.</summary>
        internal MethodInfo PatchTarget(PatchSpec p)
        {
            MethodInfo m;
            if (_patchTargets.TryGetValue(p, out m)) return m;
            string why;
            m = MemberResolver.ResolvePatch(FindType(p.Type), p, out why);
            _patchTargets[p] = m;
            if (m == null) WarnOnce(p.Id.GetHashCode() ^ 0x5bd1e995, "patch " + p.Id + " " + p.Key + " unavailable (" + why + ")");
            return m;
        }

        /// <summary>Is the object an instance of the spec's type (a type spec)? False when the type is not there.</summary>
        internal bool IsOfType(object o, MemberSpec typeSpec)
        {
            var t = o == null ? null : TypeOf(typeSpec);
            return t != null && t.IsInstanceOfType(o);
        }

        internal Type TypeOf(MemberSpec typeSpec) { return Member(typeSpec) as Type; }

        // ---------- singletons (T-125): the reflection is built once per type spec, the answer is kept for one frame ----------

        private sealed class SingletonProbe
        {
            public Func<bool> Exists0; public Func<bool, bool> Exists1;   // InstanceExists() / InstanceExists(bool) via Delegate.CreateDelegate
            public MethodInfo ExistsMi; public bool ExistsTakesArg;       // fallback when CreateDelegate fails
            public Func<object> Instance; public PropertyInfo InstancePi; // static Instance getter (delegate, else PropertyInfo)
            public bool Missing;                                          // no type / no Instance: always null
        }

        private SingletonProbe[] _singletons = new SingletonProbe[256];
        private long[] _singletonFrame = new long[256], _instanceFrame = new long[256];   // frame number + 1 of the cached value; 0 = none
        private object[] _singletonValue = new object[256], _instanceValue = new object[256];
        private static readonly object[] FalseArg = { false };   // the one argument array of the MethodInfo fallback
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;

        private void GrowSingletons(int i)
        {
            if (i < _singletons.Length) return;
            int n = Math.Max(i + 1, _singletons.Length * 2);
            Array.Resize(ref _singletons, n);
            Array.Resize(ref _singletonFrame, n); Array.Resize(ref _instanceFrame, n);
            Array.Resize(ref _singletonValue, n); Array.Resize(ref _instanceValue, n);
        }

        private SingletonProbe ProbeFor(MemberSpec typeSpec)
        {
            int i = typeSpec.Index;
            GrowSingletons(i);
            var pr = _singletons[i];
            if (pr != null) return pr;
            pr = new SingletonProbe();
            try
            {
                var t = TypeOf(typeSpec);
                if (t == null) pr.Missing = true;
                else
                {
                    var ie = t.GetMethod("InstanceExists", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    if (ie != null)
                    {
                        pr.ExistsMi = ie;
                        var ps = ie.GetParameters();
                        pr.ExistsTakesArg = ps.Length != 0;
                        try
                        {
                            if (ps.Length == 0 && ie.ReturnType == typeof(bool)) pr.Exists0 = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), ie);
                            else if (ps.Length == 1 && ps[0].ParameterType == typeof(bool) && ie.ReturnType == typeof(bool)) pr.Exists1 = (Func<bool, bool>)Delegate.CreateDelegate(typeof(Func<bool, bool>), ie);
                        }
                        catch { pr.Exists0 = null; pr.Exists1 = null; }
                    }
                    var p = t.GetProperty("Instance", AllStat);
                    if (p == null || p.GetGetMethod(true) == null) pr.Missing = true;
                    else
                    {
                        pr.InstancePi = p;
                        try { pr.Instance = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), p.GetGetMethod(true)); }
                        catch { pr.Instance = null; }
                    }
                }
            }
            catch { pr.Missing = true; }
            _singletons[i] = pr;
            return pr;
        }

        /// <summary>The same steps as the common code: InstanceExists(false), then Instance; any exception = null.</summary>
        private object ReadSingleton(SingletonProbe pr)
        {
            if (pr.Missing) return null;
            try
            {
                if (pr.Exists0 != null) { if (!pr.Exists0()) return null; }
                else if (pr.Exists1 != null) { if (!pr.Exists1(false)) return null; }
                else if (pr.ExistsMi != null && !(bool)pr.ExistsMi.Invoke(null, pr.ExistsTakesArg ? FalseArg : null)) return null;
                return pr.Instance != null ? pr.Instance() : pr.InstancePi.GetValue(null, null);
            }
            catch { return null; }
        }

        // the frame to cache under; -1 = no cache (no clock, or not the thread that owns the cache)
        private long SingletonFrame()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread) return -1;
            return FrameNow();
        }

        /// <summary>Replacement of SingletonIfExists("..."): the live Instance of the type (the Unity liveness test included), or null; read once per frame.</summary>
        internal object Singleton(MemberSpec typeSpec)
        {
            if (typeSpec == null || typeSpec.Type == null) return null;
            var pr = ProbeFor(typeSpec);
            long f = SingletonFrame();
            if (f < 0) { var o = ReadSingleton(pr); return IsAlive(o) ? o : null; }
            int i = typeSpec.Index;
            if (_singletonFrame[i] == f + 1) return _singletonValue[i];
            var v = ReadSingleton(pr);
            if (!IsAlive(v)) v = null;
            _singletonValue[i] = v; _singletonFrame[i] = f + 1;
            return v;
        }

        /// <summary>Replacement of SingletonInstance("..."): the Instance property without the Unity liveness test (the old SingletonInstance semantics); read once per frame.</summary>
        internal object SingletonInstance(MemberSpec typeSpec)
        {
            if (typeSpec == null || typeSpec.Type == null) return null;
            var pr = ProbeFor(typeSpec);
            long f = SingletonFrame();
            if (f < 0) return ReadSingleton(pr);
            int i = typeSpec.Index;
            if (_instanceFrame[i] == f + 1) return _instanceValue[i];
            var v = ReadSingleton(pr);
            _instanceValue[i] = v; _instanceFrame[i] = f + 1;
            return v;
        }

        /// <summary>The member for this object: the declared type must fit (duck: looked up on the object's own type).</summary>
        private MemberInfo MemberFor(object obj, MemberSpec s)
        {
            if (s == null || obj == null) return null;
            if (s.Duck)
            {
                Dictionary<Type, MemberInfo> byType;
                if (!_duck.TryGetValue(s, out byType)) _duck[s] = byType = new Dictionary<Type, MemberInfo>();
                MemberInfo dm;
                var ot = obj.GetType();
                if (!byType.TryGetValue(ot, out dm))
                {
                    string why;
                    byType[ot] = dm = MemberResolver.Resolve(ot, s, out why);
                }
                return dm;
            }
            var m = Member(s);
            if (m == null) return null;
            var t = _declType[s.Index];
            if (!s.Static && t != null && !t.IsInstanceOfType(obj))
            {
                WarnOnce(s.Index + 0x10000, s.Key + " asked on " + obj.GetType().FullName);
                return null;
            }
            return m;
        }

        // ---------- data ----------

        internal object Get(object obj, MemberSpec s)
        {
            try
            {
                var m = MemberFor(obj, s);
                var p = m as PropertyInfo; if (p != null) return p.GetValue(obj, null);
                var f = m as FieldInfo; if (f != null) return f.GetValue(obj);
            }
            catch { }
            return null;
        }

        internal object GetStatic(MemberSpec s)
        {
            try
            {
                var m = Member(s);
                var p = m as PropertyInfo; if (p != null) return p.GetValue(null, null);
                var f = m as FieldInfo; if (f != null) return f.GetValue(null);
            }
            catch { }
            return null;
        }

        internal bool Set(object obj, MemberSpec s, object value)
        {
            try
            {
                var m = MemberFor(obj, s);
                var p = m as PropertyInfo; if (p != null && p.CanWrite) { p.SetValue(obj, value, null); return true; }
                var f = m as FieldInfo; if (f != null) { f.SetValue(obj, value); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>UniRx ReactiveProperty&lt;T&gt;.Value of the property named by the spec.</summary>
        internal object RxGet(object obj, MemberSpec s)
        {
            var rp = Get(obj, s);
            return rp == null ? null : Get(rp, RxValueSpec);
        }

        internal void RxSet(object obj, MemberSpec s, object v)
        {
            var rp = Get(obj, s);
            if (rp == null) throw new MissingMemberException(s.Type, s.Member);
            if (!Set(rp, RxValueSpec, v)) throw new MissingMemberException(rp.GetType().Name, RxValueSpec.Member);
        }

        // ---------- calls ----------

        private MethodInfo MethodFor(object obj, MemberSpec s)
        {
            var m = MemberFor(obj, s) as MethodInfo;
            if (m == null) throw new MissingMemberException(s == null ? "(null)" : s.Type ?? "(duck)", s == null ? "" : s.Member);
            return m;
        }

        internal object Call(object obj, MemberSpec s, params object[] args)
        {
            return MethodFor(obj, s).Invoke(obj, args);
        }

        /// <summary>Like Call, but omitted trailing parameters get their default values (as a normal C# call).</summary>
        internal object CallOpt(object obj, MemberSpec s, params object[] given)
        {
            var m = MethodFor(obj, s);
            return m.Invoke(obj, FillDefaults(m, given));
        }

        internal object CallStatic(MemberSpec s, params object[] args)
        {
            var m = Member(s) as MethodInfo;
            if (m == null) throw new MissingMemberException(s == null ? "(null)" : s.Type, s == null ? "" : s.Member);
            return m.Invoke(null, args);
        }

        /// <summary>Like CallStatic, but omitted trailing parameters get their default values.</summary>
        internal object CallStaticOpt(MemberSpec s, params object[] given)
        {
            var m = Member(s) as MethodInfo;
            if (m == null) throw new MissingMemberException(s == null ? "(null)" : s.Type, s == null ? "" : s.Member);
            return m.Invoke(null, FillDefaults(m, given));
        }

        internal object New(MemberSpec ctor, params object[] args)
        {
            var c = Member(ctor) as ConstructorInfo;
            if (c == null) throw new MissingMemberException(ctor == null ? "(null)" : ctor.Type, ".ctor");
            return c.Invoke(args);
        }

        private static object[] FillDefaults(MethodBase m, object[] given)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = given != null && i < given.Length ? given[i] : DefaultFor(ps[i]);
            return args;
        }

        // ---------- own units only (ADR-0045 section 2) ----------

        private bool OwnObject(object obj)
        {
            try { return obj != null && _isOwnObject != null && _isOwnObject(obj); }
            catch { return false; }
        }

        /// <summary>OwnOnly spec: the value for a player's own object, null for any other.</summary>
        internal object GetOwn(object obj, MemberSpec s) { return OwnObject(obj) ? Get(obj, s) : null; }

        /// <summary>OwnOnly ReactiveProperty: its Value for a player's own object, null for any other.</summary>
        internal object RxGetOwn(object obj, MemberSpec s) { return OwnObject(obj) ? RxGet(obj, s) : null; }

        internal object CallOwn(object obj, MemberSpec s, params object[] args) { return OwnObject(obj) ? Call(obj, s, args) : null; }

        // ---------- self-test (selfcheck ui) ----------

        /// <summary>null = ok. On the probe types of MemberResolver (no game needed).</summary>
        internal string SelfTestMemberAccess()
        {
            var saveLog = Log; var saveOwn = _isOwnObject;
            var lines = new List<string>();
            try
            {
                Log = lines.Add;
                string pt = typeof(MemberResolver.ResolverProbe).FullName;
                var vis = MemberSpec.Field("t", pt, "Vis");
                var hidden = MemberSpec.Field("t", pt, "_hidden");
                var label = MemberSpec.Prop("t", pt, "Label");
                var m1 = MemberSpec.Method("t", pt, "M", new[] { "int" }, "int");
                var ms = MemberSpec.Method("t", pt, "M", new[] { "string" }, "string");
                var opt = MemberSpec.Method("t", pt, "Opt", new[] { "int", "string" }, "int");
                var stat = MemberSpec.Method("t", pt, "Inst", new[] { "int" }, "int", true, true);
                var ctor = MemberSpec.Ctor("t", pt, new[] { "int" });
                var gone = MemberSpec.Method("t", pt, "Gone", new string[0]);
                var duck = MemberSpec.DuckMember("t", "Label", MemberKind.Property, null, "selftest duck");
                var own = MemberSpec.Field("t", pt, "Vis", true, false, true);
                var o = new MemberResolver.ResolverProbe(3);
                var other = new MemberResolver.ResolverProbe(9);
                if (!(Get(o, vis) is int) || (int)Get(o, vis) != 3) return "access: Get field";
                if (!Set(o, vis, 5) || (int)Get(o, vis) != 5) return "access: Set field";
                if (!Set(o, label, "lbl") || (string)Get(o, label) != "lbl") return "access: Set/Get property";
                if (!(Get(o, hidden) is int) || (int)Get(o, hidden) != 7) return "access: private base field";
                if ((int)Call(o, m1, 4) != 5 || (string)Call(o, ms, "a") != "a!") return "access: Call picks the overload by signature";
                if ((int)CallOpt(o, opt, 8) != 8) return "access: CallOpt";
                if ((int)CallStatic(stat, 6) != 6) return "access: CallStatic";
                var made = New(ctor, 11) as MemberResolver.ResolverProbe;
                if (made == null || made.Vis != 11) return "access: New";
                if ((string)Get(o, duck) != "lbl") return "access: duck member";
                if (!Present(vis) || Present(gone)) return "access: Present";
                bool threw = false;
                try { Call(o, gone); } catch (MissingMemberException) { threw = true; }
                if (!threw) return "access: Call of a missing member must throw MissingMemberException";
                // a wrong object: null and exactly one log line per spec
                lines.Clear();
                if (Get(new object(), vis) != null || Get("x", vis) != null) return "access: wrong object type must give null";
                int mismatch = 0;
                foreach (var l in lines) if (l.Contains(vis.Key)) mismatch++;
                if (mismatch != 1) return "access: wrong type logs " + mismatch + " lines: " + string.Join(" | ", lines.ToArray());
                // OwnOnly: only for the player's own object; without a predicate nobody is own
                if (GetOwn(o, own) != null) return "access: GetOwn without predicate must be null";
                SetOwnPredicate(x => ReferenceEquals(x, o));
                if (GetOwn(o, own) == null || (int)GetOwn(o, own) != 5) return "access: GetOwn of own object";
                if (GetOwn(other, own) != null) return "access: GetOwn of a foreign object must be null";
                if (CallOwn(other, m1, 1) != null || (int)CallOwn(o, m1, 1) != 2) return "access: CallOwn";
                // patch target by the same resolver
                var tg = PatchTarget(new PatchSpec("selftest_access", "t", pt, "M", new[] { "string" }, "string", PatchKind.Prefix, "always"));
                if (tg == null || tg.ReturnType != typeof(string)) return "access: PatchTarget";
                return null;
            }
            catch (Exception e) { return "access: " + e.GetType().Name + ": " + e.Message; }
            finally { Log = saveLog; _isOwnObject = saveOwn; }
        }
    }
}
