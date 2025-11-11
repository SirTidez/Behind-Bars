# Behind Bars - Schedule I Mod

## Overview

Behind Bars is a comprehensive mod for Schedule I that significantly expands the after-arrest experience. Instead of the basic arrest mechanics, players now face a complete criminal justice system including jail time, bail negotiations, court proceedings, and probation periods.

## Features

### 🚔 Jail System
- **Severity Assessment**: Crimes are automatically assessed for severity (Minor, Moderate, Major, Severe)
- **Dynamic Sentencing**: Jail time and fines scale based on crime severity and player level
- **Flexible Options**: Players can choose between paying fines or serving jail time
- **Realistic Scaling**: Higher-level players face stiffer penalties, barons can't escape with petty fines
- **Jail Facilities**: Complete jail infrastructure with cells, common rooms, booking areas, and guard rooms
- **Cell Assignment**: Automatic cell assignment system with proper cell management
- **Booking Process**: Full booking experience with mugshot stations and intake procedures
- **Inventory Management**: Secure inventory drop-off and pickup stations for personal belongings
- **Jail Time Tracking**: Real-time jail time countdown with game time conversion
- **Early Release**: Options for early release based on good behavior
- **Security Systems**: Security cameras, monitoring stations, and access control
- **Jail Doors**: Automated security doors with palm scanner integration
- **Lighting Control**: Dynamic lighting system for jail areas

### 💰 Bail System
- **Smart Calculation**: Bail amounts are 2.5x the fine amount, adjusted for player status
- **Negotiation Support**: Bail amounts can be negotiated based on player skills and evidence
- **Multiplayer Integration**: Friends can pay bail for arrested players in multiplayer sessions
- **Level-Appropriate Pricing**: Bail scales with player level and wealth status
- **Bail UI**: Comprehensive bail interface showing amounts and payment options
- **Dynamic Bail Updates**: Bail amounts update in real-time as jail time progresses

### ⚖️ Court System
- **Planned Feature**: Full court system implementation coming in Phase 3
- **Courtroom Experience**: Full court session with judge, charges, and sentencing (planned)
- **Bail Negotiation**: Players can negotiate bail amounts using skills, evidence, and witnesses (planned)
- **Time-Limited Sessions**: 60-second negotiation windows add urgency and strategy (planned)
- **Deal-Based Mechanics**: Similar to pawn shop negotiations but in a legal context (planned)

### 🔄 Parole System
- **Parole Supervision**: Active parole monitoring system with game time tracking
- **LSI Risk Assessment**: Level of Service Inventory (LSI) system for risk-based supervision
  - **Minimum Risk**: Low supervision, 10% search chance
  - **Medium Risk**: Moderate supervision, 30% search chance
  - **High Risk**: Intensive supervision, 50% search chance
  - **Severe Risk**: Maximum supervision, 70% search chance
- **Parole Officers**: Dynamic NPC system with multiple officer types
  - **Supervising Officers**: Stationary officers at police stations
  - **Patrol Officers**: Officers patrolling multiple city areas (Uptown, Westside, Docks, Northtown)
- **Random Contraband Searches**: Dynamic search system based on LSI level and proximity
  - **Detection Ranges**: Varies by LSI level (17m-40m detection radius)
  - **Search Cooldowns**: Prevents search spam (2-minute minimum between searches)
  - **Grace Period**: 30-minute grace period after release before searches begin
- **Violation Tracking**: Comprehensive violation system tracking contraband possession and other infractions
- **Parole Records**: Persistent parole records integrated with criminal history
- **Parole Status UI**: Real-time UI showing time remaining, supervision level, and violations
- **Parole Conditions UI**: Detailed UI showing parole conditions and requirements
- **Violation Consequences**: Violations extend parole duration and increase LSI level

### 🕵️ Crime Detection & Tracking
- **Crime Detection System**: Advanced crime detection for various offenses
- **Contraband Detection**: Automatic detection of illegal items during searches
- **Witness System**: Witness tracking and reporting for crimes
- **Crime Types**: Support for multiple crime types including:
  - Assault on Civilians
  - Drug Possession
  - Manslaughter
  - Murder
  - Witness Intimidation
- **Rap Sheet System**: Comprehensive criminal record tracking
  - **Crime Records**: Detailed records of all crimes committed
  - **Parole Records**: Complete parole history and status
  - **Violation Records**: Tracking of parole violations
  - **Persistent Storage**: All records saved across game sessions
- **Wanted Level UI**: Visual indicator of current wanted status

### 👮 NPC System
- **Jail Guards**: Multiple guard types with specialized roles
  - **Guard Room Guards**: Stationary guards monitoring guard room
  - **Booking Guards**: Guards handling intake procedures
  - **Intake Officers**: Dedicated officers for processing new arrests
  - **Patrol Guards**: Guards patrolling jail areas
