Shader "GaussianSplatting/GaussianSplat"
{
    // -----------------------------------------------------------------------
    // HDRP SubShader
    // -----------------------------------------------------------------------
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
            "IgnoreProjector"= "True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        // Share surviving Gaussian fragments with the camera depth buffer. The
        // fragment shader clips negligible alpha before a depth value is written.
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   5.0
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "GaussianSplatCore.hlsl"
            ENDHLSL
        }
    }

    // -----------------------------------------------------------------------
    // URP SubShader
    // -----------------------------------------------------------------------
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
            "IgnoreProjector"= "True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        // Share depth with URP opaque and transparent geometry.
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   5.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GaussianSplatCore.hlsl"
            ENDHLSL
        }
    }

    // -----------------------------------------------------------------------
    // Built-in RP SubShader (fallback)
    // -----------------------------------------------------------------------
    SubShader
    {
        Tags
        {
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
            "IgnoreProjector"= "True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   5.0
            #include "UnityCG.cginc"
            #include "GaussianSplatCore.hlsl"
            ENDHLSL
        }
    }
}
