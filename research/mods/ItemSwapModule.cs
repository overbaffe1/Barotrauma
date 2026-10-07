using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  ITEM SWAP (волна 592) — свап ЛЮБОГО предмета мира.
    //  ВОЛНА 591: MultiPlayerCampaign.ServerRead берёт itemToRemoveID
    //  из пакета и НЕ проверяет владение/расположение — можно свапать
    //  чужие предметы (турели чужой сабы, предметы в чужом кармане).
    //
    //  ПРАВА (гейт = AllowedToManageCampaign(ManageCampaign)):
    //   • у тебя перм ManageCampaign (0x40), ИЛИ ты хост, ИЛИ
    //   • сервер permless, ИЛИ хост в лобби (не InGame), ИЛИ
    //   • все перм-холдеры мертвы/в ступоре >60с (окно 586), ИЛИ соло.
    //   НА СЕРВЕРЕ ДРУГА С ЖИВЫМ АДМИНОМ В РАУНДЕ — МОЛЧА НЕ СРАБОТАЕТ.
    //  БЛИЗОСТЬ: нужен NPC-апгрейдер в 250 юнитах ИЛИ сеттинг
    //   AllowRemoteCampaignInteractions=true (тогда из любой точки).
    //  ДЕНЬГИ: ДА — цена нового префаба × связанные предметы,
    //   с ТВОЕГО личного кошелька. Бесплатно если цель уже в
    //   AvailableSwaps предмета (этот свап уже делали).
    //
    //  ПАКЕТ (SERVER_COMMAND → ManageCampaign → MultiPlayerCampaign.ServerRead):
    //   u16 currentLocIndex, u16 selectedLocIndex (ТЕКУЩИЙ! 65535 =
    //   рандом локации — не слать!), byte missionCount=0,
    //   bool×3 false, byte 0 ×3 (крейты), byte 0 (sold), u16 upgrades=0,
    //   u16 swapCount=1, u16 victimItemId, Identifier newPrefab.
    // ============================================================
    public class ItemSwapModule : CSModuleBase
    {
        public override string Id   => "item_swap";
        public override string Name => "Item Swap";
        public override string Description =>
            "Свап ЛЮБОГО предмета мира по ID.\n\n" +
            "• Кликом из списка или ID вручную\n" +
            "• Жертва может быть на ЧУЖОЙ сабе/в чужом кармане\n" +
            "• Нужен NPC-апгрейдер рядом (или AllowRemoteCampaignInteractions)\n" +
            "• Перм-гейт: permless/лобби хоста/окно 586/свой перм\n" +
            "• Деньги: цена замены с ТВОЕГО кошелька";

        public override string Category => "exploit";

        private static readonly Color AccentColor = new Color(255, 170, 90);
        private static readonly Color OkColor     = new Color(120, 255, 140);
        private static readonly Color DangerColor = new Color(255, 110, 110);
        private static readonly Color DimColor    = new Color(160, 170, 190);
        private static readonly Color RowColor    = new Color(30, 36, 48);
        private static readonly Color SelColor    = new Color(90, 40, 40);

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBlock _status;
        private static GUITextBox _itemIdBox;
        private static GUITextBox _prefabBox;

        private static Item _selected;
        private static string _lastNote = "";

        public override string GetLabel() => "Item Swap 🔄";

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[Swap] Нет подключения", DangerColor);
                return;
            }
            if (GameMain.GameSession?.Campaign == null)
            {
                GUI.AddMessage("[Swap] Нужна кампания", DangerColor);
                return;
            }
            if (_window != null) { CloseWindow(); return; }
            BuildWindow();
        }

        private static void BuildWindow()
        {
            try
            {
                _selected = null;
                _lastNote = "";
                _window = new GUIMessageBox("Item Swap", "", Array.Empty<LocalizedString>(), new Vector2(0.62f, 0.85f));
                var content = _window.Content;
                content.ClearChildren();

                // ---- статус ----
                _status = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.09f), content.RectTransform),
                    "", textAlignment: Alignment.CenterLeft);
                _status.TextColor = AccentColor;
                _status.CanBeFocused = false;

                // ---- ручной ввод ----
                var inputFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.11f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.095f) }, style: null);
                var inputLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.92f), inputFrame.RectTransform, Anchor.Center), isHorizontal: true);
                var idLabel = new GUITextBlock(new RectTransform(new Vector2(0.09f, 1f), inputLayout.RectTransform), "ID:");
                idLabel.CanBeFocused = false;
                var idFrame = new GUIFrame(new RectTransform(new Vector2(0.17f, 1f), inputLayout.RectTransform), style: null);
                _itemIdBox = new GUITextBox(new RectTransform(Vector2.One, idFrame.RectTransform), "");
                var prefLabel = new GUITextBlock(new RectTransform(new Vector2(0.13f, 1f), inputLayout.RectTransform), "Новый:");
                prefLabel.CanBeFocused = false;
                var prefFrame = new GUIFrame(new RectTransform(new Vector2(0.39f, 1f), inputLayout.RectTransform), style: null);
                _prefabBox = new GUITextBox(new RectTransform(Vector2.One, prefFrame.RectTransform), "");

                // ---- список кандидатов ----
                var listLabel = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.045f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.21f) },
                    "Свапаемые предметы мира (клик = выбрать):", textAlignment: Alignment.CenterLeft);
                listLabel.TextColor = DimColor;
                listLabel.CanBeFocused = false;

                var listFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.42f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.255f) }, style: null);
                _list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
                _list.Color = new Color(20, 25, 35);
                RefreshList();

                // ---- кнопки ----
                var actionLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.08f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.685f) }, isHorizontal: true);
                var swapBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 0.95f), actionLayout.RectTransform), "★ СВАПНУТЬ ★");
                swapBtn.Color = SelColor;
                swapBtn.OnClicked = (b, d) => { LaunchSwap(); return true; };
                var refreshBtn = new GUIButton(new RectTransform(new Vector2(0.24f, 0.95f), actionLayout.RectTransform), "Обновить");
                refreshBtn.OnClicked = (b, d) => { RefreshList(); return true; };
                var closeBtn = new GUIButton(new RectTransform(new Vector2(0.24f, 0.95f), actionLayout.RectTransform), "Закрыть");
                closeBtn.OnClicked = (b, d) => { CloseWindow(); return true; };

                UpdateStatus();
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Swap] GUI fail: " + e.Message, DangerColor);
                CloseWindow();
            }
        }

        private static void RefreshList()
        {
            if (_list == null) { return; }
            try
            {
                _list.Content.ClearChildren();

                var header = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.07f), _list.Content.RectTransform), style: null);
                header.Color = new Color(50, 60, 80);
                var headerText = new GUITextBlock(new RectTransform(Vector2.One, header.RectTransform),
                    "ID | предмет (свапаемые):", textAlignment: Alignment.CenterLeft);
                headerText.TextColor = AccentColor;
                headerText.CanBeFocused = false;

                int shown = 0;
                foreach (Item it in Item.ItemList)
                {
                    if (it == null || it.Removed) { continue; }
                    if (it.Prefab?.SwappableItem == null) { continue; }
                    if (it.HiddenInGame || !it.AllowSwapping) { continue; }
                    if (shown >= 80) { break; }
                    shown++;

                    Item item = it;
                    string owner = "";
                    try
                    {
                        if (item.ParentInventory?.Owner is Character oc) { owner = " [" + oc.Name + "]"; }
                        else if (item.Submarine != null && Submarine.MainSub != null && item.Submarine != Submarine.MainSub) { owner = " [чужая саба]"; }
                    }
                    catch { }

                    string suffix = _selected == item ? "  ◀" : "";
                    var row = new GUIButton(
                        new RectTransform(new Vector2(1f, 0.07f), _list.Content.RectTransform),
                        item.ID + " | " + item.Name + owner + suffix);
                    row.Color = _selected == item ? SelColor : RowColor;
                    row.OnClicked = (b, d) =>
                    {
                        _selected = item;
                        if (_itemIdBox != null) { _itemIdBox.Text = item.ID.ToString(); }
                        // подсказка: первая замена из конфига свапа
                        try
                        {
                            var con = item.Prefab.SwappableItem.ConnectedItemsToSwap;
                            if (_prefabBox != null)
                            {
                                if (con != null && con.Count > 0) { _prefabBox.Text = con[0].swapTo.Value; }
                                else if (item.AvailableSwaps.Count > 0)
                                {
                                    foreach (ItemPrefab ap in item.AvailableSwaps) { _prefabBox.Text = ap.Identifier.Value; break; }
                                }
                            }
                        }
                        catch { }
                        RefreshList();
                        UpdateStatus();
                        return true;
                    };
                }

                if (shown == 0) { AddNoteRow("Свапаемых предметов не найдено"); }
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Swap] list fail: " + e.Message, DangerColor);
            }
        }

        private static void AddNoteRow(string text)
        {
            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.09f), _list.Content.RectTransform), style: null);
            row.Color = RowColor;
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text,
                textAlignment: Alignment.Center);
            txt.TextColor = DimColor;
            txt.CanBeFocused = false;
        }

        private static void UpdateStatus()
        {
            if (_status == null) { return; }
            string target = _selected == null ? "не выбран" : _selected.ID + " " + _selected.Name;
            string note = string.IsNullOrEmpty(_lastNote) ? "" : "\n" + _lastNote;
            _status.Text = "Цель: " + target + note;
        }

        // ========================================================
        //  СВАП: точный байт-лейаут MultiPlayerCampaign.ServerRead
        // ========================================================
        private static void LaunchSwap()
        {
            ushort victimId;
            string newPrefab;
            try
            {
                if (!ushort.TryParse(_itemIdBox.Text.Trim(), out victimId))
                {
                    GUI.AddMessage("[Swap] ID не число", DangerColor);
                    return;
                }
                newPrefab = _prefabBox.Text.Trim();
                if (string.IsNullOrEmpty(newPrefab))
                {
                    GUI.AddMessage("[Swap] Укажи identifier нового префаба", DangerColor);
                    return;
                }
            }
            catch
            {
                GUI.AddMessage("[Swap] Поля ввода не готовы", DangerColor);
                return;
            }

            // текущая локация — НЕ 65535 (иначе ManageMap-гейт в окне пермлесс
            // отрандомит выбор локации кампании!)
            ushort locIndex = 0;
            try
            {
                var loc = GameMain.GameSession?.Campaign?.Map?.CurrentLocation;
                if (loc != null) { locIndex = (ushort)loc.LocationIndex; }
            }
            catch { }

            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);

                msg.WriteUInt16(locIndex);        // currentLocIndex
                msg.WriteUInt16(locIndex);        // selectedLocIndex = текущая (без изменений)
                msg.WriteByte(0);                 // selectedMissionCount

                msg.WriteBoolean(false);          // purchasedHullRepairs
                msg.WriteBoolean(false);          // purchasedItemRepairs
                msg.WriteBoolean(false);          // purchasedLostShuttles

                msg.WriteByte(0);                 // buyCrateItems: storeCount=0
                msg.WriteByte(0);                 // subSellCrateItems: storeCount=0
                msg.WriteByte(0);                 // purchasedItems: storeCount=0
                msg.WriteByte(0);                 // soldItems: storeCount=0

                msg.WriteUInt16(0);               // purchasedUpgradeCount

                msg.WriteUInt16(1);               // purchasedItemSwapCount
                msg.WriteUInt16(victimId);        // itemToRemoveID — ЛЮБОЙ Item мира
                msg.WriteIdentifier(newPrefab.ToIdentifier()); // itemToInstall

                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Swap] send fail: " + e.Message, DangerColor);
                return;
            }

            _lastNote = "Пакет свапа " + victimId + " → " + newPrefab + " отправлен.\n" +
                        "Молчание = гейт перм/близость/цена не прошла.";
            GUI.AddMessage("[Swap] свап " + victimId + " → " + newPrefab + " отправлен", OkColor);
            GUI.AddMessage("[Swap] если тишина: хост жив в раунде / нет NPC рядом / нет денег", AccentColor);
            UpdateStatus();
        }

        private static void CloseWindow()
        {
            try { _window?.Close(); } catch { }
            _window = null;
            _list = null;
            _status = null;
            _itemIdBox = null;
            _prefabBox = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
