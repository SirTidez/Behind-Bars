using System.Collections.Generic;
using UnityEngine;
using BehindBars.Areas;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Owns the jail's serializable area models and rebuilds them from the authored scene hierarchy.
    /// Missing areas are omitted from <c>allAreas</c>; this manager does not add authorization,
    /// occupancy, navigation, or activity behavior beyond delegating to each area model.
    /// </summary>
#if MONO
    public sealed class JailAreaManager : MonoBehaviour
#else
    public sealed class JailAreaManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Jail Areas")]
#endif
        // Serialized area instances. Initialize rebuilds their scene references; callers should
        // use the accessors below when they need the manager's current discovery result.
        public KitchenArea kitchen = new KitchenArea();
        public LaundryArea laundry = new LaundryArea();
        public PhoneArea phoneArea = new PhoneArea();
        public BookingArea booking = new BookingArea();
        public StorageArea storage = new StorageArea();
        public ExitScannerArea exitScanner = new ExitScannerArea();
        public GuardRoomArea guardRoom = new GuardRoomArea();
        public MainRecArea mainRec = new MainRecArea();
        public ShowerArea showers = new ShowerArea();

#if MONO
        [Header("Area Configuration")]
#endif
        // These flags control discovery and editor gizmo output only. They do not enforce access
        // on their own and showAreaBounds has no runtime rendering effect outside gizmos.
        public bool enableAreaSystem = true;
        public bool showAreaBounds = false;

        // Initialization order is also query precedence for overlapping area colliders.
        private List<JailAreaBase> allAreas = new List<JailAreaBase>();

        /// <summary>
        /// Discover configured jail areas below <paramref name="jailRoot"/>.
        /// </summary>
        /// <param name="jailRoot">Root whose direct named children contain the area hierarchy.</param>
        /// <remarks>When disabled, the method leaves the existing discovery list untouched and returns.</remarks>
        public void Initialize(Transform jailRoot)
        {
            if (!enableAreaSystem)
            {
                ModLogger.Info("Area system disabled, skipping initialization");
                return;
            }

            InitializeJailAreas(jailRoot);
        }

        void InitializeJailAreas(Transform jailRoot)
        {
            allAreas.Clear();

            InitializeArea(kitchen, jailRoot, "Kitchen");
            InitializeArea(laundry, jailRoot, "Laundry");
            InitializeArea(phoneArea, jailRoot, "Phone");
            InitializeArea(booking, jailRoot, "Booking");
            InitializeArea(storage, jailRoot, "Storage");
            // ExitScannerStation is in Hallway, find it properly
            var hallway = jailRoot.Find("Hallway");
            if (hallway != null)
            {
                var exitScannerTransform = hallway.Find("ExitScannerStation");
                if (exitScannerTransform != null)
                {
                    exitScanner.Initialize(exitScannerTransform);
                    allAreas.Add(exitScanner);
                    ModLogger.Debug($"✓ Initialized ExitScanner area in Hallway");
                }
                else
                {
                    ModLogger.Warn($"⚠️ ExitScannerStation not found in Hallway");
                }
            }
            else
            {
                ModLogger.Warn($"⚠️ Hallway not found in jail structure");
            }
            InitializeArea(guardRoom, jailRoot, "GuardRoom");
            InitializeArea(mainRec, jailRoot, "MainRec");
            InitializeArea(showers, jailRoot, "Showers");

            ModLogger.Debug($"✓ Area system initialized with {allAreas.Count} areas");
        }

        void InitializeArea<T>(T area, Transform jailRoot, string areaName) where T : JailAreaBase
        {
            Transform areaTransform = jailRoot.Find(areaName);
            if (areaTransform != null)
            {
                area.Initialize(areaTransform);
                allAreas.Add(area);
                ModLogger.Debug($"✓ Initialized {areaName} area");
            }
            else
            {
                ModLogger.Warn($"⚠️ {areaName} area not found in jail structure");
            }
        }

        /// <summary>
        /// Return the first discovered area whose collider bounds contain a world position.
        /// </summary>
        /// <param name="playerPosition">World-space position to test.</param>
        /// <returns>The first matching area name, or <c>Unknown</c> when none match.</returns>
        /// <remarks>Overlapping areas resolve by initialization order, not by nearest or smallest bounds.</remarks>
        public string GetPlayerCurrentArea(Vector3 playerPosition)
        {
            foreach (var area in allAreas)
            {
                if (area.IsPositionInArea(playerPosition))
                {
                    return area.areaName;
                }
            }

            return "Unknown";
        }

        /// <summary>
        /// Find a discovered area by case-insensitive name.
        /// </summary>
        /// <param name="areaName">Area name to look up.</param>
        /// <returns>The first matching area, or <c>null</c> when discovery did not register it.</returns>
        /// <remarks>The current implementation expects a non-null name.</remarks>
        public JailAreaBase GetAreaByName(string areaName)
        {
            foreach (var area in allAreas)
            {
                if (area.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return area;
                }
            }

            return null;
        }

        /// <summary>
        /// Return a shallow copy of the currently discovered area list.
        /// </summary>
        /// <returns>A new list whose area objects remain manager-owned and mutable.</returns>
        public List<JailAreaBase> GetAllAreas()
        {
            return new List<JailAreaBase>(allAreas);
        }

        /// <summary>
        /// Test whether a position is inside a discovered area marked as requiring authorization.
        /// </summary>
        /// <param name="playerPosition">World-space position to test.</param>
        /// <returns><c>true</c> when any matching restricted area is found.</returns>
        /// <remarks>This checks only the model flag and collider bounds; it does not inspect player permissions.</remarks>
        public bool IsPlayerInRestrictedArea(Vector3 playerPosition)
        {
            foreach (var area in allAreas)
            {
                if (area.IsPositionInArea(playerPosition) && area.requiresAuthorization)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Delegate an accessibility change to a discovered area.
        /// </summary>
        /// <param name="areaName">Case-insensitive area name.</param>
        /// <param name="accessible">New accessibility value.</param>
        /// <remarks>Unknown names are logged and ignored.</remarks>
        public void SetAreaAccessible(string areaName, bool accessible)
        {
            var area = GetAreaByName(areaName);
            if (area != null)
            {
                area.SetAccessible(accessible);
                ModLogger.Info($"✓ Set {areaName} accessibility to: {accessible}");
            }
            else
            {
                ModLogger.Warn($"⚠️ Area not found: {areaName}");
            }
        }

        /// <summary>
        /// Mark every discovered area inaccessible and invoke its door-lock behavior.
        /// </summary>
        public void LockDownAllAreas()
        {
            foreach (var area in allAreas)
            {
                area.SetAccessible(false);
            }

            ModLogger.Info("🔒 All areas locked down");
        }

        /// <summary>
        /// Mark every discovered area accessible and invoke its door-unlock behavior.
        /// </summary>
        public void OpenAllAreas()
        {
            foreach (var area in allAreas)
            {
                area.SetAccessible(true);
            }

            ModLogger.Info("🔓 All areas opened");
        }

        /// <summary>
        /// Log the local player's current area and restricted-area result for diagnostics.
        /// </summary>
        public void TestPlayerPosition()
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null)
            {
                Vector3 playerPos = player.transform.position;
                string currentArea = GetPlayerCurrentArea(playerPos);
                bool inRestricted = IsPlayerInRestrictedArea(playerPos);

                ModLogger.Info($"Player Position Test:");
                ModLogger.Info($"  Position: {playerPos}");
                ModLogger.Info($"  Current Area: {currentArea}");
                ModLogger.Info($"  In Restricted Area: {inRestricted}");
            }
            else
            {
                ModLogger.Warn("⚠️ Player not found for position test");
            }
        }

        /// <summary>
        /// Log discovered area metadata and run the local-player position diagnostic.
        /// </summary>
        /// <remarks>This is diagnostic output only; it does not repair missing scene objects.</remarks>
        public void TestAreaSystem()
        {
            ModLogger.Info("=== TESTING AREA SYSTEM ===");
            ModLogger.Info($"Total areas: {allAreas.Count}");

            foreach (var area in allAreas)
            {
                Bounds bounds = area.GetTotalBounds();
                Vector3 center = area.GetAreaCenter();
                Vector3 size = area.GetAreaSize();

                ModLogger.Info($"Area: {area.areaName}");
                ModLogger.Info($"  Initialized: {area.isInitialized}");
                ModLogger.Info($"  Accessible: {area.isAccessible}");
                ModLogger.Info($"  Requires Auth: {area.requiresAuthorization}");
                ModLogger.Info($"  Max Occupancy: {area.maxOccupancy}");
                ModLogger.Info($"  Bounds: {bounds}");
                ModLogger.Info($"  Center: {center}");
                ModLogger.Info($"  Size: {size}");
                ModLogger.Info($"  Doors: {area.doors.Count}");
                ModLogger.Info($"  Lights: {area.lights.Count}");
            }

            TestPlayerPosition();
            ModLogger.Info("=== END AREA TEST ===");
        }

        void OnDrawGizmos()
        {
            if (!showAreaBounds || allAreas == null) return;

            foreach (var area in allAreas)
            {
                if (area == null || !area.isInitialized) continue;

                Bounds bounds = area.GetTotalBounds();

                // Set color based on area accessibility
                Gizmos.color = area.isAccessible ? Color.green : Color.red;
                if (area.requiresAuthorization)
                {
                    Gizmos.color = Color.yellow;
                }

                // Draw bounds wireframe
                Gizmos.DrawWireCube(bounds.center, bounds.size);

                // Draw area name at center
                Vector3 labelPos = bounds.center + Vector3.up * (bounds.size.y * 0.5f + 1f);

#if UNITY_EDITOR
                UnityEditor.Handles.Label(labelPos, area.areaName);
#endif
            }
        }

        void OnDrawGizmosSelected()
        {
            if (allAreas == null) return;

            foreach (var area in allAreas)
            {
                if (area == null || !area.isInitialized) continue;

                Bounds bounds = area.GetTotalBounds();

                // Solid color for selected
                Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
                Gizmos.DrawCube(bounds.center, bounds.size);

                // Draw area details
                Gizmos.color = Color.white;
                Vector3 center = area.GetAreaCenter();
                Gizmos.DrawWireSphere(center, 0.5f);
            }
        }

        /// <summary>Return the configured kitchen area model.</summary>
        public KitchenArea GetKitchen() => kitchen;
        /// <summary>Return the configured laundry area model.</summary>
        public LaundryArea GetLaundry() => laundry;
        /// <summary>Return the configured phone area model.</summary>
        public PhoneArea GetPhoneArea() => phoneArea;
        /// <summary>Return the configured booking area model.</summary>
        public BookingArea GetBooking() => booking;
        /// <summary>Return the configured storage area model.</summary>
        public StorageArea GetStorage() => storage;
        /// <summary>Return the configured exit-scanner area model.</summary>
        public ExitScannerArea GetExitScanner() => exitScanner;
        /// <summary>Return the configured guard-room area model.</summary>
        public GuardRoomArea GetGuardRoom() => guardRoom;
        /// <summary>Return the configured main-recreation area model.</summary>
        public MainRecArea GetMainRec() => mainRec;
        /// <summary>Return the configured shower area model.</summary>
        public ShowerArea GetShowers() => showers;

        /// <summary>Log the enabled state and accessibility of every discovered area.</summary>
        public void LogAreaStatus()
        {
            ModLogger.Info($"=== AREA SYSTEM STATUS ===");
            ModLogger.Info($"Enabled: {enableAreaSystem}");
            ModLogger.Info($"Total Areas: {allAreas.Count}");

            foreach (var area in allAreas)
            {
                ModLogger.Info($"  {area.areaName}: Accessible={area.isAccessible}, Auth={area.requiresAuthorization}, Doors={area.doors.Count}");
            }
            ModLogger.Info($"=========================");
        }
    }
}
