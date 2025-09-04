using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Messaging;
using Il2CppSystem.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
#else
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Messaging;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Jail-specific behavior for guard NPCs with patrol routes and prisoner monitoring
    /// </summary>
    public class JailGuardBehavior : MonoBehaviour
    {
        public enum GuardRole
        {
            StationGuard,      // Guards specific areas
            PatrolGuard,       // Patrols between areas
            WatchTowerGuard,   // Monitors from security cameras
            ResponseGuard,      // Responds to incidents
            CellBlockGuard
        }

        [System.Serializable]
        public class PatrolData
        {
            public Vector3[] points;
            public float speed = 2f;
            public float waitTime = 3f;
        }

        // Saveable fields for persistence across game sessions
        [SaveableField("guard_role")]
        public GuardRole role = GuardRole.StationGuard;

        [SaveableField("guard_patrol_route")]
        public PatrolData patrolRoute = new PatrolData();

        [SaveableField("guard_shift_start_time")]
        public float shiftStartTime = 0f;

        [SaveableField("guard_shift_duration")]
        public float shiftDuration = 480f; // 8 minutes default

        [SaveableField("guard_experience_level")]
        public int experienceLevel = 1;

        [SaveableField("guard_badge_number")]
        public string badgeNumber = "";

        // Runtime state
        private UnityEngine.AI.NavMeshAgent navAgent;
        private int currentPatrolIndex = 0;
        private float lastPatrolTime = 0f;
        private bool isOnDuty = true;
        private GameObject assignedArea;
        private bool hasReachedDestination = true;
        private Vector3 currentDestination;
        private float lastDestinationTime = 0f;

#if !MONO
        private Il2CppScheduleOne.NPCs.NPC npcComponent;
#else
        private ScheduleOne.NPCs.NPC npcComponent;
#endif

        public void Initialize(GuardRole guardRole, string badge = "")
        {
            role = guardRole;
            badgeNumber = string.IsNullOrEmpty(badge) ? GenerateBadgeNumber() : badge;
            shiftStartTime = Time.time;

            ModLogger.Info($"Initializing {role} guard with badge {badgeNumber}");

            // Apply appropriate appearance
            //var appearanceType = role == GuardRole.WatchTowerGuard ? NPCAppearanceManager.GuardType.Senior : NPCAppearanceManager.GuardType.Regular;
            //NPCAppearanceManager.ApplyGuardAppearance(gameObject, appearanceType, badgeNumber);
        }

        private void Start()
        {
            try
            {
                navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
#if !MONO
                npcComponent = GetComponent<Il2CppScheduleOne.NPCs.NPC>();
#else
                npcComponent = GetComponent<ScheduleOne.NPCs.NPC>();
#endif

                if (navAgent == null)
                {
                    ModLogger.Error($"NavMeshAgent not found on guard {gameObject.name}");
                    return;
                }

                if (npcComponent == null)
                {
                    ModLogger.Error($"NPC component not found on guard {gameObject.name}");
                    return;
                }

                SetupGuardBehavior();
                StartCoroutine("GuardBehaviorLoop");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error starting JailGuardBehavior: {e.Message}");
            }
        }

        private void SetupGuardBehavior()
        {
            switch (role)
            {
                case GuardRole.PatrolGuard:
                    SetupPatrolRoute();
                    break;
                case GuardRole.StationGuard:
                    SetupStationPosition();
                    break;
                case GuardRole.WatchTowerGuard:
                    SetupWatchTowerPosition();
                    break;
                case GuardRole.ResponseGuard:
                    SetupResponsePosition();
                    break;
            }
        }

        private void SetupPatrolRoute()
        {
            // Default patrol route around jail areas
            if (patrolRoute.points == null || patrolRoute.points.Length == 0)
            {
                patrolRoute.points = new Vector3[]
                {
                    new Vector3(50f, 8.6593f, -215f),   // Cell block area
                    new Vector3(55f, 8.6593f, -220f),   // Common area
                    new Vector3(60f, 8.6593f, -225f),   // Kitchen area
                    new Vector3(52f, 8.6593f, -230f)    // Back to start
                };
                patrolRoute.speed = 2f;
                patrolRoute.waitTime = 5f;
            }
        }

        private void SetupStationPosition()
        {
            // Station guard stays in one area
            Vector3 stationPosition = new Vector3(52.4923f, 8.6593f, -219.0759f);
            if (navAgent != null)
            {
                SetDestinationOnce(stationPosition);
            }
        }

        private void SetupWatchTowerPosition()
        {
            // Watch tower guard stays at elevated position
            Vector3 watchPosition = new Vector3(58f, 10f, -222f);
            if (navAgent != null)
            {
                SetDestinationOnce(watchPosition);
            }
        }

        private void SetupResponsePosition()
        {
            // Response guard stays at central location
            Vector3 responsePosition = new Vector3(55f, 8.6593f, -218f);
            if (navAgent != null)
            {
                SetDestinationOnce(responsePosition);
            }
        }

        /// <summary>
        /// Set destination only if not already set to avoid constant movement
        /// </summary>
        private void SetDestinationOnce(Vector3 destination)
        {
            if (navAgent == null) return;
            
            // Only set destination if it's different from current or we haven't reached it
            if (Vector3.Distance(currentDestination, destination) > 1f || !hasReachedDestination)
            {
                currentDestination = destination;
                navAgent.SetDestination(destination);
                hasReachedDestination = false;
                lastDestinationTime = Time.time;
                ModLogger.Debug($"Guard {badgeNumber} setting new destination: {destination}");
            }
        }

        /// <summary>
        /// Check if we've reached our destination
        /// </summary>
        private bool CheckReachedDestination()
        {
            if (navAgent == null || hasReachedDestination) return hasReachedDestination;
            
            if (!navAgent.pathPending && navAgent.remainingDistance < 1.5f)
            {
                hasReachedDestination = true;
                ModLogger.Debug($"Guard {badgeNumber} reached destination");
                return true;
            }
            
            return false;
        }

        private IEnumerator GuardBehaviorLoop()
        {
            while (isOnDuty)
            {
                // Check if we've reached our destination
                CheckReachedDestination();
                
                switch (role)
                {
                    case GuardRole.PatrolGuard:
                        yield return HandlePatrolBehavior();
                        break;
                    case GuardRole.StationGuard:
                        yield return HandleStationBehavior();
                        break;
                    case GuardRole.WatchTowerGuard:
                        yield return HandleWatchTowerBehavior();
                        break;
                    case GuardRole.ResponseGuard:
                        yield return HandleResponseBehavior();
                        break;
                }

                // Much longer wait to prevent constant behavior updates
                yield return new WaitForSeconds(5f);
            }
        }

        private IEnumerator HandlePatrolBehavior()
        {
            if (patrolRoute.points == null || patrolRoute.points.Length == 0)
            {
                yield break;
            }

            // Only move to next patrol point if we've reached the current one
            if (hasReachedDestination)
            {
                // Wait at the patrol point before moving to next
                if (Time.time - lastDestinationTime > patrolRoute.waitTime)
                {
                    currentPatrolIndex = (currentPatrolIndex + 1) % patrolRoute.points.Length;
                    Vector3 targetPoint = patrolRoute.points[currentPatrolIndex];
                    
                    if (navAgent != null)
                    {
                        navAgent.speed = patrolRoute.speed;
                        SetDestinationOnce(targetPoint);
                    }
                }
            }
        }

        private IEnumerator HandleStationBehavior()
        {
            // Station guards just stay in position - only rotate occasionally
            if (hasReachedDestination && UnityEngine.Random.Range(0f, 1f) < 0.1f) // 10% chance to look around
            {
                // Rotate to look around
                float startRotation = transform.eulerAngles.y;
                float targetRotation = startRotation + UnityEngine.Random.Range(-45f, 45f);
                
                // Simple rotation without complex animation
                transform.eulerAngles = new Vector3(0, targetRotation, 0);
            }
            yield break;
        }

        private IEnumerator HandleWatchTowerBehavior()
        {
            // Watch tower guards just stay in position and occasionally scan
            if (hasReachedDestination && UnityEngine.Random.Range(0f, 1f) < 0.05f) // 5% chance to scan
            {
                float scanDirection = transform.eulerAngles.y + UnityEngine.Random.Range(-30f, 30f);
                transform.eulerAngles = new Vector3(0, scanDirection, 0);
            }
            
            // TODO: Implement camera monitoring behavior
            // This could integrate with SecurityCamera components
            yield break;
        }

        private IEnumerator HandleResponseBehavior()
        {
            // Response guards just idle in position waiting for alerts
            // Only move if there's an actual incident (not implemented yet)
            
            // TODO: Implement response to jail alerts/incidents
            yield break;
        }

        public void OnPlayerInteraction(GameObject player)
        {
            try
            {
                if (npcComponent == null) return;

                string message = GetGuardInteractionMessage();
                npcComponent.SendTextMessage(message);

                // Create response options
                var responses = CreateGuardResponseOptions(player);
                if (responses.Length > 0)
                {
                    MelonCoroutines.Start(ShowResponsesAfterDelay(responses, 1f));
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in guard interaction: {e.Message}");
            }
        }

        private string GetGuardInteractionMessage()
        {
            switch (role)
            {
                case GuardRole.StationGuard:
                    return $"Officer {badgeNumber}: You need something, inmate?";
                case GuardRole.PatrolGuard:
                    return $"Officer {badgeNumber}: Keep moving, nothing to see here.";
                case GuardRole.WatchTowerGuard:
                    return $"Security Chief {badgeNumber}: I'm watching you.";
                case GuardRole.ResponseGuard:
                    return $"Response Officer {badgeNumber}: Any trouble here?";
                default:
                    return $"Guard {badgeNumber}: What do you want?";
            }
        }

#if !MONO
        private Il2CppScheduleOne.Messaging.Response[] CreateGuardResponseOptions(GameObject player)
#else
        private ScheduleOne.Messaging.Response[] CreateGuardResponseOptions(GameObject player)
#endif
        {
            var responseList = new System.Collections.Generic.List<
#if !MONO
                Il2CppScheduleOne.Messaging.Response
#else
                ScheduleOne.Messaging.Response
#endif
            >();

            // Basic interaction responses
#if !MONO
            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "I need medical attention",
                label = "medical_request",
                callback = new System.Action(() => HandleMedicalRequest(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "When is my next meal?",
                label = "meal_request",
                callback = new System.Action(() => HandleMealInquiry(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "Nothing, officer",
                label = "dismiss",
                callback = new System.Action(() => HandleDismiss(player))
            });
#else
            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "I need medical attention",
                label = "medical_request",
                callback = () => HandleMedicalRequest(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "When is my next meal?",
                label = "meal_request",
                callback = () => HandleMealInquiry(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "Nothing, officer",
                label = "dismiss",
                callback = () => HandleDismiss(player)
            });
#endif

            return responseList.ToArray();
        }

        private IEnumerator ShowResponsesAfterDelay(
#if !MONO
            Il2CppScheduleOne.Messaging.Response[] responses, 
#else
            ScheduleOne.Messaging.Response[] responses,
#endif
            float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (npcComponent != null && npcComponent.MSGConversation != null)
            {
                npcComponent.MSGConversation.ClearResponses();
#if !MONO
                var responsesList = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.Response>();
                foreach (var response in responses)
                {
                    responsesList.Add(response);
                }
#else
                var responsesList = new System.Collections.Generic.List<ScheduleOne.Messaging.Response>(responses);
#endif
                npcComponent.MSGConversation.ShowResponses(responsesList, 0f, true);
            }
        }

        private void HandleMedicalRequest(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Medical requests go through the warden. Submit a formal request.");
            }
        }

        private void HandleMealInquiry(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Meals are served at 8 AM, 12 PM, and 6 PM. Don't be late.");
            }
        }

        private void HandleDismiss(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Stay out of trouble.");
            }
        }

        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        public void EndShift()
        {
            isOnDuty = false;
            ModLogger.Info($"Guard {badgeNumber} ended their shift");
        }

        public bool IsOnDuty() => isOnDuty;

        public GuardRole GetRole() => role;

        public string GetBadgeNumber() => badgeNumber;
    }

    /// <summary>
    /// Jail-specific behavior for inmate NPCs with cell assignments and daily routines
    /// </summary>
    public class JailInmateBehavior : MonoBehaviour
    {
        public enum InmateStatus
        {
            NewInmate,
            RegularInmate,
            ModelInmate,
            ProblematicInmate,
            InSolitary
        }

        // Saveable fields for persistence
        [SaveableField("inmate_prisoner_id")]
        public string prisonerID = "";

        [SaveableField("inmate_cell_number")]
        public int cellNumber = -1;

        [SaveableField("inmate_sentence_days")]
        public int sentenceDaysRemaining = 30;

        [SaveableField("inmate_behavior_score")]
        public float behaviorScore = 50f;

        [SaveableField("inmate_crime_type")]
        public string crimeType = "Unknown";

        [SaveableField("inmate_time_served")]
        public float timeServed = 0f;

        [SaveableField("inmate_status")]
        public InmateStatus status = InmateStatus.NewInmate;

        // Runtime state
        private bool isInCell = false;
        private bool hasAssignedCell = false;
        private Vector3 cellPosition;
        private float lastMealTime = 0f;
        private float lastActivityTime = 0f;

#if !MONO
        private Il2CppScheduleOne.NPCs.NPC npcComponent;
#else
        private ScheduleOne.NPCs.NPC npcComponent;
#endif

        public void Initialize(string crime, int sentence, string prisonerId = "")
        {
            crimeType = crime;
            sentenceDaysRemaining = sentence;
            prisonerID = string.IsNullOrEmpty(prisonerId) ? GeneratePrisonerID() : prisonerId;
            timeServed = 0f;
            behaviorScore = 50f; // Start with neutral behavior

            ModLogger.Info($"Initializing inmate {prisonerID} for {crime} - {sentence} days");

            // Apply appropriate appearance based on crime severity
            //var prisonerType = DetermineInmateTypeFromCrime(crime);
            //NPCAppearanceManager.ApplyPrisonerAppearance(gameObject, prisonerType, crime);
        }

        //private NPCAppearanceManager.PrisonerType DetermineInmateTypeFromCrime(string crime)
        //{
        //    string crimeLower = crime.ToLower();
            
        //    if (crimeLower.Contains("murder") || crimeLower.Contains("assault") || crimeLower.Contains("violent"))
        //    {
        //        return NPCAppearanceManager.PrisonerType.LargeThreat;
        //    }
        //    else if (crimeLower.Contains("theft") || crimeLower.Contains("petty") || crimeLower.Contains("minor"))
        //    {
        //        return NPCAppearanceManager.PrisonerType.SmallTimeCriminal;
        //    }
        //    else if (behaviorScore >= 80f)
        //    {
        //        return NPCAppearanceManager.PrisonerType.ModelPrisoner;
        //    }
        //    else if (behaviorScore < 30f)
        //    {
        //        return NPCAppearanceManager.PrisonerType.DangerousPrisoner;
        //    }
            
        //    return NPCAppearanceManager.PrisonerType.Regular;
        //}

        private void Start()
        {
            try
            {
#if !MONO
                npcComponent = GetComponent<Il2CppScheduleOne.NPCs.NPC>();
#else
                npcComponent = GetComponent<ScheduleOne.NPCs.NPC>();
#endif

                if (npcComponent == null)
                {
                    ModLogger.Error($"NPC component not found on inmate {gameObject.name}");
                    return;
                }

                MelonCoroutines.Start(InmateBehaviorLoop());
                MelonCoroutines.Start(UpdateTimeServed());
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error starting JailInmateBehavior: {e.Message}");
            }
        }

        private IEnumerator InmateBehaviorLoop()
        {
            while (sentenceDaysRemaining > 0)
            {
                switch (status)
                {
                    case InmateStatus.NewInmate:
                        yield return HandleNewInmateBehavior();
                        break;
                    case InmateStatus.RegularInmate:
                        yield return HandleRegularInmateBehavior();
                        break;
                    case InmateStatus.ModelInmate:
                        yield return HandleModelInmateBehavior();
                        break;
                    case InmateStatus.ProblematicInmate:
                        yield return HandleProblematicInmateBehavior();
                        break;
                    case InmateStatus.InSolitary:
                        yield return HandleSolitaryBehavior();
                        break;
                }

                yield return new WaitForSeconds(60f);
            }

            // Sentence completed
            OnSentenceCompleted();
        }

        private IEnumerator UpdateTimeServed()
        {
            while (sentenceDaysRemaining > 0)
            {
                yield return new WaitForSeconds(1f); // Update every second
                timeServed += Time.deltaTime;
            }
        }

        private IEnumerator HandleNewInmateBehavior()
        {
            // New inmates stay close to their cell and are cautious
            if (!hasAssignedCell)
            {
                yield return RequestCellAssignment();
            }

            // Gradually adjust to prison life
            if (timeServed > 60f) // After 1 minute, no longer "new"
            {
                status = InmateStatus.RegularInmate;
                ModLogger.Info($"Inmate {prisonerID} is no longer considered new");
            }

            yield return new WaitForSeconds(10f);
        }

        private IEnumerator HandleRegularInmateBehavior()
        {
            // Regular daily routine
            yield return new WaitForSeconds(UnityEngine.Random.Range(15f, 45f));
            
            // Randomly modify behavior score
            float behaviorChange = UnityEngine.Random.Range(-2f, 2f);
            ModifyBehaviorScore(behaviorChange);
            
            // Check for status changes
            UpdateInmateStatus();
        }

        private IEnumerator HandleModelInmateBehavior()
        {
            // Model inmates have better behavior and follow routines
            yield return new WaitForSeconds(UnityEngine.Random.Range(30f, 60f));
            
            // Model inmates slowly improve their behavior
            ModifyBehaviorScore(UnityEngine.Random.Range(0f, 1f));
        }

        private IEnumerator HandleProblematicInmateBehavior()
        {
            // Problematic inmates cause trouble
            yield return new WaitForSeconds(UnityEngine.Random.Range(10f, 30f));
            
            // May cause incidents or lose more behavior points
            float behaviorChange = UnityEngine.Random.Range(-3f, 0f);
            ModifyBehaviorScore(behaviorChange);
            
            // Chance of being sent to solitary
            if (behaviorScore < 20f && UnityEngine.Random.Range(0f, 1f) < 0.1f)
            {
                status = InmateStatus.InSolitary;
                ModLogger.Info($"Inmate {prisonerID} sent to solitary confinement");
            }
        }

        private IEnumerator HandleSolitaryBehavior()
        {
            // Inmates in solitary are isolated
            yield return new WaitForSeconds(60f);
            
            // Gradually improve behavior in solitary
            ModifyBehaviorScore(UnityEngine.Random.Range(-1f, 2f));
            
            // Can be released from solitary
            if (behaviorScore > 30f && timeServed > lastActivityTime + 120f) // 2 minutes minimum
            {
                status = InmateStatus.RegularInmate;
                ModLogger.Info($"Inmate {prisonerID} released from solitary confinement");
            }
        }

        private IEnumerator RequestCellAssignment()
        {
            // Try to get a cell assignment from the jail controller
            var jailController = Core.ActiveJailController;
            if (jailController != null)
            {
                // TODO: Implement cell assignment logic with JailController
                // For now, just mark as having a cell
                hasAssignedCell = true;
                cellNumber = UnityEngine.Random.Range(1, 20);
                ModLogger.Info($"Assigned cell {cellNumber} to inmate {prisonerID}");
            }
            yield return new WaitForSeconds(1f);
        }

        public void OnPlayerInteraction(GameObject player)
        {
            try
            {
                if (npcComponent == null) return;

                string message = GetInmateInteractionMessage();
                npcComponent.SendTextMessage(message);

                var responses = CreateInmateResponseOptions(player);
                if (responses.Length > 0)
                {
                    MelonCoroutines.Start(ShowResponsesAfterDelay(responses, 1f));
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in inmate interaction: {e.Message}");
            }
        }

        private string GetInmateInteractionMessage()
        {
            switch (status)
            {
                case InmateStatus.NewInmate:
                    return $"Prisoner {prisonerID}: I'm still figuring this place out... what do you want?";
                case InmateStatus.ModelInmate:
                    return $"Prisoner {prisonerID}: I'm trying to keep my head down and do my time. What's up?";
                case InmateStatus.ProblematicInmate:
                    return $"Prisoner {prisonerID}: What's it to you? I don't owe you anything.";
                case InmateStatus.InSolitary:
                    return $"Prisoner {prisonerID}: They got me locked up tight... shouldn't even be talking.";
                default:
                    return $"Prisoner {prisonerID}: Yeah? What do you need?";
            }
        }

#if !MONO
        private Il2CppScheduleOne.Messaging.Response[] CreateInmateResponseOptions(GameObject player)
#else
        private ScheduleOne.Messaging.Response[] CreateInmateResponseOptions(GameObject player)
#endif
        {
            var responseList = new System.Collections.Generic.List<
#if !MONO
                Il2CppScheduleOne.Messaging.Response
#else
                ScheduleOne.Messaging.Response
#endif
            >();

#if !MONO
            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "What are you in for?",
                label = "crime_inquiry",
                callback = new System.Action(() => HandleCrimeInquiry(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "How much time do you have left?",
                label = "time_inquiry",
                callback = new System.Action(() => HandleTimeInquiry(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "Never mind",
                label = "dismiss",
                callback = new System.Action(() => HandleDismiss(player))
            });
#else
            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "What are you in for?",
                label = "crime_inquiry",
                callback = () => HandleCrimeInquiry(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "How much time do you have left?",
                label = "time_inquiry",
                callback = () => HandleTimeInquiry(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "Never mind",
                label = "dismiss",
                callback = () => HandleDismiss(player)
            });
#endif

            return responseList.ToArray();
        }

        private IEnumerator ShowResponsesAfterDelay(
#if !MONO
            Il2CppScheduleOne.Messaging.Response[] responses,
#else
            ScheduleOne.Messaging.Response[] responses,
#endif
            float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (npcComponent != null && npcComponent.MSGConversation != null)
            {
                npcComponent.MSGConversation.ClearResponses();
#if !MONO
                var responsesList = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.Response>();
                foreach (var response in responses)
                {
                    responsesList.Add(response);
                }
#else
                var responsesList = new System.Collections.Generic.List<ScheduleOne.Messaging.Response>(responses);
#endif
                npcComponent.MSGConversation.ShowResponses(responsesList, 0f, true);
            }
        }

        private void HandleCrimeInquiry(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage($"I'm here for {crimeType}. Not proud of it, but that's life.");
            }
        }

        private void HandleTimeInquiry(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage($"Got {sentenceDaysRemaining} days left. Been here for {Mathf.FloorToInt(timeServed)} seconds already.");
            }
        }

        private void HandleDismiss(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Yeah, that's what I thought. Mind your own business.");
            }
        }

        private void ModifyBehaviorScore(float change)
        {
            behaviorScore = Mathf.Clamp(behaviorScore + change, 0f, 100f);
            lastActivityTime = Time.time;
            
            if (Mathf.Abs(change) > 1f) // Only log significant changes
            {
                ModLogger.Debug($"Inmate {prisonerID} behavior score changed by {change:F1} to {behaviorScore:F1}");
            }
        }

        private void UpdateInmateStatus()
        {
            InmateStatus previousStatus = status;

            if (behaviorScore >= 80f)
            {
                status = InmateStatus.ModelInmate;
            }
            else if (behaviorScore <= 25f)
            {
                status = InmateStatus.ProblematicInmate;
            }
            else
            {
                status = InmateStatus.RegularInmate;
            }

            if (status != previousStatus)
            {
                ModLogger.Info($"Inmate {prisonerID} status changed from {previousStatus} to {status}");
                
                // Update appearance when status changes
                //var prisonerType = DetermineInmateTypeFromCrime(crimeType);
                //NPCAppearanceManager.ApplyPrisonerAppearance(gameObject, prisonerType, crimeType);
            }
        }

        private void OnSentenceCompleted()
        {
            ModLogger.Info($"Inmate {prisonerID} has completed their sentence");
            // TODO: Handle prisoner release - remove from jail, update records, etc.
        }

        private string GeneratePrisonerID()
        {
            return $"P{UnityEngine.Random.Range(10000, 99999)}";
        }

        public string GetPrisonerID() => prisonerID;
        public int GetCellNumber() => cellNumber;
        public float GetBehaviorScore() => behaviorScore;
        public InmateStatus GetStatus() => status;
        public string GetCrimeType() => crimeType;
        public int GetRemainingDays() => sentenceDaysRemaining;
    }

    /// <summary>
    /// Custom SaveableField attribute for persistence
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field)]
    public class SaveableField : System.Attribute
    {
        public string SaveName { get; }
        public SaveableField(string saveName) => SaveName = saveName;
    }
}