- **Parole Officers**: Advanced parole officer system
  - **Supervising Officers**: Stationary supervisors at police stations
  - **Patrol Officers**: Officers patrolling city areas with preset routes
  - **Search Officers**: Officers conducting random contraband searches
- **Release Officers**: Officers handling release procedures
- **Inmates**: NPC inmates populating jail cells
- **NPC Behavior**: Advanced AI with state machines, patrol routes, and interactions
- **NPC Audio**: Voice commands and dialogue system for all NPCs
- **NPC Coordination**: Coordinated patrols and activities between NPCs

### 🖥️ User Interface
- **Jail Info UI**: Comprehensive jail information display
  - Real-time jail time countdown
  - Crime information display
  - Bail amount tracking
  - Dynamic updates as time progresses
- **Bail UI**: Dedicated bail interface for negotiations and payments
- **Parole Status UI**: Persistent UI showing parole supervision status
  - Time remaining display
  - LSI level and search probability
  - Violation count
- **Parole Conditions UI**: Detailed parole conditions display on release
- **Wanted Level UI**: Visual wanted level indicator
- **Officer Command UI**: Interface for officer commands and interactions

## Technical Implementation

### Architecture
- **Modular Design**: Each system (Jail, Bail, Court, Probation) is self-contained
- **Event-Driven**: Systems communicate through events and callbacks
- **Conditional Compilation**: Supports both Mono and IL2CPP versions using `#if !MONO` directives
- **Harmony Integration**: Uses Harmony for patching game systems

### Core Systems
1. **JailSystem**: Handles arrest processing, severity assessment, and jail mechanics
2. **BailSystem**: Manages bail calculations, payments, and multiplayer support
3. **ParoleSystem**: Manages parole periods, NPC spawning, and violation tracking
4. **CrimeDetectionSystem**: Detects and tracks crimes committed by players
5. **RapSheetManager**: Manages persistent criminal records and LSI assessments
6. **GameTimeManager**: Handles game time conversion and tracking
7. **CourtSystem**: (Planned for Phase 3) Controls court sessions, negotiations, and sentencing

### Player Management
- **PlayerHandler**: Tracks individual player criminal records and status
- **RapSheet**: Comprehensive criminal record with crime history, parole records, and LSI levels
- **PersistentPlayerData**: Persistent data storage across game sessions
- **Multiplayer Support**: Handles player interactions across network sessions

### Jail Infrastructure
- **JailController**: Central controller managing all jail systems
- **JailAreaManager**: Manages jail areas (cells, common room, booking, guard room)
- **CellAssignmentManager**: Handles cell assignment and management
- **JailTimeTracker**: Tracks jail time with game time conversion
- **ReleaseManager**: Manages release procedures and parole activation
- **InventoryProcessor**: Handles inventory drop-off and pickup
- **BookingProcess**: Manages booking procedures and mugshot capture

## Installation

### Prerequisites
- Schedule I (Steam)
- MelonLoader (latest version)
- .NET Framework 4.7.2 or higher

### Setup
1. Download the latest release
2. Extract to your Schedule I mods folder
3. Launch the game
4. The mod will automatically initialize

### Configuration
The mod includes extensive configuration options through MelonPreferences:
- Jail time scaling
- Bail multipliers
- Probation durations
- Search intervals
- Debug logging

## Usage

### Getting Arrested
1. Commit a crime in-game (the mod detects arrests automatically)
2. The system assesses crime severity
3. Choose between paying a fine or serving jail time
4. If you can't pay, you'll be sent to jail

### Bail Process
1. After arrest, bail amount is calculated
2. You can attempt to negotiate the amount
3. Pay bail to be released immediately
4. Friends can pay bail for you in multiplayer

### Court Sessions
1. *(Planned for Phase 3)* Enter the courtroom for bail negotiation
2. *(Planned for Phase 3)* Use your skills and evidence to argue for lower bail
3. *(Planned for Phase 3)* Time is limited - make your case quickly
4. *(Planned for Phase 3)* Accept the judge's final decision

### Probation
1. Automatic after multiple arrests
2. Probation Officer will patrol and search you
3. Avoid violations to complete probation successfully
4. Violations extend probation or lead to revocation

## Development

