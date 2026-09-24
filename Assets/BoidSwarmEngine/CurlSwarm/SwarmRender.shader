// SwarmRender.shader
// URP unlit procedural renderer for the curl swarm. No vertex buffers: 4 verts / particle via SV_VertexID,
// shared through a 32-bit index buffer (6 indices / quad) for post-transform cache reuse.
Shader "MAAYAI/SwarmRender"
{
    Properties
    {
        [HDR] _ColorSlow ("Slow Emission", Color) = (0.05, 0.35, 1.6, 1)
        [HDR] _ColorFast ("Fast Emission", Color) = (4.0, 1.1, 0.25, 1)
        _EmissionIntensity ("Emission Intensity", Range(0, 16)) = 2.0
        _EmissionSpeedBoost ("Speed Boost (EV stops)", Range(0, 6)) = 2.5
        _ParticleScale ("Particle Scale (quad half-extent, m)", Float) = 0.1
        _VelocityStretch ("Velocity Stretch", Range(0, 6)) = 2.0

        [Header(Fill Rate)]
        [Tooltip(Hard ceiling on a particles half extent in rendered pixels. Bounds total swarm fragment cost)]
        _MaxPixelSize ("Max Pixel Size", Range(2, 64)) = 6
        [Tooltip(Particles closer than this to the eye in metres are collapsed away entirely)]
        _NearCull ("Near Cull (m)", Range(0.001, 1)) = 0.04
        [Tooltip(Fully sized again by this distance in metres)]
        _NearFade ("Near Fade (m)", Range(0.01, 2)) = 0.22
        [Tooltip(How much brightness a clamped particle may recover so the cloud keeps its energy)]
        _MaxEnergyBoost ("Max Energy Boost", Range(1, 8)) = 3

        [Header(Bead Shading)]
        [Tooltip(0 is a soft additive haze and 1 is a distinct lit bead)]
        _BeadShape ("Bead Shape", Range(0, 1)) = 0.85
        _BeadRim ("Bead Rim", Range(0, 2)) = 0.9
        _BeadCore ("Bead Core Hotspot", Range(0, 2)) = 0.7
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SwarmUnlit"
            Tags { "LightMode" = "UniversalForward" }

            // Additive: order-independent (no sorting), no discard (keeps HSR alive on TBDR GPUs).
            // ZTest Always: AR plane / occlusion depth can never hide the swarm.
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // A/B for profiling: world-sized billboards with no screen clamp, as before. See SwarmSurface.
            #pragma multi_compile_vertex _ SWARM_LEGACY_PARTICLE_SIZE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle
            {
                float4 posLife;
                float4 velInvLife;
            };

            StructuredBuffer<Particle> _Particles;
            float4x4 _SwarmLocalToWorld;
            float    _InvMaxSpeed;

            CBUFFER_START(UnityPerMaterial)
                half4 _ColorSlow;
                half4 _ColorFast;
                half  _EmissionIntensity;
                half  _EmissionSpeedBoost;
                float _ParticleScale;
                float _BeadShape;
                float _BeadRim;
                float _BeadCore;
                float _VelocityStretch;
                float _MaxPixelSize;
                float _NearCull;
                float _NearFade;
                float _MaxEnergyBoost;
            CBUFFER_END

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half2  uv         : TEXCOORD0;
                half3  color      : TEXCOORD1;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;

                uint pid    = vertexID >> 2;
                uint corner = vertexID & 3u;
                // corner 0..3 -> (-1,-1) (1,-1) (1,1) (-1,1)
                float2 uv = float2(((corner + 1u) >> 1) & 1u, corner >> 1) * 2.0 - 1.0;

                Particle p = _Particles[pid];

                float speedN = saturate(length(p.velInvLife.xyz) * _InvMaxSpeed);
                float lifeT  = saturate(p.posLife.w * p.velInvLife.w);
                float fade   = saturate(lifeT * 5.0) * saturate((1.0 - lifeT) * 10.0);

                float3 posWS = mul(_SwarmLocalToWorld, float4(p.posLife.xyz, 1.0)).xyz;
                float3 velWS = mul((float3x3)_SwarmLocalToWorld, p.velInvLife.xyz);

                float3 posVS = TransformWorldToView(posWS);
                float2 velVS = mul((float3x3)UNITY_MATRIX_V, velWS).xy;

                // View-space velocity-aligned billboard (streak).
                float  vl2  = dot(velVS, velVS);
                float2 axis = vl2 > 1e-10 ? velVS * rsqrt(vl2) : float2(1.0, 0.0);
                float2 perp = float2(-axis.y, axis.x);

                // Dead / fading particles shrink to zero area -> rasterizer rejects them, zero fragment cost.
                // Quad half-extent in view-space metres: scale x per-particle jitter x life fade.
                float size    = max(_ParticleScale, 0.0) * (0.6 + 0.8 * frac(pid * 0.6180339887)) * fade;
                float stretch = 1.0 + _VelocityStretch * speedN;

                // ---- Fill-rate control -------------------------------------------------------------------
                // A world-sized billboard is a fragment bomb: walk the camera into the cloud and a 2 mm bead
                // covers a third of the screen, 16k times over, additively. Two bounds fix that without
                // touching the particle count.
                float viewDepth = -posVS.z;                         // metres in front of the eye (perspective)

#if defined(SWARM_LEGACY_PARTICLE_SIZE)
                float energy = 1.0;
                float nearFade = 1.0;
#else

                // 1. Screen-space clamp. Pixels per metre at this depth comes straight out of the projection:
                //    P._m11 is cot(fov/2), and half the render target's height converts NDC to pixels. Note
                //    _ScreenParams is the RENDER target, so this already accounts for URP's 0.8 render scale.
                float pixelsPerMetre = UNITY_MATRIX_P._m11 * 0.5 * _ScreenParams.y * rcp(max(viewDepth, 1e-4));
                // The streak axis is 'stretch' times longer, so the cap is divided by it: the LONG edge is what
                // has to obey the pixel budget, otherwise a fast particle busts it by up to 3x.
                float maxMetres      = _MaxPixelSize * rcp(max(pixelsPerMetre, 1e-4)) * rcp(max(stretch, 1.0));
                float clamped        = min(size, maxMetres);

                // Shrinking a bead steals its contribution, so give the lost area back as brightness - capped,
                // because an uncapped 1/x boost turns a bead pressed against the lens into a white screen.
                float energy = min(size * size * rcp(max(clamped * clamped, 1e-12)), _MaxEnergyBoost);

                // 2. Near-plane collapse. Inside _NearCull the quad degenerates to a point: four identical
                //    vertices, zero area, culled before rasterisation. No fragments, no discard, no blending.
                float nearFade = saturate((viewDepth - _NearCull) * rcp(max(_NearFade - _NearCull, 1e-4)));

                size = clamped * nearFade;
#endif
                // ------------------------------------------------------------------------------------------

                posVS.xy += (axis * (uv.x * stretch) + perp * uv.y) * size;
                o.positionCS = TransformWViewToHClip(posVS);

                half  sN  = (half)speedN;
                half3 col = lerp(_ColorSlow.rgb, _ColorFast.rgb, sN);
                o.color = col * (_EmissionIntensity * exp2(_EmissionSpeedBoost * sN)
                                 * (half)(fade * energy * nearFade));
                o.uv    = (half2)uv;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half r2 = (half)dot(i.uv, i.uv);
                half disc = saturate(1.0h - r2);
                if (disc <= 0.0h) discard;          // outside the bead: nothing to add

                // Soft haze, which is what the quad drew before: falls off smoothly to nothing.
                half haze = disc * disc;

                // Bead: treat the quad as the face of a sphere and shade it. The implicit normal gives a core
                // hotspot where it faces the eye and a bright rim at the silhouette, which is what separates a
                // cloud of distinct luminous spheres from a single additive fog.
                half z = sqrt(disc);                 // sphere normal's z, radius 1
                half core = pow(z, 3.0h) * _BeadCore;
                half rim  = pow(1.0h - z, 2.0h) * _BeadRim;
                half edge = (half)smoothstep(0.0h, 0.22h, disc);   // a definite edge instead of an endless fade
                half bead = (core + rim + 0.35h) * edge;

                half a = lerp(haze, bead, (half)_BeadShape);
                return half4(i.color * a, 0.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
