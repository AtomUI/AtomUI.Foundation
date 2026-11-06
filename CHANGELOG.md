# Changelog

版本状态的唯一入口。正式行为合同见 [docs/architecture/overview.md](docs/architecture/overview.md)；每条实现状态只在取得对应版本的新验证证据后提升，证据以链接保存，不在此复制。

## Unreleased

- 暂无。

## 1.0.4（本地源 AtomUILocal，未公开发布）

- 2026-11-05 已由 `scripts/PublishToLocalSources.ps1` 从提交 `da90e01` 打包并推送到本地源 `AtomUILocal`。包 SHA-256 前缀：`AtomUI.Foundation.1.0.4` 为 `0b9dedfb608a92a8`，`AtomUI.Foundation.Generator.1.0.4` 为 `547e86a36327f25f`。从该源以独立包缓存还原后，net8.0（.NET 8.0.24）与 net10.0（.NET 10.0.8）消费程序运行通过，生成目录、Transient 请求 owner 与 `TryAcquireView` 均按预期工作。
- 2026-11-05 发布准备。包含 1.0.4-local.1 的全部审计修复与下列优化。公开 API 新增 `ModuleChangePreparation.StopCurrent()`、`CompleteCurrentAsync()`、`ModuleHost.TryAcquireView`、`ModuleServiceScope.TryBorrow`、`ModuleDiagnosticCodes.ModuleChangeRejected`、`ModuleServiceDiagnosticCodes.SingletonCapturesTransient`，已并入 `PublicAPI.Shipped.txt`；无公开 API 删除。
- 本机验证：Release 双目标构建 0 警告；运行时 224 个、生成器 52 个用例通过；真实消费者的生成器四种配置、net8/net10 JIT 与 net10 trim 通过。
- 尚待取得的资格：Native AOT 消费者在本机被 NuGet 漏洞审计拦截（`Microsoft.DotNet.ILCompiler` 10.0.8，GHSA-p3jp-9v83-76w9）；.NET 8 SDK 装载用例只在 CI 运行；原 LuminEditor 1000 轮联合场景未重跑。

- 2026-11-05 批准[架构优化重构方案](docs/architecture/2026-11-05-foundation-architecture-optimization-proposal.md)，正式分册已按其第 8 节修订，P0–P5 在本版本实施。行为变化：
  - Transient 服务改为请求 owner，最后一个 lease 归还后释放，不再等到所在 scope 关闭。
  - 生成器显示名推断不再剥除 `DataGrid` 前缀。
  - 实例重复接管检查从进程级改为 `LifetimeDomain` 级。
  - `CreateModules()` 标记 `[Obsolete]`；新增 `TryAcquireView`、`TryBorrow`。批准稿中的 `PrepareTransitionAsync` 在 P3 实施时撤销。
- 2026-11-05 完成方案 P0：新增 O02、O05、O07、O09 与新发现 O16 的红色用例（`Category=P0Red`），新增并发回归骨架，引入以 1.0.3 为 Shipped 基线的 PublicAPI 分析器。当前公开 API 与 1.0.3 一致。
- 2026-11-05 完成方案 P1：启动、重配与最终关闭统一为同一组合变更事务与退休调度器；新增 `ModuleChangePreparation.StopCurrent()` 与 `CompleteCurrentAsync()`，最终关闭可退休当前代领域状态并归还其持有的模块 lease（审计 R01）；`LifetimeRetirement` 拆为内部 batch 与公开回执。Native AOT 证据待 SDK 更新后补跑。
- 2026-11-05 完成方案 P2：Transient 改为请求 owner，最后一个 lease 归还后释放；Singleton 捕获 Transient 报告 Info 诊断 `ATOMUISVC004`；修复 O16，已准入工厂在 scope 同时退休时不再被误判失败；服务层退休索引合并为单一 ledger。
- 2026-11-05 完成方案 P3：服务激活准入与 owner 预留原子化，服务层不再同步等待在途工厂；宿主与生命周期域共用异步上下文回调帧做重入检测；所有权表与 owner 编号改为 domain 级；推进循环索引化。行为变化：两个独立 `LifetimeDomain` 可分别接管同一实例；owner 编号只在 domain 内唯一。
- 2026-11-05 完成方案 P4：生成器增量模型值相等化，无关编辑不再重新生成目录；生成器改为针对 Roslyn 4.8.0 编译，可被 .NET 8 SDK 装载；显示名推断不再剥除 `DataGrid` 前缀。
- 2026-11-05 完成方案 P5：新增 `ModuleHost.TryAcquireView`、`ModuleServiceScope.TryBorrow` 与 `ModuleDiagnosticCodes.ModuleChangeRejected`；变更拒绝保留候选阶段的结构化诊断；`CreateModules()` 标记弃用；运行时开启 AOT 与裁剪分析器；发布工作流增加消费者验证、属性桥资产检查与发布前验证门禁。

## 1.0.4-local.1（本地，未公开发布）

- 2026-11-05 在 2026-11-04 架构审计修复源码上设定版本并发布到本地 NuGet 源；Release 双目标构建 0 警告，运行时 200/200、生成器 48/48。记录：[本地发布](docs/engineering/evidence/local-release-20261105-1.0.4-local.1.json)、[修复证据](docs/engineering/evidence/architecture-remediation-20261104.json)。
- 修复范围：恢复失败隔离、准备对象 getter 回调边界、失败保留闭包收缩、已满足 WaitForRelease 关系解除、成功候选票据裁剪、失败模块退休粒度、生成器 extern alias 身份与依赖输入诊断。详见[2026-11-04 架构审计](docs/engineering/2026-11-04-foundation-architecture-audit.md)。
- 源码未提交，公开 1.0.3 不包含上述修复。

## 1.0.3

- 源码 `0d0a361`。新增同一 Host 上的[模块动态重配事务](docs/architecture/module-reconfiguration.md)与领域联合发布；修正已发布领域状态持有模块 lease 时的退休等待循环。
- 2026-11-03 本地批次：运行时 170/170、生成器 37/37；独立包消费者 net8/net10 JIT、裁剪与 macOS arm64 Native AOT 实际运行。记录：[本地前置记录](docs/engineering/evidence/module-reconfiguration-20261103-local-1.0.3.json)。真实 Core 联合接入未完成。

## 1.0.2

- 源码 `a0fbe11`。新增显式受管服务运行时：typed factory、唯一 owner、真实 lease、scope 关联、退休屏障与失败保留；Host 协调、异步创建、预检、取消、回滚与关闭结果；生成器 partial/类型形状校验、名称引用、诊断与 ProjectReference/PackageReference 配置桥。
- Release 运行时套件 102/102，生成器 37/37；net8/net10 包消费、trim 与 macOS arm64 Native AOT 本机验证通过。

## 1.0.1

- 源码 `0041863`。公开基线：模块身份、描述、Required 图、显式启用、中立服务描述与 instance 查找、模块目录生成器常规路径。不包含受管运行时与动态重配。
