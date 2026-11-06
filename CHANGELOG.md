# Changelog

版本状态的唯一入口。正式行为合同见 [docs/architecture/overview.md](docs/architecture/overview.md)；每条实现状态只在取得对应版本的新验证证据后提升，证据以链接保存，不在此复制。

## Unreleased

- 2026-11-05 批准[架构优化重构方案](docs/architecture/2026-11-05-foundation-architecture-optimization-proposal.md)，正式分册已按其第 8 节修订；实现尚未开始。方案含下列行为变化，实施时在此标注版本：
  - Transient 服务改为请求 owner，最后一个 lease 归还后释放，不再等到所在 scope 关闭。
  - 生成器显示名推断不再剥除 `DataGrid` 前缀。
  - 实例重复接管检查从进程级改为 `LifetimeDomain` 级。
  - `CreateModules()` 标记 `[Obsolete]`；新增 `TryAcquireView`、`TryBorrow`、`PrepareTransitionAsync`。
- 2026-11-05 完成方案 P0：新增 O02、O05、O07、O09 与新发现 O16 的红色用例（`Category=P0Red`），新增并发回归骨架，引入以 1.0.3 为 Shipped 基线的 PublicAPI 分析器。当前公开 API 与 1.0.3 一致。
- 2026-11-05 完成方案 P1：启动、重配与最终关闭统一为同一组合变更事务与退休调度器；新增 `ModuleChangePreparation.StopCurrent()` 与 `CompleteCurrentAsync()`，最终关闭可退休当前代领域状态并归还其持有的模块 lease（审计 R01）；`LifetimeRetirement` 拆为内部 batch 与公开回执。Native AOT 证据待 SDK 更新后补跑。

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
