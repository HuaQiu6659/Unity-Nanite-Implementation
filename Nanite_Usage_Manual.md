# Unity Nanite 虚拟几何体原型 - 使用手册 (终极全管线兼容版)

本指南将引导你如何在 Unity 中配置并运行这套纯 GPU 驱动的 Nanite 原型。本版本已实现 100% 兼容 **Built-in / URP / HDRP** 渲染管线，并加入了实验性的 **GPU 骨骼动画 (Skinning)** 支持。

## 🚀 快速开始（1分钟开箱即用）

### 第一步：配置渲染管线与摄像机
1. **兼容性确认**：无论你的工程是 Built-in 还是 URP，直接打开即可，无需配置 Render Feature。
2. 在场景中选中你的 **Main Camera**（或者任何你想用来观察的摄像机）。
3. 将 `Assets/Plugins/Nanite/Scripts/NaniteRenderer.cs` 脚本拖拽挂载到该摄像机上。
4. 找到 `NaniteRenderer` 组件的 **[Shaders]** 面板，依次从 Project 窗口将以下文件拖入对应的槽位：
   *   **Culling Shader**: 拖入 `Culling.compute`
   *   **Software Rasterizer**: 拖入 `SoftwareRasterizer.compute`
   *   **Material Pass Shader**: 拖入 `MaterialPass.shader` (注意：是直接拖入 Shader 文件本身，无需创建 Material 材质球)
   *   **Build Indirect Args Shader**: 拖入 `BuildIndirectArgs.compute`
5. *(可选 Debug)* 展开 **[Debug]** 菜单，勾选 `Debug Mode` 和 `Show LOD Watermark`，这能让你在运行时清晰地看到不同颜色的 Cluster 切块和 LOD 层级水印。

### 第二步：配置测试模型 (支持静态 & 骨骼动画)
1. 在场景中创建一个物体。它可以是：
   *   普通的静态模型（如自带的 Cube，或外部导入的几十万面高模），只要它带有 `MeshFilter`。
   *   **带骨骼动画的角色模型**（带有 `SkinnedMeshRenderer` 和 `Animator`）。
2. 找到 `Assets/Plugins/Nanite/Scripts/NaniteTester.cs` 脚本，将它挂载到该模型的**最顶层父物体**上。
3. 选中该模型，在 Inspector 中找到刚挂载的 `NaniteTester` 组件，将 **第一步中配置好 `NaniteRenderer` 的摄像机** 拖入 `Nanite Camera Renderer` 槽位中。

### 第三步：见证奇迹
点击 Unity 的 **Play** 按钮。

1. `NaniteTester` 会在后台自动提取网格数据（包括骨骼权重），并送入 C++ 的 `meshoptimizer` 进行 Cluster 聚类划分，最后构建出 BVH 树。
2. 原始的 Unity MeshRenderer 或 SkinnedMeshRenderer 会被自动隐藏。
3. 你的模型现在已经完全由 GPU Compute Shader 接管渲染！如果是骨骼角色，你将看到它随着动画一起在 Nanite 管线中正确变形。

---

## ⚙️ 核心架构说明 (供进阶开发者参考)

### 1. 跨管线兼容方案 (Feature 托管 vs Overlay)
本原型采用了智能的**双模运行机制**：
- **在 URP 环境下 (推荐)**：你可以在 Universal Renderer Data 中添加名为 `NaniteURPFeature` 的 Renderer Feature。系统会自动将 GPU 计算挂载到不透明物体渲染前 (`BeforeRenderingOpaques`)，并**直接将像素颜色和深度写入 URP 的主目标缓冲中**。这种方式下，Nanite 模型可以完美接受后续的 Bloom、DOF (景深)、SSAO 等所有后处理效果，并与常规场景物体发生正确的深度穿插。
- **在 Built-in / 无 Feature 环境下**：系统会降级到独立画布模式。在材质重建阶段，画面会被独立渲染到名为 `naniteOutput` 的专属 RenderTexture 上，最后通过 `Composite.shader` 叠加 (Overlay) 到屏幕。此模式下不享受后处理。

### 2. GPU 骨骼动画原理 (Experimental Skinning)
由于 Nanite 的剔除是基于静态 Cluster 包围盒的，传统的蒙皮会在 CPU 端带来巨大的负担。本原型在 `Culling.compute` 中实现了一套轻量级的 Compute Shader 线性混合蒙皮 (Linear Blend Skinning)。
CPU 每帧只需传递少量的骨骼矩阵 (`_BoneMatrices`)，GPU 在遍历顶点准备送入光栅化器之前，会实时根据顶点的 4 根骨骼权重 (`_BoneWeights`) 计算变形。
*(注意：此方案下 BVH 剔除仍基于初始包围盒，动作幅度过大可能导致错误的视锥体剔除，这是图形学界正在攻克的前沿难题。)*

### 3. 数据流转 API
如果你不想使用 `NaniteTester`，而是希望在自己的框架中动态加载模型，你可以直接调用 `NaniteRenderer` 的公共 API：
```csharp
// 基础静态模型加载
naniteRenderer.LoadModelData(vertices, indices, clusters, groups, bvhNodes);

// 骨骼蒙皮模型加载
naniteRenderer.LoadModelData(vertices, indices, clusters, groups, bvhNodes, skinWeights, bones, bindposes);
```