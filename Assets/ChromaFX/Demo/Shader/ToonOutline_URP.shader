Shader "Custom/ToonOutline_URP"
{
    Properties
    {
        // 贴图
        _BaseMap        ("Base Color",    2D)            = "white" {}
        _BumpMap        ("Normal Map",    2D)            = "bump"  {}
        _BumpScale      ("Normal Scale",  Float)         = 1.0

        // 三渲二色阶
        _LitColor       ("Lit Color",     Color)         = (1, 1, 1, 1)
        _ShadowColor    ("Shadow Color",  Color)         = (0.3, 0.3, 0.4, 1)
        _ShadowThreshold("Shadow Threshold", Range(0,1)) = 0.5
        _ShadowSmoothness("Shadow Smoothness", Range(0,0.5)) = 0.05
        _ColorBlend     ("Color / Texture Blend (0=Tex, 1=Flat Color)", Range(0,1)) = 0.5

        // 描边
        _OutlineColor   ("Outline Color", Color)         = (0,0,0,1)
        _OutlineWidth   ("Outline Width", Range(0, 0.1)) = 0.02
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
        }

        // ════════════════════════════════════════════════
        // Pass 1 — 描边（背面法线外扩）
        // ════════════════════════════════════════════════
        Pass
        {
            Name "Outline"
            Cull Front

            HLSLPROGRAM
            #pragma vertex   OutlineVert
            #pragma fragment OutlineFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float  _OutlineWidth;
                // 占位（保持 CBuffer 一致）
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float  _BumpScale;
                float4 _LitColor;
                float4 _ShadowColor;
                float  _ShadowThreshold;
                float  _ShadowSmoothness;
                float  _ColorBlend;
            CBUFFER_END

            struct Attributes { float4 posOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings   { float4 posCS : SV_POSITION; };

            Varyings OutlineVert(Attributes IN)
            {
                Varyings OUT;
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
                float3 posWS    = TransformObjectToWorld(IN.posOS.xyz);
                posWS          += normalize(normalWS) * _OutlineWidth;
                OUT.posCS       = TransformWorldToHClip(posWS);
                return OUT;
            }

            half4 OutlineFrag(Varyings IN) : SV_Target
            {
                return _OutlineColor;
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════
        // Pass 2 — 主体三渲二光照
        // ════════════════════════════════════════════════
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex   ToonVert
            #pragma fragment ToonFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float  _BumpScale;
                float4 _LitColor;
                float4 _ShadowColor;
                float  _ShadowThreshold;
                float  _ShadowSmoothness;
                float  _ColorBlend;
                float4 _OutlineColor;
                float  _OutlineWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS     : POSITION;
                float3 normalOS  : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv        : TEXCOORD0;
            };

            struct Varyings
            {
                float4 posCS     : SV_POSITION;
                float2 uv        : TEXCOORD0;
                float3 posWS     : TEXCOORD1;
                float3 normalWS  : TEXCOORD2;
                float3 tangentWS : TEXCOORD3;
                float3 bitangWS  : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
                float  fogFactor : TEXCOORD6;
            };

            Varyings ToonVert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.posOS.xyz);
                VertexNormalInputs   nrmInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.posCS     = posInputs.positionCS;
                OUT.posWS     = posInputs.positionWS;
                OUT.uv        = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalWS  = nrmInputs.normalWS;
                OUT.tangentWS = nrmInputs.tangentWS;
                OUT.bitangWS  = nrmInputs.bitangentWS;
                OUT.shadowCoord = GetShadowCoord(posInputs);
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 ToonFrag(Varyings IN) : SV_Target
            {
                // ── 贴图采样 ──────────────────────────────────
                half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 normalTS  = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);

                // ── 法线（切线空间→世界空间）──────────────────
                float3x3 TBN = float3x3(
                    normalize(IN.tangentWS),
                    normalize(IN.bitangWS),
                    normalize(IN.normalWS));
                float3 normalWS = normalize(mul(normalTS, TBN));

                // ── 挑出对当前像素贡献最大的一盏光 ─────────────
                // 主光（平行光）和附加光（点光/聚光，例如火堆光源）逐个比较，
                // 谁的 半兰伯特×衰减 更大，谁就负责明暗分界和高光方向
                Light   mainLight    = GetMainLight(IN.shadowCoord);
                Light   dominant     = mainLight;
                float   dominantAtt  = saturate(dot(normalWS, normalize(mainLight.direction)) * 0.5 + 0.5)
                                        * mainLight.shadowAttenuation * mainLight.distanceAttenuation;

                #ifdef _ADDITIONAL_LIGHTS
                    uint addCount = GetAdditionalLightsCount();
                    for (uint i = 0u; i < addCount; i++)
                    {
                        Light addLight = GetAdditionalLight(i, IN.posWS);
                        float addAtt   = saturate(dot(normalWS, normalize(addLight.direction)) * 0.5 + 0.5)
                                        * addLight.distanceAttenuation * addLight.shadowAttenuation;
                        if (addAtt > dominantAtt)
                        {
                            dominantAtt = addAtt;
                            dominant    = addLight;
                        }
                    }
                #endif

                // ── 三渲二漫反射（Step + Smoothstep）─────────
                float diffuse  = smoothstep(
                    _ShadowThreshold - _ShadowSmoothness,
                    _ShadowThreshold + _ShadowSmoothness,
                    dominantAtt);

                // _ColorBlend: 0 = 只看贴图原色，1 = 颜色完全盖过贴图
                half3 litBase   = lerp(baseColor.rgb, _LitColor.rgb,    _ColorBlend);
                half3 shadBase  = lerp(baseColor.rgb, _ShadowColor.rgb, _ColorBlend);

                half3 litColor  = litBase * dominant.color;
                half3 shadColor = shadBase;
                half3 col       = lerp(shadColor, litColor, diffuse);

                // ── Fog ───────────────────────────────────────
                col = MixFog(col, IN.fogFactor);

                return half4(col, baseColor.a);
            }
            ENDHLSL
        }

        // ════════════════════════════════════════════════
        // Pass 3 — 阴影投射
        // ════════════════════════════════════════════════
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float  _BumpScale;
                float4 _LitColor;
                float4 _ShadowColor;
                float  _ShadowThreshold;
                float  _ShadowSmoothness;
                float  _ColorBlend;
                float4 _OutlineColor;
                float  _OutlineWidth;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 posOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings   { float4 posCS : SV_POSITION; };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.posOS.xyz);
                float3 nrmWS = TransformObjectToWorldNormal(IN.normalOS);

                // 手动实现shadow bias，避免LerpWhiteTo版本兼容问题
                float  invNdotL = 1.0 - saturate(dot(_LightDirection, nrmWS));
                float  scale    = invNdotL * 0.01;
                posWS           = _LightDirection * (-0.005) + posWS;
                posWS           = nrmWS * scale + posWS;

                OUT.posCS = TransformWorldToHClip(posWS);

                // depth bias
                #if UNITY_REVERSED_Z
                    OUT.posCS.z = min(OUT.posCS.z, OUT.posCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.posCS.z = max(OUT.posCS.z, OUT.posCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // ════════════════════════════════════════════════
        // Pass 4 — DepthOnly（URP需要）
        // ════════════════════════════════════════════════
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float  _BumpScale;
                float4 _LitColor;
                float4 _ShadowColor;
                float  _ShadowThreshold;
                float  _ShadowSmoothness;
                float  _ColorBlend;
                float4 _OutlineColor;
                float  _OutlineWidth;
            CBUFFER_END

            struct Attributes { float4 posOS : POSITION; };
            struct Varyings   { float4 posCS : SV_POSITION; };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT;
                OUT.posCS = TransformObjectToHClip(IN.posOS.xyz);
                return OUT;
            }
            half4 DepthFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
