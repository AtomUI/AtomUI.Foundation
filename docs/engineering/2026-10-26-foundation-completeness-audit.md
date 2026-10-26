# Foundation 功能完整性审计

日期：2026-10-26。代码基线：`release/1.0`，`0041863`，版本 `1.0.1`。

Foundation 已实现模块描述、依赖图、生命周期主流程和模块目录生成。服务类型 API 提供中立描述，执行容器可由消费端提供；没有内建 DI 执行层是事实，但不能因此把该原有分层设计认定为意外占位。宿主异常路径、生成器真实构建接入和验证体系另有可复现缺陷。LuminEditor 所需的跨 owner 借用与退休是有真实消费需求的新增合同，应与现有缺陷分开评审。

对应方案：[Foundation 统一重构方案](../architecture/2026-10-26-foundation-refactor-proposal.md)。本次没有修改产品代码、现有测试、项目配置或发布配置。

本文件保存审计基线及观察值；正式目标已归入[架构总览](../architecture/overview.md)及其设计分册。审计记录不覆盖正式合同，也不因合同更新而变成新实现的验证证据。

## 1. 范围与验证基线

首轮审阅包括两个包的全部 C# 产品源码、两个测试项目、根构建配置、NuGet 打包、README 和发布工作流。后续依用户要求，补查 DataGrid Commercial、Charts、SyntaxViewer、CLI 的真实接入，以及 LuminEditor 的命令结果、借用和关闭合同。消费端结论来自源码、历史测试与已还原依赖清单，本轮没有执行这些产品的全量测试，也没有改动其源码。

已执行：

| 验证 | 结果 | 能证明的范围 |
| --- | --- | --- |
| `dotnet test AtomUI.Foundation.slnx --framework net10.0 --nologo --verbosity minimal` | 运行时 2/2；生成器 18/18，通过 | 现有测试通过；不能证明未覆盖的宿主、服务语义完整 |
| `dotnet build AtomUI.Foundation.slnx --configuration Release --nologo --verbosity minimal` | 0 warning，0 error | 运行时 `net10.0` / `net8.0` 均编译成功；不等于两个框架的运行验证 |
| 本地审计探针 | 13 个运行时场景、8 个生成器场景完成，含正向对照 | 确认下面列出的具体边界行为 |
| 真实 MSBuild 配置传递实验 | 设置两个 catalog 属性后仍生成默认目录名称 | 生成器配置的构建接入未完成 |
| Generator Release 打包与压缩包检查 | 打包成功；包内无 `build` / `buildTransitive` 属性桥接文件 | 当前 NuGet 包未补齐配置传递 |

环境为 macOS arm64，SDK `10.0.300`。本轮没有执行 trim / Native AOT 发布，也没有对 Windows、Linux、外部 NuGet 消费项目进行运行验证；这些属于重构验收范围。

## 2. 能力清单

| 能力 | 判定 | 说明 |
| --- | --- | --- |
| 模块标识、阶段标识、描述对象、目录快照 | 已实现基础语义 | 类型标识、不可变集合快照和基础参数校验确实存在 |
| 依赖图校验与拓扑排序 | 已实现，正式测试不足 | 重复 ID、缺失依赖、循环检测均有执行代码；正向探针验证排序和循环 |
| 显式启用模块及依赖闭包 | 已实现，文档偏差 | 默认不启用任何模块，README 却省略启用步骤 |
| 模块工厂、配置、初始化、扩展阶段 | 已实现主流程，异常边界有缺陷 | 并非空壳；取消、重叠调用、部分创建失败存在资源或状态问题 |
| 模块关闭与失败回滚 | 已实现部分语义 | 普通初始化异常会逆序关闭；取消和重复关闭语义不完整 |
| 现成实例服务登记与查找 | 已实现 | 当前能工作的服务使用路径；外部实例不会被自动 Dispose |
| 类型形式的 Singleton / Scoped / Transient | 已实现中立描述登记 | CLI 会导出给外部容器执行；默认 Getter 不激活类型，不应把这一点单独视为错误 |
| 服务作用域、自动释放、关闭后访问限制 | 无内建执行层；扩展须有显式合同 | 旧路径不应自动接管调用方实例；新受管服务路径另行设计与验收 |
| 宿主预检 | 对外类型未接入 | `ModuleHostValidationResult` 没有宿主调用入口 |
| 运行时结构化诊断 | 部分实现 | 诊断对象有字段，但循环路径未结构化填入，观察者异常可逃逸 |
| 模块目录生成 | 常规路径已实现，边界缺陷 | 标准类、依赖、模块工厂和诊断均存在；partial、类型形状、命名遮蔽有漏洞 |
| catalog MSBuild 配置 | 测试层实现，真实接入缺失 | 单测直接注入 AnalyzerConfig，真实构建没有属性桥接 |
| 序列化 ID 边界警告 | 部分实现 | 普通成员访问调用可识别；`using static` 调用绕过检测 |
| 构建、打包、本地发布脚本 | 已实现主流程 | 不是占位；消费端验证、CI 触发和目标框架覆盖不足 |

