using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Emits the Doric/Tuscan kit parametrically: meshes, LOD prefabs, marble material and a generated veining
/// normal map. Everything here is team-created, which matters because the GDD's asset plan declares provenance.
///
/// The orders are lathe and extrusion forms - a fluted shaft is a radial profile, a cornice is a swept chamfer,
/// an urn is a profile spun about Y - so they generate cleanly and parametrically. What does NOT generate is
/// figurative relief and foliage; those are authored sculpture and are deliberately absent.
///
/// LODs are AUTHORED, not decimated. Each level re-runs the same profile at a lower radial and height segment
/// count, so the silhouette stays correct all the way down instead of collapsing the way a decimator would take
/// it. The flutes simply stop being generated below LOD0, which is exactly right: they are sub-pixel by then.
/// </summary>
public static class RomanKitGenerator
{
    const string KitFolder = "Assets/MatrixPrefabs/Roman";
    const string MeshFolder = KitFolder + "/Meshes";
    const string TextureFolder = KitFolder + "/Textures";
    const string MarblePath = KitFolder + "/Marble_Courtyard.mat";
    const string NormalPath = TextureFolder + "/Marble_Veining_Normal.png";

    // Design metres. The world root scales this to the table, so everything here is authored at architectural
    // size and never at tabletop size.
    const float ColumnHeight = 9.0f;
    const float ColumnRadius = 0.62f;
    // Sixteen, not the canonical twenty: the flute is a radial cosine, so it needs ~6 segments per groove to
    // resolve. Twenty flutes at an affordable segment count lands near 2 samples per period and the grooves
    // alias away entirely - the shaft renders smooth and the geometry is paid for with nothing to show.
    const int Flutes = 16;

    [MenuItem("Funobotz/Roman Kit/1. Generate Kit")]
    public static void Generate()
    {
        Directory.CreateDirectory(MeshFolder);
        Directory.CreateDirectory(TextureFolder);

        var marble = BuildMarble();

        BuildPiece("Column_Doric", marble, ColumnLods());
        BuildPiece("Stair_Tread", marble, BoxLods(new Vector3(4.0f, 0.42f, 1.10f), 0.045f));
        BuildPiece("Stylobate_Block", marble, BoxLods(new Vector3(4.0f, 0.70f, 4.0f), 0.055f));
        BuildPiece("Entablature_Span", marble, EntablatureLods());
        BuildPiece("Balustrade_Post", marble, BalusterLods());
        BuildPiece("Balustrade_Rail", marble, BoxLods(new Vector3(3.2f, 0.26f, 0.34f), 0.035f));
        BuildPiece("Urn", marble, UrnLods());
        BuildPiece("Pedestal", marble, BoxLods(new Vector3(2.4f, 1.30f, 2.4f), 0.06f));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[RomanKit] Kit generated into {KitFolder} (8 pieces, 3 LODs each).");
    }

    // ==============================================================================================
    // Pieces
    // ==============================================================================================

    /// <summary>Shaft with entasis, echinus and abacus. Flutes only exist on LOD0.</summary>
    static Mesh[] ColumnLods()
    {
        return new[]
        {
            // 96 radial = 6 samples per flute. Expensive per column, but LOD0 only engages when a column fills
            // ~45% of screen height, which is one or two of them at a time - never the whole colonnade.
            ColumnMesh("Column_Doric_LOD0", 96, 7, Flutes, 0.05f),
            ColumnMesh("Column_Doric_LOD1", 20, 3, 0, 0f),
            ColumnMesh("Column_Doric_LOD2", 10, 1, 0, 0f),
        };
    }

