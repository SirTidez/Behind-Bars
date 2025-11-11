# Parole Officer Random Search Integration Plan

## Executive Summary
This plan outlines the integration of random contraband searches into the parole officer patrol behavior system, using LSI (Level of Service Inventory) risk assessment levels to determine search frequency and intensity.

---

## 1. LSI Supervision Level System

### 1.1 LSI Enum Definition

**File Location:** `Systems/CrimeTracking/RapSheet.cs`

```csharp
/// <summary>
/// Level of Service Inventory (LSI) - Risk assessment level for parolees
/// Determines supervision intensity and search frequency
/// </summary>
[Serializable]
public enum LSILevel : int
{
    /// <summary>
    /// No LSI assessment recorded (default state)
    /// Search chance: 0% (not on parole)
    /// </summary>
    None = 0,

    /// <summary>
    /// Minimum risk - Low supervision requirements
    /// Search chance: 10% per patrol encounter
    /// Check-in frequency: Once per week
    /// </summary>
    Minimum = 1,

    /// <summary>
    /// Medium risk - Moderate supervision requirements
    /// Search chance: 30% per patrol encounter
    /// Check-in frequency: Twice per week
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High risk - Intensive supervision requirements
    /// Search chance: 60% per patrol encounter
    /// Check-in frequency: Every other day
    /// </summary>
    High = 3,

    /// <summary>
    /// Severe risk - Maximum supervision requirements
    /// Search chance: 90% per patrol encounter
    /// Check-in frequency: Daily
    /// Electronic monitoring recommended
    /// </summary>
    Severe = 4
}
```

### 1.2 RapSheet Integration

**Modifications to RapSheet.cs:**

```csharp
[Serializable]
public class RapSheet
{
    // ... existing fields ...

    /// <summary>
    /// LSI risk assessment level - determines supervision intensity
    /// </summary>
    [JsonProperty("lsiLevel")]
    public LSILevel LSILevel = LSILevel.None;

    /// <summary>
    /// Last LSI assessment date
    /// </summary>
    [JsonProperty("lastLSIAssessment")]
    public DateTime LastLSIAssessment = DateTime.MinValue;

    // ... existing methods ...

    /// <summary>
    /// Calculate and assign LSI level based on rap sheet data
    /// </summary>
    public LSILevel CalculateLSILevel()
    {
        if (CrimesCommited == null || CrimesCommited.Count == 0)
            return LSILevel.Minimum;

        int score = 0;

        // Factor 1: Number of crimes (0-20 points)
        score += Math.Min(CrimesCommited.Count * 2, 20);

        // Factor 2: Crime severity (0-30 points)
        float avgSeverity = 0f;
        foreach (var crime in CrimesCommited)
        {
            avgSeverity += crime.Severity;
        }
        avgSeverity /= CrimesCommited.Count;
        score += (int)(avgSeverity * 10);

        // Factor 3: Parole violations (0-30 points)
        if (CurrentParoleRecord != null)
        {
            score += Math.Min(CurrentParoleRecord.GetViolationCount() * 5, 30);
        }

        // Factor 4: Past parole failures (0-20 points)
        if (PastParoleRecords != null)
        {
            score += Math.Min(PastParoleRecords.Count * 10, 20);
        }

        // Determine LSI level based on score
        // Total possible: 100 points
        if (score < 20) return LSILevel.Minimum;      // 0-19
        if (score < 40) return LSILevel.Medium;       // 20-39
        if (score < 70) return LSILevel.High;         // 40-69
        return LSILevel.Severe;                       // 70+
    }

    /// <summary>
    /// Update LSI level based on current criminal history
    /// </summary>
    public void UpdateLSILevel()
    {
        LSILevel = CalculateLSILevel();
        LastLSIAssessment = DateTime.Now;
        ModLogger.Info($"Updated LSI level for {FullName}: {LSILevel}");
        SaveRapSheet();
    }

    /// <summary>
    /// Get search probability based on LSI level
    /// </summary>
    public float GetSearchProbability()
    {
        switch (LSILevel)
        {
            case LSILevel.None:
                return 0.0f;        // 0%
            case LSILevel.Minimum:
                return 0.10f;       // 10%
            case LSILevel.Medium:
                return 0.30f;       // 30%
            case LSILevel.High:
                return 0.60f;       // 60%
            case LSILevel.Severe:
                return 0.90f;       // 90%
            default:
                return 0.0f;
        }
    }
}
```

