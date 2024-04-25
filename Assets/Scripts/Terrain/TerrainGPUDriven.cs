using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class TerrainGPUDriven
{
    private Mesh _mesh;
    private Material _material;

    public GlobalValue _gValue;
    public bool isManualUpdate;
    private int subMeshIndex = 0;
    private uint[] _argArr = new uint[4];
    public static Vector3Int HIZ_MAP_SIZE = new Vector3Int(2048, 2048, 4 * 2 - 1);
    private VirtualTexture _virtualTexture;

    private ComputeBuffer mDispatchArgsBuffer =
        new ComputeBuffer(3, sizeof(float), ComputeBufferType.IndirectArguments);

    private ComputeBuffer mDispatchArgsBuffer2 =
        new ComputeBuffer(3, sizeof(float), ComputeBufferType.IndirectArguments);

    private ComputeShader _cs;
    private ComputeBuffer _argsBuffer;
    private ComputeBuffer _finalNodeList;
    private ComputeBuffer _nodeBrunchList;
    private ComputeBuffer _initialNodeList;
    private ComputeBuffer _nodeBufferA;
    private ComputeBuffer _nodeBufferB;
    private ComputeBuffer _culledPatchList;
    private ComputeBuffer _nodeStructsBuffer;
    private ComputeBuffer _currNodeStructBuffer;
    private uint[] _nodeLODDispatchArr = new uint[3];
    public NodeInfoStruct[] _nodeStructs { get; private set; }
    private RenderTexture _heightRT;
    private RenderTexture _nodeSectorMap;

    public void Init(Material mat, ComputeShader cs)
    {
        _cs = cs;
        _material = mat;
        ReleaseBuffer();
        TerrainConfig.InitComputeShader(cs);
        _gValue = new GlobalValue()
        {
            _SplitNum = 4,
            _PerNodePacthNum = 8,
            _PerPacthSize = 8,
            _HizMapSize = HIZ_MAP_SIZE,
        };
    }

    private float maxHeight;

    public void CreateHeightMap(Texture2D heightMap, float maxHeight)
    {
        this.maxHeight = maxHeight;
        int kBuildMinMaxHeightMap = _cs.FindKernel("BuildMinMaxHeightMapByHeightMap");
        int kBuildMinMaxHeightMapMipLevel = _cs.FindKernel("BuildMinMaxHeightMapByMinMaxHeightMap");

        Vector2Int hMapSize = new Vector2Int(heightMap.width, heightMap.height);

        TerrainConfig.HeightMipLevel =
            TerrainConfig.GetHeightMipLevelBySize(hMapSize.x, _gValue._SplitNum *
                                                              _gValue._PerNodePacthNum);
        if (TerrainConfig.HeightMipLevel == -1)
            throw new Exception("heightMipLevel==-1");

        RenderTextureDescriptor outputRTDesc =
            new RenderTextureDescriptor(hMapSize.x, hMapSize.y, RenderTextureFormat.RGFloat, 0,
                TerrainConfig.HeightMipLevel + 1);
        outputRTDesc.autoGenerateMips = false;
        outputRTDesc.useMipMap = true;
        outputRTDesc.enableRandomWrite = true;

        _heightRT = RenderTexture.GetTemporary(outputRTDesc);
        _heightRT.filterMode = FilterMode.Point;
        _heightRT.Create();

        _cs.SetTexture(kBuildMinMaxHeightMap, Shader.PropertyToID("heightMap"), heightMap);
        _cs.SetTexture(kBuildMinMaxHeightMap, Shader.PropertyToID("outputMinMaxHeightMap"), _heightRT, 0);
        TerrainConfig.Dispatch(_cs, kBuildMinMaxHeightMap,
            new Vector2Int(outputRTDesc.width, outputRTDesc.height));


        for (int i = 0; i < TerrainConfig.HeightMipLevel; i++)
        {
            Vector2Int inputSize = new Vector2Int(hMapSize.x >> i, hMapSize.y >> i);
            RenderTextureDescriptor inputRTDesc =
                new RenderTextureDescriptor(inputSize.x, inputSize.y, RenderTextureFormat.RGFloat, 0, 1);
            inputRTDesc.enableRandomWrite = true;
            inputRTDesc.autoGenerateMips = false;
            RenderTexture inputRT = RenderTexture.GetTemporary(inputRTDesc);
            inputRT.filterMode = FilterMode.Point;
            inputRT.Create();

            Graphics.CopyTexture(_heightRT, 0, i, inputRT, 0, 0);

            _cs.SetTexture(kBuildMinMaxHeightMapMipLevel, Shader.PropertyToID("inputMinMaxHeightMap"),
                inputRT);
            _cs.SetTexture(kBuildMinMaxHeightMapMipLevel, Shader.PropertyToID("outputMinMaxHeightMap"),
                _heightRT, i + 1);

            TerrainConfig.Dispatch(_cs, kBuildMinMaxHeightMapMipLevel, inputSize / 2);
            RenderTexture.ReleaseTemporary(inputRT);
        }
    }

    public void InitTerrain(int terrainSize)
    {
        _gValue._TerrainSize = terrainSize;
        _mesh = CreateQuardMesh(_gValue._PerPacthSize, 1f);
        _argArr[0] = (uint)_mesh.GetIndexCount(subMeshIndex);
        _argArr[2] = (uint)_mesh.GetIndexStart(subMeshIndex);
        _argArr[3] = (uint)_mesh.GetBaseVertex(subMeshIndex);

        int totalNodeNum = 0;
        int nodeNum = _gValue._SplitNum;
        _gValue._MAXLOD = 1;
        while (terrainSize / (nodeNum << _gValue._MAXLOD) / _gValue._PerNodePacthNum / _gValue._PerPacthSize > 1)
        {
            _gValue._MAXLOD++;
        }

        _nodeStructs = new NodeInfoStruct[_gValue._MAXLOD + 1];
        int heightMipLevel = TerrainConfig.HeightMipLevel;
        for (int i = _gValue._MAXLOD; i >= 0; i--)
        {
            int nodeSize = _gValue._TerrainSize / nodeNum;
            var nodeInfo = new NodeInfoStruct()
            {
                CurLOD = i,
                NodeNum = nodeNum,
                NodeSize = nodeSize,
                PacthSize = nodeSize / _gValue._PerNodePacthNum,
                VertexScale = nodeSize / _gValue._PerNodePacthNum / _gValue._PerPacthSize
            };

            nodeInfo.HeightMipLevel = heightMipLevel--;
            if (nodeInfo.VertexScale < 1)
                throw new Exception("VertexScale < 1");
            _nodeStructs[i] = nodeInfo;
            totalNodeNum += nodeNum * nodeNum;
            nodeNum *= 2;
        }

        var bufferSize = Marshal.SizeOf(typeof(RenderPatch));
        _culledPatchList = new ComputeBuffer(Pow2(_nodeStructs[0].NodeNum) * Pow2(_gValue._PerNodePacthNum) / 2,
            bufferSize, ComputeBufferType.Append);

        bufferSize = sizeof(float);
        _initialNodeList = new ComputeBuffer(Pow2(_gValue._SplitNum), bufferSize * 2, ComputeBufferType.Append);
        _finalNodeList = new ComputeBuffer(totalNodeNum, bufferSize * 3, ComputeBufferType.Append);
        _nodeBufferA = new ComputeBuffer(totalNodeNum, bufferSize * 2, ComputeBufferType.Append);
        _nodeBufferB = new ComputeBuffer(totalNodeNum, bufferSize * 2, ComputeBufferType.Append);
        _nodeBrunchList = new ComputeBuffer(totalNodeNum, sizeof(byte) * 2);
        for (int i = _gValue._MAXLOD; i >= 0; i--)
        {
            totalNodeNum -= Pow2(_nodeStructs[i].NodeNum);
            _nodeStructs[i].Offset = totalNodeNum;
        }


        _virtualTexture = new VirtualTexture(512);
        _nodeStructsBuffer = new ComputeBuffer(_nodeStructs.Length, Marshal.SizeOf(typeof(NodeInfoStruct)));
        _currNodeStructBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(NodeInfoStruct)));
        _nodeStructsBuffer.SetData(_nodeStructs);

        var nodeBufferDatas = new uint2[Pow2(_gValue._SplitNum)];
        for (uint i = 0, index = 0; i < _gValue._SplitNum; i++)
        {
            for (uint j = 0; j < _gValue._SplitNum; j++)
            {
                nodeBufferDatas[index++] = new uint2(i, j);
            }
        }

        _initialNodeList.SetData(nodeBufferDatas);
        var size = _nodeStructs[0].NodeNum;
        var descriptor = new RenderTextureDescriptor(size, size, RenderTextureFormat.R8, 0, 1);
        descriptor.autoGenerateMips = false;
        descriptor.enableRandomWrite = true;
        _nodeSectorMap = new RenderTexture(descriptor);
        _nodeSectorMap.filterMode = FilterMode.Point;
        _nodeSectorMap.Create();
        _argsBuffer = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
    }


    public static Matrix4x4 lastFrameVPMatrix;

    public void Draw(CommandBuffer cmd, Camera camera)
    {
#if UNITY_EDITOR
        if (_mesh == null)
            return;
#endif
        _gValue._CameraWorldPos = camera.transform.position;
        cmd.SetGlobalMatrix(TerrainConfig.SID_VPMatrix, lastFrameVPMatrix);
        cmd.SetGlobalTexture(TerrainConfig.SID_HeightMapRT, _heightRT);
        cmd.SetGlobalBuffer(TerrainConfig.SID_BlockLODList, _nodeStructsBuffer);
        cmd.SetGlobalVectorArray(TerrainConfig.SID_ViewFrustumPlane,
            TerrainConfig.CalculateCameraVector4(camera));

        if (!isManualUpdate)
        {
            Profiler.BeginSample("Terrain_InitNodeBuffer");
            InitNodeBuffer(cmd);
            Profiler.EndSample();
            Profiler.BeginSample("Terrain_CreateLODNodeList");
            CreateLODNodeList(cmd, TerrainConfig.K_TraverseQuadTree);
            Profiler.EndSample();
            Profiler.BeginSample("Terrain_CreateSectorMap");
            CreateSectorMap(cmd, TerrainConfig.K_CreateNodeSectorMap);
            Profiler.EndSample();
            Profiler.BeginSample("Terrain_CreatePatch");
            CreatePatch(cmd, TerrainConfig.K_CreatePatch);
            Profiler.EndSample();
            _virtualTexture.Update(cmd, _gValue._CameraWorldPos);
        }

        Profiler.BeginSample("Terrain_CreateTerrain");
        CreateTerrain(cmd);
        Profiler.EndSample();
    }

    private void InitNodeBuffer(CommandBuffer cmd)
    {
        _material.SetFloat(TerrainConfig.SID_MipInitial, VirtualTexture.MipInitial);
        _material.SetFloat(TerrainConfig.SID_MipDifference, VirtualTexture.MipDifference);
        _material.SetFloat(TerrainConfig.SID_MipLevelMax, VirtualTexture.MipLevelMax);
        cmd.SetGlobalInteger(TerrainConfig.SID_IndirectSize, VirtualTexture.IndirectSize);
        _material.SetTexture(TerrainConfig.SID_MixedDiffuseTex, _virtualTexture.albedoMap);
        _material.SetTexture(TerrainConfig.SID_MixedNormalTex, _virtualTexture.normalMap);
        _material.SetInt(TerrainConfig.SID_SectorCountX, _virtualTexture.sectorCountX);
        _material.SetInt(TerrainConfig.SID_SectorCountY, _virtualTexture.sectorCountZ);
        _material.SetTexture(TerrainConfig.SID_IndirectMap, _virtualTexture.indirectMap);
        _material.SetBuffer(TerrainConfig.SID_MipLevelList, _virtualTexture.mipLevelBuffer);
        cmd.SetGlobalFloat(TerrainConfig.SID_Max_Height, maxHeight);
        cmd.SetBufferCounterValue(_finalNodeList, 0);
        cmd.SetBufferCounterValue(_nodeBufferA, 0);
        cmd.SetBufferCounterValue(_nodeBufferB, 0);
        cmd.SetBufferCounterValue(_nodeBrunchList, 0);
        cmd.SetBufferCounterValue(_culledPatchList, 0);
        cmd.SetBufferCounterValue(_initialNodeList, (uint)Pow2(_gValue._SplitNum));

        _gValue.UpdateValue(cmd);
    }

    private void CreateLODNodeList(CommandBuffer cmd, int kIndex)
    {
        cmd.SetComputeBufferParam(_cs, kIndex, TerrainConfig.SID_AppendFinalNodeList,
            _finalNodeList);
        cmd.SetComputeBufferParam(_cs, kIndex, TerrainConfig.SID_NodeBrunchList,
            _nodeBrunchList);

        _nodeLODDispatchArr[0] = (uint)(Pow2(_gValue._SplitNum));
        _nodeLODDispatchArr[1] = 1;
        _nodeLODDispatchArr[2] = 1;
        cmd.SetBufferData(mDispatchArgsBuffer, _nodeLODDispatchArr);

        for (int i = _gValue._MAXLOD; i >= 0; i--)
        {
            cmd.SetGlobalInt(TerrainConfig.SID_CurLOD, i);
            if (i == _gValue._MAXLOD)
                cmd.SetComputeBufferParam(_cs, kIndex, TerrainConfig.SID_ConsumeList, _initialNodeList);
            else
                cmd.SetComputeBufferParam(_cs, kIndex, TerrainConfig.SID_ConsumeList, _nodeBufferB);
            cmd.SetComputeBufferParam(_cs, kIndex, TerrainConfig.SID_AppendList, _nodeBufferA);
            cmd.DispatchCompute(_cs, kIndex, mDispatchArgsBuffer, 0);

            cmd.CopyCounterValue(_nodeBufferA, mDispatchArgsBuffer, 0);
            (_nodeBufferA, _nodeBufferB) = (_nodeBufferB, _nodeBufferA);
        }
    }

    private void CreateTerrain(CommandBuffer command)
    {
        _argArr[1] = 0;
        _argsBuffer.SetData(_argArr);
        command.CopyCounterValue(_culledPatchList, _argsBuffer, 4);
        command.SetGlobalBuffer(TerrainConfig.SID_ArgsBuffer, _argsBuffer);
        command.SetGlobalBuffer(TerrainConfig.SID_BlockPatchList, _culledPatchList);

        command.DrawMeshInstancedIndirect(
            _mesh,
            subMeshIndex,
            _material, 0,
            _argsBuffer);
    }

    private void CreatePatch(CommandBuffer command, int kernelIndex)
    {
        command.CopyCounterValue(_finalNodeList, mDispatchArgsBuffer, 0);
        command.SetComputeBufferParam(_cs, kernelIndex, TerrainConfig.SID_FinalNodeList,
            _finalNodeList);
        command.SetComputeBufferParam(_cs, kernelIndex, TerrainConfig.SID_CulledPatchList,
            _culledPatchList);
        command.SetComputeTextureParam(_cs, kernelIndex, TerrainConfig.SID_NodeSectorMap, _nodeSectorMap);
        command.SetComputeTextureParam(_cs, kernelIndex, TerrainConfig.SID_HizMap,
            TerrainConfig.depthRT);
        command.DispatchCompute(_cs, kernelIndex, mDispatchArgsBuffer, 0);


#if UNITY_EDITOR
        _cs.GetKernelThreadGroupSizes(kernelIndex, out var x, out var y, out var z);
        if (x != _gValue._PerNodePacthNum)
        {
            throw new Exception($"compute shader numthreads != {_gValue._PerNodePacthNum}");
        }
#endif
    }

    private void CreateSectorMap(CommandBuffer command, int kernelIndex)
    {
        var size = _nodeStructs[0].NodeNum;
        command.SetComputeTextureParam(_cs, kernelIndex, TerrainConfig.SID_NodeSectorMap, _nodeSectorMap);
        command.SetComputeBufferParam(_cs, kernelIndex, TerrainConfig.SID_NodeBrunchList,
            _nodeBrunchList);
        TerrainConfig.Dispatch(command, _cs, kernelIndex, new Vector2Int(size, size));
    }


    private uint3[] _readBackPatchList;

    public uint3[] ReadBackHandler(CommandBuffer cmd)
    {
        if (_readBackPatchList == null)
            _readBackPatchList = new uint3[_finalNodeList.count];
        cmd.RequestAsyncReadback(_finalNodeList, (data) =>
            {
                var nativeArray = data.GetData<uint3>();
                nativeArray.CopyTo(_readBackPatchList);
            }
        );

        cmd.RequestAsyncReadback(mDispatchArgsBuffer, (data) =>
            {
                var nativeArray = data.GetData<uint>();
                nativeArray.CopyTo(_nodeLODDispatchArr);
                RendererFeatureTerrain.sGPUInfo[0] = _nodeLODDispatchArr[0];
            }
        );

        cmd.CopyCounterValue(_culledPatchList, mDispatchArgsBuffer2, 0);
        cmd.RequestAsyncReadback(mDispatchArgsBuffer2, (data) =>
            {
                var nativeArray = data.GetData<uint>();
                nativeArray.CopyTo(_nodeLODDispatchArr);
                RendererFeatureTerrain.sGPUInfo[1] = _nodeLODDispatchArr[0];
            }
        );
        return _readBackPatchList;
    }

    private Mesh CreateQuardMesh(int size, float gridSize = 1)
    {
        _gValue._PerPacthGridNum = Convert.ToInt32(size / gridSize);
        int gridSizeX = Convert.ToInt32(size / gridSize);
        int gridSizeY = Convert.ToInt32(size / gridSize);

        Mesh mesh = new Mesh();
        Vector3[] vertices = new Vector3[(gridSizeX + 1) * (gridSizeY + 1)];
        Vector2[] uv = new Vector2[vertices.Length]; // 添加 UV 数组

        for (int x = 0; x <= gridSizeX; x++)
        {
            for (int y = 0; y <= gridSizeY; y++)
            {
                float posx = (float)x * gridSize;
                float posz = (float)y * gridSize;
                int index = y * (gridSizeX + 1) + x;
                vertices[index] = new Vector3(posx, 0, posz);

                // 计算 UV 值
                uv[index] = new Vector2((float)x / gridSizeX, (float)y / gridSizeY);
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
        mesh.uv = uv; // 设置 UV
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        return mesh;
    }


    void ReleaseBuffer()
    {
        _argsBuffer?.Release();
        _finalNodeList?.Release();
        _nodeBrunchList?.Release();
        _initialNodeList?.Release();
        _nodeBufferA?.Release();
        _nodeBufferB?.Release();
        _nodeStructsBuffer?.Release();
        _currNodeStructBuffer?.Release();
        _culledPatchList?.Release();

        if (_heightRT != null)
        {
            RenderTexture.ReleaseTemporary(_heightRT);
            _heightRT = null;
        }
    }

    private int Pow2(int a)
    {
        return a * a;
    }
}