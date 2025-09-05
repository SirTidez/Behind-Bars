using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BehindBars.Areas
{
    [System.Serializable]
    public abstract class JailAreaBase
    {
        public string areaName;
        public Transform areaRoot;
        public bool isInitialized = false;

        public List<Transform> bounds = new List<Transform>();
        public List<JailDoor> doors = new List<JailDoor>();
        public List<Light> lights = new List<Light>();

        public bool isAccessible = true;
        public bool requiresAuthorization = false;
        public int maxOccupancy = -1; // -1 = unlimited

        public abstract void Initialize(Transform root);
        public abstract void SetAccessible(bool accessible);

        // Unified bounds calculation - works for all areas
        public virtual bool IsPositionInArea(Vector3 position)
        {
            return bounds.Any(bound =>
            {
                Collider collider = bound.GetComponent<Collider>();
                return collider != null && collider.bounds.Contains(position);
            });
        }

        // Calculate combined bounds of all area bounds
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

        // Get center point of the area
        public virtual Vector3 GetAreaCenter()
        {
            return GetTotalBounds().center;
        }

        // Get area size
        public virtual Vector3 GetAreaSize()
        {
            return GetTotalBounds().size;
        }

        // Check if area overlaps with another area
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

        // Generic helper method for IL2CPP-safe Transform searching
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

    [System.Serializable]
    public class KitchenArea : JailAreaBase
    {
        public List<Transform> cookingStations = new List<Transform>();
        public List<Transform> storageAreas = new List<Transform>();
        public bool kitchenOperational = true;

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

    [System.Serializable]
    public class LaundryArea : JailAreaBase
    {
        public List<Transform> washingMachines = new List<Transform>();
        public List<Transform> dryingAreas = new List<Transform>();
        public List<Transform> clothingCollectionPoints = new List<Transform>();
        public bool laundryOperational = true;

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

        public float CompleteLaundryLoad(float qualityScore)
        {
            if (!miniGameEnabled) return 0f;

            float reductionAmount = sentenceReductionPerLoad * qualityScore;
            Debug.Log($"🎯 Laundry load completed! Quality: {qualityScore:F2}, Sentence reduction: {reductionAmount:F2} hours");
            return reductionAmount;
        }
    }

    [System.Serializable]
    public class PhoneArea : JailAreaBase
    {
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

    [System.Serializable]
    public class BookingArea : JailAreaBase
    {
        public JailDoor prisonEntryDoor;
        public JailDoor bookingInnerDoor;
        public JailDoor guardDoor;
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

            // IL2CPP-safe recursive search for processing stations
            FindTransformsRecursive(root,
                name => name.Contains("Processing") || name.Contains("Desk"),
                transform => processingStations.Add(transform));
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
                doors.Add(prisonEntryDoor);
                Debug.Log($"✓ Found Prison Enter Door at {prisonEnterTransform.name}");
            }

            Transform bookingInnerTransform = root.Find("Booking_InnerDoor");
            if (bookingInnerTransform != null)
            {
                bookingInnerDoor = new JailDoor();
                bookingInnerDoor.doorHolder = bookingInnerTransform;
                bookingInnerDoor.doorName = "Booking Inner Door";
                bookingInnerDoor.doorType = JailDoor.DoorType.AreaDoor;
                bookingInnerDoor.currentState = JailDoor.DoorState.Closed;
                doors.Add(bookingInnerDoor);
                Debug.Log($"✓ Found Booking Inner Door at {bookingInnerTransform.name}");
            }

            Transform guardDoorTransform = root.Find("Booking_GuardDoor");
            if (guardDoorTransform != null)
            {
                guardDoor = new JailDoor();
                guardDoor.doorHolder = guardDoorTransform;
                guardDoor.doorName = "Booking Guard Door";
                guardDoor.doorType = JailDoor.DoorType.GuardDoor;
                guardDoor.currentState = JailDoor.DoorState.Closed;
                doors.Add(guardDoor);
                Debug.Log($"✓ Found Booking Guard Door at {guardDoorTransform.name}");
            }
        }

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
    }

    [System.Serializable]
    public class GuardRoomArea : JailAreaBase
    {
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

    [System.Serializable]
    public class MainRecArea : JailAreaBase
    {
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

    [System.Serializable]
    public class ShowerArea : JailAreaBase
    {
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
}