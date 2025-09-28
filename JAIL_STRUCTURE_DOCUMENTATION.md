# Jail Structure Documentation

## Overview
This document details the complete jail structure hierarchy, including guard spawn points, interaction stations, door points, and the flow of prisoner processing through the facility.

## Jail GameObject Hierarchy

```
Jail
├── Cells
│   ├── Cell_00
│   │   ├── CellDoorHolder[0]
│   │   │   └── DoorPoint
│   │   ├── CellBounds[0]
│   │   ├── CellBedTop
│   │   └── CellBedBottom
│   ├── Cell_01
│   │   ├── CellDoorHolder[0]
│   │   │   └── DoorPoint
│   │   ├── CellBounds[0]
│   │   ├── CellBedTop
│   │   └── CellBedBottom
│   ├── [Cell_02 through Cell_35 with same structure]
│   └── HoldingCells
│       ├── HoldingCell_00
│       │   ├── HoldingDoorHolder[0]
│       │   │   └── DoorPoint
│       │   ├── HoldingCellBounds[0]
│       │   ├── HoldingCellSpawn[0]
│       │   ├── HoldingCellSpawn[1]
│       │   └── HoldingCellSpawn[2]
│       └── HoldingCell_01
│           ├── HoldingDoorHolder[1]
│           │   └── DoorPoint
│           ├── HoldingCellBounds[1]
│           ├── HoldingCellSpawn[0]
│           ├── HoldingCellSpawn[1]
│           └── HoldingCellSpawn[2]
├── Kitchen
├── Phones
├── Hallway
│   ├── Point Light
│   ├── ExitTrigger
│   ├── ExitDoor
│   ├── ExitScannerStation
│   │   ├── GuardPoint
│   │   ├── ScanTarget
│   │   ├── Interaction
│   │   ├── Draggable
│   │   │   └── IkTarget
│   │   │       └── MockHand
│   │   ├── Holder
│   │   │   └── Canvas
│   │   │       ├── imgScanEffect
│   │   │       ├── Start
│   │   │       └── End
│   │   ├── PalmScanner
│   │   └── InteractionCamera
│   ├── Bounds
│   └── Structure
├── Storage
│   ├── Storage_HallDoor
│   ├── GuardPoint
│   ├── Booking_StorageDoor
│   ├── Booking_StorageDoor
│   ├── Cubbies
│   ├── InventoryPickup
│   │   ├── Interaction
│   │   ├── PillowAndSheets
│   │   ├── BedRoll
│   │   ├── JailCup
│   │   └── JailToothBrush
│   ├── InventoryDropOff
│   │   ├── Interaction
│   │   ├── PossesionCubby
│   │   │   ├── CubbyTray
│   │   │   └── CubbyTrayLid
│   │   ├── Bounds
│   │   └── Desktop
│   ├── InventoryPickup
│   ├── Bounds
│   ├── Desktop
│   ├── EquipJailSuit
│   └── StorageWalls
├── Booking
│   ├── GuardSpawn[0]
│   ├── GuardSpawn[1]
│   ├── Booking_GuardDoor
│   │   ├── GuardRoomDoorTrigger_FromGuardRoom
│   │   ├── GuardRoomDoorTrigger_FromBooking
│   │   ├── DoorPoint_GuardRoom
│   │   └── DoorPoint_Booking
│   ├── Booking_InnerDoor
│   │   ├── BookingDoorTrigger_FromBooking
│   │   ├── BookingDoorTrigger_FromHall
│   │   ├── DoorPoint_Booking
│   │   └── DoorPoint_Hall
│   ├── Prison_EnterDoor
│   │   ├── PrisonDoorTrigger_FromPrison
│   │   ├── PrisonDoorTrigger_FromHall
│   │   ├── DoorPoint_Hall
│   │   └── DoorPoint_Prison
│   ├── MugshotStation
│   │   ├── MugshotMonitor
│   │   ├── MugshotBackDrop
│   │   ├── GuardPoint
│   │   ├── MugshotCamera
│   │   ├── StandingPoint
│   │   └── Interaction
│   └── ScannerStation
│       ├── GuardPoint
│       ├── ScanTarget
│       ├── Interaction
│       ├── Draggable
│       ├── Holder
│       └── PalmScanner
```

## Guard System Architecture

### Guard Spawn Points
- **GuardSpawn[0] (Booking Area)**: Becomes the **Intake Officer** - responsible for escorting prisoners through the booking process
- **GuardSpawn[1] (Booking Area)**: Becomes the **Booking Station Guard** - manages the computer/monitoring station
- **GuardSpawn[0] (Guard Room)**: Guard room stationary position
- **GuardSpawn[1] (Guard Room)**: Guard room stationary position

