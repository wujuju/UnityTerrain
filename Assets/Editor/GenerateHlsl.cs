using System;
using System.Reflection;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Windows;

public class GenerateHLSL
{
    [MenuItem("Tools/SavedImages")]
    private static void SavedImages()
    {
        // SaveTex2DArrayToPNG(VirtualTexture.ttt, "Assets/SavedImages/savedArray.png");
    }

    static void SaveTex2DArrayToPNG(RenderTexture texArray, string filePath)
    {
        int width = texArray.width;
        int height = texArray.height;
        int depth = texArray.volumeDepth;

        // 创建一个新的 2D 纹理，用于临时存储每个层的像素数据
        Texture2D tempTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        byte[] bytes;

        // 遍历 Tex2DArray 的每个层，并保存为 PNG 图像
        for (int i = 0; i < depth; i++)
        {
            var active = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.Default);
            Graphics.Blit(texArray, active, i, 0);
            tempTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tempTex.Apply();
            bytes = tempTex.EncodeToPNG();
            File.WriteAllBytes(filePath.Replace(".png", "_" + i + ".png"), bytes);
        }
    }

    // [MenuItem("Tools/GenerateNormal")]
    // private static void GenerateNormal()
    // {
    //     var texture = AssetDatabase.LoadAssetAtPath<Texture>("Assets/Resources/Test/height.jpg");
    //     var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Resources/Instance/InstanceCulling.compute");
    //     
    //     var size = new Vector2Int(texture.width, texture.height);
    //     var rtdesc = new RenderTextureDescriptor(size.x, size.y, RenderTextureFormat.ARGB32);
    //     rtdesc.enableRandomWrite = true;
    //     rtdesc.autoGenerateMips = false;
    //     var _NormalTex = RenderTexture.GetTemporary(rtdesc);
    //     int kernelIndex = cs.FindKernel("HeightToNormal");
    //     cs.SetTexture(kernelIndex, Shader.PropertyToID("_HeightTex"), texture);
    //     cs.SetTexture(kernelIndex, Shader.PropertyToID("_NormalTex"), _NormalTex);
    //     float strength = 2.5f;
    //     float level = 7;
    //     cs.SetFloat("_dz", (float)(1.0 / strength * (1.0 + Mathf.Pow(2f, level))));
    //     cs.SetFloat("_invertR", 1);
    //     cs.SetFloat("_invertG", 1);
    //     cs.SetFloat("_invertH", 1);
    //     cs.SetInt("_type", 0);
    //     cs.SetInt("_heightOffset", 1);
    //     BlockTerrainGPUConfig.Dispatch(cs, kernelIndex, size);
    //     
    //     SaveRenderTextureAsPNG(_NormalTex, "Assets/Resources/Test/normal2.jpg");
    //     RenderTexture.ReleaseTemporary(_NormalTex);
    // }

    static void SaveRenderTextureAsPNG(RenderTexture renderTexture, string filePath)
    {
        // 创建一个中转的Texture2D来存储RenderTexture的内容
        Texture2D texture2D = new Texture2D(renderTexture.width, renderTexture.height);

        // 读取RenderTexture的内容到Texture2D
        RenderTexture.active = renderTexture;
        texture2D.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
        texture2D.Apply();

        // 将Texture2D保存为PNG图像文件
        byte[] pngData = texture2D.EncodeToJPG();
        System.IO.File.WriteAllBytes(filePath, pngData);

        // 在Unity编辑器中刷新Asset数据库，使新的文件在Project视图中可见
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif

        Debug.Log("RenderTexture saved to: " + filePath);
    }

    // 添加一个自定义菜单项，点击该菜单项会在控制台输出一条消息
    [MenuItem("Tools/GenerateHLSL")]
    private static void GenerateHLSLCode()
    {
        string code = string.Format("#ifndef {0}\n#define {0}\n", "TerrainInfoStruct_CS_HLSL");
        code += GenerateHLSLCode(typeof(NodeInfoStruct), true, false);
        code += GenerateHLSLCode(typeof(RenderPatch), true);
        // code += GenerateHLSLCode(typeof(MipLevel), true);
        code += GenerateHLSLCode(typeof(GlobalValue), false);
        code += "#endif\n";
        string savePath = "Assets/Resources/Instance/TerrainInfoStruct.cs.hlsl";
        File.WriteAllBytes(savePath, Encoding.UTF8.GetBytes(code));
    }

    [MenuItem("Tools/GeneratehaderPropertyToIDL")]
    private static void GeneratehaderPropertyToIDL()
    {
        GenerateShaderPropertyToID(typeof(GlobalValue));
    }

    static void GenerateShaderPropertyToID(Type structType)
    {
        FieldInfo[] fields = structType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        StringBuilder sb = new StringBuilder();
        StringBuilder sb2 = new StringBuilder();
        foreach (FieldInfo field in fields)
        {
            string fileName = field.Name;
            var fType = field.FieldType;
            sb.Append(string.Format("public static int SID{0} = Shader.PropertyToID(\"{0}\");\n", fileName));

            if (field.FieldType == typeof(float))
            {
                sb2.Append(string.Format("cmd.SetGlobalFloat(SID{0},{0});\n", fileName));
            }
            else if (field.FieldType == typeof(int))
            {
                sb2.Append(string.Format("cmd.SetGlobalInt(SID{0},{0});\n", fileName));
            }
            else if (fType == typeof(Vector2) || fType == typeof(Vector3) || fType == typeof(Vector4))
            {
                sb2.Append(string.Format("cmd.SetGlobalVector(SID{0},{0});\n", fileName));
            }
            else
            {
                throw new Exception("not find type:" + field.FieldType);
            }
        }

        
        Debug.LogError(sb.ToString() + sb2.ToString());
        // Debug.LogError(sb2.ToString());
    }

    static string GenerateHLSLCode(Type structType, bool isStruct, bool isAddHead_ = true)
    {
        StringBuilder sb = new StringBuilder();
        // sb.Append(string.Format("#ifndef {0}\n#define {0}\n", header));
        if (isStruct)
        {
            sb.Append(string.Format("struct {0}\n", structType.Name.ToString()));
            sb.Append("{\n");
        }
        else
            sb.Append(string.Format("CBUFFER_START ({0}Buffer)\n", structType.Name.ToString()));

        FieldInfo[] fields = structType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (FieldInfo field in fields)
        {
            var fileName = field.Name;
            if (isAddHead_ && !fileName.StartsWith("_"))
                fileName = "_" + field.Name;
            if (field.FieldType == typeof(float))
            {
                sb.Append(string.Format("float {0};\n", fileName));
            }
            else if (field.FieldType == typeof(uint))
            {
                sb.Append(string.Format("uint {0};\n", fileName));
            }
            else if (field.FieldType == typeof(int))
            {
                sb.Append(string.Format("int {0};\n", fileName));
            }
            else if (field.FieldType == typeof(uint2))
            {
                sb.Append(string.Format("uint2 {0};\n", fileName));
            }
            else if (field.FieldType == typeof(Vector2))
            {
                sb.Append(string.Format("float2 {0};\n", fileName));
            }
            else if (field.FieldType == typeof(Vector3))
            {
                sb.Append(string.Format("float3 {0};\n", fileName));
            }
            else if (field.FieldType == typeof(Vector4))
            {
                sb.Append(string.Format("float4 {0};\n", fileName));
            }
            else if (field.FieldType == typeof(uint4))
            {
                sb.Append(string.Format("uint4 {0};\n", fileName));
            }
            else
            {
                throw new Exception("cant find type:" + field.FieldType);
            }
        }

        if (isStruct)
            sb.Append("};\n");
        else
            sb.Append("CBUFFER_END\n");
        // sb.Append("#endif\n");

        return sb.ToString();
    }
}