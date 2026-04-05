# Unity Nanite 架构演进与底层图形学踩坑实录 (Knowledge Base)

这份文档总结了我们在尝试于 Unity 引擎中复刻类似 Unreal Engine 5 (UE5) Nanite 虚拟几何体渲染管线时，所经历的架构演变、跨平台探索以及那些“血淋淋”的底层图形学踩坑经验。这不仅是当前项目的技术沉淀，更是非常宝贵的 GPU 渲染编程指南。

---

## 1. 架构的终极演进 (Architecture Evolution)

### 1.1 从 C++ 动态链接 (DLL) 到 Pure C# (全平台制霸)
*   **初期的弯路**：我们最初使用了开源的 C++ 库 `meshoptimizer` 来执行网格的聚类分簇 (Clusterize)。这带来了一系列灾难性的跨平台兼容问题：
    *   `DllNotFoundException`：由于 Windows 系统的 MSVC / MinGW 运行时依赖缺失，导致不同电脑环境无法加载。
    *   `%1 不是有效的 Win32 应用程序`：64 位的 Unity 引擎试图调用 32 位的动态库时引发的架构冲突。
    *   **移动端噩梦**：若要支持 Android 和 iOS，需要配置复杂的 NDK 交叉编译 `.so` 和 Xcode 的 `.a` 静态库。
*   **最终解法**：**“砸碎 DLL，回归原生”**。我们在 `NaniteBuilder.cs` 中用纯 C# (Pure C#) 手写了一套基于顶点哈希的网格分簇算法。虽然牺牲了微小的预处理速度，但换来了 **100% 的跨平台能力 (Windows, Mac, Linux, Android, iOS, WebGL)**，实现了真正的开箱即用。

### 1.2 从 Built-in 叠加 (Overlay) 到原生 URP Renderer Feature
*   **降维兼容模式 (Built-in)**：早期版本利用 `RenderPipelineManager` 挂载事件，将 Nanite 画在一张名为 `naniteOutput` 的透明 RenderTexture 上，然后通过全屏 Blit 强行叠加到屏幕。这导致 Nanite 模型无法接受任何屏幕空间后处理 (Bloom, SSAO, DOF)。
*   **原生 URP 深度回写 (Native URP)**：通过编写 `NaniteURPFeature`，我们将 Nanite 的执行时机卡在了 `RenderPassEvent.BeforeRenderingOpaques`。在 `MaterialPass.shader` 中，我们解码了 GPU 软光栅化算出的深度值，并使用 `out float outDepth : SV_Depth` **强行写回 URP 的 Z-Buffer 中**。
    *   *质变*：Nanite 模型不仅能正确遮挡普通物体，还能完美融入 URP 的全套后处理管线。

---

## 2. 致命的 GPU 内存与 Compute Shader 踩坑录

如果你在写 Compute Shader 时发现“屏幕上什么都没有”，或者“顶点飞到了外太空”，请务必对照以下几条：

### 2.1 结构体步长 (Stride) 的内存错位惨案
在向 GPU 申请显存 (`new ComputeBuffer`) 时，C# 端的字节数必须与 HLSL 中的 `struct` 大小**绝对、严丝合缝地对齐**。
*   **案发回放**：在给 `Cluster` 和 `VisibleTriangle` 添加 `materialID` 和 `mipLevel` 字段后，结构体大小从 32 字节增加到了 36 字节，44 字节变成了 52 字节。但我忘记修改 C# 中硬编码的 Stride 数值。
*   **灾难后果**：GPU 在读取第二个三角形时，读到的是上一个三角形的内存尾巴，整个显存被错位的垃圾数据填满，导致所有顶点坐标变成了极其庞大或极小的乱码（`NaN` 或 `Infinity`），画面瞬间消失。

### 2.2 矩阵的“空间折叠” (Column-Major vs Row-Major)
*   **案发回放**：在 Culling Shader 的骨骼蒙皮 (`SkinVertex`) 中，我理所当然地写了 `mul(m, float4(pos, 1.0))`。
*   **灾难后果**：Unity C# 传给 `StructuredBuffer` 的 `Matrix4x4` 在显存里是**按列存储的 (Column-Major)**。但是 HLSL 在强转读取时，默认是**按行读取 (Row-Major)**。这导致整个骨骼矩阵被**转置 (Transposed)**了！顶点乘上转置后的矩阵，直接飞出了视锥体，消失在几万公里外。
*   **修复秘籍**：在 HLSL 中处理从 StructuredBuffer 读来的 Unity 矩阵时，**必须利用矢量的右乘特性来隐式转置**，即改为：`mul(float4(pos, 1.0), m)`。

### 2.3 32位与64位的指针雷区 (size_t)
*   **案发回放**：当还在使用 C++ DLL 时，C++ 接口定义的是 `size_t`。在 64 位系统下这是个 64 位整数。而我在 C# 的 P/Invoke 中使用了 `int` (32位)。
*   **灾难后果**：向 C++ 传递几百万面的数组大小时，高 32 位的内存会被垃圾数据填充，导致 C++ 访问越界，Unity 编辑器直接 `Access Violation (0xC0000005)` 闪退崩溃。
*   **修复秘籍**：与 C++ 的 `size_t` 通信，C# 端必须使用 `UIntPtr`。