### Project Structure
```
Behind Bars/
├── Core.cs                        # Main mod entry point
├── Systems/                       # Core system implementations
│   ├── JailSystem.cs             # Jail mechanics
│   ├── BailSystem.cs             # Bail system
│   ├── CourtSystem.cs            # Court proceedings
│   ├── ParoleSystem.cs           # Parole mechanics
│   ├── ParoleTimeTracker.cs      # Parole time tracking
│   ├── GameTimeManager.cs        # Game time conversion
│   ├── Jail/                     # Jail infrastructure
│   │   ├── JailController.cs     # Central jail controller
│   │   ├── JailAreaManager.cs    # Area management
│   │   ├── CellAssignmentManager.cs
│   │   ├── JailTimeTracker.cs
│   │   ├── ReleaseManager.cs
│   │   ├── BookingProcess.cs
│   │   └── [many more...]
│   ├── NPCs/                     # NPC systems
│   │   ├── PrisonNPCManager.cs
│   │   ├── GuardBehavior.cs
│   │   ├── ParoleOfficerBehavior.cs
│   │   ├── ParoleSearchSystem.cs
│   │   └── [many more...]
│   ├── CrimeDetection/           # Crime detection
│   │   ├── CrimeDetectionSystem.cs
│   │   ├── ContrabandDetectionSystem.cs
│   │   └── WitnessSystem.cs
│   ├── CrimeTracking/            # Criminal records
│   │   ├── RapSheet.cs
│   │   ├── RapSheetManager.cs
│   │   ├── CrimeRecord.cs
│   │   └── ParoleRecord.cs
│   └── Data/
│       └── PersistentPlayerData.cs
├── Players/                       # Player management
│   └── PlayerHandler.cs          # Individual player tracking
├── UI/                           # User interfaces
│   ├── BehindBarsUIManager.cs
│   ├── BailUI.cs
│   ├── ParoleStatusUI.cs
│   ├── ParoleConditionsUI.cs
│   └── WantedLevelUI.cs
├── Harmony/                      # Game integrations
│   ├── HarmonyPatches.cs
│   └── StorageEntityPatch.cs
└── Utils/                        # Utilities and constants
    ├── Constants.cs              # Configuration constants
    ├── ModLogger.cs              # Logging utilities
    ├── Helpers.cs                # Helper functions
    └── AssetBundleUtils.cs      # Asset bundle utilities
```

### Building
The project supports multiple build configurations:
- **Debug Mono**: Development build for Mono version
- **Release Mono**: Production build for Mono version
- **Debug IL2CPP**: Development build for IL2CPP version
- **Release IL2CPP**: Production build for IL2CPP version

### Conditional Compilation
```csharp
#if MONO
using FishNet;
#else
using Il2CppFishNet;
#endif
```

## Roadmap

### Phase 1 (Completed) ✅
- ✅ Complete jail system with cells, booking, and infrastructure
- ✅ Bail calculation and payment system
- ✅ Parole system with LSI risk assessment
- ✅ Comprehensive UI implementation
- ✅ NPC system with guards and parole officers
- ✅ Crime detection and tracking system
- ✅ Rap sheet and persistent record system
- ✅ Inventory management system
- ✅ Security systems (cameras, doors, monitoring)

### Phase 2 (In Progress)
- 🔄 Advanced parole officer AI improvements
- 🔄 Additional crime types and detection
- 🔄 Performance optimizations

### Phase 3 (Planned)
- [ ] Court system implementation
  - [ ] Full court session framework
  - [ ] Courtroom experience with judge and charges
  - [ ] Enhanced bail negotiation in court
  - [ ] Time-limited court sessions
- [ ] Additional NPC types (Lawyers, Judges)
- [ ] Evidence system for court cases
- [ ] Reputation system with law enforcement
- [ ] Community content support
- [ ] Jail activities and work programs

## Contributing

### Development Setup
1. Clone the repository
2. Open in Visual Studio 2022 or later
3. Restore NuGet packages
4. Build for your target configuration

### Code Style
- Follow C# naming conventions
- Use XML documentation for public methods
- Implement proper error handling
- Add logging for debugging

### Testing
- Test on both Mono and IL2CPP versions
- Verify multiplayer functionality
- Test edge cases and error conditions

## Troubleshooting

### Common Issues
- **Mod not loading**: Check MelonLoader installation
- **Arrests not detected**: Verify game version compatibility
- **Performance issues**: Check debug logging settings

### Debug Mode
Enable debug logging in the mod configuration to see detailed system information and troubleshoot issues.

## License

This mod is provided as-is for educational and entertainment purposes. Use at your own risk.

## Credits

### Development Team
- **SirTidez**: Lead Developer
- **Dreous**: Development Team Member
- **spec**: Development Team Member - Asset creation, modeling, and packaging for the jail environment
- **Game**: Schedule I by TVGS
- **Mod Loader**: MelonLoader by LavaGang
- **Harmony**: pardeike

### Third-Party Dependencies
- **MelonLoader**: Mod loading framework by LavaGang
- **SwapperPlugin**: Asset swapping functionality by the_croods
- **assetville**: https://www.unrealengine.com/en-US/eula/content

### Special Thanks
- **DropDaDeuce**: AssetBundleUtils implementation and general asset development assistance

## Support

For issues, feature requests, or contributions:
- Create an issue on the project repository
- Join the community Discord
- Check the troubleshooting guide

---

**Note**: This mod modifies core game systems. Always backup your save files and test in a separate installation before using on your main game.
