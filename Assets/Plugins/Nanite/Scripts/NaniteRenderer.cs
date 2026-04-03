using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering; // 新增：用于 URP/HDRP 的 RenderPipelineManager

using UnityEngine.Rendering.Universal;

namespace UnityNanite
{
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct NaniteSkinWeight
    {
        public float w0, w1, w2, w3;
        public int i0, i1, i2, i3;
    }

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

        public RenderTexture naniteOutput;
        private Material compositeMat;

        public Matrix4x4 objectToWorldMatrix = Matrix4x4.identity; // 独立于相机的模型 Transform

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

        private ComputeBuffer depthBuffer;
        private ComputeBuffer payloadBuffer;
        private RenderTexture[] hzbMips;

        // Global Mesh Data
        private ComputeBuffer vertexBuffer;
        private ComputeBuffer indexBuffer;
        private ComputeBuffer visibleClustersCountBuffer;
        private ComputeBuffer visibleTrianglesCountBuffer; // 新增：用于软光栅化边界检查

        // GPU Skinning Data
        private ComputeBuffer boneWeightBuffer;
        private ComputeBuffer boneMatrixBuffer;
        private ComputeBuffer dummySkinWeightBuffer;
        private ComputeBuffer dummyBoneMatrixBuffer;
        private Transform[] bones;
        private Matrix4x4[] bindposes;
        private Matrix4x4[] boneMatrixCache;
        private bool isSkinned = false;

        private Camera mainCam;
        private Vector4[] colorArrayCache = new Vector4[8]; // 缓存：消除每帧 new Vector4[8] 的 GC

        // 标识当前系统是否使用 URP Feature 驱动
        private bool isURPFeatureDriven = false;

        public bool IsReady()
        {
            return cullingShader != null && softwareRasterizer != null && materialPassMat != null && buildIndirectArgsShader != null && bvhBuffer != null && bvhBuffer.IsValid();
        }

        void Start()
        {
            mainCam = GetComponent<Camera>();
            if (materialPassShader != null)
                materialPassMat = new Material(materialPassShader);
            
            cmd = new CommandBuffer();
            cmd.name = "Nanite Render Pass";

            Shader compShader = Shader.Find("Hidden/NaniteComposite");
            if (compShader != null)
                compositeMat = new Material(compShader);

            // 检查当前是否在 URP 环境下，并且用户已经添加了 Feature
            if (GraphicsSettings.renderPipelineAsset != null && GraphicsSettings.renderPipelineAsset.GetType().Name.Contains("Universal"))
            {
                isURPFeatureDriven = true;
            }
            
            if (!isURPFeatureDriven)
            {
                // 如果不是 URP Feature 驱动（比如 Built-in 或 HDRP），继续使用 Overlay 方式
                if (GraphicsSettings.renderPipelineAsset != null)
                {
                    RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                }
            }
        }

        void InitScreenBuffers(Camera cam)
        {
            if (naniteOutput != null) naniteOutput.Release();
            naniteOutput = new RenderTexture(cam.pixelWidth, cam.pixelHeight, 24, RenderTextureFormat.ARGB32);
            naniteOutput.enableRandomWrite = true;
            naniteOutput.Create();

            if (depthBuffer != null) depthBuffer.Release();
            if (payloadBuffer != null) payloadBuffer.Release();
            depthBuffer = new ComputeBuffer(cam.pixelWidth * cam.pixelHeight, 4);
            payloadBuffer = new ComputeBuffer(cam.pixelWidth * cam.pixelHeight, 4);
        }

        void OnDisable()
        {
            if (GraphicsSettings.renderPipelineAsset != null)
            {
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            }
        }

