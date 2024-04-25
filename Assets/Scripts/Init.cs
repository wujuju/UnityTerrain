using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public class Init : MonoBehaviour
{
    public Terrain[] terrains;
    public Texture2D heightMap;

    void Start()
    {
        TerrainConfig.GenerateRandomLODColors(20);
        // Application.targetFrameRate = 10;
        if (SystemInfo.supportsGeometryShaders)
            Debug.Log("GPU supports Geometry Shaders.");
        else
            Debug.LogError("GPU does not support Geometry Shaders.");
        if (!SystemInfo.supportsInstancing)
            Debug.LogError("not supportsInstancing");
        float maxHeight = 255;
        if (heightMap == null)
        {
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
            var terrainBlenderInfos = new TerrainBlenderInfo[terrainNum];
            for (int k = 0; k < terrainNum; k++)
            {
                var terrain = terrains[k];
                var terrainData = terrain.terrainData;
                if (heightSize != (int)terrainData.size.x)
                {
                    Debug.LogError("heightSize != (int)terrainData.size.x");
                    return;
                }

                var offsetX = (k % splitNumX) * heightSize;
                var offsetY = ((k / splitNumX)) * heightSize;
                for (int i = 0; i < heightSize; i++)
                {
                    for (int j = 0; j < heightSize; j++)
                    {
                        pixelData[offsetX + i + (j + offsetY) * totalHeightSize] =
                            terrainData.GetHeight(i, j) / maxHeight;
                    }
                }

                TerrainBlenderInfo terrainBlenderInfo = new TerrainBlenderInfo();
                var controlNum = terrainData.alphamapTextureCount;
                var splatNum = terrainData.alphamapLayers;
                terrainBlenderInfo.controls = new Texture[controlNum];
                terrainBlenderInfo.controls_st = new Vector4[controlNum];
                terrainBlenderInfo.splats = new Texture[splatNum];
                terrainBlenderInfo.normals = new Texture[splatNum];
                terrainBlenderInfo.splats_st = new Vector4[splatNum];
                terrainBlenderInfo.smoothness = new float[splatNum];
                terrainBlenderInfo.metallic = new float[splatNum];
                terrainBlenderInfo.rect = new Rect(offsetX, offsetY, heightSize, heightSize);
                for (int i = 0; i < controlNum; i++)
                {
                    terrainBlenderInfo.controls[i] = terrainData.alphamapTextures[i];
                    terrainBlenderInfo.controls_st[i] = new Vector4(1f / terrainData.alphamapWidth,
                        1f / terrainData.alphamapWidth, terrainData.alphamapWidth, terrainData.alphamapWidth);
                }

                var terrainSize = terrainData.size;
                for (int i = 0; i < splatNum; i++)
                {
                    var terrainLayer = terrainData.terrainLayers[i];
                    terrainBlenderInfo.splats[i] = terrainLayer.diffuseTexture;
                    terrainBlenderInfo.normals[i] = terrainLayer.normalMapTexture;
                    terrainBlenderInfo.smoothness[i] = terrainLayer.smoothness;
                    terrainBlenderInfo.metallic[i] = terrainLayer.metallic;
                    var tileSize = terrainLayer.tileSize;
                    var tileOffset = terrainLayer.tileOffset;
                    Vector4 splatSt = new Vector4(terrainSize.x / tileSize.x, terrainSize.z / tileSize.y,
                        tileOffset.x / tileSize.x, tileOffset.y / tileSize.y);
                    terrainBlenderInfo.splats_st[i] = splatSt;
                }

                terrainBlenderInfos[k] = terrainBlenderInfo;
            }

            VirtualTexture.terrainBlenderInfos = terrainBlenderInfos;
            heightMap.SetPixelData(pixelData, 0);
            heightMap.Apply();
            // var layer = terrains[0].terrainData.terrainLayers[0];
            // RendererFeatureTerrain.SetTerrainTexture(layer.diffuseTexture, layer.normalMapTexture);
            RendererFeatureTerrain.sIsWorking = false;
            ChangeTerrain();
        }

        RendererFeatureTerrain.CreateMap(heightMap, maxHeight);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            ChangeTerrain();
        }
    }

    void ChangeTerrain()
    {
        RendererFeatureTerrain.sIsWorking = !RendererFeatureTerrain.sIsWorking;

        foreach (var terrain in terrains)
        {
            terrain.enabled = (!RendererFeatureTerrain.sIsWorking);
        }
    }

