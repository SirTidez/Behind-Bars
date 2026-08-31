# Justice System Refactor Changelog

## Purpose

This document is the canonical architectural migration record for the Behind Bars justice-system cleanup. It is not a release changelog and it is not a player-facing feature summary. Its purpose is to document, from start to finish, how the project moves from the current mixed-ownership architecture to a manager-led, decision-complete structure that another engineer can implement and maintain without guessing intent.

This refactor exists to fix the current architectural drift:

- `Core` is acting as both entrypoint and mixed composition root.
- Several gameplay managers self-create or bootstrap on demand.
- Jail, bail, parole, NPC, UI, persistence, and event ownership are split across overlapping systems.
- Parole intake, search, and check-in flows are duplicated across multiple classes.
- Some release and station callbacks represent multiple phases with overloaded meaning.
- Save ownership is split across multiple systems, and some models still rely on unstable player keys.

This changelog also records a hard requirement for the entire migration: IL2CPP behavior is the primary target and must remain aligned with Mono through compile-time guards and shared behavior contracts.

## Baseline Architecture Snapshot

### Current top-level ownership

Before the refactor:

- `Core` initializes major systems directly, registers IL2CPP types, manages scene startup, drives load progress, creates runtime managers, and exposes subsystem getters.
- `JailSystem` owns arrest handling and sentence flow, but also reaches into bail, parole, UI, NPC coordination, and release cleanup.
- `BailSystem` owns part of bail calculation and payment, while other bail logic also exists elsewhere.
- `CourtSystem` is an incomplete negotiation layer that still hands back into bail/payment flow.
- `ParoleSystem` owns parole timers and records, but also triggers supervising officer logic, check-ins, search scheduling, and other NPC-facing behavior.
- `PrisonNPCManager` owns spawning and registration, while other NPC-related managers also create or coordinate officer behavior.
- `BehindBarsUIManager` acts as a large UI facade with significant gameplay-side branching and event subscriptions.

### Current manager and singleton drift

Before the refactor:

- Some systems are pure lazy singletons.
- Some systems are manually constructed by `Core`.
- Some are `MonoBehaviour` singletons attached at runtime.
- Some managers self-create when first accessed.
- Ownership and teardown are inconsistent across Jail, Release, NPC, UI, and persistence systems.

### Current duplicate and overloaded flows

Before the refactor:

- Parole intake can be triggered from more than one path.
- Search ownership exists both in parole domain logic and officer-side search logic.
- Check-in scheduling and check-in interaction are split between domain and NPC components.
- Release phases are represented through callbacks whose meaning depends on global context rather than explicit stage contracts.
- Station components reuse event names for materially different actions.

### Current persistence and identity problems

Before the refactor:

- Justice gameplay state is spread across rap sheets, saveable infrastructure, and separate persistent-player state.
- Some persistence paths overlap conceptually without one canonical owner.
- Some records still rely on mutable or unstable player identity patterns rather than a stable player key.

### Current major classes and their baseline roles

These classes define the starting point, even where the current ownership is incorrect:

- `Core`: entrypoint, type registration, startup bootstrap, scene loading, subsystem access.
- `JailSystem`: arrest, jail flow, bail handoff, release initiation, parole violation handoff.
- `BailSystem`: bail quoting, affordability, payment handling.
- `CourtSystem`: negotiation/session shell for bail-related flow.
- `ParoleSystem`: parole lifecycle, timers, check-ins, violations, NPC-triggered supervision.
- `ReleaseManager`: release progression, officer pool usage, exit processing.
- `BookingProcess`: booking progression, station callbacks, intake progression.
- `PrisonNPCManager`: NPC spawning, registration, role lookup, role-specific coordination leakage.
- `DynamicParoleOfficerManager`: officer presence and parole-triggered spawn/update behavior.
- `ParoleOfficerBehavior`: patrol, escort, intake, dialogue, compliance/search-adjacent logic.
- `ParoleSearchSystem`: officer search execution and some release/search timing.
- `ParoleCheckInSystem`: officer check-in interaction, dialogue progression, domain mutation leakage.
- `BehindBarsUIManager`: loading screen, parole UI, bail UI, notifications, wanted level, command UI, gameplay event wiring.
- `RapSheetManager`: criminal record access and persistence cache.
- `ParoleConditionManager`: current condition ownership and evaluation logic, currently too global.

## Refactor Goals and Rules

The target architecture follows these rules:

- There is one global runtime owner: `BehindBarsSystemManager`.
- There is one top-level manager per domain.
- Every multi-step flow has one authoritative owner.
- Gameplay logic never branches on UI visibility.
- Gameplay managers are never lazily created during runtime flow.
- Anonymous listeners are never used where teardown is required.
- Justice gameplay state has one canonical save ownership model.
- Parole intake has one canonical entry path.
- Parole search has one canonical execution path.
- Every class has one clearly documented responsibility and an explicit owner.

## Implemented Progress

The following refactor slices are already landed in the codebase and should be treated as the current migration baseline rather than future intent:

