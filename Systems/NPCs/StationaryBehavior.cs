using UnityEngine;
using Behind_Bars.Helpers;
using BBHelpers = Behind_Bars.Helpers.Helpers;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Component that keeps an NPC at a fixed position, with ability to return after activities
    /// Used by supervising parole officer to remain at police station entrance
    /// </summary>
    public class StationaryBehavior : MonoBehaviour
    {
#if !MONO
        public StationaryBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Configuration

#if MONO
        [SerializeField]
#endif
        private Vector3 stationaryPosition;

#if MONO
        [SerializeField]
#endif
        private float positionTolerance = 1.5f;

#if MONO
        [SerializeField]
#endif
        private float returnSpeed = 2.5f;

#if MONO
        [SerializeField]
#endif
        private bool maintainPosition = true;

        #endregion

        #region State

        private BaseJailNPC npcComponent;
        private bool isAtPosition = false;
        private bool isReturning = false;
        private float nextPositionCheckTime;

        private const float PositionCheckIntervalSeconds = 0.25f;

        #endregion

        #region Initialization

        private void Awake()
        {
            npcComponent = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
            if (npcComponent == null)
            {
                ModLogger.Error($"StationaryBehavior on {gameObject.name} requires BaseJailNPC component");
            }
        }

        private void Start()
        {
            // If position not set, use current position
            if (stationaryPosition == Vector3.zero)
            {
                stationaryPosition = transform.position;
                ModLogger.Debug($"StationaryBehavior: Set stationary position to current position: {stationaryPosition}");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Set the stationary position for this NPC
        /// </summary>
        public void SetStationaryPosition(Vector3 position)
        {
            stationaryPosition = position;
            ModLogger.Debug($"StationaryBehavior: Set stationary position to {position}");
        }

        /// <summary>
        /// Return to the stationary position
        /// </summary>
        public void ReturnToPosition()
        {
            if (npcComponent == null)
            {
                ModLogger.Warn($"StationaryBehavior: Cannot return to position, NPC component is null");
                return;
            }

            if (IsAtPosition())
            {
                ModLogger.Debug($"StationaryBehavior: Already at stationary position");
                return;
            }

            isReturning = true;
            npcComponent.MoveTo(stationaryPosition);
            ModLogger.Debug($"StationaryBehavior: Returning to stationary position {stationaryPosition}");
        }

        /// <summary>
        /// Check if NPC is at the stationary position
        /// </summary>
        public bool IsAtPosition()
        {
            isAtPosition = GetDistanceSquaredFromStationaryPosition() <= positionTolerance * positionTolerance;
            return isAtPosition;
        }

        /// <summary>
        /// Get the stationary position
        /// </summary>
        public Vector3 GetStationaryPosition()
        {
            return stationaryPosition;
        }

        /// <summary>
        /// Enable or disable position maintenance
        /// </summary>
        public void SetMaintainPosition(bool maintain)
        {
            maintainPosition = maintain;
            if (!maintain)
            {
                isReturning = false;
            }
        }

        /// <summary>
        /// Check if position maintenance is enabled
        /// </summary>
        public bool IsMaintainingPosition()
        {
            return maintainPosition;
        }

        #endregion

        #region Update

        private void Update()
        {
            if (!maintainPosition || npcComponent == null) return;

            float currentTime = Time.time;
            if (currentTime < nextPositionCheckTime)
            {
                return;
            }

            nextPositionCheckTime = currentTime + PositionCheckIntervalSeconds;
            float distanceSquared = GetDistanceSquaredFromStationaryPosition();
            float toleranceSquared = positionTolerance * positionTolerance;
            isAtPosition = distanceSquared <= toleranceSquared;

            // Check if we've reached the position while returning
            if (isReturning && isAtPosition)
            {
                isReturning = false;
                ModLogger.Debug($"StationaryBehavior: Reached stationary position");
            }

            // If not at position and not currently moving/returning, return to position
            if (!isAtPosition && !isReturning)
            {
                // Only auto-return if we're significantly away (not just minor drift)
                float returnDistance = positionTolerance * 2f;
                if (distanceSquared > returnDistance * returnDistance)
                {
                    isReturning = true;
                    npcComponent.MoveTo(stationaryPosition);
                    ModLogger.Debug($"StationaryBehavior: Returning to stationary position {stationaryPosition}");
                }
            }
        }

        private float GetDistanceSquaredFromStationaryPosition()
        {
            return (transform.position - stationaryPosition).sqrMagnitude;
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmosSelected()
        {
            // Draw stationary position
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(stationaryPosition, positionTolerance);
            Gizmos.DrawLine(transform.position, stationaryPosition);
        }

        #endregion
    }
}

