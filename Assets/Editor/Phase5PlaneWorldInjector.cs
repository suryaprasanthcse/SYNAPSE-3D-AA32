using System.IO;
using System.Linq;
using MAAYAI.Swarm;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Phase 5: the shift from a single tracked card to a table-scale world on a detected AR plane.
///
/// What this does to the active scene:
///   1. Turns on horizontal plane detection, and leaves image tracking running alongside it - ARCore does both
///      at once, and the printed cards are still part of the submission. Only the two systems that genuinely
///      fight the plane world are disabled: the card-yaw puzzle and the card-anchored cave.
///   2. Injects Luminara_World: a plane-anchored root carrying untextured blockout for all three acts of the GDD
///      map - the Grand Gateway, the Rainbow Bridge and the Mystery Cave - authored at the document's real
///      dimensions and scaled down to whatever surface the device finds.
///   3. Injects the player rig: Petalo_Rig (pilot) -> Petalo_Gait (bob) -> Petalo (the raymarched avatar), with
///      the swarm re-tethered from the card to Petalo itself.
///
/// Idempotent: running it again rebuilds Luminara_World from scratch and re-applies the AR configuration.
/// </summary>
public static class Phase5PlaneWorldInjector
{
    const string WorldRootName = "Luminara_World";
    const string MaterialFolder = "Assets/MatrixPrefabs/Materials";
    const string SurfaceShaderName = "MAAYAI/SwarmSurface";

    // ---- The GDD's map, in design metres. Laid along +Z and centred on the root. ----
    // Act 1 spans z -65..-25, Act 2 spans z -20..+40, Act 3 spans z +45..+65.
    const float GatewayZ = -45f;
    const float BridgeZ = 10f;
    const float CaveZ = 55f;

    static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ID_Smoothness = Shader.PropertyToID("_Smoothness");
    static readonly int ID_SpecularStrength = Shader.PropertyToID("_SpecularStrength");

    [MenuItem("Funobotz/Deploy Phase 5 Plane World")]
    public static void Deploy()
    {
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Deploy Phase 5 Plane World");

        SwitchToPlaneDetection();

        var world = BuildWorldRoot(out PlaneAnchoredWorld anchored);
        if (world == null)
        {
            Undo.CollapseUndoOperations(undoGroup);
            return;
        }

        Material stone = GetBlockoutMaterial("Blockout_Stone", new Color(0.44f, 0.43f, 0.41f));
        Material span = GetBlockoutMaterial("Blockout_Span", new Color(0.52f, 0.47f, 0.40f));
        Material cave = GetBlockoutMaterial("Blockout_Cave", new Color(0.17f, 0.17f, 0.19f));

        BuildGrandGateway(world.transform, stone);
        BuildRainbowBridge(world.transform, span);
        Transform forge = BuildMysteryCave(world.transform, cave);

        BuildPlayerRig(world.transform, forge);
        FitToMeasuredBounds(world, anchored);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log($"[Phase5] Plane world deployed: 3 acts, {world.GetComponentsInChildren<MeshRenderer>(true).Length} " +
                  "blockout pieces, player rig tethered to the swarm.");
    }

    // ==============================================================================================
    // 1. AR configuration: image tracking off, plane detection on
    // ==============================================================================================
    static void SwitchToPlaneDetection()
    {
        var origin = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault();
        if (origin == null)
        {
            Debug.LogError("[Phase5] No XR Origin in the active scene: AR components cannot be configured.");
            return;
        }

        // Plane detection on, horizontal only. The world lies on a table; vertical planes are just walls the
        // player happens to be standing near, and detecting them costs tracking budget for nothing.
        var planeManager = origin.GetComponent<ARPlaneManager>();
        if (planeManager == null)
        {
            planeManager = Undo.AddComponent<ARPlaneManager>(origin.gameObject);
            Debug.Log("[Phase5] Added ARPlaneManager to the XR Origin.");
        }
        Undo.RecordObject(planeManager, "Configure plane detection");
        planeManager.enabled = true;
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
        EditorUtility.SetDirty(planeManager);

        // Image tracking stays ON. The plane now carries the world, but the printed cards are still part of the
        // submission and ARCore tracks images and planes at the same time without conflict. Turning this off was
        // the single most confusing thing about the first plane build: the cards silently stopped existing.
        foreach (var manager in Object.FindObjectsByType<ARTrackedImageManager>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            Undo.RecordObject(manager, "Enable image tracking");
            manager.enabled = true;
            EditorUtility.SetDirty(manager);
        }
        EnableAll<SpatialMatrixCardRig>("card rig");
        EnableAll<ARLibraryForceLoader>("reference library loader");

        // These two do conflict with the plane world and stay off: the card-yaw puzzle drives state from a card
        // rotation the new loop never asks for, and CardAnchoredContent would drag the old cave onto the card.
        DisableAll<PuzzleStateController>("card-yaw puzzle");
        DisableAll<CardAnchoredContent>("card-anchored content");
    }