---

## 2. Random Search System

### 2.1 Search Trigger System

**New File:** `Systems/NPCs/ParoleSearchSystem.cs`

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Handles random contraband searches by parole officers
    /// Integrates with patrol system and LSI risk assessment
    /// </summary>
    public class ParoleSearchSystem
    {
        private static ParoleSearchSystem _instance;
        public static ParoleSearchSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ParoleSearchSystem();
                }
                return _instance;
            }
        }

        // Search cooldowns per player to prevent spam
        private Dictionary<Player, float> lastSearchTime = new Dictionary<Player, float>();
        private const float SEARCH_COOLDOWN = 120f; // 2 minutes minimum between searches

        // Detection range for officers to notice parolees
        private const float DETECTION_RANGE = 15f;

        /// <summary>
        /// Check if a parole officer should initiate a random search
        /// Called from patrol logic when officer is near a player
        /// </summary>
        public bool ShouldInitiateSearch(ParoleOfficerBehavior officer, Player player)
        {
            // Pre-checks
            if (officer == null || player == null) return false;
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.ProcessingIntake) return false;
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.EscortingParolee) return false;

            // Check distance
            float distance = Vector3.Distance(officer.transform.position, player.transform.position);
            if (distance > DETECTION_RANGE) return false;

            // Check search cooldown
            if (lastSearchTime.ContainsKey(player))
            {
                if (Time.time - lastSearchTime[player] < SEARCH_COOLDOWN)
                {
                    return false;
                }
            }

            // Get player's rap sheet
            var rapSheetManager = Core.Instance?.RapSheetManager;
            if (rapSheetManager == null) return false;

            var rapSheet = rapSheetManager.GetRapSheet(player);
            if (rapSheet == null) return false;

            // Only search players on parole
            if (rapSheet.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
            {
                return false;
            }

            // Get search probability based on LSI level
            float searchChance = rapSheet.GetSearchProbability();

            // Roll for random search
            float roll = UnityEngine.Random.Range(0f, 1f);
            bool shouldSearch = roll < searchChance;

            ModLogger.Debug($"Search roll for {player.name}: {roll:F2} vs {searchChance:F2} (LSI: {rapSheet.LSILevel}) = {shouldSearch}");

            return shouldSearch;
        }

        /// <summary>
        /// Initiate a contraband search on the player
        /// </summary>
        public IEnumerator PerformParoleSearch(ParoleOfficerBehavior officer, Player player)
        {
            if (officer == null || player == null) yield break;

            // Record search time
            lastSearchTime[player] = Time.time;

            // Stop officer movement
            officer.StopMovement();

            // Announce search
            officer.PlayGuardVoiceCommand(
                JailNPCAudioController.GuardCommandType.Stop,
                "Parole compliance check. Stay where you are.",
                true
            );

            ModLogger.Info($"Officer {officer.GetBadgeNumber()} initiating parole search on {player.name}");

            // Wait for officer to reach player
            officer.MoveTo(player.transform.position);
            yield return new WaitForSeconds(2f);

            // Announce search
            officer.TrySendNPCMessage("I'm going to search your inventory. Don't move.", 3f);
            yield return new WaitForSeconds(1.5f);

            // Perform actual contraband search
            var contrabandSystem = new ContrabandDetectionSystem();
            var detectedCrimes = contrabandSystem.PerformContrabandSearch(player);

            if (detectedCrimes != null && detectedCrimes.Count > 0)
            {
                // Contraband found!
                HandleContrabandFound(officer, player, detectedCrimes);
            }
            else
            {
                // Clean search
                officer.PlayGuardVoiceCommand(
                    JailNPCAudioController.GuardCommandType.AllClear,
                    "You're clean. Stay out of trouble.",
                    true
                );

                ModLogger.Info($"Officer {officer.GetBadgeNumber()}: Clean search for {player.name}");
            }

            // Resume patrol
            yield return new WaitForSeconds(2f);
            officer.StartPatrol();
        }

        /// <summary>
        /// Handle contraband detection during search
        /// </summary>
        private void HandleContrabandFound(ParoleOfficerBehavior officer, Player player, List<CrimeInstance> crimes)
        {
            officer.PlayGuardVoiceCommand(
                JailNPCAudioController.GuardCommandType.Alert,
                "Contraband detected! You're in violation of parole!",
                true
            );

            ModLogger.Info($"Officer {officer.GetBadgeNumber()}: Found {crimes.Count} contraband items on {player.name}");

            // Add crimes to rap sheet
            var rapSheetManager = Core.Instance?.RapSheetManager;
            if (rapSheetManager != null)
            {
                var rapSheet = rapSheetManager.GetRapSheet(player);
                if (rapSheet != null)
                {
                    foreach (var crime in crimes)
                    {
                        rapSheet.AddCrime(crime);
                    }

                    // Add parole violation
                    if (rapSheet.CurrentParoleRecord != null)
                    {
                        var violation = new ViolationRecord
                        {
                            ViolationType = ViolationType.ContrabandPossession,
                            ViolationTime = DateTime.Now,
                            Details = $"Found {crimes.Count} contraband items during parole search"
                        };
                        rapSheet.CurrentParoleRecord.AddViolation(violation);

                        // Re-assess LSI level after violation
                        rapSheet.UpdateLSILevel();
                    }
                }
            }

            // Initiate arrest for parole violation
            var jailSystem = Core.Instance?.JailSystem;
            if (jailSystem != null)
            {
                MelonCoroutines.Start(jailSystem.HandleImmediateArrest(player));
            }
        }

        /// <summary>
        /// Clear search cooldown for a player (for testing)
        /// </summary>
        public void ClearSearchCooldown(Player player)
        {
            if (lastSearchTime.ContainsKey(player))
            {
                lastSearchTime.Remove(player);
            }
        }
    }
}
```

### 2.2 ParoleOfficerBehavior Integration

**Modifications to ParoleOfficerBehavior.cs:**

```csharp
// Add to class fields
private float lastSearchCheckTime = 0f;
private const float SEARCH_CHECK_INTERVAL = 5f; // Check for search opportunities every 5 seconds

