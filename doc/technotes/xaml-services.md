# XAML 服务与统一 Designer 路线图

本文记录 OpenDevelop 的 XAML 语言服务，以及 WinForms、WPF、WinUI 三类 designer
的现状、目标架构和实施顺序。UnoDevelop 的 XAML Designer 是 WinUI 路线的重要参考实现，
但它不是 OpenDevelop 当前已经拥有的功能。

## 最终目标

OpenDevelop 应根据项目和文件所属 UI framework，为同一个编辑器工作流选择正确的 designer：

| 项目类型 | 设计文件 | Designer backend | 目标能力 |
|---|---|---|---|
| WinForms | `.cs`/`.vb` + `.Designer.*` + `.resx` | 现有 `FormsDesigner` | 加载、选择、属性编辑、Toolbox、代码 round-trip |
| WPF | `.xaml` | 现有 `WpfDesign` | XAML DOM、设计面、选择/Adorner、属性、Toolbox、源码同步 |
| WinUI 3 / Uno Platform | `.xaml` | 新 WinUI/Uno backend | 实时预览、选择、属性、Toolbox、源码同步；按 project profile 处理 dialect/runtime 差异 |

这里的“同时支持”是三套 framework backend 共享 IDE 级契约和用户体验，不是把三种
对象模型强行合并成一套控件层次。`System.Windows.Forms.Control`、
`System.Windows.DependencyObject` 和 `Microsoft.UI.Xaml.DependencyObject` 必须保持隔离。

## 当前基线

### OpenDevelop

| 组件 | 位置 | 当前状态 |
|---|---|---|
| WPF Designer | `src/AddIns/DisplayBindings/WpfDesign/` 与 `externals/vscode-wpf/external/WpfDesigner/` | 已进入主 solution，使用 `LibreWPF.Sdk`；是 WPF 的正式 backend |
| WinForms Designer | `src/AddIns/DisplayBindings/FormsDesigner/` | C# backend 已改为无 CodeDOM 的 Roslyn `BasicDesignerLoader`；`.Designer.cs` round-trip、旧格式迁移、共享 Toolbox Pad 和真实拖放测试均已完成；VB Roslyn backend 尚未完成 |
| XAML language server | `externals/vscode-wpf/` | `.xaml` 的 WPF language server 已接入；framework 判定不能只靠扩展名 |
| WinUI Designer | 尚无 OpenDevelop addin | 待实现；不得把 UnoDevelop 的完成状态记作 OpenDevelop 的完成状态 |

### WinForms round-trip 与 Toolbox 的实际状态

此前所谓“尚未恢复”来自 `FormsDesigner.csproj` 中已经过时的排除注释，而不是当前实现。
实际链路如下：

- `CSharpBinding.FormsDesigner.RoslynFormsDesignerSecondaryDisplayBinding` 用 Roslyn 判断 C#
  partial class 是否可设计；
- `RoslynDesignerLoader` 读取主文件与 `.Designer.cs`，把 `InitializeComponent` 的受支持子集
  转成 CodeDOM object graph，保存时重写方法和新增字段；
- `FormsDesignerViewContent.ToolsContent` 暴露共享 `WpfToolbox`；后者显示 WinForms 分类，
  通过真实 `System.Drawing.Design.IToolboxService` 和 WPF/WinForms drag bridge 创建控件；
- `DragToolboxItem_OntoWinFormsDesignSurface_AddsControlToForm` 端到端验证拖放、可见尺寸、
  保存进 `.Designer.cs` 及 tool selection reset。

本次审计补上了 FormsDesigner DevFlow action 的启动预加载，避免 AddIn 惰性加载晚于 DevFlow
一次性 action discovery 而造成测试 404。

旧的实现曾是“Roslyn parser + CodeDOM serializer bridge”；该实现已经被替换。OpenDevelop 的
目标比微软 17.5 的“Roslyn code generator”更进一步：活动的 WinForms backend 不再把 CodeDOM
当作中间模型。新的 `RoslynFormsDesignerLoader` 直接派生
`BasicDesignerLoader`，而不是 `CodeDomDesignerLoader`；读取端把 project `Document` 的 syntax /
semantic model 投影为组件图，保存端从组件图生成 C# syntax tree。旧 CodeDOM generator
产生的 `this.`、全限定类型和显式 delegate 必须作为兼容输入被接受，但第一次 designer 保存
就迁移为 Roslyn 风格；不会为了兼容旧文件而回退到 CodeDOM serialization。

