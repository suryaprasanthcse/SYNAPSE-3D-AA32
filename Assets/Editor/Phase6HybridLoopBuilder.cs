using System.Collections.Generic;
using System.Linq;
using MAAYAI.Swarm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Locks the hybrid gameplay loop into Phase5_TrojanHorse: three acts from the GDD, each a long traversal of
/// ruined colonnades and grand staircases ending at a binary card gate, driven by <see cref="HybridLoopDirector"/>.
///
///   Act 1  THE GRAND GATEWAY   causeway colonnade, two grand flights, a fractured arch that restores itself
///   Act 2  THE MYSTERY CAVE    a grand descent into a hypostyle hall choked with rubble, sealed vault doors
///   Act 3  THE LUMENFORGE      colonnade, a switchback climb, a broken viaduct, the Tholos and its forge
///
/// All three are authored in design metres with their ground datum at y = 0, and laid side by side along X so
/// they do not overlap in the Scene view. At runtime only one is active and the world root is moved so that act
/// lands on the real floor in front of the player (room scale, 0.03: Petalo is the GDD's 12 cm drone).
///
/// Everything architectural is on Marble_DarkAR - the Doric kit's own meshes and marble albedo, shaded by the
/// SwarmSurface forward shader with its key light gain at zero, so it is lit by Petalo and the swarm alone.
///
/// Firewalled, as in the circuit builder: Petalo_Rig is moved, never rebuilt, and the Lumenforge node (AnomalyZone
/// plus its persisted UnityEvent to the mission header) is re-homed rather than recreated.
/// </summary>
public static class Phase6HybridLoopBuilder
{
    const string ScenePath = "Assets/Scenes/Phase5_TrojanHorse.unity";
    const string KitFolder = "Assets/MatrixPrefabs/Roman";
    const string MarblePath = KitFolder + "/Marble_Courtyard.mat";
    const string DarkMarblePath = KitFolder + "/Marble_DarkAR.mat";
    const string WorldRootName = "Luminara_World";
    const string RigName = "Petalo_Rig";
    const float RoomScale = 0.03f;

    // Kit dimensions (measured from the generated LOD0 meshes).
    const float Slab = 4.0f;          // stylobate footprint
    const float SlabHalf = 0.35f;     // half its thickness: slabs are placed by centre
    const float Tread = 0.42f;        // stair riser
    const float TreadDepth = 1.10f;
    const float ColumnH = 9.0f;
    const float PedestalH = 1.3f;
    const float SpawnLift = 2.05f;    // Petalo's hover + half its 4 m body: the rig origin is its centre

    static readonly Dictionary<string, GameObject> Kit = new();
    static Material darkMarble;
    static System.Random rng;

    const string SimulationEnvironmentPath = "Assets/XR/SimulationEnvironments/MatrixSimulationEnvironment.prefab";

    /// <summary>
    /// Puts the Act 2 and Act 3 cards into the XR Simulation room next to the original King of Spades, so every
    /// gate can be solved in Play mode: during a scan, F aims the simulated phone at the card the gate wants.
    /// They hang on the room's wall, where the acts laid on the floor can never cover them.
    /// </summary>
    [MenuItem("Funobotz/Hybrid Loop/Add Simulation Cards")]
    public static void AddSimulationCards()
    {
        var root = PrefabUtility.LoadPrefabContents(SimulationEnvironmentPath);
        try
        {
            var king = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Simulated_Card");
            if (king == null)
            {
                Debug.LogError("[HybridLoop] No Simulated_Card in the simulation environment to copy from.");
                return;
            }
            var kingRenderer = king.GetComponent<MeshRenderer>();

            var cards = new[]
            {
                (reference: "queen_of_hearts", texture: "Assets/queen_of_hearts.jpg", size: new Vector2(0.10f, 0.15f), z: 0.55f),
                (reference: "all_knowing_eye", texture: "Assets/all_knowing_eye.jpg", size: new Vector2(0.10f, 0.18f), z: -0.55f),
            };

            foreach (var card in cards)
            {
                string objectName = "Simulated_Card_" + card.reference;
                var old = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == objectName);
                if (old != null) Object.DestroyImmediate(old.gameObject);

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(card.texture);
                if (texture == null)
                {
                    Debug.LogError($"[HybridLoop] Card texture '{card.texture}' not found.");
                    continue;
                }

                var copy = Object.Instantiate(king.gameObject, king.parent);
                copy.name = objectName;
                // On the wall (x = -1.25), face turned into the room: the image normal is the card's +Y.
                copy.transform.localPosition = new Vector3(-1.235f, 0.55f, card.z);
                copy.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
                copy.transform.localScale = Vector3.one;

                var tracked = new SerializedObject(copy.GetComponent("SimulatedTrackedImage"));
                tracked.FindProperty("m_Image").objectReferenceValue = texture;
                tracked.FindProperty("m_ImagePhysicalSizeMeters").vector2Value = card.size;
                tracked.ApplyModifiedPropertiesWithoutUndo();

                if (kingRenderer != null && kingRenderer.sharedMaterial != null)
                {
                    string matPath = $"Assets/Materials/Simulated_Card_{card.reference}_Unlit.mat";
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    if (mat == null)
                    {
                        mat = new Material(kingRenderer.sharedMaterial);
                        AssetDatabase.CreateAsset(mat, matPath);
                    }
                    mat.mainTexture = texture;
                    EditorUtility.SetDirty(mat);
                    copy.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, SimulationEnvironmentPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[HybridLoop] Simulation room now holds all three gate cards (King of Spades, Queen of Diamonds, All-Knowing Eye).");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    [MenuItem("Funobotz/Hybrid Loop/Reset Saved Progress")]
    public static void ResetProgress()
    {
        HybridLoopDirector.ResetProgress();
        Debug.Log("[HybridLoop] Saved progress cleared: the next run starts at Act 1.");
    }

    [MenuItem("Funobotz/Hybrid Loop/Build Hybrid Loop")]
    public static void Build()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            Debug.LogError($"[HybridLoop] Active scene is '{scene.path}'. Open {ScenePath} first.");
            return;
        }
        if (!LoadKit()) return;
        darkMarble = BuildDarkMarble();

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Build Hybrid Loop");

        var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == WorldRootName);
        if (root == null)
        {
            Debug.LogError("[HybridLoop] No Luminara_World in the scene.");
            return;
        }
        Transform world = root.transform;

        var rig = world.Find(RigName);
        var forgeZone = root.GetComponentsInChildren<AnomalyZone>(true).FirstOrDefault();
        Transform forge = forgeZone != null ? forgeZone.transform : null;
        if (forge != null) Undo.SetTransformParent(forge, world, "Preserve Lumenforge");

        foreach (var child in world.Cast<Transform>().ToList())
        {
            if (child == rig || child == forge) continue;
            Undo.DestroyObjectImmediate(child.gameObject);
        }

        rng = new System.Random(1717);
        var act1 = BuildGrandGateway(world);
        var act2 = BuildMysteryCave(world);
        var act3 = BuildLumenforge(world, forge);
        var acts = new[] { act1, act2, act3 };

        foreach (var act in acts) FinishAct(act);

        if (rig != null)
        {
            Undo.RecordObject(rig, "Place Petalo");
            rig.localPosition = act1.transform.localPosition + new Vector3(0f, SpawnLift, -3f);
            rig.localRotation = Quaternion.identity;
        }

        ConfigureWorld(root);
        var director = ConfigureSceneSystems(scene, root, rig, acts);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Report(root, rig, forge, director);
    }