    static Mesh ColumnMesh(string name, int radial, int shaftRings, int flutes, float fluteDepth)
    {
        var profile = new List<Vector2>();
        var fluted = new List<bool>();

        float baseH = ColumnHeight * 0.055f;
        float capH = ColumnHeight * 0.085f;
        float shaftH = ColumnHeight - baseH - capH;

        // Base: plinth then torus-ish flare.
        profile.Add(new Vector2(ColumnRadius * 1.30f, 0f)); fluted.Add(false);
        profile.Add(new Vector2(ColumnRadius * 1.30f, baseH * 0.55f)); fluted.Add(false);
        profile.Add(new Vector2(ColumnRadius * 1.06f, baseH)); fluted.Add(false);

        // Shaft with entasis: a Doric shaft is not a cylinder, it swells slightly and tapers to ~0.82 at the neck.
        for (int i = 0; i <= shaftRings; i++)
        {
            float t = (float)i / shaftRings;
            float taper = Mathf.Lerp(1f, 0.82f, t);
            float entasis = 1f + 0.035f * Mathf.Sin(t * Mathf.PI);
            profile.Add(new Vector2(ColumnRadius * taper * entasis, baseH + shaftH * t));
            fluted.Add(true);
        }

        // Capital: echinus flare into a square-ish abacus.
        profile.Add(new Vector2(ColumnRadius * 0.86f, baseH + shaftH + capH * 0.18f)); fluted.Add(false);
        profile.Add(new Vector2(ColumnRadius * 1.16f, baseH + shaftH + capH * 0.62f)); fluted.Add(false);
        profile.Add(new Vector2(ColumnRadius * 1.24f, baseH + shaftH + capH * 0.70f)); fluted.Add(false);
        profile.Add(new Vector2(ColumnRadius * 1.24f, ColumnHeight)); fluted.Add(false);

        return Lathe(name, profile.ToArray(), fluted.ToArray(), radial, flutes, fluteDepth);
    }

    static Mesh[] EntablatureLods()
    {
        // Architrave, frieze, then a cornice that oversails - the overhang is what casts the band of shadow that
        // makes a colonnade read as architecture rather than as posts.
        return new[]
        {
            EntablatureMesh("Entablature_Span_LOD0", 0.05f, true),
            EntablatureMesh("Entablature_Span_LOD1", 0.03f, true),
            EntablatureMesh("Entablature_Span_LOD2", 0f, false),
        };
    }

    static Mesh EntablatureMesh(string name, float chamfer, bool threePart)
    {
        var parts = new List<CombineInstance>();
        void Add(Mesh m, Vector3 offset)
        {
            parts.Add(new CombineInstance { mesh = m, transform = Matrix4x4.Translate(offset) });
        }

        if (threePart)
        {
            Add(ChamferBox(name + "_a", new Vector3(4.0f, 0.62f, 1.05f), chamfer), new Vector3(0f, 0.31f, 0f));
            Add(ChamferBox(name + "_f", new Vector3(4.0f, 0.52f, 0.95f), chamfer), new Vector3(0f, 0.88f, 0f));
            Add(ChamferBox(name + "_c", new Vector3(4.0f, 0.34f, 1.38f), chamfer), new Vector3(0f, 1.31f, 0f));
        }
        else
        {
            Add(ChamferBox(name + "_s", new Vector3(4.0f, 1.48f, 1.15f), 0f), new Vector3(0f, 0.74f, 0f));
        }

        var mesh = new Mesh { name = name };
        mesh.CombineMeshes(parts.ToArray(), true, true);
        mesh.RecalculateBounds();
        return SaveMesh(mesh);
    }

    static Mesh[] BalusterLods()
    {
        return new[]
        {
            BalusterMesh("Balustrade_Post_LOD0", 18),
            BalusterMesh("Balustrade_Post_LOD1", 10),
            BalusterMesh("Balustrade_Post_LOD2", 6),
        };
    }

    static Mesh BalusterMesh(string name, int radial)
    {
        var p = new[]
        {
            new Vector2(0.20f, 0f),    new Vector2(0.20f, 0.10f), new Vector2(0.13f, 0.18f),
            new Vector2(0.17f, 0.34f), new Vector2(0.11f, 0.52f), new Vector2(0.08f, 0.68f),
            new Vector2(0.12f, 0.82f), new Vector2(0.18f, 0.94f), new Vector2(0.18f, 1.04f),
        };
        return Lathe(name, p, new bool[p.Length], radial, 0, 0f);
    }

    static Mesh[] UrnLods()
    {
        return new[] { UrnMesh("Urn_LOD0", 22), UrnMesh("Urn_LOD1", 12), UrnMesh("Urn_LOD2", 7) };
    }

    static Mesh UrnMesh(string name, int radial)
    {
        var p = new[]
        {
            new Vector2(0.26f, 0f),    new Vector2(0.28f, 0.08f), new Vector2(0.20f, 0.18f),
            new Vector2(0.38f, 0.42f), new Vector2(0.44f, 0.70f), new Vector2(0.38f, 0.98f),
            new Vector2(0.26f, 1.16f), new Vector2(0.30f, 1.28f), new Vector2(0.27f, 1.34f),
        };
        return Lathe(name, p, new bool[p.Length], radial, 0, 0f);
    }

