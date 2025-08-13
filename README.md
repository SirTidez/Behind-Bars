# Behind Bars
**An expansion to the police system in Schedule 1**

## Overview
**Behind Bars** is a gameplay expansion mod designed to enhance the law enforcement mechanics in *Schedule 1*. The goal is to make arrests, jail time, bail, court proceedings, and post-incarceration systems more immersive and interactive.  

This mod introduces:
- Arrest consequences beyond a simple fine.
- A functioning bail and court negotiation system.
- A jail mechanic with multiple implementation options.
- Probation and parole systems with NPC oversight.

---

## Planned Features

### 1. Jail System
- **Trigger**: Activated upon player arrest if the severity of the charges warrants jail time.
- **Assessment**: Charge severity is evaluated at the time of arrest to determine if jail is necessary.
- **Implementation Options**:
  1. **Prefab Jail Cell** – Player is teleported to an actual cell in the game world.
  2. **UI Lock** – Player is frozen in place with a UI notification stating they are in jail.
- **Release**: Players can pay a fine or serve time.
- **Checklist**:
  - [ ] Create jail prefab or designate an out-of-bounds holding area.

---

### 2. Bail System
- **Dynamic Bail Amount**:
  - Based on **charge severity** and **player level**.
  - Prevents unrealistic situations (e.g., high-level players paying trivial bail).
- **Failure to Pay**:
  - Player is remanded to jail.
- **Multiplayer Support**:
  - Friends can visit the **court house** or **police station** to post bail for an incarcerated player.

---

### 3. Court-House Negotiating
- **Court Scene**:
  - Occurs immediately after arrest.
  - Player can negotiate bail terms using a system similar to the pawn shop’s deal mechanics.
- **Roleplay Element**:
  - Encourages player skill and interaction over fixed pricing.

---

### 4. Probation / Parole
- **Post-Release Monitoring**:
  - Adds an NPC called **Probation Officer** (modeled after standard police NPCs).
  - This NPC performs **random body searches** throughout the player’s probation term.
- **Checklist**:
  - [ ] Create Probation Officer NPC.

---

## Technical Approach
- **Game Systems Integration**: Hooks into existing arrest triggers within *Schedule 1*.
- **UI & Prefab Assets**:
  - Custom jail cells or UI overlays for incarceration scenes.
  - New NPC model/behavior scripts for the Probation Officer.
- **Networking**:
  - Multiplayer bail posting via server-synced transactions.
- **Balance**:
  - Bail and fine scaling formulas based on charge severity and player progression.

---

## Installation
1. Download the latest release from the [mod page](#).
2. Place the mod folder into your *Schedule 1* `Mods` directory.
3. Launch the game with your mod loader enabled.

---

## Future Plans
- **Expanded Court Cases**: Introduce randomized judge personalities and outcomes.
- **Prison Labor**: Optional minigames during jail time to reduce sentence.
- **Rehabilitation Programs**: Missions or events during probation to clear record.
- **Reputation Impact**: Criminal history affecting NPC interactions and prices.

---

## Credits
- **Concept & Design**: Tyler Ludka  
- **Development**: IfBars & Contributors  
- **Game**: Schedule 1