    // ==============================================================================================
    // Act 1 - THE GRAND GATEWAY
    // ==============================================================================================
    static HybridAct BuildGrandGateway(Transform world)
    {
        var act = Node(world, "Act1_GrandGateway", new Vector3(0f, 0f, 0f));
        var s = act.transform;

        // Spawn plaza.
        FloorGrid(s, new[] { -4f, 0f, 4f }, -4f, 4f, 0f);
        Place("Urn", s, new Vector3(-5.2f, 0f, -5.2f));
        Place("Urn", s, new Vector3(5.2f, 0f, -5.2f));

        // The long ruined colonnade: a causeway under a broken portico, with fallen shafts to slalom between.
        FloorGrid(s, new[] { -2f, 2f }, 8f, 60f, 0f);
        Colonnade(s, 0f, 8f, 58f, 3.25f, 0f, 0.3f, roof: true);
        FallenColumn(s, new Vector3(1.6f, 0f, 20f));
        FallenColumn(s, new Vector3(-1.6f, 0f, 38f));
        Place("Urn", s, new Vector3(-1.6f, 0f, 30f));

        // Grand staircase: two flights of ten with a landing, rising 8.4 m to the gate plaza.
        float top = Flight(s, new[] { -4f, 0f, 4f }, 62f, 10, 0f, +1);
        Cheeks(s, 6.35f, 62f, 73f, top);
        FloorGrid(s, new[] { -4f, 0f, 4f }, 75f, 79f, top);
        Retain(s, -6f, 6f, 73f, 81f, top);
        Place("Urn", s, new Vector3(-5f, top, 77f));
        Place("Urn", s, new Vector3(5f, top, 77f));
        float plaza = Flight(s, new[] { -4f, 0f, 4f }, 81f, 10, top, +1);
        Cheeks(s, 6.35f, 81f, 92f, plaza);
        StairPosts(s, 5.6f, 62f, 10, 0f);
        StairPosts(s, 5.6f, 81f, 10, top);

        // Gate plaza and the Gateway itself.
        FloorGrid(s, new[] { -8f, -4f, 0f, 4f, 8f }, 94f, 110f, plaza);
        Retain(s, -10f, 10f, 92f, 112f, plaza);
        const float gateZ = 102f;
        const float pierScale = 1.6f;
        float pierBase = plaza + PedestalH * pierScale;
        foreach (float x in new[] { -7.5f, 7.5f })
        {
            Place("Pedestal", s, new Vector3(x, plaza + PedestalH * pierScale * 0.5f, gateZ), 0f, pierScale);
            Place("Column_Doric", s, new Vector3(x, pierBase, gateZ), 0f, pierScale);
        }
        float pierTop = pierBase + ColumnH * pierScale;

        // Five voussoirs on a semicircle. Built at rest, recorded, then flung into their broken poses.
        var gate = GateNode(s, "Gate_GrandGateway", new Vector3(0f, plaza + 2f, gateZ - 5f), new Vector3(12f, 4f, 5f),
                            "king of spades", "KEYSTONE", "King of Spades");
        var archRoot = new GameObject("Arch_Segments");
        Undo.RegisterCreatedObjectUndo(archRoot, "Arch");
        archRoot.transform.SetParent(s, false);

        const int segments = 5;
        const float radius = 8.2f;
        const float segmentScale = 1.2f;
        var pieces = new Transform[segments];
        var rest = new Vector3[segments];
        var restRotation = new Quaternion[segments];
        for (int i = 0; i < segments; i++)
        {
            float theta = 180f - (i + 0.5f) * (180f / segments);
            float r = theta * Mathf.Deg2Rad;
            var p = new Vector3(Mathf.Cos(r) * radius, pierTop + Mathf.Sin(r) * radius, gateZ);
            var q = Quaternion.Euler(0f, 0f, theta + 90f);
            var seg = Place("Entablature_Span", archRoot.transform, p, q, segmentScale);
            StripColliders(seg);
            seg.name = i == segments / 2 ? "Voussoir_Keystone" : $"Voussoir_{i}";

            pieces[i] = seg.transform;
            rest[i] = p;
            restRotation[i] = q;

            // Hanging in mid-air, held by residual energy: pushed out along the arc's normal and up, and
            // tumbled. The crown drifts highest.
            Vector3 outward = new Vector3(Mathf.Cos(r), Mathf.Sin(r), 0f);
            float lift = 2.5f + (i == segments / 2 ? 4f : Rand(0f, 2.5f));
            seg.transform.localPosition = p + outward * Rand(1.5f, 3.5f) + Vector3.up * lift +
                                          new Vector3(0f, 0f, Rand(-2.5f, 2.5f));
            seg.transform.localRotation = q * Quaternion.Euler(Rand(-20f, 20f), Rand(-25f, 25f), Rand(-30f, 30f));
        }
        var arch = Undo.AddComponent<ArchRestoreReaction>(gate.gameObject);
        arch.Configure(pieces, rest, restRotation);
        EditorUtility.SetDirty(arch);

        var barrier = Barrier(s, new Vector3(0f, plaza + 1.5f, gateZ), new Vector3(13f, 3f, 1.5f));
        WireGate(gate, barrier, arch);

        // Beyond the gate: the road into the dark, and the act's exit.
        FloorGrid(s, new[] { -2f, 2f }, 114f, 122f, plaza);
        Retain(s, -4f, 4f, 112f, 124f, plaza);
        Place("Urn", s, new Vector3(-3.2f, plaza, 116f));
        Place("Urn", s, new Vector3(3.2f, plaza, 116f));
        Exit(s, new Vector3(0f, plaza + 2f, 121f), new Vector3(8f, 4f, 4f));

        return Act(act, 1, "THE GRAND GATEWAY", "The first light of Aethyra waits beyond the arch",
                   new Vector3(0f, SpawnLift, -3f), gate, "Reach the Grand Gateway", "Pass through the Gateway", false);
    }

