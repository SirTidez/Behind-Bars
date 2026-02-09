# Changelog

## alpha-1.2.0
- **IL2CPP Jail/NPC Parity Pass**: Integrated the latest jail and parole IL2CPP fixes to preserve canonical NPC flows and improve runtime stability
  - Expanded IL2CPP-safe handling across jail processing, NPC coordination, spawn/behavior paths, and supporting utilities
  - Added post-release compliance flow updates and parole officer runtime fixes
- **Crime and Witness Reliability**: Reworked witness/crime handling to prioritize Behind Bars tracking while preventing duplicate crime entries
  - Added mod-managed assault registration with mirrored native crime suppression to avoid double counting in wanted/crime UI
  - Excluded mod law-enforcement officers from civilian witness behavior and routed officer assaults to immediate re-arrest with an added Assault charge
  - Suppressed stale/delayed witness police calls once arrest/jail flow is already in progress
- **Post-Release Dialogue/Voice Fixes**: Fixed post-release dialogue flow and jail voice database creation for IL2CPP paths

## alpha-1.1.0
- **Security Camera Performance**: Cameras now only render when you are close enough and can actually see a monitor screen
  - Added monitor face visibility checks and tightened the activation range to 10m
  - Disabled all cameras when no monitors are visible and forced unmapped cameras off
- **Palm Scanner UX**: Restored auto-complete behavior for palm scans
- **NPC Lookup Optimization**: Improved NPC lookups using NPCRegistryHelper for better performance

## alpha-1.0.8
- **Advanced Performance Optimizations**: Additional performance improvements building on alpha-1.0.7
  - **NavMesh Caching System**: Implemented comprehensive caching for NavMesh pathfinding operations
    - Added caching for `NavMeshUtility.GetReachableAccessPoint` with time-to-live (TTL) and position-based cache invalidation
    - Integrated path caching for `NPCMovement.CanGetTo` using existing PathCache system
    - Reduced redundant pathfinding calculations by reusing cached results when NPC position hasn't changed significantly
  - **Employee Update Throttling**: Throttled `Employee.UpdateBehaviour` calls from every frame to 1.5-second intervals, significantly reducing CPU usage for employee NPCs
  - **Event-Driven NPC Architecture**: Migrated NPC update system from per-frame Update() calls to event-driven architecture
    - BaseJailNPC now uses NPCUpdateManager for throttled, event-driven state updates
    - Improved performance by batching NPC updates instead of processing every frame
  - **Event-Driven Player Tracking**: Replaced coroutine-based player location tracking with event-driven system
    - PlayerLocationTracker now uses event-driven architecture for better performance and responsiveness
    - Reduced overhead from continuous coroutine execution
  - **NPC System Performance**: Additional optimizations in NPC behavior systems
    - Optimized patrol point movement checks in ParoleOfficerBehavior
    - Improved dialogue lookup caching in ReleaseOfficerBehavior
    - Enhanced destination update logic to reduce redundant path calculations

## alpha-1.0.7
- **Performance Optimizations**: Comprehensive performance improvements across multiple systems
  - **NavMesh Optimization**: Optimized NavMesh operations and improved jail scanner interaction efficiency
  - **Jail System Performance**: Enhanced jail systems performance with reduced allocations and improved processing
  - **NPC Performance**: Improved NPC patrol and search performance, optimized update loops for better frame rates
  - **UI Performance**: Optimized UI update loops to reduce overhead and improve responsiveness
  - **Memory Management**: Improved asset bundle stream disposal and memory usage patterns

## alpha-1.0.6
- **Event-Driven Status Updates**: Implemented event-driven system for jail and parole status updates, improving real-time UI responsiveness and system synchronization
- **Release Escort Improvements**: Refactored release escort system to use dedicated prison door state tracking for more reliable release processes
- **Parole Check-In and Intake Systems**: Added comprehensive parole check-in and intake systems for supervising officers, enhancing parole officer interactions
- **Dialogue System Integration**: Integrated dialogue system with improved jail time tracking for better player-NPC interactions
- **Bug Fixes**: 
  - Fixed release error notification display issues
  - Resolved stuck release cleanup error messages
- **Testing Improvements**: Updated jail managed testing keybinds to require Left Alt modifier for safer testing

## alpha-1.0.5
- **Save System Overhaul**: Migrated save system from UserDataDirectory to game save folders and fixed critical save/load issues
  - **Migration to Game Save Folders**: Changed from saving to MelonEnvironment.UserDataDirectory to game's save folder structure (Modded/Saveables/) for proper integration with game saves
    - Mod data now saves within game save folders, ensuring data travels with save backups/transfers
    - Added whitelisting of Modded paths in SaveManager to prevent cleanup deletion
  - **Fixed RapSheet saving**: Added GetAllRapSheets() method to RapSheetManager and updated SaveablePatches to save per-player saveables (RapSheets) that were excluded from auto-discovery
  - **Fixed RapSheet loading**: Corrected initialization order to prevent OnLoaded() from being called before LoadInternal(), which was overwriting loaded data
  - **Fixed ParoleRecord serialization**: Updated SaveInternal and LoadInternal to properly detect and serialize/deserialize nested objects with SaveableField attributes (like ParoleRecord) using SaveableSerializer instead of standard JsonConvert
  - Added comprehensive debug logging throughout save/load process for better troubleshooting
  - Made SaveableSerializer.SerializeValue() and DeserializeValue() public to support nested object serialization

## alpha-1.0.2
- **Jail Status Tracking Improvements**: Added explicit jail status tracking to JailTimeTracker for more accurate UI and logic separation from sentence tracking
- **Crime Type Mapping**: Enhanced FineCalculator to better map crime descriptions to type names for fine lookup
- **UI Updates**: Updated UI and systems to use new jail status checks, removed delayed parole UI logic, and improved cell assignment debug logging
- **Asset Bundle Loading**: Refactored UI asset bundle loading with retry logic for better reliability
  - Moved asset bundle loading before UI manager initialization in Core.cs
  - Added retry logic for UI prefab loading in BehindBarsUIManager
- **Project Cleanup**: Updated .gitignore for new asset and config paths

## alpha-1.0.1
- **Logging Improvements**: Added configurable debug logging option (disabled by default)
  - Users can now enable detailed debug logs via mod configuration if experiencing issues
  - Significantly reduced log spam during initialization and gameplay
  - Converted verbose initialization logs to debug level:
    - NPC spawning and appearance setup logs
    - Security door resolution logs
    - Jail component initialization (cells, beds, booking stations, etc.)
    - UI component setup logs
    - NavMesh and area system initialization logs
    - Parole officer appearance and behavior logs
    - Release manager and booking process logs
  - Only essential information, warnings, and errors are shown by default

## 1.0.0
- Implemented base mod logic
