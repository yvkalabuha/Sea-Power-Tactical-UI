using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-094: the game part of the buoy revive (decision in BuoyReviveMath).</summary>
    internal sealed partial class GameApi
    {
        /// <summary>True while the Harmony postfix on SceneCreator.LaunchWeapons is installed (set by HarmonyFeature).</summary>
        internal bool BuoyReviveActive { get; set; }

        /// <summary>Postfix of SceneCreator.LaunchWeapons: launches the sonobuoys without a launch platform. Returns the number revived; never throws.</summary>
        internal int OnWeaponsLaunched(object sceneCreator)
        {
            if (!BuoyReviveActive || sceneCreator == null) return 0;
            int launched = 0, total = 0, failed = 0;
            string firstError = null;
            var skipped = new Dictionary<BuoyReviveVerdict, int>();
            try
            {
                if (!Present(BuoyReviveSpecs.WeaponsToLaunch) || !Present(BuoyReviveSpecs.ContainerLaunch) || !Present(BuoyReviveSpecs.SkipReport)) return 0;
                var dict = Get(sceneCreator, BuoyReviveSpecs.WeaponsToLaunch) as IDictionary;
                if (dict == null) return 0;
                bool isSave = GetStatic(BuoyReviveSpecs.IsASaveFile) is bool && (bool)GetStatic(BuoyReviveSpecs.IsASaveFile);
                var keys = new List<object>();
                foreach (object k in dict.Keys) keys.Add(k);   // Container_Launch must not see the dictionary change under the loop
                foreach (object w in keys)
                {
                    bool skipSet = false;
                    try
                    {
                        if (w == null) continue;
                        var facts = new BuoyFacts
                        {
                            IsSave = isSave,
                            IsBomb = w.GetType().Name == "Bomb",
                            HasPlatform = Get(w, BuoyReviveSpecs.LaunchPlatform) != null,
                            Destroyed = Get(w, BuoyReviveSpecs.IsDestroyed) is bool && (bool)Get(w, BuoyReviveSpecs.IsDestroyed),
                            HasTaskforce = Get(w, BuoyReviveSpecs.Taskforce) != null,
                        };
                        var ap = Get(w, BuoyReviveSpecs.Ap);
                        facts.IsSonobuoy = ap != null && Convert.ToString(Get(ap, AswSpecs.ApSubType)) == "Sonobuoy";
                        if (facts.IsSonobuoy && facts.IsBomb) total++;
                        facts.Launched = Convert.ToBoolean(Call(w, BuoyReviveSpecs.IsLaunched));   // isLaunched() is a method
                        var v = BuoyReviveMath.Decide(facts);
                        if (v != BuoyReviveVerdict.Revive)
                        {
                            int n; skipped.TryGetValue(v, out n); skipped[v] = n + 1;
                            continue;
                        }
                        Set(w, BuoyReviveSpecs.SkipReport, true); skipSet = true;
                        var pos = ((UnityEngine.Component)w).transform.position;
                        Call(w, BuoyReviveSpecs.ContainerLaunch, null, pos, UnityEngine.Vector3.zero, null);
                        launched++;
                    }
                    catch (Exception e)
                    {
                        if (skipSet) { try { Set(w, BuoyReviveSpecs.SkipReport, false); } catch { } }   // a failed launch must not keep the report muted
                        failed++;
                        if (firstError == null) firstError = e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message);
                    }
                }
                var line = BuoyReviveMath.Summary(launched, total, skipped, failed);
                if (line != null) Log(line);
                if (firstError != null) Log("buoy revive: first error " + firstError);
            }
            catch (Exception e) { try { Log("buoy revive failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
            return launched;
        }

        /// <summary>T-296: null = the buoy has a launch platform (or the member is not there): the game's own SonobouyStatus runs.
        /// Otherwise the status text without ${ParentName}; never throws (on any error: the fallback "").</summary>
        internal string SonobuoyStatusNoPlatform(object bomb)
        {
            if (bomb == null || !Present(BuoyReviveSpecs.LaunchPlatform)) return null;
            if (!ReferenceEquals(Get(bomb, BuoyReviveSpecs.LaunchPlatform), null)) return null;   // reference null only: a Unity-destroyed carrier goes to the game
            try
            {
                var lh = Singleton(CoreSpecs.LanguageHandler);
                var look = lh == null ? null : GameTextLookup(lh);
                if (look == null) return "";
                string key = "Status_SonobuoyActive";
                var ap = Get(bomb, BuoyReviveSpecs.Ap);
                var sensors = Get(bomb, BuoyReviveSpecs.Sensors) as IList;
                if (sensors != null && sensors.Count > 0 && sensors[0] != null && Present(BuoyReviveSpecs.InTransmitterRange)
                    && !Convert.ToBoolean(Call(sensors[0], BuoyReviveSpecs.InTransmitterRange))) key = "Status_SonobuoyOutOfRange";
                string template = look("BottomRow", key);
                bool noLayer = Get(bomb, BuoyReviveSpecs.NoLayer) is bool && (bool)Get(bomb, BuoyReviveSpecs.NoLayer);
                string stateKey = noLayer ? "DefaultDepth" : Convert.ToString(RxGet(bomb, AswSpecs.SonoDeploy));
                string deployStatus = look("ContextMenu", stateKey);
                var mount = Get(bomb, BuoyReviveSpecs.MountObject) as UnityEngine.GameObject;
                string depth = BuoyReviveMath.DepthText(mount != null, mount != null ? mount.transform.position.y : 0f);
                float life = 0f, range = 0f;
                var rl = RxGet(bomb, AswSpecs.BombRemainingLife);
                if (rl != null) life = Convert.ToSingle(rl);
                if (ap != null && Get(ap, BuoyReviveSpecs.TransmitterRange) != null) range = Convert.ToSingle(Get(ap, BuoyReviveSpecs.TransmitterRange));
                return BuoyReviveMath.FormatStatus(template, life, range, deployStatus, depth);
            }
            catch (Exception e)
            {
                if (!_statusFallbackLogged) { _statusFallbackLogged = true; try { Log("buoy status fallback: " + e.GetType().Name + ": " + e.Message); } catch { } }
                return "";
            }
        }

        private bool _statusFallbackLogged;
    }
}