    // ==============================================================================================
    // Act 2 - THE MYSTERY CAVE
    // ==============================================================================================
    static HybridAct BuildMysteryCave(Transform world)
    {
        var act = Node(world, "Act2_MysteryCave", new Vector3(160f, 0f, 0f));
        var s = act.transform;

        // High landing, then a grand descent of fifteen steps into the dark.
        const float landing = 6.3f;
        FloorGrid(s, new[] { -4f, 0f, 4f }, -4f, 0f, landing);
        Retain(s, -6f, 6f, -6f, 2f, landing);
        float floor = Flight(s, new[] { -4f, 0f, 4f }, 2f, 15, landing, -1);
        Cheeks(s, 6.35f, 2f, 18.5f, landing);

        // The hypostyle hall: a forest of columns, a broken roof, and rubble that forces a weave.
        FloorGrid(s, new[] { -8f, -4f, 0f, 4f, 8f }, 20.5f, 64.5f, floor);
        for (float z = 22f; z <= 64.1f; z += 6f)
        {
            foreach (float x in new[] { -8.6f, -3.4f, 3.4f, 8.6f })
            {
                double roll = rng.NextDouble();
                if (roll < 0.12) BrokenPillar(s, new Vector3(x, floor, z));
                else
                {
                    Place("Column_Doric", s, new Vector3(x, floor, z));
                    if (roll > 0.3) Place("Entablature_Span", s, new Vector3(x, floor + ColumnH, z), 90f);
                }
            }
        }
        Walls(s, 10.35f, 18.5f, 66.5f, floor, 2);

        FallenSpan(s, new Vector3(0f, floor, 31f), 0f);
        FallenSpan(s, new Vector3(-6f, floor, 43f), 0f);
        FallenSpan(s, new Vector3(6f, floor, 43f), 0f);
        FallenSpan(s, new Vector3(0f, floor, 55f), 0f);
        BrokenPillar(s, new Vector3(-1f, floor, 47f));
        Place("Urn", s, new Vector3(6f, floor, 26f));
        Place("Urn", s, new Vector3(-6f, floor, 36f));
        Place("Urn", s, new Vector3(1.5f, floor, 60f));

        // The vault: a sealed wall with a pair of doors.
        const float doorZ = 68.5f;
        FloorGrid(s, new[] { -4f, 0f, 4f }, doorZ, doorZ, floor);
        const float frameScale = 1.3f;
        foreach (float x in new[] { -5f, 5f }) Place("Column_Doric", s, new Vector3(x, floor, doorZ), 0f, frameScale);
        float lintelY = floor + ColumnH * frameScale;
        foreach (float x in new[] { -5.2f, 0f, 5.2f })
            Place("Entablature_Span", s, new Vector3(x, lintelY, doorZ), Quaternion.identity, frameScale);
        foreach (float x in new[] { -8f, 8f })
            for (int k = 0; k < 3; k++)
                StripColliders(Place("Stylobate_Block", s, new Vector3(x, floor + 2f + 4f * k, doorZ), Quaternion.Euler(90f, 0f, 0f)));

        var gate = GateNode(s, "Gate_VaultDoors", new Vector3(0f, floor + 2f, doorZ - 4f), new Vector3(14f, 4f, 4f),
                            // The reference image file is misnamed; the printed card is the Queen of Diamonds.
                            "queen_of_hearts", "ANCHOR", "Queen of Diamonds");
        var hinges = new Transform[2];
        for (int side = 0; side < 2; side++)
        {
            float hingeX = side == 0 ? -4f : 4f;
            var hinge = new GameObject(side == 0 ? "Door_Hinge_L" : "Door_Hinge_R");
            Undo.RegisterCreatedObjectUndo(hinge, "Hinge");
            hinge.transform.SetParent(s, false);
            hinge.transform.localPosition = new Vector3(hingeX, floor, doorZ);
            for (int k = 0; k < 3; k++)
            {
                var leaf = Place("Stylobate_Block", hinge.transform, new Vector3(side == 0 ? 2f : -2f, 2f + 4f * k, 0f),
                                 Quaternion.Euler(90f, 0f, 0f));
                StripColliders(leaf);
            }
            hinges[side] = hinge.transform;
        }
        var doors = Undo.AddComponent<DoorOpenReaction>(gate.gameObject);
        doors.Configure(hinges, new[] { -100f, 100f });
        EditorUtility.SetDirty(doors);

        var barrier = Barrier(s, new Vector3(0f, floor + 1.5f, doorZ), new Vector3(8.5f, 3f, 1.5f));
        WireGate(gate, barrier, doors);

        // Corridor beyond the doors, and the exit.
        FloorGrid(s, new[] { -2f, 2f }, 72.5f, 84.5f, floor);
        Place("Urn", s, new Vector3(-3.2f, floor, 76f));
        Place("Urn", s, new Vector3(3.2f, floor, 76f));
        Exit(s, new Vector3(0f, floor + 2f, 83.5f), new Vector3(8f, 4f, 4f));

        return Act(act, 2, "THE MYSTERY CAVE", "Where Aethyra hid its light from the dark",
                   new Vector3(0f, landing + SpawnLift, -3f), gate, "Cross the hall to the Vault", "Enter the Vault", false);
    }