`ModuleBase` 的空虚方法是可选生命周期钩子；两个 AssemblyMarker 是标记类型。这些都不应被当成待填业务功能。当前没有承诺可选依赖、冲突规则、能力发现、热卸载或父子容器，不能因缺少这些功能就判为残缺。

## 3. 审计项与原设计边界

优先级：P1 为影响公开功能、资源清理或常规构建的缺陷；P2 为契约、诊断或工程保障缺口。F01/F12 包含须保留的旧行为与新增运行时的边界，不等同于已确认实现错误。编号用于与重构阶段、验收用例对应。

### F01 契约边界：中立服务描述不等于内建容器

证据：[ModuleServiceCollection.cs](../../src/AtomUI.Foundation/Modularity/ModuleServiceCollection.cs)，第 32–50、75–88、97–105 行；[ModuleServiceRegistry.cs](../../src/AtomUI.Foundation/Modularity/ModuleServiceRegistry.cs)。

`AddSingleton<TService,TImplementation>`、`AddScoped`、`AddTransient` 追加描述；`GetRequiredService` 只查现成实例字典。`Freeze` 冻结登记，不建立 provider，也不校验服务构造图。这是执行事实，不自动构成违反原设计。

探针 `R02`：登记 `AddScoped<IDisposable,MemoryStream>()`，冻结后 descriptor 数量为 1，获取服务仍抛出“未注册”。补查历史后发现，DataGrid 提取通用模块代码前的测试明确要求 `AddSingletonType_Records_Service_Descriptor_Without_Creating_Instance` 和 `AddTransient_And_AddScoped_Record_Neutral_Service_Lifetimes`；CLI 的 ModuleServiceDescriptorMapper 正在将这些类型与 lifetime 映射到 Microsoft DI。因此不应把 R02 改成默认自动解析的回归用例。

需要修复的是职责说明与 API 使用引导；若支持 Foundation-owned 服务，则新增显式运行时资格，不能通过改变旧 Freeze 的含义暗中引入第二个 provider。

### F02 P1 取消与部分创建失败的清理不完整

证据：[ModuleHost.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.cs)，第 146–201、251–284 行。

创建阶段先把实例放进局部 `created`，整个循环成功后才交给宿主；取消分支直接重抛。工厂返回 null 时，`ModuleEntry` 的异常也绕过局部清理分支。

| 探针 | 触发 | 实际行为 |
| --- | --- | --- |
| `R03` | A 工厂创建实例并取消令牌，B 尚未创建 | 抛取消；宿主持有 0 个实例；之后关闭也不调用 A 的 Shutdown |
| `R04` | A 在初始化钩子内取消 | 抛取消；A 未 Shutdown；宿主仍停在 ServicesFrozen |
| `R08` | A 创建成功，B 工厂返回 null | 初始化返回失败；A 未 Shutdown，宿主持有 0 个实例 |
| `R10` 对照 | A 初始化抛普通异常 | 返回失败；A Shutdown 1 次；宿主进入 Failed |

这说明回滚不是完全没有实现，而是不同失败来源没有走统一清理路径。

### F03 P1 生命周期操作没有互斥与统一状态约束

证据：[ModuleHost.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.cs)，第 33–107、251–275、382–395、626–630 行。

探针 `R06` 用两个 TaskCompletionSource 固定调用顺序：初始化钩子挂起 → 执行 Shutdown → 放行初始化。结果为 `Phase=Initialized`，模块已 Shutdown 1 次，`ResolveModules` 仍报告成功；内部 `_isShutdown` 又已经为 true。无需线程随机竞争即可复现。

根因是多个入口直接更新 `_phase`、`_isInitialized`、`_isShutdown`，没有共同的运行中操作和终态模型。

### F04 P1 关闭的重复调用与结果保存不完整

证据：[ModuleHost.cs](../../src/AtomUI.Foundation/Modularity/ModuleHost.cs)，第 382–435 行。

探针 `R05`：逆序关闭 B 后取消，重试关闭；B 最终 Shutdown 2 次，A 1 次。流程没有跳过已经完成关闭的 entry。

