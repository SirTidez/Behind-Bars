# Behind Bars Mod - Development To-Do List

## Overview
This document outlines all development tasks for the Behind Bars mod, organized by system and priority. Based on the Trello board analysis, the mod aims to create a comprehensive criminal justice system for Schedule I.

## 🚔 Jail System

### Core Implementation
- [x] Create JailSystem class structure
- [x] Implement crime severity assessment (Minor, Moderate, Major, Severe)
- [x] Add dynamic jail time calculation based on severity and player level
- [x] Implement fine calculation system
- [x] Add player affordability checking for fines
- [x] Create jail sentence data structure

### Jail Mechanics
- [ ] **HIGH PRIORITY**: Implement actual jail cell mechanics
  - [ ] Design jail cell prefab or designate holding areas
  - [ ] Add jail cell spawning system
  - [ ] Implement player teleportation to jail
  - [ ] Add jail cell boundaries and escape prevention
  - [ ] Create jail cell environment (bars, furniture, etc.)

### Jail Time System
- [ ] **HIGH PRIORITY**: Implement actual time-based jail system
  - [ ] Add real-time countdown timer
  - [ ] Implement jail time acceleration (configurable)
  - [ ] Add early release options (good behavior, work programs)
  - [ ] Create jail time UI display
  - [ ] Add jail time persistence across game sessions

### Jail Activities
- [ ] **MEDIUM PRIORITY**: Add activities to pass time in jail
  - [ ] Implement jail work programs
  - [ ] Add exercise/fitness activities
  - [ ] Create social interactions with other inmates
  - [ ] Add educational programs
  - [ ] Implement jail yard time

## 💰 Bail System

### Core Implementation
- [x] Create BailSystem class structure
- [x] Implement bail amount calculation (2.5x fine amount)
- [x] Add player level scaling for bail amounts
- [x] Create bail offer data structure
- [x] Add negotiation range system (20% flexibility)

### Bail Mechanics
- [ ] **HIGH PRIORITY**: Implement actual bail payment system
  - [ ] Add bail payment UI
  - [ ] Implement money transfer from player to system
  - [ ] Add bail bond system (partial payment options)
  - [ ] Create bail forfeiture if player skips court
  - [ ] Add bail refund system for court appearances

### Multiplayer Support
- [ ] **MEDIUM PRIORITY**: Allow friends to pay bail
  - [ ] Implement friend bail payment requests
  - [ ] Add bail payment notifications
  - [ ] Create bail payment history tracking
  - [ ] Add bail payment confirmation system

## ⚖️ Court System

### Core Implementation
- [x] Create CourtSystem class structure
- [x] Implement court session phases (Initial, Negotiation, Final, Sentencing)
- [x] Add negotiation time limits (60 seconds)
- [x] Create court session data structure
- [x] Add bail negotiation mechanics

### Court Mechanics
- [ ] **HIGH PRIORITY**: Implement actual courtroom scene
  - [ ] Design courtroom prefab/environment
  - [ ] Add judge dialogue system
  - [ ] Implement prosecution/defense system
  - [ ] Create evidence presentation mechanics
  - [ ] Add witness testimony system

### Court Proceedings
- [ ] **MEDIUM PRIORITY**: Implement full court process
  - [ ] Add opening statements
  - [ ] Implement evidence examination
  - [ ] Create cross-examination system
  - [ ] Add jury deliberation (if applicable)
  - [ ] Implement verdict and sentencing

### Court Negotiation
- [ ] **MEDIUM PRIORITY**: Enhance bail negotiation
  - [ ] Add multiple negotiation rounds
  - [ ] Implement judge personality system
  - [ ] Create negotiation skill requirements
  - [ ] Add plea bargain options
  - [ ] Implement court costs and fees

## 👮 Parole / Probation System

> Status reconciled 2026-09-01. The runtime names this lifecycle parole; several
> older checklist items below were already implemented under parole-specific systems.

### Core Implementation
- [x] Create ProbationSystem class structure
- [x] Implement probation status tracking
- [x] Add probation duration system (10 minutes default)
- [x] Add violation tracking system

### Parole Mechanics
- [x] **HIGH PRIORITY**: Implement the core parole lifecycle
  - [ ] Replace direct random-search inventory scans with the native concealment search UI
  - [x] Create parole violation consequences
  - [x] Add idempotent parole completion rewards
  - [ ] Make roaming parole officers respond to witnessed crimes like law enforcement

### Parole Monitoring
- [x] **MEDIUM PRIORITY**: Establish parole monitoring foundations
  - [x] Add parole violation detection
  - [x] Implement parole completion tracking
  - [x] Create parole extension system
  - [x] Add parole violation consequences
  - [ ] Surface the next check-in window and outstanding fees consistently in UI/dialogue
  - [ ] Replace hidden instant home visits with announced visit windows