    // ==============================================================================================
    // Act 3 - THE LUMENFORGE
    // ==============================================================================================
    static HybridAct BuildLumenforge(Transform world, Transform forge)
    {
        var act = Node(world, "Act3_Lumenforge", new Vector3(320f, 0f, 0f));
        var s = act.transform;

        // Spawn plaza and a colonnaded approach.
        FloorGrid(s, new[] { -4f, 0f, 4f }, -4f, 0f, 0f);
        FloorGrid(s, new[] { -2f, 2f }, 4f, 16f, 0f);
        Colonnade(s, 0f, 4f, 16f, 3.25f, 0f, 0.25f, roof: true);

        // First flight north, then a landing and a switchback east.
        float top = Flight(s, new[] { -4f, 0f, 4f }, 18f, 10, 0f, +1);
        Cheeks(s, 6.35f, 18f, 29f, top);
        FloorGrid(s, new[] { -4f, 0f, 4f, 8f, 12f }, 31f, 35f, top);
        Retain(s, -6f, 14f, 29f, 37f, top);
        float deck = FlightEast(s, new[] { 31f, 35f }, 14f, 10, top);
        FloorGrid(s, new[] { 27f, 31f }, 31f, 35f, deck);
        Retain(s, 25f, 33f, 29f, 37f, deck);

        // The viaduct: a covered walkway with two broken spans, carried on piers from the ground.
        var broken = new HashSet<int> { 3, 6 };
        for (int i = 0; i < 10; i++)
        {
            float z = 39f + i * Slab;
            Floor(s, 27f, deck, z);
            if (!broken.Contains(i))
            {
                Floor(s, 31f, deck, z);
                Place("Balustrade_Post", s, new Vector3(32.6f, deck, z));
                Place("Balustrade_Rail", s, new Vector3(32.6f, deck + 1.2f, z + Slab * 0.5f), 90f);
            }
            Place("Balustrade_Post", s, new Vector3(25.4f, deck, z));
            Place("Balustrade_Rail", s, new Vector3(25.4f, deck + 1.2f, z + Slab * 0.5f), 90f);

            if (i % 3 == 0)
            {
                float pierScale = (deck - 2f * SlabHalf) / ColumnH;
                foreach (float x in new[] { 27f, 31f })
                    StripColliders(Place("Column_Doric", s, new Vector3(x, 0f, z), 0f, pierScale));
            }
            if (i % 2 == 0)
            {
                Place("Column_Doric", s, new Vector3(25.8f, deck, z));
                if (!broken.Contains(i)) Place("Column_Doric", s, new Vector3(32.2f, deck, z));
                Place("Entablature_Span", s, new Vector3(25.8f, deck + ColumnH, z), 90f);
                if (!broken.Contains(i)) Place("Entablature_Span", s, new Vector3(32.2f, deck + ColumnH, z), 90f);
            }
        }
        // Rubble on the intact (east) half of span 2, so the lane past it is the full west slab. Placed centrally
        // it left under a metre beside the gallery columns - narrower than Petalo.
        BrokenPillar(s, new Vector3(30.6f, deck, 45.2f));
        Place("Urn", s, new Vector3(26.2f, deck, 39f + 5 * Slab));

        // The Tholos: three steps up, a round platform, a half ring of columns around the forge.
        const float centreX = 29f, centreZ = 90f;
        float sanctum = Flight(s, new[] { 21f, 25f, 29f, 33f, 37f }, 77f, 3, deck, +1);
        for (int x = -2; x <= 2; x++)
            for (int z = -2; z <= 2; z++)
            {
                var p = new Vector2(x * Slab, z * Slab);
                if (p.magnitude > 10.5f) continue;
                Floor(s, centreX + p.x, sanctum, centreZ + p.y);
            }
        const int ring = 11;
        for (int i = 0; i < ring; i++)
        {
            float a = Mathf.Lerp(15f, 165f, (float)i / (ring - 1)) * Mathf.Deg2Rad;
            var p = new Vector3(centreX + Mathf.Cos(a) * 8.5f, sanctum, centreZ + Mathf.Sin(a) * 8.5f);
            Place("Column_Doric", s, p);
            Place("Entablature_Span", s, new Vector3(p.x, sanctum + ColumnH, p.z),
                  Mathf.Atan2(p.x - centreX, p.z - centreZ) * Mathf.Rad2Deg + 90f);
        }
        float supportScale = (sanctum - 2f * SlabHalf) / ColumnH;
        for (int i = 0; i < 6; i++)
        {
            float a = i * 60f * Mathf.Deg2Rad;
            StripColliders(Place("Column_Doric", s,
                new Vector3(centreX + Mathf.Cos(a) * 7f, 0f, centreZ + Mathf.Sin(a) * 7f), 0f, supportScale));
        }

        // The Lumenforge on its altar. Its AnomalyZone stays disarmed until the card is shown.
        AnomalyZone zone = null;
        if (forge != null)
        {
            Undo.SetTransformParent(forge, s, "Home Lumenforge");
            forge.localPosition = new Vector3(centreX, sanctum, centreZ);
            forge.localRotation = Quaternion.identity;
            forge.localScale = Vector3.one;
            foreach (var child in forge.Cast<Transform>().ToList()) Undo.DestroyObjectImmediate(child.gameObject);
            Place("Pedestal", forge, new Vector3(0f, PedestalH * 0.5f, 0f));
            Place("Pedestal", forge, new Vector3(0f, PedestalH * 1.5f, 0f), 45f);
            Place("Urn", forge, new Vector3(0f, PedestalH * 2f, 0f));

            // AnomalyZone requires a Collider, so the box is secured first and only the others are removed.
            var trigger = forge.GetComponent<BoxCollider>();
            if (trigger == null) trigger = Undo.AddComponent<BoxCollider>(forge.gameObject);
            foreach (var c in forge.GetComponents<Collider>())
                if (c != trigger) Undo.DestroyObjectImmediate(c);
            Undo.RecordObject(trigger, "Forge trigger");
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 2f, 0f);
            trigger.size = new Vector3(7f, 4f, 7f);
            zone = forge.GetComponent<AnomalyZone>();
        }

