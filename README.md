

# 简介

GPU Driven顾名思义，所有信息由GPU处理计算或渲染

很多文章也有介绍，这里记录下自己的一个思路

先看下效果图

![image-20240402190019333](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/image-202404021900193331zQ6cl.png)

用的Unity商城免费的资源



# Terrain转高度图

这里我用了4个原生`Unity`地形，大小`1024x1024`

拼成了一个`2x2`，大小为`2048x2048`的高度图

```C#
            int terrainNum = terrains.Length;
            int splitNumX = 2;
            int heightSize = (int)terrains[0].terrainData.size.x;
            if (!Mathf.IsPowerOfTwo(heightSize))
            {
                Debug.LogError("heightSize is not PowerOfTwo");
                return;
            }

            var realHeightSize = Mathf.NextPowerOfTwo(heightSize);
            int totalHeightSize = realHeightSize * terrainNum / splitNumX;
            float[] pixelData = new float[totalHeightSize * totalHeightSize];
            heightMap = new Texture2D(totalHeightSize, totalHeightSize, TextureFormat.RFloat, false);
            maxHeight = terrains[0].terrainData.heightmapScale.y;

            for (int k = 0; k < terrainNum; k++)
            {
                var terrain = terrains[k];

                var offsetX = (k % splitNumX) * heightSize;
                var offsetY = ((k / splitNumX)) * heightSize;
                for (int i = 0; i < heightSize; i++)
                {
                    for (int j = 0; j < heightSize; j++)
                    {
                        pixelData[offsetX + i + (j + offsetY) * totalHeightSize] =
                            terrain.terrainData.GetHeight(i, j) / maxHeight;
                    }
                }
            }

            heightMap.SetPixelData(pixelData, 0);
            heightMap.Apply();
```



# 创建Mesh

准备一个平铺的网格，这里我们用一个`8x8`，单位1米

这里用顶点索引，可以用最少的顶点数量去生成

```C#
private Mesh CreateQuardMesh(int size, float gridSize = 1)
    {
        _gValue._PerPacthGridNum = Convert.ToInt32(size / gridSize);
        int gridSizeX = Convert.ToInt32(size / gridSize);
        int gridSizeY = Convert.ToInt32(size / gridSize);

        Mesh mesh = new Mesh();
        Vector3[] vertices = new Vector3[(gridSizeX + 1) * (gridSizeY + 1)];
        for (int x = 0; x <= gridSizeX; x++)
        {
            for (int y = 0; y <= gridSizeY; y++)
            {
                float posx = (float)x * gridSize;
                float posz = (float)y * gridSize;
                vertices[y * (gridSizeX + 1) + x] = new Vector3(posx, 0, posz);
            }
        }

        int[] triangles = new int[gridSizeX * gridSizeY * 6];

        for (int i = 0; i < gridSizeX; i++)
        {
            for (int j = 0; j < gridSizeY; j++)
            {
                int triIndex = (j * gridSizeX + i) * 6;

                triangles[triIndex] = j * (gridSizeX + 1) + i;
                triangles[triIndex + 1] = (j + 1) * (gridSizeX + 1) + i;
                triangles[triIndex + 2] = (j + 1) * (gridSizeX + 1) + i + 1;

                triangles[triIndex + 3] = (j + 1) * (gridSizeX + 1) + i + 1;
                triangles[triIndex + 4] = j * (gridSizeX + 1) + i + 1;
                triangles[triIndex + 5] = j * (gridSizeX + 1) + i;
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        return mesh;
    }
```



# 创建地形

## 准备数据

首先我们会根据`_SplitNum=4`参数，切割整个地形

_`SplitNum`表示边长，`_SplitNum*_SplitNum`才是会切割地形的块数

```C#
public struct NodeInfoStruct
{
    public int CurLOD;
    public int NodeNum;
    public int Offset;
    public int NodeSize;
    public int PacthSize;
    public int VertexScale;
    public int HeightMipLevel;
}
```

每一块就是一个`Node`，然后Node也会切成固定数量的`Pacth`，一个`Pacth`可以表示成一个`Mesh`