### Guard Points (Interaction Supervision)
Guard Points are specific positions where guards stand while supervising prisoner interactions:

1. **MugshotStation/GuardPoint**: Where the intake officer stands during mugshot capture
2. **ScannerStation/GuardPoint**: Where the intake officer stands during fingerprint scanning
3. **Storage/GuardPoint**: Where the intake officer stands during inventory drop-off/pickup
4. **ExitScannerStation/GuardPoint**: Where the release officer stands during final exit fingerprint scan

### Door Points (Safe Door Operation)
Door Points are positions where guards stand to safely operate doors without being hit by them:

1. **Booking_GuardDoor**:
   - `DoorPoint_GuardRoom`: Position on guard room side
   - `DoorPoint_Booking`: Position on booking area side

2. **Booking_InnerDoor**:
   - `DoorPoint_Booking`: Position on booking side
   - `DoorPoint_Hall`: Position on hallway side

3. **Prison_EnterDoor**:
   - `DoorPoint_Hall`: Position on hallway side
   - `DoorPoint_Prison`: Position on prison interior side

4. **HoldingCell Doors**:
   - Each holding cell has a `DoorPoint` within the `HoldingDoorHolder` for safe door operation

## Prisoner Processing Flow

### Phase 1: Holding Cell Extraction
1. **Intake Officer** (GuardSpawn[0] from Booking) approaches holding cell
2. Guard positions at `HoldingDoorHolder/DoorPoint`
3. Opens door and instructs prisoner: "Come with me"
4. Prisoner exits holding cell

### Phase 2: Mugshot Station
1. Guard leads prisoner to `MugshotStation`
2. Guard positions at `MugshotStation/GuardPoint`
3. Prisoner positions at `MugshotStation/StandingPoint`
4. Guard instructs: "Stand in front of the camera"
5. Station interaction enabled
6. Mugshot capture process occurs
7. Guard waits at GuardPoint during capture

### Phase 3: Scanner Station
1. Guard leads prisoner to `ScannerStation`
2. Guard positions at `ScannerStation/GuardPoint`
3. Guard instructs: "Place your hand on the scanner"
4. Station interaction enabled
5. Fingerprint scan process occurs
6. Guard supervises from GuardPoint

### Phase 4: Inventory Processing
Based on sentence length:

#### For Long Sentences (Prison Assignment):
1. Guard uses `Booking_InnerDoor` (via DoorPoints)
2. Leads prisoner to `Storage` area
3. Guard positions at `Storage/GuardPoint`

   **Inventory Drop-Off**:
   - Prisoner interacts with `InventoryDropOff/Interaction`
   - Personal items placed in `PossesionCubby`
   - Items stored in `CubbyTray` with `CubbyTrayLid`

   **Inventory Pick-Up**:
   - Prisoner interacts with `InventoryPickup/Interaction`
   - Receives prison items:
     - `PillowAndSheets`
     - `BedRoll`
     - `JailCup`
     - `JailToothBrush`

4. Guard leads prisoner to assigned cell (0-11)

#### For Short Sentences:
1. Guard returns prisoner to holding cell
2. No inventory processing required

## Release Process Flow

### Phase 1: Release Officer Assignment
1. **Release Officer** (GuardSpawn[1] from Booking) receives release order
2. Guard approaches prisoner cell or holding cell
3. Guard instructs: "Your release has been processed - follow me"

### Phase 2: Storage Area (Inventory Return)
1. Guard escorts prisoner to `Storage` area
2. Guard positions at `Storage/GuardPoint`
3. Guard instructs: "Collect your personal belongings"

   **Inventory Pick-Up**:
   - Prisoner interacts with `InventoryPickup/Interaction`
   - Legal personal items returned from `PossesionCubby`
   - Items retrieved from `CubbyTray`
   - Contraband items remain confiscated

### Phase 3: Exit Scanner Station
1. Guard escorts prisoner to `ExitScannerStation`
2. Guard positions at `ExitScannerStation/GuardPoint`
3. Guard instructs: "Final fingerprint scan required for release"

   **Exit Fingerprint Scan**:
   - Prisoner interacts with `ExitScannerStation/Interaction`
   - Palm scanning process identical to booking scanner
   - Uses same components: `ScanTarget`, `Draggable/IkTarget`, `PalmScanner`
   - Scan animation via `Holder/Canvas/imgScanEffect`
   - Guard supervises from `GuardPoint`

### Phase 4: Door Opening and Exit
1. Upon successful scan completion:
   - `ExitDoor` automatically opens
   - Guard steps aside
   - System shows: "Scan complete - proceed to exit"

