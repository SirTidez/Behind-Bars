# Changelog

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