实现需要使用 project `Document`/`Workspace`、compilation、Simplifier、Formatter 和
AnalyzerConfigOptions，并只替换带 annotation 的字段与 `InitializeComponent`。资源读写也要从
`ProjectResourcesComponentCodeDomSerializer` / `ProjectResourcesMemberCodeDomSerializer` 中
提炼成了与语法树无关的 `RoslynDesignerResourceModel`，由 Roslyn backend 处理
`ComponentResourceManager.ApplyResources`。仍待完成的是接入完整 project Workspace /
`.editorconfig`、VB backend，以及微软新版 generator 的异步/并行、`nameof`、高 DPI 工作。

核心 backend 不把 `System.CodeDom` 作为文档模型；集成测试同时断言运行时 loader 不是
`CodeDomDesignerLoader`。旧 loader 不作为运行时 fallback。出于第三方 WinForms 控件生态兼容，
显式声明的自定义 `CodeDomSerializer` 允许在 `LegacyCodeDomSerializerAdapter` 边界内运行；其
短生命周期输出立即转换成 Roslyn statements 并丢弃，最终仍由 Roslyn formatter / project
document 写回。无法转换的返回形状会阻止保存并报告 serializer/control 类型，不能静默丢属性。

### UnoDevelop 可复用的经验

UnoDevelop 的 `src/AddIns/DisplayBindings/XamlDesigner/` 已实现原生
`Microsoft.UI.Xaml` 的 Source/Design secondary view、`XamlReader` 预览、Toolbox provider、
Outline provider、Properties Pad 联动及集成测试。它证明了 XAML Studio 的交互和算法可以
在 WinUI/Uno 上重建，也给出了 IDE 契约的参考形状。

但 UnoDevelop 的 UI 文件不能直接链接进 OpenDevelop：前者的控件类型是
`Microsoft.UI.Xaml.*`，后者的 shell 和文档视图是 `System.Windows.*`。应优先提取无 UI
依赖的模型、XAML 文本变换和测试 fixture，再分别保留 WinUI 与 WPF host adapter。

## 外部参考与依赖

```text
externals/
├── XAMLStudio/       UWP/Windows.UI.Xaml 参考实现；只参考交互、算法和资源
├── vscode-wpf/
│   ├── external/WpfDesigner/   OpenDevelop 的 WPF designer engine
│   └── external/wxsg/          XAML language services 与 framework profiles
└── AXSG（由 wxsg 间接包含）    XAML 分析/生成基础
```

XAML Studio 使用 `Windows.UI.Xaml`，不能直接编译进使用 `Microsoft.UI.Xaml` 的 WinUI 3
backend，也不能直接编译进 WPF shell。可移植的是设计思路和 UI-free 代码，而不是命名空间
替换后的整批源码。

## ProGPU / WinUI：先验证，再决定 host

ProGPU 已让 LibreWPF/LibreWinForms 在非 Windows 环境具备可运行的渲染基础，但“ProGPU
具备 WinUI 相关支持”并不自动证明以下能力已经成立：

- OpenDevelop 的 WPF visual tree 能直接承载 `Microsoft.UI.Xaml.UIElement`；
- `Microsoft.UI.Xaml.Markup.XamlReader.Load` 能在 OpenDevelop 的目标平台和线程模型中工作；
- WinUI dispatcher、resource lookup、XamlRoot、输入、焦点和 DPI 能嵌入 WPF 文档 tab；
- 加载被设计项目的自定义控件时，依赖和 `x:Bind`/code-behind 能被安全隔离。

在选择架构前必须建立一个最小 spike，并记录所用 ProGPU 包/commit 和目标平台矩阵。验收项：

1. 在 OpenDevelop 进程中创建 WinUI `Application`/dispatcher，并用 `XamlReader.Load` 渲染
   仅含标准控件的页面。
