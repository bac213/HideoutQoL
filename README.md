# HideoutQoL (`v0.1.0`)

面向 **SPT 4.1.6（EFT `0.16.9.40743`）** 的纯客户端 BepInEx 藏身处性能优化与体验增强插件（`net472`）。

## 主要功能

### 1. 藏身处性能与卡顿优化
- **O(1) 仓库物品桶索引**（`StashIndex` / `StashIndexPatch`）：拦截增删刷新、材料匹配、战利品箱排序与比特币显卡装卸，将全仓库线性扫描替换为 `TemplateId` 哈希分桶查询。
- **生产配方列表虚拟化与卡片池化**（`VirtualizedProduceList` / `ProductionFlowPatch`）：仅渲染视口内及小缓冲区卡片，合并同帧 `UpdateViews` 与 `ItemChangedHandler` 刷新；子面板单次实例化与快速淡入；限制水/酒连续生产视图定时刷新频率。
- **原位更新需求项与图标微优化**（`RequirementViewPatch` / `HideoutUiOptimizationsPatch`）：需求材料变化时原地更新数量与边框，跳过 `DestroyImmediate + AddComponent`、伪控制器视图广播注册与重复 `<mspace>` 排版。
- **自动愿望单缓存与按需预加载**（`WishlistCachePatch` / `BundlePreloadPatch`）：缓存藏身处建造愿望单集合；将进入藏身处时的配方包预加载裁剪至仅进行中/已完成的产物，其余按需加载。

### 2. 无缝大厅藏身处背景与交互优化
- **无缝大厅 ↔ 藏身处切换**（`HideoutBackdropPatch`）：启动到主菜单后后台静音预加载藏身处并在大厅及兼容子界面保持藏身处俯视背景，实现主菜单与藏身处瞬时切换。
- **交互提速**（`HideoutInteractionPatch`）：移除切换设施时的 0.5 秒固定延迟；可配置发电机切换冷却（默认 `0` 秒）；支持防抖与按材料/工具名反查配方。

### 3. 全站制作工坊界面（`HideoutWorkshopScreen`）
- **原版风格全屏三栏制作面板**：点击已建造的多配方制作设施（工作台、医疗站、卫生间、营养部、情报中心等）自动进入全站工坊界面，可通过 `[设施详情/升级]` 随时切回原版侧边抽屉，或在顶部 `[工坊总览]` 跨设施查看。
- **多维度筛选与目录树**：左侧提供各工作站状态计数（待领取/进行中/可制作）与两级 Handbook 分类树；中间栏支持状态芯片、设施等级、隐藏未解锁、排序下拉框及延迟搜索。
- **右侧 3D 详情预览与一键操作**：集成交互式 `CameraImage` + `WeaponPreview`（拖拽旋转/滚轮缩放，自动降级 2D）、归一化材料清单（支持 `[反查]` 所需该材料的全部配方与 `[跳蚤]` 一键搜索购买，默认自动取消“仅限商人”过滤以直接显示玩家报价），以及 `[一键全领]`、`[开始制造]`、`[领完再造]`、`[购齐缺料]`、`[取消制造]`。

## 安装方法

1. 从 Release 获取 `HideoutQoL-v0.1.0-SPT4.1.6.zip`。
2. 解压后可双击运行 `一键安装.bat` 自动识别 SPT 目录安装，或将压缩包内的 `BepInEx` 文件夹直接覆盖到 `<SPT根目录>`（`HideoutQoL.Client.dll` 位于 `BepInEx/plugins/`）。
3. 启动游戏后可在 **F12 → HideoutQoL** 调整所有性能、界面、大厅背景与调试选项。

## 从源码编译

需要 `.NET SDK`（支持 `net472`）及指向本机 SPT 4.1.6 `Managed` / `BepInEx/core` 目录（默认配置于 [HideoutQoL.Client.csproj](HideoutQoL.Client/HideoutQoL.Client.csproj)）：

```powershell
dotnet build -c Release HideoutQoL.Client/HideoutQoL.Client.csproj
```

编译产物位于 `HideoutQoL.Client/bin/Release/HideoutQoL.Client.dll`。
