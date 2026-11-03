# Module Reconfiguration Implementation Plan

> **For agentic workers:** Use test-driven-development for each task and a fresh final review. User requested implementation in the current checkout; do not create branches/worktrees or commit/publish without a separate request.

**Goal:** Implement transactional module changes on one initialized Host, preserving unaffected instances and publishing Foundation/domain state together.

**Architecture:** One composition snapshot owns registrations, enabled roots, module entries, neutral services, managed bindings and domain state. Candidate preparation uses the same graph/lifecycle algorithms against private composition state. Recoverable admission suspension and irreversible batch retirement remain separate operations in the existing lifetime authority.

**Tech Stack:** C#, net8.0/net10.0 runtime, xUnit v3, static factories; no new runtime dependency.

**Spec:** [Module reconfiguration](../../architecture/module-reconfiguration.md).

## Global Constraints

- Stay on the current branch and checkout. Existing architecture edits belong to this task and must be preserved.
- Do not unfreeze current registrations, reset Host phase, create a second Host/provider, or rebuild unaffected instances.
- Keep neutral instance ownership and legacy APIs compatible; new APIs are documented before implementation.
- No callbacks or I/O in the publication critical section. One combined snapshot is the publication authority.
- Retire only after commit; precommit cancellation restores admission. Real readers and failed resource retention remain observable.
- Foundation implementation/qualification only; do not modify the sibling editor or claim its complete product qualification.

## Review Focus

- A factory reserved before suspension must remain accounted for through late handoff.
- Old and new registrations for one service type must never alias the wrong cached owner.
- Failed candidate cleanup must retain its captures without closing a shared current service.
- Commit/shutdown/cancellation races must have one result and preserve all cleanup tickets.
- Module leases, result leases and long captures must not create a precommit global-drain deadlock.

## Task 1: Recoverable lifetime admission and batch retirement

Files: `src/AtomUI.Foundation/Lifetimes/`, `tests/AtomUI.Foundation.Tests/Lifetimes/LifetimeSuspensionTests.cs`.

Produces: `LifetimeDomain.Suspend(IEnumerable<LifetimeOwner>) -> LifetimeSuspension`; `LifetimeSuspension.Owners`, `Stop()`, `CompleteRetirement() -> LifetimeRetirement`, `Dispose()` resumes only this suspension. Incoming capture closure is frozen atomically; Stop distributes all notifications with release disabled, Complete opens batch release. Existing owner states and domain Stop remain unchanged.

- [x] Write and run failing tests for recoverable admission, captured consumer closure, full notification barrier, late factory, overlapping suspension, cross-domain rejection and failed-candidate shared-service retention.
- [x] Implement at the domain admission/retirement authority, not in caller wrappers.
- [x] Run focused suspension tests and existing lifetime tests; record nonzero counts.

## Task 2: Service binding identity and isolated candidates

Files: `src/AtomUI.Foundation/Modularity/Services/`, `tests/AtomUI.Foundation.Tests/Services/ModuleServiceBindingTests.cs`.

Produces: `ModuleServiceBindings` with typed factory registration, `Freeze()`, `Root`, `BindScope(ModuleServiceScope)`; `ModuleServiceRuntime.Bindings`, `CreateCandidate(ModuleServiceBindings, IEnumerable<ModuleId>)`, and a transition using Task 1. Unchanged ServiceRegistration objects and per-scope caches are shared by identity. Runtime remains the single lifetime/ownership authority. Candidate abort retires only newly owned bindings. Transition rejects a retained binding whose owner would be retired through a real capture.

- [x] Test isolated registration and activation, unchanged singleton/scoped identity, explicit replacement with old reader, unaffected service availability, real capture impact, candidate cleanup failure, concurrent activation and full shutdown.
- [x] Refactor binding lookup/cache ownership; keep current Root access attached to the Host snapshot rather than a second current pointer.
- [x] Run focused binding and legacy runtime tests.

## Task 3: One Host composition and public change protocol

Files: `Modularity/ModuleHost*`, `ModuleComposition.cs`, `ModuleChange*.cs`, `ModuleServiceCollection.cs`, `ModuleServiceConfigurationContext.cs`; `ModuleHostChangeTests.cs`.

Produces: `ModuleHost.Current`, `AcquireView(IEnumerable<ModuleId>? modules = null)`, `ChangeAsync(ModuleChangeRequest, CancellationToken)`; immutable `ModuleHostView`, disposable `ModuleHostLease`, `ModuleChangeStatus/Result/Retirement`, request with baseline/target registrations/options/disabled/replaced IDs/cascade/participant. `IModuleChangeParticipant.ConfigureAsync` collects candidate declarations and `PrepareAsync` returns `ModuleChangePreparation` with sealed State and suspend/resume/abort/previous-retirement callbacks. `IModuleHostReconfigurationLifetime.ServiceRuntime` exposes the existing managed runtime through custom lifetime adapters.