- `Core` now boots `BehindBarsSystemManager`, and legacy subsystem access is routed through compatibility facades instead of direct mixed ownership.
- `ReleaseManager` is now explicitly bootstrapped and shut down by `BehindBarsSystemManager`, with compatibility fallback retained only as a temporary bridge.
- Release-officer registry ownership moved to `PrisonNPCManager`; `ReleaseOfficerBehavior` registers through the NPC registry, and `ReleaseManager` now consumes that registry instead of treating its own officer list as authoritative.
- Bail-vs-time-served ownership was narrowed so bail marks a pending `BailPayment` release, jail sentence waiters consume exactly one pending release type after custody cleanup, and the duplicate fallthrough into `TimeServed` is removed.
- Legacy random-search ownership was removed from `ParoleSystem`; officer-driven search now flows through `ParoleSearchSystem` as the only active search engine.
- Initial parole intake start was narrowed to `DynamicParoleOfficerManager`, which now queues and retries the supervising-officer handoff idempotently instead of relying on duplicate direct starts.
- `ParoleCheckInSystem` now owns active check-in interaction flow, with `ParoleOfficerBehavior` reduced to a compatibility shim/accessor and `PrisonNPCManager` responsible for attaching the controller to the supervising officer.
- Supervising-officer session arbitration was extracted into `SupervisingOfficerInteractionCoordinator`, so intake/check-in ownership is no longer hidden as a nested helper inside `DynamicParoleOfficerManager`.
- `BehindBarsSystemManager` now owns an explicit `PlayerKeyService`, and bail storage / cell assignment started consuming that shared key source instead of inventing local player-name-based identifiers.
- `PersistentPlayerData` and `JailSystem` now use the shared player-key service for inventory snapshots, stored exit-position ownership, and pending release state, with legacy-name fallback kept where needed for compatibility.
- `RapSheet` and `RapSheetManager` now use stable-player-key-first persistence identity for criminal records: in-memory cache ownership remains key-backed, on-disk save identity now derives from a sanitized stable player key, and legacy name-folder saves are loaded once and marked for migration on the next save instead of remaining the live canonical path.
- `ParoleTimeTracker` and `ParoleSystem` runtime ownership now use shared player-key-backed dictionaries and session sets for active paroles, check-in scheduling, warrant throttles, and pending officer-text retries, while keeping `Player` references only in the value layer where behavior still needs them.
- `JailTimeTracker` runtime sentence ownership now uses shared player-key-backed dictionaries and sets for active sentences, completed-sentence summaries, jail-status flags, and fallback real-time tracking, again preserving the public `Player` API while removing object-identity ownership from internal state.
- `ReleaseManager` now keys active release ownership by stable player key through `ReleaseRequest.playerKey`, while preserving the public `Player`-based API and release-officer callback/event surface as a compatibility bridge.
- `JailCellManager`, `JailController`, and shared jail occupancy structures now split holding-cell occupancy into stable occupant keys plus display names, preserving legacy string entrypoints while removing the manager-level dependence on `player.name` as the only runtime identifier.
- `JailSystem` now uses player-key-aware cell occupancy entrypoints for active holding-cell assignment/release, and `ReleaseOfficerBehavior` now tracks its assigned releasee by stable player key so release callbacks and retry logic no longer depend on raw `Player` reference equality at the flow boundary.
- Release-side station components (`InventoryPickupStation`, `JailInventoryPickupStation`, and `ExitScannerStation`) now resolve `ReleaseManager` through the registered system-owned path instead of leaf-level `ReleaseManager.Instance` fallback creation, so release flow consumers no longer reintroduce lazy manager ownership from the edge of the system.
- `ReleaseOfficerBehavior` and `JailSystem` no longer rely on active `ReleaseManager.Instance` lookups in live release orchestration paths; both now resolve the registered manager instead, while `JailSystem` exit-position lookup follows the same player-key ownership path as the rest of active jail runtime state and the dormant main-cell transfer flow already targets the player-aware cell APIs before that path is re-enabled.
- `CellDetail` no longer exposes the legacy string-only spawn assignment/release overloads as part of the active architecture. Live jail flows now enter through the player-aware API only, with the key/display-name split kept as a private implementation detail inside `JailStructures`.
- `JailCellManager` and `JailController` now match that boundary: the public string-only holding-cell assignment/release bridge was removed from active runtime surface, and the old name-based path survives only as a local diagnostic helper inside `JailCellManager`.
- `PersistentPlayerData` now treats name-keyed inventory snapshots and stored exit positions as legacy migration paths instead of normal runtime lookup: active writes use the stable player key, reads resolve the stable key first, and any legacy name-keyed data is rewritten to the stable key on access. `JailSystem` was narrowed alongside it by removing the public string-key exit-position wrapper from the active API surface.
- Runtime player-key resolution is now centralized behind `Core.ResolvePlayerKey(...)` for active justice-state systems (`BailSystem`, `JailSystem`, `ReleaseManager`, `JailTimeTracker`, `ParoleSystem`, `ParoleTimeTracker`, `JailCellManager`, `JailStructures`, `ReleaseOfficerBehavior`, `RapSheet`, `RapSheetManager`, and `PersistentPlayerData`) so those systems no longer duplicate local `GetPlayerKeyService()?.GetPlayerKey(player) ?? player.name` bridges.
- `CrimeManager` now exists under `BehindBarsSystemManager` as the first explicit crime-domain owner. `RapSheetManager` is bootstrapped and shut down through that manager, while `RapSheetManager.Instance` remains as a compatibility bridge so existing call sites can migrate incrementally instead of through a single wide churn.
- `CellAssignmentManager` now consumes the shared `Core.ResolvePlayerKey(...)` resolver instead of carrying its own local player-key fallback policy, removing the last duplicated active jail-occupancy identity rule outside the shared resolver boundary.
- The top-level runtime owners now consume rap-sheet access through `Core.ResolveRapSheetManager()` instead of reaching straight to `RapSheetManager.Instance`: `Core`, `JailSystem`, `ReleaseManager`, and `ParoleSystem` now all resolve crime-state access through the manager-owned path first, leaving Harmony/UI/test callers on the compatibility bridge for a later cleanup slice.
- The next live runtime layer is now migrated too: parole NPC systems (`DynamicParoleOfficerManager`, `ParoleCheckInSystem`, `ParoleIntakeStateMachine`, `ParoleOfficerBehavior`, and `ParoleSearchSystem`), parole helper services (`HomeVisitSystem`, `ParoleCompletionRewards`, and `ParoleFeeSystem`), `FineCalculator`, and `BehindBarsUIManager` now all consume `Core.ResolveRapSheetManager()` instead of directly using `RapSheetManager.Instance`. That leaves the remaining singleton callers concentrated in Harmony patches, test scaffolding, and the compatibility fallback inside `Core`.
- Rap-sheet ownership is now fully closed over the crime domain: `CrimeManager` exposes public rap-sheet operations, `Core` exposes thin static wrappers (`GetRapSheet`, `MarkRapSheetChanged`, `GetAllRapSheets`, `ClearRapSheetCache`), and all remaining Harmony/UI/test callers now use those manager-owned entrypoints instead of referencing `RapSheetManager.Instance` directly. The singleton remains only as an internal compatibility fallback inside `Core` and `CrimeManager`.
- IL2CPP compile blockers were burned down to zero errors for both `Debug IL2CPP` and `Debug Mono`, including item-surface cleanup (`BaseItemInstance` / `ItemInstance.Name`), parole support regressions, and the mismatched jail NPC audio-controller surface.
- `ParoleManager` now owns the parole-time tracker access seam instead of leaving `ParoleSystem` to talk to `ParoleTimeTracker` directly. `Core.ResolveParoleManager()` now routes parole runtime code through the manager-owned tracker facade for start/stop/remaining-time access, while the tracker implementation itself remains unchanged.
- `JusticeUIManager` now exists under `BehindBarsSystemManager` as the manager-owned UI root for gameplay systems. `Core` routes top-level loading-screen, scene-init, parole-status, jail-info, and related helper entrypoints through `Core.ResolveUIManager()` instead of reaching straight into `BehindBarsUIManager.Instance`.
- The gameplay-side UI ownership seam is now mostly collapsed behind that manager-owned root: jail/release orchestration, parole/officer behaviors, and jail station/interactable flows now resolve UI access through `Core.ResolveUIManager()` instead of directly calling `BehindBarsUIManager.Instance`. The remaining direct singleton references are intentionally concentrated inside `JusticeUIManager`, which now acts as the only compatibility wrapper around the concrete scene UI implementation.
- `UpdateNotificationUI` no longer relies on anonymous button-listener lambdas where teardown matters. It now uses explicit stable listener registration and paired unregistration, preserving the existing behavior while making the notification UI safer across Mono and IL2CPP lifecycle edges.
- `ReleaseManager` listener ownership was tightened further: per-request release-officer callbacks are now attached and detached through stable stored delegates, with centralized cleanup on success, failure, cancellation, reassignment, and manager shutdown instead of anonymous inline subscriptions that could be orphaned.
- `JailTimeTracker` now owns its arrest/release listener lifecycle explicitly: release-completion subscription and local-player arrest subscription are attached through stable stored handlers, retried without stacking duplicate listeners, and torn down through an explicit shutdown path instead of relying on retry-side anonymous registration.
- Parole officer event-bridge ownership has moved up into `BehindBarsSystemManager`. `DynamicParoleOfficerManager` no longer subscribes directly to the static parole lifecycle events; the system manager now owns that bridge and forwards parole start/end transitions into the scene-bound NPC manager, while `ParoleSystem` ensures the dynamic parole officer manager exists before it emits the initial start event so first-release intake behavior remains intact.
- The static parole lifecycle events on `ParoleSystem` have now been audited and narrowed to explicit compatibility-only surface. No current in-repo runtime path still subscribes to `OnParoleStarted` or `OnParoleEnded`; the manager graph now consumes the instance-owned `ParoleStarted` / `ParoleEnded` events, while the legacy static events remain mirrored only for external compatibility until that public shim can be retired.
- `DialogueChoiceListener` no longer uses a single global label/callback slot with anonymous listener attachment. It now tracks registrations per `DialogueHandler`, stores stable listener references for reliable removal, replaces existing registration on re-register, and exposes explicit unregister behavior. `ReleaseOfficerBehavior` now tears down its pending dialogue-choice registration on destroy so the release dialogue path does not leave orphaned listeners behind.
- Parole support-service ownership is now centralized behind `Core` resolver shims: active callers in parole UI, rap-sheet logic, officer interaction systems, jail release flow, and `ParoleSystem` itself now resolve `ParoleConditionManager`, `ParoleFeeSystem`, and `HomeVisitSystem` through explicit `Core.Resolve...` entrypoints. The only remaining direct singleton access for those services is the compatibility fallback intentionally concentrated inside `Core`.
- `ReleaseManager` callsite ownership is now similarly centralized: active gameplay callers now resolve release orchestration through `Core.ResolveReleaseManager()` instead of directly probing `ReleaseManager.TryGetRegisteredInstance(...)`. The singleton/bootstrap surface still exists inside `ReleaseManager`, but it is now hidden behind a single explicit compatibility seam in `Core`.
- Cell-assignment and prison-NPC registry access have been narrowed in the same way. Active jail, booking, release, parole, and officer flows now resolve `CellAssignmentManager` and `PrisonNPCManager` through `Core.ResolveCellAssignmentManager()` and `Core.ResolvePrisonNpcManager()` instead of reaching directly for the singleton instances. Direct singleton access for those seams now exists only in the `Core` compatibility shims.
- Persistent-player-state access is now centralized too: active harmony, jail, release, storage, and inventory flows now resolve `PersistentPlayerData` through `Core.ResolvePersistentPlayerData()` instead of directly touching `PersistentPlayerData.Instance`. The singleton remains only as the compatibility fallback inside `Core`.
- Active jail/parole tracker and parole-orchestration singleton calls are now concentrated behind `Core` as well. Runtime code now uses `Core.ResolveJailTimeTracker()`, `Core.ResolveParoleTimeTracker()`, and `Core.ResolveDynamicParoleOfficerManager()` across jail flow, parole flow, witness suppression, parole check-in, UI, and system-manager bridge paths. Direct singleton access for these seams now remains only inside `Core` plus test-only calls to `DynamicParoleOfficerManager.Instance`.
- The next architectural tier has been scaffolded: `JailManager`, `ParoleManager`, and `NpcManager` now exist as documented top-level domain-manager shells that explicitly model the remaining ownership cuts without changing live gameplay behavior. `BehindBarsSystemManager` now instantiates and exposes these managers, attaches the currently known collaborators, and refreshes scene-bound bindings opportunistically so the future cutovers have a stable composition point.
- Booking ownership now has a first-class compatibility seam too. `Core.ResolveBookingProcess()` now centralizes booking-process access, `BehindBarsSystemManager` attaches the resolved booking process onto `JailManager`, and the first live callers (`JailSystem` and `IntakeOfficerStateMachine`) now use that resolver instead of directly probing `BookingProcess.Instance`.
- `JailManager` now owns the first real jail-domain orchestration behavior rather than just passive references: booking-process startup and completion waiting are now centralized in `JailManager.RunBookingProcess(...)`, and `JailSystem` routes both of its previous inline booking flows through that manager-owned seam instead of duplicating the same `BookingProcess.StartBooking(...)` and polling logic in multiple places.
- `JailManager` now also owns the active custody and sentence-tracker seam. `JailSystem` and `ReleaseManager` route jail-status flags, custody-control maintenance, sentence tracking start/stop, remaining-time reads, original-sentence reads, and time-served reads through `JailManager` instead of reaching directly into `JailTimeTracker` from multiple jail/release flows.
- The jail/release handoff is now manager-owned too. Pending release types live on `JailManager`, active bail and release-UI callers now mark or trigger release through `JailManager`, and `JailSystem` has been reduced to compatibility forwarding for pending release consumption and enhanced-release initiation while still preserving the existing `ReleaseManager` runtime underneath.
- Post-booking jail time is now also owned by `JailManager`. `StartJailTimeAfterBooking(...)` was moved behind the manager seam, with `JailSystem` reduced to a thin compatibility wrapper so the manager now owns the post-booking wait-and-release orchestration while the legacy call path remains intact for `BookingProcess`.
- `JailManager` now exposes the remaining jail/release helper seams needed by booking and release cleanup. Live callers now resolve fallback bail calculation, jail-status cleanup, and immediate-arrest forwarding through `JailManager` instead of reaching through `JailSystem` directly.
- `NpcManager` and `ParoleManager` now each own one concrete live seam instead of just passive references. `NpcManager` refreshes its own scene-bound NPC collaborators through a manager-owned binding pass, and `ParoleManager` now owns `ParoleTimeTracker` access so live parole-time tracking calls in `ParoleSystem` route through the manager seam instead of the tracker singleton/service directly.
- Supervising-officer text dispatch is now partially manager-owned as well. Runtime callers in `HomeVisitSystem`, `ParoleFeeSystem`, `ParoleCompletionRewards`, `ParoleCheckInSystem`, and `ParoleOfficerBehavior` now route those messages through `ParoleManager` instead of reaching directly into `ParoleSystem` for that domain service.
- `NpcManager` now also owns the remaining active parole-officer collaborator seams used by `ParoleSystem`: parole-start handoff, on-demand parole-officer-manager bootstrapping, and supervising-officer lookup now route through the NPC manager shell instead of direct resolver calls.
- The parole lifecycle bridge is now narrower as well. `BehindBarsSystemManager` no longer forwards parole-start and parole-end transitions straight to `DynamicParoleOfficerManager`; those callbacks now route through `NpcManager`, so the global composition root is no longer directly coupled to parole NPC transition handling.
- Supervising-officer session ownership is being tightened further inside the NPC interaction slice. `ParoleCheckInSystem` now routes check-in reserve/start/cancel/complete transitions through manager-exposed coordinator helpers instead of stitching the session lifecycle together inline, which reduces the number of places that need to reason about active check-in state and keeps the admission/teardown surface centered on `DynamicParoleOfficerManager`.
- The remaining test scaffolding for dynamic parole-officer spawning now uses `Core.ResolveDynamicParoleOfficerManager()` instead of direct `DynamicParoleOfficerManager.Instance` access. This keeps the test harness aligned with the same compatibility seam used by runtime code without changing the test behavior.
- The jail-domain cut is deeper now in live runtime code. Remaining NPC/parole arrest handoffs in `GuardBehavior`, `ParoleOfficerBehavior`, `ParoleSearchSystem`, and the parole-revocation path now route through `JailManager.HandleImmediateArrest(...)` instead of calling back into `JailSystem` directly, so jail authority is narrower and more explicit in the active runtime graph.
- `NpcManager` now owns the booking/release-side NPC resolver seam instead of leaving those flows on direct `Core.ResolvePrisonNpcManager()` calls. Booking and release code now use manager-owned wrappers for intake-officer lookup, escort requests, release-officer registry access, supervising-officer lookup, and parole/guard roster reads, while `PrisonNPCManager` remains the implementation detail behind that facade.
- Daily check-in session ownership is now manager-owned at the parole domain boundary. `ParoleManager` exposes `TryBeginCheckInSession(...)`, `EndCheckInSession(...)`, `NotifyDailyCheckInCompleted(...)`, and `GetDailyCheckInStatus(...)`, and `ParoleCheckInSystem` now uses those wrappers instead of reaching into `ParoleSystem` directly for check-in admission, completion, and timing validation.
- `DynamicParoleOfficerManager` now prefers the manager-owned parole seam when it needs loaded-player parole state, reducing another direct dependency on `ParoleSystem` as the effective owner of officer-side interaction scheduling.
- Guard and parole officer registration now also route through `NpcManager` wrappers. `GuardBehavior` and `ParoleOfficerBehavior` register with the canonical NPC registry through `NpcManager.RegisterGuard(...)` and `NpcManager.RegisterParoleOfficer(...)`, and `DynamicParoleOfficerManager` now resolves prison-NPC collaborators through the `NpcManager`-owned scene binding path instead of the direct prison-manager resolver.
- This checkpoint has been revalidated on the combined tree after integration: both `Debug IL2CPP` and `Debug Mono` compile with `0 Error(s)` after the jail-caller cleanup, booking/release NPC facade cleanup, and parole check-in/session wrapper cutovers landed together.
- `ParoleManager` now owns the actual runtime daily check-in state rather than just forwarding calls. Daily check-in requirements, scheduled-day bookkeeping, reminders, expiry processing, and active check-in session tracking moved out of `ParoleSystem`, while `ParoleSystem` keeps only the compatibility-facing wrapper methods for those APIs.
- Missed daily check-in consequence handling also moved into `ParoleManager`. Rapport penalties, LSI escalation, violation creation, supervising-officer text, and warrant issuance handoff are now manager-owned, leaving `ParoleSystem` responsible only for broader parole runtime behavior and warrant enforcement.
- `DynamicParoleOfficerManager` no longer falls back to `Core.Instance?.GetParoleSystem()` in its active initialization path, and parole-officer spawning now routes through `NpcManager.SpawnParoleOfficer(...)` instead of reaching through `NpcManager.PrisonNpcManager` directly.
- The remaining dead parole-side compatibility leftovers were trimmed from `ParoleSystem`, including the unused placeholder `SpawnParoleOfficer()` path and the redundant `NpcManager` parole-officer registration extension shim.
- `JailManager` now owns more of the post-sentence jail/release seam. Active jail-flow reset, bail fallback calculation, and post-sentence release completion now route through manager-owned APIs, leaving `JailSystem` with compatibility fallback behavior instead of duplicated live orchestration.
- `CourtSystem` now routes bail handoff through `Core.ResolveBailSystem()` rather than the direct legacy getter, and the remaining `Core.GetJailSystem()`, `Core.GetBailSystem()`, `Core.GetCourtSystem()`, and `Core.GetParoleSystem()` APIs are now explicitly marked as compatibility-only surfaces.
- `NpcManager.RefreshSceneBindings()` now refreshes through the canonical prison-NPC resolver instead of reading back through its own attached collaborator field, removing the last self-referential scene-binding bug from the manager-owned NPC seam.
- `NpcManager` now queues early guard, parole-officer, and release-officer registration work when the scene-bound `PrisonNPCManager` is not yet ready, flushes those registrations once the binding appears, and logs failures instead of silently dropping registration requests during startup timing gaps.
- `DynamicParoleOfficerManager.Initialize()` is now reentrant-safe. Initialization short-circuits when already initialized or already in progress, and cleanup clears both initialization guards so staged bootstrap and retry paths no longer risk duplicate subscriptions, duplicate spawn passes, or orphaned tracked officers.
- `DynamicParoleOfficerManager` check-in cancellation now clears the correct supervising-officer reservation path instead of calling the intake cancel path, preventing failed check-ins from leaking ownership state and blocking later interactions.
- The primary holding-cell post-sentence release path in `JailSystem` now routes through `JailManager.CompletePostSentenceRelease(...)` in the live path instead of reimplementing pending-release consumption and bail lookup inline. The old logic remains only as a compatibility fallback when `JailManager` is unavailable.
- `CourtSystem` no longer silently drops sentencing handoff when bail infrastructure is unavailable. It now records explicit judge notes, logs the failure, and exits while preserving custody behavior instead of silently falling through without processing the final decision.
- `ParoleManager` now exposes manager-owned wrappers for parole start, LSI step-down evaluation, and warrant issuance, so release/check-in code no longer needs to reach directly into `ParoleSystem` for those runtime actions.
- The remaining live bail lookups in `JailManager`, `JailSystem`, `BookingProcess`, and `ReleaseManager` now route through `Core.ResolveBailSystem()` instead of direct `Core.Instance?.BailSystem` reads, and the patrol curfew check in `ParoleOfficerBehavior` no longer branches on direct `Core.Instance?.ParoleSystem` presence.
- At this checkpoint, the obsolete subsystem getters on `Core` remain only as explicit compatibility definitions. Active runtime callsites now resolve jail, bail, parole, rap-sheet, UI, release, booking, and NPC collaborators through manager-owned seams or `Core.Resolve...` shims.
- Final cleanup narrowed the remaining compatibility surface further: the obsolete `Core.GetJailSystem()`, `GetBailSystem()`, `GetCourtSystem()`, and `GetParoleSystem()` getters were removed after the live runtime graph stopped using them, leaving `Core` with only the explicit `Resolve...` shims and the player-key compatibility accessor.
- `NpcManager` now refreshes its prison-NPC scene binding through a direct scene lookup helper instead of routing that binding loop back through `Core.ResolvePrisonNpcManager()`, so the only remaining prison-manager compatibility fallback is the explicit shim in `Core`.

