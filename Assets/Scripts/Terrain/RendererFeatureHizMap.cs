using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RendererFeatureHizMap : ScriptableRendererFeature
{
    class CustomRenderPass : ScriptableRenderPass
    {
        int ShaderID_ScaleSize;
        int Kernel_GenerateHizMap;
        private ComputeShader cs;
        private Vector2Int depathRTSize;
        private RenderTextureDescriptor rtDesc;
        private RenderTexture depthRT;

        public CustomRenderPass(ComputeShader cs, Vector3Int depathRTSize)
        {
            this.cs = cs;
            this.depathRTSize = new Vector2Int(depathRTSize.x, depathRTSize.y);
            ShaderID_ScaleSize = Shader.PropertyToID("_SampleDepthScale");
            Kernel_GenerateHizMap = cs.FindKernel("GenerateHizMap");
            rtDesc = new RenderTextureDescriptor(depathRTSize.x, depathRTSize.y)
            {
                mipCount = depathRTSize.z + 1,
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = true,
                colorFormat = RenderTextureFormat.RFloat,
            };
            if (SystemInfo.usesReversedZBuffer)
                cs.EnableKeyword("_REVERSE_Z");
            else
                cs.DisableKeyword("_REVERSE_Z");
            depthRT = TerrainConfig.GetDepthTemporary(rtDesc);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
#if UNITY_EDITOR
            if (renderingData.cameraData.camera.name == "SceneCamera")
                return;
#endif
            var camera = renderingData.cameraData.camera;
            Matrix4x4 projMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
            TerrainGPUDriven.lastFrameVPMatrix = projMatrix * camera.worldToCameraMatrix;
            CommandBuffer cmd = CommandBufferPool.Get("HizMap");
            DrawHiz(cmd, renderingData.cameraData.renderer.cameraDepthTargetHandle);
            // DrawHiz(cmd, null);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        void DrawHiz(CommandBuffer cmd, RTHandle depthRTHandle)
        {
            Vector2Int curSize = depathRTSize * 2;
            cmd.SetGlobalVector(ShaderID_ScaleSize, new Vector4(1.0f / curSize.x, 1.0f / curSize.y));
            cmd.SetGlobalTexture("_InputDepthTex", depthRTHandle);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip0", depthRT, 0);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip1", depthRT, 1);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip2", depthRT, 2);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip3", depthRT, 3);
            TerrainConfig.Dispatch(cmd, cs, Kernel_GenerateHizMap, curSize);

            curSize = curSize / (int)Mathf.Pow(2, 4);
            RenderTexture tempRT = RenderTexture.GetTemporary(curSize.x, curSize.y, 0, rtDesc.colorFormat);
            tempRT.filterMode = FilterMode.Point;
            cmd.CopyTexture(depthRT, 0, 3, tempRT, 0, 0);
            cmd.SetGlobalTexture("_InputDepthTex", tempRT);
            cmd.SetGlobalVector(ShaderID_ScaleSize, new Vector4(1.0f / curSize.x, 1.0f / curSize.y));
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip0", depthRT, 4);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip1", depthRT, 5);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip2", depthRT, 6);
            cmd.SetComputeTextureParam(cs, Kernel_GenerateHizMap, "HIZ_MAP_Mip3", depthRT, 7);
            TerrainConfig.Dispatch(cmd, cs, Kernel_GenerateHizMap, curSize);
            RenderTexture.ReleaseTemporary(tempRT);
        }
    }

    private CustomRenderPass _scriptablePass;
    public ComputeShader hizComputeShader;

    public override void Create()
    {
        _scriptablePass = new CustomRenderPass(hizComputeShader, TerrainGPUDriven.HIZ_MAP_SIZE);
        _scriptablePass.renderPassEvent = RenderPassEvent.BeforeRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // if (BlockTerrainGPU.isHizCulling)
        renderer.EnqueuePass(_scriptablePass);
    }
}