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
            #include "HLSLSupport.cginc"

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            struct fragOut
            {
                float4 color : SV_Target;
                float depth : SV_Depth;
            };

            // Texture2D<uint2> _VisibilityTex; // Old 2D approach
            StructuredBuffer<uint> _DepthBuffer;
            StructuredBuffer<uint> _PayloadBuffer;
            float4 _ScreenParams;

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
                
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y;
                #endif
                
                return o;
            }

            uint _ScreenWidth;
            uint _ScreenHeight;
            
            int _DebugMode;
            int _ShowLODWatermark;
            float4 _LODColors[8];

            // A tiny procedural font renderer for numbers 0-9
            float PrintDigit(float2 uv, uint digit) 
            {
                if(uv.x < 0.2 || uv.x > 0.8 || uv.y < 0.1 || uv.y > 0.9) return 0.0;
                uv = (uv - float2(0.2, 0.1)) * float2(1.0/0.6, 1.0/0.8);
                int cx = int(uv.x * 3);
                int cy = int((1.0 - uv.y) * 5);
                int idx = cy * 3 + cx;
                int font[10] = { 31599, 9362, 29671, 29391, 23497, 31183, 31215, 29257, 31727, 31695 };
                if(digit > 9) return 0.0;
                int bits = font[digit];
                return (bits & (1 << (14 - idx))) ? 1.0 : 0.0;
            }

            fragOut frag (v2f i)
            {
                fragOut o;
                
                uint x = (uint)(i.uv.x * _ScreenWidth);
                uint y = (uint)(i.uv.y * _ScreenHeight);
                uint pixelIndex = y * _ScreenWidth + x;

                uint depthInt = _DepthBuffer[pixelIndex];
                
                if (depthInt == 0) discard;

                uint payload = _PayloadBuffer[pixelIndex];
                uint mipLevel   = (payload >> 28) & 0xF;
                uint materialID = (payload >> 24) & 0xF;
                uint clusterID  = (payload >> 12) & 0xFFF;
                uint triID      = payload & 0xFFF;
                float depth     = asfloat(depthInt); // Decode depth
                
                float3 albedo = float3(0.0, 0.0, 0.0);

                if (_DebugMode == 1)
                {
                    // Fetch predefined LOD color
                    float3 lodColor = _LODColors[min(mipLevel, 7)].rgb;
                    
                    // Add slight random variation per cluster (like UE5 reference image)
                    float hash = frac(sin(clusterID * 12.9898) * 43758.5453);
                    albedo = lodColor * (0.8 + 0.2 * hash);

                    if (_ShowLODWatermark == 1)
                    {
                        // Tile UV for watermark
                        float2 watermarkUV = i.uv * _ScreenParams.xy * 0.02; // Scale factor
                        float2 gridUV = frac(watermarkUV);
                        float digit = PrintDigit(gridUV, mipLevel);
                        
                        // Blend watermark (white) over the cluster color
                        albedo = lerp(albedo, float3(1.0, 1.0, 1.0), digit * 0.6);
                    }
                }
                else
                {
                    // Fetch vertex data using clusterID and triID
                    // Interpolate UVs, Normals
                    
                    // --- Normal Material Shading ---
                    if (materialID == 0)
                        albedo = float3(frac(clusterID * 0.1), frac(triID * 0.3), 0.5); // Default / Debug
                    else if (materialID == 1)
                        albedo = float3(1.0, 0.0, 0.0); // Material 1 -> Red
                    else if (materialID == 2)
                        albedo = float3(0.0, 1.0, 0.0); // Material 2 -> Green
                    else
                        albedo = float3(0.0, 0.0, 1.0); // Material 3+ -> Blue
                }
                
                o.color = float4(albedo, 1.0);
                o.depth = depth; // Write back to Depth Buffer for SSAO/Shadows
                return o;
            }
            ENDHLSL
        }
    }
}