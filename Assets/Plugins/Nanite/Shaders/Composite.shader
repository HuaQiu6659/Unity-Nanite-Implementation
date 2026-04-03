Shader "Hidden/NaniteComposite"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Overlay" }
        Pass
        {
            ZTest Always 
            Cull Off 
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha // Composite alpha over scene

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "HLSLSupport.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;

            v2f vert (appdata v)
            {
                v2f o;
                // Fullscreen triangle compatibility without UnityCG.cginc
                // In modern pipelines, Blit provides correct screen quads
                o.vertex = float4(v.vertex.xy, 0.0, 1.0);
                
                #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x < 0)
                    o.vertex.y = -o.vertex.y;
                #endif
                
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}