These implemented slices do not complete the refactor. They establish the current checkpoint for the next phases, especially domain-manager cutovers, persistence cleanup, and event/save ownership consolidation.

## Phase-by-Phase Change Log

## Phase 1: Runtime Foundation

### What changed

- Introduce `BehindBarsSystemManager` as the single runtime composition root.
- Move subsystem construction and lifecycle ownership out of `Core`.
- Add a common subsystem lifecycle contract for top-level managers.
- Keep temporary compatibility facades on `Core` for existing code during migration.

### Why it changed

Before:

- `Core` directly created multiple systems and also advanced load/setup flows.
- Startup ownership was split between `Core`, scene lifecycle, and self-creating managers.

After:

- `Core` is only the Melon entrypoint.
- `BehindBarsSystemManager` owns construction order, initialization, scene-ready callbacks, startup validation, and shutdown.

### Affected managers and classes

- `Core`
- `BehindBarsSystemManager`
- All top-level domain managers

### Removed duplication and orphan logic

- Removed mixed startup responsibility between direct construction, lazy singleton access, and scene-bound self-bootstrap.
- Removed the need for gameplay systems to discover sibling managers through ad hoc initialization timing.

### Validation required before moving on

- `Debug IL2CPP` compiles.
- `Debug Mono` compiles.
- Startup logs show each manager created exactly once.
- No gameplay manager is created lazily during a gameplay callback.

