using System.Collections.Generic;
using System.Linq;
using MAAYAI.Swarm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Assembles a four-stage architectural circuit from the procedural Doric kit: a route with real traversal
/// length, elevation change and forced steering, rather than three props in a line.
///
///   1. Atrium Plaza      - sunken court, spawn, navigate around a central plinth
///   2. Switchback Terraces - 90 degree turn, two flights with a mid-landing, +5.2 m of climb
///   3. Colonnade Viaduct - long covered walkway driven UNDER the entablature, two broken spans
///   4. Tholos Sanctum    - elevated rotunda framing the AnomalyZone
///
/// The route folds back on itself so a long traversal fits a compact footprint, which is what makes it work on
/// a table: a straight circuit of the same length would scale down until nothing was legible.
///
/// Two things are firewalled and must survive untouched:
///   - Petalo_Rig and everything under it. The avatar is a signed distance field and has nothing to do with this.
///   - The Lumenforge node, which carries AnomalyZone, its trigger collider and a UnityEvent wired to the mission
///     header. It is detached and re-parented rather than rebuilt, because re-creating it would silently drop
///     that wiring - a break that only surfaces when a playthrough fails to complete.
/// </summary>
public static class Phase5RomanCourtyard
{
    const string ForkPath = "Assets/Scenes/Phase5_TrojanHorse.unity";
    const string KitFolder = "Assets/MatrixPrefabs/Roman";
    const string WorldRootName = "Luminara_World";
    const string RigName = "Petalo_Rig";

    // Kit dimensions the layout has to agree with, or seams open up under the wheels.
    const float Slab = 4.0f;          // stylobate block footprint
    const float SlabTop = 0.35f;      // half its height: slabs are placed by centre
    const float Tread = 0.42f;        // stair riser
    const float TreadDepth = 1.10f;
    const float ColumnH = 9.0f;

    static readonly Dictionary<string, GameObject> Kit = new();
    static Transform world;

