using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace VoiceBotBridge.Dev
{
    /// <summary>
    /// T-165 live A/B of the graphics levers (dev mod only, docs/research/seapower-performance.md section 4): `gfx` dump / set / reset.
    /// Every member is reached by reflection by name (Unity, Ceto, Amplify, game scripts), BCL only, so devtest compiles it with fake types.
    /// The value before the first write is kept per lever and written back on on=false / reset. Runs on the Unity main thread (DevPump);
    /// Customer scope (2026-10-21): no render-rate / render-scale levers, the PiP camera is never touched. Never part of the Workshop DLL (ADR-0061 approach).
    /// </summary>
    internal sealed class DevGfx
    {
        /// <summary>Lever ids (research L2-L5, L7, L8 + fps60 for measuring without the 30 fps limiter).</summary>
        public static readonly string[] Levers = { "rcm", "ssao", "bloom_lite", "cloud", "refl_mask", "ovl", "lod", "plight", "softp", "particles", "fps60" };

        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        /// <summary>One written member: how to read / write it and the value before the first write.</summary>
        private sealed class Edit
        {
            public string Name;
            public Func<object> Get;
            public Action<object> Set;
            public object Was;
            public object Target;   // the scene object written (cloud: the Clouds GameObject, found again only through it once inactive)
        }

        private readonly Func<string, Type> _findType;
        private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
        private readonly Dictionary<string, List<Edit>> _applied = new Dictionary<string, List<Edit>>();
        private readonly List<string> _order = new List<string>();

        private readonly Func<long> _nowMs;

        public DevGfx(Func<string, Type> findType = null, Func<long> nowMs = null)
        {
            _findType = findType ?? FindInDomain;
            if (nowMs == null) { var sw = System.Diagnostics.Stopwatch.StartNew(); nowMs = () => sw.ElapsedMilliseconds; }
            _nowMs = nowMs;
        }

        private static Type FindInDomain(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); } catch { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        private Type T(string name)
        {
            Type t;
            if (_types.TryGetValue(name, out t) && t != null) return t;
            t = _findType(name);
            _types[name] = t;   // a miss is retried next call (assemblies may load later)
            return t;
        }

        // ---------- reflection helpers ----------

        private static MemberInfo Data(Type t, string name, bool isStatic)
        {
            for (var c = t; c != null; c = c.BaseType)
            {
                var flags = (isStatic ? Stat : Inst) | BindingFlags.DeclaredOnly;
                var p = c.GetProperty(name, flags);
                if (p != null) return p;
                var f = c.GetField(name, flags);
                if (f != null) return f;
            }
            return null;
        }

        private static Type DataType(MemberInfo m) { var p = m as PropertyInfo; return p != null ? p.PropertyType : ((FieldInfo)m).FieldType; }

        private static object Read(MemberInfo m, object o)
        {
            var p = m as PropertyInfo;
            return p != null ? p.GetValue(o, null) : ((FieldInfo)m).GetValue(o);
        }

        private static void Write(MemberInfo m, object o, object v)
        {
            v = Coerce(v, DataType(m));
            var p = m as PropertyInfo;
            if (p != null) p.SetValue(o, v, null); else ((FieldInfo)m).SetValue(o, v);
        }

        /// <summary>Value to the member type: enum by name or number, LayerMask-like struct by its int `value`, numbers by Convert.</summary>
        private static object Coerce(object v, Type t)
        {
            if (v == null || t.IsInstanceOfType(v)) return v;
            if (t.IsEnum) return v is string ? Enum.Parse(t, (string)v) : Enum.ToObject(t, Convert.ToInt32(v, CultureInfo.InvariantCulture));
            if (t.IsValueType && !t.IsPrimitive)
            {
                var vp = t.GetProperty("value", Inst);
                if (vp != null && vp.CanWrite)
                {
                    object box = Activator.CreateInstance(t);
                    vp.SetValue(box, Coerce(v, vp.PropertyType), null);
                    return box;
                }
            }
            return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            try { var m = Data(o.GetType(), name, false); return m == null ? null : Read(m, o); } catch { return null; }
        }

        private object SGet(string type, string name)
        {
            var t = T(type);
            if (t == null) return null;
            try { var m = Data(t, name, true); return m == null ? null : Read(m, null); } catch { return null; }
        }

        private static MethodInfo Method(Type t, string name, Type[] ps, bool isStatic)
        {
            foreach (var mi in t.GetMethods((isStatic ? Stat : Inst)))
            {
                if (mi.Name != name || mi.IsGenericMethodDefinition) continue;
                var pp = mi.GetParameters();
                if (pp.Length != ps.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++) if (pp[i].ParameterType != ps[i]) { ok = false; break; }
                if (ok) return mi;
            }
            return null;
        }

        private static object Call(object o, string name, Type[] ps, params object[] args)
        {
            if (o == null) return null;
            try { var mi = Method(o.GetType(), name, ps, false); return mi == null ? null : mi.Invoke(o, args); } catch { return null; }
        }

        private object SCall(string type, string name, Type[] ps, params object[] args)
        {
            var t = T(type);
            if (t == null) return null;
            try { var mi = Method(t, name, ps, true); return mi == null ? null : mi.Invoke(null, args); } catch { return null; }
        }

        /// <summary>JSON-friendly copy: numbers, bools, strings; enums and others by name / ToString; LayerMask-like structs by `value`.</summary>
        private static object Out(object v)
        {
            if (v == null || v is bool || v is string || v is int || v is long) return v;
            if (v is float || v is double) return Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture), 4);
            var t = v.GetType();
            if (t.IsEnum) return v.ToString();
            if (t.IsValueType && !t.IsPrimitive) { var vp = t.GetProperty("value", Inst); if (vp != null) { try { return Out(vp.GetValue(v, null)); } catch { } } }
            if (t.IsPrimitive) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            var n = Get(v, "name") as string;
            return n ?? t.Name;
        }

        // ---------- scene objects ----------

        private object MainCamera()
        {
            var cam = SGet("SeaPower.CameraManager", "Main");
            return cam ?? SGet("UnityEngine.Camera", "main");
        }

        private object Comp(object host, string type)
        {
            var t = T(type);
            if (host == null || t == null) return null;
            return Call(host, "GetComponent", new[] { typeof(Type) }, t);
        }

        private object FindFirst(string type)
        {
            var t = T(type);
            if (t == null) return null;
            return SCall("UnityEngine.Object", "FindFirstObjectByType", new[] { typeof(Type) }, t)
                ?? SCall("UnityEngine.Object", "FindObjectOfType", new[] { typeof(Type) }, t);
        }

        private object Ocean() { return SGet("Ceto.Ocean", "Instance"); }
        private object Reflection() { var o = Ocean(); return Get(o, "Reflection") ?? Comp(o, "Ceto.PlanarReflection") ?? FindFirst("Ceto.PlanarReflection"); }
        private object UnderWater() { var o = Ocean(); return Get(o, "UnderWater") ?? Comp(o, "Ceto.UnderWater"); }
        private object CloudShadow() { return Comp(MainCamera(), "SeaPower.ScreenSpaceCloudShadow") ?? FindFirst("SeaPower.ScreenSpaceCloudShadow"); }

        private object CloudsParent()
        {
            List<Edit> eds;
            if (_applied.TryGetValue("cloud", out eds)) foreach (var e in eds) if (e.Name == "Clouds.active") return e.Target;
            return SCall("UnityEngine.GameObject", "Find", new[] { typeof(string) }, "Clouds");
        }

        // ---------- dump ----------

        public Dictionary<string, object> Dump()
        {
            var missing = new List<string>();
            var cams = new List<object>();
            var main = MainCamera();
            var all = SGet("UnityEngine.Camera", "allCameras") as IEnumerable;
            if (all == null) missing.Add("UnityEngine.Camera.allCameras");
            else foreach (var c in all)
                {
                    if (c == null) continue;
                    cams.Add(new Dictionary<string, object>
                    {
                        { "name", Get(c, "name") as string }, { "main", ReferenceEquals(c, main) }, { "enabled", Get(c, "enabled") },
                        { "depth", Out(Get(c, "depth")) }, { "target_texture", Out(Get(c, "targetTexture")) }, { "culling_mask", Out(Get(c, "cullingMask")) },
                    });
                }
            var behaviours = new List<object>();
            var mb = T("UnityEngine.MonoBehaviour");
            if (main == null) missing.Add("main camera");
            else if (mb == null) missing.Add("UnityEngine.MonoBehaviour");
            else
            {
                var arr = Call(main, "GetComponents", new[] { typeof(Type) }, mb) as IEnumerable;
                if (arr != null) foreach (var b in arr) if (b != null) behaviours.Add(new Dictionary<string, object> { { "type", b.GetType().FullName }, { "enabled", Get(b, "enabled") } });
            }
            var ocean = Ocean();
            var oceanD = ocean == null ? null : new Dictionary<string, object>
            {
                { "enabled", Get(ocean, "enabled") }, { "normal_overlay", Out(Get(ocean, "normalOverlaySize")) }, { "foam_overlay", Out(Get(ocean, "foamOverlaySize")) },
                { "height_overlay", Out(Get(ocean, "heightOverlaySize")) }, { "clip_overlay", Out(Get(ocean, "clipOverlaySize")) },
            };
            if (ocean == null) missing.Add("Ceto.Ocean.Instance");
            var refl = Reflection();
            var reflD = refl == null ? null : new Dictionary<string, object>
            {
                { "enabled", Get(refl, "enabled") }, { "reflection_mask", Out(Get(refl, "reflectionMask")) }, { "resolution", Out(Get(refl, "reflectionResolution")) },
                { "blur_mode", Out(Get(refl, "blurMode")) }, { "blur_iterations", Out(Get(refl, "blurIterations")) },
            };
            if (refl == null) missing.Add("Ceto.PlanarReflection");
            var uw = UnderWater();
            var uwD = uw == null ? null : new Dictionary<string, object> { { "enabled", Get(uw, "enabled") }, { "mode", Out(Get(uw, "underwaterMode")) }, { "depth_mode", Out(Get(uw, "depthMode")) } };
            const string Q = "UnityEngine.QualitySettings";
            var quality = new Dictionary<string, object>
            {
                { "lod_bias", Out(SGet(Q, "lodBias")) }, { "pixel_light_count", Out(SGet(Q, "pixelLightCount")) }, { "soft_particles", Out(SGet(Q, "softParticles")) },
                { "anisotropic_filtering", Out(SGet(Q, "anisotropicFiltering")) }, { "shadows", Out(SGet(Q, "shadows")) }, { "anti_aliasing", Out(SGet(Q, "antiAliasing")) },
                { "vsync_count", Out(SGet(Q, "vSyncCount")) },
            };
            var clouds = CloudsParent();
            var shadow = CloudShadow();
            return new Dictionary<string, object>
            {
                { "cameras", cams },
                { "main_camera", main == null ? null : new Dictionary<string, object> { { "name", Get(main, "name") as string }, { "behaviours", behaviours } } },
                { "ocean", oceanD }, { "reflection", reflD }, { "underwater", uwD }, { "quality", quality },
                { "clouds", new Dictionary<string, object> { { "parent", clouds != null }, { "active", clouds == null ? null : Get(clouds, "activeSelf") }, { "shadow_enabled", shadow == null ? null : Get(shadow, "enabled") } } },
                { "particle_systems", ParticleCount() },
                { "target_frame_rate", Out(SGet("UnityEngine.Application", "targetFrameRate")) },
                { "vsync_count", Out(SGet(Q, "vSyncCount")) },
                { "applied", new List<object>(_order.ConvertAll(x => (object)x)) },
                { "missing", missing.ConvertAll(x => (object)x) },
            };
        }

        private object ParticleCount()
        {
            var arr = Particles() as ICollection;
            return arr == null ? (object)null : arr.Count;
        }

        /// <summary>Active ParticleSystems now: Object.FindObjectsByType(Type, FindObjectsSortMode.None) (no sort), else FindObjectsOfType(Type). null = API missing.</summary>
        private IEnumerable Particles()
        {
            var pt = T("UnityEngine.ParticleSystem");
            var ot = T("UnityEngine.Object");
            if (pt == null || ot == null) return null;
            try
            {
                foreach (var mi in ot.GetMethods(Stat))
                {
                    if (mi.Name != "FindObjectsByType" || mi.IsGenericMethodDefinition) continue;
                    var pp = mi.GetParameters();
                    if (pp.Length == 2 && pp[0].ParameterType == typeof(Type) && pp[1].ParameterType.IsEnum)
                        return mi.Invoke(null, new object[] { pt, Enum.ToObject(pp[1].ParameterType, 0) }) as IEnumerable;   // FindObjectsSortMode.None = 0
                }
            }
            catch { }
            return SCall("UnityEngine.Object", "FindObjectsOfType", new[] { typeof(Type) }, pt) as IEnumerable;
        }

        // ---------- levers ----------

        /// <summary>Instance member edit (null target / member = a miss, added to missing).</summary>
        private static Edit Member(object target, string label, string name, List<string> missing)
        {
            if (target == null) { missing.Add(label); return null; }
            MemberInfo m;
            try { m = Data(target.GetType(), name, false); } catch { m = null; }
            if (m == null) { missing.Add(target.GetType().FullName + "." + name); return null; }
            return new Edit { Name = label + "." + name, Get = () => Read(m, target), Set = v => Write(m, target, v) };
        }

        private Edit Static(string type, string name, List<string> missing)
        {
            var t = T(type);
            MemberInfo m = t == null ? null : Data(t, name, true);
            if (m == null) { missing.Add(type + "." + name); return null; }
            return new Edit { Name = t.Name + "." + name, Get = () => Read(m, null), Set = v => Write(m, null, v) };
        }

        /// <summary>A member of ParticleSystem.main (MainModule is a struct proxy of the native system: setting on a boxed copy writes through).</summary>
        private static Edit MainModule(object ps, string name, int i) { return Module(ps, "main", name, i); }

        /// <summary>A member of a ParticleSystem module struct (main, emission): same write-through proxy rule as MainModule.</summary>
        private static Edit Module(object ps, string module, string name, int i)
        {
            if (ps == null) return null;
            MemberInfo mm = Data(ps.GetType(), module, false);
            if (mm == null) return null;
            object mod;
            try { mod = Read(mm, ps); } catch { return null; }
            var m = mod == null ? null : Data(mod.GetType(), name, false);
            if (m == null) return null;
            string label = (Get(ps, "name") as string ?? "ps") + "#" + i + "." + module + "." + name;
            return new Edit { Name = label, Target = ps, Get = () => Read(m, Read(mm, ps)), Set = v => Write(m, Read(mm, ps), v) };
        }

        private static Edit Active(object go, List<string> missing)
        {
            if (go == null) { missing.Add("GameObject Clouds"); return null; }
            var set = Method(go.GetType(), "SetActive", new[] { typeof(bool) }, false);
            if (set == null) { missing.Add("GameObject.SetActive"); return null; }
            return new Edit { Name = "Clouds.active", Target = go, Get = () => Get(go, "activeSelf"), Set = v => set.Invoke(go, new object[] { v }) };
        }

        /// <summary>The planned writes of a lever: (edit, new value from the current one).</summary>
        private List<KeyValuePair<Edit, Func<object, object>>> Plan(string lever, List<string> missing)
        {
            var p = new List<KeyValuePair<Edit, Func<object, object>>>();
            Action<Edit, Func<object, object>> add = (e, f) => { if (e != null) p.Add(new KeyValuePair<Edit, Func<object, object>>(e, f)); };
            Func<object, object> off = _ => false;
            var cam = MainCamera();
            switch (lever)
            {
                case "rcm": add(Member(Comp(cam, "RenderColorMask"), "RenderColorMask", "enabled", missing), off); break;
                case "ssao": add(Member(Comp(cam, "AmplifyOcclusionEffect"), "AmplifyOcclusionEffect", "enabled", missing), off); break;
                case "bloom_lite":
                {
                    var b = Comp(cam, "AmplifyBloom.AmplifyBloomEffect");
                    add(Member(b, "AmplifyBloomEffect", "ApplyLensGlare", missing), off);
                    add(Member(b, "AmplifyBloomEffect", "ApplyLensFlare", missing), off);
                    add(Member(b, "AmplifyBloomEffect", "BloomDownsampleCount", missing), v => Math.Min(Convert.ToInt32(v, CultureInfo.InvariantCulture), 3));
                    break;
                }
                case "cloud":
                    add(Active(CloudsParent(), missing), off);
                    add(Member(CloudShadow(), "ScreenSpaceCloudShadow", "enabled", missing), off);
                    break;
                case "refl_mask": add(Member(Reflection(), "PlanarReflection", "reflectionMask", missing), _ => 0); break;
                case "ovl":
                {
                    var o = Ocean();
                    add(Member(o, "Ocean", "normalOverlaySize", missing), _ => "QUARTER");
                    add(Member(o, "Ocean", "foamOverlaySize", missing), _ => "QUARTER");
                    break;
                }
                case "lod": add(Static("UnityEngine.QualitySettings", "lodBias", missing), v => Math.Min(Convert.ToSingle(v, CultureInfo.InvariantCulture), 0.5f)); break;   // never raises a lower bias
                case "plight": add(Static("UnityEngine.QualitySettings", "pixelLightCount", missing), _ => 0); break;
                case "softp": add(Static("UnityEngine.QualitySettings", "softParticles", missing), off); break;
                case "fps60":
                    add(Static("UnityEngine.Application", "targetFrameRate", missing), _ => 60);
                    add(Static("UnityEngine.QualitySettings", "vSyncCount", missing), _ => 0);
                    break;
                case "particles":   // L8: maxParticles x0.25 on the particle systems alive now (new ones spawned later are not touched)
                {
                    var pt = T("UnityEngine.ParticleSystem");
                    var found = pt == null ? null : (SCall("UnityEngine.Object", "FindObjectsOfType", new[] { typeof(Type) }, pt) as IEnumerable);
                    if (found == null) { missing.Add("UnityEngine.ParticleSystem / Object.FindObjectsOfType(Type)"); break; }
                    int n = 0;
                    foreach (var ps in found)
                    {
                        var e = MainModule(ps, "maxParticles", n++);
                        if (e == null) { missing.Add("ParticleSystem.main.maxParticles"); break; }
                        add(e, v => Math.Max(1, Convert.ToInt32(v, CultureInfo.InvariantCulture) / 4));
                    }
                    if (n == 0) missing.Add("no active ParticleSystem");
                    break;
                }
            }
            return p;
        }

        /// <summary>null = ok; otherwise "unsupported" (detail = missing members) | "failed" (every write threw).</summary>
        public string Set(string lever, bool on, out Dictionary<string, object> data, out string detail)
        {
            data = null; detail = null;
            var changes = new List<object>();
            var errors = new List<object>();
            if (!on)
            {
                List<Edit> eds;
                if (!_applied.TryGetValue(lever, out eds))
                {
                    data = new Dictionary<string, object> { { "lever", lever }, { "on", false }, { "changes", changes }, { "errors", errors }, { "note", "not applied" } };
                    return null;
                }
                Restore(lever, eds, changes, errors);
                data = new Dictionary<string, object> { { "lever", lever }, { "on", false }, { "changes", changes }, { "errors", errors } };
                return null;
            }
            if (_applied.ContainsKey(lever))
            {
                data = new Dictionary<string, object> { { "lever", lever }, { "on", true }, { "changes", changes }, { "errors", errors }, { "note", "already applied" } };
                return null;
            }
            var missing = new List<string>();
            var plan = Plan(lever, missing);
            if (plan.Count == 0) { detail = missing.Count > 0 ? string.Join(", ", missing.ToArray()) : lever; return "unsupported"; }
            var done = new List<Edit>();
            foreach (var kv in plan)
            {
                var e = kv.Key;
                try
                {
                    e.Was = e.Get();
                    e.Set(kv.Value(e.Was));
                    done.Add(e);
                    changes.Add(new Dictionary<string, object> { { "member", e.Name }, { "was", Out(e.Was) }, { "now", Out(e.Get()) } });
                }
                catch (Exception ex) { errors.Add(e.Name + ": " + Inner(ex)); }
            }
            if (done.Count == 0) { detail = string.Join("; ", errors.ConvertAll(x => (string)x).ToArray()); return "failed"; }
            _applied[lever] = done;
            _order.Add(lever);
            foreach (var m in missing) errors.Add("missing " + m);
            data = new Dictionary<string, object> { { "lever", lever }, { "on", true }, { "changes", changes }, { "errors", errors } };
            return null;
        }

        private void Restore(string lever, List<Edit> eds, List<object> changes, List<object> errors)
        {
            for (int i = eds.Count - 1; i >= 0; i--)
            {
                var e = eds[i];
                try
                {
                    e.Set(e.Was);
                    changes.Add(new Dictionary<string, object> { { "member", e.Name }, { "now", Out(e.Get()) } });
                }
                catch (Exception ex) { errors.Add(e.Name + ": " + Inner(ex)); }
            }
            _applied.Remove(lever);
            _order.Remove(lever);
        }

        /// <summary>Restores every applied lever, newest first.</summary>
        public Dictionary<string, object> Reset()
        {
            var changes = new List<object>();
            var errors = new List<object>();
            var levers = new List<object>();
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                string l = _order[i];
                levers.Add(l);
                Restore(l, _applied[l], changes, errors);
            }
            return new Dictionary<string, object> { { "restored", levers }, { "changes", changes }, { "errors", errors } };
        }

        private static string Inner(Exception e) { var x = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; return x.GetType().Name + ": " + x.Message; }

        /// <summary>The journal line of every call (tests rely on the prefix).</summary>
        public static string Line(Dictionary<string, object> a, string result)
        {
            object v;
            string op = a != null && a.TryGetValue("op", out v) && v is string ? (string)v : "-";
            string lever = a != null && a.TryGetValue("lever", out v) && v is string ? (string)v : "-";
            string on = a != null && a.TryGetValue("on", out v) && v is bool ? ((bool)v ? "true" : "false") : "-";
            return "[Dev][GFX] gfx op=" + op + " lever=" + lever + " on=" + on + " → " + result;
        }
    }
}
