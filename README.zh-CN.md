# AtomUI.Foundation

[![License: LGPL-3.0](https://img.shields.io/badge/license-LGPL--3.0-white?labelColor=black&style=flat-square)](LICENSE)

文档语言：[English](README.md) | [简体中文](README.zh-CN.md)

## 介绍

AtomUI.Foundation 是 AtomUI 生态的基础设施仓库，提供可被其它 AtomUI 包复用的运行时基础能力和源码生成能力，
不绑定到某一个具体控件库。

第一个版本聚焦 Modularity：

- `AtomUI.Foundation` 提供模块标识、模块描述、依赖图解析、生命周期宿主和模块服务注册等运行时基础类型。
- `AtomUI.Foundation.Generator` 提供用于模块目录生成的 Roslyn 源码生成器和诊断规则。

## 包

| 包名 | 描述 |
|---|---|
| `AtomUI.Foundation` | AtomUI 核心运行时基础设施。 |
| `AtomUI.Foundation.Generator` | AtomUI.Foundation 的 Roslyn 源生成器。 |

当前版本：

```bash
dotnet add package AtomUI.Foundation --version 1.0.2
```

如果项目需要生成模块目录，请把 generator 作为 analyzer 引入：

```xml
<PackageReference Include="AtomUI.Foundation.Generator" Version="1.0.2" PrivateAssets="all" />
```

## Modularity

使用 `ModuleAttribute` 标记模块类，并继承 `ModuleBase`：

```csharp
using AtomUI.Modularity;

[Module(DisplayName = "Core")]
public sealed partial class CoreModule : ModuleBase
{
}

[Module(DisplayName = "Selection", Dependencies = [typeof(CoreModule)])]
public sealed partial class SelectionModule : ModuleBase
{
}
```

模块目录生成器会生成内部类型 `ModuleGeneratedCatalog`，并提供 `CreateRegistrations()` 方法：

```csharp
using AtomUI.Modularity;

var registrations = ModuleGeneratedCatalog.CreateRegistrations();
var options = new ModuleHostOptionsBuilder().Enable<SelectionModule>().Build();
var host = new ModuleHost(registrations, options);
var result = await host.InitializeAsync();
```

注册不等于启用。启用 `SelectionModule` 会同时选择其 Required 依赖 `CoreModule`；空启用根集合不会运行任何模块。

1.0.2 Generator NuGet 包会自动传递自定义目录配置。仓库外使用源码 `ProjectReference` 时，仍需导入共享 props 并向编译器暴露这些配置属性：

```xml
<PropertyGroup>
    <AtomUIModularityCatalogNamespace>MyApp.Generated</AtomUIModularityCatalogNamespace>
    <AtomUIModularityCatalogTypeName>MyModuleCatalog</AtomUIModularityCatalogTypeName>
</PropertyGroup>
<ItemGroup>
    <CompilerVisibleProperty Include="AtomUIModularityCatalogNamespace" />
    <CompilerVisibleProperty Include="AtomUIModularityCatalogTypeName" />
</ItemGroup>
```

## 正式设计

[架构总览](docs/architecture/overview.md) 是正式入口，包含[模块宿主](docs/architecture/modularity.md)、[服务登记](docs/architecture/service-registration.md)、[受管生命周期](docs/architecture/managed-lifetimes.md)和[源码生成](docs/architecture/source-generation.md)等设计分册。

1.0.2 保留中立描述和调用方实例所有权，并新增可显式启用的受管服务运行时，提供 typed factory、owner-backed lease、scope 共享和协调退休。通过 `ModuleServiceRuntime` 启用受管能力；原有 descriptor 登记不会激活容器或转移所有权。已验证范围和仍待完成的跨平台/产品资格见[验证合同](docs/engineering/verification.md)。

## 本地开发

构建完整解决方案：

```bash
dotnet build AtomUI.Foundation.slnx
```

运行运行时测试：

```bash
dotnet test tests/AtomUI.Foundation.Tests/AtomUI.Foundation.Tests.csproj --framework net10.0
```

运行 generator 测试：

```bash
dotnet test tests/AtomUI.Foundation.Generator.Tests/AtomUI.Foundation.Generator.Tests.csproj --framework net10.0
```

发布包到本地 NuGet 源：

```powershell
pwsh -File scripts/PublishToLocalSources.ps1
pwsh -File scripts/PublishToLocalSources.ps1 -localSourcesDir /path/to/nuget.local -buildType Release
```

## 仓库结构

| 路径 | 用途 |
|---|---|
| `src/AtomUI.Foundation` | 运行时包源码。 |
| `src/AtomUI.Foundation.Generator` | Roslyn generator 包源码。 |
| `tests/AtomUI.Foundation.Tests` | 运行时测试。 |
| `tests/AtomUI.Foundation.Generator.Tests` | Generator 测试。 |
| `build/` | 集中的 MSBuild 版本、包元数据和输出路径配置。 |
| `docs/` | 架构和工程文档。 |
| `scripts/` | 本地开发和发布脚本。 |

## 授权

AtomUI.Foundation 采用与 AtomUI 相同的授权：GNU Lesser General Public License v3.0。详见 [LICENSE](LICENSE)。
