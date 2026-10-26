# 模块源码生成设计

`AtomUI.Foundation.Generator` 提供 Roslyn 增量模块目录生成器及诊断。已发布的 1.0.1 支持常规模块；1.0.2 实现本文的符号唯一性、类型形状校验、全局类型引用和实际 MSBuild 配置接入。本文是当前实现应满足的合同，跨程序集组合等未运行场景仍需按验证合同取得证据。

## 输入与输出

生成器读取原始源码的 ModuleAttribute、模块类型和 Dependencies。输出当前程序集内部的 `ModuleGeneratedCatalog`，以 CreateRegistrations 返回显式 ModuleRegistration、ModuleDescriptor、Required 引用和直接构造工厂。

类型身份和展示名称分开保存；内部匹配依据符号身份，不用展示字符串冒充全局唯一符号。输出中的模块类型使用全局限定名称并转义关键字，不能受生成目录命名空间中的同名类型遮蔽。

同一模块符号只发现一次。partial 的其他带特性声明不能因读取到合并后的 ModuleAttribute 而再次登记。输入顺序变化不改变稳定目录顺序或诊断含义。

## 可生成类型与错误定位

生成目录中的 new 表达式必须在实际上下文合法。至少验证：

- 类型实现 IModule，非抽象，具有目录可调用的无参构造。
- 类型及所有外层类型对生成文件可访问；可访问非泛型嵌套类可支持。
- 未闭合泛型及泛型外层不生成缺少类型参数的引用。
- file-local 类型不能被错误引用到另一生成文件。
- required 成员得到构造合同满足，否则在用户声明处报告不支持的形状。
- Required 目标是可从当前程序集引用的封闭模块类型，并具备必要声明；开放泛型、非模块或缺少模块元数据的目标必须在发射前报告诊断；本程序集图不存在环。

开放泛型依赖使用 `ATOMUIMODGEN011` 在依赖属性处报告，不生成无法编译的 `Required<T>()` 引用。

不支持的输入报告指向原声明/依赖的明确诊断，不输出注定无法编译的工厂。具有参数构造的模块仍可通过手写显式 ModuleRegistration 注册，不要求生成器反射构造。

错误检测同时验证输入源码本身、生成器诊断和更新后编译结果，不能用生成器零异常代替生成代码可编译。

## MSBuild 配置桥接

目录命名空间和类型名由 `AtomUIModularityCatalogNamespace`、`AtomUIModularityCatalogTypeName` 配置。生成器从 AnalyzerConfig 的对应 build_property 读取，非法值产生有位置或配置来源说明的诊断并按既有回退策略处理。

1.0.1 的消费项目需显式暴露属性；1.0.2 的 NuGet 包通过 `buildTransitive` 桥接这两个属性：

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

1.0.2 Generator 包提供统一 props/targets 桥接，仓库 ProjectReference 入口复用同一配置。仓库外的源码 ProjectReference 消费者需显式导入共享 props。包和项目引用必须同时验证实际 editorconfig、生成类型名及输出编译；只向单测字典注入两个属性不构成构建接入验收。

## 序列化边界诊断

ModuleId 和 ModulePhaseId 的 FromSerializedValue 警告依据实际目标方法符号识别，覆盖普通成员调用、类型别名及 using static。显式 ModuleSerializedIdBoundary 继续允许受控序列化/兼容代码；调用语法变化不能绕过同一规则。

## 跨程序集与生成器协作

目录保持程序集内部可见，各包可提供薄的公开静态导出门面，返回本包已有注册。应用合并导出后交给 Foundation Host 校验，不运行期扫描所有引用程序集。

生成器读取到外部模块类型的元数据，只能生成该依赖引用，不表示已取得外部模块注册或已启用它。跨包目录可用性必须通过真实双程序集消费者验证。

通用模块生成与领域命令/schema/选项生成各读原始声明，不依赖另一个生成器同轮输出被再次发现。受管服务工厂生成也不自动混入模块目录生成器；若需要，另行完成对应 API 与生成合同。

## 编译和打包边界

Generator 保持 netstandard2.0，仅作为 analyzer；运行时不装载其程序集执行服务。当前生成语法面向支持集合表达式等语法的 C# 12 或更高消费者，不能因 analyzer 目标为 netstandard2.0 就声称生成源支持旧 C#。

源生成不自动证明无反射、trim 安全或 Native AOT 可运行。包加载、属性桥接、生成源编译与实际消费程序运行分别取得证据。工程细则见[生成器工程规则](../engineering/source-generator-guidelines.md)。