    static Mesh[] BoxLods(Vector3 size, float chamfer)
    {
        string baseName = $"Box_{size.x:0.00}x{size.y:0.00}x{size.z:0.00}";
        return new[]
        {
            ChamferBox(baseName + "_LOD0", size, chamfer),
            ChamferBox(baseName + "_LOD1", size, chamfer * 0.5f),
            ChamferBox(baseName + "_LOD2", size, 0f),
        };
    }

    // ==============================================================================================
    // Mesh primitives
    // ==============================================================================================

    /// <summary>
    /// Spin a profile about Y. Flutes are a radial modulation of the shaft radius, so twenty grooves cost
    /// nothing beyond the radial segment count they need to resolve - no extra geometry, no texture.
    /// </summary>
    static Mesh Lathe(string name, Vector2[] profile, bool[] fluted, int radial, int flutes, float fluteDepth)
    {
        int rings = profile.Length;
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        float height = profile[rings - 1].y;

        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s <= radial; s++)          // duplicate seam column so UVs do not wrap backwards
            {
                float a = (float)s / radial * Mathf.PI * 2f;
                float radius = profile[r].x;

                if (flutes > 0 && fluted[r])
                    radius -= fluteDepth * (0.5f + 0.5f * Mathf.Cos(flutes * a));

                verts.Add(new Vector3(Mathf.Cos(a) * radius, profile[r].y, Mathf.Sin(a) * radius));
                uvs.Add(new Vector2((float)s / radial * 2f, profile[r].y / Mathf.Max(height, 1e-3f)));
            }
        }

        int stride = radial + 1;
        for (int r = 0; r < rings - 1; r++)
        {
            for (int s = 0; s < radial; s++)
            {
                int i0 = r * stride + s, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
                tris.Add(i0); tris.Add(i2); tris.Add(i1);
                tris.Add(i1); tris.Add(i2); tris.Add(i3);
            }
        }

        // Caps: a fan each end, so the piece is watertight and casts a solid shadow.
        AddCap(verts, uvs, tris, profile[0], radial, 0, false);
        AddCap(verts, uvs, tris, profile[rings - 1], radial, (rings - 1) * stride, true);

        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();       // the veining normal map needs tangents
        mesh.RecalculateBounds();
        return SaveMesh(mesh);
    }

    static void AddCap(List<Vector3> verts, List<Vector2> uvs, List<int> tris, Vector2 ring, int radial,
                       int ringStart, bool top)
    {
        int centre = verts.Count;
        verts.Add(new Vector3(0f, ring.y, 0f));
        uvs.Add(new Vector2(0.5f, 0.5f));

        for (int s = 0; s < radial; s++)
        {
            int a = ringStart + s, b = ringStart + s + 1;
            if (top) { tris.Add(centre); tris.Add(a); tris.Add(b); }
            else { tris.Add(centre); tris.Add(b); tris.Add(a); }
        }
    }

    /// <summary>
    /// Box with genuinely chamfered edges: six inset faces, twelve edge quads, eight corner triangles.
    ///
    /// The chamfer is what puts a highlight on every arris - real stone never has a perfectly sharp edge, and a
    /// box without one reads as greybox however good the lighting is. Every facet carries its own vertices rather
    /// than sharing them, so the flats stay flat and the bevel catches a distinct highlight instead of the whole
    /// piece shading like a rounded lozenge.
    ///
    /// Winding is derived from each facet's outward normal rather than written out by hand: twenty-six facets is
    /// well past the point where hand-ordered indices stay correct.
    /// </summary>
    static Mesh ChamferBox(string name, Vector3 size, float chamfer)
    {
        Vector3 h = size * 0.5f;
        float c = Mathf.Min(chamfer, Mathf.Min(h.x, Mathf.Min(h.y, h.z)) * 0.45f);

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        // A corner is a bitmask: bit0 = +x, bit1 = +y, bit2 = +z.
        Vector3 Sign(int corner) => new Vector3((corner & 1) != 0 ? 1f : -1f,
                                                (corner & 2) != 0 ? 1f : -1f,
                                                (corner & 4) != 0 ? 1f : -1f);

        // The three vertices a chamfered corner contributes: full extent on one axis, pulled back on the others.
        Vector3 CornerVert(int corner, int axis)
        {
            Vector3 e = new Vector3(h.x - c, h.y - c, h.z - c);
            e[axis] = h[axis];
            return Vector3.Scale(Sign(corner), e);
        }

        // UVs in metres, so the veining tiles at one density across every piece regardless of its size.
        Vector2 PlanarUV(Vector3 v, int axis) =>
            axis == 0 ? new Vector2(v.z, v.y) : axis == 1 ? new Vector2(v.x, v.z) : new Vector2(v.x, v.y);

        void Facet(Vector3[] pts, Vector3 outward, int uvAxis)
        {
            int b = verts.Count;
            foreach (var pt in pts) { verts.Add(pt); uvs.Add(PlanarUV(pt, uvAxis)); }

            bool flip = Vector3.Dot(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]), outward) < 0f;
            for (int i = 1; i < pts.Length - 1; i++)
            {
                if (flip) { tris.Add(b); tris.Add(b + i + 1); tris.Add(b + i); }
                else { tris.Add(b); tris.Add(b + i); tris.Add(b + i + 1); }
            }
        }

        // --- six faces ---
        int[,] ringOrder = { { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } };
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                var ring = new Vector3[4];
                for (int k = 0; k < 4; k++)
                {
                    int mask = 0;
                    if (sign > 0) mask |= 1 << axis;
                    if (ringOrder[k, 0] > 0) mask |= 1 << u;
                    if (ringOrder[k, 1] > 0) mask |= 1 << v;
                    ring[k] = CornerVert(mask, axis);
                }
                Vector3 outward = Vector3.zero;
                outward[axis] = sign;
                Facet(ring, outward, axis);
            }
        }

        // --- twelve edge quads: each bridges the two faces meeting along one axis ---
        for (int i = 0; i < 3; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                int k = 3 - i - j;                     // the axis this edge runs along
                for (int si = -1; si <= 1; si += 2)
                {
                    for (int sj = -1; sj <= 1; sj += 2)
                    {
                        int maskLo = 0;
                        if (si > 0) maskLo |= 1 << i;
                        if (sj > 0) maskLo |= 1 << j;
                        int maskHi = maskLo | (1 << k);

                        var quad = new[]
                        {
                            CornerVert(maskLo, i), CornerVert(maskLo, j),
                            CornerVert(maskHi, j), CornerVert(maskHi, i)
                        };
                        Vector3 outward = Vector3.zero;
                        outward[i] = si;
                        outward[j] = sj;
                        Facet(quad, outward.normalized, i);
                    }
                }
            }
        }

        // --- eight corner triangles ---
        for (int corner = 0; corner < 8; corner++)
        {
            var tri = new[] { CornerVert(corner, 0), CornerVert(corner, 1), CornerVert(corner, 2) };
            Facet(tri, Sign(corner).normalized, 1);
        }

        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return SaveMesh(mesh);
    }

    static Mesh SaveMesh(Mesh mesh)
    {
        string path = $"{MeshFolder}/{mesh.name}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            existing.Clear();
            existing.SetVertices(new List<Vector3>(mesh.vertices));
            existing.SetUVs(0, new List<Vector2>(mesh.uv));
            existing.SetTriangles(mesh.triangles, 0);
            existing.RecalculateNormals();
            existing.RecalculateTangents();
            existing.RecalculateBounds();
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    // ==============================================================================================
    // Prefabs with authored LODs
    // ==============================================================================================
    static void BuildPiece(string pieceName, Material marble, Mesh[] lods)
    {
        var root = new GameObject(pieceName);
        var group = root.AddComponent<LODGroup>();

        var levels = new LOD[lods.Length];
        // Screen-relative heights. The player pilots from a distance most of the time and occasionally brings
        // the phone right up to a column, so LOD0 is reserved for genuinely close inspection rather than being
        // the default state.
        float[] transitions = { 0.45f, 0.16f, 0.02f };

        for (int i = 0; i < lods.Length; i++)
        {
            var child = new GameObject($"{pieceName}_LOD{i}");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = lods[i];

            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = marble;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            levels[i] = new LOD(transitions[i], new Renderer[] { renderer });
        }

        group.SetLODs(levels);
        group.RecalculateBounds();

        Directory.CreateDirectory(KitFolder);
        PrefabUtility.SaveAsPrefabAsset(root, $"{KitFolder}/{pieceName}.prefab");
        Object.DestroyImmediate(root);

        int t0 = lods[0].triangles.Length / 3, t1 = lods[1].triangles.Length / 3, t2 = lods[2].triangles.Length / 3;
        Debug.Log($"[RomanKit] {pieceName}: LOD0 {t0} tris, LOD1 {t1}, LOD2 {t2}");
    }

    // ==============================================================================================
    // Marble
    // ==============================================================================================
    static Material BuildMarble()
    {
        var normal = BuildVeiningNormal();

        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var marble = AssetDatabase.LoadAssetAtPath<Material>(MarblePath);
        if (marble == null)
        {
            marble = new Material(lit) { name = "Marble_Courtyard" };
            AssetDatabase.CreateAsset(marble, MarblePath);
        }

        marble.shader = lit;
        marble.SetColor("_BaseColor", new Color(0.9922f, 0.9843f, 0.9686f, 1f));   // #FDFBF7
        marble.SetFloat("_Smoothness", 0.2f);
        marble.SetFloat("_Metallic", 0f);

        if (normal != null)
        {
            marble.SetTexture("_BumpMap", normal);
            marble.SetFloat("_BumpScale", 0.6f);
            marble.EnableKeyword("_NORMALMAP");
        }
        // Tiling in world-ish units: the UVs were authored in metres, so 1 means one veining period per metre.
        marble.SetTextureScale("_BaseMap", new Vector2(0.5f, 0.5f));
        marble.SetTextureScale("_BumpMap", new Vector2(0.5f, 0.5f));

        EditorUtility.SetDirty(marble);
        return marble;
    }

    /// <summary>
    /// A tiling veining normal map, synthesised rather than sourced. Marble veins are thin, high-contrast and
    /// directional, so the height field is ridged turbulence stretched along one axis; the normal comes from a
    /// Sobel of that. Seamless because the noise is sampled on a torus.
    /// </summary>
    static Texture2D BuildVeiningNormal()
    {
        const int N = 512;
        var height = new float[N * N];

        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                float u = (float)x / N, v = (float)y / N;
                float h = 0f, amp = 1f, freq = 3f;

                for (int o = 0; o < 4; o++)
                {
                    // Tiling noise: sample Perlin on a torus so the edges meet.
                    float nx = Mathf.Cos(u * Mathf.PI * 2f) * freq + 7.3f;
                    float ny = Mathf.Sin(u * Mathf.PI * 2f) * freq + 3.1f;
                    float n = Mathf.PerlinNoise(nx + v * freq * 2.4f, ny + v * freq * 0.7f);
                    h += Mathf.Abs(n - 0.5f) * 2f * amp;     // ridged: veins, not blobs
                    amp *= 0.5f;
                    freq *= 2.1f;
                }

                // Sharpen into thin veins and stretch them, which is what makes it read as marble.
                h = Mathf.Pow(1f - Mathf.Clamp01(h), 6f);
                height[y * N + x] = h;
            }
        }

        var tex = new Texture2D(N, N, TextureFormat.RGBA32, true);
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                float hl = height[y * N + ((x - 1 + N) % N)];
                float hr = height[y * N + ((x + 1) % N)];
                float hd = height[((y - 1 + N) % N) * N + x];
                float hu = height[((y + 1) % N) * N + x];

                var n = new Vector3((hl - hr) * 2.2f, (hd - hu) * 2.2f, 1f).normalized;
                px[y * N + x] = new Color32(
                    (byte)((n.x * 0.5f + 0.5f) * 255f),
                    (byte)((n.y * 0.5f + 0.5f) * 255f),
                    (byte)((n.z * 0.5f + 0.5f) * 255f), 255);
            }
        }
        tex.SetPixels32(px);
        tex.Apply();

        Directory.CreateDirectory(TextureFolder);
        File.WriteAllBytes(NormalPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(NormalPath, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(NormalPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.NormalMap;
            importer.mipmapEnabled = true;              // stops the veining shimmering as the camera pulls back
            importer.streamingMipmaps = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath);
    }
}
