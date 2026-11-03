using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// Legend "Radar coverage" drawn with GL into the coverage render texture (T-090, customer answer 2026-10-19: the legend goes under the
    /// game's windows too). Text = glyph quads of Unity's dynamic font (Cyrillic works); backgrounds and swatches = solid quads.
    /// Output is premultiplied alpha like the zone. No font -> not Ready, one warning, no legend (T-090 Q3: never the IMGUI legend, it would cover the menus).
    /// </summary>
    internal sealed class CoverageLegendRt
    {
        private readonly Color[] _tier;
        private readonly Color _grey;
        private readonly Action<string> _log;
        private Font _font;
        private Material _solid;

        public CoverageLegendRt(Color[] tierColors, Color grey, Action<string> log)
        {
            _tier = tierColors ?? new Color[0];
            _grey = grey;
            _log = log ?? (m => { });
            try
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { _font = null; }
                if (_font == null) { try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { _font = null; } }
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh != null)
                {
                    _solid = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    _solid.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    _solid.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _solid.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                    _solid.SetInt("_ZWrite", 0);
                    _solid.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                }
            }
            catch (Exception e) { _font = null; _log("radar legend: init failed (" + e.GetType().Name + ": " + e.Message + ")"); }
            if (!Ready) _log("radar legend: no usable built-in font (LegacyRuntime.ttf), the legend is not drawn in noesis mode");
        }

        /// <summary>Font and solid material found.</summary>
        public bool Ready { get { return _font != null && _solid != null; } }

        private static FontStyle StyleOf(LegendLine l) { return l.Bold ? FontStyle.Bold : FontStyle.Normal; }

        /// <summary>Width of the widest line in screen pixels at the given font size (swatch included).</summary>
        public double MeasureWidth(IList<LegendLine> lines, int fontPx)
        {
            if (!Ready || lines == null) return 0;
            double best = 0;
            try
            {
                foreach (var l in lines)
                {
                    var st = StyleOf(l);
                    _font.RequestCharactersInTexture(l.Text, fontPx, st);
                    double w = l.Swatch >= 0 ? fontPx * 0.8 + 4 : 0;
                    foreach (char ch in l.Text)
                    {
                        CharacterInfo ci;
                        if (_font.GetCharacterInfo(ch, out ci, fontPx, st)) w += ci.advance;
                    }
                    if (w > best) best = w;
                }
            }
            catch (Exception e) { _log("radar legend: measure failed (" + e.Message + ")"); return 0; }
            return best;
        }

        private static Color ArgbColor(uint c)
        {
            return new Color(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, ((c >> 24) & 255) / 255f);
        }

        private static void Quad(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0); GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0);
        }

        /// <summary>Called inside the active render texture with the pixel matrix loaded. The picture is mirrored as a whole when flipY is on,
        /// so the glyphs are always drawn upright in local coordinates (y down).</summary>
        public void Draw(IList<LegendLine> lines, LegendBox box, bool flipY)
        {
            if (!Ready || lines == null || !box.Visible) return;
            int size = Math.Max(6, (int)Math.Round(box.FontPx));
            float k = (float)(box.Pad / 6.0), topPad = 4f * k, lh = (float)box.LineH;
            float sw = (float)box.FontPx * 0.8f;
            foreach (var l in lines) _font.RequestCharactersInTexture(l.Text, size, StyleOf(l));

            _solid.SetPass(0);
            GlSafe.Quads(() =>
            {
            GL.Color(new Color(0, 0, 0, 0.55f));
            Quad((float)box.X, (float)box.Y, (float)(box.X + box.W), (float)(box.Y + box.H));
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Swatch < 0) continue;
                float tx = (float)(box.X + box.Pad), ly = (float)box.Y + topPad + i * lh, sy = ly + (lh - sw) * 0.5f;
                if (l.Swatch == 4 || l.Swatch == 5 || (l.Argb != 0 && l.Dashed))
                {
                    // Swatch 5 (T-182): the outline without jamming, tier-0 colour dimmed
                    GL.Color(l.Argb != 0 ? ArgbColor(l.Argb) : l.Swatch == 5 && _tier.Length > 0 ? new Color(_tier[0].r, _tier[0].g, _tier[0].b, 0.6f) : _grey);
                    float t = Math.Max(1f, 2f * k);
                    Quad(tx, sy + sw * 0.5f - t * 0.5f, tx + sw * 0.4f, sy + sw * 0.5f + t * 0.5f);
                    Quad(tx + sw * 0.6f, sy + sw * 0.5f - t * 0.5f, tx + sw, sy + sw * 0.5f + t * 0.5f);
                }
                else if (l.Argb != 0)
                {
                    GL.Color(ArgbColor(l.Argb));
                    Quad(tx, sy, tx + sw, sy + sw);
                }
                else
                {
                    GL.Color(_tier[Math.Min(l.Swatch, _tier.Length - 1 < 0 ? 0 : _tier.Length - 1)]);
                    Quad(tx, sy, tx + sw, sy + sw);
                }
            }
            });

            _font.material.SetPass(0);
            GlSafe.Quads(() =>
            {
            GL.Color(Color.white);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var st = StyleOf(l);
                float x = (float)(box.X + box.Pad) + (l.Swatch >= 0 ? sw + 4f * k : 0f);
                float baseline = (float)box.Y + topPad + i * lh + lh * 0.5f + size * 0.35f;
                foreach (char ch in l.Text)
                {
                    CharacterInfo ci;
                    if (!_font.GetCharacterInfo(ch, out ci, size, st)) continue;
                    float x0 = x + ci.minX, x1 = x + ci.maxX, yTop = baseline - ci.maxY, yBot = baseline - ci.minY;
                    GL.TexCoord2(ci.uvTopLeft.x, ci.uvTopLeft.y); GL.Vertex3(x0, yTop, 0);
                    GL.TexCoord2(ci.uvTopRight.x, ci.uvTopRight.y); GL.Vertex3(x1, yTop, 0);
                    GL.TexCoord2(ci.uvBottomRight.x, ci.uvBottomRight.y); GL.Vertex3(x1, yBot, 0);
                    GL.TexCoord2(ci.uvBottomLeft.x, ci.uvBottomLeft.y); GL.Vertex3(x0, yBot, 0);
                    x += ci.advance;
                }
            }
            });
        }

        /// <summary>T-171: texts at RT pixels (x, y = the label's left edge and vertical centre), each on a dark plate. Same conventions as the legend: inside the active
        /// render texture with the pixel matrix loaded, glyphs upright in local coordinates.</summary>
        public void DrawTexts(IList<float[]> pos, IList<string> texts, IList<uint> colors, int fontPx)
        {
            if (!Ready || pos == null || texts == null || pos.Count == 0) return;
            int size = Math.Max(6, fontPx);
            float pad = Math.Max(2f, size * 0.25f), lh = size * 1.25f;
            var widths = new float[texts.Count];
            _font.RequestCharactersInTexture(string.Join("", new List<string>(texts).ToArray()), size, FontStyle.Normal);
            for (int i = 0; i < texts.Count; i++)
            {
                float w = 0;
                foreach (char ch in texts[i]) { CharacterInfo ci; if (_font.GetCharacterInfo(ch, out ci, size, FontStyle.Normal)) w += ci.advance; }
                widths[i] = w;
            }
            _solid.SetPass(0);
            GlSafe.Quads(() =>
            {
                GL.Color(new Color(0, 0, 0, 0.55f));
                for (int i = 0; i < texts.Count; i++)
                    Quad(pos[i][0] - pad, pos[i][1] - lh * 0.5f, pos[i][0] + widths[i] + pad, pos[i][1] + lh * 0.5f);
            });
            _font.material.SetPass(0);
            GlSafe.Quads(() =>
            {
                for (int i = 0; i < texts.Count; i++)
                {
                    GL.Color(ArgbColor(colors[i] | 0xFF000000u));
                    float x = pos[i][0], baseline = pos[i][1] + size * 0.35f;
                    foreach (char ch in texts[i])
                    {
                        CharacterInfo ci;
                        if (!_font.GetCharacterInfo(ch, out ci, size, FontStyle.Normal)) continue;
                        float x0 = x + ci.minX, x1 = x + ci.maxX, yTop = baseline - ci.maxY, yBot = baseline - ci.minY;
                        GL.TexCoord2(ci.uvTopLeft.x, ci.uvTopLeft.y); GL.Vertex3(x0, yTop, 0);
                        GL.TexCoord2(ci.uvTopRight.x, ci.uvTopRight.y); GL.Vertex3(x1, yTop, 0);
                        GL.TexCoord2(ci.uvBottomRight.x, ci.uvBottomRight.y); GL.Vertex3(x1, yBot, 0);
                        GL.TexCoord2(ci.uvBottomLeft.x, ci.uvBottomLeft.y); GL.Vertex3(x0, yBot, 0);
                        x += ci.advance;
                    }
                }
            });
        }

        public void Dispose()
        {
            if (_solid != null) UnityEngine.Object.Destroy(_solid);
            _solid = null;
        }
    }
}
