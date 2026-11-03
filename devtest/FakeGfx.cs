using System;
using System.Collections.Generic;

// gfx (T-165): the smallest fake of the Unity / Ceto / Amplify / game members DevGfx reaches by name through AppDomain type lookup.
// Same full names and member kinds (property vs field, static vs instance) as Unity 6000.0 and Sea Power 0.8.7.
#pragma warning disable 0169, 0414, 0649
namespace UnityEngine
{
    internal enum FindObjectsSortMode { None, InstanceID }
    internal class Object
    {
        private static int _ids;
        private readonly int _id = ++_ids;
        public bool Destroyed;   // fake of a destroyed native object: Equals(null) is true, module writes throw
        public string name { get; set; }
        public int GetInstanceID() { return _id; }
        public override bool Equals(object o) { return o == null ? Destroyed : ReferenceEquals(this, o); }
        public override int GetHashCode() { return _id; }
        public static Object[] FindObjectsByType(Type t, FindObjectsSortMode m) { return FindObjectsOfType(t); }
        public static Object FindFirstObjectByType(Type t) { foreach (var go in GameObject.Scene) foreach (var c in go.Comps) if (t.IsInstanceOfType(c) && go.activeSelf) return c; return null; }
        public static Object[] FindObjectsOfType(Type t) { var l = new List<Object>(); foreach (var go in GameObject.Scene) foreach (var c in go.Comps) if (t.IsInstanceOfType(c) && go.activeSelf) l.Add(c); return l.ToArray(); }
    }
    internal sealed class ParticleSystem : Component
    {
        public int Max = 1000;
        public float Life = 1f, RateT = 10f, RateD = 4f;
        private void Alive() { if (Destroyed) throw new NullReferenceException("destroyed"); }
        public struct MainModule
        {
            private readonly ParticleSystem m_ParticleSystem; public MainModule(ParticleSystem ps) { m_ParticleSystem = ps; }
            public int maxParticles { get { return m_ParticleSystem.Max; } set { m_ParticleSystem.Alive(); m_ParticleSystem.Max = value; } }
            public float startLifetimeMultiplier { get { return m_ParticleSystem.Life; } set { m_ParticleSystem.Alive(); m_ParticleSystem.Life = value; } }
        }
        public struct EmissionModule
        {
            private readonly ParticleSystem m_ParticleSystem; public EmissionModule(ParticleSystem ps) { m_ParticleSystem = ps; }
            public float rateOverTimeMultiplier { get { return m_ParticleSystem.RateT; } set { m_ParticleSystem.Alive(); m_ParticleSystem.RateT = value; } }
            public float rateOverDistanceMultiplier { get { return m_ParticleSystem.RateD; } set { m_ParticleSystem.Alive(); m_ParticleSystem.RateD = value; } }
        }
        public MainModule main => new MainModule(this);
        public EmissionModule emission => new EmissionModule(this);
    }
    internal sealed class GameObject : Object
    {
        public static readonly List<GameObject> Scene = new List<GameObject>();
        public readonly List<Component> Comps = new List<Component>();
        public bool activeSelf { get; private set; } = true;
        public void SetActive(bool v) { activeSelf = v; }
        public static GameObject Find(string n) { foreach (var g in Scene) if (g.activeSelf && g.name == n) return g; return null; }
        public static GameObject Make(string n) { var g = new GameObject { name = n }; Scene.Add(g); return g; }
        public T Add<T>() where T : Component, new() { var c = new T { name = name }; c.gameObject = this; Comps.Add(c); return c; }
    }
    internal class Component : Object
    {
        public GameObject gameObject { get; set; }
        public Component GetComponent(Type t) { foreach (var c in gameObject.Comps) if (t.IsInstanceOfType(c)) return c; return null; }
        public Component GetComponent(string t) { return null; }
        public Component[] GetComponents(Type t) { return gameObject.Comps.FindAll(c => t.IsInstanceOfType(c)).ToArray(); }
    }
    internal class Behaviour : Component { public bool enabled { get; set; } = true; }
    internal class MonoBehaviour : Behaviour { }
    internal sealed class RenderTexture : Object { }
    internal struct LayerMask { private int m_Mask; public int value { get { return m_Mask; } set { m_Mask = value; } } }
    internal enum AnisotropicFiltering { Disable, Enable, ForceEnable }
    internal enum ShadowQuality { Disable, HardOnly, All }
    internal sealed class Camera : Behaviour
    {
        public float depth { get; set; }
        public RenderTexture targetTexture { get; set; }
        public int cullingMask { get; set; } = -1;
        public static Camera main { get; set; }
        public static Camera[] allCameras { get { var l = new List<Camera>(); foreach (var g in GameObject.Scene) foreach (var c in g.Comps) if (c is Camera && ((Camera)c).enabled && g.activeSelf) l.Add((Camera)c); return l.ToArray(); } }
    }
    internal static class QualitySettings
    {
        public static float lodBias { get; set; } = 1f;
        public static int pixelLightCount { get; set; } = 2;
        public static bool softParticles { get; set; } = true;
        public static AnisotropicFiltering anisotropicFiltering { get; set; } = AnisotropicFiltering.Enable;
        public static ShadowQuality shadows { get; set; }
        public static int antiAliasing { get; set; }
        public static int vSyncCount { get; set; } = 1;
    }
    internal static class Application { public static int targetFrameRate { get; set; } = 30; }
}

