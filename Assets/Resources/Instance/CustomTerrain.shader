Shader "Custom/Instance2"
{
    Properties
    {
        _MainTex ("Albedo Map", 2D) = "white" {}
    }
    SubShader
    {
        Tags
        {
            "RenderType"="Opaque" "LightMode" = "UniversalForward"
        }
        LOD 100
        ZWrite On
        ZTest LEqual
        Pass
        {
            //            Cull Off
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "common.hlsl"
            #pragma vertex vert
            // #pragma fragment frag
            #pragma fragment frag2
            // #pragma geometry geom2
            #pragma shader_feature _DEBUG_LOD
            #pragma shader_feature _FIX_LOD_SEAM
            #pragma shader_feature _DEBUG_PATCH
            #pragma multi_compile_fog
            #define _DEBUG_COLOR _DEBUG_LOD || _DEBUG_MIP

            struct appdata
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                float4 tangent : TANGENT;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4 normal : TEXCOORD2;
                half4 tangent : TEXCOORD3;
                half4 bitangent : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                #if _DEBUG_COLOR
                half3 color : TEXCOORD6;
                #endif
                float4 clipPos : SV_POSITION;
            };

            float4 _MainTex_ST;
            float _max_height;
            Texture2D<float4> _MainTex;
            SamplerState samplerLinearClamp;

            Texture2D<float4> _NormapTex;
            SamplerState samplerNormalLinearClamp;
            StructuredBuffer<RenderPatch> _BlockPatchList;
            Texture2D<float2> _HeightMapRT;
            Texture2D<int> _TextureMap;
            float _TextureUVScale;
            #if _DEBUG_COLOR
            float3 _debugColor[20];
            #endif

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                RenderPatch patch = _BlockPatchList[instanceID];
                const NodeInfoStruct blockInfo = _NodeStructs[patch._wpos.z];
                #if _FIX_LOD_SEAM
                FixLODConnectSeam(v.positionOS, patch);
                #endif

                #if _DEBUG_PATCH
                float3 worldPos = v.positionOS * 0.95 * float3(blockInfo.VertexScale, 1, blockInfo.VertexScale) + float3(patch._wpos.x, 0, patch._wpos.y);
                #else
                float3 worldPos = v.positionOS * float3(blockInfo.VertexScale, 1, blockInfo.VertexScale) + float3(
                    patch._wpos.x, 0, patch._wpos.y);
                #endif
                worldPos.y = _HeightMapRT.Load(float3(worldPos.xz, 0)).y * _max_height;
                o.uv = worldPos.xz % 1024 / 1024.0;
                o.positionWS = worldPos;
                o.clipPos = TransformWorldToHClip(worldPos);
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(worldPos);
                float4 vertexTangent = float4(cross(float3(0, 0, 1), v.normalOS), 1.0);
                VertexNormalInputs normalInput = GetVertexNormalInputs(v.normalOS, vertexTangent);
                o.normal = half4(normalInput.normalWS, viewDirWS.x);
                o.tangent = half4(normalInput.tangentWS, viewDirWS.y);
                o.bitangent = half4(normalInput.bitangentWS, viewDirWS.z);
                o.fogFactor = ComputeFogFactor(o.clipPos.z);
                #if _DEBUG_LOD
                o.color = _debugColor[patch._wpos.z];
                #endif
                #if _DEBUG_MIP
                o.color = GetMipColor(patch._wpos.w);
                #endif
                return o;
            }

            half4 frag2(v2f i) : SV_Target
            {
                InputData inputData = (InputData)0;
                real3 albedo = SAMPLE_TEXTURE2D(_MainTex, samplerLinearClamp, i.uv);
                half3 nrm = UnpackNormal(SAMPLE_TEXTURE2D(_NormapTex, samplerNormalLinearClamp, i.uv));
                half3 normalTS = normalize(nrm.xyz);
                half3 viewDirWS = half3(i.normal.w, i.tangent.w, i.bitangent.w);
                inputData.tangentToWorld = half3x3(-i.tangent.xyz, i.bitangent.xyz, i.normal.xyz);
                inputData.normalWS = TransformTangentToWorld(normalTS, inputData.tangentToWorld);
                inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
                inputData.viewDirectionWS = viewDirWS;
                inputData.fogCoord = InitializeInputDataFog(float4(i.positionWS, 1.0), i.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.clipPos);

                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.clipPos;

                half4 color = UniversalFragmentPBR(inputData, albedo, 0, 0, .5, 1, 0, 1);
                color.rgb *= color.a;
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                #if  _DEBUG_COLOR
                color.rgb = i.color;
                #endif
                return half4(color.rgb, 1.0);
            }

            [maxvertexcount(3)]
            void geom2(triangle v2f p[3], inout TriangleStream<v2f> triangleStream)
            {
                for (int i = 0; i < 3; i++)
                {
                    v2f A = p[i];
                    float3 AB = p[(i + 1) % 3].positionWS - A.positionWS;
                    float3 AC = p[(i + 2) % 3].positionWS - A.positionWS;

                    A.normal = half4(normalize(cross(AB, AC)), 1.0);
                    triangleStream.Append(A);
                }
            }

            // Calculate MipMap level based on screen space ddx and ddy derivatives
            float CalculateMipLevel(float2 uv2, float textureSize)
            {
                float2 uv = uv2 * textureSize;
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);
                float rho = max(sqrt(dot(dx, dx)), sqrt(dot(dy, dy)));
                float lambda = log2(rho);
                int d = max(int(lambda + 0.5), 0);

                // Ensure mipLevel is within the valid range (0.0 to maxMipLevel)
                // d = clamp(d, 0.0, 8);

                return d;
            }
            ENDHLSL
        }
    }
}