// Modify HandlePatrolLogic() method
private void HandlePatrolLogic()
{
    if (!patrolInitialized || availablePatrolPoints.Count == 0) return;

    // Continue patrol movement
    if (Time.time - lastPatrolTime >= patrolRoute.waitTime)
    {
        MoveToNextPatrolPoint();
    }

    // Check for search opportunities while patrolling
    if (Time.time - lastSearchCheckTime >= SEARCH_CHECK_INTERVAL)
    {
        CheckForSearchOpportunities();
        lastSearchCheckTime = Time.time;
    }
}

// Add new method
/// <summary>
/// Check if any nearby players should be searched
/// Called periodically during patrol
/// </summary>
private void CheckForSearchOpportunities()
{
    // Only patrol officers perform random searches
    if (role != ParoleOfficerRole.PatrolOfficer) return;

    // Get all players in range
    var players = GameObject.FindObjectsOfType<Player>();
    if (players == null || players.Length == 0) return;

    foreach (var player in players)
    {
        if (player == null) continue;

        // Check if search should be initiated
        if (ParoleSearchSystem.Instance.ShouldInitiateSearch(this, player))
        {
            // Initiate search
            ModLogger.Info($"Officer {badgeNumber}: Initiating random search on {player.name}");

            // Stop patrol temporarily
            StopMovement();
            currentActivity = ParoleOfficerActivity.MonitoringArea;

            // Start search coroutine
            MelonCoroutines.Start(ParoleSearchSystem.Instance.PerformParoleSearch(this, player));

            // Only search one player at a time
            break;
        }
    }
}
```

---

## 3. Implementation Flowchart

```
┌─────────────────────────────────────────────────────────────────┐
│                     PAROLE OFFICER PATROL                        │
│                                                                   │
│  ┌────────────────────────────────────────────────────────┐    │
│  │                  Every 5 seconds                        │    │
│  │        CheckForSearchOpportunities()                    │    │
│  └──────────────────┬─────────────────────────────────────┘    │
│                     │                                             │
│                     v                                             │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  Get all nearby players (within 15m)                   │    │
│  └──────────────────┬─────────────────────────────────────┘    │
│                     │                                             │
│                     v                                             │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  For each player:                                       │    │
│  │  ShouldInitiateSearch(officer, player)                 │    │
│  └──────────────────┬─────────────────────────────────────┘    │
│                     │                                             │
│         ┌───────────┴───────────┐                                │
│         v                       v                                │
│    ┌────────┐            ┌─────────┐                            │
│    │  NO    │            │  YES    │                            │
│    │ Skip   │            │ Search  │                            │
│    └────────┘            └────┬────┘                            │
│                               │                                  │
│                               v                                  │
└───────────────────────────────┼──────────────────────────────────┘
                                │
                ┌───────────────┴───────────────┐
                │                               │
