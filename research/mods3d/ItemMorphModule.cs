// ============================================================================
//  ItemMorphModule.cs — Item tracking + teleport with icons (v2.0)
//  Иконки предметов добавлены (from InvViewer pattern)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace CSHUB.Modules
{
    public class ItemMorphModule : CSModuleBase
    {
        public override string Id => "item_morph";
        public override string Name => "Item Morph";
        public override string Description =>
            "Телепорт и слежение за предметами.\n\n" +
            "• Камера следует за предметом\n" +
            "• Персонаж ПОСТОЯННО телепортируется\n" +
            "• ЛКМ — телепорт в точку курсора\n" +
            "• Иконки предметов в списке";
        public override string Category => "exploit";

        public static bool IsTracking = false;
        public static Item TrackedItem = null;
        public static Vector2 TeleportPosition = Vector2.Zero;
        public static Character SavedCharacter = null;

        private static bool _wasLMBPressed = false;
        private static bool _hookInstalled = false;
        private static int _frameCounter = 0;

        private static GUIMessageBox _mainWindow = null;
        private static GUIListBox _itemListBox = null;
        private static GUITextBox _searchBox = null;
        private static GUITextBlock _searchPlaceholder = null;
        private static GUITextBlock _statusLabel = null;
        private static GUIButton _backBtn = null;

        private static Dictionary<string, ItemCategory> _itemsByType = new Dictionary<string, ItemCategory>();
        private static List<ItemCategory> _sortedCategories = new List<ItemCategory>();
        private static string _searchQuery = "";
        private static ItemCategory _currentCategory = null;
        private static int _currentPage = 0;
        private static int _itemsPerPage = 12;

        private static readonly Color AccentColor = new Color(100, 200, 255);
        private static readonly Color SuccessColor = new Color(100, 255, 140);
        private static readonly Color DangerColor = new Color(255, 100, 100);
        private static readonly Color WarningColor = new Color(255, 220, 100);

        public class ItemCategory
        {
            public string PrefabId;
            public string Name;
            public string CategoryTag;
            public List<Item> Items = new List<Item>();
            public Color Color = Color.White;
            public Sprite Icon;
            public Color IconColor = Color.White;
        }

        public override void Initialize()
        {
            base.Initialize();
            InstallHook();
        }

        private static void InstallHook()
        {
            if (_hookInstalled) return;
            try
            {
                var method = AccessTools.Method(typeof(GameMain), "Update");
                if (method != null)
                {
                    CSHUBPlugin.HarmonyInstance.Patch(method,
                        postfix: new HarmonyMethod(typeof(ItemMorphModule), nameof(OnGameUpdate)));
                    _hookInstalled = true;
                }
            }
            catch (Exception e) { LuaCsLogger.LogError($"[ItemMorph] Hook: {e.Message}"); }
        }

        private static void OnGameUpdate()
        {
            try { DoTrackingUpdate(); } catch (Exception e) { LuaCsLogger.LogError($"[ItemMorph] Upd: {e.Message}"); }
        }

        private static void DoTrackingUpdate()
        {
            _frameCounter++;
            if (!IsTracking) return;
            if (TrackedItem == null || TrackedItem.Removed) { StopTracking(); return; }
            if (Character.Controlled == null) return;

            Camera cam = null;
            try { cam = Screen.Selected?.Cam; } catch { }
            if (cam == null) return;

            Vector2 itemPos = TrackedItem.WorldPosition;
            cam.Position = itemPos;

            bool lmbDown = Mouse.GetState().LeftButton == ButtonState.Pressed;
            bool lmbClicked = lmbDown && !_wasLMBPressed;
            _wasLMBPressed = lmbDown;

            if (lmbClicked)
            {
                bool onGui = GUI.MouseOn != null;
                if (!onGui)
                {
                    Vector2 worldPos = cam.ScreenToWorld(PlayerInput.MousePosition);
                    TeleportPosition = worldPos;
                    GUI.AddMessage($"TP: {worldPos.X:F0}, {worldPos.Y:F0}", new Color(0, 255, 255));
                }
            }

            Character.Controlled.TeleportTo(TeleportPosition);

            try
            {
                if (Character.Controlled.AnimController?.Collider != null)
                {
                    Character.Controlled.AnimController.Collider.LinearVelocity = Vector2.Zero;
                    Character.Controlled.AnimController.Collider.AngularVelocity = 0f;
                }
            }
            catch { }
        }

        public override string GetLabel()
        {
            if (IsTracking && TrackedItem != null) return "Item Morph: TRACKING";
            return "Item Morph";
        }

        public override void OnClick()
        {
            InstallHook();
            if (_mainWindow != null) { CloseWindow(); return; }
            OpenMainWindow();
        }

        public static void StartTracking(Item item)
        {
            if (item == null || item.Removed) { GUI.AddMessage("Item not found!", DangerColor); return; }
            if (Character.Controlled == null) { GUI.AddMessage("No character!", DangerColor); return; }

            SavedCharacter = Character.Controlled;
            TrackedItem = item;
            TeleportPosition = item.WorldPosition;
            Character.Controlled.TeleportTo(TeleportPosition);
            try { var cam = Screen.Selected?.Cam; if (cam != null) cam.Position = TeleportPosition; } catch { }
            IsTracking = true;
            GUI.AddMessage($"TRACKING: {item.Name}", SuccessColor);
            GUI.AddMessage("LMB = TP to cursor", WarningColor);
        }

        public static void StopTracking()
        {
            IsTracking = false; TrackedItem = null;
            if (SavedCharacter != null && !SavedCharacter.Removed)
            {
                try { var cam = Screen.Selected?.Cam; if (cam != null) cam.Position = SavedCharacter.WorldPosition; } catch { }
            }
            GUI.AddMessage("Tracking stopped", WarningColor);
        }

        private static void OpenMainWindow()
        {
            CloseWindow();
            CollectItems();

            _mainWindow = new GUIMessageBox("Item Morph", "", Array.Empty<LocalizedString>(), new Vector2(0.72f, 0.85f));
            var content = _mainWindow.Content;
            content.ClearChildren();

            var header = new GUIFrame(new RectTransform(new Vector2(1f, 0.07f), content.RectTransform, Anchor.TopCenter), style: null);
            header.Color = new Color(40, 50, 70);
            var headerLay = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.85f), header.RectTransform, Anchor.Center), isHorizontal: true);

            _statusLabel = new GUITextBlock(new RectTransform(new Vector2(0.4f, 1f), headerLay.RectTransform),
                IsTracking ? $"TRACKING: {TrackedItem?.Name}" : "Not tracking", textAlignment: Alignment.CenterLeft);
            _statusLabel.TextColor = IsTracking ? SuccessColor : Color.White;

            var refreshBtn = new GUIButton(new RectTransform(new Vector2(0.15f, 0.9f), headerLay.RectTransform), "Refresh");
            refreshBtn.Color = WarningColor;
            refreshBtn.OnClicked = (b, d) => { CollectItems(); RefreshList(); return true; };

            var stopBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 0.9f), headerLay.RectTransform), "STOP");
            stopBtn.Color = DangerColor;
            stopBtn.OnClicked = (b, d) => { StopTracking(); RefreshList(); return true; };

            var closeBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 0.9f), headerLay.RectTransform), "Close");
            closeBtn.Color = new Color(100, 100, 120);
            closeBtn.OnClicked = (b, d) => { CloseWindow(); return true; };

            var controls = new GUIFrame(new RectTransform(new Vector2(1f, 0.07f), content.RectTransform, Anchor.TopCenter)
                { RelativeOffset = new Vector2(0, 0.08f) }, style: null);
            controls.Color = new Color(30, 38, 55);

            var ctrlLay = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.85f), controls.RectTransform, Anchor.Center), isHorizontal: true);

            var searchFrame = new GUIFrame(new RectTransform(new Vector2(0.35f, 1f), ctrlLay.RectTransform), style: null);
            _searchBox = new GUITextBox(new RectTransform(Vector2.One, searchFrame.RectTransform), "");
            _searchPlaceholder = new GUITextBlock(new RectTransform(new Vector2(0.95f, 0.8f), searchFrame.RectTransform, Anchor.CenterLeft),
                "  Search...", textAlignment: Alignment.CenterLeft);
            _searchPlaceholder.TextColor = new Color(130, 130, 130);
            _searchPlaceholder.CanBeFocused = false;
            _searchBox.OnTextChanged += (b, t) => { _searchQuery = t?.ToLower() ?? ""; _currentPage = 0; RefreshList(); return true; };

            _backBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 1f), ctrlLay.RectTransform), "< Back");
            _backBtn.Color = AccentColor;
            _backBtn.Visible = _currentCategory != null;
            _backBtn.OnClicked = (b, d) => { _currentCategory = null; _currentPage = 0; RefreshList(); return true; };

            var prevBtn = new GUIButton(new RectTransform(new Vector2(0.08f, 1f), ctrlLay.RectTransform), "<<");
            prevBtn.OnClicked = (b, d) => { if (_currentPage > 0) { _currentPage--; RefreshList(); } return true; };

            var nextBtn = new GUIButton(new RectTransform(new Vector2(0.08f, 1f), ctrlLay.RectTransform), ">>");
            nextBtn.OnClicked = (b, d) => { _currentPage++; RefreshList(); return true; };

            var listFrame = new GUIFrame(new RectTransform(new Vector2(1f, 0.83f), content.RectTransform, Anchor.BottomCenter), style: null);
            _itemListBox = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
            _itemListBox.Color = new Color(20, 25, 35);

            RefreshList();
        }

        private static void CollectItems()
        {
            _itemsByType.Clear();
            _sortedCategories.Clear();

            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed) continue;

                string prefabId = item.Prefab?.Identifier.ToString() ?? "unknown";
                string prefabName = item.Prefab?.Name?.ToString() ?? item.Name ?? "???";

                if (!_itemsByType.ContainsKey(prefabId))
                {
                    string catTag = "";
                    Color catColor = new Color(180, 200, 255);

                    try
                    {
                        if (item.Prefab?.Category != null)
                        {
                            catTag = item.Prefab.Category.ToString();
                            var l = catTag.ToLower();
                            if (l.Contains("weapon")) catColor = new Color(255, 120, 120);
                            else if (l.Contains("tool")) catColor = new Color(255, 210, 100);
                            else if (l.Contains("medical")) catColor = new Color(120, 255, 160);
                            else if (l.Contains("diving")) catColor = new Color(120, 210, 255);
                        }
                    }
                    catch { }

                    Sprite icon = null;
                    Color iconColor = Color.White;
                    try
                    {
                        icon = item.Prefab.InventoryIcon ?? item.Prefab.Sprite;
                        if (icon != null)
                        {
                            if (item.Prefab.InventoryIcon != null)
                            {
                                try { iconColor = item.InventoryIconColor; } catch { }
                            }
                            else
                            {
                                try { iconColor = item.SpriteColor; } catch { }
                            }
                        }
                    }
                    catch { }

                    _itemsByType[prefabId] = new ItemCategory { PrefabId = prefabId, Name = prefabName, CategoryTag = catTag, Color = catColor, Icon = icon, IconColor = iconColor };
                }

                _itemsByType[prefabId].Items.Add(item);
            }

            _sortedCategories = _itemsByType.Values.OrderBy(c => c.Name).ToList();
        }

        private static void RefreshList()
        {
            if (_itemListBox == null) return;
            _itemListBox.Content.ClearChildren();

            if (_backBtn != null) _backBtn.Visible = _currentCategory != null;
            if (_searchPlaceholder != null && _searchBox != null) _searchPlaceholder.Visible = string.IsNullOrEmpty(_searchBox.Text);

            if (_currentCategory == null) ShowCategories();
            else ShowItems();
        }

        private static void ShowCategories()
        {
            var filtered = _sortedCategories.Where(c =>
                string.IsNullOrEmpty(_searchQuery) || c.Name.ToLower().Contains(_searchQuery) || c.CategoryTag.ToLower().Contains(_searchQuery)
            ).ToList();

            if (filtered.Count == 0) { AddEmptyRow("Nothing found"); return; }

            int pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (float)_itemsPerPage));
            _currentPage = Math.Clamp(_currentPage, 0, pages - 1);

            AddHeaderRow($"Categories - Page {_currentPage + 1}/{pages}");

            foreach (var cat in filtered.Skip(_currentPage * _itemsPerPage).Take(_itemsPerPage))
            {
                var row = new GUIFrame(new RectTransform(new Vector2(1f, 0.07f), _itemListBox.Content.RectTransform), style: null);
                row.Color = new Color(cat.Color, 50);

                var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.9f), row.RectTransform, Anchor.Center), isHorizontal: true);

                // ИКОНКА
                AddIcon(layout, cat.Icon, cat.IconColor, 0.07f);

                var ind = new GUIFrame(new RectTransform(new Vector2(0.01f, 1f), layout.RectTransform), style: null);
                ind.Color = cat.Color;

                var nameBtn = new GUIButton(new RectTransform(new Vector2(0.53f, 1f), layout.RectTransform), cat.Name, textAlignment: Alignment.CenterLeft);
                nameBtn.Color = new Color(55, 62, 78);
                nameBtn.HoverColor = new Color(75, 85, 105);
                nameBtn.OnClicked = (b, d) => { _currentCategory = cat; _currentPage = 0; RefreshList(); return true; };

                var countTxt = new GUITextBlock(new RectTransform(new Vector2(0.12f, 1f), layout.RectTransform), $"x{cat.Items.Count}", textAlignment: Alignment.Center);
                countTxt.TextColor = WarningColor;

                var goBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 0.9f), layout.RectTransform), "GO");
                goBtn.Color = SuccessColor;
                goBtn.TextColor = Color.Black;
                goBtn.OnClicked = (b, d) =>
                {
                    var nearest = cat.Items.Where(i => i != null && !i.Removed).OrderBy(i =>
                        Character.Controlled != null ? Vector2.Distance(i.WorldPosition, Character.Controlled.WorldPosition) : 0
                    ).FirstOrDefault();
                    if (nearest != null) { StartTracking(nearest); RefreshList(); }
                    return true;
                };
            }
        }

        private static void ShowItems()
        {
            var items = _currentCategory.Items.Where(i => i != null && !i.Removed).ToList();

            if (!string.IsNullOrEmpty(_searchQuery))
                items = items.Where(i => (i.Name?.ToLower().Contains(_searchQuery) ?? false)).ToList();

            if (items.Count == 0) { AddEmptyRow("No items"); return; }

            int pages = Math.Max(1, (int)Math.Ceiling(items.Count / (float)_itemsPerPage));
            _currentPage = Math.Clamp(_currentPage, 0, pages - 1);

            AddHeaderRow($"{_currentCategory.Name} - Page {_currentPage + 1}/{pages}");

            int idx = _currentPage * _itemsPerPage;
            foreach (var item in items.Skip(idx).Take(_itemsPerPage))
            {
                idx++;
                bool tracking = IsTracking && item == TrackedItem;

                var row = new GUIFrame(new RectTransform(new Vector2(1f, 0.075f), _itemListBox.Content.RectTransform), style: null);
                row.Color = tracking ? new Color(80, 255, 130, 80) : new Color(40, 48, 62);

                var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.9f), row.RectTransform, Anchor.Center), isHorizontal: true);

                // ИКОНКА ПРЕДМЕТА
                Sprite icon = null;
                Color iconCol = Color.White;
                try
                {
                    icon = item.Prefab?.InventoryIcon ?? item.Prefab?.Sprite;
                    if (icon != null)
                    {
                        if (item.Prefab.InventoryIcon != null)
                        {
                            try { iconCol = item.InventoryIconColor; } catch { }
                        }
                        else
                        {
                            try { iconCol = item.SpriteColor; } catch { }
                        }
                    }
                }
                catch { }
                AddIcon(layout, icon, iconCol, 0.07f);

                var numTxt = new GUITextBlock(new RectTransform(new Vector2(0.05f, 1f), layout.RectTransform), $"#{idx}", textAlignment: Alignment.Center);
                numTxt.TextColor = new Color(140, 140, 160);

                var nameTxt = new GUITextBlock(new RectTransform(new Vector2(0.31f, 1f), layout.RectTransform), item.Name ?? "???", textAlignment: Alignment.CenterLeft);
                nameTxt.TextColor = tracking ? SuccessColor : Color.White;

                string loc = "[World]";
                try
                {
                    if (item.ParentInventory?.Owner is Character c) loc = $"[P] {c.Name}";
                    else if (item.ParentInventory?.Owner is Item cont) loc = $"[C] {cont.Name}";
                    else if (item.Submarine != null) loc = "[Sub]";
                }
                catch { }

                var locTxt = new GUITextBlock(new RectTransform(new Vector2(0.22f, 1f), layout.RectTransform), loc, textAlignment: Alignment.CenterLeft);
                locTxt.TextColor = new Color(180, 185, 200);

                string coords = "???";
                try { var p = item.WorldPosition; coords = $"{(int)p.X},{(int)p.Y}"; } catch { }
                var coordTxt = new GUITextBlock(new RectTransform(new Vector2(0.14f, 1f), layout.RectTransform), coords, textAlignment: Alignment.Center);
                coordTxt.TextColor = new Color(150, 180, 220);

                var tpBtn = new GUIButton(new RectTransform(new Vector2(0.13f, 0.9f), layout.RectTransform), tracking ? "ACT" : "TP");
                tpBtn.Color = tracking ? SuccessColor : AccentColor;
                tpBtn.TextColor = tracking ? Color.Black : Color.White;
                tpBtn.OnClicked = (b, d) => { StartTracking(item); RefreshList(); return true; };
            }
        }

        /// <summary>
        /// Добавляет иконку предмета в горизонтальный layout.
        /// Использует GUIImage с Sprite из ItemPrefab.InventoryIcon или .Sprite.
        /// </summary>
        private static void AddIcon(GUILayoutGroup layout, Sprite icon, Color color, float width)
        {
            var frame = new GUIFrame(new RectTransform(new Vector2(width, 0.95f), layout.RectTransform), style: null);
            frame.Color = Color.Transparent;

            if (icon == null) return;

            try
            {
                var img = new GUIImage(
                    new RectTransform(new Vector2(0.85f, 0.85f), frame.RectTransform, Anchor.Center),
                    icon, false);
                img.Color = color;
                img.CanBeFocused = false;
            }
            catch { }
        }

        private static void AddHeaderRow(string text)
        {
            var row = new GUIFrame(new RectTransform(new Vector2(1f, 0.05f), _itemListBox.Content.RectTransform), style: null);
            row.Color = new Color(50, 60, 80);
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text, textAlignment: Alignment.Center);
            txt.TextColor = AccentColor;
        }

        private static void AddEmptyRow(string text)
        {
            var row = new GUIFrame(new RectTransform(new Vector2(1f, 0.1f), _itemListBox.Content.RectTransform), style: null);
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text, textAlignment: Alignment.Center);
            txt.TextColor = new Color(150, 150, 150);
        }

        private static void CloseWindow()
        {
            _mainWindow?.Close();
            _mainWindow = null;
            _itemListBox = null;
            _searchBox = null;
            _searchPlaceholder = null;
            _statusLabel = null;
            _backBtn = null;
        }

        public override void Update()
        {
            if (IsTracking) DoTrackingUpdate();
        }
    }
}
