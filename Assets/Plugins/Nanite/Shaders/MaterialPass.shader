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
            #pragma require int64 // Require SM 6.0 64-bit int support
            #include "UnityCG.cginc"

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            struct fragOut
            {
                float4 color : SV_Target;
                float depth : SV_Depth;
            };

            // Texture2D<uint2> _VisibilityTex; // Old 2D approach
            StructuredBuffer<uint64_t> _VisibilityBuffer64; // New 64-bit linear buffer

            // Buffers to fetch vertex data
            // StructuredBuffer<Vertex> _Vertices;
            // StructuredBuffer<uint> _Indices;

            v2f vert (appdata v)
            {
                v2f o;
                // Generate a fullscreen triangle
                float2 uv = float2((v.vertexID << 1) & 2, v.vertexID & 2);
                o.vertex = float4(uv * 2.0f - 1.0f, 0.0f, 1.0f);
                o.uv = uv;
                return o;
            }

            uint _ScreenWidth;
            uint _ScreenHeight;

            fragOut frag (v2f i)
            {
                fragOut o;
                
                uint x = (uint)(i.uv.x * _ScreenWidth);
                uint y = (uint)(i.uv.y * _ScreenHeight);
                uint pixelIndex = y * _ScreenWidth + x;

                uint64_t visData = _VisibilityBuffer64[pixelIndex];
                
                if (visData == 0) discard;

                uint payload = (uint)(visData & 0xFFFFFFFF);
                uint materialID = (payload >> 24) & 0xFF;
                uint clusterID  = (payload >> 12) & 0xFFF;
                uint triID      = payload & 0xFFF;
                float depth     = asfloat((uint)(visData >> 32)); // Decode depth
                
                // Fetch vertex data using clusterID and triID
                // Calculate barycentric coords using screenspace derivates or similar
                // Interpolate UVs, Normals
                
                // --- 这里我们利用解码出来的 MaterialID 来改变着色 ---
                float3 albedo = float3(0.0, 0.0, 0.0);
                if (materialID == 0)
                    albedo = float3(frac(clusterID * 0.1), frac(triID * 0.3), 0.5); // Default / Debug
                else if (materialID == 1)
                    albedo = float3(1.0, 0.0, 0.0); // Material 1 -> Red
                else if (materialID == 2)
                    albedo = float3(0.0, 1.0, 0.0); // Material 2 -> Green
                else
                    albedo = float3(0.0, 0.0, 1.0); // Material 3+ -> Blue
                
                o.color = float4(albedo, 1.0);
                o.depth = depth; // Write back to Depth Buffer for SSAO/Shadows
                return o;
            }
            ENDHLSL
        }
    }
}