---

## 3. 算法与逻辑层面的隐蔽 Bug

### 3.1 根节点误差判定的“无限死锁”
*   **Bug 表现**：不管怎么运行，所有模型都被剔除。
*   **原因**：Nanite 是基于屏幕误差 (`Screen Error`) 驱动的。在构建虚拟几何体的 BVH 树时，如果不小心将根节点的 `maxParentLODError` 设为了 `float.MaxValue`，在 GPU 剔除计算时，算出的屏幕误差就会是 `Infinity`。
*   **结果**：由于判断条件是 `Infinity < 1.0 像素`，这永远为 `False`。于是 GPU 认为连根节点都不需要画，直接将整个模型无情剔除。

### 3.2 局部缩放 (Local Scale) 导致的边界框错位
*   **Bug 表现**：模型一旦作为子物体且带有缩放（比如 0.01），就会消失或位置错乱。
*   **原因**：传递给 GPU 的 `_ObjectToWorld` 矩阵如果是父物体的矩阵，而模型顶点依然是巨大的原始坐标。在计算视锥体剔除和距离时，算出的包围盒与实际世界空间严重脱节。
*   **修复**：必须精确追踪 `targetRenderer.transform.localToWorldMatrix`，并在 Shader 中提取矩阵的缩放向量 (`length(_ObjectToWorld[0].xyz)`)，动态修正 `radius` 边界。

### 3.3 URP 提早执行导致的时序死锁 (NullReferenceException)
*   **Bug 表现**：URP 开启后，瞬间报出一堆 `Compute Shader Property is not set`。
*   **原因**：URP Renderer Feature 的执行生命周期比挂载在物体上的 `MonoBehaviour.Start()` 还要早！这导致 Feature 尝试派发 Compute Shader 时，C# 还没来得及 `new ComputeBuffer`。
*   **修复**：将 `InitBuffers()` 的调用从 `Start` 转移到 `ExecuteNanitePass` 的前置防御性检查中，实现**惰性/按需初始化 (Lazy Initialization)**。

---

## 5. 核心参考资料与开源库致谢 (References)

在本项目从零到一的构建、试错与重构过程中，以下资料和开源项目提供了不可或缺的理论支撑与底层技术实现：

*   **[MeshOptimizer (by zeux)](https://github.com/zeux/meshoptimizer)**
    *   **作用**：本项目最核心的 C++ 底层基石。我们利用其 `meshopt_buildMeshlets` 和 `meshopt_simplify` 接口，实现了极其高效的网格分簇（Clusterize）与边塌陷简化。
    *   **踩坑价值**：它的引入让我们经历了完整的 C++/C# 混合编程、P/Invoke 指针转换 (`size_t` 到 `UIntPtr`) 以及跨平台编译（Zig 交叉编译纯静态库）的“大洗礼”。
*   **[《在 Unity 中实现简易版 Nanite》 (知乎 @W_101)](https://zhuanlan.zhihu.com/p/653609802)**
    *   **作用**：本原型最核心的架构灵感来源。其提供的 C# 封装思路、Compute Shader 软件光栅化（Barycentric Coordinates）、以及原子操作深度测试（`InterlockedMax`）的代码骨架，为我们指明了渲染管线的方向。
    *   **踩坑价值**：在复刻其代码时，我们发现了许多潜在的 Bug（如 ComputeBuffer 步长未动态计算、根节点 LOD 误差无穷大死锁等），并在此基础上大幅优化，将其从仅支持 Built-in 强行叠加，升级为了完美融入 URP 并支持后处理的 Native Feature。

---

## 4. 总结与展望

这套原型系统成功验证了在 Unity 中实现 **软件光栅化 (Software Rasterization) + 硬件深度回写 (Depth Write-back) + 纯 GPU 驱动渲染管线 (GPU-Driven Rendering Pipeline)** 的可行性。

尽管我们中途经历了 DLL 的跨平台地狱、显存步长错位、矩阵转置翻车等无数个让画面瞬间消失的黑洞，但每一次的爬坑都让我们对 GPU 显存布局、Unity 渲染底层机制有了极为深刻的肌肉记忆。

**后续优化方向**：
1. 实现完整的 BVH (Bounding Volume Hierarchy) 多层级简化算法，彻底激活 LOD 层级的微边形切换。
2. 将 Material Pass 从简单的 ID 上色，升级为支持 G-Buffer 写入的 PBR 延迟渲染 (Deferred Shading) 兼容模式。

> *“图形学编程就是这样，哪怕你算对了 99% 的逻辑，只要有 1 个字节的显存错位，或者 1 个浮点数变成了 NaN，你得到的永远只是一块黑色的屏幕。”*