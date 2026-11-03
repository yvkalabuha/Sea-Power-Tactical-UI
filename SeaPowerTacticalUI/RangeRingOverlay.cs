// T-137 E1 (ADR-0055): the range rings as an IRtOverlay consumer of the coverage layer (Noesis render texture). The geometry is RangeRingMath.Emit
// (pure, budgeted, clipped to the RT); this class only batches what it emits and draws the fill triangles in one GL.TRIANGLES block, then the
// outlines in one GL.LINES block, with the premultiplied material (like CapOverlay). The scene is built by RangeRingFeature (once per period); DueAt is "draw requested" and goes to +inf after the draw.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed class RangeRingOverlay : IRtOverlay, IRtLegend, IRtTexts, IRingPainter
    {
        private readonly Action<string> _log;
        private Material _matPm;
        private readonly List<float> _xy = new List<float>(4096);
        private readonly List<uint> _col = new List<uint>(1024);
        private readonly List<float> _tri = new List<float>(8192);
        private readonly List<uint> _triCol = new List<uint>(1024);
        private readonly Stopwatch _sw = new Stopwatch();
        private double _due = double.PositiveInfinity;
        private double _lastCutLog = double.NegativeInfinity;
        private long _draws;
        private double _msSum;

        /// <summary>The last built scene (null / empty = nothing to draw). Written by the module on the main thread.</summary>
        public RingScene Scene;
        /// <summary>The map layer entry: false = nothing is drawn and the layer does not keep itself alive for us.</summary>
        public bool Show;
        /// <summary>Opacity, MaxSegments, MinRadiusPx (settings write here; Emit reads on every draw).</summary>
        public readonly RingStyle Style = new RingStyle();

        // statistics of the last draw (diagnostics, ui_state section "rings")
        public RingDrawStats LastStats;
        public double MsAvg, MsMax;
        /// <summary>RT pixels per nautical mile at the first unit of the last draw (0 = not drawn yet): the labels skip rings below LabelMinPx.</summary>
        public double LastPxPerNm { get { return LastStats.PxPerNm; } }
        public long Draws { get { return _draws; } }

        public RangeRingOverlay(Action<string> log)
        {
            _log = log ?? (m => { });
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                _matPm = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _matPm.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);   // premultiplied alpha: the layer's texture is premultiplied
                _matPm.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _matPm.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _matPm.SetInt("_ZWrite", 0);
            }
        }

        public bool Ready { get { return _matPm != null; } }

        // ---- IRtOverlay -------------------------------------------------------------------------------------------------------------------
        public string Id { get { return "rings"; } }

        public bool HasContent()
        {
            var s = Scene;
            return Show && s != null && s.Units.Count > 0;
        }

        /// <summary>The centre of the first unit of the scene.</summary>
        public bool RefPoint(out double lat, out double lon)
        {
            lat = lon = 0;
            var s = Scene;
            if (!Show || s == null || s.Units.Count == 0) return false;
            lat = s.Units[0].Center.Lat; lon = s.Units[0].Center.Lon;
            return !double.IsNaN(lat) && !double.IsNaN(lon);
        }

        public double DueAt { get { return Show ? _due : double.PositiveInfinity; } }

        /// <summary>A new scene is ready: the layer draws it with the next frame.</summary>
        public void RequestDraw(double now) { _due = now; }

        // ---- IRtLegend (T-171): the lines the module prepares from the scene; the layer draws them above the radar legend while the overlay has content ----
        /// <summary>Title, one coloured line per kind present, the style line; null = none. Written by the module on the main thread.</summary>
        public IList<RtLegendLine> Legend;

        public IList<RtLegendLine> LegendLines() { return Show ? Legend : null; }

        // ---- IRtTexts (T-171): the range labels are drawn by the layer itself, independent of the contact_info label canvas ----
        /// <summary>Written by the module on the main thread; null = none.</summary>
        public IList<RtTextItem> Texts;

        public IList<RtTextItem> TextItems() { return Show ? Texts : null; }

        // ---- IRingPainter -----------------------------------------------------------------------------------------------------------------
        public void Line(float ax, float ay, float bx, float by, uint argbPremul)
        {
            _xy.Add(ax); _xy.Add(ay); _xy.Add(bx); _xy.Add(by); _col.Add(argbPremul);
        }

        public void Tri(float ax, float ay, float bx, float by, float cx, float cy, uint argbPremul)
        {
            _tri.Add(ax); _tri.Add(ay); _tri.Add(bx); _tri.Add(by); _tri.Add(cx); _tri.Add(cy); _triCol.Add(argbPremul);
        }

        private static Color Col(uint c) { return new Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, ((c >> 24) & 255) / 255f); }

        /// <summary>Draws into the active render texture. affRt = {a0..a5} geo -> RT pixels. Throws are caught by the layer.</summary>
        public void DrawGeo(double[] affRt, int rw, int rh)
        {
            _due = double.PositiveInfinity;
            var scene = Scene;
            if (!Show || scene == null || _matPm == null) return;
            _sw.Reset(); _sw.Start();
            DrawGuard.Stage = "rings/draw/emit";
            _xy.Clear(); _col.Clear(); _tri.Clear(); _triCol.Clear();
            RingDrawStats st = RangeRingMath.Emit(scene, affRt, rw, rh, Style, this);
            DrawGuard.Stage = "rings/draw/gl";
            if (_triCol.Count > 0)
            {
                // 2026-10-22: the semi-transparent fills of the weapon rings / wedges, under the outlines
                DrawGuard.Stage = "rings/draw/fill";
                _matPm.SetPass(0);
                GlSafe.Tris(() =>
                {
                    for (int i = 0; i < _triCol.Count; i++)
                    {
                        GL.Color(Col(_triCol[i]));
                        int k = i * 6;
                        GL.Vertex3(_tri[k], _tri[k + 1], 0); GL.Vertex3(_tri[k + 2], _tri[k + 3], 0); GL.Vertex3(_tri[k + 4], _tri[k + 5], 0);
                    }
                });
                DrawGuard.Stage = "rings/draw/gl";
            }
            if (_col.Count > 0)
            {
                _matPm.SetPass(0);
                GlSafe.Lines(() =>
                {
                    for (int i = 0; i < _col.Count; i++)
                    {
                        GL.Color(Col(_col[i]));
                        int k = i * 4;
                        GL.Vertex3(_xy[k], _xy[k + 1], 0); GL.Vertex3(_xy[k + 2], _xy[k + 3], 0);
                    }
                });
            }
            _sw.Stop();
            DrawGuard.Stage = "";
            double ms = _sw.Elapsed.TotalMilliseconds;
            _draws++; _msSum += ms; MsAvg = _msSum / _draws; if (ms > MsMax) MsMax = ms;
            LastStats = st;
            if ((scene.RingsCut > 0 || st.SegmentsCut > 0 || st.TrisCut > 0) && Time.unscaledTime - _lastCutLog >= 60.0)
            {
                _lastCutLog = Time.unscaledTime;
                _log("rings: budget cut rings=" + scene.RingsCut + " segs=" + st.Segments + " cutsegs=" + st.SegmentsCut + " tris=" + st.Tris + " cuttris=" + st.TrisCut);
            }
        }

        public void Dispose()
        {
            try { if (_matPm != null) UnityEngine.Object.Destroy(_matPm); } catch (Exception) { }
            _matPm = null;
        }
    }
}