2. 将该 visual 显示在 WPF 文档区域，验证 resize、输入、焦点、DPI 和主题资源。
3. 连续加载有效/无效 XAML，验证异常不会污染 IDE，且能恢复最后一次有效预览。
4. 卸载文档后检查线程、窗口、事件和 collectible load context 是否释放。
5. 至少在 Windows 与当前 ProGPU 支持的非 Windows 目标各运行一次。

根据结果二选一：

- **方案 A：进程内 host**。仅在 ProGPU 明确提供 WPF/WinUI interop host 且上述 spike 全部
  通过时采用。新增 WPF `WinUIDesignerViewContent` adapter，WinUI renderer 保持独立程序集。
- **方案 B：进程外 preview host**。若对象模型或 dispatcher 不能安全共存，启动一个小型
  WinUI/Uno preview process，通过 JSON-RPC 发送 XAML、项目上下文、viewport 和 selection，
  通过原生子窗口或捕获画面承载预览。此方案隔离性更好，也更适合加载用户程序集。

在 spike 完成前，文档不得把方案 A 标记为既定实现；方案 B 是必须保留的 fallback。

## 目标架构

```text
                         OpenDevelop Workbench
                                  │
              ┌───────────────────┼───────────────────┐
              │                   │                   │
       Designer registry     Shared Toolbox      Properties/Outline
       + project detector    and commands        host contracts
              │
       ┌──────┴──────┬───────────────┐
       │             │               │
 WinForms backend  WPF backend   WinUI backend
 FormsDesigner     WpfDesign     in-proc adapter or preview RPC
       │             │               │
 WinForms object   WPF XamlDom    WinUI XAML document model
 model/services    and designer   + isolated renderer
```

应从 UnoDevelop 已验证的 provider 模式中提炼并放到 shell/base 层的 framework-neutral 契约：

- `IDesignerProvider`：CanDesign、创建 secondary view、生命周期和保存；
- `IDesignerToolboxProvider`：分类、工具项和 framework-specific insertion payload；
- `IDesignerSelectionService`：当前 selection 与 selection change；
- `IDesignerPropertyAdapter`：把 backend 对象暴露给统一 Properties Pad；
- `IDesignerOutlineProvider`：元素/控件树及源码定位；
- `IDesignerDocumentSynchronizer`：文本版本、解析结果、selection 与 mutation 的双向同步。

契约中不能出现三种 UI framework 的具体类型；使用 opaque handle、descriptor 和文本 edit。
已有 `IToolboxProvider`、`IOutlineContentHost` 等契约能够满足需求时应扩展或适配它们，不再
平行创建同义接口。

## Framework 检测与路由

`.xaml` 同时可能是 WPF、WinUI 或 Uno，不能以扩展名决定 designer/LSP。路由顺序应是：

1. 读取 owning project 的 SDK、TFM、PackageReference 和 XAML item metadata；
2. 优先识别 Uno（Uno 项目也包含 `Microsoft.UI.Xaml`，否则会被误判为 WinUI）；
3. 识别 WinUI/Windows App SDK，再识别 WPF；
4. loose XAML 无项目上下文时让用户选择 profile，或只开 source、不给出错误 designer；
5. designer 与 language server 必须消费同一个 detection result，不能各自重新猜测。

建议统一结果为 `XamlFrameworkKind`（Wpf、WinUI、Uno、Unknown）和带证据的
`XamlFrameworkContext`。OpenDevelop 的新 backend 从第一阶段就同时承诺 WinUI 3 与 Uno
Platform 项目：二者共享 `Microsoft.UI.Xaml` object model、presentation namespace 和大量控件，
但必须保留独立 profile。Uno profile 的检测优先级高于 WinUI，并负责 Uno SDK、目标平台、
`Uno.WinUI` 版本和 Uno-specific resource/custom-control resolution；不能把 Uno 项目仅当作
普通 WinUI 项目误判后碰运气加载。

## WinUI backend 的分层

