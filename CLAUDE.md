# Claude Worktree Notes

This worktree is for the IL2CPP branch of Behind Bars.

## Runtime Priority
- Treat IL2CPP as the default runtime behavior target.
- Preserve Mono support using compile-time split paths.
- Prefer `#if MONO` for Mono-only code and `#else` for IL2CPP logic.

## IL2CPP Coding Practices
- Prefer direct Il2Cpp APIs and types over reflection-heavy approaches.
- Reuse existing helpers in `Utils/` before introducing new wrappers.
- Be explicit with runtime-specific namespaces (`Il2CppScheduleOne.*`, `Il2CppFishNet.*`, etc.).

## Event/Listener Safety
- Avoid anonymous lambdas for listeners that must be removed later.
- Keep delegate references stable so add/remove pairs map correctly.
- Use `Utils/EventHelper` for UnityEvent add/remove where possible.
- Pair every subscription with lifecycle-safe unsubscription.

## Component and Lifecycle Safety
- Guard runtime component adds with `GetComponent` checks to prevent duplicates.
- Validate object lifetime before invoking callbacks.
- Do not assume Mono casting/delegate behavior is identical under IL2CPP.

## Verification Expectations
- Build with `Debug IL2CPP` as the primary check.
- For runtime-split edits, also sanity-check `Debug Mono` when practical.
- Prefer small, targeted diffs focused on runtime safety and compatibility.