比如这块地形是`2048x2048`,我们`_MAXLOD=3`，一个`Node`可以分成`8x8个`Pacth

`LOD3` `Node`数量`4x4`      `NodeSize=512x512`  `PacthSize=64x64`

`LOD2` `Node`数量`8x8`      `NodeSize=256x256`  `PacthSize=32x32`

`LOD1` `Node`数量`16x16`  `NodeSize=128x128`  `PacthSize=16x16`

`LOD0` `Node`数量`32x32`  `NodeSize=64x64`      `PacthSize=8x8`

本来一个`Mesh`是`8x8`大小，如果`LOD`大于`0`，我们就需要把`Mesh`放大

我们会用到GPU Instance技术， `DrawMeshInstancedIndirect`把所有的`Pacth`渲染出来



## TraverseQuadTree

这里我们用Compute Shader

根据`_MAXLOD`大小，判断当前的`Node`是否可以再细分

`EvaluateNode`函数很简单，就是到相机的`dst/nodeSize/(一个你自定义的系数)`

```c++
[numthreads(1, 1, 1)]
void TraverseQuadTree(uint3 id : SV_DispatchThreadID)
{
    uint2 nodeXY = _ConsumeList.Consume();
    const NodeInfoStruct nodeLOD = _NodeStructs[_CurLOD];
    const int nodeIndex = nodeLOD.Offset + nodeXY.y * nodeLOD.NodeNum + nodeXY.x;
    if (_CurLOD > 0 && EvaluateNode(nodeXY, nodeLOD.NodeSize))
    {
        nodeXY *= 2;
        _AppendList.Append(nodeXY);
        _AppendList.Append(nodeXY + uint2(0, 1));
        _AppendList.Append(nodeXY + uint2(1, 0));
        _AppendList.Append(nodeXY + uint2(1, 1));
        _NodeBrunchList[nodeIndex] = 1;
    }
    else
    {
        _AppendFinalNodeList.Append(uint3(nodeXY, _CurLOD));
        _NodeBrunchList[nodeIndex] = 2;
    }
}
```

![image-20240404111323523](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/image-20240404111323523h97pOA.png)

数字表示`LOD`



## CreateNodeSectorMap

记录整个`Node`节点的`LOD`信息，纹理大小是`LOD0`的节点数，也就是`32x32`

用于填补`Mesh`在不同`LOD`后产生的缝隙

```c++
[numthreads(8,8,1)]
void CreateNodeSectorMap(uint3 id : SV_DispatchThreadID)
{
    const uint2 sectorXY = id.xy;
    // [unroll]
    for (int lod = _MAXLOD; lod >= 0; lod--)
    {
    		//当前所属LOD的节点XY
        uint2 nodeXY = sectorXY >> lod;
        const NodeInfoStruct nodeInfo = _NodeStructs[lod];
        const int nodeIndex = nodeInfo.Offset + nodeXY.y * nodeInfo.NodeNum + nodeXY.x;
        //该节点没有被拆分
        if (_NodeBrunchList[nodeIndex] == 2)
        {
            _NodeSectorMap[sectorXY] = lod;
            return;
        }
    }
    _NodeSectorMap[sectorXY] = 0;
}
```



## CreatePatch

先看下完整代码