2. **Exit Process**:
   - Prisoner walks to `ExitTrigger` area
   - Upon entering `ExitTrigger`, player is teleported to release location
   - System shows: "Release complete - you are free to go!"

### Phase 5: Release Completion
1. Player teleported to external release coordinates
2. All jail status flags cleared
3. Inventory access restored
4. ReleaseManager notified of completion
5. Release Officer returns to post

## Interaction Stations

### MugshotStation Components
- **StandingPoint**: Transform where prisoner stands for photo
- **MugshotCamera**: Camera component for capturing mugshot
- **MugshotMonitor**: Display for showing captured mugshot
- **MugshotBackDrop**: Background for mugshot photos
- **GuardPoint**: Guard supervision position
- **Interaction**: Player interaction component

### ScannerStation Components
- **ScanTarget**: Surface where palm is scanned
- **PalmScanner**: The scanning device itself
- **GuardPoint**: Guard supervision position
- **Interaction**: Player interaction component
- **Draggable**: Component for hand positioning
- **Holder**: Parent transform for scanner components

### Storage Area Components
- **InventoryDropOff**: Confiscation station
  - Personal belongings storage system
  - CubbyTray system for item organization
- **InventoryPickup**: Prison item distribution
  - Automated item addition to player inventory
  - Standard prison issue items
- **GuardPoint**: Supervision position for both interactions

### ExitScannerStation Components
- **ScanTarget**: Surface where palm is scanned (identical to booking scanner)
- **PalmScanner**: The scanning device itself
- **GuardPoint**: Guard supervision position for release officer
- **Interaction**: Player interaction component for exit scanning
- **Draggable**: Component for hand positioning during scan
  - **IkTarget**: IK target for hand placement
  - **MockHand**: Visual hand model for palm scanning
- **Holder**: Container for UI components
  - **Canvas**: UI canvas for scan effects
    - **imgScanEffect**: Moving scan line animation
    - **Start**: Start position for scan animation
    - **End**: End position for scan animation
- **InteractionCamera**: First-person camera view during scanning
- **ExitDoor**: Door that opens after successful scan
- **ExitTrigger**: Trigger area that teleports player to release location

## Guard Behavior Integration

### Intake Officer Responsibilities (GuardSpawn[0])
1. Retrieve prisoners from holding cells
2. Escort through booking stations
3. Supervise interactions at GuardPoints
4. Control doors using DoorPoints
5. Guide to storage for inventory processing
6. Deliver to assigned cell or return to holding

### Booking Station Guard (GuardSpawn[1])
1. Remain at booking computer station
2. Monitor booking process
3. Coordinate with intake officer
4. Manage booking records

### Release Officer Responsibilities (GuardSpawn[1] - Dual Role)
1. Process release orders from ReleaseManager
2. Escort prisoners from cells to storage area
3. Supervise inventory return at `Storage/GuardPoint`
4. Guide prisoners to `ExitScannerStation`
5. Supervise final fingerprint scan from `ExitScannerStation/GuardPoint`
6. Ensure proper door operation during release sequence
7. Return to booking station post after release completion

### Guard Room Guards
1. General facility monitoring
2. Backup for emergencies
3. Patrol coordination when not needed for intake/release

## Key Design Features

### Safety and Realism
- **DoorPoints** ensure guards don't get hit by opening/closing doors
- **GuardPoints** provide proper supervision positioning
- **Interaction enabling/disabling** based on guard presence

### Process Flow Control
- Guards must be at GuardPoints before stations activate
- Sequential processing enforced by guard escort
- Proper door control maintains security

### Inventory Management
- Clear separation between confiscation and distribution
- Automated item handling through interactions
- Guard supervision ensures compliance

## Implementation Notes

1. **Guard Assignment**:
   - GuardSpawn[0] in Booking is designated as the Intake Officer role
   - GuardSpawn[1] in Booking serves dual role as Booking Station Guard and Release Officer
2. **Station Activation**: All stations (Mugshot, Scanner, ExitScanner) should only be interactable when guard is at GuardPoint
3. **Door Sequencing**: Use DoorPoints for proper door operation timing, ExitDoor opens automatically after scan
4. **Escort Paths**: Guards should navigate between GuardPoints and DoorPoints
5. **State Management**: Track prisoner progress through booking and release phases
6. **Communication**: Guards provide contextual instructions at each step
7. **Release Integration**: ExitScannerStation integrates with ReleaseManager for complete release workflow
8. **Exit Trigger**: ExitTrigger provides seamless teleportation to release location after scan completion

This structure provides a complete framework for realistic prisoner processing with proper guard supervision, safe door operations, and controlled interaction sequences.