- [x] Test the original A→A/B scenario, D=0, no-change, graph rejection, Required-disable/cascade and explicit replacement before implementing the path.
- [x] Refactor lifecycle execution to accept a composition; preserve initial API ordering, token identity and rollback.
- [x] Attribute neutral service operations to their module and fork the retained ledger; never replay unaffected module configuration.
- [x] Add candidate-only cleanup, one combined publication, retained view leases and closing of every pending retired composition.
- [x] Run host change and existing Host/neutral-service tests.

## Task 4: Joint decision, failure and race acceptance

Files: dedicated host change tests and public consumer fixture.

- [x] Add barrier tests for domain rejection, coherent reads, precommit cancellation/reopen, stale/foreign baseline, commit-vs-close, slow old reader, release failure and real A-captures-B Stop order.
- [x] Verify each new test red before implementing any missing behavior; preserve the original trigger and real owner/factory facts.
- [x] Add a package-only consumer executing create/request/change/retire/shutdown, without reflection JSON or internal access.

## Task 5: Qualification and review

- [x] Run runtime and generator suites once relevant source stabilizes; run Release build for both library TFMs and `git diff --check`.
- [x] Pack local candidates and run the independent consumer on net8/net10, applicable trim and local Native AOT; retain package hashes and commands in one evidence record.
- [x] Fresh review of the final diff against the spec; fix consequential findings with regression tests.
- [x] Update canonical implementation status only to match executed evidence; leave Core/product and other-platform qualifications pending.

## Execution record

- 2026-11-02: Architecture updates retained; baseline source a0fbe11; previous unchanged runtime suite 102/102. User requested source-level optimization, not trigger avoidance.
- Ruling: Execute the authorized implementation without another plan-approval prompt, following the user's no-redundant-confirmation rule. No branch/worktree/commit is authorized.
- Pre-flight: Task 2 consumes Task 1's suspension API; Task 3 consumes binding candidates and transitions. Keep ownership boundaries explicit; do not dispatch simultaneous edits of the same files.

- Tasks 1–5: complete for the authorized Foundation scope. Current evidence: [authoritative batch](../../engineering/evidence/module-reconfiguration-20261102.json); runtime 168/168, generator 37/37, Release net8/net10 build with zero warnings/errors, six actual package consumer executions (JIT/trim/AOT × net8/net10) on macOS arm64.
- Final review R1/R2: reproduced before fixes; full prior-owner retirement accounting and one Required-aware shutdown scheduler now pass the original regressions. Candidate-domain abort also progresses jointly with its private service retirement.
- Ownership: failed module/candidate resources remain reachable; successful retired factory payloads and superseded same-contributor neutral values are collectible. Surviving shadowed contributions restore without module replay.
- Native qualification: net10 Homebrew SDK requires existing OpenSSL/Brotli search paths via per-process LIBRARY_PATH. Linker deployment/deprecation warnings are retained in the evidence; only this macOS26.6.2 host is qualified, not earlier macOS versions. No SDK or system dependency was installed/modified.
- No new branches/worktrees, commits, NuGet publication, sibling Core edits or cross-platform/product qualification. The independently compiled Foundation package consumer is not the Core joint consumer.

- 2026-11-03 user review: prior completion claim corrected. Published old domain state returning B's ModuleHostLease from CompletePreviousAsync reproduced a circular wait; the prior batch omitted this case.
- Repair: one shared domain/module/service drain scheduler for previous retirement and candidate abort, full Stop barrier first, service release after module callbacks. Original probe unchanged before/after; two regression cases RED timeout → GREEN; full runtime 170/170 and generator 37/37.
- Local 1.0.3: both exact packages inspected, package-only consumers include this domain/module lease branch across six JIT/trim/AOT paths, and both packages pushed to configured AtomUILocal. Current authoritative evidence: [local 1.0.3 batch](../../engineering/evidence/module-reconfiguration-20261103-local-1.0.3.json).
- Completion boundary: local repair/release only. Public NuGet, release commit/tag/push and actual LuminEditor Core joint qualification remain unperformed; downstream GEN_TEST_001 and related blockers are not closed.

- 2026-11-03 release authorization: user requested “发布 1.0.3”, then “发布后删除本地的 1.0.3 版本”. This authorizes the release commit, annotated version tag, push and strict repository workflow publication, followed by removal of the two local-feed packages and matching global caches after official publication is verified. It does not authorize a new branch/worktree or closing unverified Core tasks.
