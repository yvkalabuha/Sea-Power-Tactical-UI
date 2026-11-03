using System;
using System.Collections;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-190 (ADR-0034 item 3, fallback path): calls CoverageLayer.OnEndOfFrame after every frame. The layer draws from here only when no suitable
    /// screen camera has rendered it for 30 frames (the full-screen map has no Camera.main). Never throws.
    /// </summary>
    internal sealed class CoverageLayerDriver : MonoBehaviour
    {
        internal CoverageLayer Layer;           // null = does nothing

        private IEnumerator Start()
        {
            var w = new WaitForEndOfFrame();
            while (true)
            {
                yield return w;
                try { if (Layer != null) Layer.OnEndOfFrame(); } catch { }
            }
        }
    }
}
