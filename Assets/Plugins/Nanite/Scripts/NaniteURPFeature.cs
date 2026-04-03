using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace UnityNanite
{
    public class NaniteURPFeature : ScriptableRendererFeature
    {
        class NaniteRenderPass : ScriptableRenderPass
        {
            private RenderTargetIdentifier colorAttachment;
            private RenderTargetIdentifier depthAttachment;

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                // 获取当前 URP 相机的颜色和深度目标
                colorAttachment = renderingData.cameraData.renderer.cameraColorTarget;
                depthAttachment = renderingData.cameraData.renderer.cameraDepthTarget;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                Camera camera = renderingData.cameraData.camera;
                
                // 获取挂载在相机上的 Nanite 数据提供者
                NaniteRenderer naniteRenderer = camera.GetComponent<NaniteRenderer>();
                if (naniteRenderer == null || !naniteRenderer.IsReady()) return;

                CommandBuffer cmd = CommandBufferPool.Get("Nanite Compute & Draw");
                
                // 将核心逻辑交给 NaniteRenderer 执行，但把 URP 的渲染目标传给它
                naniteRenderer.ExecuteNanitePass(cmd, camera, colorAttachment, depthAttachment);
                
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        NaniteRenderPass m_ScriptablePass;

        public override void Create()
        {
            m_ScriptablePass = new NaniteRenderPass();
            // 关键：在绘制不透明物体之前插入 Nanite！
            // 这样它能写深度，且会被后续的后处理（Bloom/DOF）正确处理！
            m_ScriptablePass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // 仅在主相机或游戏视图相机中执行
            if (renderingData.cameraData.cameraType == CameraType.Game || renderingData.cameraData.cameraType == CameraType.SceneView)
            {
                renderer.EnqueuePass(m_ScriptablePass);
            }
        }
    }
}