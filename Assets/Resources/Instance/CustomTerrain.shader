Shader "Custom/Instance2"
{
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
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "common.hlsl"
            #pragma vertex vert
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
                float4 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4 normal : TEXCOORD2;
                half4 tangent : TEXCOORD3;
                half4 bitangent : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                half3 vertexSH : TEXCOORD8;
                #if _DEBUG_COLOR
                half3 color : TEXCOORD6;
                #endif
                float4 clipPos : SV_POSITION;
            };

            float4 _MainTex_ST;
            Texture2DArray<float4> _MixedDiffuseTex;
            SamplerState samplerLinearClamp;
            SamplerState samplerLinearClamp2;
            Texture2DArray<float4> _MixedNormalTex;
            SamplerState samplerNormalLinearClamp;
            StructuredBuffer<RenderPatch> _BlockPatchList;
            Texture2D<float2> _HeightMapRT;
            Texture2DArray<float4> _IndirectMap;
            #if _DEBUG_COLOR
            float3 _debugColor[20];
            #endif
            float _MipInitial;
            float _MipLevelMax;
            float _MipDifference;
            float _SectorCountX;
            float _SectorCountY;
            int2 _CurrentSectorXY;
            int _IndirectSize;

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                RenderPatch patch = _BlockPatchList[instanceID];
                const NodeInfoStruct blockInfo = _NodeStructs[patch._wpos.z];
                #if _FIX_LOD_SEAM
                FixLODConnectSeam(v.positionOS, patch);
                #endif

                #if _DEBUG_PATCH
                float3 worldPos = v.positionOS * 0.98 * float3(blockInfo.VertexScale, 1, blockInfo.VertexScale) + float3(patch._wpos.x, 0, patch._wpos.y);
                #else
                float3 worldPos = v.positionOS * float3(blockInfo.VertexScale, 1, blockInfo.VertexScale) + float3(
                    patch._wpos.x, 0, patch._wpos.y);
                #endif
                
                worldPos.y = _HeightMapRT.Load(float3(worldPos.xz, 0)).y * _Max_Height;
                o.uv.xy = worldPos.xz / float2(_TerrainSize, _TerrainSize);
                o.uv.zw = o.uv * unity_LightmapST.xy + unity_LightmapST.zw;
                o.positionWS = worldPos;
                o.clipPos = TransformWorldToHClip(worldPos);
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(worldPos);
                float4 vertexTangent = float4(cross(float3(0, 0, 1), v.normalOS), 1.0);
                VertexNormalInputs normalInput = GetVertexNormalInputs(v.normalOS, vertexTangent);
                o.vertexSH = SampleSH(v.normalOS);
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

            int CalcLod(float2 uv)
            {
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);
                float rho = max(sqrt(dot(dx, dx)), sqrt(dot(dy, dy)));
                float lambda = log2(rho);
                return max(int(lambda + 0.5), 0);
            }

            StructuredBuffer<int2> _MipLevelList;

            float4 samplePageMipLevelTable(float2 uv, uint mipLevel)
            {
                int mipLevelSize = _MipLevelList[mipLevel].x;
                float4 indirectTalble = _IndirectMap[uint3(uv * mipLevelSize % _IndirectSize, mipLevel)];
                int phyId = (int)(indirectTalble.z);
                if (indirectTalble.w == 0)
                {
                    mipLevel = mipLevel + 1;
                    mipLevelSize = _MipLevelList[mipLevel].x;
                }
                float3 uvNew = float3(
                    (uv.x - indirectTalble.x) * mipLevelSize,
                    (uv.y - indirectTalble.y) * mipLevelSize,
                    phyId);
                int lod = CalcLod(uvNew.xy * (1 << 9));
                lod = clamp(lod, 0, 6);
                float4 result = SAMPLE_TEXTURE2D_ARRAY_LOD(_MixedDiffuseTex, samplerLinearClamp, uvNew.xy, uvNew.z, lod);
                // result =float4( GetMipColor(mipLevel),1);
                return result;
            }

            half4 frag2(v2f i) : SV_Target
            {
                InputData inputData = (InputData)0;
                half3 nrm = 0;
                real3 albedo = 0;
                // real3 albedo = SAMPLE_TEXTURE2D_ARRAY(_MainTex, samplerLinearClamp, i.uv.xy, i.rtIndex);
                // half3 nrm = UnpackNormal(
                //     SAMPLE_TEXTURE2D_ARRAY(_NormapTex, samplerNormalLinearClamp, i.uv.xy, i.rtIndex));
                uint absMax = max(abs((int)_CurrentSectorXY.x - (int)(i.uv.x * _SectorCountX)),
                                   abs((int)_CurrentSectorXY.y - (int)(i.uv.y * _SectorCountY)));
                int mipLevelSign = (int)floor((-2.0f * _MipInitial - _MipDifference +
                        sqrt(8.0f * _MipDifference * absMax +
                            (2.0f * _MipInitial - _MipDifference) *
                            (2.0f * _MipInitial - _MipDifference)))
                    / (2.0f * _MipDifference)) + 1;
                uint mipLevel = clamp(mipLevelSign, 0, _MipLevelMax);
                albedo = samplePageMipLevelTable(i.uv.xy, mipLevel);

                half3 normalTS = normalize(nrm.xyz);
                half3 viewDirWS = half3(i.normal.w, i.tangent.w, i.bitangent.w);
                inputData.tangentToWorld = half3x3(-i.tangent.xyz, i.bitangent.xyz, i.normal.xyz);
                // inputData.normalWS = TransformTangentToWorld(normalTS, inputData.tangentToWorld);
                // inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
                inputData.normalWS = float3(0, 1, 0);
                inputData.viewDirectionWS = viewDirWS;
                inputData.fogCoord = InitializeInputDataFog(float4(i.positionWS, 1.0), i.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.clipPos);
                // inputData.bakedGI = SAMPLE_GI(i.uv.wz, i.vertexSH, inputData.normalWS);
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.clipPos;

                half4 color = UniversalFragmentPBR(inputData, albedo, 0, 0, 0, 1, 0, 1);
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