```c++
[numthreads(8, 8, 1)]
void CreatePatch(uint3 id : SV_DispatchThreadID, uint3 groupId:SV_GroupID, uint3 groupThreadId:SV_GroupThreadID)
{
    uint3 nodeLODInfo = _FinalNodeList[groupId.x];
    RenderPatch renderPatch=(RenderPatch)0;
    const uint2 nodeXY = nodeLODInfo.xy;
    const uint2 patchXY = groupThreadId.xy;
    renderPatch._wpos.z = nodeLODInfo.z;

    const NodeInfoStruct nodeInfo = _NodeStructs[renderPatch._wpos.z];
    const uint2 nodePos = nodeInfo.NodeSize * nodeXY;//Node世界坐标
    const uint2 patchPosInNode = nodeInfo.PacthSize * patchXY;//Pacth本地坐标（相对于Node）
    renderPatch._wpos.xy = nodePos + patchPosInNode;//Pacth世界坐标
    const float3 wpos = float3(renderPatch._wpos.x, 0, renderPatch._wpos.y);
    //nodeXY * _PerNodePacthNum + patchXY 获取高度图的像素位置
    float2 minMaxHeight = _HeightMapRT.mips[nodeInfo.HeightMipLevel][nodeXY * _PerNodePacthNum + patchXY].xy;
    const float3 boundMin = float3(0, minMaxHeight.x * _max_height, 0);
    const float3 boundMax = float3(nodeInfo.PacthSize, minMaxHeight.y * _max_height, nodeInfo.PacthSize);
    Bounds bounds;
    bounds.minPosition = wpos + boundMin;
    bounds.maxPosition = wpos + boundMax;

    #if _VIEW_FRUSTUM_CULLING
    if (IsFrustumCulling(bounds, _ViewFrustumPlane))
        return;
    #endif

    #if _DEBUG_MIP
    renderPatch._wpos.w = GetBoundsMip(bounds);
    #endif
    #if _HIZ_CULLING
        if (IsHizCulling(bounds))
            return;
    #endif
    SetLODTrans(renderPatch, nodeXY, patchXY, nodeInfo);
    _CulledPatchList.Append(renderPatch);
}
```

计算出`Pacth`的世界坐标，判断是否需要视锥剔除，Hiz剔除，

`struct RenderPatch`
`{`
	`uint4 _wpos;`
	`uint4 _lodTrans;`
`};`

`_wpos.xy`世界坐标

`_wpos.z`  `Patch`的`LOD`

_`wpos.w`  当前的`Mip`信息



### HeightMinMaxMap

`LOD(N)`一个像素代表`LOD(N-1)`4个像素，这个4个像素高度可能都不一样，我们需要算出最高和最低的，保证精度不丢失

高度图所生成的`depth`大小为`LOD0`时，`Pacth`的数量，也就是高度图的大小**<u>等于</u>**`Pacth`的大小

也就是一个`Pacth`等于高度图里面的一个像素



`Node`里面有一个`HeightMipLevel`，这个值就是当前`Node`的`Pacth`的大小相同的高度图的`depth`

`nodeXY * _PerNodePacthNum + patchXY` 获取高度图的像素位置

这样就可以获得每个Pacth的最大最小高度，方便进行计算

```
[numthreads(8, 8, 1)]
void BuildMinMaxHeightMapByMinMaxHeightMap(uint3 id : SV_DispatchThreadID)
{
    const uint2 uv = id.xy * 2;
    const float2 h1 = inputMinMaxHeightMap[uv];
    const float2 h2 = inputMinMaxHeightMap[uv + uint2(1, 0)];
    const float2 h3 = inputMinMaxHeightMap[uv + uint2(0, 1)];
    const float2 h4 = inputMinMaxHeightMap[uv + uint2(1, 1)];

    float hMin = min(min(h1.x, h2.x), min(h3.x, h4.x));
    float hMax = max(max(h1.y, h2.y), max(h3.y, h4.y));
    outputMinMaxHeightMap[id.xy] = float2(hMin, hMax);
}
```



### FrustumCulling

`Unity`默认不是有视锥剔除吗？没错是的，这里我们还需要做一次，因为`GPU Instace`这种模式，`Unity`不会帮你做剔除

使用`Unity`的`api`，获取视锥的6个面`GeometryUtility.CalculateFrustumPlanes`顺序是`左右上下远近`

得到一个平面的法线，和这条法线距离相机的距离，并且法线是朝视锥体内

根据`Pacth`的最大和最小的高度，组成一个包围盒

`struct Bounds`
`{`
    `float3 minPosition;`
    `float3 maxPosition;`
`};`

分别判断这个包围盒`minPosition`和`maxPosition`离平面最近的那个点，是否在视锥6个面中

判断方法就用平面公式
$$
Ax 
0
​
 +By 
0
​
 +Cz 
