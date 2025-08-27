# CommonRoomTable and CellTable Implementation

## Task Summary
Successfully created two new spawnable assets: `CommonRoomTable` and `CellTable`, following the same architectural pattern as the existing `ToiletSink` and `BunkBed` classes. Both assets include complete manager classes and spawning logic with proper separation distances.

## Implementation Details

### 1. New Asset Types Added

#### **FurnitureType Enum**
- Added `CommonRoomTable` and `CellTable` to the `FurnitureType` enum
- Maintains consistency with existing asset types

#### **Spawn Configurations**
- **CommonRoomTable**: Position (-60, -2.5, -55), Size (3, 1, 2), Brown wood color
- **CellTable**: Position (-62, -2.5, -52), Size (2, 1, 1.5), Dark brown color
- Ensures minimum 2f separation between all assets

### 2. Manager Classes

#### **CommonRoomTableManager**
- Singleton pattern implementation
- Static methods for creation, removal, and management
- Comprehensive error handling and logging
- Asset tracking and lifecycle management

#### **CellTableManager**
- Identical structure to CommonRoomTableManager
- Separate instance management for cell tables
- Consistent API with other manager classes

### 3. Asset Classes

#### **CommonRoomTable**
- Implements `ISpawnableAsset` interface
- Full component management (MeshRenderer, BoxCollider)
- Material application and position management
- Proper cleanup and destruction handling

#### **CellTable**
- Same structure as CommonRoomTable
- Optimized for cell environments
- Consistent behavior with other assets

### 4. Spawning Logic

#### **AssetFactory Integration**
- Added creation logic to `CreateAsset` method
- Proper type switching for new asset types
- Configuration-driven spawning system

#### **Scene Initialization**
- Automatic spawning on Main scene load
- Generic method usage for type safety
- Comprehensive logging and error handling

### 5. IL2CPP Support

#### **Class Registration**
- Added `ClassInjector.RegisterTypeInIl2CPP` calls for all new classes
- Conditional compilation for Mono/IL2CPP versions
- Proper namespace handling

## Technical Features

### **Asset Separation**
- BunkBed: (-55, -2.5, -58)
- ToiletSink: (-58, -2.5, -57) 
- CommonRoomTable: (-60, -2.5, -55)
- CellTable: (-62, -2.5, -52)
- Minimum 2f separation maintained between all assets

### **Material System**
- CommonRoomTable: Brown wood texture (0.6, 0.4, 0.2)
- CellTable: Dark brown texture (0.4, 0.3, 0.2)
- Automatic material creation and application
- Support for custom material colors via configuration

### **Collider Management**
- CommonRoomTable: 3x1x2 collider size
- CellTable: 2x1x1.5 collider size
- Configurable collider dimensions
- Proper physics interaction setup

## Usage Examples

### **Manual Spawning**
```csharp
// Spawn using generic methods
var commonTable = AssetManager.SpawnAsset<CommonRoomTable>(FurnitureType.CommonRoomTable);
var cellTable = AssetManager.SpawnAsset<CellTable>(FurnitureType.CellTable);

// Spawn using non-generic methods
ISpawnableAsset asset = AssetManager.SpawnAsset(FurnitureType.CommonRoomTable);
```

### **Manager Operations**
```csharp
// Get counts
int commonCount = CommonRoomTableManager.GetCommonRoomTableCount();
int cellCount = CellTableManager.GetCellTableCount();

// Clear all assets
CommonRoomTableManager.ClearAllCommonRoomTables();
CellTableManager.ClearAllCellTables();
```

## Files Modified

1. **Systems/AssetManager.cs**
   - Added new furniture types to enum
   - Added spawn configurations
   - Implemented CommonRoomTableManager and CommonRoomTable classes
   - Implemented CellTableManager and CellTable classes
   - Updated AssetFactory.CreateAsset method

2. **Core.cs**
   - Added IL2CPP class registration
   - Added automatic spawning logic
   - Integrated with scene initialization system

## Benefits

1. **Consistency**: Follows established architectural patterns
2. **Extensibility**: Easy to add more table types in the future
3. **Maintainability**: Centralized configuration and management
4. **Performance**: Efficient asset tracking and lifecycle management
5. **Debugging**: Comprehensive logging and error handling
6. **Separation**: Proper asset spacing prevents overlap issues

## Future Enhancements

- Support for table-specific interactions (e.g., placing items)
- Dynamic table arrangement based on room layout
- Table material customization at runtime
- Integration with jail cell generation system
- Support for table animations or effects

## Testing Notes

- Assets spawn automatically on Main scene load
- Proper separation distances maintained
- Materials apply correctly to all renderers
- Colliders function as expected
- Manager classes track asset counts accurately
- Cleanup and destruction work properly