        var gate = GateNode(s, "Gate_Lumenforge", new Vector3(centreX, deck + 2f, 79f), new Vector3(14f, 4f, 5f),
                            "all_knowing_eye", "PRISM", "The All-Knowing Eye");
        var ignite = Undo.AddComponent<ForgeIgniteReaction>(gate.gameObject);
        ignite.Configure(zone);
        EditorUtility.SetDirty(ignite);
        WireGate(gate, null, ignite);

        return Act(act, 3, "THE LUMENFORGE", "The machine that once lit a civilisation",
                   new Vector3(0f, SpawnLift, -3f), gate, "Climb to the Tholos", "Drive Petalo into the Lumenforge", true);
    }

    // ==============================================================================================
    // Layout primitives (all in design metres, parent-local)
    // ==============================================================================================
    static void Floor(Transform p, float x, float top, float z) =>
        Place("Stylobate_Block", p, new Vector3(x, top - SlabHalf, z));

    static void FloorGrid(Transform p, float[] xs, float zFrom, float zTo, float top)
    {
        for (float z = zFrom; z <= zTo + 0.01f; z += Slab)
            foreach (float x in xs) Floor(p, x, top, z);
    }

    /// <summary>A flight climbing (+1) or descending (-1) along +Z. Returns the height at its far end.</summary>
    static float Flight(Transform p, float[] xs, float zStart, int steps, float startTop, int direction)
    {
        for (int i = 0; i < steps; i++)
        {
            float top = startTop + (i + 1) * Tread * direction;
            float z = zStart + i * TreadDepth + TreadDepth * 0.5f;
            foreach (float x in xs) Place("Stair_Tread", p, new Vector3(x, top - Tread * 0.5f, z));
        }
        return startTop + steps * Tread * direction;
    }

    /// <summary>A flight climbing along +X, treads turned 90 degrees.</summary>
    static float FlightEast(Transform p, float[] zs, float xStart, int steps, float startTop)
    {
        for (int i = 0; i < steps; i++)
        {
            float top = startTop + (i + 1) * Tread;
            float x = xStart + i * TreadDepth + TreadDepth * 0.5f;
            foreach (float z in zs) Place("Stair_Tread", p, new Vector3(x, top - Tread * 0.5f, z), 90f);
        }
        float end = startTop + steps * Tread;
        float xEnd = xStart + steps * TreadDepth;
        foreach (float z in new[] { zs.Min() - 2.35f, zs.Max() + 2.35f })
            for (float x = xStart + 2f; x < xEnd + 0.01f; x += Slab)
                StackWall(p, new Vector3(x, 0f, z), Quaternion.Euler(90f, 0f, 0f), Mathf.Lerp(startTop, end, (x - xStart) / (xEnd - xStart)));
        return end;
    }

    /// <summary>Solid side walls ("cheeks") under a flight along +Z, stepping with it.</summary>
    static void Cheeks(Transform p, float halfWidth, float zFrom, float zTo, float maxTop)
    {
        for (float z = zFrom + 2f; z < zTo + 0.01f; z += Slab)
            foreach (float x in new[] { -halfWidth, halfWidth })
                StackWall(p, new Vector3(x, 0f, z), Quaternion.Euler(0f, 0f, 90f), maxTop);
    }

    /// <summary>Retaining walls around a raised deck, so it stands on the floor instead of floating over it.</summary>
    static void Retain(Transform p, float x0, float x1, float z0, float z1, float top)
    {
        for (float z = z0 + 2f; z < z1; z += Slab)
        {
            StackWall(p, new Vector3(x0 - SlabHalf, 0f, z), Quaternion.Euler(0f, 0f, 90f), top);
            StackWall(p, new Vector3(x1 + SlabHalf, 0f, z), Quaternion.Euler(0f, 0f, 90f), top);
        }
        for (float x = x0 + 2f; x < x1; x += Slab)
            StackWall(p, new Vector3(x, 0f, z1 + SlabHalf), Quaternion.Euler(90f, 0f, 0f), top);
    }

    /// <summary>Enclosing walls along both sides of a hall, <paramref name="layers"/> blocks (4 m) high.</summary>
    static void Walls(Transform p, float halfWidth, float zFrom, float zTo, float floor, int layers)
    {
        for (float z = zFrom + 2f; z < zTo; z += Slab)
            foreach (float x in new[] { -halfWidth, halfWidth })
                for (int k = 0; k < layers; k++)
                    StripColliders(Place("Stylobate_Block", p, new Vector3(x, floor + 2f + 4f * k, z), Quaternion.Euler(0f, 0f, 90f)));
    }

    /// <summary>
    /// Vertical slabs stacked from the ground to just under <paramref name="top"/>. Visual only: Petalo's ledge
    /// clamp already keeps it on the deck, and a wall top it could stand on would only strand it.
    /// </summary>
    static void StackWall(Transform p, Vector3 foot, Quaternion rotation, float top)
    {
        // Each layer is 4 m; a layer is only laid if it tops out at or under the deck, never through it.
        for (float y = 2f; y + 2f <= top + 0.5f; y += Slab)
            StripColliders(Place("Stylobate_Block", p, new Vector3(foot.x, foot.y + y, foot.z), rotation));
    }

    static void StairPosts(Transform p, float halfWidth, float zStart, int steps, float startTop)
    {
        for (int i = 1; i < steps; i += 2)
        {
            float top = startTop + (i + 1) * Tread;
            float z = zStart + i * TreadDepth + TreadDepth * 0.5f;
            Place("Balustrade_Post", p, new Vector3(-halfWidth, top, z));
            Place("Balustrade_Post", p, new Vector3(halfWidth, top, z));
        }
    }

    /// <summary>Columns down both sides of a walkway, some fallen to stumps, with the entablature where it survives.</summary>
    static void Colonnade(Transform p, float centreX, float zFrom, float zTo, float halfWidth, float top, float ruin, bool roof)
    {
        for (float z = zFrom; z <= zTo + 0.01f; z += 4.2f)
            foreach (float side in new[] { -1f, 1f })
            {
                var at = new Vector3(centreX + side * halfWidth, top, z);
                double roll = rng.NextDouble();
                if (roll < ruin * 0.4) continue;                        // gone entirely
                if (roll < ruin) { BrokenPillar(p, at); continue; }      // a stump on its plinth
                Place("Column_Doric", p, at);
                if (roof && rng.NextDouble() > 0.2) Place("Entablature_Span", p, at + Vector3.up * ColumnH, 90f);
            }
    }

    static void BrokenPillar(Transform p, Vector3 at)
    {
        Place("Pedestal", p, at + Vector3.up * (PedestalH * 0.5f));
        Place("Column_Doric", p, at + Vector3.up * PedestalH, (float)rng.NextDouble() * 360f, 0.26f);
    }

    /// <summary>A whole shaft lying along the walkway: 1.6 m high, too tall to climb, so it is steered around.</summary>
    static void FallenColumn(Transform p, Vector3 at) =>
        Place("Column_Doric", p, at + Vector3.up * 0.8f, Quaternion.Euler(90f, Rand(-6f, 6f), 0f));

    static void FallenSpan(Transform p, Vector3 at, float yaw) =>
        Place("Entablature_Span", p, at, Quaternion.Euler(0f, yaw + Rand(-8f, 8f), Rand(-4f, 4f)));

    // ==============================================================================================
    // Gates, exits, acts
    // ==============================================================================================
    static CardGate GateNode(Transform act, string name, Vector3 centre, Vector3 size, string card, string role, string display)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Gate");
        go.transform.SetParent(act, false);
        go.transform.localPosition = centre;
        var box = Undo.AddComponent<BoxCollider>(go);
        box.isTrigger = true;
        box.size = size;

        var gate = Undo.AddComponent<CardGate>(go);
        var so = new SerializedObject(gate);
        so.FindProperty("requiredCard").stringValue = card;
        so.FindProperty("cardRole").stringValue = role;
        so.FindProperty("cardDisplayName").stringValue = display;
        so.ApplyModifiedPropertiesWithoutUndo();
        return gate;
    }

    static void WireGate(CardGate gate, Collider barrier, GateReaction reaction)
    {
        var so = new SerializedObject(gate);
        so.FindProperty("barrier").objectReferenceValue = barrier;
        so.FindProperty("reaction").objectReferenceValue = reaction;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>An invisible wall. Its top is a 3 m rise, which the pilot's ground probes refuse to climb.</summary>
    static Collider Barrier(Transform act, Vector3 centre, Vector3 size)
    {
        var go = new GameObject("Gate_Barrier");
        Undo.RegisterCreatedObjectUndo(go, "Barrier");
        go.transform.SetParent(act, false);
        go.transform.localPosition = centre;
        var box = Undo.AddComponent<BoxCollider>(go);
        box.size = size;
        return box;
    }

    static void Exit(Transform act, Vector3 centre, Vector3 size)
    {
        var go = new GameObject("Act_Exit");
        Undo.RegisterCreatedObjectUndo(go, "Exit");
        go.transform.SetParent(act, false);
        go.transform.localPosition = centre;
        var box = Undo.AddComponent<BoxCollider>(go);
        box.isTrigger = true;
        box.size = size;
        Undo.AddComponent<ActExit>(go);
    }

    static HybridAct Act(GameObject go, int number, string title, string subtitle, Vector3 spawn, CardGate gate,
                         string approach, string exit, bool finale)
    {
        var spawnPoint = new GameObject("Spawn");
        Undo.RegisterCreatedObjectUndo(spawnPoint, "Spawn");
        spawnPoint.transform.SetParent(go.transform, false);
        spawnPoint.transform.localPosition = spawn;

        var act = Undo.AddComponent<HybridAct>(go);
        var so = new SerializedObject(act);
        so.FindProperty("actNumber").intValue = number;
        so.FindProperty("title").stringValue = title;
        so.FindProperty("subtitle").stringValue = subtitle;
        so.FindProperty("spawnPoint").objectReferenceValue = spawnPoint.transform;
        so.FindProperty("gate").objectReferenceValue = gate;
        so.FindProperty("approachObjective").stringValue = approach;
        so.FindProperty("exitObjective").stringValue = exit;
        so.FindProperty("finale").boolValue = finale;
        so.FindProperty("architectureMaterial").objectReferenceValue = darkMarble;
        so.ApplyModifiedPropertiesWithoutUndo();
        return act;
    }

    /// <summary>
    /// Dark material on every renderer, no shadows (there is no key light to cast them), and no static flags:
    /// the world root is moved at runtime to place each act, and static-batched geometry would stay behind.
    /// </summary>
    static void FinishAct(HybridAct act)
    {
        foreach (var r in act.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = darkMarble;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }
        foreach (var t in act.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);

        // The kit culls below 2% of screen height, which was tuned for a table. At room scale an urn is 4 cm and a
        // floor slab 12 cm, so the far end of an act would lose its floor a few metres from the phone. The
        // detail switches stay where they were; only the cull moves down to "sub-pixel".
        foreach (var lodGroup in act.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lodGroup.GetLODs();
            if (lods.Length == 0) continue;
            lods[lods.Length - 1].screenRelativeTransitionHeight = RoomScaleCull;
            lodGroup.SetLODs(lods);
        }
    }

    const float RoomScaleCull = 0.003f;

    // ==============================================================================================
    // Scene systems
    // ==============================================================================================
    static void ConfigureWorld(GameObject root)
    {
        var anchored = root.GetComponent<PlaneAnchoredWorld>();
        if (anchored == null) return;
        var so = new SerializedObject(anchored);
        so.FindProperty("fitMode").enumValueIndex = (int)PlaneAnchoredWorld.FitMode.RoomScale;
        so.FindProperty("roomScale").floatValue = RoomScale;
        so.FindProperty("sizeMultiplier").floatValue = 1f;
        so.ApplyModifiedProperties();

        Undo.RecordObject(root.transform, "World scale");
        root.transform.localScale = Vector3.one;
    }

    static HybridLoopDirector ConfigureSceneSystems(Scene scene, GameObject root, Transform rig, HybridAct[] acts)
    {
        var roots = scene.GetRootGameObjects();

        // The old single-card puzzle and card-anchored diorama both react to the King of Spades - which is now
        // the Act 1 key. Left on, scanning the gate card would also pop a diorama onto it and drive the swarm's
        // colour from a rotation puzzle that no longer exists.
        foreach (var name in new[] { "MysteryCave_Anchor", "Lumenforge_Puzzle" })
        {
            var go = roots.FirstOrDefault(g => g.name == name);
            if (go != null && go.activeSelf)
            {
                Undo.RecordObject(go, "Retire " + name);
                go.SetActive(false);
            }
        }

        // Dark AR: no key light (it is also the shadow catcher's only source, so that goes too).
        var keyLight = roots.FirstOrDefault(g => g.name == "Key_Light");
        foreach (var catcher in Object.FindObjectsByType<ARShadowCatcher>(FindObjectsInactive.Include))
        {
            Undo.RecordObject(catcher, "Disable shadow catcher");
            catcher.enabled = false;
        }
        foreach (var diag in Object.FindObjectsByType<SwarmMatrixDiagnostics>(FindObjectsInactive.Include))
        {
            var so = new SerializedObject(diag);
            so.FindProperty("showOnStart").boolValue = false;
            so.ApplyModifiedProperties();
        }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0f;
        RenderSettings.reflectionIntensity = 0f;

        // Camera: AR depth for the swarm transition.
        var cam = Camera.main;
        SwarmDepthField depthField = null;
        if (cam != null)
        {
            var occlusion = cam.GetComponent<AROcclusionManager>();
            if (occlusion == null) occlusion = Undo.AddComponent<AROcclusionManager>(cam.gameObject);
            occlusion.enabled = false;
            // Depth feeds the swarm only; the camera background must never occlude the act with it.
            var oso = new SerializedObject(occlusion);
            oso.FindProperty("m_OcclusionPreferenceMode").enumValueIndex = (int)UnityEngine.XR.ARSubsystems.OcclusionPreferenceMode.NoOcclusion;
            oso.ApplyModifiedPropertiesWithoutUndo();
            depthField = cam.GetComponent<SwarmDepthField>();
            if (depthField == null) depthField = Undo.AddComponent<SwarmDepthField>(cam.gameObject);
        }

        // The loop itself.
        var loop = roots.FirstOrDefault(g => g.name == "Hybrid_Loop");
        if (loop == null)
        {
            loop = new GameObject("Hybrid_Loop");
            Undo.RegisterCreatedObjectUndo(loop, "Hybrid Loop");
        }
        // Explicit null checks: a missing component is a fake-null object in the Editor, which '??' lets through.
        var director = loop.GetComponent<HybridLoopDirector>();
        if (director == null) director = Undo.AddComponent<HybridLoopDirector>(loop);
        var ui = loop.GetComponent<HybridLoopUI>();
        if (ui == null) ui = Undo.AddComponent<HybridLoopUI>(loop);
        var dark = loop.GetComponent<DarkARLighting>();
        if (dark == null) dark = Undo.AddComponent<DarkARLighting>(loop);

        var d = new SerializedObject(director);
        d.FindProperty("world").objectReferenceValue = root.GetComponent<PlaneAnchoredWorld>();
        d.FindProperty("pilot").objectReferenceValue = rig != null ? rig.GetComponent<PetaloPilot>() : null;
        d.FindProperty("petalo").objectReferenceValue = rig != null ? rig.GetComponentInChildren<PetaloBeacon>(true) : null;
        d.FindProperty("swarm").objectReferenceValue = Object.FindAnyObjectByType<SwarmGPUArchitect>();
        d.FindProperty("tether").objectReferenceValue = rig != null ? rig.GetComponent<SwarmTether>() : null;
        d.FindProperty("trackedImages").objectReferenceValue = Object.FindAnyObjectByType<ARTrackedImageManager>();
        d.FindProperty("ui").objectReferenceValue = ui;
        d.FindProperty("header").objectReferenceValue = Object.FindAnyObjectByType<MissionHeader>();
        d.FindProperty("depthField").objectReferenceValue = depthField;
        d.FindProperty("viewCamera").objectReferenceValue = cam;
        var list = d.FindProperty("acts");
        list.arraySize = acts.Length;
        for (int i = 0; i < acts.Length; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = acts[i];
        d.ApplyModifiedPropertiesWithoutUndo();

        var l = new SerializedObject(dark);
        var lights = l.FindProperty("disabledLights");
        lights.arraySize = keyLight != null ? 1 : 0;
        if (keyLight != null) lights.GetArrayElementAtIndex(0).objectReferenceValue = keyLight.GetComponent<Light>();
        l.ApplyModifiedPropertiesWithoutUndo();

        return director;
    }

    // ==============================================================================================
    // Materials and placement
    // ==============================================================================================
    static Material BuildDarkMarble()
    {
        var shader = Shader.Find("MAAYAI/SwarmSurface");
        var source = AssetDatabase.LoadAssetAtPath<Material>(MarblePath);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(DarkMarblePath);
        if (mat == null)
        {
            mat = new Material(shader) { name = "Marble_DarkAR" };
            AssetDatabase.CreateAsset(mat, DarkMarblePath);
        }
        mat.shader = shader;
        if (source != null)
        {
            mat.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
            mat.SetTextureScale("_BaseMap", source.GetTextureScale("_BaseMap"));
            mat.SetColor("_BaseColor", source.GetColor("_BaseColor"));
        }
        mat.SetFloat("_KeyLightGain", 0f);      // Dark AR: the swarm and Petalo are the only light
        mat.SetFloat("_ShadowStrength", 0f);
        mat.SetFloat("_Wrap", 0.25f);
        mat.SetFloat("_Smoothness", 0.4f);
        mat.SetFloat("_SpecularStrength", 0.3f);
        mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
        mat.SetFloat("_RenderMode", 0f);
        mat.SetFloat("_SrcBlend", 1f);
        mat.SetFloat("_DstBlend", 0f);
        mat.SetFloat("_ZWrite", 1f);
        mat.SetFloat("_Dissolve", 0f);
        mat.SetFloat("_DissolveEdge", 0.08f);
        mat.SetColor("_DissolveEdgeColor", new Color(0.3f, 1.6f, 2.4f, 1f));
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static GameObject Place(string piece, Transform parent, Vector3 local, float yaw = 0f, float scale = 1f) =>
        Place(piece, parent, local, Quaternion.Euler(0f, yaw, 0f), scale);

    static GameObject Place(string piece, Transform parent, Vector3 local, Quaternion rotation, float scale = 1f)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(Kit[piece], parent);
        Undo.RegisterCreatedObjectUndo(instance, "Place " + piece);
        instance.transform.localPosition = local;
        instance.transform.localRotation = rotation;
        instance.transform.localScale = Vector3.one * scale;
        AddCollider(instance);
        return instance;
    }

    /// <summary>Same collider policy as the circuit: convex hulls for lathe pieces, tight boxes for the rest.</summary>
    static void AddCollider(GameObject instance)
    {
        var lod0 = instance.GetComponentInChildren<MeshFilter>();
        if (lod0 == null || lod0.sharedMesh == null) return;

        string n = instance.name;
        bool round = n.StartsWith("Column") || n.StartsWith("Urn") || n.StartsWith("Balustrade_Post");
        if (round)
        {
            Mesh hull = lod0.sharedMesh;
            var group = instance.GetComponent<LODGroup>();
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

    static void StripColliders(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Undo.DestroyObjectImmediate(c);
    }

    static GameObject Node(Transform parent, string name, Vector3 local)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        return go;
    }

    static float Rand(float min, float max) => (float)(min + rng.NextDouble() * (max - min));

    static bool LoadKit()
    {
        Kit.Clear();
        foreach (var n in new[] { "Column_Doric", "Stair_Tread", "Stylobate_Block", "Entablature_Span",
                                  "Balustrade_Post", "Balustrade_Rail", "Urn", "Pedestal" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{KitFolder}/{n}.prefab");
            if (prefab == null)
            {
                Debug.LogError($"[HybridLoop] Missing kit piece '{n}'. Run Funobotz/Roman Kit/1. Generate Kit first.");
                return false;
            }
            Kit[n] = prefab;
        }
        return true;
    }

    static void Report(GameObject root, Transform rig, Transform forge, HybridLoopDirector director)
    {
        foreach (var act in root.GetComponentsInChildren<HybridAct>(true))
        {
            var renderers = act.GetComponentsInChildren<MeshRenderer>(true);
            Bounds b = renderers.Length > 0 ? renderers[0].bounds : new Bounds();
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            Debug.Log($"[HybridLoop] Act {act.ActNumber} '{act.Title}': {act.GetComponentsInChildren<LODGroup>(true).Length} pieces, " +
                      $"{b.size.x:0} x {b.size.z:0} design m ({b.size.x * RoomScale:0.0} x {b.size.z * RoomScale:0.0} m in the room), " +
                      $"{b.size.y:0.0} m tall.");
        }
        Debug.Log($"[HybridLoop] Firewall: Petalo_Rig {(rig != null ? "PRESERVED" : "MISSING")}, " +
                  $"Lumenforge {(forge != null ? "PRESERVED" : "MISSING")}. Director {(director != null ? "wired" : "MISSING")}.");
    }
}