namespace Ceto
{
    internal enum OVERLAY_MAP_SIZE { DOUBLE, FULL_HALF, FULL, HALF, QUARTER }
    internal enum REFLECTION_RESOLUTION { FULL, HALF, QUARTER }
    internal enum UNDERWATER_MODE { ABOVE_ONLY, ABOVE_AND_BELOW }
    internal sealed class Ocean : UnityEngine.MonoBehaviour
    {
        public OVERLAY_MAP_SIZE heightOverlaySize = OVERLAY_MAP_SIZE.HALF;
        public OVERLAY_MAP_SIZE normalOverlaySize = OVERLAY_MAP_SIZE.FULL;
        public OVERLAY_MAP_SIZE foamOverlaySize = OVERLAY_MAP_SIZE.FULL;
        public OVERLAY_MAP_SIZE clipOverlaySize = OVERLAY_MAP_SIZE.HALF;
        public static Ocean Instance { get; set; }
        public PlanarReflection Reflection { get; set; }
        public UnderWater UnderWater { get; set; }
    }
    internal sealed class PlanarReflection : UnityEngine.MonoBehaviour
    {
        public UnityEngine.LayerMask reflectionMask;
        public REFLECTION_RESOLUTION reflectionResolution = REFLECTION_RESOLUTION.QUARTER;
        public int blurIterations = 1;
    }
    internal sealed class UnderWater : UnityEngine.MonoBehaviour { public UNDERWATER_MODE underwaterMode = UNDERWATER_MODE.ABOVE_AND_BELOW; }
}

namespace AmplifyBloom
{
    internal class AmplifyBloomBase : UnityEngine.MonoBehaviour
    {
        private int m_bloomDownsampleCount = 6;
        public bool ApplyLensGlare { get; set; } = true;
        public bool ApplyLensFlare { get; set; } = true;
        public int BloomDownsampleCount { get { return m_bloomDownsampleCount; } set { m_bloomDownsampleCount = Math.Clamp(value, 1, 6); } }
    }
    internal sealed class AmplifyBloomEffect : AmplifyBloomBase { }
}

namespace SeaPower
{
    internal class Singleton<T> : UnityEngine.MonoBehaviour where T : class
    {
        public static T Instance { get; set; }
        public static bool InstanceExists(bool log = false) { return Instance != null; }
    }
    internal sealed class CameraManager : Singleton<CameraManager>
    {
        public bool _renderingPaused;
        public bool IsRenderingPaused => _renderingPaused;
        public static UnityEngine.Camera Main => InstanceExists() ? UnityEngine.Camera.main : null;
    }
    internal sealed class ScreenSpaceCloudShadow : UnityEngine.MonoBehaviour { }
}

internal sealed class RenderColorMask : UnityEngine.MonoBehaviour { }
internal class AmplifyOcclusionBase : UnityEngine.MonoBehaviour { }
internal sealed class AmplifyOcclusionEffect : AmplifyOcclusionBase { }
#pragma warning restore 0169, 0414, 0649
