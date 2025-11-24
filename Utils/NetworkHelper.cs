using UnityEngine;
using Behind_Bars.Helpers;


#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.Police;
using Il2CppFishNet.Object;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.Law;
using ScheduleOne.Police;
using FishNet.Object;
#endif

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Helper utilities for FishNet networking operations
    /// Provides safe, consistent methods for network ID handling
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>
        /// Safely get the network ObjectId from a Player
        /// Returns -1 if Player or NetworkObject is null
        /// </summary>
        public static int GetPlayerNetworkId(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot get network ID - player is null");
                return -1;
            }

            if (player.NetworkObject == null)
            {
                ModLogger.Warn($"Cannot get network ID - player {player.name} has null NetworkObject");
                return -1;
            }

            return player.NetworkObject.ObjectId;
        }

        /// <summary>
        /// Safely get the network ObjectId as a string from a Player
        /// Returns empty string if Player or NetworkObject is null
        /// </summary>
        public static string GetPlayerNetworkIdString(Player player)
        {
            int objectId = GetPlayerNetworkId(player);
            if (objectId == -1)
            {
                return "";
            }

            return objectId.ToString();
        }

        /// <summary>
        /// Check if a player has a valid NetworkObject
        /// </summary>
        public static bool HasValidNetworkObject(Player player)
        {
            return player != null && player.NetworkObject != null;
        }

        /// <summary>
        /// Safely initiate foot pursuit on a police officer
        /// Handles network ID extraction and null checks
        /// </summary>
        public static bool TryBeginFootPursuit(PoliceOfficer police, Player perpetrator)
        {
            if (police == null)
            {
                ModLogger.Warn("Cannot begin foot pursuit - police officer is null");
                return false;
            }

            if (!HasValidNetworkObject(perpetrator))
            {
                ModLogger.Warn($"Cannot begin foot pursuit - player {perpetrator?.name ?? "null"} has invalid NetworkObject");
                return false;
            }

            try
            {
                // Use ObjectId directly (int) - FishNet best practice
                int networkId = perpetrator.NetworkObject.ObjectId;
                ModLogger.Debug($"Initiating foot pursuit - Officer: {police.name}, Target ID: {networkId}");
                
                // Try with int first (preferred)
                police.BeginFootPursuit_Networked(networkId.ToString());
                return true;
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Failed to begin foot pursuit: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Safely initiate body search on a police officer
        /// Handles network ID extraction and null checks
        /// </summary>
        public static bool TryBeginBodySearch(PoliceOfficer police, Player perpetrator)
        {
            if (police == null)
            {
                ModLogger.Warn("Cannot begin body search - police officer is null");
                return false;
            }

            if (!HasValidNetworkObject(perpetrator))
            {
                ModLogger.Warn($"Cannot begin body search - player {perpetrator?.name ?? "null"} has invalid NetworkObject");
                return false;
            }

            try
            {
                // Use ObjectId directly (int) - FishNet best practice
                int networkId = perpetrator.NetworkObject.ObjectId;
                ModLogger.Debug($"Initiating body search - Officer: {police.name}, Target ID: {networkId}");
                
                // Try with int first (preferred)
                police.BeginBodySearch_Networked(networkId.ToString());
                return true;
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Failed to begin body search: {e.Message}");
                return false;
            }
        }
    }
}

