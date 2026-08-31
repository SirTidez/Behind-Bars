# Repository Guidelines

## Branch Purpose
- This worktree is the IL2CPP-focused branch of Behind Bars.
- Treat IL2CPP as the primary runtime target; keep Mono compatibility via conditional compilation.
- Prefer patterns already used in this worktree over patterns from Mono-first branches.

## Project Structure & Module Organization
- Core gameplay systems live under `Systems/` (jail, parole, crime, dialogue, NPCs).
- UI components are in `UI/`.
- Harmony patches live in `Harmony/`.
- Player logic is in `Players/`.
- Utilities and runtime helpers are in `Utils/`.
- Manual/in-game test scaffolding is in `Tests/`.

## Build, Test, and Development Commands
- Open and build `Behind Bars.sln` in Visual Studio 2022+.
- CLI build example: `dotnet build "Behind Bars.sln" -c "Debug IL2CPP"`.
- Supported configurations: `Debug Mono`, `Release Mono`, `Debug IL2CPP`, `Release IL2CPP`.
- Validate runtime-specific changes in an IL2CPP game instance before merging.

## IL2CPP Runtime Rules
- Use compile-time guards for runtime splits: `#if MONO` for Mono-only paths, `#else` for IL2CPP paths.
- Avoid runtime reflection-heavy logic unless no direct API path exists.
- Use explicit Il2Cpp namespaces/types where needed (`Il2CppScheduleOne.*`, `Il2CppFishNet.*`, etc.).
- Prefer existing helper utilities (for example `Utils/EventHelper.cs`, `Utils/Helpers.cs`) before adding new interop wrappers.

## IL2CPP Behavior Parity Guardrails (Critical)
- IL2CPP behavior must match Mono for core NPC systems (guard, intake, release, parole, inmate) unless explicitly approved by the user.
- Do not treat static/fallback NPC behavior as a final fix. Temporary fallback is allowed only for crash triage and must be removed.
- If canonical behavior types fail IL2CPP injection/registration, treat this as a blocking defect and fix the type surface.

## IL2CPP Injection-Safe API Surface
- For IL2CPP-injected behavior classes, avoid exposing unsupported public/protected signatures:
  - `System.Action`/`System.Func` fields, events, parameters, or return types unless `#if MONO`.
  - Delegate-heavy generic APIs and runtime-only helper signatures unless hidden.
  - Known unstable array-based exposed method surfaces unless explicitly hidden from IL2CPP.
- Gate Mono-only signatures with `#if MONO`.
- Use `[HideFromIl2Cpp]` for helper methods that should not be part of IL2CPP exposed class surfaces.

## IL2CPP Component Access Rules
- Never call generic Unity APIs directly for mod-injected component types on IL2CPP (`AddComponent<T>`, `GetComponent<T>`, `FindObjectOfType<T>`, `FindObjectsOfType<T>`, `ScriptableObject.CreateInstance<T>`).
- Use `Utils/Helpers` wrappers and IL2CPP-safe type-based APIs for injected components.
- Verify IL2CPP type registration before first runtime component creation.

## NPC Runtime Parity Rules
- Guards, intake officers, release officers, parole officers, and inmates must run through their canonical behavior classes in final IL2CPP path.
- Do not bypass canonical escort/state-machine flows with manager-only teleport or direct-complete logic in final path.
- Do not silently downgrade Booking0/Booking1 behavior to static guards in final path.

## Registration and Startup Validation
- Register all NPC behavior and dependency types during startup before spawn/lookup.
- On registration failure, log full exception context for the type and treat as runtime-blocking for that feature.
- Validate startup logs for canonical NPC behavior types to ensure no "does not have a corresponding IL2CPP class pointer" errors.

## Regression Gates Before Completion
- Required IL2CPP in-game checks:
  1. Intake officer responds and runs full booking flow.
  2. Release officer spawns and completes full escort/release flow.
  3. Parole officer systems run canonical flow.
  4. Inmates run canonical behavior flow.
  5. No fallback/static guard logs in normal flow.
  6. No IL2CPP class pointer / MethodInfoStoreGeneric failures for canonical NPC behavior classes.
- Required build checks: `Debug IL2CPP` and `Debug Mono` must both compile.

## Events, Listeners, and Handler Safety
- Do not use anonymous listener lambdas when the listener must later be removed.
- Keep listener references stable so unsubscription works reliably across runtime boundaries.
- Prefer `Utils/EventHelper` add/remove wrappers for UnityEvent and generic UnityEvent listeners.
- Always pair subscription and unsubscription in lifecycle-safe locations (init/teardown, enable/disable, spawn/despawn).

## Components and Lifecycle (Mono/IL2CPP Differences)
- Be careful when adding runtime components; avoid duplicate `AddComponent` calls and guard with `GetComponent` checks.
- Validate object lifetime before callbacks fire (destroyed/null Unity objects can still hold stale delegates).
- Avoid assumptions that Mono behaviors transfer 1:1 to IL2CPP object casting or delegate wiring.

## Coding Style & Logging
- Use C# conventions: PascalCase for types/methods, camelCase for locals/fields.
- Add XML docs for public methods only when behavior is non-obvious.
- Use `ModLogger` consistently for diagnostics and runtime failure context.
- Keep diffs targeted; do not bundle unrelated refactors with runtime fixes.

## Testing Guidelines
- Testing is primarily manual/in-game in this repository.
- For event-handler changes, verify: subscribe once, no duplicate callbacks, clean unsubscribe, no post-destroy invocation.
- For runtime split changes, sanity-test both `Debug IL2CPP` and `Debug Mono` builds when practical.

## Commit & PR Guidelines
- Commit messages should be short and imperative.
- PR notes should call out IL2CPP-specific behavior changes and any Mono fallback logic.
- Mention impacted systems and include quick verification notes.