0
​
 +D=0在平面上
$$

$$
Ax 
0
​
 +By 
0
​
 +Cz 
0
​
 +D>0在平面内
$$

$$
Ax 
0
​
 +By 
0
​
 +Cz 
0
​
 +D<0在平面外
$$

```c++
bool IsFrustumCulling(Bounds bounds, float4 planes[6])
{
    const float3 minPosition = bounds.minPosition;
    const float3 maxPosition = bounds.maxPosition;
    [unroll]
    for (int i = 0; i < 6; i++)
    {
        float3 p = minPosition;
        float3 normal = planes[i].xyz;
      	//需要获取距离平面最近的坐标
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

bool IsOutSidePlane(float4 plane, float3 position)
{
    return dot(plane.xyz, position) + plane.w < 0;
}
```

来看下效果，4800+`Pacth`降低到了2800+

![image-20240404110714606](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/image-20240404110714606hj1Bq4.png)

![Kapture 2024-04-03 at 22.18.03](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/Kapture 2024-04-03 at 22.18.03Hmn67X.gif)

```c++
#if UNITY_EDITOR
                _terrainGPUDriven.Draw(cmd, Camera.main);
                renderCallBack(cmd);
#else
                _terrainGPUDriven.Draw(cmd, renderingData.cameraData.camera);
#endif
```

顺便发现`renderingData.cameraData.camera` 在`Game`和`Scene`，会自动调整到对应的相机坐标

`Camera.main` 只会获取到`Game`下的坐标



### HizCulling

先看代码

```c++
bool IsHizCulling(Bounds bounds)
{
    Bounds boundsUVD = CalBoundUVD(bounds);
    const float2 size = (boundsUVD.maxPosition.xy - boundsUVD.minPosition.xy) * _HizMapSize.xy;
    uint2 mipXY = ceil(log2(size));
  	//max(mipXY.x, mipXY.y)正方形
    const uint mip = clamp(max(mipXY.x, mipXY.y), 0, _HizMapSize.z);
    const uint2 mipHizMapSize = (uint2)_HizMapSize.xy >> mip;
    float d1 = SampleHizMap(boundsUVD.minPosition.xy, mip, mipHizMapSize);
    float d2 = SampleHizMap(boundsUVD.maxPosition.xy, mip, mipHizMapSize);
    float d3 = SampleHizMap(float2(boundsUVD.minPosition.x, boundsUVD.maxPosition.y), mip, mipHizMapSize);
    float d4 = SampleHizMap(float2(boundsUVD.maxPosition.x, boundsUVD.minPosition.y), mip, mipHizMapSize);

    #if _REVERSE_Z
    float depth = boundsUVD.maxPosition.z;
    return d1 > depth && d2 > depth && d3 > depth && d4 > depth;
    #else
    float depth = boundsUVD.minPosition.z;
    return d1 < depth && d2 < depth && d3 < depth && d4 < depth;
    #endif
}
```

`CalBoundUVD`就是求出`NDC`空间下包围盒坐标信息

怎么求NDC系坐标？

```c++
inline float3 CalPointUVD(float3 pos)
{
    float4 clipSpace = mul(_VPMatrix, float4(pos, 1.0));
    float3 uvd = clipSpace.xyz / clipSpace.w;
    uvd.xy = (uvd.xy + 1.0) * 0.5;
    return uvd;
}
```

乘以`VP`矩阵，换算到投影坐标，除以w就是`NDC`坐标了范围[-1,1]，再转换到[0,1]之间

乘以我们的`Mip0`的`_HizMapSize`

再`log2`以下就可以求出所需要采样的`mip`

为什要`log2`？比如算出物体在`Mip0`大小为`128`，我们需要取得它在那个`mip`下可以表示为`1`个像素

答案很显然，2的7次方就是`log2(128)`，我们最后采样一个`2x2`大小深度算一下就行

为啥要`2x2`？因为我们算出来的是包围盒，一个小正方形

看下效果，`2800+`降低到`2300+`

![image-20240404214425476](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/image-20240404214425476RvArwM.png)

再来个俯视图

