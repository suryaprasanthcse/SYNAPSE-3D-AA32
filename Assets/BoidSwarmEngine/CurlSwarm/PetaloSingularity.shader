// PetaloSingularity.shader
// Petalo: a character that exists only as mathematics. No mesh, no rig, no authored geometry - a unit cube is
// rasterised purely to spawn rays, and every pixel is sphere-traced against a signed distance field and shaded
// as a real surface.
//
// DUAL STATE. One field, two bodies, continuously morphable:
//
//   State A - EXPLORATION (_StateBlend = 0)
//     The Funobotz mascot, rebuilt as constructive solid geometry: octagonal tiered plinth, tapered white pot,
//     green stem, and a flat flower head carrying eight yellow petals, a red rim, a dark face, two glowing amber
//     eyes, a smile and a tongue. This is what the player drives across the table. The cartoon outline in the
//     source art is reproduced as a view-dependent rim term rather than extra geometry, so it costs no steps.
//
//   State B - THE ANOMALY (_StateBlend = 1)
//     The original anisotropic brushed titanium mandala: polar-repeated petal rings, a folded fractal ring, an
//     orbiting halo and an emissive heart, lit by the swarm's VPL grid. PRESERVED VERBATIM from the pre-shift
//     shader - FieldB below is the old FieldSDF, unmodified. Do not simplify it; it is the cutscene/ignition body.
//
//   _StateBlend lerps the two distance fields, so the mascot does not cut to the mandala, it becomes it. A lerp
//   of two SDFs is not itself an exact SDF, so the tracer damps its step hardest at the halfway point.
//
// Because it writes real depth (SV_Depth from the hit distance), Petalo occludes and is occluded correctly by the
// world blockout. That is what separates a solid character from additive haze.
//
// Mobile budget (Adreno 650, 30 fps thermal lock): sphere tracing, 48-step cap, early break on hit or on leaving
// the volume, a 4-tap tetrahedral normal only on hit pixels, no shadow rays. Materials are resolved ONCE at the
// hit point - the trace loop and the normal taps run a distance-only path that never touches a colour.
Shader "MAAYAI/PetaloSingularity"
{
    Properties
    {
        [Header(State)]
        [Tooltip(0 is the mascot avatar and 1 is the titanium anomaly)]
        _StateBlend ("State Blend (0 = Petalo, 1 = Anomaly)", Range(0, 1)) = 0
        [Tooltip(0 morphs the whole body at once and 1 breaks the core out first)]
        _MorphCoreFirst ("Morph Core First", Range(0, 1)) = 1

        [Header(Titanium Shell)]
        _MetalColor ("Brushed Titanium", Color) = (0.62, 0.66, 0.70, 1)
        _Shininess ("Shininess", Range(4, 256)) = 64
        _Anisotropy ("Brush Anisotropy", Range(0, 1)) = 0.65
        _FresnelPower ("Fresnel Power", Range(1, 8)) = 4
        _SpecularGain ("Specular Gain", Range(0, 8)) = 2.5

        [Header(Emissive Core)]
        [HDR] _CoreColor ("Core Emission", Color) = (0.4, 2.2, 3.0, 1)
        [HDR] _ShellColor ("Crevice Emission", Color) = (0.1, 0.8, 1.4, 1)
        [HDR] _AlignedColor ("Aligned (Gold) Emission", Color) = (3.0, 1.9, 0.5, 1)
        _EmissionGain ("Emission Gain", Range(0, 4)) = 1.0
        _CreviceWidth ("Crevice Width", Range(0.005, 0.12)) = 0.045

        [Header(Housing)]
        _HousingColor ("Matte White Shell", Color) = (0.94, 0.94, 0.95, 1)
        _AccentColor ("Edge Accent", Color) = (1.0, 0.52, 0.08, 1)
        _HousingSpecular ("Housing Specular", Range(0, 1)) = 0.12
        _AccentWidth ("Accent Width", Range(0.0, 0.06)) = 0.012
        _BaseRadius ("Hex Base Radius", Range(0.05, 0.5)) = 0.26
        _BaseHeight ("Hex Base Height", Range(0.01, 0.2)) = 0.075
        _PotHeight ("Pot Height", Range(0.05, 0.4)) = 0.135
        _PotTopHalf ("Pot Top Half Width", Range(0.03, 0.3)) = 0.155
        _PotBottomHalf ("Pot Bottom Half Width", Range(0.02, 0.3)) = 0.105
        _StemRadius ("Stem Radius", Range(0.003, 0.05)) = 0.022
        _FlowerHeight ("Anomaly Flower Height", Range(-0.2, 0.45)) = 0.20

        [Header(Mascot Body   State A)]
        // State A carries its own plinth and pot dimensions. Sharing State B's would have quietly restyled the
        // anomaly's housing the moment the mascot was art-directed, and the mandala is meant to be untouched.
        _PlinthRadius ("Octagonal Plinth Radius", Range(0.05, 0.5)) = 0.25
        _PlinthHeight ("Octagonal Plinth Height", Range(0.02, 0.35)) = 0.21
        _PotHeightA ("Mascot Pot Height", Range(0.05, 0.45)) = 0.25

        [Header(Mascot Head   State A)]
        // The whole avatar has to fit the unit ray volume, which spans -0.5..0.5. The petals reach
        // _HeadRadius + petal length = 0.254 beyond the head centre, so anything above ~0.246 clips the
        // flower's crown against the bounding cube.
        _HeadCentre ("Head Centre Y", Range(0.0, 0.245)) = 0.238
        _FaceRadius ("Face Disc Radius", Range(0.02, 0.25)) = 0.100
        _RimRadius ("Red Rim Outer Radius", Range(0.03, 0.3)) = 0.128
        _HeadRadius ("Petal Ring Radius", Range(0.05, 0.35)) = 0.168
        _MascotPetals ("Mascot Petal Count", Range(5, 12)) = 8
        _MascotPetalSize ("Mascot Petal (length, width, thick)", Vector) = (0.086, 0.064, 0.021, 0)
        _ToonOutline ("Cartoon Outline Strength", Range(0, 1)) = 0.85

        [Header(Mascot Palette)]
        _PetalYellow ("Petal Yellow", Color) = (1.0, 0.78, 0.09, 1)
        _RimRed ("Rim Red", Color) = (0.85, 0.13, 0.15, 1)
        _FaceDark ("Face Dark", Color) = (0.28, 0.12, 0.09, 1)
        _SmileWhite ("Smile / Tongue White", Color) = (0.97, 0.97, 0.96, 1)
        _StemGreen ("Stem Green", Color) = (0.42, 0.72, 0.15, 1)
        [HDR] _EyeGlow ("Eye Glow", Color) = (2.4, 1.6, 0.25, 1)

        [Header(Anomaly Flower   State B)]
        _Alignment ("Alignment (0 = cyan, 1 = gold)", Range(0, 1)) = 0
        _Pulse ("Pulse", Range(0, 1)) = 0
        _Bloom ("Bloom (0 = bud, 1 = open)", Range(0, 1)) = 0.78
        _Petals ("Petals Per Ring", Range(3, 12)) = 6
        _PetalLength ("Petal Length", Range(0.05, 0.45)) = 0.20
        _PetalWidth ("Petal Width", Range(0.01, 0.2)) = 0.085
        _PetalThickness ("Petal Thickness", Range(0.002, 0.06)) = 0.032
        _CoreRadius ("Core Radius", Range(0.01, 0.5)) = 0.10
        _ShellRadius ("Shell Radius", Range(0.05, 0.9)) = 0.34
        _FoldCount ("Fractal Folds", Range(1, 6)) = 3
        _SpinSpeed ("Spin Speed", Range(0, 4)) = 0.6
        [IntRange] _Steps ("Trace Steps", Range(8, 64)) = 48

        // Back-face culling is the default: one raymarch per pixel instead of two, and it is what makes the
        // conservative depth output below legal. PetaloBeacon switches this to Off when the camera is inside the
        // ray volume, because then the front faces are behind the near plane and Petalo would vanish.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry+10"
        }

        Pass
        {
            Name "PetaloSurface"
            Tags { "LightMode" = "UniversalForward" }

            // A solid object: opaque, writes depth, and visible from inside its own bounding volume.
            Blend Off
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            // 4.5, not 3.5: SV_DepthGreaterEqual is a Shader Model 5 output semantic (ps_4_0 rejects it). The
            // swarm renderer already requires 4.5 for vertex-stage SSBOs, so this costs no device coverage.
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            // Shared with SwarmSurface: one toggle flips the whole frame between the irradiance volume and the
            // pre-optimisation per-fragment VPL loop, so an A/B capture compares like with like.
            #pragma multi_compile_fragment _ SWARM_LEGACY_VPL_LOOP

            // Set by PetaloBeacon only while culling back faces, where the rasterised depth is guaranteed to be
            // in front of (or equal to) the traced hit.
            #pragma multi_compile_local_fragment _ PETALO_CONSERVATIVE_DEPTH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct SwarmVPL
            {
                float4 posSoftSq;   // xyz world position, w soft-core radius^2
                float4 color;       // rgb linear flux
            };

            StructuredBuffer<SwarmVPL> _SwarmVPLs;
            float4 _SwarmVPLParams;     // x total count, y gain, z 1 / range^2, w beacon count
            float  _SwarmAmbient;       // global floor, matching SwarmSurface: see the note there

            // The swarm's 64 cells, pre-integrated into an L1 irradiance volume by SwarmVPL.compute (CSVolume).
            // Petalo is always inside it: the swarm is tethered to Petalo, so the volume is built around him.
            TEXTURE3D(_SwarmIrrL0);
            SAMPLER(sampler_SwarmIrrL0);
            TEXTURE3D(_SwarmIrrL1);
            SAMPLER(sampler_SwarmIrrL1);
            float4 _SwarmVolumeMin;
            float4 _SwarmVolumeInvSize;

            CBUFFER_START(UnityPerMaterial)
                half  _StateBlend;
                half  _MorphCoreFirst;
                half4 _MetalColor;
                half  _Shininess;
                half  _Anisotropy;
                half  _FresnelPower;
                half  _SpecularGain;
                half4 _CoreColor;
                half4 _ShellColor;
                half4 _AlignedColor;
                half  _EmissionGain;
                half  _CreviceWidth;
                half  _Alignment;
                half  _Pulse;
                half  _Bloom;
                half  _Petals;
                half  _PetalLength;
                half  _PetalWidth;
                half  _PetalThickness;
                half  _CoreRadius;
                half  _ShellRadius;
                half  _FoldCount;
                half  _SpinSpeed;
                half  _Steps;
                float _Cull;
                half4 _HousingColor;
                half4 _AccentColor;
                half  _HousingSpecular;
                half  _AccentWidth;
                half  _BaseRadius;
                half  _BaseHeight;
                half  _PotHeight;
                half  _PotTopHalf;
                half  _PotBottomHalf;
                half  _StemRadius;
                half  _FlowerHeight;
                half  _PlinthRadius;
                half  _PlinthHeight;
                half  _PotHeightA;
                half  _HeadCentre;
                half  _FaceRadius;
                half  _RimRadius;
                half  _HeadRadius;
                half  _MascotPetals;
                half4 _MascotPetalSize;
                half  _ToonOutline;
                half4 _PetalYellow;
                half4 _RimRed;
                half4 _FaceDark;
                half4 _SmileWhite;
                half4 _StemGreen;
                half4 _EyeGlow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 cameraOS   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Conservative depth. Writing SV_Depth turns off early-Z for every fragment of the ray volume, which
            // on a tiler means the whole silhouette is traced even where it is already occluded. With back faces
            // culled, the traced hit can only ever be BEHIND the rasterised front face, which is exactly the
            // contract SV_DepthGreaterEqual states - so the hardware keeps rejecting early and the shader still
            // writes true depth. Guarded by API: the semantic is not available everywhere, and GLES3 must fall
            // back to plain SV_Depth rather than fail to compile.
            #if defined(PETALO_CONSERVATIVE_DEPTH) && SHADER_TARGET >= 45 && \
                (defined(SHADER_API_D3D11) || defined(SHADER_API_VULKAN) || defined(SHADER_API_METAL) || \
                 defined(SHADER_API_D3D12) || defined(SHADER_API_PS5) || defined(SHADER_API_GAMECORE))
                #define PETALO_DEPTH_SEMANTIC SV_DepthGreaterEqual
            #else
                #define PETALO_DEPTH_SEMANTIC SV_Depth
            #endif

            struct FragOut
            {
                half4 color : SV_Target;
                float depth : PETALO_DEPTH_SEMANTIC;
            };

            // Everything the lighting loop needs from the field. Filled once, at the hit point only.
            struct Surf
            {
                half3 albedo;
                half3 emission;
                half  gloss;        // specular exponent
                half  spec;         // specular gain
                half  diffuse;      // diffuse gain (metal is specular-dominant, plastic is not)
                half  aniso;        // brushed anisotropy, 0 on moulded plastic
                half  outline;      // 1 = this material takes the cartoon ink line
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.positionOS = input.positionOS.xyz;
                o.cameraOS = mul(GetWorldToObjectMatrix(), float4(GetCameraPositionWS(), 1.0)).xyz;
                return o;
            }

            float2x2 Rot(float a)
            {
                float s, c;
                sincos(a, s, c);
                return float2x2(c, -s, s, c);
            }

            float SMin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            // ==========================================================================================
            // Primitives
            // ==========================================================================================

            // Hexagonal prism (iq), swizzled so the hexagon lies in XZ and extrudes along Y.
            float SDHexPrism(float3 p, float2 h)
            {
                const float3 k = float3(-0.8660254, 0.5, 0.57735);
                float3 q = float3(abs(p.x), abs(p.z), abs(p.y));
                q.xy -= 2.0 * min(dot(k.xy, q.xy), 0.0) * k.xy;
                float2 d = float2(length(q.xy - float2(clamp(q.x, -k.z * h.x, k.z * h.x), h.x)) * sign(q.y - h.x),
                                  q.z - h.y);
                return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
            }

            // Regular octagon (iq): two mirror folds reduce the plane to a single edge. The mascot's plinth is
            // octagonal in the source art, so the hex prism above is not reused for it.
            float SDOctagon(float2 p, float r)
            {
                const float3 k = float3(-0.9238795325, 0.3826834323, 0.4142135623);
                p = abs(p);
                p -= 2.0 * min(dot(float2( k.x, k.y), p), 0.0) * float2( k.x, k.y);
                p -= 2.0 * min(dot(float2(-k.x, k.y), p), 0.0) * float2(-k.x, k.y);
                p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
                return length(p) * sign(p.y);
            }

            // Octagon in XZ, extruded along Y.
            float SDOctPrism(float3 p, float r, float halfH)
            {
                float dXZ = SDOctagon(p.xz, r);
                float dY = abs(p.y) - halfH;
                return min(max(dXZ, dY), 0.0) + length(max(float2(dXZ, dY), 0.0));
            }

            // Tapered box: the pot widens towards its rim, so the half-extent is interpolated along Y. Bounded
            // rather than exact, which is why the tracer damps its steps.
            float SDTaperedBox(float3 p, float halfHeight, float2 bottomHalf, float2 topHalf)
            {
                float t = saturate((p.y + halfHeight) / max(2.0 * halfHeight, 1e-4));
                float2 halfExtent = lerp(bottomHalf, topHalf, t);
                float2 q = abs(p.xz) - halfExtent;
                float dXZ = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
                float dY = abs(p.y) - halfHeight;
                return length(max(float2(dXZ, dY), 0.0)) + min(max(dXZ, dY), 0.0);
            }

            float SDCappedCylinder(float3 p, float radius, float halfHeight)
            {
                float2 d = float2(length(p.xz) - radius, abs(p.y) - halfHeight);
                return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
            }

            // Disc lying in XY, extruded along Z: the head is a flat plate facing the character's forward axis.
            float SDDiscZ(float3 p, float radius, float halfThick)
            {
                float2 d = float2(length(p.xy) - radius, abs(p.z) - halfThick);
                return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
            }

            // Flat annulus in XY: the red rim around the face.
            float SDAnnulusZ(float3 p, float midRadius, float halfWidth, float halfThick)
            {
                float dXY = abs(length(p.xy) - midRadius) - halfWidth;
                float dZ = abs(p.z) - halfThick;
                return min(max(dXY, dZ), 0.0) + length(max(float2(dXY, dZ), 0.0));
            }

            float SDEllipsoid(float3 p, float3 r)
            {
                float k0 = length(p / r);
                float k1 = length(p / (r * r));
                return k0 * (k0 - 1.0) / max(k1, 1e-5);
            }

            // Capped torus (iq): an arc, not a full ring. The smile is the lower arc of a circle, so the cap is
            // rotated to open upward.
            float SDCappedTorus(float3 p, float2 sc, float ra, float rb)
            {
                p.x = abs(p.x);
                float k = (sc.y * p.x > sc.x * p.y) ? dot(p.xy, sc) : length(p.xy);
                return sqrt(max(dot(p, p) + ra * ra - 2.0 * ra * k, 0.0)) - rb;
            }

            // One ring of petals: polar repetition folds space into a single wedge, so one petal evaluation
            // becomes N petals. Symmetry is where sacred geometry gets cheap.
            float PetalRing(float3 p, float petals, float radius, float tilt, float phase, float3 size)
            {
                float sector = 6.28318530718 / petals;
                float a = atan2(p.z, p.x) + phase;
                a = a - sector * floor(a / sector) - sector * 0.5;
                float r = length(p.xz);
                float3 q = float3(cos(a) * r, p.y, sin(a) * r);
                q.x -= radius;
                q.xy = mul(Rot(tilt), q.xy);
                return SDEllipsoid(q - float3(size.x, 0, 0), size);
            }

            // The same polar fold, but about the head's forward (Z) axis, so the petals lie in the face plane.
            float PetalRingXY(float3 p, float petals, float radius, float phase, float3 size)
            {
                float sector = 6.28318530718 / petals;
                float a = atan2(p.y, p.x) + phase;
                a = a - sector * floor(a / sector) - sector * 0.5;
                float r = length(p.xy);
                float3 q = float3(cos(a) * r, sin(a) * r, p.z);
                q.x -= radius;
                return SDEllipsoid(q, size);
            }

            // ==========================================================================================
            // STATE A - the Funobotz mascot, as constructive solid geometry
            //
            // Every part is kept separately so the shading pass can colour each one without a second trace.
            // The distance-only path unions the same fields, so the two can never drift apart.
            // ==========================================================================================
            struct PartsA
            {
                float plinth;   // octagonal tiered base
                float pot;      // tapered white pot
                float stem;     // green stem
                float petals;   // eight yellow petals
                float rim;      // red annulus
                float face;     // dark face disc
                float eyes;     // two amber orbs
                float mouth;    // smile arc + tongue
            };

            PartsA EvalA(float3 p, float t)
            {
                PartsA a = (PartsA)0;

                // --- Plinth: two octagonal tiers, the lower one very slightly inset so the silhouette steps. ---
                float plinthHalf = _PlinthHeight * 0.5;
                float plinthCentre = -0.5 + plinthHalf;
                float capHalf = plinthHalf * 0.30;
                float cap = SDOctPrism(p - float3(0, plinthCentre + plinthHalf - capHalf, 0), _PlinthRadius, capHalf);
                float body = SDOctPrism(p - float3(0, plinthCentre - capHalf, 0), _PlinthRadius * 0.965,
                                        plinthHalf - capHalf);
                a.plinth = min(cap, body);

                // --- Pot: tapered, wider at the rim, sitting on the plinth. ---
                float potHalf = _PotHeightA * 0.5;
                float potCentre = plinthCentre + plinthHalf + potHalf;
                a.pot = SDTaperedBox(p - float3(0, potCentre, 0), potHalf, _PotBottomHalf.xx, _PotTopHalf.xx);

                // --- Stem: from the pot rim up to the underside of the head. ---
                float potTop = potCentre + potHalf;
                float headBottom = _HeadCentre - _RimRadius * 0.55;
                float stemHalf = max((headBottom - potTop) * 0.5, 0.001);
                a.stem = SDCappedCylinder(p - float3(0, potTop + stemHalf, 0), _StemRadius, stemHalf);

                // --- Head: a flat plate in the XY plane, facing the character's forward (+Z) axis. ---
                // A slow idle sway, so the mascot is alive while standing still. It never spins: this is a face.
                float3 h = p - float3(0, _HeadCentre, 0);
                h.xy = mul(Rot(0.055 * sin(t * 0.8)), h.xy);

                float3 size = float3(_MascotPetalSize.x, _MascotPetalSize.y, _MascotPetalSize.z);
                a.petals = PetalRingXY(h, floor(_MascotPetals), _HeadRadius, 0.0, size);

                float rimHalf = max((_RimRadius - _FaceRadius) * 0.5, 0.002);
                a.rim = SDAnnulusZ(h, _FaceRadius + rimHalf, rimHalf, 0.030);

                a.face = SDDiscZ(h - float3(0, 0, 0.004), _FaceRadius, 0.028);

                // Eyes: spheres set into the face and standing slightly proud of it.
                float3 e = h - float3(0, 0.024, 0.026);
                e.x = abs(e.x) - 0.040;                       // mirror: one evaluation, two eyes
                a.eyes = length(e) - 0.026;

                // Smile: the lower arc of a circle. Y is flipped so the capped torus opens upward. Z is scaled up
                // and the distance divided by the same factor, which squashes the arc into a flat ink line
                // without breaking the metric. It sits at z = 0.034, just proud of the face disc's front at
                // 0.032, so it reads as painted on rather than sunk into it.
                float3 s = float3(h.x, -(h.y + 0.012), (h.z - 0.034) * 2.4);
                const float2 smileArc = float2(0.9063, 0.4226);   // sin/cos of 65 degrees
                float smile = SDCappedTorus(s, smileArc, 0.056, 0.011) / 2.4;

                // Tongue: hanging off the right end of the smile, as in the source art.
                float tongue = SDEllipsoid(h - float3(0.031, -0.054, 0.034), float3(0.025, 0.021, 0.015));
                a.mouth = min(smile, tongue);

                return a;
            }

            // Distance-only union of State A. Hot path: no colours are touched.
            float MapA(float3 p, float t)
            {
                PartsA a = EvalA(p, t);
                float shell = SMin(a.plinth, a.pot, 0.010);       // plinth and pot read as one moulded body
                shell = SMin(shell, a.stem, 0.008);
                float head = min(min(a.petals, a.rim), a.face);
                head = min(head, min(a.eyes, a.mouth));
                return SMin(shell, head, 0.006);
            }

            // ==========================================================================================
            // STATE B - the titanium mandala. PRESERVED VERBATIM from the pre-shift shader.
            //
            // `isCore` returns 1 near the emissive heart, `isHousing` 1 on the moulded shell - that is how one
            // trace shades three materials without ever splitting the object into meshes.
            // ==========================================================================================
            float FieldB(float3 p, float t, out float isCore, out float isHousing)
            {
                float bloom = _Bloom;

                // --- Housing: hex base -> tapered pot -> stem. Static, so no time term touches it. ---
                float baseHalf = _BaseHeight * 0.5;
                float baseCentre = -0.5 + baseHalf;
                float hexBase = SDHexPrism(p - float3(0, baseCentre, 0), float2(_BaseRadius, baseHalf));

                float potHalf = _PotHeight * 0.5;
                float potCentre = baseCentre + baseHalf + potHalf;
                float pot = SDTaperedBox(p - float3(0, potCentre, 0), potHalf,
                                         _PotBottomHalf.xx, _PotTopHalf.xx);

                float potTop = potCentre + potHalf;
                float stemTop = _FlowerHeight - 0.04;
                float stemHalf = max((stemTop - potTop) * 0.5, 0.001);
                float stem = SDCappedCylinder(p - float3(0, potTop + stemHalf, 0), _StemRadius, stemHalf);

                float housing = min(min(hexBase, pot), stem);

                // --- Flower: lifted so it emerges from the rim of the pot. ---
                p -= float3(0, _FlowerHeight, 0);
                p.xz = mul(Rot(t * _SpinSpeed * 0.35), p.xz);

                float core = length(p) - (_CoreRadius * 0.34 * (1.0 + 0.18 * sin(t * 2.3) + 0.5 * _Pulse));

                float petals = floor(_Petals);
                float3 size = float3(_PetalLength, _PetalThickness, _PetalWidth);

                float tilt1 = lerp(-1.25, -0.12, bloom) + 0.06 * sin(t * 0.9);
                float ring1 = PetalRing(p, petals, _ShellRadius * 0.34, tilt1, t * _SpinSpeed * 0.20, size);

                float tilt2 = lerp(-1.35, -0.45, bloom) + 0.05 * sin(t * 1.1 + 1.7);
                float ring2 = PetalRing(p, petals, _ShellRadius * 0.22, tilt2,
                                        t * -_SpinSpeed * 0.28 + 3.14159 / max(petals, 1.0),
                                        size * float3(0.72, 0.9, 0.8));

                float3 q = p;
                int folds = (int)_FoldCount;
                float scale = 1.0;
                [loop]
                for (int i = 0; i < folds; i++)
                {
                    q.xz = abs(q.xz) - 0.055;
                    q.xz = mul(Rot(0.785398 + t * 0.10), q.xz);
                    q *= 1.28;
                    scale *= 1.28;
                }
                float tilt3 = lerp(-1.45, -0.95, bloom);
                float ring3 = PetalRing(q, petals, _ShellRadius * 0.30, tilt3, 0.0,
                                        size * float3(0.55, 1.1, 0.5)) / scale;

                float3 r1 = p;
                r1.xz = mul(Rot(-t * _SpinSpeed * 0.8), r1.xz);
                float halo1 = length(float2(length(r1.xz) - _ShellRadius * 0.52 * (0.6 + 0.4 * bloom), r1.y)) - 0.006;

                float petalsField = SMin(ring1, ring2, 0.010);
                float mechanism = SMin(ring3, core, 0.012);
                float flower = SMin(petalsField, mechanism, 0.010);
                flower = min(flower, halo1);

                isCore = saturate((petalsField - mechanism) * 26.0 + 0.35);

                // Nearest part wins the material. The stem meets the flower with a small blend so the two read as
                // one machined object rather than two primitives touching.
                float d = SMin(flower, housing, 0.008);
                isHousing = saturate((flower - housing) * 40.0 + 0.5);
                isCore *= 1.0 - isHousing;
                return d;
            }

            float MapB(float3 p, float t)
            {
                float ignoredCore = 0.0, ignoredHousing = 0.0;
                return FieldB(p, t, ignoredCore, ignoredHousing);
            }

            // ==========================================================================================
            // The combined field
            // ==========================================================================================

            // Morph, not cut: the mascot's distance field bends into the mandala's. Used by the trace loop and by
            // the normal taps, so it must stay free of colour work.
            // The morph is spatial, not uniform. Lerping the whole body by one scalar dissolves the mascot and
            // the mandala into each other everywhere at once, which reads as a glitch. Weighting the blend by
            // distance from the avatar's centre makes the anomaly grow outward: freeze it halfway and the
            // cartoon shell is still standing while the mathematical core has already broken out through it.
            float StateAt(float3 p)
            {
                float bias = _MorphCoreFirst;
                float r = saturate(length(p) * 2.0);     // 0 at the centre, 1 at the bounding cube's face
                return saturate(_StateBlend * (1.0 + bias) - r * bias);
            }

            float MapDistance(float3 p, float t)
            {
                // Single exit with an initialised result: multiple returns read as "potentially uninitialised"
                // to FXC, and the pure states still evaluate only one of the two fields.
                half s = _StateBlend;
                float d = 0.0;
                if (s <= 0.001h)      d = MapA(p, t);
                else if (s >= 0.999h) d = MapB(p, t);
                else                  d = lerp(MapA(p, t), MapB(p, t), StateAt(p));
                return d;
            }

            // 4-tap tetrahedral gradient: the cheapest usable normal, evaluated only where a ray actually hits.
            float3 FieldNormal(float3 p, float t, float eps)
            {
                float2 k = float2(1.0, -1.0);
                return normalize(k.xyy * MapDistance(p + k.xyy * eps, t) +
                                 k.yyx * MapDistance(p + k.yyx * eps, t) +
                                 k.yxy * MapDistance(p + k.yxy * eps, t) +
                                 k.xxx * MapDistance(p + k.xxx * eps, t));
            }

            // ==========================================================================================
            // Material resolve - COLD PATH. Called once, at the hit point.
            // ==========================================================================================

            Surf MakeSurf(half3 albedo, half3 emission, half gloss, half spec, half diffuse, half aniso, half outline)
            {
                Surf s = (Surf)0;
                s.albedo = albedo;
                s.emission = emission;
                s.gloss = gloss;
                s.spec = spec;
                s.diffuse = diffuse;
                s.aniso = aniso;
                s.outline = outline;
                return s;
            }

            Surf LerpSurf(Surf a, Surf b, half h)
            {
                Surf s = (Surf)0;
                s.albedo = lerp(a.albedo, b.albedo, h);
                s.emission = lerp(a.emission, b.emission, h);
                s.gloss = lerp(a.gloss, b.gloss, h);
                s.spec = lerp(a.spec, b.spec, h);
                s.diffuse = lerp(a.diffuse, b.diffuse, h);
                s.aniso = lerp(a.aniso, b.aniso, h);
                s.outline = lerp(a.outline, b.outline, h);
                return s;
            }

            // Smooth material select: whichever part is nearest wins, with a narrow blend band so adjacent parts
            // do not alias into a hard seam at the silhouette.
            void Pick(inout float dAcc, inout Surf sAcc, float d, Surf s, float k)
            {
                half h = (half)saturate(0.5 + 0.5 * (dAcc - d) / k);
                sAcc = LerpSurf(sAcc, s, h);
                dAcc = lerp(dAcc, d, h) - k * h * (1.0 - h);
            }

            Surf ShadeA(float3 p, float t)
            {
                PartsA a = EvalA(p, t);

                const half3 unlit = half3(0.0h, 0.0h, 0.0h);

                // Moulded plastic everywhere on the body: diffuse-dominant, barely glossy, takes the ink line.
                Surf white  = MakeSurf(_HousingColor.rgb, unlit, 14.0h, _HousingSpecular, 1.35h, 0.0h, 1.0h);
                Surf green  = MakeSurf(_StemGreen.rgb,    unlit, 18.0h, 0.18h,            1.25h, 0.0h, 1.0h);
                Surf yellow = MakeSurf(_PetalYellow.rgb,  unlit, 16.0h, 0.20h,            1.40h, 0.0h, 1.0h);
                Surf red    = MakeSurf(_RimRed.rgb,       unlit, 20.0h, 0.26h,            1.30h, 0.0h, 1.0h);
                Surf dark   = MakeSurf(_FaceDark.rgb,     unlit, 24.0h, 0.30h,            1.00h, 0.0h, 1.0h);
                Surf ink    = MakeSurf(_SmileWhite.rgb,   unlit, 12.0h, 0.10h,            1.45h, 0.0h, 0.0h);
                // The eyes are the only emissive part of the mascot, which is what makes them read as lit bulbs.
                Surf eye    = MakeSurf(_SmileWhite.rgb * 0.30h, _EyeGlow.rgb * _EmissionGain,
                                       48.0h, 0.9h, 0.6h, 0.0h, 0.0h);

                float d = a.plinth;
                Surf s = white;
                Pick(d, s, a.pot,    white,  0.010);
                Pick(d, s, a.stem,   green,  0.008);
                Pick(d, s, a.petals, yellow, 0.006);
                Pick(d, s, a.rim,    red,    0.005);
                Pick(d, s, a.face,   dark,   0.005);
                Pick(d, s, a.mouth,  ink,    0.003);
                Pick(d, s, a.eyes,   eye,    0.004);
                return s;
            }

            Surf ShadeB(float3 p, float t)
            {
                float isCore = 0.0, isHousing = 0.0;
                FieldB(p, t, isCore, isHousing);

                // Metal: little diffuse, strong tinted specular. Housing: diffuse-dominant, barely glossy.
                half3 metal = lerp(_MetalColor.rgb, _AlignedColor.rgb * 0.35h, _Alignment);
                half3 albedo = lerp(metal, _HousingColor.rgb, (half)isHousing);

                half3 emissive = lerp(_ShellColor.rgb, _CoreColor.rgb, (half)isCore);
                emissive = lerp(emissive, _AlignedColor.rgb, _Alignment);
                emissive *= (half)isCore * 0.9h * _EmissionGain * (1.0h + 0.9h * _Pulse);
                emissive *= 1.0h - (half)isHousing;   // the shell is moulded plastic: it reflects, never glows

                return MakeSurf(albedo,
                                emissive,
                                lerp(_Shininess, 12.0h, (half)isHousing),
                                lerp(_SpecularGain, _HousingSpecular, (half)isHousing),
                                lerp(0.35h, 1.35h, (half)isHousing),
                                lerp(_Anisotropy, 0.0h, (half)isHousing),
                                (half)isHousing * 0.35h);        // the anomaly is barely inked: it is not a cartoon
            }

            Surf ShadeField(float3 p, float t)
            {
                // Must use the same spatial weight as MapDistance, or a surface traced as mascot shell gets
                // shaded as titanium and the split stops reading as one object coming apart.
                half s = _StateBlend;
                Surf r = (Surf)0;
                if (s <= 0.001h)      r = ShadeA(p, t);
                else if (s >= 0.999h) r = ShadeB(p, t);
                else                  r = LerpSurf(ShadeA(p, t), ShadeB(p, t), (half)StateAt(p));
                return r;
            }

            FragOut Frag(Varyings input)
            {
                FragOut o;

                float3 ro = input.cameraOS;
                float3 rd = normalize(input.positionOS - ro);

                float3 inv = 1.0 / (rd + 1e-6);
                float3 t0 = (-0.5 - ro) * inv;
                float3 t1 = ( 0.5 - ro) * inv;
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);
                float enter = max(max(max(tmin.x, tmin.y), tmin.z), 0.0);
                float exitT = min(min(tmax.x, tmax.y), tmax.z);
                if (exitT <= enter) discard;

                float t = _Time.y;
                int steps = (int)_Steps;
                float travelled = enter;
                bool hit = false;

                // Sphere tracing: step by the distance to the field. Polar folds already break the exact metric,
                // and a lerp of two fields breaks it further, so the step is damped hardest at the halfway point
                // of the morph, where neither field's Lipschitz bound holds.
                float damp = lerp(0.55, 0.42, 4.0 * _StateBlend * (1.0 - _StateBlend));

                [loop]
                for (int i = 0; i < steps; i++)
                {
                    float3 p = ro + rd * travelled;
                    float d = MapDistance(p, t);
                    if (d < 0.0012) { hit = true; break; }
                    travelled += max(d * damp, 0.0015);
                    if (travelled > exitT) break;
                }

                if (!hit) discard;

                float3 pHit = ro + rd * travelled;
                float3 nOS = FieldNormal(pHit, t, 0.0016);
                Surf surf = ShadeField(pHit, t);

                float3 positionWS = mul(GetObjectToWorldMatrix(), float4(pHit, 1.0)).xyz;
                float3 N = normalize(TransformObjectToWorldNormal(nOS));
                float3 V = GetWorldSpaceNormalizeViewDir(positionWS);

                // Brushed anisotropy: the brush runs around the petal, so the highlight is squashed along the
                // tangential direction instead of sampling an anisotropy map.
                float3 up = abs(N.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 tangent = normalize(cross(N, up));

                half3 diffuse = 0;
                half3 specular = 0;

                uint count = (uint)(_SwarmVPLParams.x + 0.5);
                float invRangeSq = _SwarmVPLParams.z;

#if defined(SWARM_LEGACY_VPL_LOOP)
                uint firstLight = 0u;           // legacy: every VPL integrated here, per fragment
#else
                // The swarm arrives as two trilinear fetches. Petalo sits at the centre of the volume by
                // construction, so this is the cheap path for the most expensive pixels in the frame.
                float3 volumeUVW = (positionWS - _SwarmVolumeMin.xyz) * _SwarmVolumeInvSize.xyz;
                float4 probeL0 = SAMPLE_TEXTURE3D_LOD(_SwarmIrrL0, sampler_SwarmIrrL0, volumeUVW, 0);
                float4 probeL1 = SAMPLE_TEXTURE3D_LOD(_SwarmIrrL1, sampler_SwarmIrrL1, volumeUVW, 0);

                float3 swarmL = probeL1.xyz;
                float  dirness = probeL1.w;
                float  swarmNDotL = saturate(dot(N, swarmL));

                diffuse += probeL0.rgb * lerp(0.5, swarmNDotL, dirness);

                float3 swarmH = normalize(swarmL + V);
                float  swarmAniso = 1.0 - surf.aniso * abs(dot(swarmH, tangent));
                specular += probeL0.rgb * (dirness * pow(saturate(dot(N, swarmH)),
                                                         surf.gloss * max(swarmAniso, 0.05)) * swarmNDotL);

                // Beacons only: Petalo's own light and the forge's, which live outside the volume's reach.
                uint beacons = (uint)(_SwarmVPLParams.w + 0.5);
                uint firstLight = count > beacons ? count - beacons : 0u;
#endif

                [loop]
                for (uint li = firstLight; li < count; li++)
                {
                    SwarmVPL l = _SwarmVPLs[li];
                    float3 toLight = l.posSoftSq.xyz - positionWS;
                    float d2 = dot(toLight, toLight);
                    float3 L = toLight * rsqrt(d2 + 1e-8);

                    float window = saturate(1.0 - d2 * invRangeSq);
                    float3 E = l.color.rgb * (window * window * rcp(d2 + l.posSoftSq.w));

                    float nDotL = saturate(dot(N, L));
                    diffuse += E * nDotL;

                    float3 H = normalize(L + V);
                    float nDotH = saturate(dot(N, H));
                    float aniso = 1.0 - surf.aniso * abs(dot(H, tangent));
                    specular += E * pow(nDotH, surf.gloss * max(aniso, 0.05)) * nDotL;
                }

                half fresnel = (half)pow(1.0 - saturate(dot(N, V)), _FresnelPower);
                half3 shell = surf.albedo * (diffuse * surf.diffuse + specular * surf.spec + (half)_SwarmAmbient)
                            + surf.albedo * fresnel * 0.25h;

                // Orange edge accent: the moulded shell's rims catch the accent colour, as on the real housing.
                half housingEdge = (half)saturate(1.0 - abs(dot(N, normalize(N + float3(0.0, 1.0, 0.0)))) * 1.6);
                shell += _AccentColor.rgb * surf.outline * housingEdge * fresnel * 1.4h;

                // Emission: the heart of the anomaly, or the mascot's eyes. Plus light leaking from crevices
                // where folds pinch together, which only the metal body has.
                float ao = saturate(MapDistance(pHit + nOS * _CreviceWidth, t) / max(_CreviceWidth, 1e-4));
                half crevice = (half)((1.0 - ao) * (1.0 - ao));
                half3 emissive = surf.emission + surf.albedo * crevice * 0.8h * _EmissionGain
                               * (1.0h - surf.outline) * _StateBlend;

                half3 rgb = shell + emissive;

                // The cartoon ink line. The source art is a flat illustration with a heavy black outline; a
                // silhouette here is exactly where the surface turns away from the eye, so the outline is a
                // fresnel term on the inked materials rather than a second pass of extruded hull geometry.
                half ink = (half)smoothstep(0.62, 0.97, 1.0 - abs(dot(N, V))) * surf.outline * _ToonOutline
                         * (1.0h - _StateBlend);
                rgb = lerp(rgb, half3(0.02h, 0.02h, 0.025h), ink);

                o.color = half4(rgb, 1.0h);

                float4 clip = TransformWorldToHClip(positionWS);
                o.depth = clip.z / clip.w;
                return o;
            }
            ENDHLSL
        }

        // Depth-only, so any prepass or depth consumer sees the bounding volume rather than nothing.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; };

            V DepthVert(A input)
            {
                V o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return o;
            }

            half DepthFrag(V input) : SV_Target { return input.positionCS.z; }
            ENDHLSL
        }
    }

    FallBack Off
}
