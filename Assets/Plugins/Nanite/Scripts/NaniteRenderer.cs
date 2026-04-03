using UnityEngine;
using UnityEngine.Rendering;
using System.Runtime.InteropServices;

namespace UnityNanite
{
    [RequireComponent(typeof(Camera))]
    public class NaniteRenderer : MonoBehaviour
    {
        [Header("Shaders")]
        public ComputeShader cullingShader;
        public ComputeShader softwareRasterizer;
        public Shader materialPassShader;
        public ComputeShader hzbGenerateShader;

        [Header("Debug")]
        public bool debugMode = false;
        public bool showLODWatermark = false;
        public Color[] lodColors = new Color[] {
            Color.green, Color.yellow, new Color(1f, 0.5f, 0f), // LOD 0,1,2
            Color.red, Color.magenta, Color.cyan, // LOD 3,4,5
            Color.blue, Color.white // LOD 6,7
        };

        private Material materialPassMat;
        private CommandBuffer cmd;

        [Header("Buffers (Debug/Simulated)")]
        private ComputeBuffer bvhBuffer;
        private ComputeBuffer clusterGroupBuffer;
        private ComputeBuffer clusterBuffer;
        private ComputeBuffer visibleClustersBuffer;
        private ComputeBuffer visibleTrianglesBuffer;
        private ComputeBuffer hwClusterIndicesBuffer; // For HW rasterization

        // Indirect Dispatch & Draw Buffers
        private ComputeBuffer indirectCullArgs;
        private ComputeBuffer indirectRasterArgs;
        private ComputeBuffer indirectDrawArgs;
        private ComputeShader buildIndirectArgsShader;

        private ComputeBuffer visibilityBuffer64;
        private RenderTexture[] hzbMips;

        private Camera mainCam;

        void Start()
        {
            mainCam = GetComponent<Camera>();
            if (materialPassShader != null)
                materialPassMat = new Material(materialPassShader);
            
            cmd = new CommandBuffer();
            cmd.name = "Nanite Render Pass";

            // 把CommandBuffer添加到相机渲染循环中（在GBuffer之前绘制Nanite物体）
            mainCam.AddCommandBuffer(CameraEvent.BeforeGBuffer, cmd);

            InitBuffers();
            InitHZB();
        }

        void InitBuffers()
        {
            // 初始化缓冲大小（在实际工程中根据加载的模型大小动态分配）
            bvhBuffer = new ComputeBuffer(1000, Marshal.SizeOf(typeof(BVHNode)));
            clusterGroupBuffer = new ComputeBuffer(1000, Marshal.SizeOf(typeof(ClusterGroup)));
            clusterBuffer = new ComputeBuffer(1000, Marshal.SizeOf(typeof(Cluster)));
            
            visibleClustersBuffer = new ComputeBuffer(1000, sizeof(uint), ComputeBufferType.Append);
            hwClusterIndicesBuffer = new ComputeBuffer(100000, sizeof(uint), ComputeBufferType.Append);
            // 假设 VisibleTriangle 结构体占用 44 字节
            visibleTrianglesBuffer = new ComputeBuffer(100000, 44, ComputeBufferType.Append); 

            // 间接调度参数缓冲 (Dispatch args = 3 uints, DrawInstanced args = 5 uints)
            indirectCullArgs = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            indirectRasterArgs = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            indirectDrawArgs = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);

            // 初始化 64位 可见性缓冲 (用于存储 32位深度 + 32位Payload)
            visibilityBuffer64 = new ComputeBuffer(Screen.width * Screen.height, 8); // 8 bytes per ulong
        }

        void InitHZB()
        {
            int hzbWidth = Mathf.NextPowerOfTwo(Screen.width) / 2;
            int hzbHeight = Mathf.NextPowerOfTwo(Screen.height) / 2;
            int mipCount = (int)Mathf.Log(Mathf.Max(hzbWidth, hzbHeight), 2) + 1;

            hzbMips = new RenderTexture[mipCount];
            for (int i = 0; i < mipCount; i++)
            {
                hzbMips[i] = new RenderTexture(hzbWidth, hzbHeight, 0, RenderTextureFormat.RFloat);
                hzbMips[i].enableRandomWrite = true;
                hzbMips[i].filterMode = FilterMode.Point;
                hzbMips[i].Create();
                
                hzbWidth = Mathf.Max(1, hzbWidth / 2);
                hzbHeight = Mathf.Max(1, hzbHeight / 2);
            }
        }

