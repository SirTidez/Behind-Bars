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
        /// <param name="player">The player whose network object should be inspected.</param>
        /// <returns>The FishNet ObjectId, or the helper sentinel <c>-1</c> when the player or
        /// its NetworkObject is unavailable.</returns>
        /// <remarks>
        /// The sentinel only represents this helper's null/error path; the
        /// method does not independently validate whether a non-negative ID is
        /// registered, connected, or otherwise usable by the network.
        /// </remarks>
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
        /// <param name="player">The player whose network object should be inspected.</param>
        /// <returns>The ObjectId converted to a string, or an empty string when
        /// <see cref="GetPlayerNetworkId(Player)"/> returns its <c>-1</c> sentinel.</returns>
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
        /// <param name="player">The player to inspect.</param>
        /// <returns><c>true</c> when both the player and its NetworkObject are non-null.</returns>
        /// <remarks>
        /// This is a reference-presence check only; it does not validate the
        /// ObjectId, ownership, connection state, or spawned state.
        /// </remarks>
        public static bool HasValidNetworkObject(Player player)
        {
            return player != null && player.NetworkObject != null;
        }

        /// <summary>
        /// Safely initiate foot pursuit on a police officer
        /// Handles network ID extraction and null checks
        /// </summary>
        /// <param name="police">The officer that should receive the pursuit request.</param>
        /// <param name="perpetrator">The player whose ObjectId is passed to the officer API.</param>
        /// <returns><c>true</c> when the local method invocation is accepted without throwing;
        /// <c>false</c> when input validation fails or the call throws.</returns>
        /// <remarks>
        /// A successful return confirms only that the current string-based
        /// <c>BeginFootPursuit_Networked</c> call returned normally. It does not
        /// confirm remote delivery or that the officer entered pursuit.
        /// </remarks>
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
                
                // Preserve the numeric ObjectId while satisfying the current string-based API.
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
        /// <param name="police">The officer that should receive the body-search request.</param>
        /// <param name="perpetrator">The player whose ObjectId is passed to the officer API.</param>
        /// <returns><c>true</c> when the local method invocation is accepted without throwing;
        /// <c>false</c> when input validation fails or the call throws.</returns>
        /// <remarks>
        /// A successful return confirms only that the current string-based
        /// <c>BeginBodySearch_Networked</c> call returned normally. It does not
        /// confirm remote delivery or completion of the search.
        /// </remarks>
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
                
                // Preserve the numeric ObjectId while satisfying the current string-based API.
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

