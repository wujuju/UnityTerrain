Shader "Custom/Hiz"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct v2f
    {
        float2 uv : TEXCOORD0;
        float4 vertex : SV_POSITION;
    };

    TEXTURE2D_X_FLOAT(_MainTex);
    SAMPLER(samplerLinearClamp);

    #pragma vertex vert
    v2f vert(appdata v)
    {
        v2f o;
        o.vertex = TransformObjectToHClip(v.vertex);
        o.uv = v.uv;
        return o;
    }
    ENDHLSL
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma fragment frag
            float2 _ScaleSize;

            float frag(v2f i) : SV_Target
            {
                float4 depth;
                depth.x = SAMPLE_TEXTURE2D_LOD(_MainTex, samplerLinearClamp, i.uv + _ScaleSize * float2(-0.25, -0.25), 0).r;
                depth.y = SAMPLE_TEXTURE2D_LOD(_MainTex, samplerLinearClamp, i.uv + _ScaleSize * float2(-0.25, 0.25), 0).r;
                depth.z = SAMPLE_TEXTURE2D_LOD(_MainTex, samplerLinearClamp, i.uv + _ScaleSize * float2(0.25, -0.25), 0).r;
                depth.w = SAMPLE_TEXTURE2D_LOD(_MainTex, samplerLinearClamp, i.uv + _ScaleSize * float2(0.25, 0.25), 0).r;

                #if defined (UNITY_REVERSED_Z)
                const float minimum = min(min(depth.x, depth.y), min(depth.z, depth.w));
                return minimum;
                #else
                const float maximum = max(max(depth.x, depth.y), max(depth.z, depth.w));
                return maximum;
                #endif
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma fragment frag

            float frag(v2f i) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_MainTex, samplerLinearClamp, i.uv);
            }
            ENDHLSL
        }
    }
}