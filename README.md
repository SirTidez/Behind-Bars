# Behind Bars - Schedule I Mod

## Overview

Behind Bars is a comprehensive mod for Schedule I that significantly expands the after-arrest experience. Instead of the basic arrest mechanics, players now face a complete criminal justice system including jail time, bail negotiations, court proceedings, and probation periods.

## Features

### 🚔 Jail System
- **Severity Assessment**: Crimes are automatically assessed for severity (Minor, Moderate, Major, Severe)
- **Dynamic Sentencing**: Jail time and fines scale based on crime severity and player level
- **Flexible Options**: Players can choose between paying fines or serving jail time
- **Realistic Scaling**: Higher-level players face stiffer penalties, barons can't escape with petty fines

### 💰 Bail System
- **Smart Calculation**: Bail amounts are 2.5x the fine amount, adjusted for player status
- **Negotiation Support**: Bail amounts can be negotiated based on player skills and evidence
- **Multiplayer Integration**: Friends can pay bail for arrested players in multiplayer sessions
- **Level-Appropriate Pricing**: Bail scales with player level and wealth status

### ⚖️ Court System
- **Courtroom Experience**: Full court session with judge, charges, and sentencing
- **Bail Negotiation**: Players can negotiate bail amounts using skills, evidence, and witnesses
- **Time-Limited Sessions**: 60-second negotiation windows add urgency and strategy
- **Deal-Based Mechanics**: Similar to pawn shop negotiations but in a legal context

### 👮 Probation System
- **Probation Officer NPC**: New NPC that patrols and conducts random searches
- **Random Body Searches**: Unpredictable search intervals (30-120 seconds)
- **Violation Tracking**: Multiple violations can extend probation or lead to revocation
- **Progressive Consequences**: Violations increase probation duration and severity

## Technical Implementation

### Architecture
- **Modular Design**: Each system (Jail, Bail, Court, Probation) is self-contained
- **Event-Driven**: Systems communicate through events and callbacks
- **Conditional Compilation**: Supports both Mono and IL2CPP versions using `#if !MONO` directives
- **Harmony Integration**: Uses Harmony for patching game systems

### Core Systems
1. **JailSystem**: Handles arrest processing, severity assessment, and jail mechanics
2. **BailSystem**: Manages bail calculations, payments, and multiplayer support
3. **CourtSystem**: Controls court sessions, negotiations, and sentencing
4. **ProbationSystem**: Manages probation periods, NPC spawning, and violation tracking

### Player Management
- **PlayerHandler**: Tracks individual player criminal records and status
- **CriminalRecord**: Comprehensive tracking of arrests, jail time, fines, and violations
- **Multiplayer Support**: Handles player interactions across network sessions

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
1. Enter the courtroom for bail negotiation
2. Use your skills and evidence to argue for lower bail
3. Time is limited - make your case quickly
4. Accept the judge's final decision

### Probation
1. Automatic after multiple arrests
2. Probation Officer will patrol and search you
3. Avoid violations to complete probation successfully
4. Violations extend probation or lead to revocation

## Development

### Project Structure
```
Behind Bars/
├── Core.cs                 # Main mod entry point
├── Systems/               # Core system implementations
│   ├── JailSystem.cs     # Jail mechanics
│   ├── BailSystem.cs     # Bail system
│   ├── CourtSystem.cs    # Court proceedings
│   └── ProbationSystem.cs # Probation mechanics
├── Players/               # Player management
│   └── PlayerHandler.cs  # Individual player tracking
├── Integrations/          # Game integrations
│   └── HarmonyPatches.cs # Harmony patching
└── Utils/                # Utilities and constants
    ├── Constants.cs      # Configuration constants
    ├── ModLogger.cs      # Logging utilities
    └── Helpers.cs        # Helper functions
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

### Phase 1 (Current)
- ✅ Basic jail system
- ✅ Bail calculation and payment
- ✅ Court session framework
- ✅ Probation system foundation

### Phase 2 (Planned)
- [ ] UI implementation for all systems
- [ ] Integration with game's money system
- [ ] Advanced crime severity detection
- [ ] Probation Officer AI improvements

### Phase 3 (Future)
- [ ] Additional NPC types (Lawyers, Judges)
- [ ] Evidence system for court cases
- [ ] Reputation system with law enforcement
- [ ] Community content support

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

### Development
- **Developer**: SirTidez
- **Game**: Schedule I by TVGS
- **Mod Loader**: MelonLoader
- **Harmony**: pardeike

### Assets

#### 3D Models & Prefabs
- **Bunk Bed**: Custom jail bunk bed design for cell accommodations
- **Toilet Sink**: Combined toilet and sink unit for jail cells
- **Common Room Table**: Multi-purpose table for jail common areas
- **Cell Table**: Compact table designed for individual jail cells

#### Materials & Textures
- **Jail Metal**: Primary metallic material for jail infrastructure
- **Jail Metal (Worn)**: Weathered variant for aged jail appearance
- **M_JailMetal**: Alternative metallic material variant

#### Asset Bundle
- **behind_bars.bundle**: Custom asset bundle containing all mod assets
- **Icon**: Mod icon and branding assets

### Third-Party Dependencies
- **MelonLoader**: Mod loading framework by LavaGang
- **SwapperPlugin**: Asset swapping functionality by the_croods

### Special Thanks
- **DropDaDeuce**: AssetBundleUtils implementation and general asset development assistance
- **spec**: Asset creation, modeling, and packaging for the jail environment

## Support

For issues, feature requests, or contributions:
- Create an issue on the project repository
- Join the community Discord
- Check the troubleshooting guide

---

**Note**: This mod modifies core game systems. Always backup your save files and test in a separate installation before using on your main game.
