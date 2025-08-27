# AssetManager CreateMaterial Methods - Memory Reference

## Overview
Added two `CreateMaterial` methods to `AssetManager` class for creating URP materials with comprehensive property settings.

## Method 1: CreateMaterial(Color color)
- **Purpose**: Creates material with base color only
- **Shader**: Universal Render Pipeline/Lit
- **Properties Set**:
  - `_BaseColor`: Input color parameter
  - `_Metallic`: 0.0f (non-metallic)
  - `_Smoothness`: 0.5f (medium smoothness)
  - `_Surface`: 0.0f (opaque)
  - `_Cull`: 2.0f (back face culling)
  - `_AlphaClip`: 0.0f (disabled)

## Method 2: CreateMaterial(Texture2D texture, Color color)
- **Purpose**: Creates material with texture and tint color
- **Shader**: Universal Render Pipeline/Lit
- **Properties Set**:
  - `_BaseMap`: Input texture parameter
  - `_BaseColor`: Input color parameter (acts as tint)
  - `_Metallic`: 0.7f (slightly metallic)
  - `_Smoothness`: 0.5f (medium smoothness)
  - `_Surface`: 0.0f (opaque)
  - `_Cull`: 2.0f (back face culling)
  - `_AlphaClip`: 0.0f (disabled)

## Key Features
- **Property Safety**: All property sets wrapped in `material.HasProperty()` checks
- **Error Handling**: Try-catch blocks with ModLogger integration
- **Space Efficient**: Single-line if statements for property checks
- **URP Compatible**: Uses correct URP shader properties (no legacy `_Glossiness`)
- **Performance Optimized**: Back face culling for wall-mounted assets

## Technical Details
- **Cull Value 2**: Back face culling (hides faces pressed against walls)
- **AlphaClip 0**: No alpha clipping (solid objects)
- **Surface 0**: Opaque rendering (no transparency)
- **Metallic Range**: 0.0f (non-metallic) to 0.7f (slightly metallic)
- **Smoothness**: 0.5f (balanced roughness/smoothness)

## Usage Context
- **Jail Furniture**: Toilet sinks, bunk beds, other solid objects
- **Wall Mounting**: Assets pressed against walls benefit from back face culling
- **Performance**: Optimized for multiple furniture instances
- **Compatibility**: Works with both Mono and Il2Cpp versions

## File Location
`Systems/AssetManager.cs` - Added after `Unload()` method, before nested classes
