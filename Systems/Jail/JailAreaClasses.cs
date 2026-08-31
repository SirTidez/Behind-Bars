using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Systems.Jail;

namespace BehindBars.Areas
{
    /// <summary>
    /// Serializable scene-discovery model shared by the jail's named areas.
    /// Bounds, lights, doors, and anchor transforms are collected from the authored
    /// hierarchy; this layer does not itself run inmate activities or pathfinding.
    /// </summary>
    [System.Serializable]
    public abstract class JailAreaBase
    {
        // Runtime-owned identity and discovery root. Initialize implementations replace
        // these values when the scene hierarchy is scanned.
        public string areaName;
        public Transform areaRoot;
        public bool isInitialized = false;

        // Discovery results. Lists are mutable and owned by each area instance; callers
        // should not assume a missing/renamed scene object will be represented by a null
        // placeholder in every list.
        public List<Transform> bounds = new List<Transform>();
        public List<JailDoor> doors = new List<JailDoor>();
        public List<Light> lights = new List<Light>();

        // Access metadata consumed by JailAreaManager. These flags do not create
        // authorization checks or enforce occupancy on their own.
        public bool isAccessible = true;
        public bool requiresAuthorization = false;
        public int maxOccupancy = -1; // -1 = unlimited

        /// <summary>
        /// Populate this area from its authored scene root.
        /// </summary>
        /// <param name="root">Transform containing the area's named children and bounds.</param>
        public abstract void Initialize(Transform root);

        /// <summary>
        /// Toggle the area's access flag and any door state owned by the implementation.
        /// </summary>
        /// <param name="accessible">Whether the area should be marked accessible.</param>
        public abstract void SetAccessible(bool accessible);

        /// <summary>
        /// Test whether a world position is inside any discovered bound collider.
        /// </summary>
        /// <param name="position">World-space position to test.</param>
        /// <returns><c>true</c> when at least one non-null bound collider contains the position.</returns>
        /// <remarks>Only collider bounds are tested; this does not account for doors, navmesh, or authorization.</remarks>
        public virtual bool IsPositionInArea(Vector3 position)
        {
            return bounds.Any(bound =>
            {
                Collider collider = bound.GetComponent<Collider>();
                return collider != null && collider.bounds.Contains(position);
            });
        }

        /// <summary>
        /// Calculate one axis-aligned bounds value that contains all discovered colliders.
        /// </summary>
        /// <returns>The combined collider bounds, or a unit fallback around <see cref="areaRoot"/> when no bounds are assigned and a root exists.</returns>
        /// <remarks>If transforms exist but none have colliders, Unity's default empty bounds is returned.</remarks>
        public virtual Bounds GetTotalBounds()
        {
            if (bounds.Count == 0)
            {
                return areaRoot != null ? new Bounds(areaRoot.position, Vector3.one) : new Bounds();
            }

            // Start with first bounds
            Bounds totalBounds = new Bounds();
            bool firstBound = true;

            foreach (Transform bound in bounds)
            {
                Collider collider = bound.GetComponent<Collider>();
                if (collider != null)
                {
                    if (firstBound)
                    {
                        totalBounds = collider.bounds;
                        firstBound = false;
                    }
                    else
                    {
                        totalBounds.Encapsulate(collider.bounds);
                    }
                }
            }

            return totalBounds;
        }

        /// <summary>
        /// Return the center of <see cref="GetTotalBounds"/>.
        /// </summary>
        public virtual Vector3 GetAreaCenter()
        {
            return GetTotalBounds().center;
        }

        /// <summary>
        /// Return the size of <see cref="GetTotalBounds"/>.
        /// </summary>
        public virtual Vector3 GetAreaSize()
        {
            return GetTotalBounds().size;
        }

        /// <summary>
        /// Test the aggregate axis-aligned bounds against another area's aggregate bounds.
        /// </summary>
        /// <param name="otherArea">Area to compare with.</param>
        /// <returns><c>true</c> when the two aggregate bounds intersect.</returns>
        /// <remarks>No null guard is applied to <paramref name="otherArea"/>.</remarks>
        public virtual bool OverlapsWith(JailAreaBase otherArea)
        {
            return GetTotalBounds().Intersects(otherArea.GetTotalBounds());
        }

        protected virtual void FindAreaBounds(Transform root)
        {
            bounds.Clear();

            // IL2CPP-safe recursive search  
            FindTransformsRecursive(root,
                name => name.Contains("Bounds"),
                transform => bounds.Add(transform));
        }

