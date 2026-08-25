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
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using ClickHandler = System.Action;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
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
        private const string PrefabAssetPath = "assets/behindbarspropertylockerui.prefab";

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
        private GameObject emptyState;
        private Button takeAllButton;
        private Button closeButton;

        private readonly List<ItemCardBinding> cardBindings = new();
        private readonly List<PersistentPlayerData.StoredItem> remainingItems = new();
        private InventoryPickupStation owner;
        private Player player;
        private bool transferInProgress;
        private ClickHandler takeAllClickHandler;
        private ClickHandler closeClickHandler;

        internal bool IsOpen => root != null && root.activeSelf;

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
            ReleaseMouseForLocker();
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
            itemGrid = FindRequired<Transform>(presentationRoot, "Blocker/Card/ItemsViewport/ItemsGrid");
            itemCardTemplate = FindRequired<Transform>(presentationRoot, "Blocker/Card/ItemsViewport/ItemCardTemplate").gameObject;
            emptyState = FindRequired<Transform>(presentationRoot, "Blocker/Card/ItemsViewport/EmptyState").gameObject;
            takeAllButton = FindRequired<Button>(presentationRoot, "Blocker/Card/ActionRow/TakeAllButton");
            closeButton = FindRequired<Button>(presentationRoot, "Blocker/Card/ActionRow/CloseButton");

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
            }
            catch (Exception exception)
            {
                ModLogger.Warn($"PropertyLockerUI could not release the mouse: {exception.Message}");
            }
        }

        private void RestoreGameplayInput()
        {
            try
            {
                PlayerSingleton<PlayerCamera>.Instance?.LockMouse();
                Singleton<HUD>.Instance?.SetCrosshairVisible(true);
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