### Cross-reference

This phase enables the infrastructure rules in Phase 2 and the manager ownership transfers in Phases 3 through 7.

## Phase 2: Infrastructure Cleanup

### What changed

- Standardize singleton policy.
- Introduce a shared event subscription policy.
- Introduce a stable player-key policy for all justice state.
- Define storage classes: save-scoped, session-scoped, machine-local.
- Deprecate overlapping helper infrastructure that no longer fits the canonical model.

### Why it changed

Before:

- Singleton patterns were inconsistent.
- Event subscription lifetimes were hard to audit.
- Save ownership was split across multiple systems.
- Some models depended on unstable identity assumptions.

After:

- Plain services are owned only by `BehindBarsSystemManager`.
- Scene-bound components are created only through explicit startup paths.
- Event subscription is lifecycle-paired and traceable.
- All justice state uses a stable player key.
- Save ownership is explicitly categorized.

### Affected managers and classes

- `GameTimeManager`
- `ReleaseManager`
- `RapSheetManager`
- `PersistentPlayerData`
- save/persistence helpers
- UI and station components with runtime listeners

### Removed duplication and orphan logic

- Deprecated helper abstractions that overlap with the canonical save model.
- Removed event wiring that depended on anonymous callbacks or one-way subscription helpers.

### Validation required before moving on