        // Recursive name search is used instead of broad scene queries. The Func/Action
        // callbacks remain protected implementation detail on this serializable data model;
        // they are not part of an injected MonoBehaviour API surface.
        protected void FindTransformsRecursive(Transform parent, System.Func<string, bool> nameCheck, System.Action<Transform> onFound)
        {
            // Check current transform
            if (nameCheck(parent.name))
            {
                onFound(parent);
            }

            // Check all children
            for (int i = 0; i < parent.childCount; i++)
            {
                FindTransformsRecursive(parent.GetChild(i), nameCheck, onFound);
            }
        }

        protected virtual void FindAreaLights(Transform root)
        {
            lights.Clear();
            Light[] areaLights = root.GetComponentsInChildren<Light>();
            lights.AddRange(areaLights);
        }

        /// <summary>
        /// Enable or disable every discovered light in the area.
        /// </summary>
        /// <param name="enabled">Whether discovered lights should be enabled.</param>
        public virtual void ToggleLights(bool enabled)
        {
            foreach (var light in lights)
            {
                if (light != null)
                {
                    light.enabled = enabled;
                }
            }
        }

        /// <summary>
        /// Lock every valid door currently registered by the area.
        /// </summary>
        public virtual void LockAllDoors()
        {
            foreach (var door in doors)
            {
                if (door.IsValid())
                {
                    door.LockDoor();
                }
            }
        }

        /// <summary>
        /// Unlock every valid door currently registered by the area.
        /// </summary>
        public virtual void UnlockAllDoors()
        {
            foreach (var door in doors)
            {
                if (door.IsValid())
                {
                    door.UnlockDoor();
                }
            }
        }
    }

    /// <summary>
    /// Scene anchors and access flags for the kitchen area.
    /// Meal-prep state is scaffolding: <see cref="StartMealPrep"/> only flips a flag and
    /// does not run a cooking mini-game or award inventory.
    /// </summary>
    [System.Serializable]
    public class KitchenArea : JailAreaBase
    {
        // Discovered kitchen interaction anchors. They are scene references only; no
        // station behavior is attached by this data class.
        public List<Transform> cookingStations = new List<Transform>();
        public List<Transform> storageAreas = new List<Transform>();
        public bool kitchenOperational = true;

        // Prototype activity settings retained for authoring/configuration. The current
        // implementation does not consume the timing or concurrency values.
        public bool miniGameEnabled = false;
        public float mealPrepTimeLimit = 300f; // 5 minutes
        public int maxSimultaneousCooks = 4;

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Kitchen";
            maxOccupancy = 8; // Kitchen capacity
            requiresAuthorization = true; // Kitchen requires supervision

            FindAreaBounds(root);
            FindAreaLights(root);
            FindKitchenComponents(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized Kitchen Area - {bounds.Count} bounds, {doors.Count} doors, {cookingStations.Count} stations");
        }

        void FindKitchenComponents(Transform root)
        {
            cookingStations.Clear();
            storageAreas.Clear();

            // IL2CPP-safe recursive search
            FindTransformsRecursive(root,
                name => name.Contains("Cooking") || name.Contains("Stove") || name.Contains("Prep"),
                transform => cookingStations.Add(transform));

            FindTransformsRecursive(root,
                name => name.Contains("Storage") || name.Contains("Pantry") || name.Contains("Fridge"),
                transform => storageAreas.Add(transform));
        }


        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;
            if (!accessible)
            {
                LockAllDoors();
                kitchenOperational = false;
                Debug.Log("🔒 Kitchen locked down - no cooking allowed");
            }
            else
            {
                UnlockAllDoors();
                kitchenOperational = true;
                Debug.Log("🔓 Kitchen operational - cooking allowed");
            }
        }

