# Modular Asset Spawning System Implementation

## Task Summary
Converted the asset spawning logic to properly handle multiple class types with a modular, extensible architecture. The system now supports generic spawning and makes it easy to add new asset types.

## Architecture Overview

### 1. Core Components

#### **ISpawnableAsset Interface**
- Base interface that all spawnable assets must implement
- Provides common properties: `IsInitialized`, `Position`
- Defines common methods: `GetDebugInfo()`, `DestroyAsset()`

#### **AssetSpawnConfig Class**
- Configuration container for asset spawning parameters
- Properties: SpawnPoint, PrefabPath, LayerName, ColliderSize, MaterialColor
- Centralized configuration management

#### **AssetFactory Static Class**
- Factory pattern implementation for creating different asset types
- Maintains registry of spawn configurations
- Provides methods to register new asset types
- Handles asset creation through type switching

#### **Generic SpawnAsset Methods**
- `SpawnAsset<T>(FurnitureType)` - Generic method returning typed assets
- `SpawnAsset(FurnitureType)` - Non-generic method returning ISpawnableAsset
- Both methods use the factory system for creation

### 2. Updated Classes

#### **BunkBed & ToiletSink Classes**
- Now implement `ISpawnableAsset` interface
- Added `DestroyAsset()` method for interface compliance
- Updated `Initialize()` method to accept optional collider size parameter
- Maintain backward compatibility with default values

#### **BunkBedManager & ToiletSinkManager**
- Updated to accept optional `AssetSpawnConfig` parameter
- Use configuration values when provided, fall back to defaults
- Maintain existing functionality while supporting new system

### 3. Key Features

#### **Modular Design**
- Easy to add new asset types without modifying existing code
- Configuration-driven spawning system
- Centralized asset management

#### **Type Safety**
- Generic methods provide compile-time type checking
- Interface ensures consistent behavior across asset types
- Proper error handling and logging

#### **Extensibility**
- Simple registration system for new asset types
- Configurable spawn points, prefab paths, and properties
- Support for custom materials and collider sizes

## Usage Examples

### **Spawning Assets**
```csharp
// Generic method (recommended)
var bunkBed = AssetManager.SpawnAsset<BunkBed>(FurnitureType.BunkBed);
var toiletSink = AssetManager.SpawnAsset<ToiletSink>(FurnitureType.ToiletSink);

// Non-generic method
ISpawnableAsset asset = AssetManager.SpawnAsset(FurnitureType.BunkBed);
```

### **Adding New Asset Types**
```csharp
// 1. Add to enum
public enum FurnitureType { BunkBed, ToiletSink, NewAsset }

// 2. Create asset class implementing ISpawnableAsset
public sealed class NewAsset : MonoBehaviour, ISpawnableAsset { ... }

// 3. Create manager class following existing pattern

// 4. Register the asset type
AssetManager.RegisterAssetType(FurnitureType.NewAsset, 
    new Vector3(0, 0, 0),           // Spawn point
    "Assets/Jail/NewAsset.prefab",   // Prefab path
    "New_Asset",                     // Layer name
    new Vector3(1, 1, 1),           // Collider size
    Color.white);                    // Material color

// 5. Add creation logic to AssetFactory.CreateAsset method

// 6. Use it
var asset = AssetManager.SpawnAsset<NewAsset>(FurnitureType.NewAsset);
```

### **Bulk Operations**
```csharp
// Spawn all registered assets
AssetManager.SpawnAllRegisteredAssets();

// Get list of registered types
var types = AssetManager.GetRegisteredAssetTypes();
```

## Configuration Management

### **Default Configurations**
- **BunkBed**: (-55, -1.5, -58), "Assets/Jail/BunkBed.prefab", "Bunk_Bed", (2,2,4), Gray
- **ToiletSink**: (-58, -1.0, -57), "Assets/Jail/ToiletSink.prefab", "Toilet_Sink", (1,1,1), Gray

### **Runtime Configuration**
- Configurations can be modified at runtime
- New asset types can be registered dynamically
- Existing configurations can be updated

## Benefits

1. **Maintainability**: Centralized configuration and factory pattern
2. **Extensibility**: Easy to add new asset types
3. **Type Safety**: Generic methods with compile-time checking
4. **Consistency**: All assets follow the same interface and patterns
5. **Flexibility**: Configurable spawn points, materials, and properties
6. **Reusability**: Common spawning logic shared across asset types

## Files Modified
1. `Systems/AssetManager.cs` - Complete modular system implementation
2. `Core.cs` - Updated to use generic SpawnAsset methods

## Future Enhancements
- Support for asset pooling
- Dynamic configuration loading from files
- Asset dependency management
- Performance optimization for bulk spawning
- Asset lifecycle management (spawn, despawn, respawn)