探针 `R11`：B 的 Shutdown 抛异常；第一次关闭返回失败，第二次却直接返回 Success。虽然不会再次执行 B，之前的清理失败被隐藏了。

### F05 P1 README 声明的生成器配置不进入真实构建

证据：[生成器读取配置](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 639–685 行；[Generator 项目](../../src/AtomUI.Foundation.Generator/AtomUI.Foundation.Generator.csproj)；[配置单测](../../tests/AtomUI.Foundation.Generator.Tests/ModuleCatalogGeneratorTests.cs)，第 56–110 行。

本次用真实构建设置 `AtomUIModularityCatalogNamespace=Audit.Custom` 和 `AtomUIModularityCatalogTypeName=AuditCatalog`。生成的 analyzer config 不包含这两个属性，输出仍为全局命名空间下的 `ModuleGeneratedCatalog`。Release NuGet 包内也没有桥接文件。

单测直接构造 AnalyzerConfig，绕过 MSBuild，所以通过不能证明配置可用于用户项目。Roslyn 的[属性传递规范](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md#consume-msbuild-properties-and-metadata)要求通过 `CompilerVisibleProperty` 暴露属性，并随包提供相应 props/targets。

### F06 P1 partial 模块会被重复发现

证据：[ModuleCatalogGenerator.cs](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 33–38、69–74、175–179 行。

探针 `G01`：`[Module] partial class M` 的另一个 partial 声明仅带 `[Obsolete]`。两个语法节点都读取到合并后的 ModuleAttribute，于是同一类型被算两次，误报 `ATOMUIMODGEN001`，不再生成目录。原始 C# 输入无编译错误。

### F07 P1 生成器未验证可生成的类型形状和名称

证据：[类型建模](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 94–107、534–541 行；[生成类型引用](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 417–428 行。

下列探针的原始 C# 输入均合法；生成器未报告针对性错误，却输出了无法编译的代码。

| 探针 | 输入 | 生成后的错误 |
| --- | --- | --- |
| `G02` | 开放泛型模块 `M<T>` | `CS0246`，生成目录内没有 T |
| `G03` | private 嵌套模块 | `CS0122`，类型不可访问 |
| `G04` | file-local 模块 | `CS0246`，生成文件看不到类型 |
| `G05` | 无参构造但有未满足的 required 成员 | `CS9035`，生成的 new 表达式不合法 |
| `G06` | 配置目录命名空间中存在同名遮蔽类型 | `CS0426`，模块类型引用缺少 `global::` |

这些类型不一定都应支持；最低完整性要求是支持的输入生成可编译代码，不支持的输入在用户声明处报告明确诊断。

### F08 P2 诊断管道与结果语义不统一

证据：[ModuleHost.CreateResult](../../src/AtomUI.Foundation/Modularity/ModuleHost.cs)，第 678–686 行；[ModuleDiagnostic](../../src/AtomUI.Foundation/Modularity/ModuleDiagnostic.cs)；三个 Result 类型。

`R07`：诊断接收器抛异常时，该异常可以逃逸出 InitializeAsync，遮盖原始模块诊断。`R12`：仅包含 Warning 的 ModuleHostResult 也被判为失败。后者是公开 API 的待明确契约，当前宿主自身主要产出 Error，不能据此声称已有业务被 Warning 阻断。

`R13`：循环能够检测，但 `DependencyPath` 为 null，路径只写在 Message。已有结构化字段与实际生产路径没有完全接上。

### F09 P2 宿主验证结果是未接入的对外类型

证据：[ModuleHostValidationResult.cs](../../src/AtomUI.Foundation/Modularity/ModuleHostValidationResult.cs)。全量搜索产品代码与测试，仅找到该类型自身定义，未找到 `ModuleHost.Validate` 等入口或消费者。

宿主内部已有注册与图校验，问题是公开的“验证结果”没有对应可调用的预检能力。预检不能被设计成在不执行模块配置的情况下就声称验证了全部服务。

### F10 P1 验证与 CI 不能覆盖公开能力

运行时仅有 AssemblyMarker 和 ModuleId 序列化两个测试，宿主、服务、依赖图都没有正式回归测试。生成器已有 18 个测试，但缺少真实 MSBuild/NuGet 配置消费、partial 和上述类型边界。

[发布工作流](../../.github/workflows/release-atomui-foundation.yml)只在 `release/1.0` 的该 workflow 文件变化时自动触发，另有手动入口；未配置普通源码 PR 的验证流程。现有测试项目只以 net10.0 运行，缺少 net8.0 运行、trim、Native AOT 和打包后执行验证。这里只评价仓库内配置，不推断 GitHub 分支保护或外部流水线状态。

### F11 P2 默认启用策略与 README 不一致

证据：[ModuleHostOptions.cs](../../src/AtomUI.Foundation/Modularity/ModuleHostOptions.cs)，第 7–10、28–34 行；[宿主筛选](../../src/AtomUI.Foundation/Modularity/ModuleHost.cs)，第 519–553 行；两个 README 的启动示例。

`R01`：提供一个注册后直接 `new ModuleHost(registrations)`，InitializeAsync 返回成功、阶段为 Initialized，但工厂调用次数与启用数量均为 0。显式启用策略本身可以成立；需要修正示例并明确空选择的含义，而不是未经评审直接改变默认行为。

### F12 契约边界：借用实例与受管服务须区分

证据：[ModuleServiceRegistry.cs](../../src/AtomUI.Foundation/Modularity/ModuleServiceRegistry.cs)；宿主 Shutdown 流程。

`R09`：Shutdown 完成后，仍可通过 host.Services 解析之前登记的实例。这不必然是错误：旧路径是调用方所有的实例，仍可合法存活；不能直接添加自动 Dispose 或关闭后强制失效来改变其合同。新增受管服务路径则必须关闭新借用准入、保活已发出的真实 lease，并明示服务最终释放边界，不能借此收紧所有旧调用。

### F13 P2 序列化 ID 警告受语法形式影响

证据：[ModuleCatalogGenerator.cs](../../src/AtomUI.Foundation.Generator/Modularity/ModuleCatalogGenerator.cs)，第 110–128 行。

`G07`：`using static AtomUI.Modularity.ModuleId` 后调用 `FromSerializedValue("legacy")` 不产生 `ATOMUIMODGEN010`；等价的 `ModuleId.FromSerializedValue` 已有警告测试。根因是先限定 MemberAccessExpression，而不是按调用目标符号识别。

## 4. 复现材料

本机探针位于忽略目录 `.artifacts/foundation-audit-2026-10-26/`，直接引用当前 checkout 已构建的程序集，没有复制仓库或建立隔离研发环境：

- `Program.cs`、`FoundationAudit.csproj`：21 个行为探针。
- `results.json`：观察值，包括输入编译错误、生成器诊断和输出编译错误。
- `generated/`：真实 MSBuild 配置实验生成的代码。
- `packages/`：本地检查用 Generator 包，未发布到任何包源。

运行探针前先运行第 1 节的 Debug 测试命令，以生成引用程序集，然后执行：

```bash
dotnet run --project .artifacts/foundation-audit-2026-10-26/FoundationAudit.csproj --no-launch-profile --verbosity quiet
```

配置实验的核心参数为：

```bash
dotnet build src/AtomUI.Foundation/AtomUI.Foundation.csproj --configuration Debug --no-incremental \
  -p:AtomUIModularityCatalogNamespace=Audit.Custom \
  -p:AtomUIModularityCatalogTypeName=AuditCatalog \
  -p:EmitCompilerGeneratedFiles=true \
  -p:CompilerGeneratedFilesOutputPath="$PWD/.artifacts/foundation-audit-2026-10-26/generated"
```

忽略目录不作为长期回归保证。方案批准后，应把对应最小场景纳入正式测试；本次未提前改动测试套件。

## 5. 消费端复核与结论修正

采用以下本地工作 checkout；对具有未提交修改的项目，以本次实际读取的文件为准。基础版本清单不把源码仓库 HEAD 当作已经运行过的包版本。Foundation 的 `v1.0.0..HEAD` 在 Modularity 运行时源码上无差异。

| 项目与本地来源 | 实际依赖与使用方式 | 为什么可用 |
| --- | --- | --- |
| `/Users/chinboy/Projects/dotnet/AtomUICharts`，`fa487b71` | 声明与 assets 为 Foundation 1.0.0；ChartCoreModule 显式登记 `new ChartPipelineBuilder()` | 使用实例路径；ChartModuleCoordinator 自己串行调度、处理取消、隔离诊断异常、等待真实工作并释放业务 owned 资源 |
| `/Users/chinboy/Projects/dotnet/AtomUISyntaxViewer`，`587ef48` | 声明与 assets 为 Foundation 1.0.0；产品源码未调用 AddScoped/AddTransient/AddSingleton | 使用模块宿主与目录，Engine/Language 通过自己的 typed factory 和 Activation 管理；Adapter 取消后主动 Shutdown；Session/缓存先释放、模块后关闭 |
| `/Users/chinboy/Projects/dotnet/AtomUIDataGridCommercial`，`60fafe3` | 当前声明与 assets 为 AtomUI.Base 1.0.0-alpha.1，无 Foundation 包 | 自有 DataGridModuleServiceTable 与 DataGridServiceScope；不能把同名业务 FoundationModule 当作 Foundation 包依赖 |
| `/Users/chinboy/Projects/dotnet/AtomUICli`，`f12ba6e` | Foundation 版本属性为 1.0.0；类型 Singleton/Transient 实际有消费者 | ModuleServiceDescriptorMapper 将描述交给 Microsoft DI，后者承担实际构造与释放 |
| `/Users/chinboy/Projects/dotnet/AtomUILuminEditor`，`05cce8a` | 中央包及 assets 为 Foundation 1.0.1 | Core-owned 命令结果与资源已有真实租约；Foundation-owned 服务分支仍明确未取得完整资格 |

关键源码：Charts 的 [ChartCoreModule](../../../AtomUICharts/src/AtomUI.Charts.Core/Modules/ChartCoreModule.cs) 第 13–16 行明确注释 Foundation 不自动构造；[Coordinator](../../../AtomUICharts/src/AtomUI.Charts.Core/Modules/ChartModuleCoordinator.cs) 第 45–51、276–352 行明确补偿取消路径；[生命周期测试](../../../AtomUICharts/tests/AtomUI.Charts.Core.Tests/Modules/FoundationLifecycleTests.cs) 覆盖这些适配行为。SyntaxViewer 的 [Adapter](../../../AtomUISyntaxViewer/src/AtomUI.SyntaxViewer.Core/Modules/GeneratedModuleCatalogAdapter.cs) 第 36–45、77–85 行和 [关闭协调器](../../../AtomUISyntaxViewer/src/AtomUI.SyntaxViewer.Core/Sessions/SyntaxShutdownCoordinator.cs) 第 23–30 行承担领域关闭。CLI 的 [映射器](../../../AtomUICli/src/AtomUI.Cli.Hosting/DependencyInjection/ModuleServiceDescriptorMapper.cs) 第 26–41 行与 [BuildHost](../../../AtomUICli/src/AtomUI.Cli.Hosting/AtomUICliApplicationBuilder.cs) 第 69–98 行形成真实的描述导出路径。

DataGrid 的迁移提交 `941b52f` 将仓库内 AtomUI.Modularity 替换成 AtomUI.Base，并移除了原通用模块测试项目。可用 `git -C /Users/chinboy/Projects/dotnet/AtomUIDataGridCommercial show '941b52f^:tests/AtomUI.Modularity.Tests/ModuleServiceCollectionTests.cs'` 复核旧合同。旧 Host/Graph/Services 测试提供了重建 Foundation 回归矩阵的行为线索；需要按当前包边界重建，不直接复制其他项目源码。当前 Foundation 只有两个运行时测试的结论仍成立，但不能据此声称消费端也没有生命周期测试。

源码来源核对：Foundation 根下的 `.referenceprojects/<项目名>` 和从当前目录逐级到根的 `ReferenceProjects/<项目名>` 均无上述项目；本次使用已存在的相邻实际工作仓库，未使用远程副本、未 clone，也未创建分支/worktree。

## 6. LuminEditor 新增运行时需求

外部评估的四项需求已与 [Foundation 模块化合同](../../../AtomUILuminEditor/docs/architecture/foundation-modularity.md) 第 172–197、226–254 行以及 [命令合同](../../../AtomUILuminEditor/docs/modules/core/command-keybinding-catalog.md) 第 160–190 行对照：真实服务借用、停止后排空屏障、逐 owner 清理、共享实例唯一 owner 都是具体消费需求。

代码也有对应边界：[EditorCommandResult](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Commands/EditorCommandResult.cs) 第 19–44 行保有结果读者；[模块桥](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Modules/EditorModuleBindings.cs) 的 Shutdown 仅提交共享退休；[CoreResourceScope](../../../AtomUILuminEditor/src/AtomUI.LuminEditor.Core/Resources/CoreResourceScope.cs) 是领域资源栈，明确不是 Foundation 服务作用域。因此这些需求不能靠修改 Core 的裸引用包装来冒充已完成，也不能从其他产品可用推断该分支已具备。

这是新增通用受管生命周期合同的依据，不是已复现“当前 provider 提前释放”的证明；当前 Foundation 尚无该服务 provider 执行路径。逐条评估及验收见[生命周期扩展评估](../architecture/2026-10-26-foundation-lifetime-extension-assessment.md)。