        /// <summary>
        /// Mark meal preparation as started when the kitchen is accessible.
        /// </summary>
        /// <remarks>This is a prototype state toggle; it does not start a mini-game or consume ingredients.</remarks>
        public void StartMealPrep()
        {
            if (!kitchenOperational || !isAccessible)
            {
                Debug.LogWarning("Cannot start meal prep - kitchen not operational");
                return;
            }

            miniGameEnabled = true;
            Debug.Log("🍳 Meal preparation started");
        }
    }

    /// <summary>
    /// Scene anchors and access flags for the laundry area.
    /// The laundry mini-game API is currently a flag/reward calculation stub; it does not
    /// validate a load or apply sentence credit by itself.
    /// </summary>
    [System.Serializable]
    public class LaundryArea : JailAreaBase
    {
        // Discovered laundry interaction anchors. Lists are rebuilt on Initialize.
        public List<Transform> washingMachines = new List<Transform>();
        public List<Transform> dryingAreas = new List<Transform>();
        public List<Transform> clothingCollectionPoints = new List<Transform>();
        public bool laundryOperational = true;

        // Prototype activity settings. Only miniGameEnabled and the reduction scalar are
        // read by the current public methods; timing/concurrency values are informational.
        public bool miniGameEnabled = false;
        public float washCycleTime = 120f; // 2 minutes per load
        public int maxSimultaneousLoads = 6;
        public float sentenceReductionPerLoad = 0.5f; // 0.5 hours per perfect load

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Laundry";
            maxOccupancy = 6;
            requiresAuthorization = false; // Inmates can use laundry freely

            FindAreaBounds(root);
            FindAreaLights(root);
            FindLaundryComponents(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized Laundry Area - {bounds.Count} bounds, {washingMachines.Count} machines, {clothingCollectionPoints.Count} collection points");
        }

        void FindLaundryComponents(Transform root)
        {
            washingMachines.Clear();
            dryingAreas.Clear();
            clothingCollectionPoints.Clear();

            // IL2CPP-safe recursive search
            FindTransformsRecursive(root,
                name => name.Contains("Washing") || name.Contains("Machine"),
                transform => washingMachines.Add(transform));

            FindTransformsRecursive(root,
                name => name.Contains("Dry") || name.Contains("Hang"),
                transform => dryingAreas.Add(transform));

            FindTransformsRecursive(root,
                name => name.Contains("Collection") || name.Contains("Basket") || name.Contains("Clothing"),
                transform => clothingCollectionPoints.Add(transform));
        }


        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;
            laundryOperational = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Laundry closed - no washing allowed");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Laundry open - washing available");
            }
        }

        /// <summary>
        /// Mark the prototype laundry activity as started when the area is accessible.
        /// </summary>
        /// <remarks>No activity UI, timing loop, or sentence update is performed here.</remarks>
        public void StartLaundryMiniGame()
        {
            if (!laundryOperational || !isAccessible)
            {
                Debug.LogWarning("Cannot start laundry mini-game - laundry not operational");
                return;
            }

            miniGameEnabled = true;
            Debug.Log("🧺 Laundry mini-game started");
        }

        /// <summary>
        /// Convert a caller-supplied laundry quality score into a sentence-reduction value.
        /// </summary>
        /// <param name="qualityScore">Unvalidated quality multiplier supplied by the caller.</param>
        /// <returns>Configured reduction multiplied by <paramref name="qualityScore"/>, or zero if the prototype is not active.</returns>
        /// <remarks>This method does not clamp the score, reset the activity, or apply the returned reduction to a sentence.</remarks>
        public float CompleteLaundryLoad(float qualityScore)
        {
            if (!miniGameEnabled) return 0f;

            float reductionAmount = sentenceReductionPerLoad * qualityScore;
            Debug.Log($"🎯 Laundry load completed! Quality: {qualityScore:F2}, Sentence reduction: {reductionAmount:F2} hours");
            return reductionAmount;
        }
    }

    /// <summary>
    /// Scene anchors and access flags for the phone area.
    /// This model does not create calls, enforce monitoring, or implement the call timer.
    /// </summary>
    [System.Serializable]
    public class PhoneArea : JailAreaBase
    {
        // Phone transforms discovered by name; the call system, if any, owns interaction.
        public List<Transform> phoneBooths = new List<Transform>();
        public float callTimeLimit = 900f; // 15 minutes
        public bool callsMonitored = true;

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Phone Area";
            maxOccupancy = 12; // Based on number of phones
            requiresAuthorization = true; // Calls need approval

            FindAreaBounds(root);
            FindAreaLights(root);
            FindPhones(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized Phone Area - {bounds.Count} bounds, {phoneBooths.Count} phones");
        }

        void FindPhones(Transform root)
        {
            phoneBooths.Clear();

            // IL2CPP-safe recursive search
            FindTransformsRecursive(root,
                name => name.Contains("Phone"),
                transform => phoneBooths.Add(transform));
        }


        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Phone area closed - no calls allowed");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Phone area open - calls permitted");
            }
        }
    }

    /// <summary>
    /// Scene references for booking stations, guard points, and booking doors.
    /// Booking flow/state ownership remains with the station and BookingProcess classes.
    /// </summary>
    [System.Serializable]
    public class BookingArea : JailAreaBase
    {
        // Door metadata is created from exact authored child names during initialization.
        public JailDoor prisonEntryDoor;
        public JailDoor bookingInnerDoor;
        public JailDoor guardDoor;

        // Station/spawn anchors used by booking orchestration; missing authored children
        // may be represented by null entries in guardSpawns.
        public List<Transform> processingStations = new List<Transform>();
        public List<Transform> guardSpawns = new List<Transform>();

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Booking";
            maxOccupancy = 4; // Limited processing capacity
            requiresAuthorization = true; // Guards only

            FindAreaBounds(root);
            FindAreaLights(root);
            FindBookingComponents(root);
            FindBookingDoors(root);

            guardSpawns.Add(root.Find("GuardSpawn[0]"));
            guardSpawns.Add(root.Find("GuardSpawn[1]"));

            isInitialized = true;
            Debug.Log($"✓ Initialized Booking Area - {bounds.Count} bounds, {processingStations.Count} stations, {doors.Count} doors");
        }

        void FindBookingComponents(Transform root)
        {
            processingStations.Clear();

            // Find stations using exact names from JAIL_STRUCTURE_DOCUMENTATION.md
            Transform mugshotStation = root.Find("MugshotStation");
            if (mugshotStation != null)
            {
                processingStations.Add(mugshotStation);
                Debug.Log($"✓ Found MugshotStation with GuardPoint: {mugshotStation.Find("GuardPoint") != null}");
            }
            else
            {
                Debug.LogWarning("⚠️ MugshotStation not found in Booking area");
            }

            Transform scannerStation = root.Find("ScannerStation");
            if (scannerStation != null)
            {
                processingStations.Add(scannerStation);
                Debug.Log($"✓ Found ScannerStation with GuardPoint: {scannerStation.Find("GuardPoint") != null}");
            }
            else
            {
                Debug.LogWarning("⚠️ ScannerStation not found in Booking area");
            }
        }

        void FindBookingDoors(Transform root)
        {
            doors.Clear();

            // Find doors using exact static paths from hierarchy
            Transform prisonEnterTransform = root.Find("Prison_EnterDoor");
            if (prisonEnterTransform != null)
            {
                prisonEntryDoor = new JailDoor();
                prisonEntryDoor.doorHolder = prisonEnterTransform;
                prisonEntryDoor.doorName = "Prison Enter Door";
                prisonEntryDoor.doorType = JailDoor.DoorType.EntryDoor;
                prisonEntryDoor.currentState = JailDoor.DoorState.Closed;

                // Find door points for SecurityDoor integration
                prisonEntryDoor.doorPoint = prisonEnterTransform.Find("DoorPoint_Hall");
                if (prisonEntryDoor.doorPoint == null)
                    prisonEntryDoor.doorPoint = prisonEnterTransform.Find("DoorPoint_Prison");

                doors.Add(prisonEntryDoor);
                Debug.Log($"✓ Found Prison Enter Door at {prisonEnterTransform.name} with doorPoint: {prisonEntryDoor.doorPoint?.name}");
            }

            Transform bookingInnerTransform = root.Find("Booking_InnerDoor");
            if (bookingInnerTransform != null)
            {
                bookingInnerDoor = new JailDoor();
                bookingInnerDoor.doorHolder = bookingInnerTransform;
                bookingInnerDoor.doorName = "Booking Inner Door";
                bookingInnerDoor.doorType = JailDoor.DoorType.AreaDoor;
                bookingInnerDoor.currentState = JailDoor.DoorState.Closed;

                // Find door points for SecurityDoor integration
                bookingInnerDoor.doorPoint = bookingInnerTransform.Find("DoorPoint_Booking");
                if (bookingInnerDoor.doorPoint == null)
                    bookingInnerDoor.doorPoint = bookingInnerTransform.Find("DoorPoint_Hall");

                doors.Add(bookingInnerDoor);
                Debug.Log($"✓ Found Booking Inner Door at {bookingInnerTransform.name} with doorPoint: {bookingInnerDoor.doorPoint?.name}");
            }

            Transform guardDoorTransform = root.Find("Booking_GuardDoor");
            if (guardDoorTransform != null)
            {
                guardDoor = new JailDoor();
                guardDoor.doorHolder = guardDoorTransform;
                guardDoor.doorName = "Booking Guard Door";
                guardDoor.doorType = JailDoor.DoorType.GuardDoor;
                guardDoor.currentState = JailDoor.DoorState.Closed;

                // Find door points for SecurityDoor integration
                guardDoor.doorPoint = guardDoorTransform.Find("DoorPoint_GuardRoom");
                if (guardDoor.doorPoint == null)
                    guardDoor.doorPoint = guardDoorTransform.Find("DoorPoint_Booking");

                doors.Add(guardDoor);
                Debug.Log($"✓ Found Booking Guard Door at {guardDoorTransform.name} with doorPoint: {guardDoor.doorPoint?.name}");
            }
        }

        /// <summary>
        /// Instantiate missing booking doors from the supplied steel-door prefab.
        /// </summary>
        /// <param name="steelDoorPrefab">Prefab used for each valid, not-yet-instantiated door.</param>
        /// <remarks>Existing door instances are left in place; invalid door metadata is skipped.</remarks>
        public void InstantiateDoors(GameObject steelDoorPrefab)
        {
            if (steelDoorPrefab == null)
            {
                Debug.LogError("BookingArea: No steel door prefab provided for door instantiation");
                return;
            }

            int instantiated = 0;
            foreach (var door in doors)
            {
                if (door.IsValid() && !door.IsInstantiated())
                {
                    InstantiateSingleDoor(door, steelDoorPrefab);
                    instantiated++;
                }
            }

            Debug.Log($"BookingArea: Instantiated {instantiated}/{doors.Count} doors");
        }

        void InstantiateSingleDoor(JailDoor door, GameObject doorPrefab)
        {
            if (door.doorHolder == null) return;

            // Clear existing door
            if (door.doorInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(door.doorInstance);
            }

            // Instantiate new door
            door.doorInstance = UnityEngine.Object.Instantiate(doorPrefab, door.doorHolder);
            door.doorInstance.transform.localPosition = Vector3.zero;
            door.doorInstance.transform.localRotation = Quaternion.identity;

            // Find the hinge (look for a child transform that could be the hinge)
            door.doorHinge = FindDoorHinge(door.doorInstance);

            // Initialize the door animation system
            door.InitializeDoor();

            Debug.Log($"✓ Instantiated {door.doorName} with hinge: {door.doorHinge?.name ?? "None"}");
        }

        Transform FindDoorHinge(GameObject doorInstance)
        {
            // Look for common hinge names
            string[] hingeNames = { "Hinge", "Pivot", "Door", "DoorMesh", "Model" };

            foreach (string hingeName in hingeNames)
            {
                Transform hinge = doorInstance.transform.Find(hingeName);
                if (hinge != null) return hinge;
            }

            // Fallback: use the first child if any
            if (doorInstance.transform.childCount > 0)
            {
                return doorInstance.transform.GetChild(0);
            }

            return doorInstance.transform;
        }


        /// <summary>
        /// Get door by name for SecurityDoor integration - avoids discovery each time
        /// </summary>
        public JailDoor GetDoorByName(string doorName)
        {
            if (doorName.Contains("Prison_Enter") || doorName.Contains("Prison Enter") || doorName.Contains("Prison_EnterDoor"))
                return prisonEntryDoor;
            if (doorName.Contains("Booking_Inner") || doorName.Contains("Booking Inner") || doorName.Contains("Booking_InnerDoor"))
                return bookingInnerDoor;
            if (doorName.Contains("Booking_Guard") || doorName.Contains("Booking Guard") || doorName.Contains("Booking_GuardDoor"))
                return guardDoor;

            return null;
        }

        /// <summary>
        /// Get door point by name for SecurityDoor integration - avoids discovery each time
        /// </summary>
        public Transform GetDoorPointByName(string pointName)
        {
            // Search all door points in this booking area
            foreach (var door in doors)
            {
                if (door?.doorHolder != null)
                {
                    // Check all children for matching door point names
                    Transform[] children = door.doorHolder.GetComponentsInChildren<Transform>();
                    foreach (Transform child in children)
                    {
                        if (child.name.Equals(pointName, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return child;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Collect authored child transforms whose names start with <c>DoorPoint_</c>.
        /// </summary>
        /// <returns>A new name-to-transform map; duplicate names overwrite earlier entries.</returns>
        public Dictionary<string, Transform> GetAllDoorPoints()
        {
            var doorPoints = new Dictionary<string, Transform>();

            foreach (var door in doors)
            {
                if (door?.doorHolder != null)
                {
                    Transform[] children = door.doorHolder.GetComponentsInChildren<Transform>();
                    foreach (Transform child in children)
                    {
                        if (child.name.StartsWith("DoorPoint_"))
                        {
                            doorPoints[child.name] = child;
                        }
                    }
                }
            }

            return doorPoints;
        }

        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Booking area secured");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Booking area operational");
            }
        }

        /// <summary>
        /// Return the processing station named <c>MugshotStation</c>, if discovered.
        /// </summary>
        public Transform GetMugshotStation()
        {
            foreach (var station in processingStations)
            {
                if (station.name == "MugshotStation")
                    return station;
            }
            return null;
        }

        /// <summary>
        /// Return the processing station named <c>ScannerStation</c>, if discovered.
        /// </summary>
        public Transform GetScannerStation()
        {
            foreach (var station in processingStations)
            {
                if (station.name == "ScannerStation")
                    return station;
            }
            return null;
        }

        /// <summary>
        /// Resolve a booking station and return its direct <c>GuardPoint</c> child.
        /// </summary>
        /// <param name="stationName">Known station name or substring used for fallback matching.</param>
        /// <returns>The station's direct guard point, or <c>null</c> when no station/point is found.</returns>
        public Transform GetStationGuardPoint(string stationName)
        {
            Transform station = null;

            switch (stationName)
            {
                case "MugshotStation":
                    station = GetMugshotStation();
                    break;
                case "ScannerStation":
                    station = GetScannerStation();
                    break;
                default:
                    // Try to find by name in processing stations
                    foreach (var s in processingStations)
                    {
                        if (s.name.Contains(stationName))
                        {
                            station = s;
                            break;
                        }
                    }
                    break;
            }

            return station?.Find("GuardPoint");
        }
    }

    /// <summary>
    /// Scene anchors for the inventory storage area.
    /// Inventory transfer and persistence are implemented by the station components, not
    /// by this area model.
    /// </summary>
    [System.Serializable]
    public class StorageArea : JailAreaBase
    {
        // Authored station/guard anchors. These can remain null when hierarchy discovery
        // cannot find the expected child names.
        public Transform inventoryDropOff;
        public Transform inventoryPickup;
        public Transform guardPoint;

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Storage";
            maxOccupancy = 2; // Limited processing capacity
            requiresAuthorization = true; // Guards only

            FindAreaBounds(root);
            FindAreaLights(root);
            FindStorageComponents(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized Storage Area - GuardPoint: {guardPoint != null}, DropOff: {inventoryDropOff != null}, Pickup: {inventoryPickup != null}");
        }

        void FindStorageComponents(Transform root)
        {
            // Find the GuardPoint for supervision
            guardPoint = root.Find("GuardPoint");
            if (guardPoint == null)
            {
                Debug.LogWarning("⚠️ GuardPoint not found in Storage area");
            }

            // Find inventory stations
            inventoryDropOff = root.Find("InventoryDropOff");
            if (inventoryDropOff == null)
            {
                Debug.LogWarning("⚠️ InventoryDropOff not found in Storage area");
            }

            inventoryPickup = root.Find("InventoryPickup");
            if (inventoryPickup == null)
            {
                Debug.LogWarning("⚠️ InventoryPickup not found in Storage area");
            }
        }

        /// <summary>
        /// Return the discovered Storage guard-point anchor.
        /// </summary>
        public Transform GetGuardPoint()
        {
            return guardPoint;
        }

        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Storage area secured");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Storage area operational");
            }
        }
    }

    /// <summary>
    /// Scene anchors for the guard room.
    /// Monitor, locker, and guard-spawn lists are discovery data only; this class does not
    /// spawn guards or implement monitor behavior.
    /// </summary>
    [System.Serializable]
    public class GuardRoomArea : JailAreaBase
    {
        // Discovery results for guard-room scene objects.
        public List<Transform> monitorStations = new List<Transform>();
        public List<Transform> equipmentLockers = new List<Transform>();
        public List<Transform> guardSpawns = new List<Transform>();
            
        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Guard Room";
            maxOccupancy = 6; // Guard capacity
            requiresAuthorization = true; // Guards only

            FindAreaBounds(root);
            FindAreaLights(root);
            FindGuardComponents(root);

            guardSpawns.Add(root.Find("GuardSpawn[0]"));
            guardSpawns.Add(root.Find("GuardSpawn[1]"));

            isInitialized = true;
            Debug.Log($"✓ Initialized Guard Room - {bounds.Count} bounds, {monitorStations.Count} stations");
        }

        void FindGuardComponents(Transform root)
        {
            monitorStations.Clear();
            equipmentLockers.Clear();

            // IL2CPP-safe recursive search
            FindTransformsRecursive(root,
                name => name.Contains("Monitor") || name.Contains("Station"),
                transform => monitorStations.Add(transform));

            FindTransformsRecursive(root,
                name => name.Contains("Locker") || name.Contains("Equipment"),
                transform => equipmentLockers.Add(transform));
        }


        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Guard room secured");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Guard room accessible");
            }
        }
    }

    /// <summary>
    /// Scene anchors and access flag for the main recreation area.
    /// Recreation scheduling and inmate movement are owned by JailLifecycleManager and NPC
    /// systems; this class only exposes the area flag and door/light helpers.
    /// </summary>
    [System.Serializable]
    public class MainRecArea : JailAreaBase
    {
        // Recreation scene anchors discovered by keyword search.
        public List<Transform> recreationEquipment = new List<Transform>();
        public List<Transform> seatingAreas = new List<Transform>();
        public bool recreationTime = true;

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Main Recreation";
            maxOccupancy = 20; // Large recreational capacity
            requiresAuthorization = false; // Open to inmates

            FindAreaBounds(root);
            FindAreaLights(root);
            FindRecreationComponents(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized Main Rec Area - {bounds.Count} bounds, {recreationEquipment.Count} equipment");
        }

        void FindRecreationComponents(Transform root)
        {
            recreationEquipment.Clear();
            seatingAreas.Clear();

            // IL2CPP-safe recursive search
            FindTransformsRecursive(root,
                name => name.Contains("Equipment") || name.Contains("Game") || name.Contains("Exercise"),
                transform => recreationEquipment.Add(transform));

            FindTransformsRecursive(root,
                name => name.Contains("Seat") || name.Contains("Bench") || name.Contains("Table"),
                transform => seatingAreas.Add(transform));
        }


        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;
            recreationTime = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Recreation time ended");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Recreation time active");
            }
        }
    }

    /// <summary>
    /// Scene anchors and access flags for the shower area.
    /// The shower timer is configuration data only; no shower interaction is implemented
    /// in this model.
    /// </summary>
    [System.Serializable]
    public class ShowerArea : JailAreaBase
    {
        // Shower stall transforms discovered by keyword search.
        public List<Transform> showerStalls = new List<Transform>();
        public float showerTimeLimit = 600f; // 10 minutes
        public bool showersOperational = true;

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "Showers";
            maxOccupancy = 8; // Shower capacity
            requiresAuthorization = false; // Open access

            FindAreaBounds(root);
            FindAreaLights(root);
            FindShowerComponents(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized Shower Area - {bounds.Count} bounds, {showerStalls.Count} stalls");
        }

        void FindShowerComponents(Transform root)
        {
            showerStalls.Clear();

            // IL2CPP-safe recursive search
            FindTransformsRecursive(root,
                name => name.Contains("Shower") || name.Contains("Stall"),
                transform => showerStalls.Add(transform));
        }


        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;
            showersOperational = accessible;

            if (!accessible)
            {
                LockAllDoors();
                Debug.Log("🔒 Showers closed");
            }
            else
            {
                UnlockAllDoors();
                Debug.Log("🔓 Showers operational");
            }
        }
    }

    /// <summary>
    /// Scene references for the exit scanner, trigger, guard point, and release door.
    /// Scanner interaction and release completion are owned by ExitScannerStation; this
    /// model only discovers the anchors and toggles area/door accessibility.
    /// </summary>
    [System.Serializable]
    public class ExitScannerArea : JailAreaBase
    {
        // The scanner station root, supervision point, and release trigger are discovered
        // from the authored ExitScanner hierarchy (with sibling fallbacks for the trigger/door).
        public Transform scannerStation;
        public Transform guardPoint;
        public Transform exitTrigger;
        public JailDoor exitDoor;
        public bool scannerOperational = true;

        public override void Initialize(Transform root)
        {
            areaRoot = root;
            areaName = "ExitScanner";
            maxOccupancy = 2; // Guard + prisoner
            requiresAuthorization = true; // Requires guard supervision

            FindAreaBounds(root);
            FindAreaLights(root);
            FindExitScannerComponents(root);

            isInitialized = true;
            Debug.Log($"✓ Initialized ExitScanner Area - Scanner: {scannerStation != null}, GuardPoint: {guardPoint != null}, ExitTrigger: {exitTrigger != null}");
        }

        void FindExitScannerComponents(Transform root)
        {
            // Find the scanner station itself
            scannerStation = root;

            // Find the GuardPoint for supervision
            guardPoint = root.Find("GuardPoint");
            if (guardPoint == null)
            {
                Debug.LogWarning("⚠️ GuardPoint not found in ExitScanner area");
            }

            // Find the exit trigger
            var triggerTransform = root.Find("ExitTrigger");
            if (triggerTransform == null)
            {
                // Try looking for it as a sibling (outside the scanner station)
                triggerTransform = root.parent?.Find("ExitTrigger");
            }
            exitTrigger = triggerTransform;

            if (exitTrigger == null)
            {
                Debug.LogWarning("⚠️ ExitTrigger not found in ExitScanner area");
            }

            // Find exit door and create JailDoor structure (like BookingArea does)
            // Based on Unity hierarchy: ExitDoor is a sibling of ExitScannerStation in Hallway
            var doorTransform = root.parent?.Find("ExitDoor");
            if (doorTransform == null)
            {
                // Fallback: try as direct child (shouldn't happen based on hierarchy)
                doorTransform = root.Find("ExitDoor");
            }

            if (doorTransform != null)
            {
                // Create new JailDoor structure during initialization
                exitDoor = new JailDoor();
                exitDoor.doorHolder = doorTransform;
                exitDoor.doorName = "Exit Door";
                exitDoor.doorType = JailDoor.DoorType.GuardDoor; // Uses same prefab as other guard doors
                exitDoor.currentState = JailDoor.DoorState.Closed;
                exitDoor.reverseDirection = true; // Exit door opens in opposite direction

                // Add to doors list for area management
                doors.Add(exitDoor);

                Debug.Log($"✓ Created ExitDoor JailDoor structure at {doorTransform.name} with reversed direction");
            }
            else
            {
                Debug.LogWarning("⚠️ ExitDoor GameObject not found in ExitScanner area");
            }
        }

        public override void SetAccessible(bool accessible)
        {
            isAccessible = accessible;
            scannerOperational = accessible;

            if (!accessible)
            {
                LockAllDoors();
                if (exitDoor != null && exitDoor.IsValid())
                {
                    exitDoor.LockDoor();
                }
                Debug.Log("🔒 Exit scanner area locked - no exits allowed");
            }
            else
            {
                UnlockAllDoors();
                if (exitDoor != null && exitDoor.IsValid())
                {
                    exitDoor.UnlockDoor();
                }
                Debug.Log("🔓 Exit scanner area accessible");
            }
        }

        /// <summary>
        /// Open the discovered exit door when the scanner area is operational.
        /// </summary>
        /// <remarks>This delegates to the local <see cref="JailDoor"/> model; release completion remains station-owned.</remarks>
        public void OpenExitDoor()
        {
            if (exitDoor != null && exitDoor.IsValid() && scannerOperational)
            {
                exitDoor.OpenDoor();
                Debug.Log("🚪 Exit door opened after successful scan");
            }
        }

        /// <summary>
        /// Close the discovered exit door, when valid.
        /// </summary>
        public void CloseExitDoor()
        {
            if (exitDoor != null && exitDoor.IsValid())
            {
                exitDoor.CloseDoor();
                Debug.Log("🚪 Exit door closed");
            }
        }

        /// <summary>
        /// Check whether the discovered exit door reports an open state.
        /// </summary>
        public bool IsExitDoorOpen()
        {
            return exitDoor != null && exitDoor.IsValid() && exitDoor.IsOpen();
        }

        /// <summary>
        /// Instantiate the missing exit door from the provided prefab.
        /// </summary>
        /// <param name="steelDoorPrefab">Prefab used for the exit door.</param>
        /// <remarks>The exit door is only created when its authored holder is valid and no instance exists.</remarks>
        public void InstantiateDoors(GameObject steelDoorPrefab)
        {
            if (steelDoorPrefab == null)
            {
                Debug.LogError("ExitScannerArea: No steel door prefab provided for door instantiation");
                return;
            }

            if (exitDoor != null && exitDoor.IsValid() && !exitDoor.IsInstantiated())
            {
                InstantiateSingleDoor(exitDoor, steelDoorPrefab);
                Debug.Log("✓ ExitScannerArea: Exit door instantiated successfully");
            }
            else
            {
                Debug.LogWarning($"ExitScannerArea: Cannot instantiate exit door - Valid: {exitDoor?.IsValid()}, Already instantiated: {exitDoor?.IsInstantiated()}");
            }
        }

        void InstantiateSingleDoor(JailDoor door, GameObject doorPrefab)
        {
            if (door.doorHolder == null) return;

            // Clear existing door
            if (door.doorInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(door.doorInstance);
            }

            // Instantiate new door
            door.doorInstance = UnityEngine.Object.Instantiate(doorPrefab, door.doorHolder);
            door.doorInstance.transform.localPosition = Vector3.zero;
            door.doorInstance.transform.localRotation = Quaternion.identity;

            // Find the hinge (look for a child transform that could be the hinge)
            door.doorHinge = FindDoorHinge(door.doorInstance);

            // Initialize the door animation system
            door.InitializeDoor();

            Debug.Log($"✓ Instantiated {door.doorName} with hinge: {door.doorHinge?.name ?? "None"}");
        }

        Transform FindDoorHinge(GameObject doorInstance)
        {
            // Look for common hinge names
            string[] hingeNames = { "Hinge", "Pivot", "Door", "DoorMesh", "Model", "HingePoint" };

            foreach (string hingeName in hingeNames)
            {
                Transform hinge = doorInstance.transform.Find(hingeName);
                if (hinge != null) return hinge;
            }

            // Fallback: use the first child if any
            if (doorInstance.transform.childCount > 0)
            {
                return doorInstance.transform.GetChild(0);
            }

            return doorInstance.transform;
        }
    }
}
