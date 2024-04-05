#ifndef TerrainInfoStruct_CS_HLSL
#define TerrainInfoStruct_CS_HLSL
struct NodeInfoStruct
{
int CurLOD;
int NodeNum;
int Offset;
int NodeSize;
int PacthSize;
int VertexScale;
int HeightMipLevel;
};
struct RenderPatch
{
uint4 _wpos;
uint4 _lodTrans;
};
CBUFFER_START (GlobalValueBuffer)
float3 _CameraWorldPos;
int _SplitNum;
int _MAXLOD;
int _TerrainSize;
int _PerNodePacthNum;
int _PerPacthGridNum;
int _PerPacthSize;
float _LodJudgeFactor;
float3 _HizMapSize;
CBUFFER_END
#endif
