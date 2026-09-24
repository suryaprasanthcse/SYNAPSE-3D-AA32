// =============================================================================
//  SwarmBoidIndirect.shader  (URP 17 / Unity 6)
//
//  Renders SwarmManager's Graphics.RenderMeshIndirect call.
//
//  Instance data: SwarmCompute.compute writes one 3x4 object-to-world matrix
//  per boid into _BoidRenderData, so the vertex shader does three dot products
//  instead of rebuilding an orthonormal basis for every vertex.
//
//  Instance ID: SwarmManager's indirect args always use startInstance = 0,
//  so SV_InstanceID is the 0-based instance index on every graphics API
//  (see GetIndirectInstanceID in UnityIndirect.cginc). No instancing
//  variants or procedural setup are required.
//
//  Procedural swim (Phase 5): ApplySwim bends each vertex along local X with a
//  travelling sine wave before the instance transform, in every pass. The
//  per-boid phase is integrated by SwarmCompute.compute (see ApplySwim).
//
//  Flat shading: the default 5-vertex pyramid shares vertices, so its vertex
//  normals are smoothed. ForwardLit rebuilds the true face normal from
//  screen-space derivatives and orients it with the interpolated normal.
// =============================================================================
Shader "MAAYAI/Swarm/BoidIndirectLit"
{
    Properties
    {
        [MainColor] _BaseColor ("Base Color", Color) = (0.17, 0.18, 0.2, 1.0)
        _Metallic ("Metallic", Range(0.0, 1.0)) = 0.9
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.6
        [HDR] _RimColor ("Speed Rim Color", Color) = (0.0, 0.6, 0.8, 1.0)
        _SpeedTintRange ("Speed For Full Rim", Float) = 6.0

        [Header(Procedural Swim)]
        _SwimSpeed ("Swim Speed (Hz at Max Speed)", Range(0.0, 10.0)) = 3.0
        _SwimFrequency ("Swim Frequency (Waves per Unit Length)", Range(0.0, 4.0)) = 1.0
        _SwimAmplitude ("Swim Amplitude (Units per Unit Behind Head)", Range(0.0, 1.0)) = 0.15
        _SwimHeadZ ("Swim Head Z (Object Space, Rigid Ahead)", Float) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Must match `struct BoidRenderData` in SwarmCompute.compute (64 bytes).
        struct BoidRenderData
        {
            float4 row0;
            float4 row1;
            float4 row2;
            float4 data;    // x = speed, y = swim phase (cycles [0, 1))
        };

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half  _Metallic;
            half  _Smoothness;
            half4 _RimColor;
            float _SpeedTintRange;
            float _SwimSpeed;       // consumed by SwarmCompute (phase integration), mirrored by SwarmManager
            float _SwimFrequency;
            float _SwimAmplitude;
            float _SwimHeadZ;
        CBUFFER_END

        static const float SWIM_TWO_PI = 6.28318530718;

        // Travelling-wave body swim in object space.
        //  - Phase is integrated on the GPU per boid (data.y), so it is continuous
        //    through speed changes and unique per boid; stroke rate scales with speed
        //    upstream. Sampling _Time.y * speed here would jump whenever speed changes.
        //  - sin(2pi * (phase + z * frequency)): as phase advances, each crest moves
        //    toward -Z, so the wave travels head -> tail. Amplitude grows
        //    linearly behind _SwimHeadZ, so the head stays steady and the tail wags most.
        float3 ApplySwim(float3 positionOS, float swimPhaseCycles)
        {
            float behindHead = max(_SwimHeadZ - positionOS.z, 0.0);
            float wave = sin(SWIM_TWO_PI * (swimPhaseCycles + positionOS.z * _SwimFrequency));
            positionOS.x += wave * _SwimAmplitude * behindHead;
            return positionOS;
        }

        StructuredBuffer<BoidRenderData> _BoidRenderData;   // bound per draw via MaterialPropertyBlock

        struct SwarmAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
        };

        void GetBoidWorldData(SwarmAttributes input, uint instanceID,
                              out float3 positionWS, out float3 normalWS, out float speed)
        {
            BoidRenderData boid = _BoidRenderData[instanceID];

            // Shared by every pass, so shadows and depth follow the animated body.
            float4 positionOS = float4(ApplySwim(input.positionOS.xyz, boid.data.y), 1.0);
            positionWS = float3(dot(boid.row0, positionOS),
                                dot(boid.row1, positionOS),
                                dot(boid.row2, positionOS));

            // Rotation with uniform scale: normals transform like directions.
            normalWS = normalize(float3(dot(boid.row0.xyz, input.normalOS),
                                        dot(boid.row1.xyz, input.normalOS),
                                        dot(boid.row2.xyz, input.normalOS)));
            speed = boid.data.x;
        }
        ENDHLSL

        // ---------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVert
            #pragma fragment ForwardFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half   speed01    : TEXCOORD2;
                half   fogFactor  : TEXCOORD3;
            };

            Varyings ForwardVert(SwarmAttributes input, uint instanceID : SV_InstanceID)
            {
                float3 positionWS, normalWS;
                float speed;
                GetBoidWorldData(input, instanceID, positionWS, normalWS, speed);

                Varyings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS   = normalWS;
                output.speed01    = saturate(speed / max(_SpeedTintRange, 1e-4));
                output.fogFactor  = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 ForwardFrag(Varyings input) : SV_Target
            {
                // True face normal, oriented to agree with the interpolated vertex normal
                // so it is independent of the API's screen-space Y direction.
                float3 faceNormal = normalize(cross(ddy(input.positionWS), ddx(input.positionWS)));
                half3 normalWS = faceNormal * (dot(faceNormal, input.normalWS) >= 0.0 ? 1.0 : -1.0);
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                half alpha = 1.0;
                BRDFData brdfData;
                InitializeBRDFData(_BaseColor.rgb, _Metallic, half3(0.0, 0.0, 0.0), _Smoothness, alpha, brdfData);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));

                half3 color = GlobalIllumination(brdfData, SampleSH(normalWS), 1.0, input.positionWS, normalWS, viewDirWS);
                color += LightingPhysicallyBased(brdfData, mainLight, normalWS, viewDirWS);

                half fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), 3.0);
                color += _RimColor.rgb * fresnel * input.speed01;

                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Set by URP's ShadowUtils.SetupShadowCasterConstantBuffer.
            float3 _LightDirection;
            float3 _LightPosition;

            float4 ShadowVert(SwarmAttributes input, uint instanceID : SV_InstanceID) : SV_POSITION
            {
                float3 positionWS, normalWS;
                float speed;
                GetBoidWorldData(input, instanceID, positionWS, normalWS, speed);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                return ApplyShadowClamping(positionCS);
            }

            half4 ShadowFrag(float4 positionCS : SV_POSITION) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull Back
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            float4 DepthVert(SwarmAttributes input, uint instanceID : SV_InstanceID) : SV_POSITION
            {
                float3 positionWS, normalWS;
                float speed;
                GetBoidWorldData(input, instanceID, positionWS, normalWS, speed);
                return TransformWorldToHClip(positionWS);
            }

            half DepthFrag(float4 positionCS : SV_POSITION) : SV_Target
            {
                return positionCS.z;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag

            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
            };

            DepthNormalsVaryings DepthNormalsVert(SwarmAttributes input, uint instanceID : SV_InstanceID)
            {
                float3 positionWS, normalWS;
                float speed;
                GetBoidWorldData(input, instanceID, positionWS, normalWS, speed);

                DepthNormalsVaryings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS   = normalWS;
                return output;
            }

            half4 DepthNormalsFrag(DepthNormalsVaryings input) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