    [MenuItem("Funobotz/Roman Kit/2. Build Circuit")]
    public static void Build()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ForkPath)
        {
            Debug.LogError($"[Circuit] Active scene is '{scene.path}', not the fork. Open {ForkPath} first.");
            return;
        }
        if (!LoadKit()) return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Build Roman Circuit");

        var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == WorldRootName);
        if (root == null)
        {
            Debug.LogError("[Circuit] No Luminara_World; run the Phase 5 injector first.");
            return;
        }
        world = root.transform;

        var rig = world.Find(RigName);
        var forge = FindForge(world);
        if (forge != null) Undo.SetTransformParent(forge, world, "Preserve Lumenforge");

        foreach (var child in world.Cast<Transform>().ToList())
        {
            if (child == rig || child == forge) continue;
            Undo.DestroyObjectImmediate(child.gameObject);
        }

        var atrium = Node("Stage1_AtriumPlaza", new Vector3(0f, 0f, -34f));
        var terraces = Node("Stage2_SwitchbackTerraces", new Vector3(0f, 0f, -8f));
        var viaduct = Node("Stage3_ColonnadeViaduct", new Vector3(22f, 0f, 14f));
        var tholos = Node("Stage4_TholosSanctum", new Vector3(22f, 0f, 52f));

        BuildAtrium(atrium);
        float landing = BuildTerraces(terraces);
        BuildViaduct(viaduct, landing);
        BuildTholos(tholos, landing, forge);

        // Spawn on the atrium floor south of the central plinth - not on its centre, which is inside the plinth
        // stack: the pilot would ground on the urn's top with a sheer drop on every side.
        if (rig != null) rig.localPosition = new Vector3(0f, SlabTop, -39.5f);

        ApplyThermalBudget(root);
        FitWorldToCircuit(root);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Report(root, rig, forge);
    }

    // ==============================================================================================
    // Stage 1 - Atrium Plaza: sunken court, colonnade, central plinth to steer around
    // ==============================================================================================
    static void BuildAtrium(Transform s)
    {
        // Sunken 4x4 floor, one slab-height down. The rim around it is what makes it read as sunken.
        for (int x = -2; x < 2; x++)
            for (int z = -2; z < 2; z++)
                Place("Stylobate_Block", s, new Vector3(x * Slab + Slab * 0.5f, -0.7f, z * Slab + Slab * 0.5f));

        // Raised perimeter walk, open on the north-east so the route can leave.
        for (int i = -3; i <= 2; i++)
        {
            Place("Stylobate_Block", s, new Vector3(i * Slab + Slab * 0.5f, SlabTop, -10f));
            Place("Stylobate_Block", s, new Vector3(-10f, SlabTop, i * Slab + Slab * 0.5f));
            if (i < 2) Place("Stylobate_Block", s, new Vector3(i * Slab + Slab * 0.5f, SlabTop, 10f));
            if (i < 1) Place("Stylobate_Block", s, new Vector3(10f, SlabTop, i * Slab + Slab * 0.5f));
        }

        // Colonnade on the rim.
        float top = SlabTop + 0.35f;
        for (int i = -2; i <= 2; i++)
        {
            Place("Column_Doric", s, new Vector3(i * 4.2f, top, -10f));
            Place("Column_Doric", s, new Vector3(-10f, top, i * 4.2f));
            if (i <= 1) Place("Column_Doric", s, new Vector3(i * 4.2f, top, 10f));
            Place("Entablature_Span", s, new Vector3(i * 4.2f, top + ColumnH, -10f));
            Place("Entablature_Span", s, new Vector3(-10f, top + ColumnH, i * 4.2f), 90f);
        }

        // Central plinth: the thing the player has to drive around rather than through.
        Place("Pedestal", s, new Vector3(0f, -0.35f, 0f));
        Place("Pedestal", s, new Vector3(0f, 0.95f, 0f), 45f);
        Place("Urn", s, new Vector3(0f, 1.6f, 0f));

        // Friction along the walls.
        Place("Urn", s, new Vector3(-7.5f, SlabTop + 0.35f, -7.5f));
        Place("Urn", s, new Vector3(7.5f, SlabTop + 0.35f, -7.5f));
        Place("Urn", s, new Vector3(-7.5f, SlabTop + 0.35f, 7.5f));
        BrokenPillar(s, new Vector3(5.5f, -0.35f, -5.5f));
        BrokenPillar(s, new Vector3(-5.0f, -0.35f, 4.0f));
    }

    // ==============================================================================================
    // Stage 2 - Switchback: east out of the atrium, then two flights north with a landing between
    // ==============================================================================================
    static float BuildTerraces(Transform s)
    {
        // Causeway north out of the atrium. Without this the rim ends at z -24 and the approach starts at
        // z -10: a fourteen-metre hole in the middle of the route that nothing can cross.
        for (int i = 0; i < 4; i++)
            Place("Stylobate_Block", s, new Vector3(0f, SlabTop, -14f + i * Slab));

        // Then east, into the foot of the first flight.
        for (int i = 0; i < 4; i++)
            Place("Stylobate_Block", s, new Vector3(i * Slab, SlabTop, -2f));

        // First flight: climbing east.
        float y = SlabTop;
        for (int i = 0; i < 5; i++)
        {
            y += Tread;
            float x = 14f + i * TreadDepth;
            Place("Stair_Tread", s, new Vector3(x, y - Tread * 0.5f, -2f), 90f);
            Place("Balustrade_Post", s, new Vector3(x, y, -4.2f), 0f);
            Place("Balustrade_Post", s, new Vector3(x, y, 0.2f), 0f);
        }

        // Mid-landing: wide enough to stop, turn, and look back down the flight.
        float landingY = y;
        for (int x = 0; x < 2; x++)
            for (int z = 0; z < 2; z++)
                Place("Stylobate_Block", s, new Vector3(21f + x * Slab, landingY - SlabTop, z * Slab + 1f));

        // Throat slab: the flight runs along z -4..0 but the landing only starts at z -1, so without this the
        // two meet over a one-metre strip - narrower than Petalo, whose ledge clamp will not cross the void.
        // Placed flush against the landing (z -5..-1) rather than overlapping it, so no coplanar tops z-fight.
        Place("Stylobate_Block", s, new Vector3(21f, landingY - SlabTop, -3f));

        Place("Urn", s, new Vector3(19.5f, landingY, 6.5f));
        BrokenPillar(s, new Vector3(25.5f, landingY, 1.5f));

        // Second flight: the 90 degree turn, now climbing north.
        for (int i = 0; i < 5; i++)
        {
            y += Tread;
            float z = 7.5f + i * TreadDepth;
            Place("Stair_Tread", s, new Vector3(22f, y - Tread * 0.5f, z));
            Place("Balustrade_Post", s, new Vector3(19.8f, y, z), 0f);
            Place("Balustrade_Post", s, new Vector3(24.2f, y, z), 0f);
        }

        for (int i = 0; i < 3; i++)
        {
            Place("Balustrade_Rail", s, new Vector3(19.8f, landingY + 1.2f, 8.5f + i * 1.6f), 90f);
            Place("Balustrade_Rail", s, new Vector3(24.2f, landingY + 1.2f, 8.5f + i * 1.6f), 90f);
        }

        // Upper landing carrying the deck from the top of the flight to the mouth of the viaduct, which starts
        // eight metres further north. Same reason as the causeway: the stage nodes do not touch on their own.
        for (int i = 0; i < 3; i++)
            Place("Stylobate_Block", s, new Vector3(22f, y - SlabTop, 13f + i * Slab));

        return y;      // deck height for everything downstream
    }

    // ==============================================================================================
    // Stage 3 - Viaduct: covered walkway with galleries either side and two broken spans
    // ==============================================================================================
    static void BuildViaduct(Transform s, float deckY)
    {
        const int Spans = 9;
        float local = deckY;                       // the stage node is at y=0, so work in absolute deck height

        // Spans 3 and 6 lose their east slab: the deck narrows to one slab and has to be threaded.
        var broken = new HashSet<int> { 3, 6 };

        for (int i = 0; i < Spans; i++)
        {
            float z = i * Slab;

            Place("Stylobate_Block", s, new Vector3(-Slab * 0.5f, local - SlabTop, z));
            if (!broken.Contains(i))
                Place("Stylobate_Block", s, new Vector3(Slab * 0.5f, local - SlabTop, z));

            // Gallery columns and the entablature overhead: this is the "drive under" geometry.
            if (i % 2 == 0)
            {
                Place("Column_Doric", s, new Vector3(-6.2f, local, z));
                Place("Column_Doric", s, new Vector3(6.2f, local, z));
                Place("Entablature_Span", s, new Vector3(-6.2f, local + ColumnH, z), 90f);
                Place("Entablature_Span", s, new Vector3(6.2f, local + ColumnH, z), 90f);
                // Three tiled pieces bridge the 12.4 m between the galleries. One piece stretched to fit would
                // be transform-scaling a prop to fake geometry, and it reads as one: the mouldings smear.
                for (int k = -1; k <= 1; k++)
                    Place("Entablature_Span", s, new Vector3(k * 4.1f, local + ColumnH, z));
            }

            // Balustrade along the intact edge only - the broken side is meant to be an open drop.
            if (!broken.Contains(i))
            {
                Place("Balustrade_Post", s, new Vector3(4.6f, local, z));
                Place("Balustrade_Rail", s, new Vector3(4.6f, local + 1.2f, z + Slab * 0.5f), 90f);
            }
            Place("Balustrade_Post", s, new Vector3(-4.6f, local, z));
            Place("Balustrade_Rail", s, new Vector3(-4.6f, local + 1.2f, z + Slab * 0.5f), 90f);

            // Piers carrying the deck, so it reads as elevated rather than floating.
            if (i % 3 == 0)
            {
                Place("Column_Doric", s, new Vector3(-3f, local - ColumnH - 0.7f, z));
                Place("Column_Doric", s, new Vector3(3f, local - ColumnH - 0.7f, z));
            }
        }

        // Rubble at the gap mouths: obstacles exactly where the steering is tightest.
        BrokenPillar(s, new Vector3(2.2f, local, 3f * Slab - 1.6f));
        BrokenPillar(s, new Vector3(2.4f, local, 6f * Slab + 1.6f));
        Place("Urn", s, new Vector3(-3.1f, local, 4.5f * Slab));
    }

    // ==============================================================================================
    // Stage 4 - Tholos: semi-circular rotunda framing the AnomalyZone
    // ==============================================================================================
    static void BuildTholos(Transform s, float deckY, Transform forge)
    {
        float y = deckY + Tread * 3f;

        // Three steps up onto the sanctum.
        for (int i = 0; i < 3; i++)
            for (int x = -2; x <= 2; x++)
                Place("Stair_Tread", s, new Vector3(x * 4f, deckY + Tread * (i + 0.5f), -4f + i * TreadDepth));

        // Circular platform.
        for (int x = -2; x <= 2; x++)
            for (int z = -1; z <= 3; z++)
            {
                var p = new Vector2(x * Slab, z * Slab);
                if (p.magnitude > 10.5f) continue;
                Place("Stylobate_Block", s, new Vector3(p.x, y - SlabTop, p.y));
            }

        // The rotunda: a half circle of columns opening back toward the viaduct.
        const int Pillars = 11;
        for (int i = 0; i < Pillars; i++)
        {
            float a = Mathf.Lerp(-170f, -10f, (float)i / (Pillars - 1)) * Mathf.Deg2Rad;
            var p = new Vector3(Mathf.Cos(a) * 8.5f, y, Mathf.Sin(a) * 8.5f + 8f);
            Place("Column_Doric", s, p);
            Place("Entablature_Span", s, new Vector3(p.x, y + ColumnH, p.z),
                  Mathf.Atan2(p.x, p.z - 8f) * Mathf.Rad2Deg);
        }

        foreach (var a in new[] { -150f, -30f })
        {
            float r = a * Mathf.Deg2Rad;
            Place("Urn", s, new Vector3(Mathf.Cos(r) * 5.5f, y, Mathf.Sin(r) * 5.5f + 8f));
        }

        if (forge == null) return;

        // Re-home the preserved Lumenforge at the centre of the rotunda and give it a marble altar.
        Undo.SetTransformParent(forge, s, "Restore Lumenforge");
        forge.localPosition = new Vector3(0f, y, 8f);
        foreach (var child in forge.Cast<Transform>().ToList())
            Undo.DestroyObjectImmediate(child.gameObject);

        Place("Pedestal", forge, Vector3.zero);
        Place("Pedestal", forge, new Vector3(0f, 1.3f, 0f), 45f);
    }

    /// <summary>
    /// A toppled column drum on its pedestal. Scaled uniformly, not squashed: compressing a fluted shaft on Y
    /// alone smears the flutes and the capital into something that reads as a bug. Roman shafts were assembled
    /// from stacked drums, so a drum lying on a plinth is the correct ruin, not an approximation of one.
    /// </summary>
    static void BrokenPillar(Transform parent, Vector3 local)
    {
        Place("Pedestal", parent, local);
        var drum = Place("Column_Doric", parent, local + new Vector3(0f, 0.65f, 0f), Random.Range(0f, 360f));
        drum.transform.localScale = Vector3.one * 0.26f;
    }

    // ==============================================================================================
    // Placement
    // ==============================================================================================
    static GameObject Place(string piece, Transform parent, Vector3 local, float yaw = 0f)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(Kit[piece], parent);
        Undo.RegisterCreatedObjectUndo(instance, "Place " + piece);
        instance.transform.localPosition = local;
        instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        AddCollider(instance);
        return instance;
    }

    /// <summary>
    /// A collider tight to the LOD0 mesh. The pilot grounds Petalo with a downward ray and the swarm's future
    /// collision would read the same volumes, so a loose box would both float the avatar and snag it on seams
    /// that are not there. Lathe pieces get a convex MeshCollider because a box around a column is a square peg
    /// the player can catch on from 45 degrees.
    /// </summary>
    static void AddCollider(GameObject instance)
    {
        var lod0 = instance.GetComponentInChildren<MeshFilter>();
        if (lod0 == null || lod0.sharedMesh == null) return;

        string n = instance.name;
        bool round = n.StartsWith("Column") || n.StartsWith("Urn") || n.StartsWith("Balustrade_Post");

        if (round)
        {
            // Convex hull of the LOD2 silhouette: cheap, and round enough that nothing catches on a corner.
            var group = instance.GetComponent<LODGroup>();
            Mesh hull = lod0.sharedMesh;
            if (group != null)
            {
                var lods = group.GetLODs();
                var last = lods[lods.Length - 1].renderers.FirstOrDefault();
                var mf = last != null ? last.GetComponent<MeshFilter>() : null;
                if (mf != null && mf.sharedMesh != null) hull = mf.sharedMesh;
            }
            var mc = Undo.AddComponent<MeshCollider>(instance);
            mc.sharedMesh = hull;
            mc.convex = true;
        }
        else
        {
            var box = Undo.AddComponent<BoxCollider>(instance);
            box.center = lod0.sharedMesh.bounds.center;
            box.size = lod0.sharedMesh.bounds.size;
        }
    }

    static Transform Node(string name, Vector3 local)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(world, false);
        go.transform.localPosition = local;
        return go.transform;
    }

    static bool LoadKit()
    {
        Kit.Clear();
        foreach (var n in new[] { "Column_Doric", "Stair_Tread", "Stylobate_Block", "Entablature_Span",
                                  "Balustrade_Post", "Balustrade_Rail", "Urn", "Pedestal" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{KitFolder}/{n}.prefab");
            if (prefab == null)
            {
                Debug.LogError($"[Circuit] Missing kit piece '{n}'. Run Funobotz/Roman Kit/1. Generate Kit first.");
                return false;
            }
            Kit[n] = prefab;
        }
        return true;
    }

    static Transform FindForge(Transform root)
    {
        foreach (var z in root.GetComponentsInChildren<AnomalyZone>(true)) return z.transform;
        return null;
    }

    // ==============================================================================================
    // Budget and fit
    // ==============================================================================================
    static void ApplyThermalBudget(GameObject root)
    {
        QualitySettings.lodBias = 1.0f;
        QualitySettings.maximumLODLevel = 0;
        QualitySettings.streamingMipmapsActive = true;
        QualitySettings.streamingMipmapsMemoryBudget = 128f;

        int flagged = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.GetComponentInParent<PetaloPilot>() != null) continue;        // firewall
            if (t.GetComponent<MeshRenderer>() == null && t.GetComponent<LODGroup>() == null) continue;
            GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic |
                StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ContributeGI);
            flagged++;
        }
        Debug.Log($"[Circuit] lodBias 1.0, streaming mipmaps on, {flagged} objects static.");
    }

    /// <summary>
    /// Re-fit the plane anchor to what was actually built. The circuit is much longer than the old three-piece
    /// layout, so a stale span would either shrink it to nothing or hang it off the edge of the table.
    /// </summary>
    static void FitWorldToCircuit(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0) return;

        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);

        var anchored = root.GetComponent<PlaneAnchoredWorld>();
        if (anchored != null)
        {
            var so = new SerializedObject(anchored);
            so.FindProperty("worldSpanMetres").floatValue = Mathf.Max(b.size.z, 1f);
            so.FindProperty("worldWidthMetres").floatValue = Mathf.Max(b.size.x, 1f);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(anchored);
        }
        Debug.Log($"[Circuit] Footprint {b.size.x:0.0} x {b.size.z:0.0} design m, {b.size.y:0.0} m of elevation.");
    }

    static void Report(GameObject root, Transform rig, Transform forge)
    {
        var groups = root.GetComponentsInChildren<LODGroup>(true);
        int colliders = root.GetComponentsInChildren<Collider>(true).Length;

        Debug.Log($"[Circuit] {groups.Length} LOD groups, {colliders} colliders across 4 stages.");
        Debug.Log($"[Circuit] Firewall: Petalo_Rig {(rig != null ? "PRESERVED" : "MISSING")}, " +
                  $"Lumenforge {(forge != null ? "PRESERVED" : "MISSING")}.");
    }
}