### Parole Activities
- [ ] **LOW PRIORITY**: Complete parole requirements
  - [ ] Implement community service
  - [ ] Finish time-windowed drug-use tracking and random check-in UAs
    - [x] Add saveable drug-use records and native-calendar expiration foundation
    - [ ] Record weed, methamphetamine, cocaine, and shroom consumption
    - [ ] Evaluate non-expired records during selected check-ins
  - [x] Create curfew system
  - [x] Implement employment condition foundation
  - [ ] Add counseling sessions
  - [ ] Add earned early discharge evaluation

## 🔧 Technical Infrastructure

### Core Systems
- [x] Create Core mod class with MelonLoader integration
- [x] Implement conditional compilation for Mono/IL2CPP
- [x] Add Harmony patching system integration
- [x] Create player event handling system
- [x] Implement system initialization and cleanup

### Player Management
- [x] Create PlayerHandler class
- [x] Implement arrest detection and handling
- [x] Add criminal record tracking system
- [x] Create arrest history management
- [x] Add probation status tracking

### Utilities and Helpers
- [x] Create Constants class with mod configuration
- [x] Implement ModLogger system
- [x] Add helper methods for common operations
- [x] Create extension methods for logging
- [x] Implement error handling utilities

### Build System
- [x] Set up project structure with .csproj
- [x] Configure build targets for Mono/IL2CPP
- [x] Add assembly references and dependencies
- [x] Implement conditional compilation directives
- [x] Create build scripts and automation

## 🎯 Testing and Quality Assurance

### Unit Testing
- [ ] **HIGH PRIORITY**: Create comprehensive test suite
  - [ ] Test jail system calculations
  - [ ] Test bail system logic
  - [ ] Test court system flow
  - [ ] Test probation system mechanics

### Integration Testing
- [ ] **MEDIUM PRIORITY**: Test system interactions
  - [ ] Test arrest → jail → bail → court flow
  - [ ] Test probation violation → jail flow
  - [ ] Test multiplayer synchronization
  - [ ] Test save/load system

### Performance Testing
- [ ] **LOW PRIORITY**: Optimize system performance
  - [ ] Test memory usage under load
  - [ ] Optimize system calls

## 📚 Documentation

### User Documentation
- [x] Create comprehensive README.md
- [x] Document all mod features and systems
- [x] Add installation and usage instructions
- [x] Create troubleshooting guide
- [x] Add configuration options

### Developer Documentation
- [x] Create TODO.md with development roadmap
- [x] Document system architecture
- [x] Add code comments and XML documentation
- [x] Create API reference
- [x] Document build and deployment process

## 🚀 Deployment and Distribution

### Mod Packaging
- [ ] **MEDIUM PRIORITY**: Create distribution package
  - [ ] Package mod for MelonLoader
  - [ ] Create installer script
  - [ ] Add version checking
  - [ ] Create update system
  - [ ] Add mod configuration options

### Community Integration
- [ ] **LOW PRIORITY**: Prepare for community release
  - [ ] Create mod showcase video
  - [ ] Write community announcement
  - [ ] Prepare bug report template
  - [ ] Create feature request system
  - [ ] Set up community support channels

## 📊 Progress Summary

### Completed Systems
- ✅ **Jail System**: Core implementation (5/8 tasks)
- ✅ **Bail System**: Core implementation (5/6 tasks)
- ✅ **Court System**: Core implementation (5/8 tasks)
- ✅ **Probation System**: Core implementation (5/8 tasks)
- ✅ **Technical Infrastructure**: Complete (15/15 tasks)
- ✅ **Documentation**: Complete (15/15 tasks)

### Overall Progress
- **Total Tasks**: 74
- **Completed**: 55 (74%)
- **Remaining**: 19 (26%)

### Priority Breakdown
- **HIGH PRIORITY**: 8 tasks remaining
- **MEDIUM PRIORITY**: 8 tasks remaining
- **LOW PRIORITY**: 3 tasks remaining

## 🎯 Next Steps

### Immediate Priorities (Next 2-4 weeks)
1. **Jail Cell Mechanics**: Implement actual jail cell spawning and player containment
2. **Jail Time System**: Add real-time countdown and time acceleration
3. **Bail Payment System**: Implement actual money transfer and payment UI
4. **Courtroom Scene**: Design and implement courtroom environment

### Medium Term Goals (Next 2-3 months)
1. **Complete Jail System**: Finish all jail mechanics and activities
2. **Complete Bail System**: Finish payment and multiplayer support
3. **Complete Court System**: Finish all court proceedings and mechanics
4. **Complete Probation System**: Finish probation monitoring and violation handling

### Long Term Vision (Next 6-12 months)
1. **Performance Optimization**: Optimize all systems for large-scale use
2. **Community Features**: Add mod sharing and collaboration tools
3. **Expansion Packs**: Add new crime types and justice system features

---

**Note**: This TODO list is dynamic and will be updated as development progresses. Priorities may shift based on community feedback and technical requirements.