#if UNITY_EDITOR
    private GUIStyle guiStyle;
    private GUIStyle guiToggleStyle;

    private void OnGUI()
    {
        if (guiToggleStyle == null)
        {
            guiStyle = GUI.skin.label;
            guiToggleStyle = GUI.skin.toggle;
            guiToggleStyle.normal.textColor = Color.black;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Space(4);
        GUILayout.BeginVertical();
        GUILayout.Space(4);
        GUILayout.Label(string.Format("NodeCount:{0} PatchCount:{1}", RendererFeatureTerrain.sGPUInfo[0],
            RendererFeatureTerrain.sGPUInfo[1]), guiStyle);
        var instance = RendererFeatureTerrain.instance;
        instance.isViewFrustumCulling =
            GUILayout.Toggle(instance.isViewFrustumCulling, "isViewFrustumCulling", guiToggleStyle);
        instance.isHizCulling = GUILayout.Toggle(instance.isHizCulling, "isHizCulling", guiToggleStyle);
        instance.isFixLODSeam =
            GUILayout.Toggle(instance.isFixLODSeam, "isFixLODSeam", guiToggleStyle);
        instance.isPatchReadBack =
            GUILayout.Toggle(instance.isPatchReadBack, "isPatchReadBack", guiToggleStyle);
        instance.isPatchDebug =
            GUILayout.Toggle(instance.isPatchDebug, "isPatchDebug", guiToggleStyle);
        instance.isMipDebug =
            GUILayout.Toggle(instance.isMipDebug, "isMipDebug", guiToggleStyle);
        if (instance.isMipDebug)
            instance.isLODDebug = false;
        instance.isLODDebug =
            GUILayout.Toggle(instance.isLODDebug, "isLODDebug", guiToggleStyle);
        if (instance.isLODDebug)
            instance.isMipDebug = false;

        if (instance.isLODDebug)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 4; i++)
            {
                guiStyle.normal.textColor = TerrainConfig.GetDebugColor(i);
                GUILayout.Label($"LOD:{i}", guiStyle);
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            for (int i = 4; i < 8; i++)
            {
                guiStyle.normal.textColor = TerrainConfig.GetDebugColor(i);
                GUILayout.Label($"LOD:{i}", guiStyle);
            }

            GUILayout.EndHorizontal();

            guiStyle.normal.textColor = Color.black;
        }

        instance.isManualUpdate =
            GUILayout.Toggle(instance.isManualUpdate, "isManualUpdate", guiToggleStyle);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        GUI.Box(new Rect(0, 0, 200, instance.isLODDebug ? 260 : 210), Texture2D.blackTexture);
    }

    private void OnDrawGizmos()
    {
        var vecSize = new Vector3(1, 0, 1);
        // if (RendererFeatureTerrain.instance.isMipDebug)
        // {
        //     RenderPatch[] nodeRenderList = RendererFeatureTerrain.sReadBackRenderPatchList;
        //     vecSize = new Vector3(8, 0, 8);
        //     for (int i = 0; i < nodeRenderList.Length; i++)
        //     {
        //         var node = nodeRenderList[i]._wpos;
        //         var blockInfo = RendererFeatureTerrain.sNodeStructs[node.z];
        //         Gizmos.DrawWireCube(new Vector3(node.x, 0, node.y), vecSize * blockInfo.VertexScale);
        //         UnityEditor.Handles.Label(new Vector3(node.x, 0, node.y), node.w.ToString());
        //     }
        //
        //     return;
        // }

        if (RendererFeatureTerrain.sGPUInfo[0] <= 0)
            return;
        uint3[] nodeList = RendererFeatureTerrain.sReadBackPatchList;
        for (int i = 0; i < RendererFeatureTerrain.sGPUInfo[0]; i++)
        {
            var node = nodeList[i];
            var blockInfo = RendererFeatureTerrain.sNodeStructs[node.z];
            var size = blockInfo.NodeSize;
            uint pId = 0;
            Gizmos.DrawWireCube(new Vector3((node.x + 0.5f) * size, 0, (node.y + 0.5f) * size), vecSize * size);
            UnityEditor.Handles.Label(new Vector3((node.x + 0.5f) * size, 0, (node.y + 0.5f) * size),
                node.x + ":" + node.y);
        }
    }
#endif
}