- No justice-state persistence uses mutable player names as primary keys.
- All long-lived subscriptions have explicit unsubscribe paths.
- Save ownership rules are documented and applied to active systems.

### Cross-reference

This phase is required before the ownership transfers in Jail, Bail, Parole, NPC, and UI can be made safely.

## Phase 3: Jail and Bail Ownership

### What changed

- Introduce `JailManager` as the top-level owner for arrest, custody, booking handoff, sentence state, and release requests.
- Introduce `BailManager` as the single owner of bail quote, negotiation, payment, and bail state.
- Move release progression under `ReleaseCoordinator`, owned by `JailManager`.
- Replace overloaded jail/release station callbacks with explicit phase-specific contracts.

### Why it changed

Before:

- `JailSystem` was handling jail flow while also reaching into bail and release decisions.
- `BailSystem` state relied on static/shared storage patterns rather than explicit domain ownership.
- `ReleaseManager` maintained a separate officer pool and runtime lifecycle outside the authoritative manager graph.
- Station callbacks reused the same events for materially different steps.

After:

- `JailManager` is the sole owner of jail and release decisions.
- `BailManager` is the sole owner of bail rules and payment state.
- Bail payment reports an outcome to jail; it does not release the player directly.
- Release phases are represented by explicit contracts rather than context-sensitive callbacks.

### Affected managers and classes

- `JailSystem`
- `BailSystem`
- `CourtSystem`
- `ReleaseManager`
- `BookingProcess`
- release/inventory/mugshot/scanner stations

### Removed duplication and orphan logic

- Removed split bail authority.
- Removed direct or implicit release triggering from bail code.
- Removed separate release-officer pool ownership outside the canonical NPC registry.

### Validation required before moving on

- One arrest produces one release path only.
- Bail payment and time-served release do not race each other.
- Station callbacks reflect explicit phase transitions.

### Cross-reference

This phase must be stable before parole activation is moved under clean ownership in Phase 4.

## Phase 4: Parole Domain Ownership

### What changed