        public void LoadModelData(Vector3[] vertices, uint[] indices, Cluster[] clusters, ClusterGroup[] groups, BVHNode[] bvhNodes, 
            NaniteSkinWeight[] skinWeights = null, Transform[] smrBones = null, Matrix4x4[] smrBindposes = null)
        {
            if (bvhBuffer != null) bvhBuffer.Release();
            if (clusterGroupBuffer != null) clusterGroupBuffer.Release();
            if (clusterBuffer != null) clusterBuffer.Release();
            if (vertexBuffer != null) vertexBuffer.Release();
            if (indexBuffer != null) indexBuffer.Release();
            if (boneWeightBuffer != null) boneWeightBuffer.Release();
            if (boneMatrixBuffer != null) boneMatrixBuffer.Release();

            isSkinned = (skinWeights != null && skinWeights.Length > 0 && smrBones != null && smrBones.Length > 0);
            if (isSkinned)
            {
                boneWeightBuffer = new ComputeBuffer(skinWeights.Length, Marshal.SizeOf(typeof(NaniteSkinWeight)));
                boneWeightBuffer.SetData(skinWeights);
                
                bones = smrBones;
                bindposes = smrBindposes;
                boneMatrixCache = new Matrix4x4[bones.Length];
                boneMatrixBuffer = new ComputeBuffer(bones.Length, Marshal.SizeOf(typeof(Matrix4x4)));
            }

            bvhBuffer = new ComputeBuffer(bvhNodes.Length, Marshal.SizeOf(typeof(BVHNode)));
            bvhBuffer.SetData(bvhNodes);

            clusterGroupBuffer = new ComputeBuffer(groups.Length, Marshal.SizeOf(typeof(ClusterGroup)));
            clusterGroupBuffer.SetData(groups);

            clusterBuffer = new ComputeBuffer(clusters.Length, Marshal.SizeOf(typeof(Cluster)));
            clusterBuffer.SetData(clusters);

            vertexBuffer = new ComputeBuffer(vertices.Length, 12);
            vertexBuffer.SetData(vertices);

            indexBuffer = new ComputeBuffer(indices.Length, 4);
            indexBuffer.SetData(indices);

            // 动态调整追加缓冲的容量上限，防止大型模型溢出导致 GPU 崩溃
            int maxClusters = clusters.Length;
            int maxTriangles = indices.Length / 3;

            if (visibleClustersBuffer == null || visibleClustersBuffer.count < maxClusters)
            {
                visibleClustersBuffer?.Release();
                visibleClustersBuffer = new ComputeBuffer(Mathf.Max(1000, maxClusters), sizeof(uint), ComputeBufferType.Append);
            }
            if (hwClusterIndicesBuffer == null || hwClusterIndicesBuffer.count < maxClusters)
            {
                hwClusterIndicesBuffer?.Release();
                hwClusterIndicesBuffer = new ComputeBuffer(Mathf.Max(100000, maxClusters), sizeof(uint), ComputeBufferType.Append);
            }
            if (visibleTrianglesBuffer == null || visibleTrianglesBuffer.count < maxTriangles)
            {
                visibleTrianglesBuffer?.Release();
                visibleTrianglesBuffer = new ComputeBuffer(Mathf.Max(100000, maxTriangles), 44, ComputeBufferType.Append); // 44 bytes = float3(12)*3 + uint(4)*2
            }
        }

        void InitBuffers()
        {
            // 初始化缓冲大小（在实际工程中根据加载的模型大小动态分配）
            // 注意：真实模型数据现在通过 LoadModelData 注入，这里只初始化动态追加缓冲和参数缓冲
            
            if (visibleClustersBuffer == null) visibleClustersBuffer = new ComputeBuffer(1000, sizeof(uint), ComputeBufferType.Append);
            if (hwClusterIndicesBuffer == null) hwClusterIndicesBuffer = new ComputeBuffer(100000, sizeof(uint), ComputeBufferType.Append);
            if (visibleTrianglesBuffer == null) visibleTrianglesBuffer = new ComputeBuffer(100000, 44, ComputeBufferType.Append); 

            // 间接调度参数缓冲 (Dispatch args = 3 uints, DrawInstanced args = 5 uints)
            indirectCullArgs = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            indirectRasterArgs = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            indirectDrawArgs = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);

