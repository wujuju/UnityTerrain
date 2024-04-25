using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public class VirtualTexture
{
    private const int MaxTaskCount = 8;
    public const int MipInitial = 2;
    public const int MipDifference = 2;
    public const int MipLevelMax = 7;
    public const int IndirectSize = 64;
    public RenderTexture albedoMap { get; private set; }
    public RenderTexture normalMap { get; private set; }
    public RenderTexture indirectMap { get; private set; }
    private bool _isGenMips;

    private ComputeShader _computeShader;

    // private MyLinkedList _curRTIdList;
    private LinkedList<LinkedTile> _curRTIdList;
    private Queue<Chunk> _taskList;
    private Vector2Int _RTSize;
    public static TerrainBlenderInfo[] terrainBlenderInfos;
    private RenderTexture tempNullRt;
    public ComputeBuffer mipLevelBuffer;
    private MipLevel[] pageMipLevelTable;
    private int2[] pageMipLevelNums;
    private int totalRTCount;
    public int sectorCountX { get; private set; }
    public int sectorCountZ { get; private set; }
    private ComputeBuffer indirectBuffers;
    private int currIndirectTaskNum = 0;
    private int4[] indirectTasks;
    private int terrainCountX = 2;
    private int terrainCountZ = 2;

    private int terrainWidthTotal;
    private int terrainLengthTotal;
    private int sectorCountXPerTerrain = 512;
    private int sectorCountZPerTerrain = 512;

    private int sectorWidth;
    private int sectorLength;
    private int sectorCount;
    private int cameraSector = -1;
    private int lastSector = -1;
    private bool clearPreviousTask;

    public VirtualTexture(int size)
    {
        _RTSize = new Vector2Int(size, size);
        indirectBuffers = new ComputeBuffer(MaxTaskCount * 2, sizeof(uint) * 4, ComputeBufferType.Append);
        indirectTasks = new int4[MaxTaskCount * 2];

        InitializeTerrain();
        InitializeMipLevle();
        InitializeTexture(totalRTCount);
        InitializeComputerShader();
        EnqueueMipLevelMax();
    }

    void InitializeTexture(int depth)
    {
        tempNullRt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf);
        tempNullRt.useMipMap = false;
        albedoMap = TerrainConfig.CreateRenderTextureArray(_RTSize.x, depth, true,
            RenderTextureFormat.ARGB32, TextureWrapMode.Clamp, FilterMode.Bilinear);
        normalMap = TerrainConfig.CreateRenderTextureArray(_RTSize.x, depth, true,
            RenderTextureFormat.ARGBHalf, TextureWrapMode.Clamp, FilterMode.Bilinear);
        indirectMap = TerrainConfig.CreateRenderTextureArray(IndirectSize, MipLevelMax + 1, false,
            RenderTextureFormat.ARGBFloat, TextureWrapMode.Repeat, FilterMode.Point);
        _taskList = new Queue<Chunk>(depth);
        // _curRTIdList = new MyLinkedList(depth);
        _curRTIdList = new LinkedList<LinkedTile>();
        for (int i = 0; i < depth; i++)
        {
            _curRTIdList.AddLast(new LinkedTile(i + 1));
        }
    }

    void InitializeTerrain()
    {
        var terrainSize = (int)terrainBlenderInfos[0].rect.width;
        terrainWidthTotal = terrainSize * terrainCountX;
        terrainLengthTotal = terrainSize * terrainCountZ;

        sectorCountX = terrainCountX * sectorCountXPerTerrain;
        sectorCountZ = terrainCountZ * sectorCountZPerTerrain;

        sectorWidth = terrainWidthTotal / sectorCountX;
        sectorLength = terrainLengthTotal / sectorCountZ;
        sectorCount = sectorCountX * sectorCountZ;
    }

    private Dictionary<int, int> mipLUTs = new Dictionary<int, int>();

    void InitializeMipLevle()
    {
        totalRTCount = 0;
        pageMipLevelTable = new MipLevel[MipLevelMax + 1];
        pageMipLevelNums = new int2[pageMipLevelTable.Length];
        for (int i = 0; i <= MipLevelMax; i++)
        {
            int diameter = CalculateDiameter(i);
            var rtBase = 1 << i;
            var rtCount = 10 + ((diameter + rtBase - 1) / rtBase + 1) *
                ((diameter + rtBase - 1) / rtBase + 1);

            totalRTCount += rtCount;
            pageMipLevelTable[i] = new MipLevel(sectorCountX >> i, sectorWidth << i, i);
            pageMipLevelNums[i] = new int2(pageMipLevelTable[i].count, pageMipLevelTable[i].size);
        }

        mipLevelBuffer = new ComputeBuffer(pageMipLevelTable.Length, sizeof(int) * 2);
        mipLevelBuffer.SetData(pageMipLevelNums);
    }

    void InitializeComputerShader()
    {
        _computeShader = Resources.Load<ComputeShader>("Instance/TerrainBlender");
        _computeShader.SetBuffer(1, TerrainConfig.SID_MipLevelList, mipLevelBuffer);
        _computeShader.SetTexture(1, TerrainConfig.SID_IndirectMap, indirectMap);
        _computeShader.SetBuffer(2, TerrainConfig.SID_MipLevelList, mipLevelBuffer);
        _computeShader.SetTexture(2, TerrainConfig.SID_IndirectMap, indirectMap);
        _computeShader.SetTexture(0, TerrainConfig.SID_MixedDiffuseTex, albedoMap);
        _computeShader.SetTexture(0, TerrainConfig.SID_MixedNormalTex, normalMap);
        _computeShader.SetVector(TerrainConfig.SID_TerrainTexSize, new Vector4(_RTSize.x, _RTSize.y));
    }

    void EnqueueMipLevelMax()
    {
        for (int i = 0; i < pageMipLevelTable[MipLevelMax].totalCount; i++)
        {
            DistributeTile(i, MipLevelMax);
        }
    }

    void DistributeTile(int i, int tempMipLevel)
    {
        var mipTable = pageMipLevelTable[tempMipLevel];
        Vector2Int xz = SectorXzToChunkXz(SectorToXZ(i), mipTable.count, mipTable.count);
        var chunk = mipTable.Get(xz.x, xz.y);
        if (chunk.isInQueue)
        {
            if (chunk.xz != xz)
                chunk.xz = xz;
        }
        else if (chunk.isCreate)
        {
            if (!chunk.isFix)
            {
                // _curRTIdList.Remove2Last(chunk.phyId);
                _curRTIdList.Remove(chunk.phyId);
                _curRTIdList.AddLast(chunk.phyId);
            }
        }
        else
        {
            // if (chunk.mipLevel < 3)
            //     return;
            _taskList.Enqueue(chunk);
            chunk.isInQueue = true;
            chunk.xz = xz;
        }
    }

    Vector2Int SectorXzToChunkXz(Vector2Int XZ, int chunkCountX, int chunkCountZ)
    {
        return new Vector2Int((int)(XZ.x * (chunkCountX / (float)sectorCountX)),
            (int)(XZ.y * (chunkCountZ / (float)sectorCountZ)));
    }

    int GetSector(float x, float z)
    {
        int currentSectorX = (int)(x / sectorWidth); //assuming x are all positive
        int currentSectorZ = (int)(z / sectorLength); //assuming z are all positive
        return currentSectorZ * sectorCountX + currentSectorX;
    }

    public void Update(CommandBuffer cmd, Vector3 targetPosition)
    {
        cameraSector = GetSector(targetPosition.x, targetPosition.z);
        Vector2Int cameraXZ = SectorToXZ(cameraSector);
        if (cameraSector != lastSector)
        {
            cmd.SetGlobalVector(TerrainConfig.SID_CurrentSectorXY, new Vector2(cameraXZ.x, cameraXZ.y));
            if (clearPreviousTask)
                _taskList.Clear();
            Profiler.BeginSample("Terrain_UpdateSector");
            //200
            //150  mipLUTs
            for (int i = 0; i < sectorCount; i++)
            {
                Vector2Int distance = GetVectorIntDistance(cameraXZ, i);
                int tempMipLevel = CalculateMipLevel(distance);
                DistributeTile(i, tempMipLevel);
            }

            lastSector = cameraSector;
            Profiler.EndSample();
        }

        if (_taskList.Count > 0)
        {
            var currTaskIndex = 0;
            for (int i = 0; i < _taskList.Count; i++)
            {
                if (currTaskIndex++ == MaxTaskCount)
                    break;
                RunTask(cmd, _taskList.Dequeue());
            }
        }
        else
        {
            if (!_isGenMips)
            {
                _isGenMips = true;
                albedoMap.GenerateMips();
            }
        }

        if (currIndirectTaskNum > 0)
        {
            indirectBuffers.SetData(indirectTasks);
            cmd.SetComputeBufferParam(_computeShader, 1, TerrainConfig.SID_IndirectList, indirectBuffers);
            cmd.DispatchCompute(_computeShader, 1, currIndirectTaskNum, 1, 1);
            currIndirectTaskNum = 0;
            cmd.DispatchCompute(_computeShader, 2, 8, 8, 7);
        }
    }

    void RunTask(CommandBuffer cmd, Chunk chunk)
    {
        _isGenMips = false;
        int mipLevel = chunk.mipLevel;
        var mipTable = pageMipLevelTable[mipLevel];
        Vector2Int chunkXZ = chunk.xz;

        var percentCountX = mipTable.count / terrainCountX;
        var percentCountZ = mipTable.count / terrainCountZ;
        int terrainIndex = (int)(chunkXZ.x / percentCountX) +
                           (int)(chunkXZ.y / percentCountZ) * terrainCountX;

        Vector4 tileST = new Vector4(terrainCountX / (float)mipTable.count,
            terrainCountZ / (float)mipTable.count,
            (chunkXZ.x % percentCountX) / (float)percentCountX,
            (chunkXZ.y % percentCountZ) / (float)percentCountZ);

        var phyTile = _curRTIdList.First;
        var removeChunk = phyTile.Value.chunk;
        if (removeChunk != null)
        {
            removeChunk.isInQueue = false;
            removeChunk.isCreate = false;
            indirectTasks[currIndirectTaskNum++] = new int4(removeChunk.xz.x, removeChunk.xz.y, mipLevel, 0);
        }

        phyTile.Value.chunk = chunk;
        SetTerrainSplatMap(cmd, terrainBlenderInfos[terrainIndex]);
        cmd.SetComputeVectorParam(_computeShader, TerrainConfig.SID_Node_ST, tileST);
        cmd.SetComputeIntParam(_computeShader, TerrainConfig.SID_ZIndex, phyTile.Value.Value);


        Vector2Int cameraXZ = SectorToXZ(cameraSector);
        // Debug.LogError(string.Format("mipLevel:{0}  chunkXZ:{1}:{2} xz:{3}:{4} c:{5}:{6} rtId:{7}",
        //     mipLevel, chunkXZ.x, chunkXZ.y,
        //     chunkXZ.x % IndirectSize, chunkXZ.y % IndirectSize,
        //     (cameraXZ.x >> mipLevel), (cameraXZ.y >> mipLevel),
        //     phyTile.Value.Value));
        indirectTasks[currIndirectTaskNum++] = new int4(chunkXZ.x, chunkXZ.y, mipLevel, phyTile.Value.Value);

        TerrainConfig.Dispatch(cmd, _computeShader, 0, _RTSize);
        chunk.phyId = phyTile;
        chunk.isCreate = true;
        chunk.isInQueue = false;
        if (mipLevel == MipLevelMax)
            _curRTIdList.RemoveFirst();
        else
        {
            _curRTIdList.RemoveFirst();
            _curRTIdList.AddLast(phyTile);
        }
    }


    int CalculateMipLevel(Vector2Int distance)
    {
        int absMax = Mathf.Max(distance.x, distance.y);
        int tempMipLevel;
        if (mipLUTs.TryGetValue(absMax, out tempMipLevel))
        {
            return tempMipLevel;
        }

        tempMipLevel = (int)Mathf.Floor((-2.0f * MipInitial - MipDifference +
                                         Mathf.Sqrt(8.0f * MipDifference * absMax +
                                                    (2.0f * MipInitial - MipDifference) *
                                                    (2.0f * MipInitial - MipDifference)))
                                        / (2.0f * MipDifference)) + 1;
        tempMipLevel = Mathf.Clamp(tempMipLevel, 0, MipLevelMax);
        mipLUTs[absMax] = tempMipLevel;
        return tempMipLevel;
    }

    Vector2Int SectorToXZ(int currentSector)
    {
        return new Vector2Int(currentSector % sectorCountX, currentSector / sectorCountX);
    }

    Vector2Int GetVectorIntDistance(Vector2Int fromXZ, int to)
    {
        Vector2Int toXZ = SectorToXZ(to);
        return new Vector2Int(Mathf.Abs(toXZ.x - fromXZ.x), Mathf.Abs(toXZ.y - fromXZ.y));
    }

    private void SetTerrainSplatMap(CommandBuffer cmd, TerrainBlenderInfo terrainBlenderInfo)
    {
        for (int i = 0; i < 3; i++)
        {
            cmd.SetComputeTextureParam(_computeShader, 0, "_Control" + i,
                i < terrainBlenderInfo.controls.Length ? terrainBlenderInfo.controls[i] : tempNullRt);
            if (i < terrainBlenderInfo.controls.Length)
                cmd.SetComputeVectorParam(_computeShader, "_ST_Control" + i, terrainBlenderInfo.controls_st[i]);
        }

        for (int i = 0; i < 12; i++)
        {
            cmd.SetComputeTextureParam(_computeShader, 0, "_Splat" + i,
                i < terrainBlenderInfo.splats.Length ? terrainBlenderInfo.splats[i] : tempNullRt);
            if (i < terrainBlenderInfo.splats.Length)
                cmd.SetComputeVectorParam(_computeShader, "_ST_Splat" + i, terrainBlenderInfo.splats_st[i]);
        }

        for (int i = 0; i < 12; i++)
        {
            cmd.SetComputeTextureParam(_computeShader, 0, "_Normal" + i,
                i < terrainBlenderInfo.normals.Length ? terrainBlenderInfo.normals[i] : tempNullRt);
        }
    }

    int CalculateDiameter(int mipLevel)
    {
        int r = (2 * MipInitial + mipLevel * MipDifference) * (mipLevel + 1) / 2;
        return r * 2 - 1;
    }

    class MipLevel
    {
        public int count;
        public int size;
        public int totalCount;
        private Chunk[][] indirects;

        public MipLevel(int count, int size, int mipLevel)
        {
            this.count = count;
            this.size = size;
            this.totalCount = count * count;
            indirects = new Chunk[IndirectSize][];

            for (int i = 0; i < IndirectSize; i++)
            {
                // 初始化每一行的列数，这里假设每一行有4列
                indirects[i] = new Chunk[IndirectSize];
                for (int j = 0; j < IndirectSize; j++)
                {
                    indirects[i][j] = new Chunk(mipLevel);
                }
            }
        }

        public Chunk Get(int x, int z)
        {
            return indirects[x % IndirectSize][z % IndirectSize];
        }
    }

    class Chunk
    {
        public bool isInQueue;
        public bool isCreate;

        public bool isFix;

        // public LinkedTile phyId;
        public LinkedListNode<LinkedTile> phyId;
        public int mipLevel;
        public Vector2Int xz;

        public Chunk(int mipLevel)
        {
            this.mipLevel = mipLevel;
            this.isFix = mipLevel == MipLevelMax;
        }
    }

    class MyLinkedList
    {
        private LinkedTile _first;
        private LinkedTile _last;
        private LinkedList<LinkedTile> list;

        public MyLinkedList(int count)
        {
            list = new LinkedList<LinkedTile>();
            for (int i = 0; i < count; i++)
            {
                var tile = new LinkedTile(i + 1);
                AddLast(tile);
                list.AddLast(tile);
            }
        }

        public LinkedTile First
        {
            get { return _first; }
        }

        public void Remove2Last(LinkedTile tile)
        {
            if (tile == _last)
                return;

            if (tile == _first)
            {
                RemoveFirst2Last();
                return;
            }

            if (tile.prev != null)
                tile.prev.next = tile.next;
            if (tile.next != null)
                tile.next.prev = tile.prev;
            AddLast(tile);
        }

        public void RemoveFirst()
        {
            var a = _first.next;
            _first = a;
            _first.prev = null;
        }

        public void RemoveFirst2Last()
        {
            var a = _first.next;
            var b = _first;
            _first = a;
            _first.prev = null;
            AddLast(b);
        }

        public void AddLast(LinkedTile tile)
        {
            if (_first == null)
            {
                _first = tile;
                return;
            }

            if (_last == null)
            {
                _first.next = tile;
                _last = tile;
                _last.prev = _first;
                return;
            }

            _last.next = tile;
            tile.prev = _last;
            _last = tile;
            _last.next = null;
        }
    }

    class LinkedTile
    {
        public int Value { get; private set; }
        public Chunk chunk;
        public LinkedTile prev;
        public LinkedTile next;

        public LinkedTile(int i)
        {
            Value = i;
        }

        public override string ToString()
        {
            // prev != null ? prev.Value.ToString() ? "0", Value.ToString(), prev != null ? prev.Value.ToString() ? 0
            return string.Format("{0}-{1}-{2}", prev != null ? prev.Value : 0, Value, next != null ? next.Value : 0);
        }
    }
}