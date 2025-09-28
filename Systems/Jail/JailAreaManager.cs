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
#if MONO
    public sealed class JailAreaManager : MonoBehaviour
#else
    public sealed class JailAreaManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        [Header("Jail Areas")]
        public KitchenArea kitchen = new KitchenArea();
        public LaundryArea laundry = new LaundryArea();
        public PhoneArea phoneArea = new PhoneArea();
        public BookingArea booking = new BookingArea();
        public StorageArea storage = new StorageArea();
        public ExitScannerArea exitScanner = new ExitScannerArea();
        public GuardRoomArea guardRoom = new GuardRoomArea();
        public MainRecArea mainRec = new MainRecArea();
        public ShowerArea showers = new ShowerArea();

        [Header("Area Configuration")]
        public bool enableAreaSystem = true;
        public bool showAreaBounds = false;

        private List<JailAreaBase> allAreas = new List<JailAreaBase>();

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
                    ModLogger.Info($"✓ Initialized ExitScanner area in Hallway");
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

            ModLogger.Info($"✓ Area system initialized with {allAreas.Count} areas");
        }

        void InitializeArea<T>(T area, Transform jailRoot, string areaName) where T : JailAreaBase
        {
            Transform areaTransform = jailRoot.Find(areaName);
            if (areaTransform != null)
            {
                area.Initialize(areaTransform);
                allAreas.Add(area);
                ModLogger.Info($"✓ Initialized {areaName} area");
            }
            else
            {
                ModLogger.Warn($"⚠️ {areaName} area not found in jail structure");
            }
        }

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

        public List<JailAreaBase> GetAllAreas()
        {
            return new List<JailAreaBase>(allAreas);
        }

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

        public void LockDownAllAreas()
        {
            foreach (var area in allAreas)
            {
                area.SetAccessible(false);
            }

            ModLogger.Info("🔒 All areas locked down");
        }

        public void OpenAllAreas()
        {
            foreach (var area in allAreas)
            {
                area.SetAccessible(true);
            }

            ModLogger.Info("🔓 All areas opened");
        }

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

        public KitchenArea GetKitchen() => kitchen;
        public LaundryArea GetLaundry() => laundry;
        public PhoneArea GetPhoneArea() => phoneArea;
        public BookingArea GetBooking() => booking;
        public StorageArea GetStorage() => storage;
        public ExitScannerArea GetExitScanner() => exitScanner;
        public GuardRoomArea GetGuardRoom() => guardRoom;
        public MainRecArea GetMainRec() => mainRec;
        public ShowerArea GetShowers() => showers;

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