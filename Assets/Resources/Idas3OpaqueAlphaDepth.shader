Shader "Hidden/IDAS3/Opaque Alpha Depth"
{
 Properties { _MainTex("Original texture",2D)="white"{} }
 SubShader {
  LOD 200
  Pass {
   Cull Off ZWrite On ZTest LEqual ColorMask 0
   Blend Off
   HLSLPROGRAM
   #pragma target 5.0
   #pragma vertex mainVS
   #pragma geometry showroomGeometry
   #pragma fragment opaqueAlphaDepthPS
   #include "Idas3SceneCommon.cginc"
   float4 opaqueAlphaDepthPS(P v):SV_TARGET {
    // Use the final original alpha, including material routing, source
    // lighting, texture filtering and clipping. Only float rounding near
    // alpha1 is tolerated; actual partial coverage never occludes here.
    clip(mainPS(v).a - .999999);
    return 0;
   }
   ENDHLSL
  }
 }
 // GPUs without a geometry stage (see Idas3Scene.shader).
 SubShader {
  LOD 100
  Pass {
   Cull Off ZWrite On ZTest LEqual ColorMask 0
   Blend Off
   HLSLPROGRAM
   #pragma target 4.5
   #pragma vertex mainVS
   #pragma fragment opaqueAlphaDepthPS
   #define IDAS_VERTEX_FACES
   #include "Idas3SceneCommon.cginc"
   float4 opaqueAlphaDepthPS(P v):SV_TARGET {
    clip(mainPS(v).a - .999999);
    return 0;
   }
   ENDHLSL
  }
 }
 Fallback Off
}
