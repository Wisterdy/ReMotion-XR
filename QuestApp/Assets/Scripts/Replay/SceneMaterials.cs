using System.Collections.Generic;
using UnityEngine;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Runtime scene materials. On Quest, Shader.Find("Standard") can
    /// return null when no serialized material uses it (the shader is
    /// stripped from the build), and new Material(null) renders as the
    /// magenta error colour — the "weird pink plane" in the replay
    /// scene. Resolve with fallbacks, cache one material per colour,
    /// and never hand a renderer a null-shader material.
    /// </summary>
    public static class SceneMaterials
    {
        private static readonly Dictionary<Color, Material> Cache = new Dictionary<Color, Material>();
        private static bool _warnedMissingShader;

        public static Shader ResolveShader() =>
            Shader.Find("Standard") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");

        public static Material MakeColorMaterial(Color color)
        {
            if (Cache.TryGetValue(color, out var cached) && cached != null) return cached;
            var shader = ResolveShader();
            if (shader == null)
            {
                if (!_warnedMissingShader)
                {
                    _warnedMissingShader = true;
                    Debug.LogError("[ReMotion] No usable scene shader (Standard/Unlit/Sprites) in this build; " +
                                   "scene colours will use the primitive default material.");
                }
                return null;
            }
            var material = new Material(shader) { color = color, name = "ReMotionSceneColor" };
            Cache[color] = material;
            return material;
        }

        /// <summary>Tint a renderer; leaves its default material alone when no shader is available.</summary>
        public static void ApplyColor(Renderer renderer, Color color)
        {
            if (renderer == null) return;
            var material = MakeColorMaterial(color);
            if (material != null) renderer.sharedMaterial = material;
        }
    }
}
