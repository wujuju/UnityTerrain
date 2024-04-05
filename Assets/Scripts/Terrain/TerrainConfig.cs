using System;
using System.Reflection;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

public class TerrainConfig
{
    public static int K_TraverseQuadTree;
    public static int K_CreatePatch;
    public static int K_CreateNodeSectorMap;

    public static int SID_VPMatrix;
    public static int SID_AppendList;
    public static int SID_ConsumeList;
    public static int SID_AppendFinalNodeList;
    public static int SID_FinalNodeList;
    public static int SID_NodeBrunchList;
    public static int SID_CulledPatchList;
    public static int SID_ArgsBuffer;
    public static int SID_BlockPatchList;
    public static int SID_BlockLODList;
    public static int SID_HeightMapRT;
    public static int SID_NodeSectorMap;
    public static int SID_HizMap;
    public static int SID_ViewFrustumPlane;
    public static int SID_CurLOD;
    public static RenderTexture depthRT;

    public static int HeightMipLevel;


    public static void InitComputeShader(ComputeShader cs)
    {
        K_TraverseQuadTree = cs.FindKernel("TraverseQuadTree");
        K_CreatePatch = cs.FindKernel("CreatePatch");
        K_CreateNodeSectorMap = cs.FindKernel("CreateNodeSectorMap");

        SID_VPMatrix = Shader.PropertyToID("_VPMatrix");
        SID_AppendList = Shader.PropertyToID("_AppendList");
        SID_ConsumeList = Shader.PropertyToID("_ConsumeList");
        SID_AppendFinalNodeList = Shader.PropertyToID("_AppendFinalNodeList");
        SID_FinalNodeList = Shader.PropertyToID("_FinalNodeList");
        SID_CulledPatchList = Shader.PropertyToID("_CulledPatchList");
        SID_NodeBrunchList = Shader.PropertyToID("_NodeBrunchList");
        SID_ArgsBuffer = Shader.PropertyToID("_ArgsBuffer");
        SID_BlockPatchList = Shader.PropertyToID("_BlockPatchList");
        SID_BlockLODList = Shader.PropertyToID("_NodeStructs");
        SID_HeightMapRT = Shader.PropertyToID("_HeightMapRT");
        SID_NodeSectorMap = Shader.PropertyToID("_NodeSectorMap");
        SID_HizMap = Shader.PropertyToID("_HizMap");
        SID_CurLOD = Shader.PropertyToID("_CurLOD");
        SID_ViewFrustumPlane = Shader.PropertyToID("_ViewFrustumPlane");
    }

    public static int GetHeightMipLevelBySize(int size, int pixSize)
    {
        for (int i = 1; i < 20; i++)
        {
            if (size >> i == pixSize)
                return i;
        }

        return -1;
    }

    public static RenderTexture GetDepthTemporary(RenderTextureDescriptor rtDesc)
    {
        if (depthRT)
            RenderTexture.ReleaseTemporary(depthRT);
        depthRT = RenderTexture.GetTemporary(rtDesc);
        depthRT.filterMode = FilterMode.Point;
        depthRT.Create();

        return depthRT;
    }

    public static void Dispatch(CommandBuffer cmd, ComputeShader cs, int kernel, Vector2Int lutSize)
    {
        cs.GetKernelThreadGroupSizes(kernel, out var threadNumX, out var threadNumY, out var threadNumZ);
        cmd.DispatchCompute(cs, kernel, lutSize.x / (int)threadNumX,
            lutSize.y / (int)threadNumY, 1);
    }

    public static void Dispatch(ComputeShader cs, int kernel, Vector2Int lutSize)
    {
        cs.GetKernelThreadGroupSizes(kernel, out var threadNumX, out var threadNumY, out var threadNumZ);
        cs.Dispatch(kernel, lutSize.x / (int)threadNumX,
            lutSize.y / (int)threadNumY, 1);
    }

    public static Vector4[] BoundToPoint(Bounds b)
    {
        b.size = new Vector3(2, 2, 2);
        Vector4[] boundingBox = new Vector4[8];
        boundingBox[0] = new Vector4(b.min.x, b.min.y, b.min.z, 1);
        boundingBox[1] = new Vector4(b.max.x, b.max.y, b.max.z, 1);
        boundingBox[2] = new Vector4(boundingBox[0].x, boundingBox[0].y, boundingBox[1].z, 1);
        boundingBox[3] = new Vector4(boundingBox[0].x, boundingBox[1].y, boundingBox[0].z, 1);
        boundingBox[4] = new Vector4(boundingBox[1].x, boundingBox[0].y, boundingBox[0].z, 1);
        boundingBox[5] = new Vector4(boundingBox[0].x, boundingBox[1].y, boundingBox[1].z, 1);
        boundingBox[6] = new Vector4(boundingBox[1].x, boundingBox[0].y, boundingBox[1].z, 1);
        boundingBox[7] = new Vector4(boundingBox[1].x, boundingBox[1].y, boundingBox[0].z, 1);
        return boundingBox;
    }

    static Plane[] sCameraFrustumPlanes = new Plane[6];
    static Vector4[] sCameraFrustumVector4 = new Vector4[6];

    public static Vector4[] CalculateCameraVector4(Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, sCameraFrustumPlanes);
        for (int i = 0; i < 6; i++)
        {
            var normal = sCameraFrustumPlanes[i].normal;
            sCameraFrustumVector4[i].Set(normal.x, normal.y, normal.z, sCameraFrustumPlanes[i].distance);
        }

        return sCameraFrustumVector4;
    }

    public static Color[] lodColors;

    public static void GenerateRandomLODColors(int numColors)
    {
        lodColors = new Color[numColors];
        Random.InitState(123456789);
        for (int i = 0; i < numColors; i++)
        {
            lodColors[i] = Random.ColorHSV(); // 生成随机颜色
        }
    }

    public static Color GetDebugColor(int i)
    {
        return lodColors[i];
    }

    public static void SetComputeShaderConstant(Type structType, object cb, CommandBuffer cs, bool isMember = false)
    {
        FieldInfo[] fields = structType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (FieldInfo field in fields)
        {
            var value = field.GetValue(cb);
            string fileName = field.Name;
            if (isMember)
                fileName = "_" + fileName;
            if (field.FieldType == typeof(float))
            {
                cs.SetGlobalFloat(fileName, (float)value);
            }
            else if (field.FieldType == typeof(int))
            {
                cs.SetGlobalInt(fileName, (int)value);
            }
            else if (field.FieldType == typeof(Vector2))
            {
                cs.SetGlobalVector(fileName, (Vector2)value);
            }
            else if (field.FieldType == typeof(Vector3))
            {
                cs.SetGlobalVector(fileName, (Vector3)value);
            }
            else if (field.FieldType == typeof(Vector4))
            {
                cs.SetGlobalVector(fileName, (Vector4)value);
            }
            else
            {
                throw new Exception("not find type:" + field.FieldType);
            }
        }
    }
}

public struct GlobalValue
{
    public Vector3 _CameraWorldPos;
    public int _SplitNum;
    public int _MAXLOD;
    public int _TerrainSize;
    public int _PerNodePacthNum;
    public int _PerPacthGridNum;
    public int _PerPacthSize;
    public float _LodJudgeFactor;
    public Vector3 _HizMapSize;
};

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

public struct RenderPatch
{
    public uint4 _wpos;
    public uint4 _lodTrans;
};