![image-20240404214543053](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/image-202404042145430538KixwU.png)

生成`HizMap`，我们放在`RenderPassEvent.BeforeRenderingSkybox`

这样就会记录Terrain和Opaque的深度信息

渲染Terrain我们在`RenderPassEvent.AfterRenderingOpaques`

但是当我们帧率比较低，还是会遇到剔除错误问题![Kapture 2024-04-04 at 21.50.43](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/Kapture 2024-04-04 at 21.50.43QjOb70.gif)

我的想法是`Hiz`剔除完后，把那些剔除了的保存到一个列表，生成`HizMap`后，再用当前`HizMap`检查下是否需要剔除，再渲染一次没被剔除的就行！不过我偷懒了，没实现！！！

### FixSeam

缝隙的是因为不同`LOD`地形相连产生的

我们上次生成的`NodeSectorMap`就派上用场了

每个像素代表`LOD0`下的一个`Node`

然后判断下`Patch`邻边的时候不同`LOD`就行了

```c++
inline void SetLODTrans(inout RenderPatch patch, uint2 nodeXY, uint2 patchXY, in NodeInfoStruct blockInfo)
{
    const uint lod = blockInfo.CurLOD;
    const uint nodeScale = 1 << lod;
    const uint2 sectorMin = nodeXY * nodeScale;
    const uint2 sectorMax = sectorMin + nodeScale - 1;
    uint4 lodTrans = 0;
    if (patchXY.x == 0)
        lodTrans.x = GetSectorLOD(sectorMin + int2(-1, 0), lod);
    if (patchXY.y == 0)
        lodTrans.y = GetSectorLOD(sectorMin + int2(0, -1), lod);
    if (patchXY.x == _PerPacthGridNum - 1)
        lodTrans.z = GetSectorLOD(sectorMax + int2(1, 0), lod);
    if (patchXY.y == _PerPacthGridNum - 1)
        lodTrans.w = GetSectorLOD(sectorMax + int2(0, 1), lod);
    patch._lodTrans = lodTrans;
}
```



![Kapture 2024-04-05 at 18.39.56](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/Kapture 2024-04-05 at 18.39.56JT7tEy.gif)

# 光照

用的Unity默认的TerrainLit，稍微改了下，去掉了不必要的计算，也没有用融合

```c++
void SplatmapFragment2(
    Varyings IN
    , out half4 outColor : SV_Target0
)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
    half3 normalTS = 0;
    half4 splatControl = half4(1, 0, 0, 0);
    half weight;
    half4 mixedDiffuse;
    half4 defaultSmoothness;
    SplatmapMix(IN.uvMainAndLM, IN.uvSplat01, IN.uvSplat23, splatControl, weight, mixedDiffuse,
                defaultSmoothness, normalTS);
    half3 albedo = mixedDiffuse.rgb;
    InputData inputData;
    InitializeInputData(IN, normalTS, inputData);

    half4 color = UniversalFragmentPBR(inputData, albedo, 0, 0, .5, 1, 0, 1);

    SplatmapFinalColor(color, inputData.fogCoord);

    outColor = half4(color.rgb, 1.0h);
}
```

可以看下和`unity`默认`Terrain`的对比，`Batches=24`是我们的地形

`CPU`端肯定没提升，我们计算都在`GPU`的，提升`0.1-0.2`毫秒左右

为啥看起来不一样？我用的`m1`的`MBP`，无法使用`Geometry Shaders`，没办法重新算一次法线

`Demo`用的`Terrain`法线是切线空间法线

大家运行的时候把`// #pragma geometry geom2`打开就行，效果应该一致

![Kapture 2024-04-05 at 19.08.00](https://gitee.com/wujuju/image-cloud/raw/master/Picsee/MD/Kapture 2024-04-05 at 19.08.00vBNn6M.gif)

下面是我们自定义的`Terrain Shader`

```c++
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
```



参考:

https://zhuanlan.zhihu.com/p/648843014

https://zhuanlan.zhihu.com/p/388844386



[源码地址]: https://github.com/wujuju/UnityTerrain