┌───────────────v────────────────┐ ┌────────────v──────────────┐
│  PRE-CHECKS                     │ │  CONTRABAND SEARCH        │
│                                 │ │                           │
│  ✓ Officer & player valid?     │ │  1. Stop officer          │
│  ✓ Not processing intake?      │ │  2. Announce search       │
│  ✓ Not escorting?               │ │  3. Move to player       │
│  ✓ Within 15m range?            │ │  4. Perform search       │
│  ✓ Search cooldown expired?    │ │                           │
│    (2 min minimum)              │ │  PerformContrabandSearch()│
│  ✓ Player on parole?            │ │                           │
│                                 │ └────────────┬──────────────┘
└────────────────┬────────────────┘              │
                 │                                │
                 v                    ┌───────────┴───────────┐
┌────────────────────────────────┐   v                       v
│  GET LSI LEVEL                 │ ┌──────────┐      ┌──────────┐
│                                │ │  CLEAN   │      │ CONTRABAND│
│  rapSheet.LSILevel             │ │          │      │  FOUND   │
│  rapSheet.GetSearchProbability()│ │ "You're  │      │          │
│                                │ │  clean"  │      └────┬─────┘
│  None:    0% chance            │ │          │           │
│  Minimum: 10% chance           │ │ Resume   │           v
│  Medium:  30% chance           │ │ patrol   │   ┌──────────────┐
│  High:    60% chance           │ │          │   │ Add crimes to│
│  Severe:  90% chance           │ └──────────┘   │  rap sheet   │
│                                │                │              │
└────────────────┬───────────────┘                │ Add parole   │
                 │                                 │  violation   │
                 v                                 │              │
┌────────────────────────────────┐                │ Update LSI   │
│  RANDOM ROLL                   │                │  level       │
│                                │                │              │
│  roll = Random(0.0, 1.0)       │                │ Initiate     │
│  if roll < searchChance:       │                │  arrest      │
│      INITIATE SEARCH           │                │              │
│  else:                         │                └──────────────┘
│      CONTINUE PATROL           │
│                                │
└────────────────────────────────┘
```

---

## 4. Implementation Checklist

### Phase 1: LSI System Foundation
- [ ] Add LSI enum to RapSheet.cs
- [ ] Add LSILevel and LastLSIAssessment fields to RapSheet
- [ ] Implement CalculateLSILevel() method
- [ ] Implement UpdateLSILevel() method
- [ ] Implement GetSearchProbability() method
- [ ] Update RapSheet serialization to include new fields
- [ ] Test LSI calculation with various criminal histories

### Phase 2: Search System Core
- [ ] Create ParoleSearchSystem.cs
- [ ] Implement ShouldInitiateSearch() logic
- [ ] Implement PerformParoleSearch() coroutine
- [ ] Implement HandleContrabandFound() logic
- [ ] Add search cooldown tracking system
- [ ] Test search probability rolls at different LSI levels

### Phase 3: Patrol Integration
- [ ] Add search check fields to ParoleOfficerBehavior
- [ ] Modify HandlePatrolLogic() to check for searches
- [ ] Implement CheckForSearchOpportunities() method
- [ ] Add search activity state handling
- [ ] Test patrol interruption and resumption
- [ ] Test multiple officers searching independently

### Phase 4: Violation Handling
- [ ] Ensure ViolationType enum includes ContrabandPossession
- [ ] Add violation recording during contraband detection
- [ ] Implement LSI level re-assessment after violations
- [ ] Test violation escalation workflow
- [ ] Test arrest initiation for parole violations

### Phase 5: Testing & Polish
- [ ] Test all LSI levels (Minimum through Severe)
- [ ] Test search cooldowns preventing spam
- [ ] Test clean searches (no contraband found)
- [ ] Test contraband detection during searches
- [ ] Test multiple officers patrolling simultaneously
- [ ] Test LSI level updates after crimes/violations
- [ ] Add debug commands for testing search system
- [ ] Performance testing with multiple parolees

---

## 5. Integration Points

### 5.1 Existing Systems Used
- **ContrabandDetectionSystem**: `PerformContrabandSearch()` for actual item checking
- **RapSheetManager**: Get player rap sheets and LSI levels
- **ParoleRecord**: Track parole status and violations
- **JailSystem**: `HandleImmediateArrest()` for parole violations
- **ParoleOfficerBehavior**: Patrol logic and NPC control
- **JailNPCAudioController**: Voice commands during searches

### 5.2 New Dependencies
- ParoleSearchSystem depends on:
  - RapSheet.LSILevel
  - RapSheet.GetSearchProbability()
  - ContrabandDetectionSystem
  - ParoleOfficerBehavior

### 5.3 Data Flow
```
Player Criminal Activity
    ↓
