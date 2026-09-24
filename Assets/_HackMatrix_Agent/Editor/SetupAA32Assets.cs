using System.Linq;
using MAAYAI.HackMatrix;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One click from a fresh checkout to the AA-32 scene: creates the URP Unlit materials (HDR colours, so the
/// existing bloom makes them glow), copies the demo scene to AA32_DroneAgent.unity, wires the agent into it,
/// and assigns the materials and built-in primitive meshes to the compiler. Idempotent.
///
/// Unlit rather than Lit + emission on purpose: Unity strips the emission variant from a build unless a saved
/// material uses it, and Unlit ignores scene lighting, so the zones cannot turn black under the dark AR setup.
/// </summary>
public static partial class SetupAA32Assets
{
    const string MaterialFolder = "Assets/Materials";
    const string SourceScene = "Assets/Scenes/HackMatrix_Agentic.unity";
    const string TargetScene = "Assets/Scenes/AA32_DroneAgent.unity";
    const string UnlitShader = "Universal Render Pipeline/Unlit";
    const string LitShader = "Universal Render Pipeline/Lit";

    [MenuItem("Hack Matrix/AA-32/Setup Assets and Wire Scene")]
    public static void Run()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[AA32] Stop Play mode first.");
            return;
        }

        var shader = Shader.Find(UnlitShader);
        if (shader == null)
        {
            Debug.LogError($"[AA32] Shader '{UnlitShader}' not found: is URP installed?");
            return;
        }
        if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder("Assets", "Materials");

        // HDR: components above 1 drive the bloom threshold, which is what makes Unlit read as emissive.
        var hazard = MakeMaterial(shader, "AA32_Hazard", new Color(1.0f, 0.08f, 0.06f) * 2.2f);
        var shoring = MakeMaterial(shader, "AA32_Shoring", new Color(1.0f, 0.55f, 0.05f) * 2.0f);
        var rescue = MakeMaterial(shader, "AA32_RescueLZ", new Color(0.1f, 1.0f, 0.35f) * 1.8f);
        var launch = MakeMaterial(shader, "AA32_LaunchPad", new Color(0.75f, 0.85f, 1.0f) * 1.4f);
        var path = MakeMaterial(shader, "AA32_FlightPath", new Color(0.0f, 1.0f, 1.0f) * 3.0f);
        AssetDatabase.SaveAssets();

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetScene) == null &&
            !AssetDatabase.CopyAsset(SourceScene, TargetScene))
        {
            Debug.LogError($"[AA32] Could not copy {SourceScene} to {TargetScene}.");
            return;
        }

        var agent = HackMatrixSceneWiring.Wire(TargetScene);
        if (agent == null) return;

        var compiler = agent.GetComponent<GenerativeCompiler>();
        var c = new SerializedObject(compiler);
        c.FindProperty("hazardMaterial").objectReferenceValue = hazard;
        c.FindProperty("shoringMaterial").objectReferenceValue = shoring;
        c.FindProperty("rescueMaterial").objectReferenceValue = rescue;
        c.FindProperty("launchMaterial").objectReferenceValue = launch;
        c.FindProperty("pathMaterial").objectReferenceValue = path;
        c.FindProperty("cubeMesh").objectReferenceValue = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        c.FindProperty("cylinderMesh").objectReferenceValue = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
        c.ApplyModifiedPropertiesWithoutUndo();

        // The console's default prompt is serialized; a scene copied from the Roman demo still holds the old one.
        var console = agent.GetComponent<SpatialCompilerConsole>();
        var ui = new SerializedObject(console);
        ui.FindProperty("defaultPrompt").stringValue =
            "M6.8 aftershock, Sector 7. Three-storey school collapsed to the north-east, people trapped. Two-storey " +
            "block down to the north-west. Houses cracked and leaning along the west road and east of the square. " +
            "Open field south of the square is clear. Team is at the south edge.";
        ui.ApplyModifiedPropertiesWithoutUndo();

        var scene = agent.scene;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();

        Debug.Log($"[AA32] Materials in {MaterialFolder}, scene {TargetScene} wired and set as the first build scene.");
        BuildPrefabsAndWire();
        SelfTest();
    }

    // =============================================================================================
    // Composite prefabs: recognisable structures from built-in primitive meshes only (no FBX import), with
    // colliders stripped, on the URP Unlit materials above. Zone prefabs live in a UNIT box - x, z in -0.5..0.5,
    // y in 0..1, pivot at the ground centre - so the compiler's scale to the zone's own box keeps every part
    // inside the footprint and height the verifier checks. Fixed angles, not random ones: the same asset on
    // every run and every machine.
    // =============================================================================================
    const string PrefabFolder = "Assets/Prefabs/AA32";

    [MenuItem("Hack Matrix/AA-32/Build Composite Prefabs and Wire Scene")]
    public static void BuildPrefabsAndWire()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[AA32] Stop Play mode first.");
            return;
        }
        var shader = Shader.Find(UnlitShader);
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Prefabs", "AA32");

        var hazardMat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_Hazard.mat");
        var shoringMat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_Shoring.mat");
        var rescueMat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_RescueLZ.mat");
        var pathMat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_FlightPath.mat");
        var white = MakeMaterial(shader, "AA32_White", Color.white * 1.3f);
        // Unlit has no shading, so parts of one colour merge into a single silhouette. A darker shade of the same
        // colour (below the bloom threshold) separates the mass from the pieces that read as damage.
        var hazardDark = MakeMaterial(shader, "AA32_HazardDark", new Color(0.42f, 0.03f, 0.02f));
        var shoringDark = MakeMaterial(shader, "AA32_ShoringDark", new Color(0.45f, 0.24f, 0.02f));
        var fieldDark = MakeMaterial(shader, "AA32_FieldDark", new Color(0.03f, 0.22f, 0.08f));
        var droneDark = MakeMaterial(shader, "AA32_DroneDark", new Color(0.03f, 0.09f, 0.11f));
        var hazardWall = MakeMaterial(shader, "AA32_HazardWall", new Color(0.24f, 0.03f, 0.03f));
        var window = MakeMaterial(shader, "AA32_Window", new Color(0.015f, 0.02f, 0.03f));

        // Solid, lit architecture: URP Lit, so Key_Light shades every face. Emissive accents stay Unlit HDR.
        var lit = Shader.Find(LitShader);
        var brick = MakeLit(lit, "Mat_Concrete_Hazard", new Color(0.42f, 0.13f, 0.10f), 0.2f, 0f);
        var sandstone = MakeLit(lit, "Mat_Concrete_Shoring", new Color(0.66f, 0.49f, 0.26f), 0.2f, 0f);
        var glass = MakeLit(lit, "Mat_Window_Glass", new Color(0.03f, 0.05f, 0.09f), 0.85f, 0.4f);
        // Metallic 0.5, not 0.8: a highly metallic surface takes its colour from reflections, and this scene (and the
        // phone, with no reflection probe) has only a dark sky to reflect, so 0.8 renders the steel near-black.
        var steel = MakeLit(lit, "Mat_Scaffold_Metal", new Color(0.76f, 0.78f, 0.80f), 0.6f, 0.5f);
        var carbon = MakeLit(lit, "Mat_Drone_Carbon", new Color(0.12f, 0.13f, 0.14f), 0.4f, 0.3f);
        var concrete = MakeLit(lit, "Mat_Concrete_Slab", new Color(0.62f, 0.60f, 0.56f), 0.25f, 0f);
        var timber = MakeLit(lit, "Mat_Timber", new Color(0.46f, 0.31f, 0.16f), 0.3f, 0f);
        AssetDatabase.SaveAssets();
        if (hazardMat == null || shoringMat == null || rescueMat == null || pathMat == null)
        {
            Debug.LogError("[AA32] Zone materials missing: run Hack Matrix > AA-32 > Setup Assets and Wire Scene first.");
            return;
        }

        var hazard = Save(BuildHazard(hazardMat, brick, concrete, glass, steel), "AA32_HazardZone");
        var shoring = Save(BuildShoring(shoringMat, sandstone, concrete, glass, steel, timber), "AA32_ShoringSite");
        var rescue = Save(BuildRescue(rescueMat, fieldDark, white), "AA32_RescueLZ");
        var drone = Save(BuildDrone(pathMat, carbon, steel), "AA32_Drone");

        var compiler = Object.FindAnyObjectByType<GenerativeCompiler>();
        if (compiler == null)
        {
            Debug.LogError("[AA32] No GenerativeCompiler in the open scene; prefabs saved but not wired.");
            return;
        }
        var c = new SerializedObject(compiler);
        c.FindProperty("hazardPrefab").objectReferenceValue = hazard;
        c.FindProperty("shoringPrefab").objectReferenceValue = shoring;
        c.FindProperty("rescuePrefab").objectReferenceValue = rescue;
        c.FindProperty("dronePrefab").objectReferenceValue = drone;
        c.ApplyModifiedPropertiesWithoutUndo();

        ConfigureLitScene(compiler);

        var scene = compiler.gameObject.scene;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log($"[AA32] Composite prefabs saved to {PrefabFolder} and wired into {scene.path}.");
    }

    // Framing is built from solid primitives, not hairlines: a Bar is a cube or cylinder stretched between two
    // points of the unit box, so it keeps real thickness and glows under bloom. The compiler's scale to the zone's
    // box maps both endpoints exactly, so every brace still ends on the corner it was drawn to.
    const float Beam = 0.05f, Rail = 0.035f;

    /// <summary>
    /// Collapsed three-storey school, solid and lit. Two intact storeys of brick carry three protruding concrete
    /// slab bands (plinth, storey, and the last intact floor) and a window grid with sills on every face. The top
    /// storeys have pancaked: a heavy deck tilted 16 degrees and a second slab on it, glowing along their broken
    /// edges. Concrete corner pillars carry glowing stress fractures; three snapped pillar stubs end in glowing
    /// breaks; steel rebar sticks out; concrete and brick chunks lie around the foundation.
    /// </summary>
    static GameObject BuildHazard(Material glow, Material brick, Material concrete, Material glass, Material steel)
    {
        var root = new GameObject("AA32_HazardZone").transform;
        const float h = 0.36f;                                                                                                    // wall half-width

        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.25f, 0f), Vector3.zero, new Vector3(2f * h, 0.50f, 2f * h), brick);      // intact storeys
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.02f, 0f), Vector3.zero, new Vector3(0.76f, 0.04f, 0.76f), concrete);     // plinth band
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.25f, 0f), Vector3.zero, new Vector3(0.76f, 0.035f, 0.76f), concrete);    // storey band
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.50f, 0f), Vector3.zero, new Vector3(0.78f, 0.05f, 0.78f), concrete);     // last intact floor
        Windows(root, h, new[] { 0.13f, 0.38f }, new[] { -0.2f, 0f, 0.2f }, new Vector2(0.11f, 0.12f), glass, concrete);

        var upper = new[]
        {
            (pos: new Vector3(0.02f, 0.61f, 0.03f), euler: new Vector3(16f, 0f, -6f), size: new Vector3(0.76f, 0.06f, 0.74f)),
            (pos: new Vector3(-0.03f, 0.77f, 0.05f), euler: new Vector3(-8f, 12f, 17f), size: new Vector3(0.70f, 0.06f, 0.68f))
        };
        foreach (var s in upper)
        {
            Part(root, PrimitiveType.Cube, s.pos, s.euler, s.size, concrete);
            var m = Matrix4x4.TRS(s.pos, Quaternion.Euler(s.euler), s.size);
            Vector3 C(float x, float y, float z) => m.MultiplyPoint3x4(new Vector3(x, y, z));
            Bar(root, PrimitiveType.Cube, C(-0.5f, 0.52f, -0.5f), C(0.5f, 0.52f, -0.5f), 0.02f, glow);                             // broken front edge
            Bar(root, PrimitiveType.Cube, C(0.5f, 0.52f, -0.5f), C(0.5f, 0.52f, 0.5f), 0.02f, glow);                               // broken side edge
            Bar(root, PrimitiveType.Cube, C(-0.1f, 0.52f, -0.5f), C(0.15f, 0.52f, 0.1f), 0.012f, glow);                            // crack across the deck
        }

        foreach (var x in new[] { -h, h })                                                                                        // corner pillars
            foreach (var z in new[] { -h, h })
            {
                Bar(root, PrimitiveType.Cube, new Vector3(x, 0f, z), new Vector3(x, 0.5f, z), 0.055f, concrete);
                Part(root, PrimitiveType.Cube, new Vector3(x, 0.42f, z), new Vector3(0f, 20f, x * z > 0 ? 9f : -9f),
                     new Vector3(0.062f, 0.012f, 0.062f), glow);                                                                  // stress fracture
            }
        foreach (var (a, b) in new[]                                                                                              // snapped pillars
        {
            (new Vector3(h, 0.52f, h), new Vector3(0.30f, 0.66f, 0.40f)),
            (new Vector3(-h, 0.52f, h), new Vector3(-0.42f, 0.63f, 0.29f)),
            (new Vector3(-h, 0.52f, -h), new Vector3(-0.30f, 0.60f, -0.42f))
        })
        {
            Bar(root, PrimitiveType.Cube, a, b, 0.05f, concrete);
            Part(root, PrimitiveType.Cube, b, new Vector3(25f, 40f, 15f), new Vector3(0.045f, 0.02f, 0.045f), glow);              // glowing break
        }

        foreach (var (a, b) in new[]                                                                                              // exposed rebar
        {
            (new Vector3(0.33f, 0.62f, -0.30f), new Vector3(0.44f, 0.70f, -0.36f)),
            (new Vector3(0.25f, 0.64f, -0.33f), new Vector3(0.31f, 0.76f, -0.44f)),
            (new Vector3(-0.30f, 0.80f, -0.25f), new Vector3(-0.42f, 0.86f, -0.33f)),
            (new Vector3(-0.10f, 0.83f, -0.30f), new Vector3(-0.12f, 0.93f, -0.42f)),
            (new Vector3(0.20f, 0.78f, 0.28f), new Vector3(0.30f, 0.90f, 0.38f))
        })
            Bar(root, PrimitiveType.Cylinder, a, b, 0.012f, steel);

        RubbleApron(root, brick, concrete);
        return root.gameObject;
    }

    /// <summary>
    /// Leaning three-storey block held by a shoring rig, solid and lit. The sandstone tower - concrete storey
    /// ledges, a parapet and 3 x 3 windows with sills per face - leans 5 degrees about z from its base. Around it
    /// stands a galvanised steel scaffold (standards, three rail tiers, sway braces) with timber work platforms,
    /// and two timber rakers on sole plates shore the leaning face from the ground. Glowing amber beacons cap the
    /// standards so the zone still reads as a shoring site at a glance.
    /// </summary>
    static GameObject BuildShoring(Material glow, Material sandstone, Material concrete, Material glass, Material steel, Material timber)
    {
        var root = new GameObject("AA32_ShoringSite").transform;
        var tower = new GameObject("LeaningTower").transform;
        tower.SetParent(root, false);
        tower.localRotation = Quaternion.Euler(0f, 0f, -5f);                                                                     // leans toward +x from the base

        const float h = 0.31f;
        Part(tower, PrimitiveType.Cube, new Vector3(0f, 0.45f, 0f), Vector3.zero, new Vector3(2f * h, 0.90f, 2f * h), sandstone);
        foreach (float y in new[] { 0.30f, 0.60f })
            Part(tower, PrimitiveType.Cube, new Vector3(0f, y, 0f), Vector3.zero, new Vector3(0.67f, 0.028f, 0.67f), concrete);  // storey ledges
        Part(tower, PrimitiveType.Cube, new Vector3(0f, 0.90f, 0f), Vector3.zero, new Vector3(0.66f, 0.035f, 0.66f), concrete);   // parapet
        Windows(tower, h, new[] { 0.15f, 0.45f, 0.75f }, new[] { -0.17f, 0f, 0.17f }, new Vector2(0.09f, 0.13f), glass, concrete);

        ShoringRig(root, glow, steel, timber);
        return root.gameObject;
    }

    /// <summary>Concrete and brick chunks on the ground apron around a collapsed building (fixed, not random).</summary>
    static void RubbleApron(Transform root, Material brick, Material concrete)
    {
        foreach (var (x, z, size, yaw, tilt, isBrick) in new[]                                                                   // rubble at the foundation
        {
            (-0.42f, -0.28f, 0.07f, 25f, 15f, false), (-0.42f, 0.16f, 0.055f, 60f, 30f, true),
            (-0.20f, -0.43f, 0.065f, 10f, 40f, true), (0.10f, -0.42f, 0.07f, 45f, 20f, false),
            (0.42f, -0.10f, 0.06f, 70f, 10f, true), (0.43f, 0.28f, 0.05f, 15f, 35f, false),
            (0.24f, 0.43f, 0.065f, 80f, 25f, true), (-0.28f, 0.43f, 0.05f, 35f, 50f, false)
        })
            Part(root, PrimitiveType.Cube, new Vector3(x, size * 0.4f, z), new Vector3(tilt, yaw, tilt * 0.5f),
                 new Vector3(size, size * 0.8f, size), isBrick ? brick : concrete);
    }

    /// <summary>
    /// The shoring rig around a block of half-width 0.31 leaning 5 degrees toward +x: timber rakers on the
    /// leaning face, a steel scaffold with glowing beacons, and timber work platforms clear of the lean.
    /// </summary>
    static void ShoringRig(Transform root, Material glow, Material steel, Material timber)
    {
        foreach (float z in new[] { -0.2f, 0.2f })                                                                               // timber rakers
        {
            Part(root, PrimitiveType.Cube, new Vector3(0.465f, 0.01f, z), Vector3.zero, new Vector3(0.07f, 0.02f, 0.11f), timber);  // sole plate
            Bar(root, PrimitiveType.Cube, new Vector3(0.475f, 0.02f, z), new Vector3(0.37f, 0.62f, z), 0.045f, timber);
        }

        const float c = 0.46f;
        var corners = new[] { new Vector2(-c, -c), new Vector2(c, -c), new Vector2(c, c), new Vector2(-c, c) };
        var tiers = new[] { 0.03f, 0.5f, 0.97f };
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var mid = (a + b) * 0.5f;
            foreach (var p in new[] { a, mid })
            {
                Bar(root, PrimitiveType.Cylinder, new Vector3(p.x, 0f, p.y), new Vector3(p.x, 0.98f, p.y), p == a ? Beam : Rail, steel);  // standards
                Part(root, PrimitiveType.Cube, new Vector3(p.x, 0.985f, p.y), Vector3.zero, new Vector3(0.05f, 0.03f, 0.05f), glow);     // beacon
            }
            foreach (float y in tiers)
                Bar(root, PrimitiveType.Cylinder, new Vector3(a.x, y, a.y), new Vector3(b.x, y, b.y), Rail, steel);            // rail tiers
            Bar(root, PrimitiveType.Cylinder, new Vector3(a.x, tiers[0], a.y), new Vector3(b.x, tiers[1], b.y), Rail * 0.8f, steel);
            Bar(root, PrimitiveType.Cylinder, new Vector3(b.x, tiers[1], b.y), new Vector3(a.x, tiers[2], a.y), Rail * 0.8f, steel);
        }

        // Work platforms: timber decks just above the rails, clear of the leaning tower (its +x face reaches x 0.36
        // at mid-height and 0.40 at the top, so the +x deck is at mid-height only).
        foreach (float y in new[] { 0.5f, 0.97f })
            Part(root, PrimitiveType.Cube, new Vector3(0f, y + 0.02f, -0.405f), Vector3.zero, new Vector3(0.9f, 0.016f, 0.1f), timber);
        Part(root, PrimitiveType.Cube, new Vector3(0.425f, 0.52f, 0f), Vector3.zero, new Vector3(0.07f, 0.016f, 0.9f), timber);
    }

    /// <summary>
    /// Window openings on all four faces of a square block of half-width <paramref name="half"/>: a glass pane
    /// just proud of the wall and a concrete sill under it, whose shadow line gives the opening depth under light.
    /// </summary>
    static void Windows(Transform parent, float half, float[] rows, float[] columns, Vector2 size, Material glass, Material sill)
    {
        foreach (float yaw in new[] { 0f, -90f, 180f, 90f })        // faces -z, +x, +z, -x (a quad faces its own -z)
        {
            var turn = Quaternion.Euler(0f, yaw, 0f);
            foreach (float y in rows)
                foreach (float x in columns)
                {
                    Part(parent, PrimitiveType.Quad, turn * new Vector3(x, y, -half - 0.003f), turn, new Vector3(size.x, size.y, 1f), glass);
                    Part(parent, PrimitiveType.Cube, turn * new Vector3(x, y - size.y * 0.5f - 0.007f, -half - 0.01f), turn,
                         new Vector3(size.x * 1.25f, 0.014f, 0.02f), sill);
                }
        }
    }

    /// <summary>
    /// Tactical pitch / helipad: an opaque dark green slab with a glowing green rim, and white markings raised
    /// 2 mm above it - perimeter lines, centre line, a box at each end, a landing ring, and the H.
    /// </summary>
    static GameObject BuildRescue(Material bright, Material field, Material white)
    {
        var root = new GameObject("AA32_RescueLZ").transform;
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), Vector3.zero, Vector3.one, field);                       // the pitch, top at y = 1

        // Quads turned +90 about x face up (local y becomes z). At the default flattening (6 mm tall zone) y = 1.35
        // puts the markings about 2 mm above the slab top, so they never z-fight.
        var up = new Vector3(90f, 0f, 0f);
        const float y = 1.35f, t = 0.024f;
        void Strip(float x0, float z0, float x1, float z1, Material m, float width) =>
            Part(root, PrimitiveType.Quad, new Vector3((x0 + x1) * 0.5f, y, (z0 + z1) * 0.5f), up,
                 new Vector3(Mathf.Max(Mathf.Abs(x1 - x0), width), Mathf.Max(Mathf.Abs(z1 - z0), width), 1f), m);
        void Rect(float x0, float z0, float x1, float z1, Material m, float width)
        {
            Strip(x0, z0, x1, z0, m, width);
            Strip(x0, z1, x1, z1, m, width);
            Strip(x0, z0, x0, z1, m, width);
            Strip(x1, z0, x1, z1, m, width);
        }

        Rect(-0.49f, -0.49f, 0.49f, 0.49f, bright, 0.02f);                    // glowing rim
        Rect(-0.42f, -0.42f, 0.42f, 0.42f, white, t);                         // touchline
        Strip(-0.42f, 0f, -0.30f, 0f, white, t);                              // centre line, broken at the ring
        Strip(0.30f, 0f, 0.42f, 0f, white, t);
        Rect(-0.18f, -0.42f, 0.18f, -0.31f, white, t);                        // end boxes
        Rect(-0.18f, 0.31f, 0.18f, 0.42f, white, t);

        const int segments = 32;                                              // landing ring from short flat strips
        const float r = 0.25f;
        for (int i = 0; i < segments; i++)
        {
            float a = (i + 0.5f) * Mathf.PI * 2f / segments;
            float len = 2f * r * Mathf.Sin(Mathf.PI / segments) * 1.08f;
            Part(root, PrimitiveType.Quad, new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r),
                 new Vector3(90f, -a * Mathf.Rad2Deg, 0f), new Vector3(t, len, 1f), white);
        }

        Strip(-0.11f, -0.15f, -0.11f, 0.15f, white, 0.055f);                  // H
        Strip(0.11f, -0.15f, 0.11f, 0.15f, white, 0.055f);
        Strip(-0.11f, 0f, 0.11f, 0f, white, 0.055f);
        return root.gameObject;
    }

    /// <summary>
    /// Heavy inspection UAV, about 10 cm across: dark chassis and battery housing with glowing trim, a camera
    /// ball with a lens under the nose, two skids on angled struts, four thick arms in an X, dark motors with
    /// glowing caps, and glowing rotor guards. The pivot is at the bottom of the skids, so it stands on the pad.
    /// </summary>
    static GameObject BuildDrone(Material glow, Material dark, Material steel)
    {
        var root = new GameObject("AA32_Drone").transform;
        const float k = 1.6f;                                   // overall scale over the previous drone
        const float lift = 0.0195f;                             // skid bottom -> chassis centre, before k
        Vector3 L(float x, float y, float z) => new Vector3(x, y + lift, z) * k;
        Vector3 S(float x, float y, float z) => new Vector3(x, y, z) * k;

        Part(root, PrimitiveType.Cube, L(0f, 0f, 0f), Vector3.zero, S(0.024f, 0.010f, 0.030f), dark);                     // chassis
        Part(root, PrimitiveType.Cube, L(0f, 0f, 0f), Vector3.zero, S(0.0246f, 0.0016f, 0.0306f), glow);                  // chassis belt
        Part(root, PrimitiveType.Cube, L(0f, 0.0075f, -0.002f), Vector3.zero, S(0.018f, 0.005f, 0.020f), dark);           // battery housing
        Part(root, PrimitiveType.Cube, L(0f, 0.0101f, -0.002f), Vector3.zero, S(0.0186f, 0.0012f, 0.0206f), glow);        // battery trim

        Part(root, PrimitiveType.Cylinder, L(0f, -0.0065f, 0.010f), Vector3.zero, S(0.004f, 0.002f, 0.004f), dark);       // gimbal neck
        Part(root, PrimitiveType.Sphere, L(0f, -0.0112f, 0.011f), Vector3.zero, S(0.010f, 0.010f, 0.010f), dark);         // camera ball
        Part(root, PrimitiveType.Cylinder, L(0f, -0.0112f, 0.0162f), new Vector3(90f, 0f, 0f), S(0.005f, 0.0008f, 0.005f), glow);  // lens

        foreach (float x in new[] { -0.012f, 0.012f })
        {
            Bar(root, PrimitiveType.Cylinder, L(x, -0.018f, -0.018f), L(x, -0.018f, 0.018f), 0.0026f * k, dark);          // skid rail
            foreach (float z in new[] { -0.009f, 0.009f })
                Bar(root, PrimitiveType.Cylinder, L(x * 0.65f, -0.005f, z), L(x, -0.018f, z), 0.0018f * k, steel);       // angled strut
        }

        const float reach = 0.024f;
        foreach (float yaw in new[] { 45f, 135f, 225f, 315f })
        {
            var d = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 At(float r, float y) => L(d.x * r, y, d.z * r);
            Bar(root, PrimitiveType.Cube, At(0.008f, 0.001f), At(reach, 0.001f), 0.0045f * k, dark);                     // square-tube boom
            Part(root, PrimitiveType.Cylinder, At(reach, 0.0040f), Vector3.zero, S(0.0080f, 0.0040f, 0.0080f), dark);     // motor pod
            Part(root, PrimitiveType.Cylinder, At(reach, 0.0086f), Vector3.zero, S(0.0035f, 0.0008f, 0.0035f), glow);     // motor cap
            Part(root, PrimitiveType.Cylinder, At(reach, 0.0072f), Vector3.zero, S(0.020f, 0.0003f, 0.020f), glow);       // rotor disc
            foreach (float blade in new[] { yaw, yaw + 90f })                                                             // blades
                Part(root, PrimitiveType.Cube, At(reach, 0.0077f), new Vector3(0f, blade + 20f, 0f), S(0.0018f, 0.0006f, 0.019f), dark);
            const int guard = 16;                                                                                         // rotor guard ring
            for (int i = 0; i < guard; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / guard;
                var centre = At(reach, 0.0072f) + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (0.0115f * k);
                Part(root, PrimitiveType.Cube, centre, new Vector3(0f, -a * Mathf.Rad2Deg, 0f),
                     S(0.0014f, 0.0022f, 2f * 0.0115f * Mathf.Sin(Mathf.PI / guard) * 1.1f), glow);
            }
        }
        return root.gameObject;
    }

    /// <summary>A cube or cylinder spanning a to b with the given thickness.</summary>
    static void Bar(Transform parent, PrimitiveType type, Vector3 a, Vector3 b, float thickness, Material material)
    {
        Vector3 d = b - a;
        float length = d.magnitude;
        // Unity's cylinder is 2 units tall, the cube 1: both stretch along their own y.
        var scale = new Vector3(thickness, type == PrimitiveType.Cylinder ? length * 0.5f : length, thickness);
        Part(parent, type, (a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d), scale, material);
    }

    static void Part(Transform parent, PrimitiveType type, Vector3 position, Vector3 euler, Vector3 scale, Material material) =>
        Part(parent, type, position, Quaternion.Euler(euler), scale, material);

    static void Part(Transform parent, PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
    {
        var go = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(go.GetComponent<Collider>());      // no physics: nothing collides, and no collider types to strip
        go.name = type.ToString();
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localRotation = rotation;
        go.transform.localScale = scale;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        bool lit = material != null && material.shader != null && material.shader.name == LitShader;
        renderer.shadowCastingMode = lit ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = lit;
    }

    static GameObject Save(GameObject root, string name)
    {
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabFolder}/{name}.prefab");
        Object.DestroyImmediate(root);
        return prefab;
    }

    /// <summary>
    /// Lit materials need light. DarkARLighting - which this scene inherited from the Roman demo, where the particle
    /// field lit the marble - zeroes ambient and switches Key_Light off every frame, which would render every Lit
    /// surface black. In THIS scene it is disabled, Key_Light stays on, and a neutral ambient keeps the faces turned
    /// away from the key readable instead of pitch black. The Roman scene keeps its own lighting untouched.
    /// </summary>
    static void ConfigureLitScene(GenerativeCompiler compiler)
    {
        var dark = compiler.GetComponent<MAAYAI.Swarm.DarkARLighting>();
        if (dark != null)
        {
            dark.enabled = false;
            EditorUtility.SetDirty(dark);
        }
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (light.name == "Key_Light")
            {
                light.enabled = true;
                EditorUtility.SetDirty(light);
            }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.40f, 0.42f, 0.47f);
        RenderSettings.reflectionIntensity = 1f;
    }

    static Material MakeLit(Shader shader, string name, Color baseColor, float smoothness, float metallic)
    {
        string assetPath = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (material == null)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, assetPath);
        }
        material.shader = shader;
        material.SetColor("_BaseColor", baseColor);
        material.color = baseColor;
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", metallic);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material MakeMaterial(Shader shader, string name, Color hdr)
    {
        string assetPath = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (material == null)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, assetPath);
        }
        material.shader = shader;
        material.SetColor("_BaseColor", hdr);
        material.color = hdr;
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>The offline zone map must pass the same verifier as a model reply, path checks included.</summary>
    [MenuItem("Hack Matrix/AA-32/Self-Test Offline Zone Map")]
    public static void SelfTest()
    {
        var plan = AgenticLLM_Bridge.OfflinePlan();
        var report = PlanVerifier.Verify(plan, 0.03f, 1.5f);
        var path = report.Path;
        string dispatch = string.Join(", ", path.Dispatches.Select(d => $"node {d.node} at {d.coveredAt:0.00} m"));
        string summary = $"'{plan.plan_name}': {plan.nodes.Length} nodes, {path.Lanes} lanes, {path.Waypoints.Count} " +
                         $"waypoints, {path.TotalLength:0.00} m path; dispatch order: {dispatch}.";
        if (report.Passed) Debug.Log("[AA32] Self-test PASS " + summary);
        else Debug.LogError("[AA32] Self-test FAIL " + summary + "\n- " + string.Join("\n- ", report.Violations.Select(v => v.Message)));

        var (correct, total) = SimulatedSensor.RunBenchmark();
        Debug.Log($"[AA32] Simulated sensor benchmark: {correct}/{total} ({100f * correct / total:0}%). {SimulatedSensor.Disclosure}.");

        SelfTestRelay();
    }

    /// <summary>
    /// The radio rule on hand-built cases with known answers: direct link, exact range, just out of range, a
    /// chain, a LZ that must not relay for another, and the verifier's own suggested fix actually connecting.
    /// </summary>
    [MenuItem("Hack Matrix/AA-32/Self-Test Relay Network")]
    public static void SelfTestRelay()
    {
        const float k = 0.03f, footprint = 1.5f;
        float range = SpatialRecipes.RadioRangeDesign * k;
        int failures = 0;

        void Expect(string name, (string type, float x, float z)[] nodes, bool connected, int maxHops)
        {
            var site = SiteModel.Build(Plan(nodes), k, footprint);
            var net = RelayNetwork.Analyse(site);
            bool ok = net.Connected == connected && (!connected || net.MaxHops == maxHops);
            if (!ok) failures++;
            string got = net.Connected ? $"connected, {net.MaxHops} hop(s)" : "isolated";
            string want = connected ? $"connected, {maxHops} hop(s)" : "isolated";
            if (ok) Debug.Log($"[AA32] Relay PASS {name}: {got}.");
            else Debug.LogError($"[AA32] Relay FAIL {name}: got {got}, expected {want}.");
        }

        Expect("LZ in direct range", new[] { ("LaunchPad", 0f, -0.6f), ("RescueLZ", 0f, -0.2f) }, true, 1);
        Expect("LZ at exactly the range", new[] { ("LaunchPad", 0f, -0.6f), ("RescueLZ", 0f, -0.6f + range) }, true, 1);
        Expect("LZ just out of range", new[] { ("LaunchPad", 0f, -0.6f), ("RescueLZ", 0f, -0.6f + range + 0.01f) }, false, 0);
        Expect("two-relay chain", new[] { ("LaunchPad", 0f, -0.6f), ("RelayNode", 0f, -0.15f), ("RelayNode", 0f, 0.3f),
                                           ("RescueLZ", 0f, 0.6f) }, true, 3);
        Expect("broken chain", new[] { ("LaunchPad", 0f, -0.6f), ("RelayNode", 0f, -0.15f), ("RelayNode", 0f, 0.5f),
                                        ("RescueLZ", 0f, 0.6f) }, false, 0);
        Expect("an LZ does not relay", new[] { ("LaunchPad", 0f, -0.6f), ("RescueLZ", 0f, -0.2f), ("RescueLZ", 0f, 0.2f) }, false, 0);
        Expect("shortest chain wins", new[] { ("LaunchPad", 0f, -0.6f), ("RelayNode", -0.4f, -0.3f), ("RelayNode", -0.4f, 0.1f),
                                               ("RelayNode", 0f, -0.2f), ("RescueLZ", 0f, 0.2f) }, true, 2);

        // The verifier's suggested relay positions must actually connect the LZ they were suggested for.
        var isolated = new[] { ("LaunchPad", -0.6f, -0.6f), ("HazardZone", 0f, 0f), ("RescueLZ", 0.6f, 0.6f) };
        var report = PlanVerifier.Verify(Plan(isolated), k, footprint);
        var site0 = SiteModel.Build(Plan(isolated), k, footprint);
        var bridge = RelayNetwork.Bridge(site0, site0.First(SpatialNodeTypes.RescueLZ).Value);
        var fixedNodes = isolated.ToList();
        if (bridge != null) fixedNodes.AddRange(bridge.Value.relays.Select(p => ("RelayNode", p.x, p.y)));
        var net1 = RelayNetwork.Analyse(SiteModel.Build(Plan(fixedNodes.ToArray()), k, footprint));
        bool fixWorks = report.Violations.Any(v => v.Code == ViolationCode.RelayIsolated) && bridge != null && net1.Connected;
        if (!fixWorks) failures++;
        if (fixWorks) Debug.Log($"[AA32] Relay PASS suggested fix: {bridge.Value.relays.Count} relay(s) connect the LZ in {net1.MaxHops} hops.");
        else Debug.LogError("[AA32] Relay FAIL suggested fix does not connect the LZ.");

        if (failures == 0) Debug.Log("[AA32] Relay network self-test: all cases pass.");
        else Debug.LogError($"[AA32] Relay network self-test: {failures} failure(s).");
    }

    static SpatialPlan Plan((string type, float x, float z)[] nodes) => new()
    {
        plan_name = "relay test",
        nodes = nodes.Select(n => new SpatialNode { node_type = n.type, coordinates = new Vector3(n.x, 0f, n.z) }).ToArray()
    };
}
