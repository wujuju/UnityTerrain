using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

public class RendererFeatureTerrain : ScriptableRendererFeature
{
    class CustomRenderPass : ScriptableRenderPass
    {
        private TerrainGPUDriven _terrainGPUDriven;
        private Action<CommandBuffer> renderCallBack;

        public CustomRenderPass(TerrainGPUDriven terrainGPUDriven, Action<CommandBuffer> renderCallBack)
        {
            this._terrainGPUDriven = terrainGPUDriven;
            this.renderCallBack = renderCallBack;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("GenerateTerrain");
            if (sIsWorking)
            {
#if UNITY_EDITOR
                _terrainGPUDriven.Draw(cmd, Camera.main);
                renderCallBack(cmd);
#else
                _terrainGPUDriven.Draw(cmd, renderingData.cameraData.camera);
#endif
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
        }
    }

    private static RendererFeatureTerrain _s;

    public static RendererFeatureTerrain instance
    {
        get { return _s; }
    }

    CustomRenderPass m_ScriptablePass;
    public Material instanceMaterial;
    public ComputeShader computeShader;
    [Range(1, 10)] public float LODJudgeFactor = 10f;
    private float lastLODJudgeFactor;
    public bool isViewFrustumCulling;
    public bool isHizCulling = false;
    public bool isFixLODSeam;
    public bool isPatchReadBack;
    public bool isPatchDebug;
    public bool isMipDebug = false;
    public bool isLODDebug = false;
    public bool isManualUpdate;
    private TerrainGPUDriven _terrainGPUDriven;
    public static uint3[] sReadBackPatchList { get; private set; }
    public static RenderPatch[] sReadBackRenderPatchList { get; private set; }

    public static NodeInfoStruct[] sNodeStructs
    {
        get { return _s._terrainGPUDriven._nodeStructs; }
    }

    public static uint[] sGPUInfo { get; protected set; }

    public static bool sIsWorking { get; set; }

    public override void Create()
    {
        _s = this;
        sGPUInfo = new uint[2];
        if (_terrainGPUDriven == null)
        {
            _terrainGPUDriven = new TerrainGPUDriven();
            _terrainGPUDriven.Init(instanceMaterial, computeShader);
            _terrainGPUDriven._gValue._LodJudgeFactor = LODJudgeFactor;
        }

        m_ScriptablePass = new CustomRenderPass(_terrainGPUDriven, OnRenderLater);
        m_ScriptablePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public static void CreateMap(Texture2D heightMap, float maxHeight)
    {
        sIsWorking = true;
        _s.instanceMaterial.SetFloat("_max_height", maxHeight);
        _s.computeShader.SetFloat("_max_height", maxHeight);
        _s._terrainGPUDriven.CreateHeightMap(heightMap);
        _s._terrainGPUDriven.InitTerrain(heightMap.width);
    }

    public static void SetTerrainTexture(Texture2D mainMap, Texture2D normalMap)
    {
        _s.instanceMaterial.SetTexture("_MainTex", mainMap);
        _s.instanceMaterial.SetTexture("_NormapTex", normalMap);
    }

    void OnRenderLater(CommandBuffer cmd)
    {
        if (isPatchReadBack)
        {
            sReadBackPatchList = _terrainGPUDriven.ReadBackHandler(cmd, sGPUInfo);
            // if (isMipDebug)
            //     sReadBackRenderPatchList = _terrainGPUDriven.ReadBackRenderHandler();
        }
        else
        {
            sGPUInfo[0] = 0;
            sGPUInfo[1] = 0;
        }

        _terrainGPUDriven._gValue._LodJudgeFactor = LODJudgeFactor;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (instanceMaterial)
        {
            if (isLODDebug)
                instanceMaterial.EnableKeyword("_DEBUG_LOD");
            else
                instanceMaterial.DisableKeyword("_DEBUG_LOD");
            if (isMipDebug)
                instanceMaterial.EnableKeyword("_DEBUG_MIP");
            else
                instanceMaterial.DisableKeyword("_DEBUG_MIP");
            if (isFixLODSeam)
                instanceMaterial.EnableKeyword("_FIX_LOD_SEAM");
            else
                instanceMaterial.DisableKeyword("_FIX_LOD_SEAM");

            if (isPatchDebug)
                instanceMaterial.EnableKeyword("_DEBUG_PATCH");
            else
                instanceMaterial.DisableKeyword("_DEBUG_PATCH");
#if UNITY_EDITOR
            if (TerrainConfig.lodColors != null)
                instanceMaterial.SetColorArray("_debugColor", TerrainConfig.lodColors);
#endif
        }

        if (computeShader)
        {
            if (SystemInfo.usesReversedZBuffer)
                computeShader.EnableKeyword("_REVERSE_Z");
            else
                computeShader.DisableKeyword("_REVERSE_Z");

            if (isViewFrustumCulling)
                computeShader.EnableKeyword("_VIEW_FRUSTUM_CULLING");
            else
                computeShader.DisableKeyword("_VIEW_FRUSTUM_CULLING");

            if (isHizCulling)
                computeShader.EnableKeyword("_HIZ_CULLING");
            else
                computeShader.DisableKeyword("_HIZ_CULLING");

            if (isMipDebug)
                computeShader.EnableKeyword("_DEBUG_MIP");
            else
                computeShader.DisableKeyword("_DEBUG_MIP");
        }

        _terrainGPUDriven.isManualUpdate = isManualUpdate;
        renderer.EnqueuePass(m_ScriptablePass);
    }

    private void OnDisable()
    {
        sIsWorking = false;
    }
}