# XAML 服务 — 架构 & 移植计划

## 依赖

```
externals/
├── XAMLStudio/     → https://github.com/dotnet/XAMLStudio
│   UWP WinUI 可视化设计器 (参考实现, 不能直接链接)
│
├── AXSG/           → https://github.com/wieslawsoltes/XamlToCSharpGenerator
│   XAML LSP + 代码生成器
│   └── XamlToCSharpGenerator.LanguageServer/ ← LSP 服务器
```

## 已实现

| 组件 | 状态 |
|---|---|
| **XamlBinding addin** | `src/AddIns/BackendBindings/XamlBinding/` — 文件类型 + LanguageBinding |
| **AXSG LSP 注册** | `LspServerRegistry.CreateDefault()` 注册 `.xaml` → AXSG LanguageServer |
| **LSP 基础** | `LspCodeCompletionBinding` + `LspLanguageService` + `LspServiceManager` |
| **XamlDesigner addin** | `src/AddIns/DisplayBindings/XamlDesigner/` — Source/Design secondary view + `XamlReader` preview |
| **XamlDesigner 集成测试** | 有效/无效 XAML、可视树 snapshot、Source/Design 切换、非 XAML 隔离 |

## 教训: XAML Studio 代码不能直接链接

XAML Studio 是 **UWP 应用** — 使用 `Windows.UI.Xaml` (UWP API)。
UnoDevelop 用 **WinUI Desktop** — `net10.0-desktop` + `Uno.WinUI` + `Microsoft.UI.Xaml`。

虽然 Uno Platform 提供 `Windows.UI.Xaml` 的 API 兼容层，但这个命名空间:

- 只在 **XAML 编译** 时可用 (通过 SDK 的类型映射)
- **不作为直接的程序集引用** 暴露给普通 C# 文件
- `net10.0-desktop` 上 `Uno.WinUI` 包提供的命名空间是 `Microsoft.UI.Xaml`

**结论**: 不能直接 link XAML Studio 的 `.cs` 文件。必须用 `Microsoft.UI.Xaml` 重新实现。

## 新的设计方案

```
┌───────────────────────────────────────────────────────────┐
│  UnoDevelop IDE (net10.0-desktop)                         │
│                                                           │
│  ├── XamlBinding.addin (done)                             │
│  │   ファイル类型注册 + LanguageBinding                   │
│  │                                                         │
│  ├── AXSG LSP (done)                                      │
│  │   代码补全 + 语义                                      │
│  │                                                         │
│  └── XamlDesigner addin (基础设计预览已完成)               │
│       ├── DesignSurface — Source/Design secondary view     │
│       ├── XamlRenderService — XamlReader 实时渲染          │
│       ├── PropertyPanel — 复用 UnoDevelop Properties Pad  │
│       └── Toolbox Pad / selection-resize adorner          │
└───────────────────────────────────────────────────────────┘
```

## 设计器 addin 移植方案

| 组件 | XAML Studio 源码 | UnoDevelop 方法 |
|---|---|---|
| **DesignSurface** | `Views/Document.xaml` + `.cs` | 参考 UI + 逻辑, 用 `Microsoft.UI.Xaml` 重写 |
| **Adorners** | `Controls/ModifySelectorAdorner.cs` | 参考算法, 重新实现 |
| **PropertyPanel** | `Views/Properties.xaml` + `PropertiesViewModel.cs` | 用 UnoPropertyGrid 替代 |
| **Toolbox** | `Views/Toolbox.xaml` + `ToolboxViewModel.cs` | 参考 UI, 重新实现 |
| **XamlRenderService** | `Toolkit/Services/XamlRenderService/` | 用 `Microsoft.UI.Xaml.Markup.XamlReader` 渲染 |
| **XamlAutocomplete** | `Toolkit/Services/XamlAutocompleteService/` | 用 AXSG LSP 替代 |
| **Json (Newtonsoft)** | 多处 | 全部用 `System.Text.Json` |
| **CommunityToolkit** | `CommunityToolkit.Mvvm` | 可共用, 需 `Uno.WinUI` 版本 |

## 注意点

1. **UnoPropertyGrid 已存在**: `UnoPropertyGrid` NuGet 包已经集成到 UnoDevelop,
   可以直接用于 PropertyPanel, 不需要重新实现。

2. **XamlRenderService**: XAML Studio 的渲染服务使用 `XamlReader.Load()` 实时编译 XAML。
   Uno Platform 的 `Microsoft.UI.Xaml.Markup.XamlReader` 同样支持此功能。

3. **Adorner**: 参考 XAML Studio 的 `ModifySelectorAdorner` 算法 (选择、移动、调整大小),
   用 Uno 的 `Microsoft.UI.Xaml.Shapes` + `AdornerLayer` 实现。

4. **AXSG LSP**: 代码补全和语义分析已经通过 LSP 基础设施就绪,
   designer 不需要再集成 XAML Studio 的 `XamlAutocompleteService`。

## 后续计划

| Phase | 内容 | 状态 |
|---|---|---|
| 1 | XamlBinding addin | done |
| 2 | AXSG LSP | done |
| 3 | XamlDesigner addin — 基础预览、视图切换和集成测试 | done |
| 4 | Property Pad 集成、独立 Toolbox Pad、Source/Design 拖放、选择/缩放 Adorners | done |

Phase 4 的 Toolbox 使用与 SharpDevelop 相同的 provider 架构。唯一的通用 Toolbox Pad
由 Shell/Workbench 注册；XAML Designer AddIn 只通过 `IToolboxProvider` 提供工具内容。
同一 XAML 文档的 Code 和 Design 子视图共享该 provider；激活 `.cs` 等没有 provider 的
文档时，Pad 显示无可用工具状态，不会保留 XAML 控件。拖放统一携带 XAML snippet：
Source 视图在光标处插入代码；Design 视图用 `XamlReader` 创建控件并加入
当前选中的容器。Design 选择会激活现有 Properties Pad，并将所选
`FrameworkElement` 交给同一个 `UnoPropertyGrid`；四角 resize handles 直接更新该元素尺寸。

Shell 同时提供 SharpDevelop 风格的通用 Outline Pad。XAML Designer 通过
`IOutlineContentHost` 提供元素树，显示元素类型与 `Name`/`x:Name`，节点操作可跳转到
对应源码行。编辑过程中 XAML 暂时无效时保留最后一次成功解析的树；切换到非 XAML
文档时清空 provider。XML/XAML 使用 `XmlFoldingStrategy`，VB 使用 Roslyn syntax tree
生成块级 folding，其他花括号语言继续使用 `BraceFoldingStrategy`。

文档外层 tab 始终显示文件名；`Code | Design` 是文档内容区底部的内部切换器。Toolbox
按控件类别使用可折叠 `Expander` 分组显示。当前提供 43 个常用控件，分为 `Layout`、
`Controls`、`Input`、`Collections`、`Navigation`、`Media`、`Shapes` 七组，组内条目
均可拖放到 Code 或 Design。
