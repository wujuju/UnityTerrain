#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "TerrainInfoStruct.cs.hlsl"
#pragma multi_compile_local __ _DEBUG_MIP
Texture2D<float> _HizMap;
uniform float4x4 _VPMatrix;
StructuredBuffer<NodeInfoStruct> _NodeStructs;
float _Max_Height;

struct Bounds
{
    float3 minPosition;
    float3 maxPosition;
};


bool IsOutSidePlane(float4 plane, float3 position)
{
    return dot(plane.xyz, position) + plane.w < 0;
}

bool IsFrustumCulling(Bounds bounds, float4 planes[6])
{
    const float3 minPosition = bounds.minPosition;
    const float3 maxPosition = bounds.maxPosition;
    [unroll]
    for (int i = 0; i < 6; i++)
    {
        float3 p = minPosition;
        float3 normal = planes[i].xyz;
        if (normal.x > 0)
            p.x = maxPosition.x;
        if (normal.y > 0)
            p.y = maxPosition.y;
        if (normal.z > 0)
            p.z = maxPosition.z;
        if (IsOutSidePlane(planes[i], p))
        {
            return true;
        }
    }
    return false;
}

inline float3 CalPointUVD(float3 pos)
{
    float4 clipSpace = mul(_VPMatrix, float4(pos, 1.0));
    float3 uvd = clipSpace.xyz / clipSpace.w;
    uvd.xy = (uvd.xy + 1.0) * 0.5;
    return uvd;
}

Bounds CalBoundUVD(Bounds boundsUVD)
{
    float3 minPosition = boundsUVD.minPosition;
    float3 maxPosition = boundsUVD.maxPosition;
    Bounds bounds;
    const float3 uvd0 = CalPointUVD(minPosition);
    const float3 uvd1 = CalPointUVD(maxPosition);
    const float3 uvd2 = CalPointUVD(float3(minPosition.x, minPosition.y, maxPosition.z));
    const float3 uvd3 = CalPointUVD(float3(minPosition.x, maxPosition.y, minPosition.z));
    const float3 uvd4 = CalPointUVD(float3(minPosition.x, maxPosition.y, maxPosition.z));
    const float3 uvd5 = CalPointUVD(float3(maxPosition.x, minPosition.y, minPosition.z));
    const float3 uvd6 = CalPointUVD(float3(maxPosition.x, maxPosition.y, minPosition.z));
    const float3 uvd7 = CalPointUVD(float3(maxPosition.x, minPosition.y, maxPosition.z));
    bounds.minPosition = min(min(min(uvd0, uvd1), min(uvd2, uvd3)), min(min(uvd4, uvd5), min(uvd6, uvd7)));
    bounds.maxPosition = max(max(max(uvd0, uvd1), max(uvd2, uvd3)), max(max(uvd4, uvd5), max(uvd6, uvd7)));
    return bounds;
}

inline float SampleHizMap(float2 uv, uint mip, uint2 mipHizMapSize)
{
    uint2 coordinates = uint2(floor(uv * mipHizMapSize));
    coordinates = clamp(coordinates, 0, mipHizMapSize - 1);
    return _HizMap.mips[mip][coordinates];
}

void FixLODConnectSeam(inout float4 vertex, RenderPatch patch)
{
    uint4 lodTrans = patch._lodTrans;
    uint2 vertexIndex = vertex.xz;

    uint lodDelta = lodTrans.x;
    if (lodDelta > 0 && vertexIndex.x == 0)
    {
        uint gridStripCount = 1 << lodDelta;
        uint modIndex = vertexIndex.y % gridStripCount;
        if (modIndex > 0)
        {
            vertex.z -= modIndex;
            // uv.y -= * modIndex;
            return;
        }
    }

    lodDelta = lodTrans.y;
    if (lodDelta > 0 && vertexIndex.y == 0)
    {
        uint gridStripCount = 1 << lodDelta;
        uint modIndex = vertexIndex.x % gridStripCount;
        if (modIndex > 0)
        {
            vertex.x -= modIndex;
            // uv.x -= uvGridStrip * modIndex;
            return;
        }
    }

    lodDelta = lodTrans.z;
    if (lodDelta > 0 && vertexIndex.x == _PerPacthGridNum)
    {
        uint gridStripCount = 1 << lodDelta;
        uint modIndex = vertexIndex.y % gridStripCount;
        if (modIndex > 0)
        {
            vertex.z += (gridStripCount - modIndex);
            // uv.y += uvGridStrip * (gridStripCount - modIndex);
            return;
        }
    }

    lodDelta = lodTrans.w;
    if (lodDelta > 0 && vertexIndex.y == _PerPacthGridNum)
    {
        uint gridStripCount = 1 << lodDelta;
        uint modIndex = vertexIndex.x % gridStripCount;
        if (modIndex > 0)
        {
            vertex.x += (gridStripCount - modIndex);
            // uv.x += uvGridStrip * (gridStripCount - modIndex);
            return;
        }
    }
}


half3 GetMipColor(uint mip)
{
    // if (mip == 0)
    //     return float3(0.5, 0, 0);
    // if (mip == 1)
    //     return float3(1, 0, 0);
    // if (mip == 2)
    //     return float3(0, 0.5, 0);
    // if (mip == 3)
    //     return float3(0, 1, 0);
    // if (mip == 4)
    //     return float3(0, 0, 0.5);
    // if (mip == 5)
    //     return float3(0, 0, 1);
    if (mip <= 6)
        return 0.5;
    if (mip == 7)
        return half3(1, 0, 0);
    if (mip == 8)
        return half3(0, 1, 0);
    if (mip == 9)
        return half3(0, 0, 1);
    return 0;
}