- Introduce `ParoleManager` as the sole owner of parole lifecycle, compliance score, check-in schedule, violations, completion, and jail suspension/resume behavior.
- Convert `ParoleConditionManager` from global active-state owner to evaluator/registry service.
- Remove legacy search scheduling from `ParoleSystem`.
- Move parole persistence under explicit domain ownership.

### Why it changed

Before:

- `ParoleSystem` owned both domain logic and gameplay-side NPC orchestration.
- Search scheduling existed both in parole domain flow and officer-side systems.
- Conditions were too global and not cleanly scoped to one parolee or one record.
- Compliance and violations were split across record classes, parole domain logic, and officer-side components.

After:

- `ParoleManager` owns parole state and rules only.
- Search policy is no longer duplicated in parole domain code.
- Conditions are evaluated per player/parole record through a registry model.
- Compliance and violation state are written through one authoritative path.

### Affected managers and classes

- `ParoleSystem`
- `ParoleConditionManager`
- `ParoleRecord`
- `RapSheetManager`
- parole-related persistence models

### Removed duplication and orphan logic

- Removed duplicate search scheduling from the parole domain.
- Removed global active-condition ownership.
- Removed parole lifecycle responsibilities from NPC/UI-driven code paths.

### Validation required before moving on

- Parole start/end, violation writes, and check-in scheduling are owned by `ParoleManager` only.
- No NPC component writes parole state directly except through manager/coordinator commands.

### Cross-reference

This phase prepares the clean interaction boundaries needed for NPC and officer orchestration in Phase 5.

## Phase 5: NPC and Interaction Ownership

### What changed

- Introduce `NpcManager` as the top-level NPC runtime owner.
- Split `PrisonNPCManager` into `NpcRegistry` and `NpcSpawnManager`.
- Introduce `ParoleOfficerManager` for officer lookup, active-role ownership, and arbitration.
- Introduce `ParoleInteractionCoordinator` for intake, search, check-in, and officer-owned interaction session flow.
- Consolidate release officer registry ownership.
- Replace blunt global route conflict behavior with explicit escort coordination rules.

### Why it changed

Before:

- NPC ownership was split between spawn code, manager lookup, and role-specific managers.
- Supervising-officer intake had multiple entry paths.
- Search behavior was duplicated across domain and NPC systems.
- Release officer ownership was split between `ReleaseManager` and NPC spawning.
- Route conflict handling acted as a coarse global mutex rather than explicit coordination.

After:

- Spawn/registry concerns are separate from role orchestration.
- Intake has one entry path.
- Search has one engine and one arbitration owner.
- Release officer registry is canonical.
- Officer coordination is explicit rather than a permanent “everything conflicts” fallback.

### Affected managers and classes

- `PrisonNPCManager`
- `DynamicParoleOfficerManager`
- `ParoleOfficerBehavior`
- `ParoleSearchSystem`
- `ParoleCheckInSystem`
- `ParoleIntakeStateMachine`
- `ReleaseOfficerBehavior`
- `OfficerCoordinator`

### Removed duplication and orphan logic

- Removed duplicate intake triggers.
- Removed duplicate search ownership.
- Removed separate release-officer pooling outside the canonical registry.
- Removed compensation-style escort cleanup that existed because no single owner controlled the route/session state.

### Validation required before moving on

- Intake begins from one explicit trigger only.
- Search is executed by one closest officer only.
- Check-in ownership is unambiguous.
- Release officers are discoverable through one registry only.

### Cross-reference

This phase must settle interaction ownership before UI and dialogue are split in Phase 6.

## Phase 6: UI and Dialogue Ownership

### What changed

- Introduce `JusticeUIManager` as the top-level UI composition root.
- Split `BehindBarsUIManager` into feature presenters/controllers.
- Move UI to presenter/view-model driven updates.
- Replace gameplay checks against UI visibility with explicit gameplay phases.
- Introduce a dialogue adapter layer.
- Replace global or static choice handling with per-session dialogue choice ownership.

### Why it changed

Before:

- `BehindBarsUIManager` owned too many screens and gameplay subscriptions.
- Gameplay systems sometimes checked whether UI was visible to make control-flow decisions.
- Dialogue control was spread across behavior logic, dialogue wrappers, and static/shared listeners.

After:

- UI renders view state pushed from managers/coordinators.
- Gameplay phases are domain/session state, not UI state.
- Dialogue presentation is driven by one adapter/presenter layer.
- Choice handling is scoped to the active interaction session.

### Affected managers and classes

- `BehindBarsUIManager`
- `ParoleConditionsUI`
- `ParoleStatusUI`
- `BailUI`
- `OfficerCommandUI`
- `UpdateNotificationUI`
- dialogue helper classes and listeners

### Removed duplication and orphan logic

- Removed gameplay branching on UI visibility.
- Removed shared/static dialogue choice ownership.
- Removed mixed view logic and domain mutation inside the same classes.

### Validation required before moving on

- UI visibility no longer decides gameplay flow.
- Dialogue handlers are scoped to the active interaction/session.
- UI presenters can be torn down cleanly on scene changes.

### Cross-reference

This phase depends on the session and manager ownership created in Phases 1 through 5 and feeds into the final persistence/documentation pass in Phase 7.

## Phase 7: Persistence and Documentation

### What changed

- Unify persistence ownership under the appropriate domain managers.
- Retire non-canonical save paths for justice-state data.
- Add XML documentation requirements for public managers, coordinators, services, and models.
- Add module overview documents and one architecture rules document.

### Why it changed

Before:

- Save ownership was split across multiple overlapping systems.
- Some save helpers no longer matched the effective architecture.
- Readability was hurt by unclear ownership and incomplete cross-references.

After:

- Each domain manager owns its own persistent gameplay models.
- Machine-local data is separate from save-scoped justice state.
- Public APIs are documented with ownership and collaborator context.
- Module docs explain how to read and maintain the system.

### Affected managers and classes

- domain managers
- persistence helpers
- `RapSheetManager`
- `PersistentPlayerData`
- `Saveable`-related helpers
- public subsystem/coordinator/model classes

### Removed duplication and orphan logic

- Deprecated overlapping or dormant save abstractions that no longer define the canonical path.
- Removed undocumented cross-domain ownership assumptions.

### Validation required before moving on

- Save ownership is documented and implemented consistently.
- Public managers and major coordinators have XML docs.
- Module overview docs exist for each domain.

### Cross-reference

This phase prepares the codebase for final cleanup and removal in Phase 8.

## Phase 8: Cleanup and Removals

### What changed

