Shader "Hidden/Nanite/MaterialPass"
{
    Properties
    {
        _VisibilityTex ("Visibility Buffer", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            ZWrite On ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

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

            Texture2D<uint2> _VisibilityTex;
            
            // Buffers to fetch vertex data
            // StructuredBuffer<Vertex> _Vertices;
            // StructuredBuffer<uint> _Indices;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                uint2 visData = _VisibilityTex.Load(int3(i.uv * _ScreenParams.xy, 0));
                
                // If empty/clear color
                if (visData.y == 0) discard;

                uint clusterID = visData.y >> 12;
                uint triID = visData.y & 0xFFF;
                
                // Fetch vertex data using clusterID and triID
                // Calculate barycentric coords using screenspace derivates or similar
                // Interpolate UVs, Normals
                // Sample albedo, normal map
                
                float3 albedo = float3(frac(clusterID * 0.1), frac(triID * 0.3), 0.5); // debug color
                
                return float4(albedo, 1.0);
            }
            ENDHLSL
        }
    }
}