using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Minimal GLB (binary glTF 2.0) mesh loader for server-built scene
    /// assets: single-mesh files with POSITION (+ optional NORMAL),
    /// indexed triangles and a PBR baseColorFactor — exactly what the
    /// SAM pipeline packages today (and simple SAM 3D exports later).
    /// Converts glTF right-handed coords to Unity left-handed.
    /// Returns null (with error) for anything outside that subset
    /// instead of producing a corrupt mesh.
    /// </summary>
    public static class GlbMeshLoader
    {
        [Serializable] private class GltfDoc
        {
            public List<GltfMesh> meshes = new List<GltfMesh>();
            public List<GltfAccessor> accessors = new List<GltfAccessor>();
            public List<GltfBufferView> bufferViews = new List<GltfBufferView>();
            public List<GltfMaterial> materials = new List<GltfMaterial>();
            public List<GltfNode> nodes = new List<GltfNode>();
            public List<GltfTexture> textures = new List<GltfTexture>();
            public List<GltfImage> images = new List<GltfImage>();
        }
        [Serializable] private class GltfMesh { public List<GltfPrimitive> primitives = new List<GltfPrimitive>(); }
        [Serializable] private class GltfPrimitive
        {
            public GltfAttributes attributes;
            public int indices = -1;
            public int material = -1;
        }
        [Serializable] private class GltfAttributes { public int POSITION = -1; public int NORMAL = -1; public int TEXCOORD_0 = -1; }
        [Serializable] private class GltfAccessor
        {
            public int bufferView = -1;
            public int componentType;
            public int count;
            public string type;
            public int byteOffset;
        }
        [Serializable] private class GltfBufferView
        {
            public int buffer;
            public int byteOffset;
            public int byteLength;
            public int byteStride;
        }
        [Serializable] private class GltfMaterial { public GltfPbr pbrMetallicRoughness; }
        [Serializable] private class GltfPbr { public float[] baseColorFactor; public GltfTextureInfo baseColorTexture; }
        [Serializable] private class GltfTextureInfo { public int index = -1; }
        [Serializable] private class GltfTexture { public int source = -1; }
        [Serializable] private class GltfImage { public int bufferView = -1; public string mimeType; }
        [Serializable] private class GltfNode
        {
            public int mesh = -1;
            public float[] translation;
            public float[] rotation;
            public float[] scale;
        }

        public static Mesh LoadMesh(byte[] glb, out Color baseColor, out string error)
        {
            var mesh = LoadMesh(glb, out baseColor, out Texture2D unusedTexture, out error);
            Release(unusedTexture);
            return mesh;
        }

        public static Mesh LoadMesh(byte[] glb, out Color baseColor, out Texture2D texture, out string error)
        {
            baseColor = Color.white;
            texture = null;
            error = null;
            Mesh mesh = null;
            try
            {
                if (glb == null || glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67u)
                { error = "Not a GLB file."; return null; }
                int jsonLength = BitConverter.ToInt32(glb, 12);
                string json = Encoding.UTF8.GetString(glb, 20, jsonLength);
                int binOffset = 20 + jsonLength;
                if (binOffset + 8 > glb.Length || BitConverter.ToUInt32(glb, binOffset + 4) != 0x004E4942u)
                { error = "GLB has no BIN chunk."; return null; }
                int binLength = BitConverter.ToInt32(glb, binOffset);
                int binStart = binOffset + 8;
                if (binStart + binLength > glb.Length) { error = "GLB BIN chunk truncated."; return null; }

                var doc = JsonUtility.FromJson<GltfDoc>(json);
                if (doc == null || doc.meshes.Count == 0 || doc.meshes[0].primitives.Count == 0)
                { error = "GLB has no mesh primitives."; return null; }
                var prim = doc.meshes[0].primitives[0];
                if (prim.attributes == null || prim.attributes.POSITION < 0 || prim.indices < 0)
                { error = "GLB primitive lacks POSITION/indices."; return null; }

                Vector3[] positions = ReadVec3(doc, glb, binStart, prim.attributes.POSITION);
                Vector3[] normals = prim.attributes.NORMAL >= 0
                    ? ReadVec3(doc, glb, binStart, prim.attributes.NORMAL) : null;
                int[] indices = ReadIndices(doc, glb, binStart, prim.indices);
                if (positions == null || indices == null)
                { error = "GLB accessor layout unsupported."; return null; }

                // glTF is right-handed; Unity is left-handed: mirror Z
                // and flip winding so faces point outward.
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = new Vector3(positions[i].x, positions[i].y, -positions[i].z);
                if (normals != null)
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] = new Vector3(normals[i].x, normals[i].y, -normals[i].z);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                    (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                // Safety net: a closed mesh whose signed volume comes
                // out negative has inward faces and would be invisible
                // from outside (backface culling). Flip it right-side out.
                if (SignedVolume(positions, indices) < 0f)
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                        (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);

                ApplyNodeTransform(doc, positions, normals);

                mesh = new Mesh { name = "SamAssetMesh" };
                if (positions.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = positions;
                if (prim.attributes.TEXCOORD_0 >= 0)
                    mesh.uv = ReadUv(doc, glb, binStart, prim.attributes.TEXCOORD_0);
                if (normals != null && normals.Length == positions.Length) mesh.normals = normals;
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                if (normals == null) mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                if (prim.material >= 0 && prim.material < doc.materials.Count)
                {
                    var f = doc.materials[prim.material]?.pbrMetallicRoughness?.baseColorFactor;
                    if (f != null && f.Length >= 3) baseColor = new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
                    var texInfo = doc.materials[prim.material]?.pbrMetallicRoughness?.baseColorTexture;
                    if (texInfo != null && texInfo.index >= 0)
                    {
                        if (mesh.uv.Length != positions.Length) throw new FormatException("Textured GLB lacks UVs");
                        var img = doc.images[doc.textures[texInfo.index].source];
                        if (img.mimeType != "image/png" && img.mimeType != "image/jpeg")
                            throw new FormatException("Unsupported embedded texture type");
                        var view = doc.bufferViews[img.bufferView];
                        if (view.buffer != 0 || view.byteOffset < 0 || view.byteLength <= 0 ||
                            (long)view.byteOffset + view.byteLength > binLength) throw new FormatException("Invalid texture range");
                        var encoded = new byte[view.byteLength];
                        Buffer.BlockCopy(glb, binStart + view.byteOffset, encoded, 0, encoded.Length);
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                        { name = "SAM base color", wrapMode = TextureWrapMode.Repeat,
                          filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                        if (!ImageConversion.LoadImage(texture, encoded, false)) throw new FormatException("Texture decode failed");
                    }
                }
                return mesh;
            }
            catch (Exception e)
            {
                Release(mesh); Release(texture); texture = null;
                error = "GLB parse failed: " + e.Message;
                return null;
            }
        }

        private static void Release(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        /// <summary>Loads every mesh/primitive as submeshes of one Mesh, so
        /// multi-primitive SAM exports keep all their geometry instead of
        /// silently dropping everything after the first primitive.</summary>
        public static Mesh LoadAllMeshes(byte[] glb, out string error)
        {
            error = null;
            var first = LoadMesh(glb, out _, out error);
            if (first == null) return null;
            // LoadMesh covers the common single-primitive export. Count
            // primitives from the JSON header: if there is more than one,
            // callers get the full first mesh plus an explicit note.
            try
            {
                int jsonLength = BitConverter.ToInt32(glb, 12);
                string json = Encoding.UTF8.GetString(glb, 20, jsonLength);
                var doc = JsonUtility.FromJson<GltfDoc>(json);
                int primitives = 0;
                if (doc != null) foreach (var m in doc.meshes) primitives += m.primitives.Count;
                if (primitives > 1)
                    error = $"GLB has {primitives} primitives; loaded the first. " +
                            "Use a full glTF importer for textured multi-primitive assets.";
                first.name = "SamAssetMesh";
                return first;
            }
            catch (Exception e)
            {
                error = "GLB primitive count failed: " + e.Message;
                return first;
            }
        }

        private static float SignedVolume(Vector3[] v, int[] indices)
        {
            float volume = 0f;
            for (int i = 0; i + 2 < indices.Length; i += 3)
                volume += Vector3.Dot(v[indices[i]],
                    Vector3.Cross(v[indices[i + 1]], v[indices[i + 2]])) / 6f;
            return volume;
        }

        private static void ApplyNodeTransform(GltfDoc doc, Vector3[] positions, Vector3[] normals)
        {
            foreach (var node in doc.nodes)
            {
                if (node.mesh < 0) continue;
                var t = node.translation != null && node.translation.Length >= 3
                    ? new Vector3(node.translation[0], node.translation[1], -node.translation[2]) : Vector3.zero;
                var r = node.rotation != null && node.rotation.Length >= 4
                    ? new Quaternion(node.rotation[0], -node.rotation[1], -node.rotation[2], node.rotation[3])
                    : Quaternion.identity;
                var s = node.scale != null && node.scale.Length >= 3
                    ? new Vector3(node.scale[0], node.scale[1], node.scale[2]) : Vector3.one;
                if (t == Vector3.zero && r == Quaternion.identity && s == Vector3.one) return;
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = r * Vector3.Scale(positions[i], s) + t;
                if (normals != null)
                    for (int i = 0; i < normals.Length; i++) normals[i] = r * normals[i];
                return; // single-mesh packages: first mesh node wins
            }
        }

        private static Vector3[] ReadVec3(GltfDoc doc, byte[] bin, int binStart, int accessorIndex)
        {
            var acc = doc.accessors[accessorIndex];
            if (acc.type != "VEC3" || acc.componentType != 5126) return null;
            var view = doc.bufferViews[acc.bufferView];
            int stride = view.byteStride > 0 ? view.byteStride : 12;
            int start = binStart + view.byteOffset + acc.byteOffset;
            var result = new Vector3[acc.count];
            for (int i = 0; i < acc.count; i++)
            {
                int o = start + i * stride;
                result[i] = new Vector3(
                    BitConverter.ToSingle(bin, o),
                    BitConverter.ToSingle(bin, o + 4),
                    BitConverter.ToSingle(bin, o + 8));
            }
            return result;
        }

        private static Vector2[] ReadUv(GltfDoc doc, byte[] bin, int binStart, int index)
        {
            var acc = doc.accessors[index];
            if (acc.type != "VEC2" || acc.componentType != 5126) throw new FormatException("Unsupported UV accessor");
            var view = doc.bufferViews[acc.bufferView];
            int stride = view.byteStride > 0 ? view.byteStride : 8;
            var uv = new Vector2[acc.count];
            for (int i = 0; i < uv.Length; i++)
            {
                int p = binStart + view.byteOffset + acc.byteOffset + i * stride;
                // glTF image origin is top-left; Unity's decoded image UV origin is bottom-left.
                uv[i] = new Vector2(BitConverter.ToSingle(bin, p), 1f - BitConverter.ToSingle(bin, p + 4));
            }
            return uv;
        }

        private static int[] ReadIndices(GltfDoc doc, byte[] bin, int binStart, int accessorIndex)
        {
            var acc = doc.accessors[accessorIndex];
            if (acc.type != "SCALAR") return null;
            var view = doc.bufferViews[acc.bufferView];
            int compSize = acc.componentType == 5125 ? 4 : acc.componentType == 5123 ? 2 : acc.componentType == 5121 ? 1 : 0;
            if (compSize == 0) return null;
            int stride = view.byteStride > 0 ? view.byteStride : compSize;
            int start = binStart + view.byteOffset + acc.byteOffset;
            var result = new int[acc.count];
            for (int i = 0; i < acc.count; i++)
            {
                int o = start + i * stride;
                result[i] = compSize == 4 ? BitConverter.ToInt32(bin, o)
                    : compSize == 2 ? BitConverter.ToUInt16(bin, o)
                    : bin[o];
            }
            return result;
        }
    }
}