RapSheet.CrimesCommited
    ↓
RapSheet.CalculateLSILevel()
    ↓
RapSheet.LSILevel
    ↓
RapSheet.GetSearchProbability()
    ↓
ParoleSearchSystem.ShouldInitiateSearch()
    ↓
ParoleSearchSystem.PerformParoleSearch()
    ↓
ContrabandDetectionSystem.PerformContrabandSearch()
    ↓
RapSheet.AddCrime() + ParoleRecord.AddViolation()
    ↓
RapSheet.UpdateLSILevel()
```

---

## 6. Testing Strategy

### 6.1 Unit Testing
- Test LSI calculation with various crime combinations
- Test search probability calculations
- Test cooldown system
- Test distance detection

### 6.2 Integration Testing
- Test complete search workflow from detection to arrest
- Test LSI level changes affecting search frequency
- Test multiple officers interacting with multiple parolees
- Test search interruption and resumption

### 6.3 Debug Commands
```csharp
// Add to Core.cs or dedicated debug system
public void SetPlayerLSI(Player player, LSILevel level)
{
    var rapSheet = RapSheetManager.GetRapSheet(player);
    if (rapSheet != null)
    {
        rapSheet.LSILevel = level;
        rapSheet.SaveRapSheet();
        ModLogger.Info($"Set {player.name} LSI to {level}");
    }
}

public void ForceSearch(Player player)
{
    var officer = FindNearestParoleOfficer(player.transform.position);
    if (officer != null)
    {
        MelonCoroutines.Start(ParoleSearchSystem.Instance.PerformParoleSearch(officer, player));
    }
}

public void ClearSearchCooldown(Player player)
{
    ParoleSearchSystem.Instance.ClearSearchCooldown(player);
    ModLogger.Info($"Cleared search cooldown for {player.name}");
}
```

---

## 7. Balance Considerations

### 7.1 Search Probability Tuning
Current probabilities can be adjusted based on gameplay testing:
- **Minimum (10%)**: Should feel rare, maybe 1-2 searches per 10 encounters
- **Medium (30%)**: Noticeable but not constant, ~3 searches per 10 encounters
- **High (60%)**: Frequent, players should expect to be searched often
- **Severe (90%)**: Nearly guaranteed, players should plan around constant searches

### 7.2 Cooldown Tuning
- Current: 2 minutes minimum between searches
- Consider: Could vary by LSI level (longer for Minimum, shorter for Severe)

### 7.3 Detection Range
- Current: 15m
- Consider: Wider range (20m) in high-crime areas, shorter (10m) in low-crime areas

---

## 8. Future Enhancements

### 8.1 Advanced Features
- **Search Zones**: Designate high-enforcement areas with increased search chances
- **Time-Based Searches**: Higher frequency during "peak crime hours"
- **Multi-Officer Searches**: Multiple officers working together for Severe cases
- **Electronic Monitoring**: Severe LSI players get tracking devices
- **Search Warrants**: Extend searches to player-owned properties
- **K9 Units**: Drug-sniffing dogs for enhanced detection

### 8.2 Player Interaction
- **Compliance Bonus**: Players who cooperate get reduced penalties
- **Resistance Penalties**: Players who flee during search get additional charges
- **Search Rights**: Players can request supervisor review (mini-game)

### 8.3 Officer Specialization
- **Dedicated Search Officers**: Officers specifically assigned to search duty
- **Training Levels**: Officers with more experience find contraband more reliably
- **Search Success Stats**: Track officer performance over time

---

## End of Integration Plan

**Total Estimated Development Time**: 8-12 hours
**Complexity Level**: Medium
**Risk Level**: Low (leverages existing systems)
**Testing Priority**: High (affects core gameplay loop)