            // 计数器保存缓冲 (改为 IndirectArguments 以防止 DX11 下 StructuredBuffer 绑定报错)
            visibleClustersCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);
            visibleTrianglesCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.IndirectArguments);

            // Dummy skinning buffers to prevent D3D11 unbound buffer warnings
            dummySkinWeightBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(NaniteSkinWeight)));
            dummyBoneMatrixBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(Matrix4x4)));
        }

        void InitHZB(Camera cam)
        {
            int hzbWidth = Mathf.NextPowerOfTwo(cam.pixelWidth) / 2;
            int hzbHeight = Mathf.NextPowerOfTwo(cam.pixelHeight) / 2;
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
            if (isURPFeatureDriven) return; // 如果被 URP Feature 托管，则在 ExecuteNanitePass 中执行
            
            if (!IsReady()) return;

            ExecuteNanitePass(cmd, mainCam, naniteOutput, naniteOutput);

            // 立刻执行管线 (此时 Nanite 的结果已渲染到 naniteOutput 中)
            Graphics.ExecuteCommandBuffer(cmd);
        }

        public void ExecuteNanitePass(CommandBuffer cmdBuffer, Camera camera, RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget)
        {
            if (!IsReady()) return;

            // Initialize buffers if not created
            if (visibleClustersBuffer == null) InitBuffers();

            if (depthBuffer == null || depthBuffer.count != camera.pixelWidth * camera.pixelHeight)
            {
                InitScreenBuffers(camera);
                InitHZB(camera);
            }

            if (isSkinned && bones != null && bindposes != null)
            {
                Matrix4x4 rootInv = objectToWorldMatrix.inverse;
                for (int i = 0; i < bones.Length; i++)
                {
                    boneMatrixCache[i] = rootInv * bones[i].localToWorldMatrix * bindposes[i];
                }
                boneMatrixBuffer.SetData(boneMatrixCache);
            }

            cmdBuffer.Clear();
            
            // 1. 生成HZB（Hierarchical Z-Buffer）
            // GenerateHZB(cmdBuffer);

            // 2. 清空追加缓冲
            cmdBuffer.SetBufferCounterValue(visibleClustersBuffer, 0);
            cmdBuffer.SetBufferCounterValue(visibleTrianglesBuffer, 0);
            cmdBuffer.SetBufferCounterValue(hwClusterIndicesBuffer, 0);

            // 清空Visibility Buffer
            int clearKernel = softwareRasterizer.FindKernel("ClearVisibilityBuffer");
            cmdBuffer.SetComputeIntParam(softwareRasterizer, "_RasterScreenWidth", camera.pixelWidth);
            cmdBuffer.SetComputeIntParam(softwareRasterizer, "_RasterScreenHeight", camera.pixelHeight);
            cmdBuffer.SetComputeBufferParam(softwareRasterizer, clearKernel, "_DepthBuffer", depthBuffer);
            cmdBuffer.SetComputeBufferParam(softwareRasterizer, clearKernel, "_PayloadBuffer", payloadBuffer);
            cmdBuffer.DispatchCompute(softwareRasterizer, clearKernel, Mathf.CeilToInt(camera.pixelWidth / 8f), Mathf.CeilToInt(camera.pixelHeight / 8f), 1);


            // 3. 执行GPU视锥体剔除和BVH遍历
            int traverseKernel = cullingShader.FindKernel("TraverseBVH");
            cmdBuffer.SetComputeMatrixParam(cullingShader, "_MatrixVP", camera.projectionMatrix * camera.worldToCameraMatrix);
            cmdBuffer.SetComputeVectorParam(cullingShader, "_CameraPos", camera.transform.position);
            cmdBuffer.SetComputeFloatParam(cullingShader, "_ScreenResolutionY", camera.pixelHeight);
            cmdBuffer.SetComputeFloatParam(cullingShader, "_FOV", camera.fieldOfView * Mathf.Deg2Rad);

            cmdBuffer.SetComputeBufferParam(cullingShader, traverseKernel, "_BVHNodes", bvhBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, traverseKernel, "_ClusterGroups", clusterGroupBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, traverseKernel, "_VisibleClusters", visibleClustersBuffer);
            
            cmdBuffer.DispatchCompute(cullingShader, traverseKernel, 1, 1, 1);

            // --- 拷贝可见Cluster数量并构建间接调度参数 ---
            cmdBuffer.CopyCounterValue(visibleClustersBuffer, indirectCullArgs, 0); // 暂存到 indirectCullArgs 开头
            cmdBuffer.CopyCounterValue(visibleClustersBuffer, visibleClustersCountBuffer, 0); // 暂存一个只读备份

            int buildArgsCull = buildIndirectArgsShader.FindKernel("BuildIndirectArgsCull");
            cmdBuffer.SetComputeBufferParam(buildIndirectArgsShader, buildArgsCull, "_VisibleClustersCount", visibleClustersCountBuffer);
            cmdBuffer.SetComputeBufferParam(buildIndirectArgsShader, buildArgsCull, "_IndirectCullArgs", indirectCullArgs);
            cmdBuffer.DispatchCompute(buildIndirectArgsShader, buildArgsCull, 1, 1, 1);

            // 4. 执行簇级剔除与HZB遮挡剔除
            int clusterCullKernel = cullingShader.FindKernel("CullClusters");
            if (hzbMips != null && hzbMips.Length > 0)
            {
                cmdBuffer.SetComputeTextureParam(cullingShader, clusterCullKernel, "_HZBTexture", hzbMips[0]);
                cmdBuffer.SetComputeVectorParam(cullingShader, "_HZBSize", new Vector2(hzbMips[0].width, hzbMips[0].height));
            }
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_VisibleClustersRead", visibleClustersBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_VisibleClustersCount", visibleClustersCountBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_Clusters", clusterBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_Vertices", vertexBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_Indices", indexBuffer);
            cmdBuffer.SetComputeMatrixParam(cullingShader, "_ObjectToWorld", objectToWorldMatrix);

            // Setup Skinning Data
            cmdBuffer.SetComputeIntParam(cullingShader, "_IsSkinned", isSkinned ? 1 : 0);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_BoneWeights", isSkinned ? boneWeightBuffer : dummySkinWeightBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_BoneMatrices", isSkinned ? boneMatrixBuffer : dummyBoneMatrixBuffer);

            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_VisibleTrianglesSW", visibleTrianglesBuffer);
            cmdBuffer.SetComputeBufferParam(cullingShader, clusterCullKernel, "_HWIndicesBuffer", hwClusterIndicesBuffer);
            cmdBuffer.DispatchCompute(cullingShader, clusterCullKernel, indirectCullArgs, 0); // 间接调度

            // --- 拷贝软硬件渲染数据数量并构建间接调度参数 ---
            cmdBuffer.CopyCounterValue(visibleTrianglesBuffer, indirectRasterArgs, 0);
            cmdBuffer.CopyCounterValue(visibleTrianglesBuffer, visibleTrianglesCountBuffer, 0); // 保存给 Compute Shader 越界检查
            cmdBuffer.CopyCounterValue(hwClusterIndicesBuffer, indirectDrawArgs, 0);
            int buildArgsRaster = buildIndirectArgsShader.FindKernel("BuildIndirectArgsRaster");
            int buildArgsDraw = buildIndirectArgsShader.FindKernel("BuildIndirectArgsDraw");
            cmdBuffer.SetComputeBufferParam(buildIndirectArgsShader, buildArgsRaster, "_VisibleTrianglesCount", indirectRasterArgs);
            cmdBuffer.SetComputeBufferParam(buildIndirectArgsShader, buildArgsRaster, "_IndirectRasterArgs", indirectRasterArgs);
            cmdBuffer.SetComputeBufferParam(buildIndirectArgsShader, buildArgsDraw, "_HWIndicesCount", indirectDrawArgs);
            cmdBuffer.SetComputeBufferParam(buildIndirectArgsShader, buildArgsDraw, "_IndirectDrawArgs", indirectDrawArgs);
            cmdBuffer.DispatchCompute(buildIndirectArgsShader, buildArgsRaster, 1, 1, 1);
            cmdBuffer.DispatchCompute(buildIndirectArgsShader, buildArgsDraw, 1, 1, 1);

            // 5. 执行软光栅化
            int rasterKernel = softwareRasterizer.FindKernel("SoftwareRasterize");
            cmdBuffer.SetComputeMatrixParam(softwareRasterizer, "_MatrixVP", camera.projectionMatrix * camera.worldToCameraMatrix);
            cmdBuffer.SetComputeIntParam(softwareRasterizer, "_RasterScreenWidth", camera.pixelWidth);
            cmdBuffer.SetComputeIntParam(softwareRasterizer, "_RasterScreenHeight", camera.pixelHeight);
            cmdBuffer.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_VisibleTrianglesCount", visibleTrianglesCountBuffer);
            cmdBuffer.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_VisibleTrianglesSW", visibleTrianglesBuffer);
            cmdBuffer.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_DepthBuffer", depthBuffer);
            cmdBuffer.SetComputeBufferParam(softwareRasterizer, rasterKernel, "_PayloadBuffer", payloadBuffer);
            cmdBuffer.DispatchCompute(softwareRasterizer, rasterKernel, indirectRasterArgs, 0);

            // 6. 执行硬光栅化 (HW Rasterization)
            // cmdBuffer.DrawProceduralIndirect(Matrix4x4.identity, hwMaterial, 0, MeshTopology.Triangles, indirectDrawArgs, 0);

            // 7. 材质Pass写入 (如果是 URP Feature 驱动，直接写入目标，否则写入独立 RT)
            cmdBuffer.SetRenderTarget(colorTarget, depthTarget);
            if (!isURPFeatureDriven)
            {
                cmdBuffer.ClearRenderTarget(true, true, Color.clear);
            }
            
            materialPassMat.SetBuffer("_DepthBuffer", depthBuffer);
            materialPassMat.SetBuffer("_PayloadBuffer", payloadBuffer);
            materialPassMat.SetInt("_ScreenWidth", camera.pixelWidth);
            materialPassMat.SetInt("_ScreenHeight", camera.pixelHeight);
            
            // --- Debug 模式传参 ---
            materialPassMat.SetInt("_DebugMode", debugMode ? 1 : 0);
            materialPassMat.SetInt("_ShowLODWatermark", showLODWatermark ? 1 : 0);
            
            for(int i = 0; i < 8; i++)
            {
                colorArrayCache[i] = i < lodColors.Length ? (Vector4)lodColors[i] : Vector4.one;
            }
            materialPassMat.SetVectorArray("_LODColors", colorArrayCache);

            // URP 深度写入开关
            materialPassMat.SetInt("_IsURP", isURPFeatureDriven ? 1 : 0);

            cmdBuffer.DrawProcedural(Matrix4x4.identity, materialPassMat, 0, MeshTopology.Triangles, 3); // 全屏三角形
        }

        // =======================
        // URP / HDRP 渲染管线钩子
        // =======================
        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == mainCam && naniteOutput != null && compositeMat != null)
            {
                CommandBuffer blitCmd = CommandBufferPool.Get("Nanite Blit Overlay");
                // 将透明背景的 Nanite 输出覆盖叠加到当前的 SRP 相机目标上
                blitCmd.Blit(naniteOutput, BuiltinRenderTextureType.CameraTarget, compositeMat);
                context.ExecuteCommandBuffer(blitCmd);
                context.Submit();
                CommandBufferPool.Release(blitCmd);
            }
        }

        // =======================
        // Built-in 渲染管线钩子
        // =======================
        void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            if (GraphicsSettings.renderPipelineAsset == null && naniteOutput != null && compositeMat != null)
            {
                // 先把主摄像机原本拍到的场景画上去
                Graphics.Blit(src, dest);
                // 再把 Nanite 的输出层叠上去
                Graphics.Blit(naniteOutput, dest, compositeMat);
            }
            else
            {
                Graphics.Blit(src, dest);
            }
        }

        void OnDestroy()
        {
            if (mainCam != null && cmd != null && GraphicsSettings.renderPipelineAsset == null)
            {
                // Clean up Built-in command buffers if any were left
                mainCam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, cmd);
            }

            if (naniteOutput != null) naniteOutput.Release();

            bvhBuffer?.Release();
            clusterGroupBuffer?.Release();
            clusterBuffer?.Release();
            visibleClustersBuffer?.Release();
            visibleTrianglesBuffer?.Release();
            hwClusterIndicesBuffer?.Release();
            
            indirectCullArgs?.Release();
            indirectRasterArgs?.Release();
            indirectDrawArgs?.Release();

            visibleClustersCountBuffer?.Release();
            vertexBuffer?.Release();
            indexBuffer?.Release();
            
            if (depthBuffer != null) depthBuffer.Release();
            if (payloadBuffer != null) payloadBuffer.Release();
            
            if (hzbMips != null)
            {
                foreach (var rt in hzbMips)
                {
                    if (rt != null) rt.Release();
                }
            }

            dummySkinWeightBuffer?.Release();
            dummyBoneMatrixBuffer?.Release();
            boneWeightBuffer?.Release();
            boneMatrixBuffer?.Release();
        }
    }
}