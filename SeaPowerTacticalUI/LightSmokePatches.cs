namespace VoiceBotBridge
{
    /// <summary>
    /// T-165 / ADR-0062: bodies of the light smoke patches (targets: PerfSpecs, installed by HarmonyFeature). Three spawn points:
    /// EffectsManager.spawnEffect (postfix on the result), ParticleUtils.SetAutoDestroyAndCustomWorldSpace with autodestroy=true
    /// (pool spawns of launcher smoke, dust, barrel smoke) and ChaffCloud.instantiateChaffRound (prefix remembers the length of _clouds, postfix takes the new items).
    /// Option off = one field read. Bodies never throw into the game: after MaxErrors the service switches itself off.
    /// </summary>
    internal static class LightSmokePatches
    {
        internal static volatile Ui.LightSmokeService Service;   // PerfFeature.Init / Dispose
        private static System.Reflection.FieldInfo _cloudsField;
        private static bool _cloudsResolved;

        // Postfix of EffectsManager.spawnEffect: never throws into the game.
        internal static void SpawnPostfix(UnityEngine.GameObject __result)
        {
            var s = Service;
            if (s == null || !s.On || __result == null) return;   // option off: one field read
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { s.OnSpawn(Ui.LsSource.Spawn, null, Ui.UnityParticleAccess.Systems(__result), Ui.LsProfile.Full); }
            catch (System.Exception e) { try { s.Fail(e); } catch { } }
            finally { Cost(s, t0); }
        }

        // Postfix of ParticleUtils.SetAutoDestroyAndCustomWorldSpace(GameObject, bool, List<TrailRenderer>): only the autodestroy=true calls are pool spawns.
        internal static void DirectPostfix(UnityEngine.GameObject __0, bool __1)
        {
            var s = Service;
            if (s == null || !s.On || !__1 || __0 == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { s.OnSpawn(Ui.LsSource.Direct, null, Ui.UnityParticleAccess.Systems(__0), Ui.LsProfile.Full); }
            catch (System.Exception e) { try { s.Fail(e); } catch { } }
            finally { Cost(s, t0); }
        }

        // Prefix of ChaffCloud.instantiateChaffRound: how many clouds there are before the round.
        internal static void ChaffPrefix(object __instance, out int __state)
        {
            __state = -1;
            var s = Service;
            if (s == null || !s.On || __instance == null) return;
            try
            {
                var list = Clouds(__instance);
                if (list != null) __state = list.Count;
            }
            catch (System.Exception e) { try { s.Fail(e); } catch { } }
        }

        // Postfix: the clouds added by the round (the lifetime stays: a cloud of chaff lives as long as the game says).
        internal static void ChaffPostfix(object __instance, int __state)
        {
            var s = Service;
            if (s == null || !s.On || __state < 0 || __instance == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var list = Clouds(__instance);
                if (list == null) return;
                for (int i = __state; i < list.Count; i++)
                {
                    var go = list[i] as UnityEngine.GameObject;
                    if (go != null) s.OnSpawn(Ui.LsSource.Chaff, null, Ui.UnityParticleAccess.Systems(go), Ui.LsProfile.KeepLifetime);
                }
            }
            catch (System.Exception e) { try { s.Fail(e); } catch { } }
            finally { Cost(s, t0); }
        }

        private static System.Collections.IList Clouds(object chaff)
        {
            if (!_cloudsResolved)
            {
                _cloudsResolved = true;
                var api = Patches.Api;
                if (api != null) _cloudsField = api.Member(PerfSpecs.ChaffClouds) as System.Reflection.FieldInfo;
            }
            return _cloudsField == null ? null : _cloudsField.GetValue(chaff) as System.Collections.IList;
        }

        private static void Cost(Ui.LightSmokeService s, long t0)
        {
            try { s.AddCost((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency); } catch { }
        }
    }
}
