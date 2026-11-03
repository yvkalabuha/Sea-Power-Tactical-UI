using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Serialization;

namespace VoiceBotBridge
{
    /// <summary>
    /// Radar coverage switch in the game's own map layers menu (0.10.3). The menu lists GizmoLayerManager.Instance.Layers
    /// (ObservableCollection&lt;MapLayer&gt;); we add one MapLayer there and keep its Visible in sync with the overlay.
    /// The layer is created by the game's own constructor MapLayer(number, name, visibleInMenu); the label comes from the [MapLayers]
    /// language table (our ini files). If the constructor fails, an uninitialised instance with the backing fields set is the fallback.
    /// A language change (UiText.Revision) re-sets the labels. Pure reflection, main thread only.
    /// </summary>
    internal sealed partial class GameApi
    {
        internal const string CoverageLayerName = "VOICEBOT_RADAR_COVERAGE";
        internal const int CoverageLayerNumber = 200;
        private object _covLayer;
        private KeyValuePair<object, string>[] _covLayerLabels;   // T-125: made once with _covLayer (was a new array every frame)
        private bool _covLayerFailed;
        private bool _covLayerSync;
        private int _layerRev = -1;

        /// <summary>The layer for the game's list: normal constructor first; on any failure an uninitialised object with the fields set.</summary>
        private object NewMapLayer(int number, string name, bool visible)
        {
            object layer;
            try { layer = New(MapLayerSpecs.LayerCtor, number, name, true); }
            catch (Exception e)
            {
                Log("layer ctor fallback: " + name + ": " + (e.InnerException ?? e).GetType().Name);
                layer = FormatterServices.GetUninitializedObject(TypeOf(MapLayerSpecs.LayerType));
                SetLayerField(layer, MapLayerSpecs.LayerNumberField, number);
                SetLayerField(layer, MapLayerSpecs.NameField, name);
                SetLayerField(layer, MapLayerSpecs.VisibleInMenuField, true);
            }
            SetLayerLabel(layer, name);
            SetLayerField(layer, MapLayerSpecs.VisibleField, visible);
            return layer;
        }

        /// <summary>Sets a field of the layer class; a missing field is an error (the layer would be unusable).</summary>
        private void SetLayerField(object layer, MemberSpec field, object value)
        {
            if (!Set(layer, field, value)) throw new MissingFieldException(field.Type, field.Member);
        }

        /// <summary>Sets (and announces) the localised label. The ctor already looked it up, but an unknown key there is an error in the game log, so we set our own text.</summary>
        private void SetLayerLabel(object layer, string name)
        {
            if (layer == null) return;
            string text = LayerText(name);
            if (Set(layer, MapLayerSpecs.DisplayName, text)) return;
            SetLayerField(layer, MapLayerSpecs.DisplayNameField, text);
            if (Present(MapLayerSpecs.NotifyChanged)) { try { Call(layer, MapLayerSpecs.NotifyChanged, MapLayerSpecs.DisplayName.Member); } catch { } }
        }

        /// <summary>Plugin.Update (cheap: one int compare): after a language change the labels of our layers are set again.</summary>
        internal void RefreshLayerLabels(IEnumerable<KeyValuePair<object, string>> layers)
        {
            if (_layerRev == UiText.Revision) return;
            _layerRev = UiText.Revision;
            foreach (var kv in layers) { try { SetLayerLabel(kv.Key, kv.Value); } catch { } }
        }