        void Update()
        {
            if (cullingShader == null || softwareRasterizer == null || materialPassMat == null || buildIndirectArgsShader == null) return;

            cmd.Clear();
            
            // 1. 生成HZB（Hierarchical Z-Buffer）
            // GenerateHZB(cmd);

            // 2. 清空追加缓冲
            cmd.SetBufferCounterValue(visibleClustersBuffer, 0);
            cmd.SetBufferCounterValue(visibleTrianglesBuffer, 0);
            cmd.SetBufferCounterValue(hwClusterIndicesBuffer, 0);

            // 清空Visibility Buffer
            // 简单模拟: Dispatch a clear kernel or use a pre-filled zero buffer
            // 这里为了简化演示代码，直接忽略Clear的Dispatch代码，在生产中需要用一个简单的ComputeShader清空 visibilityBuffer64


            // 3. 执行GPU视锥体剔除和BVH遍历
            int traverseKernel = cullingShader.FindKernel("TraverseBVH");
            cmd.SetComputeMatrixParam(cullingShader, "_MatrixVP", mainCam.projectionMatrix * mainCam.worldToCameraMatrix);
            cmd.SetComputeVectorParam(cullingShader, "_CameraPos", mainCam.transform.position);
            cmd.SetComputeFloatParam(cullingShader, "_ScreenResolutionY", Screen.height);
            cmd.SetComputeFloatParam(cullingShader, "_FOV", mainCam.fieldOfView * Mathf.Deg2Rad);

            cmd.SetComputeBufferParam(cullingShader, traverseKernel, "_BVHNodes", bvhBuffer);
            cmd.SetComputeBufferParam(cullingShader, traverseKernel, "_ClusterGroups", clusterGroupBuffer);
            cmd.SetComputeBufferParam(cullingShader, traverseKernel, "_VisibleClusters", visibleClustersBuffer);
            
            cmd.DispatchCompute(cullingShader, traverseKernel, 1, 1, 1);

            // --- 拷贝可见Cluster数量并构建间接调度参数 ---
            cmd.CopyCounterValue(visibleClustersBuffer, indirectCullArgs, 0); // 暂存到 indirectCullArgs 开头
            int buildArgsCull = buildIndirectArgsShader.FindKernel("BuildIndirectArgsCull");
            cmd.SetComputeBufferParam(buildIndirectArgsShader, buildArgsCull, "_VisibleClustersCount", indirectCullArgs);
            cmd.SetComputeBufferParam(buildIndirectArgsShader, buildArgsCull, "_IndirectCullArgs", indirectCullArgs);
            cmd.DispatchCompute(buildIndirectArgsShader, buildArgsCull, 1, 1, 1);

            // 4. 执行簇级剔除与HZB遮挡剔除
            int clusterCullKernel = cullingShader.FindKernel("CullClusters");
            if (hzbMips != null && hzbMips.Length > 0)
            {
                cmd.SetComputeTextureParam(cullingShader, clusterCullKernel, "_HZBTexture", hzbMips[0]);
                cmd.SetComputeVectorParam(cullingShader, "_HZBSize", new Vector2(hzbMips[0].width, hzbMips[0].height));
            }
            cmd.SetComputeBufferParam(cullingShader, clusterCullKernel, "_VisibleClusters", visibleClustersBuffer);
            cmd.SetComputeBufferParam(cullingShader, clusterCullKernel, "_VisibleTrianglesSW", visibleTrianglesBuffer);
            cmd.SetComputeBufferParam(cullingShader, clusterCullKernel, "_HWIndicesBuffer", hwClusterIndicesBuffer);
            cmd.DispatchCompute(cullingShader, clusterCullKernel, indirectCullArgs, 0); // 间接调度

            // --- 拷贝软硬件渲染数据数量并构建间接调度参数 ---
            cmd.CopyCounterValue(visibleTrianglesBuffer, indirectRasterArgs, 0);
            cmd.CopyCounterValue(hwClusterIndicesBuffer, indirectDrawArgs, 0);
            int buildArgsRaster = buildIndirectArgsShader.FindKernel("BuildIndirectArgsRaster");
            int buildArgsDraw = buildIndirectArgsShader.FindKernel("BuildIndirectArgsDraw");
            cmd.SetComputeBufferParam(buildIndirectArgsShader, buildArgsRaster, "_VisibleTrianglesCount", indirectRasterArgs);
            cmd.SetComputeBufferParam(buildIndirectArgsShader, buildArgsRaster, "_IndirectRasterArgs", indirectRasterArgs);
            cmd.SetComputeBufferParam(buildIndirectArgsShader, buildArgsDraw, "_HWIndicesCount", indirectDrawArgs);
            cmd.SetComputeBufferParam(buildIndirectArgsShader, buildArgsDraw, "_IndirectDrawArgs", indirectDrawArgs);
            cmd.DispatchCompute(buildIndirectArgsShader, buildArgsRaster, 1, 1, 1);
            cmd.DispatchCompute(buildIndirectArgsShader, buildArgsDraw, 1, 1, 1);

            // 5. 执行软光栅化
            int rasterKernel = softwareRasterizer.FindKernel("SoftwareRasterize");
            cmd.SetComputeMatrixParam(softwareRasterizer, "_MatrixVP", mainCam.projectionMatrix * mainCam.worldToCameraMatrix);
            cmd.SetComputeIntParam(softwareRasterizer, "_ScreenWidth", Screen.width);
            cmd.SetComputeIntParam(softwareRasterizer, "_ScreenHeight", Screen.height);
            cmd.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_VisibleTrianglesSW", visibleTrianglesBuffer);
            cmd.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_VisibilityBuffer64", visibilityBuffer64);
            cmd.DispatchCompute(softwareRasterizer, rasterKernel, indirectRasterArgs, 0);

            // 6. 执行硬光栅化 (HW Rasterization)
            // cmd.DrawProceduralIndirect(Matrix4x4.identity, hwMaterial, 0, MeshTopology.Triangles, indirectDrawArgs, 0);

            // 7. 材质Pass与G-Buffer写入
            cmd.SetRenderTarget(BuiltinRenderTextureType.GBuffer0); // 输出到反照率等G-Buffer
            materialPassMat.SetBuffer("_VisibilityBuffer64", visibilityBuffer64);
            materialPassMat.SetInt("_ScreenWidth", Screen.width);
            materialPassMat.SetInt("_ScreenHeight", Screen.height);
            
            // --- Debug 模式传参 ---
            materialPassMat.SetInt("_DebugMode", debugMode ? 1 : 0);
            materialPassMat.SetInt("_ShowLODWatermark", showLODWatermark ? 1 : 0);
            
            Vector4[] colorArray = new Vector4[8];
            for(int i = 0; i < 8; i++)
            {
                colorArray[i] = i < lodColors.Length ? (Vector4)lodColors[i] : Vector4.one;
            }
            materialPassMat.SetVectorArray("_LODColors", colorArray);

            cmd.DrawProcedural(Matrix4x4.identity, materialPassMat, 0, MeshTopology.Triangles, 3); // 全屏三角形
        }

        void OnDestroy()
        {
            if (mainCam != null && cmd != null)
                mainCam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, cmd);

            bvhBuffer?.Release();
            clusterGroupBuffer?.Release();
            clusterBuffer?.Release();
            visibleClustersBuffer?.Release();
            visibleTrianglesBuffer?.Release();
            hwClusterIndicesBuffer?.Release();
            
            indirectCullArgs?.Release();
            indirectRasterArgs?.Release();
            indirectDrawArgs?.Release();
            
            if (visibilityBuffer != null) visibilityBuffer.Release();
            
            if (hzbMips != null)
            {
                foreach (var rt in hzbMips)
                {
                    if (rt != null) rt.Release();
                }
            }
        }
    }
}