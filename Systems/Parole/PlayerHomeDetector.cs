using Behind_Bars.Helpers;
using UnityEngine;
using Object = UnityEngine.Object;
using Behind_Bars.Utils;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Property;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Property;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Detects whether the player is at home (near any owned property).
    /// Used by curfew enforcement and home visit systems.
    /// </summary>
    public static class PlayerHomeDetector
    {
        private const float HOME_DETECTION_RADIUS = 30f;

        /// <summary>
        /// Check if player is at or near any owned property
        /// </summary>
        public static bool IsPlayerAtHome(Player player)
        {
            if (player == null) return false;

            try
            {
                Vector3 playerPos = player.transform.position;

                // Try to find owned properties via the game's property system
                var properties = UnityEngine.Object.FindObjectsOfType<Property>();
                if (properties == null || properties.Length == 0)
                {
                    ModLogger.Debug("[HOME] No properties found in scene");
                    return false;
                }

                foreach (var property in properties)
                {
                    if (property == null) continue;

                    // Check if this property is owned by the player
                    bool isOwned = false;
                    try
                    {
                        isOwned = property.IsOwned;
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }

                    if (!isOwned) continue;

                    float distance = Vector3.Distance(playerPos, property.transform.position);
                    if (distance <= HOME_DETECTION_RADIUS)
                    {
                        ModLogger.Debug($"[HOME] Player is within {distance:F1}m of owned property '{property.PropertyName}' (threshold: {HOME_DETECTION_RADIUS}m)");
                        return true;
                    }
                }

                return false;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[HOME] Error checking player home status: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the position of the player's closest owned property (for officer navigation)
        /// </summary>
        public static Vector3? GetClosestOwnedPropertyPosition(Player player)
        {
            if (player == null) return null;

            try
            {
                Vector3 playerPos = player.transform.position;
                float closestDistance = float.MaxValue;
                Vector3? closestPosition = null;

                var properties = UnityEngine.Object.FindObjectsOfType<Property>();
                if (properties == null) return null;

                foreach (var property in properties)
                {
                    if (property == null) continue;

                    bool isOwned = false;
                    try
                    {
                        isOwned = property.IsOwned;
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }

                    if (!isOwned) continue;

                    float distance = Vector3.Distance(playerPos, property.transform.position);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestPosition = property.transform.position;
                    }
                }

                return closestPosition;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[HOME] Error finding closest owned property: {ex.Message}");
                return null;
            }
        }
    }
}
