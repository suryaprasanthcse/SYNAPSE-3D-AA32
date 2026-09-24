using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A minimal glTF 2.0 reader for static models: turns a .gltf + .bin into ONE Unity Mesh asset with a sub-mesh
/// per primitive (in glTF material order), baking every node transform into the vertices. Editor-only, so no glTF
/// runtime or shader goes into the build - the mesh is an ordinary asset like any FBX import produces.
///
/// Supported: triangle primitives, POSITION / NORMAL / TEXCOORD_0, float vertex data, u8/u16/u32 indices, node
/// matrix or TRS. Not supported (and not needed for Sketchfab static exports): skins, morph targets, sparse
/// accessors, embedded data URIs, extensions.
///
/// glTF is right-handed and Unity left-handed: x is negated on positions and normals and each triangle's winding
/// is reversed. glTF UVs start top-left, Unity's bottom-left: v becomes 1 - v. Tangents are recomputed.
/// </summary>
public static class GltfMeshImporter
{
    [Serializable] sealed class Root { public Node[] nodes; public MeshDef[] meshes; public Accessor[] accessors; public BufferView[] bufferViews; public BufferDef[] buffers; public int[] scenes_dummy; }
    [Serializable] sealed class Node { public string name; public int mesh = -1; public int[] children; public float[] matrix; public float[] translation; public float[] rotation; public float[] scale; }
    [Serializable] sealed class MeshDef { public string name; public Primitive[] primitives; }
    [Serializable] sealed class Primitive { public Attributes attributes; public int indices = -1; public int material = -1; public int mode = 4; }
    [Serializable] sealed class Attributes { public int POSITION = -1; public int NORMAL = -1; public int TEXCOORD_0 = -1; }
    [Serializable] sealed class Accessor { public int bufferView = -1; public int byteOffset; public int componentType; public int count; public string type; }
    [Serializable] sealed class BufferView { public int buffer; public int byteOffset; public int byteLength; public int byteStride; }
    [Serializable] sealed class BufferDef { public string uri; public int byteLength; }

    public sealed class Part
    {
        public int Material;        // glTF material index
        public string Name;         // glTF mesh name + primitive index
        public Bounds Bounds;       // in Unity space
    }

    /// <summary>Convert and save; returns the mesh and, per sub-mesh, which glTF material it uses.</summary>
    /// <param name="skipMaterials">glTF material indices whose primitives are left out (e.g. a ground plate).</param>
    public static (Mesh mesh, List<Part> parts) Import(string gltfPath, string meshAssetPath, ICollection<int> skipMaterials = null)
    {
        var root = JsonUtility.FromJson<Root>(File.ReadAllText(gltfPath));
        string folder = Path.GetDirectoryName(gltfPath);
        var buffers = new List<byte[]>();
        foreach (var b in root.buffers)
        {
            if (string.IsNullOrEmpty(b.uri) || b.uri.StartsWith("data:"))
                throw new NotSupportedException("glTF buffers must be external .bin files.");
            buffers.Add(File.ReadAllBytes(Path.Combine(folder, Uri.UnescapeDataString(b.uri))));
        }

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var subMeshes = new List<List<int>>();
        var parts = new List<Part>();

        // Scene roots: every node that is nobody's child.
        var isChild = new bool[root.nodes.Length];
        foreach (var n in root.nodes) if (n.children != null) foreach (int c in n.children) isChild[c] = true;
        for (int i = 0; i < root.nodes.Length; i++)
            if (!isChild[i]) Walk(i, Matrix4x4.identity);

        void Walk(int index, Matrix4x4 parent)
        {
            var node = root.nodes[index];
            var world = parent * Local(node);
            if (node.mesh >= 0)
            {
                var mesh = root.meshes[node.mesh];
                for (int p = 0; p < mesh.primitives.Length; p++)
                {
                    var prim = mesh.primitives[p];
                    if (prim.mode != 4 || prim.attributes.POSITION < 0) continue;       // triangles only
                    if (skipMaterials != null && skipMaterials.Contains(prim.material)) continue;
                    int baseVertex = positions.Count;
                    var pos = ReadVec(root, buffers, prim.attributes.POSITION, 3);
                    var nor = prim.attributes.NORMAL >= 0 ? ReadVec(root, buffers, prim.attributes.NORMAL, 3) : null;
                    var uv = prim.attributes.TEXCOORD_0 >= 0 ? ReadVec(root, buffers, prim.attributes.TEXCOORD_0, 2) : null;

                    var bounds = new Bounds();
                    for (int v = 0; v < pos.Count; v++)
                    {
                        Vector3 w = world.MultiplyPoint3x4(new Vector3(pos[v][0], pos[v][1], pos[v][2]));
                        var u = new Vector3(-w.x, w.y, w.z);                              // right- to left-handed
                        positions.Add(u);
                        if (v == 0) bounds = new Bounds(u, Vector3.zero); else bounds.Encapsulate(u);
                        if (nor != null)
                        {
                            Vector3 n = world.MultiplyVector(new Vector3(nor[v][0], nor[v][1], nor[v][2])).normalized;
                            normals.Add(new Vector3(-n.x, n.y, n.z));
                        }
                        else normals.Add(Vector3.up);
                        uvs.Add(uv != null ? new Vector2(uv[v][0], 1f - uv[v][1]) : Vector2.zero);
                    }

                    var tris = new List<int>();
                    if (prim.indices >= 0)
                    {
                        var idx = ReadIndices(root, buffers, prim.indices);
                        for (int t = 0; t + 2 < idx.Count; t += 3)                          // reversed winding
                        {
                            tris.Add(baseVertex + idx[t]);
                            tris.Add(baseVertex + idx[t + 2]);
                            tris.Add(baseVertex + idx[t + 1]);
                        }
                    }
                    else
                        for (int t = 0; t + 2 < pos.Count; t += 3)
                        {
                            tris.Add(baseVertex + t);
                            tris.Add(baseVertex + t + 2);
                            tris.Add(baseVertex + t + 1);
                        }

                    subMeshes.Add(tris);
                    parts.Add(new Part { Material = prim.material, Name = $"{mesh.name}[{p}]", Bounds = bounds });
                }
            }
            if (node.children != null) foreach (int c in node.children) Walk(c, world);
        }

        var result = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
        bool created = result == null;
        if (created) result = new Mesh();
        result.Clear();
        result.name = Path.GetFileNameWithoutExtension(meshAssetPath);
        result.indexFormat = positions.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        result.SetVertices(positions);
        result.SetNormals(normals);
        result.SetUVs(0, uvs);
        result.subMeshCount = subMeshes.Count;
        for (int s = 0; s < subMeshes.Count; s++) result.SetTriangles(subMeshes[s], s);
        result.RecalculateTangents();
        result.RecalculateBounds();
        if (created) AssetDatabase.CreateAsset(result, meshAssetPath);
        else EditorUtility.SetDirty(result);
        AssetDatabase.SaveAssets();
        return (result, parts);
    }

