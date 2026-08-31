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
        // Authored world-space post. A zero value is replaced with the current
        // transform position during Start.
        private Vector3 stationaryPosition;

#if MONO
        [SerializeField]
#endif
        // Radius used by IsAtPosition and the periodic maintenance check, in
        // world units. Auto-return requires twice this tolerance.
        private float positionTolerance = 1.5f;

#if MONO
        [SerializeField]
#endif
        // Retained Mono-serialized tuning field; current movement delegates to
        // BaseJailNPC.MoveTo and does not apply this speed directly.
        private float returnSpeed = 2.5f;

#if MONO
        [SerializeField]
#endif
        // Master gate for the periodic position-maintenance loop.
        private bool maintainPosition = true;

        #endregion

        #region State

        // StationaryBehavior owns only the post/tolerance state; BaseJailNPC owns
        // actual navigation and movement completion.
        private BaseJailNPC npcComponent;
        private bool isAtPosition = false;
        private bool isReturning = false;
        private float nextPositionCheckTime;

        private const float PositionCheckIntervalSeconds = 0.25f;

        #endregion

        #region Initialization

        /// <summary>
        /// Resolves the BaseJailNPC navigation owner. Without that component this
        /// helper can report position but cannot issue a return request.
        /// </summary>
        private void Awake()
        {
            npcComponent = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
            if (npcComponent == null)
            {
                ModLogger.Error($"StationaryBehavior on {gameObject.name} requires BaseJailNPC component");
            }
        }

        /// <summary>
        /// Captures the current transform as the post when no serialized position
        /// was supplied. This component does not otherwise move the NPC at startup.
        /// </summary>
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
        /// Sets the world-space post used by future checks and return requests.
        /// </summary>
        /// <param name="position">New stationary world-space position.</param>
        public void SetStationaryPosition(Vector3 position)
        {
            stationaryPosition = position;
            ModLogger.Debug($"StationaryBehavior: Set stationary position to {position}");
        }

        /// <summary>
        /// Requests BaseJailNPC navigation to the post unless already within the
        /// configured tolerance. The serialized returnSpeed is not applied here.
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
        /// Checks the current transform against the post using squared distance and
        /// updates the cached at-position flag.
        /// </summary>
        /// <returns>True when within positionTolerance world units.</returns>
        public bool IsAtPosition()
        {
            isAtPosition = GetDistanceSquaredFromStationaryPosition() <= positionTolerance * positionTolerance;
            return isAtPosition;
        }

        /// <summary>
        /// Gets the configured or startup-captured world-space post.
        /// </summary>
        /// <returns>The stationary position used by this component.</returns>
        public Vector3 GetStationaryPosition()
        {
            return stationaryPosition;
        }

        /// <summary>
        /// Enables or disables periodic drift correction. Disabling also cancels
        /// the local returning flag but does not stop a BaseJailNPC path already
        /// accepted by navigation.
        /// </summary>
        /// <param name="maintain">Whether automatic position maintenance is enabled.</param>
        public void SetMaintainPosition(bool maintain)
        {
            maintainPosition = maintain;
            if (!maintain)
            {
                isReturning = false;
            }
        }

        /// <summary>
        /// Gets whether the periodic drift-correction loop is enabled.
        /// </summary>
        /// <returns>True when maintenance may issue automatic return requests.</returns>
        public bool IsMaintainingPosition()
        {
            return maintainPosition;
        }

        #endregion

        #region Update

        /// <summary>
        /// Samples position every quarter scaled Unity second while maintenance is
        /// enabled. It clears returning inside tolerance and only auto-returns when
        /// drift exceeds twice the configured tolerance.
        /// </summary>
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

        /// <summary>Returns squared world distance from the current post.</summary>
        private float GetDistanceSquaredFromStationaryPosition()
        {
            return (transform.position - stationaryPosition).sqrMagnitude;
        }

        #endregion

        #region Gizmos

        /// <summary>Draws the post and tolerance radius for editor inspection only.</summary>
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

