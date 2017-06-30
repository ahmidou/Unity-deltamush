// Unity built-in shader source. Copyright (c) 2016 Unity Technologies. MIT license (see license.txt)

#ifndef UNITY_STANDARD_CORE_FORWARD_INCLUDED
#define UNITY_STANDARD_CORE_FORWARD_INCLUDED

#if defined(UNITY_NO_FULL_STANDARD_SHADER)
#   define UNITY_STANDARD_SIMPLE 1
#endif

#include "UnityStandardConfig.cginc"

#if UNITY_STANDARD_SIMPLE
    #include "UnityStandardCoreForwardSimple.cginc"
#else
    #include "UnityStandardCore.cginc"
#endif

struct OutputVertex
{
    float3 pos;
    float3 normal;
};
StructuredBuffer<OutputVertex> Vertices;

VertexInput loadVertex(uint id)
{
	/*
	struct VertexInput
	{
	    float4 vertex   : POSITION;
	    half3 normal    : NORMAL;
	    float2 uv0      : TEXCOORD0;
	    float2 uv1      : TEXCOORD1;
	#if defined(DYNAMICLIGHTMAP_ON) || defined(UNITY_PASS_META)
	    float2 uv2      : TEXCOORD2;
	#endif
	#ifdef _TANGENT_TO_WORLD
	    half4 tangent   : TANGENT;
	#endif
	    UNITY_VERTEX_INPUT_INSTANCE_ID
	};
	*/

	VertexInput vi;
	vi.vertex = float4(Vertices[id].pos, 1);//float4(0,0,0,1);
	vi.normal = Vertices[id].normal;
	vi.uv0 = float2(0,0);
	vi.uv1 = float2(0,0);

	return vi;
}

#if UNITY_STANDARD_SIMPLE
    VertexOutputBaseSimple vertBase2 (uint id : SV_VertexID) { return vertForwardBaseSimple(loadVertex(id)); }
    VertexOutputForwardAddSimple vertAdd2 (uint id : SV_VertexID) { return vertForwardAddSimple(loadVertex(id)); }
    half4 fragBase (VertexOutputBaseSimple i) : SV_Target { return fragForwardBaseSimpleInternal(i); }
    half4 fragAdd (VertexOutputForwardAddSimple i) : SV_Target { return fragForwardAddSimpleInternal(i); }
#else
    VertexOutputForwardBase vertBase2 (uint id : SV_VertexID) { return vertForwardBase(loadVertex(id)); }
    VertexOutputForwardAdd vertAdd2 (uint id : SV_VertexID) { return vertForwardAdd(loadVertex(id)); }
    half4 fragBase (VertexOutputForwardBase i) : SV_Target { return fragForwardBaseInternal(i); }
    half4 fragAdd (VertexOutputForwardAdd i) : SV_Target { return fragForwardAddInternal(i); }
#endif

#endif // UNITY_STANDARD_CORE_FORWARD_INCLUDED
