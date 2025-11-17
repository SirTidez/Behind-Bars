# Changelog

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
