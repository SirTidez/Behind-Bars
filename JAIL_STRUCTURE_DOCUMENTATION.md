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
├── Storage
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
│   └── InventoryDropOff
│       ├── Interaction
│       ├── PossesionCubby
│       │   ├── CubbyTray
│       │   └── CubbyTrayLid
│       ├── Bounds
│       └── Desktop
├── EquipJailSuit
├── StorageWalls
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

### Guard Room Guards
1. General facility monitoring
2. Backup for emergencies
3. Patrol coordination when not needed for intake

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

1. **Guard Assignment**: GuardSpawn[0] in Booking is designated as the Intake Officer role
2. **Station Activation**: Stations should only be interactable when guard is at GuardPoint
3. **Door Sequencing**: Use DoorPoints for proper door operation timing
4. **Escort Paths**: Guards should navigate between GuardPoints and DoorPoints
5. **State Management**: Track prisoner progress through each phase
6. **Communication**: Guards provide contextual instructions at each step

This structure provides a complete framework for realistic prisoner processing with proper guard supervision, safe door operations, and controlled interaction sequences.