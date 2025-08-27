# Bunk Bed Implementation Update

## Task Summary
Updated the bunk bed logic to match the toiletsink implementation, including manager and actual class itself. Changed the spawn point to be -55, -1.5, -58 and added the spawn logic to the core class just as the toiletsink logic is implemented.

## Changes Made

### 1. AssetManager.cs Updates
- **BunkBedManager Class**: Completely rewritten to match ToiletSinkManager pattern
  - Added singleton pattern with Instance property
  - Implemented CreateBunkBed, RemoveBunkBed, ClearAllBunkBeds methods
  - Added management methods: GetAllBunkBeds, GetBunkBedCount
  - Implemented SpawnBunkBed method with full prefab loading and material application
  - Added ForceObjectVisibility and SetLayerRecursively helper methods

- **BunkBed Class**: Completely rewritten to match ToiletSink class pattern
  - Added proper initialization with spawn location and material
  - Implemented component management (MeshRenderer, BoxCollider)
  - Added methods: SetPosition, SetMaterial, DestroyBunkBed, GetDebugInfo
  - Proper cleanup in OnDestroy method

- **SpawnAsset Method**: Updated to use BunkBedManager.CreateBunkBed
  - Changed spawn point to Vector3(-55f, -1.5f, -58f)
  - Uses "Assets/Jail/BunkBed.prefab" as the prefab string

- **TestBunkBedSystem Method**: Added to test bunk bed spawning functionality

### 2. Core.cs Updates
- **IL2CPP Registration**: Added BunkBed and BunkBedManager to ClassInjector
- **Scene Initialization**: Added bunk bed spawning alongside toilet sink spawning
- **TestBunkBedSystem Method**: Added to test bunk bed system functionality

## Key Features Implemented
- **Singleton Manager Pattern**: BunkBedManager follows same pattern as ToiletSinkManager
- **Prefab Loading**: Loads from "Assets/Jail/BunkBed.prefab" in asset bundle
- **Material Management**: Creates and applies materials using AssetManager.CreateMaterial
- **Component Management**: Ensures MeshRenderer, MeshFilter, and BoxCollider exist
- **Layer Management**: Sets layer to "Bunk_Bed" for proper rendering
- **Parenting**: Parents to "Map" GameObject for proper scene hierarchy
- **Error Handling**: Comprehensive error handling and logging throughout

## Spawn Coordinates
- **Bunk Bed**: (-55, -1.5, -58)
- **Toilet Sink**: (-58, -1.0, -57) (unchanged)

## Conditional Compilation
- Uses `#if !MONO` statements for IL2CPP vs Mono versions
- Proper constructor syntax for both platforms
- IL2CPP type registration in Core class

## Testing
- Added test methods in both AssetManager and Core classes
- Automatic spawning on scene initialization
- Debug logging for troubleshooting

## Files Modified
1. `Systems/AssetManager.cs` - Complete bunk bed system implementation
2. `Core.cs` - Added bunk bed spawning and testing logic

## Notes
- No changes made to existing toiletsink logic
- Bunk bed system follows exact same pattern as toiletsink system
- Collider size set to Vector3(2f, 2f, 4f) for bunk bed dimensions
- Material color set to slightly gray (0.7f, 0.7f, 0.7f, 1.0f)
