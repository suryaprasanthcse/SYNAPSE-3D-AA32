// SwarmSurface.shader
// Stage B: forward surface shader lit exclusively by the swarm's 64 virtual point lights.
// No ambient, no main/additional lights, no shadows, no reflection probes: in the void those are all zero,
// so skipping URP's light loop is both cheaper and exact.
//
// Lighting per VPL: wrapped Lambert x inverse-square with a soft core (no singularity when a surface enters a
// cell) x smooth range window, plus an energy-normalised Blinn-Phong lobe. Albedo = _BaseMap x _BaseColor,
// so card artwork is revealed only where swarm light lands.
//
// Two render modes:
//   Opaque (0)        - the surface IS the object: albedo x swarm light, writes depth. For virtual geometry
//                       (the cave, holograms) that must occlude what is behind it.
//   LightReceiver (1) - the surface is a STAND-IN for something real that the camera already shows: the printed
//                       card, the physical desk. It must not draw its own albedo over the passthrough feed or it
//                       hides the player's hands. It contributes only the light the swarm casts onto that real
//                       surface: additive, no depth write, albedo used solely as reflectance.
//
// Adreno notes: all pixels read the same 64 VPLs in the same order (uniform, cache-resident 2 KB buffer);
// uniform loop bound; per light: 1 rsqrt, 1 rcp, 1 exp2 (pow), no branches.
Shader "MAAYAI/SwarmSurface"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)

        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        _Wrap ("Diffuse Wrap", Range(0, 1)) = 0.15
        _Smoothness ("Smoothness", Range(0, 1)) = 0.35
        _SpecularStrength ("Specular Strength", Range(0, 1)) = 0.2

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2

        [Header(Key Light)]
        _KeyLightGain ("Key Light Gain", Range(0, 4)) = 1
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.7

        [Header(Dissolve)]
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _DissolveScale ("Dissolve Noise Scale (1/world m)", Float) = 16
        _DissolveEdge ("Dissolve Edge Width", Range(0.001, 0.5)) = 0.08
        [HDR] _DissolveEdgeColor ("Dissolve Edge Colour", Color) = (0.3, 1.6, 2.4, 1)

        [Enum(Opaque, 0, LightReceiver, 1, ShadowCatcher, 2)] _RenderMode ("Render Mode", Float) = 0
        _ReceiverGain ("Light Receiver Gain", Range(0, 2)) = 1
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 1      // One
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 0      // Zero (opaque)
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float  _KeyLightGain;
            float  _ShadowStrength;
            half4  _BaseColor;
            half   _Cutoff;
            half   _Wrap;
            half   _Smoothness;
            half   _SpecularStrength;
            half   _RenderMode;
            half   _ReceiverGain;
            float  _Dissolve;
            float  _DissolveScale;
            float  _DissolveEdge;
            half4  _DissolveEdgeColor;
        CBUFFER_END

        half4 SampleAlbedo(float2 uv)
        {
            half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
            #if defined(_ALPHATEST_ON)
                clip(albedo.a - _Cutoff);
            #endif
            return albedo;
        }

        // Act dissolve: world-space value noise, two octaves. Evaluated only while a material is actually
        // dissolving (a uniform branch on _Dissolve), so the resting architecture pays nothing for it.
        float DissolveHash(float3 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return frac((p.x + p.y) * p.z);
        }

        float DissolveValueNoise(float3 p)
        {
            float3 i = floor(p);
            float3 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            float n000 = DissolveHash(i);
            float n100 = DissolveHash(i + float3(1, 0, 0));
            float n010 = DissolveHash(i + float3(0, 1, 0));
            float n110 = DissolveHash(i + float3(1, 1, 0));
            float n001 = DissolveHash(i + float3(0, 0, 1));
            float n101 = DissolveHash(i + float3(1, 0, 1));
            float n011 = DissolveHash(i + float3(0, 1, 1));
            float n111 = DissolveHash(i + float3(1, 1, 1));
            float x00 = lerp(n000, n100, f.x), x10 = lerp(n010, n110, f.x);
            float x01 = lerp(n001, n101, f.x), x11 = lerp(n011, n111, f.x);
            return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
        }

        // Returns the edge glow (0..1) and clips everything already dissolved. At _Dissolve = 1 every pixel is
        // clipped (the noise never reaches 1); at 0 nothing is evaluated.
        float DissolveClip(float3 positionWS)
        {
            float edge = 0.0;
            [branch]
            if (_Dissolve > 1e-4)
            {
                float3 p = positionWS * _DissolveScale;
                float n = DissolveValueNoise(p) * 0.65 + DissolveValueNoise(p * 2.13 + 17.1) * 0.35;
                float threshold = _Dissolve * (1.0 + _DissolveEdge);
                clip(n - threshold);
                edge = 1.0 - saturate((n - threshold) / _DissolveEdge);
            }
            return edge;
        }
        ENDHLSL

        Pass
        {
            Name "SwarmForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest LEqual
            Blend [_SrcBlend] [_DstBlend]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing

            // The swarm alone cannot make plaster read as a physical object on a desk: point lights with no
            // occlusion give no contact, and contact is what sells "this is sitting there". These bring in URP's
            // main light and its cascaded soft shadow map.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            // A/B for profiling: the pre-volume path (every VPL integrated per fragment) stays in the build so a
            // single APK can be captured both ways on device. Toggled globally by SwarmMatrixDiagnostics.
            #pragma multi_compile_fragment _ SWARM_LEGACY_VPL_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct SwarmVPL
            {
                float4 posSoftSq;   // xyz world position, w soft-core radius^2
                float4 color;       // rgb linear flux
            };

            StructuredBuffer<SwarmVPL> _SwarmVPLs;
            float4 _SwarmVPLParams;     // x total count, y gain, z 1 / range^2, w beacon count

            // The swarm's 64 cells arrive pre-integrated as an L1 irradiance volume (SwarmVPL.compute, CSVolume):
            // two trilinear fetches instead of a 64-iteration loop per fragment. The volume is axis aligned in
            // world space, so the lookup is three mads. Outside it the border texels are zero and the sampler
            // clamps, so unlit space stays exactly black with no branch.
            TEXTURE3D(_SwarmIrrL0);
            SAMPLER(sampler_SwarmIrrL0);
            TEXTURE3D(_SwarmIrrL1);
            SAMPLER(sampler_SwarmIrrL1);
            float4 _SwarmVolumeMin;         // xyz: world corner of the volume
            float4 _SwarmVolumeInvSize;     // xyz: 1 / world size, w: 1 when the volume is live
            float  _SwarmSurfaceDebug;  // global: 0 lit, 1 albedo only (texture check), 2 swarm light only (VPL check)

            // Global ambient floor. The void lights every surface from the swarm alone, which is multiplicative:
            // with no VPL nearby a surface resolves to EXACTLY black, so a world the player has not reached yet
            // is not dim, it is absent. This adds a small constant so geometry is always faintly readable.
            // Set it to 0 to get the original pure-void behaviour back.
            float  _SwarmAmbient;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                o.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half4 Frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                half4 albedo = SampleAlbedo(input.uv);

                float3 posWS = input.positionWS;
                float dissolveEdge = DissolveClip(posWS);
                float3 N = normalize(input.normalWS);
                N = isFrontFace ? N : -N;                       // two-sided cards / planes light correctly
                float3 V = GetWorldSpaceNormalizeViewDir(posWS);

                float wrap      = _Wrap;
                float invWrap   = rcp(1.0 + wrap);
                float shininess = exp2(10.0 * _Smoothness + 1.0);
                float specNorm  = (shininess + 8.0) * (1.0 / (8.0 * PI));
                float invRangeSq = _SwarmVPLParams.z;

                float3 diffuse  = 0.0;
                float3 specular = 0.0;

#if defined(SWARM_LEGACY_VPL_LOOP)
                // Legacy: 64 swarm cells + beacons, integrated per fragment. Kept only for A/B captures.
                uint legacyCount = (uint)(_SwarmVPLParams.x + 0.5);
                [loop]
                for (uint k = 0u; k < legacyCount; k++)
                {
                    SwarmVPL l = _SwarmVPLs[k];
                    float3 toLight = l.posSoftSq.xyz - posWS;
                    float  d2      = dot(toLight, toLight);
                    float3 L       = toLight * rsqrt(d2 + 1e-8);
                    float  window  = saturate(1.0 - d2 * invRangeSq);
                    float3 E       = l.color.rgb * (window * window * rcp(d2 + l.posSoftSq.w));
                    float  nDotL   = dot(N, L);
                    diffuse += E * saturate((nDotL + wrap) * invWrap);
                    float3 H = normalize(L + V);
                    specular += E * (pow(saturate(dot(N, H)), shininess) * saturate(nDotL));
                }
#else
                // --- The swarm, reconstructed from the irradiance volume ---
                float3 volumeUVW = (posWS - _SwarmVolumeMin.xyz) * _SwarmVolumeInvSize.xyz;
                float4 probeL0 = SAMPLE_TEXTURE3D_LOD(_SwarmIrrL0, sampler_SwarmIrrL0, volumeUVW, 0);
                float4 probeL1 = SAMPLE_TEXTURE3D_LOD(_SwarmIrrL1, sampler_SwarmIrrL1, volumeUVW, 0);

                float3 swarmE   = probeL0.rgb;
                float3 swarmL   = probeL1.xyz;
                float  dirness  = probeL1.w;                 // 0: light arrives from all sides, 1: one direction

                // Wrapped Lambert against the mean direction, blended toward a flat response as the arriving
                // light loses direction - a surface deep inside the cloud is lit from everywhere, not from a point.
                float  swarmNDotL = dot(N, swarmL);
                diffuse += swarmE * lerp(0.5, saturate((swarmNDotL + wrap) * invWrap), dirness);

                float3 swarmH = normalize(swarmL + V);
                specular += swarmE * (dirness * pow(saturate(dot(N, swarmH)), shininess) * saturate(swarmNDotL));

                // --- Beacons: Petalo and the ignited forge. ---
                // These stay per-fragment on purpose. They travel far outside the swarm's volume (the forge sits
                // in the Tholos while Petalo carries the swarm), and they are the lights the player reads as
                // "my lamp is touching that stone". Four iterations, not sixty-four.
                uint total   = (uint)(_SwarmVPLParams.x + 0.5);
                uint beacons = (uint)(_SwarmVPLParams.w + 0.5);
                uint first   = total > beacons ? total - beacons : 0u;

                [loop]
                for (uint i = first; i < total; i++)
                {
                    SwarmVPL l = _SwarmVPLs[i];

                    float3 toLight = l.posSoftSq.xyz - posWS;
                    float  d2      = dot(toLight, toLight);
                    float3 L       = toLight * rsqrt(d2 + 1e-8);

                    float  window  = saturate(1.0 - d2 * invRangeSq);
                    float3 E       = l.color.rgb * (window * window * rcp(d2 + l.posSoftSq.w));

                    float  nDotL   = dot(N, L);
                    diffuse += E * saturate((nDotL + wrap) * invWrap);

                    float3 H = normalize(L + V);
                    specular += E * (pow(saturate(dot(N, H)), shininess) * saturate(nDotL));
                }
#endif

                // --- Key light: one shadowed directional, shared by every SwarmSurface. ---
                // Dark AR runs with the gain at zero, and the shadow-map fetch behind this is not free, so the
                // whole block is skipped by a uniform branch rather than multiplied out at the end.
                float keyShadow = 1.0;
                [branch]
                if (_KeyLightGain > 0.0)
                {
                    float4 shadowCoord = TransformWorldToShadowCoord(posWS);
                    Light key = GetMainLight(shadowCoord);
                    keyShadow = lerp(1.0, key.shadowAttenuation, _ShadowStrength);
                    float3 keyE = key.color * (key.distanceAttenuation * keyShadow * _KeyLightGain);

                    float keyNDotL = dot(N, key.direction);
                    diffuse += keyE * saturate((keyNDotL + wrap) * invWrap);

                    float3 keyH = normalize(key.direction + V);
                    specular += keyE * (pow(saturate(dot(N, keyH)), shininess) * saturate(keyNDotL));
                }

                float gain = _SwarmVPLParams.y;

                // Applied after the VPL loop and before the debug branches, so "Lit" shows the floor and the
                // two diagnostic views stay pure measurements of texture and of swarm light.
                float3 lit = diffuse + _SwarmAmbient;

                // Debug views separate "no texture" from "no light" (driven by SwarmMatrixDiagnostics).
                if (_SwarmSurfaceDebug > 0.5 && _SwarmSurfaceDebug < 1.5) return half4(albedo.rgb, 1.0h);
                if (_SwarmSurfaceDebug > 1.5) return half4(diffuse * gain, 1.0h);

                float3 color = (albedo.rgb * lit + specular * (specNorm * _SpecularStrength)) * gain;

                // The dissolve front is self-lit: in the void it is the only thing that shows where the geometry
                // is being unmade (or re-made), so it must not depend on the swarm being nearby.
                color += _DissolveEdgeColor.rgb * (dissolveEdge * dissolveEdge);

                // LightReceiver: emit ONLY the reflected light, additively. The camera feed already carries the
                // real surface, so albedo is reflectance here, never something to draw. Alpha 0 keeps it additive.
                // _ReceiverGain scales this separately from the light itself, so a bright cave does not wash the
                // real card: the feed's own exposure is already carrying most of that surface's brightness.
                // ShadowCatcher: an invisible surface over the real table that darkens the camera feed exactly
                // where the blockout occludes the key light. Multiplicative, because an additive receiver can
                // only ever brighten - it has no way to put a shadow on the desk.
                if (_RenderMode > 1.5)
                {
                    half occl = (half)saturate(1.0 - keyShadow);
                    return half4((1.0h - occl).xxx, 1.0h);
                }

                if (_RenderMode > 0.5) return half4(color * _ReceiverGain, 0.0h);

                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        // Shadow caster. Without this the blockout is lit by the key light but throws no shadow, and the
        // geometry keeps floating instead of sitting on the surface.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings o;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = positionCS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.positionWS = positionWS;
                return o;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                SampleAlbedo(input.uv);   // carries the alpha clip
                DissolveClip(input.positionWS);
                return 0;
            }
            ENDHLSL
        }

        // Depth prepass (Forward+ / depth texture consumers) with matching alpha clip.
        // ZWrite follows the render mode: a LightReceiver writes no depth anywhere, or it would occlude the feed.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull [_Cull]
            ZWrite [_ZWrite]
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return o;
            }

            half DepthFrag(Varyings input) : SV_Target
            {
                #if defined(_ALPHATEST_ON)
                    SampleAlbedo(input.uv);
                #endif
                DissolveClip(input.positionWS);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