    static Matrix4x4 Local(Node n)
    {
        if (n.matrix != null && n.matrix.Length == 16)
        {
            var m = new Matrix4x4();
            for (int i = 0; i < 16; i++) m[i % 4, i / 4] = n.matrix[i];                       // glTF is column-major
            return m;
        }
        var t = n.translation != null && n.translation.Length == 3 ? new Vector3(n.translation[0], n.translation[1], n.translation[2]) : Vector3.zero;
        var r = n.rotation != null && n.rotation.Length == 4 ? new Quaternion(n.rotation[0], n.rotation[1], n.rotation[2], n.rotation[3]) : Quaternion.identity;
        var s = n.scale != null && n.scale.Length == 3 ? new Vector3(n.scale[0], n.scale[1], n.scale[2]) : Vector3.one;
        return Matrix4x4.TRS(t, r, s);
    }

    static List<float[]> ReadVec(Root root, List<byte[]> buffers, int accessorIndex, int width)
    {
        var a = root.accessors[accessorIndex];
        if (a.componentType != 5126) throw new NotSupportedException($"accessor {accessorIndex}: only float vertex data is supported.");
        var view = root.bufferViews[a.bufferView];
        var data = buffers[view.buffer];
        int stride = view.byteStride > 0 ? view.byteStride : width * 4;
        int start = view.byteOffset + a.byteOffset;
        var list = new List<float[]>(a.count);
        for (int i = 0; i < a.count; i++)
        {
            var v = new float[width];
            for (int c = 0; c < width; c++) v[c] = BitConverter.ToSingle(data, start + i * stride + c * 4);
            list.Add(v);
        }
        return list;
    }

    static List<int> ReadIndices(Root root, List<byte[]> buffers, int accessorIndex)
    {
        var a = root.accessors[accessorIndex];
        var view = root.bufferViews[a.bufferView];
        var data = buffers[view.buffer];
        int start = view.byteOffset + a.byteOffset;
        var list = new List<int>(a.count);
        for (int i = 0; i < a.count; i++)
            list.Add(a.componentType switch
            {
                5121 => data[start + i],
                5123 => BitConverter.ToUInt16(data, start + i * 2),
                5125 => (int)BitConverter.ToUInt32(data, start + i * 4),
                _ => throw new NotSupportedException($"accessor {accessorIndex}: index type {a.componentType}.")
            });
        return list;
    }
}
