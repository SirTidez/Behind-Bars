using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MelonLoader;
using Behind_Bars.Systems.Data;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne;
using ClickHandler = System.Action;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne;
using ClickHandler = UnityEngine.Events.UnityAction;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Owns the editor-authored personal-property locker presentation. The AssetBundle contains
    /// only native uGUI components; item data and transfer authority remain in InventoryPickupStation.
    /// </summary>
    internal sealed class PropertyLockerUI
    {
        private const string BundleResourceName = "Behind_Bars.behind_bars_property_locker.bundle";
        // This must match the full authored AssetBundle path.  The bundle is built from
        // Assets/BehindBars/PropertyLocker/BehindBarsPropertyLockerUI.prefab, not the
        // project root.  Omitting PropertyLocker caused every release to take the
        // emergency direct-transfer path instead of showing the interactive locker.
        private const string PrefabAssetPath = "assets/behindbars/propertylocker/behindbarspropertylockerui.prefab";

        private static PropertyLockerUI instance;
        internal static PropertyLockerUI Instance => instance ??= new PropertyLockerUI();

#if MONO
        private AssetBundle bundle;
#else
        private Il2CppAssetBundle bundle;
#endif
        private GameObject prefab;
        private GameObject root;
        private Transform itemGrid;
        private GameObject itemCardTemplate;
        private Text titleText;
        private Text countText;
        private Text statusText;
        private Text clothingText;
        private GameObject emptyState;
        private Button takeAllButton;
        private Button closeButton;
        private Canvas presentationCanvas;
        private GraphicRaycaster presentationRaycaster;

        private readonly List<ItemCardBinding> cardBindings = new();
        private readonly List<PersistentPlayerData.StoredItem> remainingItems = new();
        private InventoryPickupStation owner;
        private Player player;
        private bool transferInProgress;
        private bool exitListenerRegistered;
#if !MONO
        // IL2CPP deregistration is identity-based. Keep the exact delegate instance that
        // was registered instead of recreating an interop trampoline on every call.
        private Il2CppScheduleOne.GameInput.ExitDelegate exitListener;
#else
        private GameInput.ExitDelegate exitListener;
#endif
        private ClickHandler takeAllClickHandler;
        private ClickHandler closeClickHandler;

        internal bool IsOpen => root != null && root.activeSelf;
        internal static bool IsPresentationOpen => instance != null && instance.IsOpen;

        /// <summary>
        /// Reassert the native cursor state while the modal is open. Schedule I can
        /// reclaim the cursor during its normal first-person update, so a single
        /// open-time call is not sufficient for an editor-authored uGUI modal.
        /// </summary>
        internal static void MaintainOpenPresentation()
        {
            instance?.MaintainLockerInput();
        }

        /// <summary>
        /// Opens the locker. The owner retains every item-transfer and release-flow decision.
        /// </summary>
        internal bool TryShow(
            InventoryPickupStation storageOwner,
            Player targetPlayer,
            IReadOnlyList<PersistentPlayerData.StoredItem> items,
            int confiscatedCount)
        {
            if (storageOwner == null || targetPlayer == null)
            {
                ModLogger.Error("PropertyLockerUI cannot open without a station owner and player");
                return false;
            }

            if (!EnsurePresentation())
            {
                return false;
            }

            owner = storageOwner;
            player = targetPlayer;
            transferInProgress = false;
            remainingItems.Clear();
            if (items != null)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    if (items[index] != null)
                    {
                        remainingItems.Add(items[index]);
                    }
                }
            }

            titleText.text = "PERSONAL PROPERTY LOCKER";
            statusText.text = confiscatedCount > 0
                ? $"{confiscatedCount} contraband item{(confiscatedCount == 1 ? string.Empty : "s")} retained by corrections"
                : "Select an item to return it to your inventory";

            RebuildItemCards();
            root.SetActive(true);
            // The scanner can leave the local avatar visible for a frame after its camera
            // handoff. The locker never needs a body view, so preserve normal first-person
            // visibility instead of resetting camera transforms into the head mesh.
            targetPlayer.SetVisibleToLocalPlayer(false);
            ReleaseMouseForLocker();
            RegisterExitListener();
            ModLogger.Info($"PropertyLockerUI opened with {remainingItems.Count} returnable item(s)");
            return true;
        }

        internal void CloseForSceneTransition()
        {
            if (!IsOpen)
            {
                ReleaseBindings();
                root = null;
                return;
            }

            ClosePresentation(restoreGameplayInput: false);
            root = null;
            owner = null;
            player = null;
            remainingItems.Clear();
            ModLogger.Debug("PropertyLockerUI released scene presentation");
        }

        private bool EnsurePresentation()
        {
            if (root != null)
            {
            return true;
            }

            try
            {
                if (bundle == null)
                {
                    bundle = Behind_Bars.Utils.AssetBundleUtils.LoadAssetBundle(BundleResourceName);
                }

                if (bundle == null)
                {
                    ModLogger.Error("PropertyLockerUI AssetBundle could not be loaded");
                    return false;
                }

                if (prefab == null)
                {
#if MONO
                    prefab = bundle.LoadAsset<GameObject>(PrefabAssetPath);
#else
                    prefab = bundle.LoadAsset(PrefabAssetPath, Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                }

                if (prefab == null)
                {
                    ModLogger.Error($"PropertyLockerUI prefab '{PrefabAssetPath}' was not found in its AssetBundle");
                    return false;
                }

                root = UnityEngine.Object.Instantiate(prefab);
                root.name = "BehindBarsPropertyLockerUI";
                BindPresentation(root.transform);
                root.SetActive(false);
                return true;
            }
            catch (Exception exception)
            {
                ModLogger.Error($"PropertyLockerUI presentation setup failed: {exception}");
                root = null;
                return false;
            }
        }

        private void BindPresentation(Transform presentationRoot)
        {
            titleText = FindRequired<Text>(presentationRoot, "Blocker/Card/Header/TitleText");
            countText = FindRequired<Text>(presentationRoot, "Blocker/Card/Header/CountText");
            statusText = FindRequired<Text>(presentationRoot, "Blocker/Card/StatusText");
            clothingText = FindRequired<Text>(presentationRoot, "Blocker/Card/ClothingPreview/ClothingListText");
            itemGrid = FindRequired<Transform>(presentationRoot, "Blocker/Card/ItemsViewport/ItemsGrid");
            itemCardTemplate = FindRequired<Transform>(presentationRoot, "Blocker/Card/ItemsViewport/ItemCardTemplate").gameObject;
            emptyState = FindRequired<Transform>(presentationRoot, "Blocker/Card/ItemsViewport/EmptyState").gameObject;
            takeAllButton = FindRequired<Button>(presentationRoot, "Blocker/Card/ActionRow/TakeAllButton");
            closeButton = FindRequired<Button>(presentationRoot, "Blocker/Card/ActionRow/CloseButton");
            presentationCanvas = presentationRoot.GetComponent<Canvas>();
            presentationRaycaster = presentationRoot.GetComponent<GraphicRaycaster>();
            if (presentationCanvas == null || presentationRaycaster == null)
            {
                throw new InvalidOperationException("Property locker root requires Canvas and GraphicRaycaster");
            }

            // This is a contextual HUD modal, not a global menu. Keep it above gameplay
            // but below Schedule I's pause/menu canvases so the game owns all paused input.
            presentationCanvas.overrideSorting = true;
            presentationCanvas.sortingOrder = 5;

            takeAllClickHandler = HandleTakeAllClicked;
            closeClickHandler = HandleCloseClicked;
            takeAllButton.onClick.RemoveListener(takeAllClickHandler);
            takeAllButton.onClick.AddListener(takeAllClickHandler);
            closeButton.onClick.RemoveListener(closeClickHandler);
            closeButton.onClick.AddListener(closeClickHandler);
        }

        private void RebuildItemCards()
        {
            ReleaseItemCardBindings();
            countText.text = $"{remainingItems.Count} ITEM{(remainingItems.Count == 1 ? string.Empty : "S")} HELD";
            emptyState.SetActive(remainingItems.Count == 0);
            takeAllButton.interactable = remainingItems.Count > 0 && !transferInProgress;
            closeButton.interactable = !transferInProgress;
            clothingText.text = BuildClothingSummary();

            for (int index = 0; index < remainingItems.Count; index++)
            {
                PersistentPlayerData.StoredItem item = remainingItems[index];
                GameObject card = UnityEngine.Object.Instantiate(itemCardTemplate, itemGrid, false);
                card.name = $"PropertyItem_{index + 1}";
                card.SetActive(true);
                var binding = new ItemCardBinding(this, card, item);
                cardBindings.Add(binding);
            }
        }

        private string BuildClothingSummary()
        {
            var clothing = owner?.GetStoredClothing(player);
            if (clothing == null || clothing.Count == 0)
            {
                return "CLOTHING TO RESTORE\nNo civilian clothing record is available.";
            }

            var names = new List<string>();
            for (int index = 0; index < clothing.Count; index++)
            {
                var layer = clothing[index];
                if (layer == null || string.IsNullOrWhiteSpace(layer.layerPath))
                {
                    continue;
                }

                string layerName = layer.layerPath.Replace('\\', '/');
                int separator = layerName.LastIndexOf('/');
                if (separator >= 0 && separator < layerName.Length - 1)
                {
                    layerName = layerName.Substring(separator + 1);
                }

                if (!names.Contains(layerName))
                {
                    names.Add(layerName);
                }
            }

            return names.Count == 0
                ? "CLOTHING TO RESTORE\nCivilian outfit saved for release."
                : "CLOTHING TO RESTORE\n" + string.Join("  •  ", names);
        }

        private void HandleTakeAllClicked()
        {
            if (transferInProgress || remainingItems.Count == 0 || owner == null)
            {
                return;
            }

            MelonCoroutines.Start(ReturnAllItems());
        }

        private System.Collections.IEnumerator ReturnAllItems()
        {
            transferInProgress = true;
            statusText.text = "Returning your personal property...";
            RebuildItemCards();

            while (remainingItems.Count > 0)
            {
                PersistentPlayerData.StoredItem item = remainingItems[0];
                if (!TryReturnItem(item))
                {
                    statusText.text = $"Unable to return {item.itemName}. Make room and try again.";
                    break;
                }

                remainingItems.RemoveAt(0);
                RebuildItemCards();
                yield return new WaitForSecondsRealtime(0.12f);
            }

            transferInProgress = false;
            RebuildItemCards();
            if (remainingItems.Count == 0)
            {
                statusText.text = "All property returned. Select CONTINUE.";
                closeButton.interactable = true;
            }
        }

        private void HandleItemClicked(PersistentPlayerData.StoredItem item)
        {
            if (transferInProgress || item == null || !remainingItems.Contains(item))
            {
                return;
            }

            if (!TryReturnItem(item))
            {
                statusText.text = $"Unable to return {item.itemName}. Make room and try again.";
                return;
            }

            remainingItems.Remove(item);
            statusText.text = $"Returned {item.itemName}";
            RebuildItemCards();
            if (remainingItems.Count == 0)
            {
                statusText.text = "All property returned. Select CONTINUE.";
            }
        }

        private bool TryReturnItem(PersistentPlayerData.StoredItem item)
        {
            if (owner == null || player == null)
            {
                return false;
            }

            return owner.TryReturnPropertyItem(player, item);
        }

        private void HandleCloseClicked()
        {
            if (transferInProgress)
            {
                return;
            }

            if (remainingItems.Count > 0)
            {
                statusText.text = "Collect all remaining property before continuing.";
                return;
            }

            InventoryPickupStation activeOwner = owner;
            Player activePlayer = player;
            ClosePresentation(restoreGameplayInput: true);
            activeOwner?.CompletePropertyLockerRetrieval(activePlayer);
        }

        private void ClosePresentation(bool restoreGameplayInput)
        {
            DeregisterExitListener();
            ReleaseItemCardBindings();
            if (root != null)
            {
                root.SetActive(false);
            }

            if (restoreGameplayInput)
            {
                RestoreGameplayInput();
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void OnExitLocker(ExitAction action)
        {
            // The first exit/menu/back press closes only this contextual panel.  We consume
            // it so the player can readjust or interact again; the next press remains the
            // game's untouched pause/menu behavior.
            if (action == null || action.Used || !IsOpen)
            {
                return;
            }

            action.Use();
            DismissThroughExitInput();
        }

        private void DismissThroughExitInput()
        {
            if (transferInProgress || !IsOpen)
            {
                return;
            }

            var activeOwner = owner;
            var activePlayer = player;
            ClosePresentation(restoreGameplayInput: true);
            activeOwner?.SuspendPropertyLockerSession(activePlayer);
            ModLogger.Info("PropertyLockerUI dismissed through the native exit action");
        }

        private void RegisterExitListener()
        {
            if (exitListenerRegistered)
            {
                return;
            }

#if !MONO
            exitListener ??= (Il2CppScheduleOne.GameInput.ExitDelegate)OnExitLocker;
            GameInput.RegisterExitListener(exitListener, priority: 2);
#else
            exitListener ??= OnExitLocker;
            GameInput.RegisterExitListener(exitListener, priority: 2);
#endif
            exitListenerRegistered = true;
        }

        private void DeregisterExitListener()
        {
            if (!exitListenerRegistered)
            {
                return;
            }

#if !MONO
            if (exitListener != null)
            {
                GameInput.DeregisterExitListener(exitListener);
            }
#else
            if (exitListener != null)
            {
                GameInput.DeregisterExitListener(exitListener);
            }
#endif
            exitListenerRegistered = false;
        }

        private void ReleaseBindings()
        {
            ReleaseItemCardBindings();
            if (takeAllButton != null && takeAllClickHandler != null)
            {
                takeAllButton.onClick.RemoveListener(takeAllClickHandler);
            }
            if (closeButton != null && closeClickHandler != null)
            {
                closeButton.onClick.RemoveListener(closeClickHandler);
            }

            takeAllClickHandler = null;
            closeClickHandler = null;
        }

        private void ReleaseItemCardBindings()
        {
            for (int index = 0; index < cardBindings.Count; index++)
            {
                cardBindings[index].Dispose();
            }
            cardBindings.Clear();

            if (itemGrid == null || itemCardTemplate == null)
            {
                return;
            }

            for (int index = itemGrid.childCount - 1; index >= 0; index--)
            {
                Transform child = itemGrid.GetChild(index);
                if (child != itemCardTemplate.transform)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        private void ReleaseMouseForLocker()
        {
            try
            {
                PlayerSingleton<PlayerCamera>.Instance?.FreeMouse();
                Singleton<HUD>.Instance?.SetCrosshairVisible(false);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                MelonCoroutines.Start(ReassertLockerInputAfterFrame());
            }
            catch (Exception exception)
            {
                ModLogger.Warn($"PropertyLockerUI could not release the mouse: {exception.Message}");
            }
        }

        private void MaintainLockerInput()
        {
            if (!IsOpen)
            {
                return;
            }

            try
            {
                PlayerSingleton<PlayerCamera>.Instance?.FreeMouse();
                Singleton<HUD>.Instance?.SetCrosshairVisible(false);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch (Exception exception)
            {
                ModLogger.Debug($"PropertyLockerUI input maintenance skipped: {exception.Message}");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private System.Collections.IEnumerator ReassertLockerInputAfterFrame()
        {
            yield return null;
            if (!IsOpen)
            {
                yield break;
            }

            PlayerSingleton<PlayerCamera>.Instance?.FreeMouse();
            Singleton<HUD>.Instance?.SetCrosshairVisible(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RestoreGameplayInput()
        {
            try
            {
                PlayerSingleton<PlayerCamera>.Instance?.LockMouse();
                Singleton<HUD>.Instance?.SetCrosshairVisible(true);
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            catch (Exception exception)
            {
                ModLogger.Warn($"PropertyLockerUI could not restore gameplay input: {exception.Message}");
            }
        }

        private static T FindRequired<T>(Transform parent, string path) where T : Component
        {
            Transform child = parent.Find(path);
            if (child == null)
            {
                throw new InvalidOperationException($"Property locker bundle is missing '{path}'");
            }

            T component = child.GetComponent<T>();
            if (component == null)
            {
                throw new InvalidOperationException($"Property locker bundle object '{path}' is missing {typeof(T).Name}");
            }

            return component;
        }

        private sealed class ItemCardBinding : IDisposable
        {
            private readonly PropertyLockerUI ui;
            private readonly PersistentPlayerData.StoredItem item;
            private readonly Button button;
            private readonly ClickHandler clickHandler;
            private readonly GameObject card;

            internal ItemCardBinding(PropertyLockerUI ui, GameObject card, PersistentPlayerData.StoredItem item)
            {
                this.ui = ui;
                this.card = card;
                this.item = item;
                button = FindRequired<Button>(card.transform, "TakeButton");
                FindRequired<Text>(card.transform, "NameText").text = item.itemName;
                FindRequired<Text>(card.transform, "CountText").text = item.stackCount > 1 ? $"x{item.stackCount}" : "1";
                clickHandler = OnClick;
                button.onClick.RemoveListener(clickHandler);
                button.onClick.AddListener(clickHandler);
            }

            public void Dispose()
            {
                if (button != null && clickHandler != null)
                {
                    button.onClick.RemoveListener(clickHandler);
                }

                if (card != null)
                {
                    UnityEngine.Object.Destroy(card);
                }
            }

            private void OnClick()
            {
                ui.HandleItemClicked(item);
            }
        }
    }
}