- Remove temporary compatibility facades after all call sites are migrated.
- Remove duplicate workflows and dead helpers.
- Remove deprecated classes that no longer participate in the canonical architecture.
- Publish the final ownership map.

### Why it changed

Before:

- Temporary compatibility layers existed to reduce risk during phased rollout.
- Deprecated classes remained as transitional scaffolding.

After:

- The codebase contains one canonical manager graph, one canonical flow per domain concern, and one documented ownership model.

### Affected managers and classes

- compatibility facades on `Core`
- deprecated subsystem wrappers
- legacy helpers
- duplicate flow implementations

### Removed duplication and orphan logic

- Deleted legacy intake/search/check-in/release/bail paths that remained only for transition compatibility.
- Removed deprecated save/helpers once no longer referenced.

### Validation required before completion

- No production flow depends on compatibility shims.
- No duplicate workflow paths remain.
- Ownership map matches actual runtime architecture.

### Cross-reference

This phase concludes the migration documented in all prior phases.

## Public API and Contract Changes

This section records every new or changed public type introduced by the refactor.

| Public Type | Owner | Purpose | Replacement Path | Status |
| --- | --- | --- | --- | --- |
| `BehindBarsSystemManager` | Global runtime | Single composition root for all subsystem managers | Replaces mixed runtime ownership in `Core` | Final |
| `ISubsystemManager` | Global runtime | Lifecycle contract for top-level managers | New contract | Final |
| `JailManager` | Justice runtime | Owns arrest, custody, release requests, and jail sessions | Replaces `JailSystem` as top-level jail owner | Final |
| `BailManager` | Justice runtime | Owns bail quote, negotiation, payment, and bail state | Replaces `BailSystem` as domain owner | Final |
| `ParoleManager` | Justice runtime | Owns parole lifecycle, compliance, check-ins, violations, and parole sessions | Replaces `ParoleSystem` as top-level parole owner | Final |
| `CrimeManager` | Justice runtime | Owns criminal record access, persistence, and stable player-key usage | Consolidates `RapSheetManager` ownership patterns | Final |
| `NpcManager` | Justice runtime | Owns NPC registry, spawning, and orchestration entry points | Replaces split manager ownership | Final |
| `JusticeUIManager` | UI runtime | Owns UI composition, presenter lifecycle, and view routing | Replaces `BehindBarsUIManager` as monolithic UI owner | Final |
| `NpcRegistry` | NPC runtime | Canonical registry for live NPC role instances | Split from `PrisonNPCManager` | Final |
| `NpcSpawnManager` | NPC runtime | Owns NPC spawning/despawning only | Split from `PrisonNPCManager` | Final |
| `ParoleOfficerManager` | NPC runtime | Owns officer arbitration, lookup, and officer role ownership | Replaces split officer ownership | Final |
| `ParoleInteractionCoordinator` | NPC runtime | Owns intake, search, and check-in interaction sessions | Replaces duplicate intake/search/check-in triggers | Final |
| `ReleaseCoordinator` | Jail runtime | Owns release flow state and contracts | Replaces `ReleaseManager` as standalone singleton | Final |
| `JailSession` | Jail runtime | Runtime jail state model | New model | Final |
| `BookingSession` | Jail runtime | Runtime booking state model | New model | Final |
| `ReleaseSession` | Jail runtime | Runtime release state model | New model | Final |
| `ParoleSession` | Parole runtime | Runtime parole state model | New model | Final |
| `OfficerInteractionSession` | NPC runtime | Runtime interaction state between officer and player | New model | Final |
| `Core.GetJailSystem()` | `Core` | Compatibility getter during migration | Delegates to `JailManager` during transition | Temporary compatibility |
| `Core.GetBailSystem()` | `Core` | Compatibility getter during migration | Delegates to `BailManager` during transition | Temporary compatibility |
| `Core.GetParoleSystem()` | `Core` | Compatibility getter during migration | Delegates to `ParoleManager` during transition | Temporary compatibility |

### Key domain event changes

- Before:
  - static cross-domain events and direct calls mixed together
  - gameplay managers and behaviors subscribed ad hoc
- After:
  - manager-owned events routed through explicit subsystem ownership
  - domain events reflect state transitions, not UI state
  - temporary event adapters may exist during migration but must be marked deprecated

## Old-to-New Ownership Map

The following map records the major ownership transfers required by the refactor.