        /// <summary>Plugin.Update: adds the layer once the game's layer manager can be built. Cheap after the first success.</summary>
        internal void PumpMapLayer()
        {
            if (_covLayer != null) RefreshLayerLabels(_covLayerLabels);
            if (_covLayer != null || _covLayerFailed || !Capabilities.Contains("radar_layer") || !Capabilities.Contains("radar_overlay")) return;
            // the manager singleton reads options and language tables: wait for both
            if (SingletonInstance(MapLayerSpecs.OptionsManager) == null || SingletonInstance(CoreSpecs.LanguageHandler) == null) return;
            try
            {
                var mgr = GetStatic(MapLayerSpecs.ManagerInstance);
                var layers = Get(mgr, MapLayerSpecs.Layers) as IList;
                var byName = Get(mgr, MapLayerSpecs.LayersByName) as IDictionary;
                var byId = Get(mgr, MapLayerSpecs.LayersById) as IDictionary;
                if (layers == null || byName == null || byId == null) return;
                if (byName.Contains(CoverageLayerName)) { _covLayer = byName[CoverageLayerName]; _covLayerLabels = new[] { new KeyValuePair<object, string>(_covLayer, CoverageLayerName) }; }
                else
                {
                    if (byId.Contains(CoverageLayerNumber)) { _covLayerFailed = true; Log("map layer: number " + CoverageLayerNumber + " is taken by the game"); return; }
                    var layer = NewMapLayer(CoverageLayerNumber, CoverageLayerName, _overlayOn);
                    byName[CoverageLayerName] = layer;
                    byId[CoverageLayerNumber] = layer;
                    InsertOurLayer(layers, layer, CoverageLayerNumber);
                    _covLayer = layer;
                    _covLayerLabels = new[] { new KeyValuePair<object, string>(_covLayer, CoverageLayerName) };
                }
                ((INotifyPropertyChanged)_covLayer).PropertyChanged += OnCoverageLayerChanged;
                SyncCoverageLayer();
                Log("map layer added: " + CoverageLayerName);
            }
            catch (Exception e)
            {
                _covLayerFailed = true;
                Log("map layer failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>T-154 D1: our layer goes right after the game's layers (Radar first among ours), not at the end behind other mods.</summary>
        private void InsertOurLayer(IList layers, object layer, int number)
        {
            var nums = new List<int>();
            foreach (var l in layers)
            {
                var n = D(Get(l, MapLayerSpecs.LayerNumber));
                nums.Add(n.HasValue ? (int)n.Value : int.MaxValue);
            }
            int idx = MapLayerMath.InsertIndex(nums, number);
            if (idx > layers.Count) idx = layers.Count;
            layers.Insert(idx, layer);
            Log("map layers: count=" + layers.Count + " ours@" + idx + " number=" + number);
        }

        private void OnCoverageLayerChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_covLayerSync || e.PropertyName != "Visible") return;
            try { SetOverlay(BoolOf(Get(sender, MapLayerSpecs.Visible)), "layers"); } catch { }
        }

        /// <summary>Overlay switched elsewhere (hotkey, bot, config): mirror it into the layer's check mark.</summary>
        private void SyncCoverageLayer()
        {
            if (_covLayer == null) return;
            try
            {
                if (BoolOf(Get(_covLayer, MapLayerSpecs.Visible)) == _overlayOn) return;
                _covLayerSync = true;
                Set(_covLayer, MapLayerSpecs.Visible, _overlayOn);
            }
            catch { }
            finally { _covLayerSync = false; }
        }

        // ---------------------------------------------------------------- T-104 fallback: window switches as map layers

        /// <summary>Localized layer label: the [MapLayers] section of the language tables (game text first, then our ini files).</summary>
        private static string LayerText(string name) { return UiText.Get("MapLayers", name); }

        /// <summary>
        /// Adds (once) a layer in the game's "Map layers" submenu whose check mark follows a mod window (Visible = window open) and back.
        /// Used when the Noesis menu insertion next to World View is unavailable. Returns true when the layer exists and is synchronised.
        /// Main thread only, call every frame: cheap after the first success.
        /// </summary>
        internal bool PumpWindowLayer(WindowLayerSpec spec)
        {
            if (spec.Failed) return false;
            if (spec.Layer == null)
            {
                if (!Capabilities.Contains("radar_layer")) return false;
                if (SingletonInstance(MapLayerSpecs.OptionsManager) == null || SingletonInstance(CoreSpecs.LanguageHandler) == null) return false;
                try
                {
                    var mgr = GetStatic(MapLayerSpecs.ManagerInstance);
                    var layers = Get(mgr, MapLayerSpecs.Layers) as IList;
                    var byName = Get(mgr, MapLayerSpecs.LayersByName) as IDictionary;
                    var byId = Get(mgr, MapLayerSpecs.LayersById) as IDictionary;
                    if (layers == null || byName == null || byId == null) return false;
                    if (byName.Contains(spec.Name)) spec.Layer = byName[spec.Name];
                    else
                    {
                        if (byId.Contains(spec.Number)) { spec.Failed = true; Log("map layer: number " + spec.Number + " is taken by the game"); return false; }
                        var layer = NewMapLayer(spec.Number, spec.Name, spec.IsOpen());
                        byName[spec.Name] = layer;
                        byId[spec.Number] = layer;
                        InsertOurLayer(layers, layer, spec.Number);
                        spec.Layer = layer;
                    }
                    var captured = spec;
                    ((INotifyPropertyChanged)spec.Layer).PropertyChanged += (s, e) =>
                    {
                        if (captured.Syncing || e.PropertyName != "Visible") return;
                        try { captured.SetOpen(BoolOf(Get(s, MapLayerSpecs.Visible))); } catch { }
                    };
                    Log("map layer added: " + spec.Name + " (window switch)");
                }
                catch (Exception e)
                {
                    spec.Failed = true;
                    Log("map layer failed: " + spec.Name + ": " + e.GetType().Name + ": " + e.Message);
                    return false;
                }
            }
            if (spec.LabelRev != UiText.Revision) { spec.LabelRev = UiText.Revision; try { SetLayerLabel(spec.Layer, spec.Name); } catch { } }
            try
            {
                bool open = spec.IsOpen();
                if (BoolOf(Get(spec.Layer, MapLayerSpecs.Visible)) != open)
                {
                    spec.Syncing = true;
                    Set(spec.Layer, MapLayerSpecs.Visible, open);
                }
            }
            catch { }
            finally { spec.Syncing = false; }
            return true;
        }
    }
}

namespace VoiceBotBridge
{
    /// <summary>A mod window shown as a map layer switch (T-104 fallback path).</summary>
    internal sealed class WindowLayerSpec
    {
        public string Name;
        public int Number;
        public Func<bool> IsOpen;
        public Action<bool> SetOpen;
        internal object Layer;
        internal bool Syncing, Failed;
        internal int LabelRev = -1;
    }
}