    static void EnableAll<T>(string label) where T : MonoBehaviour
    {
        var found = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var component in found)
        {
            if (component.enabled) continue;
            Undo.RecordObject(component, $"Enable {label}");
            component.enabled = true;
            EditorUtility.SetDirty(component);
        }
        if (found.Length > 0) Debug.Log($"[Phase5] Enabled {found.Length} x {typeof(T).Name} ({label}).");
    }

    static void DisableAll<T>(string label) where T : MonoBehaviour
    {
        var found = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var component in found)
        {
            if (!component.enabled) continue;
            Undo.RecordObject(component, $"Disable {label}");
            component.enabled = false;
            EditorUtility.SetDirty(component);
        }
        if (found.Length > 0) Debug.Log($"[Phase5] Disabled {found.Length} x {typeof(T).Name} ({label}).");
    }

    // ==============================================================================================
    // 2. The world root
    // ==============================================================================================
    static GameObject BuildWorldRoot(out PlaneAnchoredWorld anchored)
    {
        // Rebuild from scratch so a second run cannot stack two worlds on the table.
        var existing = SceneManager.GetActiveScene().GetRootGameObjects()
            .FirstOrDefault(go => go.name == WorldRootName);
        if (existing != null) Undo.DestroyObjectImmediate(existing);

        var root = new GameObject(WorldRootName);
        Undo.RegisterCreatedObjectUndo(root, "Create Luminara World");
        root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        anchored = Undo.AddComponent<PlaneAnchoredWorld>(root);
        return root;
    }

    static void FitToMeasuredBounds(GameObject world, PlaneAnchoredWorld anchored)
    {
        var renderers = world.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0 || anchored == null) return;

        // Measure what was actually built rather than trusting the layout constants: the fit is then correct even
        // if an act is retuned later.
        Bounds bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);

        var so = new SerializedObject(anchored);
        so.FindProperty("worldSpanMetres").floatValue = Mathf.Max(bounds.size.z, 1f);
        so.FindProperty("worldWidthMetres").floatValue = Mathf.Max(bounds.size.x, 1f);
        so.ApplyModifiedProperties();

        Debug.Log($"[Phase5] World measures {bounds.size.x:0.0} x {bounds.size.z:0.0} design metres " +
                  "(fitted to the detected surface at runtime).");
    }

    // ==============================================================================================
    // 3. Act blockout
    // ==============================================================================================

    // Act 1 - The Grand Gateway. GDD: a 40 m shattered stone arch spanning a dark chasm, its segments sitting
    // off their load path. The causeway is the walkable route; the displaced segments are the puzzle.
    static void BuildGrandGateway(Transform parent, Material material)
    {
        var act = Node(parent, "Act1_GrandGateway", new Vector3(0f, 0f, GatewayZ));

        Block(act, "Plateau_Start", new Vector3(0f, -1f, -16f), new Vector3(24f, 2f, 16f), Vector3.zero, material);
        Block(act, "Plateau_Far", new Vector3(0f, -1f, 16f), new Vector3(24f, 2f, 16f), Vector3.zero, material);

        // The ceremonial gateway itself: two piers and a lintel, driven through rather than over.
        Block(act, "Pier_L", new Vector3(-7f, 5f, -9f), new Vector3(3f, 12f, 3f), Vector3.zero, material);
        Block(act, "Pier_R", new Vector3(7f, 5f, -9f), new Vector3(3f, 12f, 3f), Vector3.zero, material);
        Block(act, "Lintel", new Vector3(0f, 12f, -9f), new Vector3(19f, 2.5f, 3.5f), Vector3.zero, material);

        // The causeway across the chasm: seven segments, three of them off their load path.
        var seated = new[] { true, false, true, true, false, true, false };
        for (int i = 0; i < seated.Length; i++)
        {
            float z = -6f + i * 4f;
            bool ok = seated[i];
            // A displaced segment is dropped, shifted and rolled - visibly not carrying load, but still drivable,
            // because the GDD's rule is that a wrong state is a hint and never a dead end.
            Vector3 position = ok ? new Vector3(0f, 0f, z)
                                  : new Vector3(i % 2 == 0 ? 1.4f : -1.4f, -0.9f, z);
            Vector3 euler = ok ? Vector3.zero : new Vector3(0f, i * 7f, i % 2 == 0 ? 9f : -11f);
            Block(act, $"ArchSegment_{i}{(ok ? "" : "_Displaced")}", position, new Vector3(9f, 1.2f, 3.6f), euler, material);
        }

        // Chasm walls, so the gap reads as depth rather than as missing floor.
        Block(act, "ChasmWall_L", new Vector3(-13f, -6f, 0f), new Vector3(2f, 12f, 16f), Vector3.zero, material);
        Block(act, "ChasmWall_R", new Vector3(13f, -6f, 0f), new Vector3(2f, 12f, 16f), Vector3.zero, material);
    }

    // Act 2 - The Rainbow Bridge. GDD: about 60 m of five broken load-bearing spans across the second chasm.
    // The spans follow an arc, which is where the name comes from and what makes the misalignments readable.
    static void BuildRainbowBridge(Transform parent, Material material)
    {
        var act = Node(parent, "Act2_RainbowBridge", new Vector3(0f, 0f, BridgeZ));

        Block(act, "Abutment_Near", new Vector3(0f, -1f, -31f), new Vector3(20f, 2f, 8f), Vector3.zero, material);
        Block(act, "Abutment_Far", new Vector3(0f, -1f, 31f), new Vector3(20f, 2f, 8f), Vector3.zero, material);

        const int spanCount = 5;
        const float spanLength = 11f;
        const float arcRise = 7f;

        for (int i = 0; i < spanCount; i++)
        {
            // Centres at -24, -12, 0, 12, 24: five spans with a 1 m break between each.
            float z = (i - (spanCount - 1) * 0.5f) * (spanLength + 1f);
            float t = (float)i / (spanCount - 1);
            float height = Mathf.Sin(t * Mathf.PI) * arcRise;

            // Two of the five are off their load path: the puzzle is to bring every span back into line.
            bool aligned = i != 1 && i != 3;
            float roll = aligned ? 0f : (i == 1 ? 13f : -16f);
            float drop = aligned ? 0f : -1.6f;

            Block(act, $"Span_{i}{(aligned ? "" : "_Broken")}",
                  new Vector3(aligned ? 0f : (i == 1 ? -1.8f : 2.1f), height + drop, z),
                  new Vector3(9f, 1f, spanLength), new Vector3(0f, 0f, roll), material);

            // Pylons carry each span down to the chasm floor.
            if (i < spanCount - 1)
            {
                float pylonZ = z + (spanLength + 1f) * 0.5f;
                float pylonTop = Mathf.Sin((t + 0.5f / (spanCount - 1)) * Mathf.PI) * arcRise;
                Block(act, $"Pylon_{i}", new Vector3(0f, pylonTop * 0.5f - 6f, pylonZ),
                      new Vector3(2.4f, pylonTop + 12f, 2.4f), Vector3.zero, material);
            }
        }

        Block(act, "ChasmFloor", new Vector3(0f, -13f, 0f), new Vector3(26f, 1f, 56f), Vector3.zero, material);
    }

    // Act 3 - The Mystery Cave. GDD: an 18 x 18 m chamber with 10 m walls and the Lumenforge at its deepest node.
    // Returns the forge, so the player rig can be told where the state change lives.
    static Transform BuildMysteryCave(Transform parent, Material material)
    {
        var act = Node(parent, "Act3_MysteryCave", new Vector3(0f, 0f, CaveZ));

        const float half = 9f;      // 18 m chamber
        const float wallHeight = 10f;

        Block(act, "CaveFloor", new Vector3(0f, -0.5f, 0f), new Vector3(18f, 1f, 18f), Vector3.zero, material);
        Block(act, "CaveCeiling", new Vector3(0f, wallHeight, 0f), new Vector3(20f, 1f, 20f), Vector3.zero, material);
        Block(act, "Wall_Back", new Vector3(0f, wallHeight * 0.5f, half), new Vector3(20f, wallHeight, 1f), Vector3.zero, material);
        Block(act, "Wall_Left", new Vector3(-half, wallHeight * 0.5f, 0f), new Vector3(1f, wallHeight, 18f), Vector3.zero, material);
        Block(act, "Wall_Right", new Vector3(half, wallHeight * 0.5f, 0f), new Vector3(1f, wallHeight, 18f), Vector3.zero, material);

        // The mouth: two stubs either side of the entrance, so the chamber is closed but drivable into.
        Block(act, "Wall_Mouth_L", new Vector3(-6.5f, wallHeight * 0.5f, -half), new Vector3(7f, wallHeight, 1f), Vector3.zero, material);
        Block(act, "Wall_Mouth_R", new Vector3(6.5f, wallHeight * 0.5f, -half), new Vector3(7f, wallHeight, 1f), Vector3.zero, material);

        // Stalactites: blockout cones stand in as tapered boxes, hanging on a deterministic scatter so the layout
        // is identical on every run.
        var rng = new System.Random(20260920);
        for (int i = 0; i < 14; i++)
        {
            float x = (float)(rng.NextDouble() * 14.0 - 7.0);
            float z = (float)(rng.NextDouble() * 14.0 - 7.0);
            float length = 2f + (float)rng.NextDouble() * 3.5f;
            Block(act, $"Stalactite_{i}", new Vector3(x, wallHeight - length * 0.5f, z),
                  new Vector3(0.9f, length, 0.9f), new Vector3(0f, (float)rng.NextDouble() * 45f, 0f), material);
        }

        // The Lumenforge at the deepest node: the plinth, and the volume that turns Petalo into the Anomaly.
        var forge = Node(act, "Lumenforge", new Vector3(0f, 0f, 6f));
        Block(forge, "Forge_Plinth", new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 4f), Vector3.zero, material);
        Block(forge, "Forge_Core", new Vector3(0f, 2.4f, 0f), new Vector3(1.8f, 2.4f, 1.8f),
              new Vector3(0f, 45f, 0f), material);

        return forge;
    }

    // ==============================================================================================
    // 4. The player rig
    // ==============================================================================================
    static void BuildPlayerRig(Transform world, Transform forge)
    {
        // Petalo starts on the Gateway plateau, which is where the GDD says the player starts.
        var rig = Node(world, "Petalo_Rig", new Vector3(0f, 0f, GatewayZ - 16f));
        var gait = Node(rig, "Petalo_Gait", Vector3.zero);

        // Reuse the avatar that is already in the scene rather than making a second one.
        var beacon = Object.FindObjectsByType<PetaloBeacon>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault();
        if (beacon != null)
        {
            Undo.SetTransformParent(beacon.transform, gait, "Reparent Petalo");
            beacon.transform.localPosition = Vector3.zero;
            beacon.transform.localRotation = Quaternion.identity;
        }
        else
        {
            var avatar = new GameObject("Petalo");
            Undo.RegisterCreatedObjectUndo(avatar, "Create Petalo");
            avatar.transform.SetParent(gait, false);
            beacon = Undo.AddComponent<PetaloBeacon>(avatar);
            Debug.LogWarning("[Phase5] No PetaloBeacon was in the scene; a fresh one was created.");
        }

        // A trigger on the rig, so the forge can tell when the player has arrived. Sized in design metres, which
        // is the space the world root scales.
        var probe = Undo.AddComponent<SphereCollider>(rig.gameObject);
        probe.isTrigger = true;
        probe.radius = 1.5f;

        // Unity only raises trigger callbacks when one of the two parties has a Rigidbody. The rig is driven by
        // the transform, not by forces, so it is kinematic: without this the Lumenforge would never fire.
        var body = Undo.AddComponent<Rigidbody>(rig.gameObject);
        body.isKinematic = true;
        body.useGravity = false;

        var pilot = Undo.AddComponent<PetaloPilot>(rig.gameObject);
        var pilotSO = new SerializedObject(pilot);
        pilotSO.FindProperty("gait").objectReferenceValue = gait;
        pilotSO.FindProperty("petalo").objectReferenceValue = beacon;
        pilotSO.ApplyModifiedProperties();

        // The swarm follows Petalo now, not the King of Spades.
        var tether = Undo.AddComponent<SwarmTether>(rig.gameObject);
        var tetherSO = new SerializedObject(tether);
        tetherSO.FindProperty("target").objectReferenceValue = rig;
        tetherSO.ApplyModifiedProperties();

        if (forge != null)
        {
            var zone = Undo.AddComponent<BoxCollider>(forge.gameObject);
            zone.isTrigger = true;
            zone.center = new Vector3(0f, 2f, 0f);
            zone.size = new Vector3(6f, 4f, 6f);

            var anomaly = Undo.AddComponent<AnomalyZone>(forge.gameObject);
            var anomalySO = new SerializedObject(anomaly);
            anomalySO.FindProperty("petalo").objectReferenceValue = beacon;
            anomalySO.ApplyModifiedProperties();
        }

        // Petalo is scaled in design metres too: the avatar is roughly 3 m tall against a 12 m gateway.
        var beaconSO = new SerializedObject(beacon);
        beaconSO.FindProperty("size").floatValue = 3f;
        beaconSO.FindProperty("stateBlend").floatValue = 0f;    // start in Exploration
        // Beacon flux is computed from Petalo's WORLD size, which the table fit shrinks to about 2 cm. At that
        // size the original 0.5 - tuned for a 12 cm drone lighting a 60 cm cave - leaves a 90 cm map almost
        // black, because the avatar is now ~45x smaller than its world instead of ~5x. This is a starting point
        // to tune by eye, not a derived constant.
        beaconSO.FindProperty("beaconIntensity").floatValue = 1.5f;
        beaconSO.ApplyModifiedProperties();
    }

    // ==============================================================================================
    // Blockout helpers
    // ==============================================================================================
    static Transform Node(Transform parent, string name, Vector3 localPosition)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        return go.transform;
    }

    static GameObject Block(Transform parent, string name, Vector3 localPosition, Vector3 size, Vector3 euler,
                            Material material)
    {
        // A primitive cube brings its own BoxCollider, which is exactly what the pilot's ground ray needs.
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = size;

        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;      // the void has no directional light to cast from
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        return go;
    }

    /// <summary>
    /// Untextured blockout, shaded by MAAYAI/SwarmSurface so the world is revealed by the swarm's VPL grid and
    /// by Petalo's own beacon - the same light model as the rest of the build. Nothing here carries a texture.
    /// </summary>
    static Material GetBlockoutMaterial(string name, Color albedo)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        var shader = Shader.Find(SurfaceShaderName);
        if (shader == null)
        {
            Debug.LogError($"[Phase5] Shader '{SurfaceShaderName}' not found; blockout will use the default material.");
            return null;
        }

        Directory.CreateDirectory(MaterialFolder);
        var material = new Material(shader) { name = name };
        material.SetColor(ID_BaseColor, albedo);
        material.SetFloat(ID_Smoothness, 0.18f);
        material.SetFloat(ID_SpecularStrength, 0.15f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
