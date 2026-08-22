# HTML → WPF 迁移阶段

## 已迁移到原生 WPF

- 顶部菜单、命令栏、状态栏
- 暗色 / 亮色主题
- 文件菜单中的“新建项目”与五业务模板选择器
- Dockview 布局替换为 AvalonDock
- UI 工具、画布、属性、图层、Project 五个面板
- 图层文件夹归组、同级排序、嵌套与父级状态继承
- 项目 Schema v2、备份恢复、自动保存、导入导出
- Unity 式独立项目目录、项目内 UI repository、Project Assets 边界和最近项目恢复
- 五个常用样式 Prefab

## 仓库外保留但不再运行于桌面主线

- `prototype-art-workbench` HTML / CSS 视觉参照
- Dockview 配置
- DOM 事件与页面路由
- localStorage 持久化

原型目录不进入 GAssetForge Git 仓库，仅在本地仓库外保留以核对视觉、命名和交互。WPF 版本不得通过
WebView 嵌入原页面，也不得重新引入其 node_modules 或构建产物。

## 下一阶段

1. 增加“打开已有项目”、最近项目列表和项目版本迁移入口。
2. 增加图层多选、框选和更细的拖放插入指示。
3. 增加画布框选、缩放手柄、旋转、吸附、参考线和平移。
4. 增加 Prefab 创建、删除、Apply / Revert 和变体。
5. 在现有 Tilemap 原生网格上增加瓦片领域模型、笔刷、地图层级与引擎导出，并为卡牌、平行透视角物体建立各自的领域模型和原生面板。
6. 建立节点图执行、生成任务队列和结果资产仓库。
7. 接入 Photoshop MCP，保持 PSD 后处理与项目 JSON 解耦。