| 层 | 职责 | 可复用来源 |
|---|---|---|
| Document model | XML/XAML 节点、稳定 ID、source span、诊断、文本 edits | AXSG/wxsg 与 UnoDevelop 的 UI-free 逻辑 |
| Render protocol | Load/Update、viewport、theme、diagnostics、visual-tree snapshot、selection | 新建，兼容进程内和进程外实现 |
| Renderer | `Microsoft.UI.Xaml.Markup.XamlReader`、资源和控件实例化 | UnoDevelop `XamlDesigner` 与 XAML Studio 的经验 |
| OpenDevelop adapter | WPF secondary view、Toolbox/Outline/Properties wiring | OpenDevelop shell + provider contracts |
| Editing operations | insert、delete、move、resize、set property 转为 versioned text edits | 三个 backend 共享命令语义，各自生成 edit |

不要把运行时 visual tree 当作唯一文档模型。每次操作最终必须生成可撤销的源码 edit；重新
解析后再刷新预览。这样可以支持无效的中间文本、Undo/Redo、格式保留和进程外 renderer。

自定义控件、merged dictionaries、`x:Bind` 和 code-behind 不应进入首个 milestone。
首版只加载安全白名单内的标准控件和资源，并对不支持的节点显示诊断/placeholder。

## 分阶段实施

| Phase | 内容 | 完成标准 | 状态 |
|---|---|---|---|
| 0 | 修正文档并盘点三套 backend | 明确 OpenDevelop/UnoDevelop 边界和已知缺口 | done |
| 1 | ProGPU/WinUI host spike | 形成可重复 demo、平台矩阵和 A/B 架构决策 | todo |
| 2 | 统一 framework detection | designer 与 LSP 对同一项目得到相同 profile；含 WPF/WinUI/Uno/Unknown 测试 | todo |
| 3 | 提炼通用 provider/selection/sync 契约 | WPF 与 WinForms backend 通过 adapter 接入且无功能回退 | todo |
| 4 | WinUI/Uno 只读 MVP | 两种 profile 均支持 Source/Design、标准控件预览、诊断、刷新、最后有效预览、Outline | todo |
| 5 | WinUI/Uno 基础编辑 | 两种 profile 的 Toolbox 插入、选择、Properties 修改、删除、Undo/Redo 均落为源码 edit | todo |
| 6 | 三类 designer 补齐 | WinForms VB backend 与现代 Roslyn codegen；WPF/WinUI/Uno 基础体验一致 | todo |
| 7 | 高级 WinUI/Uno | 两种 profile 的项目资源、自定义控件、隔离加载；评估 `x:Bind`/code-behind | backlog |

Phase 3 不应阻塞 Phase 1 的 spike；但正式 WinUI addin 不应在没有 framework detection 和
document synchronization 契约的情况下直接复制 UnoDevelop view。

## 测试矩阵

每个 backend 至少覆盖：

- 正确项目打开正确 designer，错误 framework 不抢占；
- 有效、无效及由无效恢复为有效的文档；
- Source/Design 切换和同一文档的未保存修改；
- Toolbox、selection、Properties、Outline 和源码位置联动；
- Undo/Redo、保存、关闭、重开及资源释放；
- 不含 designer 的普通 `.cs`/`.xml` 文件不出现残留 provider；
- host 崩溃或 renderer 超时不导致 OpenDevelop 退出（进程外方案）；
- Windows 与 ProGPU 声称支持的非 Windows 平台分别有 smoke test。

WinUI 集成测试可复用 UnoDevelop fixture 的意图，但测试必须运行 OpenDevelop 自己的 app 和
backend，不能以 UnoDevelop 测试通过代替 OpenDevelop 验收。

## 完成定义

“OpenDevelop 支持 WinForms/WPF/WinUI designer”只有在以下条件同时满足时才能宣告：

- 三类真实项目能被稳定识别并打开正确设计面；
- 每类至少具备预览、选择、Properties、Toolbox 插入、源码同步和 Undo/Redo；
- unsupported XAML 给出诊断而不是使 IDE 崩溃；
- framework-specific 类型没有泄漏进通用 shell 契约；
- 自动化测试覆盖路由、编辑 round-trip、生命周期和目标平台；
- 文档记录实际使用的 ProGPU/LibreWPF/WinUI 版本以及仍不支持的功能。
