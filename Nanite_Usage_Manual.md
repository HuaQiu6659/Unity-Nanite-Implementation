# Unity Nanite 插件使用说明手册

欢迎使用 Unity Nanite 原型实现插件！该系统在 Unity 内模拟了 UE5 的 Virtualized Geometry System（虚拟几何体系统），通过离线网格分簇、BVH 构建、GPU 遮挡剔除以及软硬光栅化管线实现了极高面数模型的高效渲染。

## 1. 目录结构
插件所有的核心文件位于 `Assets/Plugins/Nanite/` 目录下：
- **`Scripts/`**：包含 C# 端的离线切分算法（NaniteBuilder）、BVH构建算法（NaniteBVH）以及挂载在相机上的管线调度器（NaniteRenderer）。
- **`Shaders/`**：包含 HZB 生成、剔除与间接调度计算、软光栅化像素填充以及最终的屏幕空间延迟重建 Shader。
- **`Plugins/`**：包含第三方库 `meshoptimizer` 的预编译动态链接库（Windows 的 `.dll` 和 Android 的 `.so`），以及 iOS 的编译脚本。

## 2. 环境要求
- **Unity 版本**：推荐 Unity 2021.3+ 或更高版本。
- **图形 API**：必须支持 Shader Model 6.0 (DX12, Vulkan) 和 **64位原子操作 (64-bit Atomics)** 以确保软光栅化无锁并发的正确性。
- **渲染管线**：目前材质重建阶段 `MaterialPass.shader` 的写入口设计为 Built-in 管线的 Deferred Shading（延迟渲染）G-Buffer0 缓冲。如需用于 URP/HDRP，需要修改管线注入点 (`CameraEvent.BeforeGBuffer` -> `RenderPassEvent`)。

## 3. 快速开始步骤

### 第一步：分配 Shader 与材质
1. 在 Project 窗口中，找到 `Assets/Plugins/Nanite/Shaders/MaterialPass.shader`，右键点击并选择 **Create -> Material**，命名为 `NaniteMaterial`。
2. 确保你的项目中包含了 `Culling.compute`, `SoftwareRasterizer.compute`, `BuildIndirectArgs.compute`, `HZBGenerate.compute` 这四个计算着色器。

### 第二步：配置渲染摄像机
1. 选中你场景中的 **Main Camera**。
2. 将 `NaniteRenderer.cs` 脚本拖拽挂载到 Main Camera 上。
3. 在 Inspector 面板中，依次将刚才的四个 Compute Shader 拖入到对应的槽位中。
4. 将第一步创建的 `NaniteMaterial` 拖入 `Material Pass Shader` 对应的材质/Shader 槽位。

### 第三步：Debug 模式的使用
在 `NaniteRenderer` 的 Inspector 面板中，有一个 **[Debug]** 折叠菜单：
- **`Debug Mode` (开启渲染模式切换)**：勾选此项后，模型表面不再渲染真实的材质，而是会根据每个 Cluster 所属的 **LOD 层级 (MipLevel)** 渲染出不同的纯色，且每个独立的 Cluster 会带有微小的明暗变化以区分边界（类似 UE5 的 Nanite Overview）。
- **`Lod Colors`**：一个包含 8 种颜色的数组，用户可以自定义第 0 到 7 级 LOD 显示的颜色。
- **`Show LOD Watermark`**：勾选此项后，将在 Debug 色块表面铺满呈现当前 Cluster 对应 LOD 层级数字的水印。

### 第四步：离线生成（代码层API调用）
目前提供的是 API 级别的调用接口。在你的模型导入脚本中，你可以这样处理高模数据：
```csharp
// 1. 获取高模的 vertices 和 indices
// 2. 调用 NaniteBuilder.BuildNanite
NaniteSubMesh naniteData = NaniteBuilder.BuildNanite(vertices, normals, indices);

// 3. 构建 BVH
List<BVHNode> bvhNodes = NaniteBVHBuilder.BuildBVH(naniteData.clusterGroupList);

// 4. 将生成的数据推送到 NaniteRenderer 的 ComputeBuffer 中供 GPU 消费
```
*(注：完整的自动转换编辑器工具和流式磁盘序列化模块在原型中尚未包含，需使用者根据项目自行编写持久化存储。)*