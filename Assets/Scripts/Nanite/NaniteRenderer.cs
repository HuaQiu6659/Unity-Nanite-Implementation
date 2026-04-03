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

        private Material materialPassMat;
        private CommandBuffer cmd;

        [Header("Buffers (Debug/Simulated)")]
        private ComputeBuffer bvhBuffer;
        private ComputeBuffer clusterGroupBuffer;
        private ComputeBuffer clusterBuffer;
        private ComputeBuffer visibleClustersBuffer;
        private ComputeBuffer visibleTrianglesBuffer;

        private RenderTexture visibilityBuffer;
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
            // 假设 VisibleTriangle 结构体占用 32 字节
            visibleTrianglesBuffer = new ComputeBuffer(100000, 32, ComputeBufferType.Append); 

            // 初始化可见性缓冲 (R32G32_UInt 或者 RGHalf 模拟)
            visibilityBuffer = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.RG32);
            visibilityBuffer.enableRandomWrite = true;
            visibilityBuffer.Create();
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
            if (cullingShader == null || softwareRasterizer == null || materialPassMat == null) return;

            cmd.Clear();
            
            // 1. 生成HZB（Hierarchical Z-Buffer）
            // 在实际流程中，需要先将上一帧的Depth提取，或是这一帧的前向Depth（如果有）进行Mipmap降采样
            // GenerateHZB(cmd);

            // 2. 清空追加缓冲
            cmd.SetBufferCounterValue(visibleClustersBuffer, 0);
            cmd.SetBufferCounterValue(visibleTrianglesBuffer, 0);

            // 清空Visibility Buffer
            cmd.SetRenderTarget(visibilityBuffer);
            cmd.ClearRenderTarget(false, true, Color.black);

            // 3. 执行GPU视锥体剔除和BVH遍历
            int cullingKernel = cullingShader.FindKernel("TraverseBVH");
            cmd.SetComputeMatrixParam(cullingShader, "_MatrixVP", mainCam.projectionMatrix * mainCam.worldToCameraMatrix);
            cmd.SetComputeVectorParam(cullingShader, "_CameraPos", mainCam.transform.position);
            cmd.SetComputeFloatParam(cullingShader, "_ScreenResolutionY", Screen.height);
            cmd.SetComputeFloatParam(cullingShader, "_FOV", mainCam.fieldOfView * Mathf.Deg2Rad);

            cmd.SetComputeBufferParam(cullingShader, cullingKernel, "_BVHNodes", bvhBuffer);
            cmd.SetComputeBufferParam(cullingShader, cullingKernel, "_ClusterGroups", clusterGroupBuffer);
            cmd.SetComputeBufferParam(cullingShader, cullingKernel, "_VisibleClusters", visibleClustersBuffer);
            
            // 调度Culling Kernel（实际情况下可能会根据实例数量调度）
            cmd.DispatchCompute(cullingShader, cullingKernel, 1, 1, 1);

            // 4. 执行簇级剔除与HZB遮挡剔除
            int clusterCullKernel = cullingShader.FindKernel("CullClusters");
            if (hzbMips != null && hzbMips.Length > 0)
            {
                cmd.SetComputeTextureParam(cullingShader, clusterCullKernel, "_HZBTexture", hzbMips[0]);
                cmd.SetComputeVectorParam(cullingShader, "_HZBSize", new Vector2(hzbMips[0].width, hzbMips[0].height));
            }
            cmd.SetComputeBufferParam(cullingShader, clusterCullKernel, "_VisibleClusters", visibleClustersBuffer);
            // cmd.DispatchCompute(cullingShader, clusterCullKernel, ...); // 使用间接调度 (DispatchIndirect)

            // 5. 执行软光栅化
            int rasterKernel = softwareRasterizer.FindKernel("SoftwareRasterize");
            cmd.SetComputeMatrixParam(softwareRasterizer, "_MatrixVP", mainCam.projectionMatrix * mainCam.worldToCameraMatrix);
            cmd.SetComputeIntParam(softwareRasterizer, "_ScreenWidth", Screen.width);
            cmd.SetComputeIntParam(softwareRasterizer, "_ScreenHeight", Screen.height);
            cmd.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_VisibleTrianglesSW", visibleTrianglesBuffer);
            cmd.SetComputeTextureParam(softwareRasterizer, rasterKernel, "_VisibilityBuffer", visibilityBuffer);
            // cmd.DispatchCompute(softwareRasterizer, rasterKernel, ...); // 间接调度根据提取出的三角形数量

            // 6. 材质Pass与G-Buffer写入
            cmd.SetRenderTarget(BuiltinRenderTextureType.GBuffer0); // 输出到反照率等G-Buffer
            cmd.Blit(visibilityBuffer, BuiltinRenderTextureType.CurrentActive, materialPassMat);
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