| Current Class | Current Responsibility | New Owner Manager | Final Role Type | Status |
| --- | --- | --- | --- | --- |
| `Core` | Entry, bootstrap, scene load, subsystem access | Global runtime | Entrypoint only | Kept, narrowed |
| `JailSystem` | Arrest, jail flow, bail/release/parole crossover | `JailManager` | Compatibility facade, then removed | Split |
| `BailSystem` | Bail quote and payment | `BailManager` | Domain service/facade | Split |
| `CourtSystem` | Negotiation/session shell | `BailManager` | Coordinator/service | Moved |
| `ParoleSystem` | Parole lifecycle plus NPC/search/check-in crossover | `ParoleManager` | Compatibility facade, then removed | Split |
| `ParoleTimeTracker` | Parole timing | `ParoleManager` | Service | Kept, moved under owner |
| `GameTimeManager` | Game-time services | Global runtime | Shared service | Kept |
| `ReleaseManager` | Release flow, officer pool, exit processing | `JailManager` | `ReleaseCoordinator` | Renamed/moved |
| `BookingProcess` | Booking sequence and station callbacks | `JailManager` | Coordinator | Kept, narrowed |
| `JailTimeTracker` | Jail time/runtime custody tracking | `JailManager` | Service | Kept, narrowed |
| `CellAssignmentManager` | Cell assignment | `JailManager` | Service | Kept |
| `CrimeSentenceCalculator` | Sentence calculation | `JailManager` | Service | Kept |
| `FineCalculator` | Fine calculation | `BailManager` or `JailManager` | Service | Kept, reassigned |
| `RapSheetManager` | Rap sheet cache and persistence access | `CrimeManager` | Service | Kept, narrowed |
| `RapSheet` | Criminal/parole record model | `CrimeManager` | Persistent model | Kept |
| `ParoleRecord` | Parole data model | `ParoleManager` | Persistent model | Kept, reassigned |
| `ViolationRecord` | Violation model | `ParoleManager` / `CrimeManager` | Persistent model | Kept |
| `ParoleConditionManager` | Global active conditions plus evaluation | `ParoleManager` | Registry/evaluator service | Split |
| `ParoleFeeSystem` | Parole fee rules | `ParoleManager` | Service | Kept |
| `HomeVisitSystem` | Home-visit scheduling | `ParoleManager` | Service | Kept |
| `PrisonNPCManager` | NPC spawn, registry, role crossover | `NpcManager` | Facade, then split | Split |
| `DynamicParoleOfficerManager` | Officer spawn/update plus parole-triggered orchestration | `NpcManager` | Spawn/despawn service | Split |
| `ParoleOfficerBehavior` | Patrol, intake, search, compliance, dialogue | `NpcManager` | Behavior component | Kept, narrowed |
| `ParoleSearchSystem` | Search execution and timing | `NpcManager` / `ParoleOfficerManager` | Coordinator/service | Kept, narrowed |
| `ParoleCheckInSystem` | Check-in interaction and domain mutation | `NpcManager` / `ParoleInteractionCoordinator` | Coordinator | Split |
| `ParoleIntakeStateMachine` | Intake detection and flow | `NpcManager` / `ParoleInteractionCoordinator` | Coordinator helper | Kept, narrowed |
| `ReleaseOfficerBehavior` | Release officer local behavior | `NpcManager` | Behavior component | Kept, narrowed |
| `OfficerCoordinator` | Escort/route conflict logic | `NpcManager` | Coordination service | Kept, rewritten |
| `BaseJailNPC` | Base NPC behavior | `NpcManager` | Base behavior | Kept |
| `BehindBarsUIManager` | All justice UI plus subscriptions and gameplay branching | `JusticeUIManager` | Facade, then split | Split |
| `ParoleConditionsUI` | Conditions view and dismiss handling | `JusticeUIManager` | View | Kept, narrowed |
| `ParoleStatusUI` | Parole HUD/status | `JusticeUIManager` | View | Kept, narrowed |
| `BailUI` | Bail screen | `JusticeUIManager` | View | Kept, narrowed |
| `OfficerCommandUI` | Officer command display | `JusticeUIManager` | View | Kept, narrowed |
| `UpdateNotificationUI` | Notification view | `JusticeUIManager` | View | Kept, narrowed |
| `WantedLevelUI` | Wanted UI | `JusticeUIManager` | View | Kept, narrowed |
| `CrimeUIManager` | Overlapping crime UI ownership | `JusticeUIManager` | Deprecated or merged | Removed/merged |
| `PersistentPlayerData` | Persistent player-side justice-adjacent data | Domain owner based on data | Persistent model/service | Split |
| `PersistentPlayerDataSaveData` | Alternate save DTO path | Domain owner based on canonical persistence | DTO or removed | Removed/merged |
| `SaveableAutoRegistry` | Saveable discovery/registration helper | Persistence layer | Helper or removed | Deprecated |
| `FileUtilities` | Save-scoped file helper with limited ownership clarity | Persistence layer | Helper or removed | Deprecated/reviewed |

## Major Change Inventory

Use this checklist to confirm the changelog and implementation stay complete.

- [x] Startup/bootstrap ownership moved from mixed `Core` + lazy runtime creation to `BehindBarsSystemManager`.
- [ ] Singleton policy documented and enforced.
- [ ] Bail authority consolidated under `BailManager`.
- [x] Release officer registry ownership consolidated under NPC runtime.
- [x] Release flow owned only by jail runtime.
- [ ] Intake path consolidated to one canonical trigger.
- [ ] Search path consolidated to one canonical engine and arbitration owner.
- [x] Check-in scheduling ownership consolidated under `ParoleManager`.
- [ ] UI ownership split from `BehindBarsUIManager` into presenter/controller layers.
- [ ] Dialogue ownership split away from behavior-side direct mutation.
- [ ] Event lifecycle rules documented and applied.
- [ ] Save ownership model unified and documented.
- [ ] Stable player identity key adopted across justice state.
- [x] Deprecated classes and temporary shims clearly marked.
- [x] Validation gates documented for each migration phase.
- [x] IL2CPP and Mono compile gates preserved.

## Validation and Rollout Notes

### Build gates

Required before and during rollout:

- `Debug IL2CPP` must compile.
- `Debug Mono` must compile.

### Required manual regression flows

Each phase must preserve these end-to-end flows:

1. Arrest to booking.
2. Arrest to sentence tracking.
3. Time-served release.
4. Bail payment release.
5. Release inventory return and exit flow.
6. Parole activation on release.
7. Supervising officer intake flow.
8. Patrol officer search flow.
9. Daily check-in scheduling and completion.
10. Missed check-in violation.
11. Save/load of jail, parole, and criminal record state.

### Migration order dependencies

- Phase 1 must land before any domain manager cutover.
- Phase 2 must land before persistent ownership or event-heavy migrations.
- Phase 3 must be stable before parole activation ownership is moved.
- Phase 4 must be stable before officer-side parole interactions are narrowed.
- Phase 5 must settle interaction ownership before UI/dialogue migration.
- Phase 6 should land before removing temporary UI gating logic in old code paths.
- Phase 7 must be complete before final deletion of deprecated save/helpers.
- Phase 8 only begins once no active production path depends on temporary compatibility layers.

### Temporary compatibility shims allowed during rollout

Allowed temporarily:

- `Core` subsystem getters delegating to new managers.
- adapter events translating old static/event-heavy flows into new manager-owned flows.
- wrapper/facade classes preserving call sites while internals migrate.

Not allowed as final state:

- lazy runtime creation of gameplay managers
- duplicate intake/search/check-in implementations
- manager bypasses around canonical behavior classes
- gameplay decisions based on UI visibility

### Criteria for removing legacy paths

Legacy code paths can be removed when:

- all call sites use the new manager-owned API
- no runtime flow requires the compatibility facade
- test/build gates pass in IL2CPP and Mono
- ownership map accurately reflects live code
- the relevant phase section in this changelog has been updated to mark the migration complete

## Maintenance Notes

- The active manager-ownership refactor is complete at this checkpoint.
- Remaining work is limited to optional compatibility-surface retirement and warning cleanup, not unfinished runtime ownership migration.
- The final compatibility cleanup retired the obsolete `Core.GetJailSystem()`, `GetBailSystem()`, `GetCourtSystem()`, and `GetParoleSystem()` getters and narrowed `NpcManager.RefreshSceneBindings()` so it resolves the prison-NPC scene binding locally instead of looping through `Core.ResolvePrisonNpcManager()`.
- This file must be updated as each phase lands.
- Major ownership transfers must be recorded here before or alongside implementation.
- If a planned class split, removal, or rename changes during implementation, this document must be revised to reflect the actual migration path.
- `README.md` remains the player/developer overview; this file remains the architectural refactor record.
