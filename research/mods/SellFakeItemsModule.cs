using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  Sell Fake Items (волна 452, починено в 516).
    //  Деньги за несуществующие предметы: сервер платит по префабу,
    //  itemId=0 → FindEntityByID=нашелся → удаление пропускается.
    //  Требуется AllowedToManageCampaign(SellInventoryItems):
    //  перма, ManageCampaign, owner, ты единственный клиент,
    //  или НИКТО на сервере не имеет пермы (= все могут).
    // ============================================================
    public class SellFakeItemMenuModule : CSModuleBase
    {
        public override string Id   => "sell_fake_items";
        public override string Name => "Sell Fake Items";
        public override string Description =>
            "Sells forged SoldItem records (free money).\n" +
            "Works when AllowedToManageCampaign(SellInventoryItems) passes:\n" +
            "owner / ManageCampaign / SellInventoryItems perm / you are the only\n" +
            "client / nobody on the server holds the perm (then everyone can).";
        public override string Category => "exploit";

        public override string GetLabel() => "Sell Fake Items";

        public override void OnClick()
        {
            try { SellFakePicker.Toggle(); }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[SellFake] " + e);
                new GUIMessageBox("CSHub — Sell Fake Items", e.ToString(),
                    new Vector2(0.55f, 0.4f));
            }
        }
    }

    internal static class SellFakePicker
    {
        private static GUIMessageBox _box;
        private static GUIListBox   _list;
        private static GUITextBox   _qty;
        private static GUIDropDown  _storeDrop;
        private static GUITextBlock _status;

        private static ItemPrefab         _selected;
        private static Location.StoreInfo _currentStore;

        public static void Toggle()
        {
            if (_box != null) { Close(); return; }
            Open();
        }

        public static void Close()
        {
            if (_box != null)
            {
                try { _box.Close(); } catch { }
                _box = null;
            }
            _list = null; _qty = null; _storeDrop = null; _status = null;
            _selected = null; _currentStore = null;
        }

        public static void Open()
        {
            var stores = GetCurrentStores();

            _box = new GUIMessageBox(
                headerText: "SELL FAKE ITEMS",
                text: "",
                buttons: new LocalizedString[] { "SELL", "CLOSE" },
                relativeSize: new Vector2(0.78f, 0.86f));

            _box.Buttons[0].Color = new Color(60, 180, 90);
            _box.Buttons[1].Color = new Color(130, 70, 70);
            _box.Buttons[1].OnClicked = (_, _) => { Close(); return true; };

            var rootRt = new RectTransform(
                new Vector2(0.96f, 0.72f),
                _box.InnerFrame.RectTransform,
                Anchor.TopCenter);
            rootRt.AbsoluteOffset = new Point(0, (int)GUI.IntScale(46));
            var root = new GUILayoutGroup(
                rootRt,
                isHorizontal: false, childAnchor: Anchor.TopLeft)
            {
                Stretch = true,
                RelativeSpacing = 0.01f
            };

            var permBlock = new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.08f), root.RectTransform),
                BuildPermissionBanner(),
                textAlignment: Alignment.CenterLeft, wrap: true,
                font: GUIStyle.SmallFont)
            {
                TextColor = CampaignMode.AllowedToManageCampaign(ClientPermissions.SellInventoryItems)
                    ? new Color(120, 255, 140)
                    : new Color(255, 180, 80)
            };

            var ctl = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.08f), root.RectTransform),
                isHorizontal: true, childAnchor: Anchor.CenterLeft)
            { RelativeSpacing = 0.02f, Stretch = true };

            new GUITextBlock(
                new RectTransform(new Vector2(0.12f, 1f), ctl.RectTransform),
                "Store:", textAlignment: Alignment.CenterRight);

            _storeDrop = new GUIDropDown(
                new RectTransform(new Vector2(0.42f, 1f), ctl.RectTransform));
            foreach (var s in stores)
                _storeDrop.AddItem(new RawLString(s.Identifier.Value + " (bal " + s.Balance + ")"), s);
            if (stores.Count > 0)
            {
                _currentStore = stores[0];
                _storeDrop.SelectItem(0);
            }
            _storeDrop.OnSelected = (_, data) =>
            {
                if (data is Location.StoreInfo s)
                {
                    _currentStore = s;
                    BuildItemList();
                }
                return true;
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.08f, 1f), ctl.RectTransform),
                "Qty:", textAlignment: Alignment.CenterRight);
            _qty = new GUITextBox(
                new RectTransform(new Vector2(0.20f, 1f), ctl.RectTransform),
                "1");

            _status = new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.05f), root.RectTransform),
                BuildStatus(),
                textAlignment: Alignment.CenterLeft, wrap: true,
                font: GUIStyle.SmallFont)
            {
                TextColor = new Color(210, 220, 230)
            };

            var listFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.78f), root.RectTransform),
                style: null)
            { Color = new Color(0, 0, 0, 100) };

            _list = new GUIListBox(
                new RectTransform(Vector2.One * 0.99f,
                    listFrame.RectTransform, Anchor.Center))
            {
                HoverCursor = CursorState.Hand,
                SelectedColor = new Color(80, 200, 255, 80),
                Spacing = (int)Math.Max(2, GUI.Scale)
            };
            _list.OnSelected = (_, data) =>
            {
                if (data is ItemPrefab pf)
                {
                    _selected = pf;
                    LuaCsLogger.Log("[SellFake] Selected: " + pf.Name.Value);
                }
                return true;
            };

            BuildItemList();

            _box.Buttons[0].OnClicked = (_, _) =>
            {
                if (_currentStore == null) { UpdateStatus("No store."); return true; }
                if (_selected == null)    { UpdateStatus("Pick an item."); return true; }
                if (!int.TryParse(_qty.Text, out int n) || n <= 0) n = 1;

                try
                {
                    if (!ServerInteractionAvailable(out string why))
                    {
                        UpdateStatus("SERVER WILL SKIP: " + why);
                        LuaCsLogger.Log("[SellFake] interaction gate: " + why);
                        return true;
                    }

                    int price = _currentStore.GetAdjustedItemSellPrice(_selected);
                    if (price <= 0) { UpdateStatus("Item has no sell price here."); return true; }

                    // сервер молча скипает позиции дороже баланса магазина —
                    // режем количество до реально оплачиваемого
                    int affordable = _currentStore.Balance / price;
                    if (affordable <= 0)
                    {
                        UpdateStatus("Store balance (" + _currentStore.Balance +
                            ") < sell price (" + price + ") — server would skip it. Pick cheaper item.");
                        return true;
                    }
                    if (n > affordable)
                    {
                        n = affordable;
                        UpdateStatus("Capped to " + n + "x — store can't afford more (bal " +
                            _currentStore.Balance + ", " + price + " mk each).");
                    }

                    SellFakePackets.Send(_currentStore.Identifier, _selected, n);
                    UpdateStatus("Sent " + n + "x " + _selected.Name.Value +
                                 " @ " + price + " mk. Check wallet.");
                }
                catch (Exception e)
                {
                    UpdateStatus("Error: " + e.Message);
                    LuaCsLogger.LogError("[SellFake] " + e);
                }
                return true;
            };
        }

        // --------------------------------------------------------

        private static List<Location.StoreInfo> GetCurrentStores()
        {
            var loc = GameMain.GameSession?.Campaign?.Map?.CurrentLocation;
            if (loc?.Stores == null) return new List<Location.StoreInfo>();
            return loc.Stores.Values.ToList();
        }

        // Точное зеркало серверного AllowedToManageCampaign (CampaignMode,
        // клиентская версия публична): перма / ManageCampaign / owner /
        // единственный клиент / никто не имеет пермы. Больше НЕ блокируем
        // отправку — решает сервер, баннер только информирует.
        private static bool ServerWouldAllow()
        {
            return CampaignMode.AllowedToManageCampaign(ClientPermissions.SellInventoryItems);
        }

        // Точное зеркало серверного HasCampaignInteractionAvailable(Store)
        // (MultiPlayerCampaign:1161). САМЫЙ ЧАСТЫЙ ПРИЧИН «НЕ РАБОТАЕТ»:
        // весь блок продажи обёрнут в него (server:954) — без живого
        // персонажа рядом с NPC-торговцем (250 юнитов) или
        // AllowRemoteCampaignInteractions сервер МОЛЧА скипает продажи.
        private static bool ServerInteractionAvailable(out string reason)
        {
            var me = GameMain.Client?.Character;
            if (me == null || me.IsIncapacitated)
            {
                reason = "нет живого персонажа (мёртв/в стуне) — сервер скипнет продажу";
                return false;
            }
            if (GameMain.Server?.ServerSettings is { AllowRemoteCampaignInteractions: true })
            {
                reason = "remote interactions разрешены — продавай откуда угодно";
                return true;
            }
            foreach (Character other in Character.CharacterList)
            {
                if (other.CampaignInteractionType != CampaignMode.InteractionType.Store) continue;
                if (me.CanInteractWith(other, maxDist: 250.0f))
                {
                    reason = "торговец рядом";
                    return true;
                }
            }
            reason = "НЕТ торговца в 250 юнитах — встань к NPC-магазина или серверу нужен AllowRemoteCampaignInteractions";
            return false;
        }

        private static string BuildPermissionBanner()
        {
            if (GameMain.Client == null) return "Not connected.";
            bool perms = ServerWouldAllow();
            string interact = ServerInteractionAvailable(out string why);
            if (!perms) return "PERMS: server would REJECT (AllowedToManageCampaign=false). | " + why;
            if (!interact) return "PERMS ok, но: " + why;
            return "Server would ALLOW this sale (" + why + ").";
        }

        private static string BuildStatus()
        {
            if (GameMain.Client == null) return "Not connected.";
            if (!(GameMain.GameSession?.Campaign is MultiPlayerCampaign))
                return "Must be in a multiplayer campaign.";
            var stores = GetCurrentStores();
            return stores.Count == 0
                ? "No stores at current location."
                : stores.Count + " store(s) ready.";
        }

        private static void UpdateStatus(string text)
        {
            if (_status != null) _status.Text = text;
        }

        private static void BuildItemList()
        {
            if (_list == null) return;
            _list.ClearChildren();
            _selected = null;

            if (_currentStore == null)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.08f), _list.Content.RectTransform),
                    "No store selected.", textAlignment: Alignment.Center)
                { TextColor = Color.Gray };
                return;
            }

            var candidates = new List<(ItemPrefab pf, int price)>();
            foreach (ItemPrefab pf in ItemPrefab.Prefabs)
            {
                if (pf == null) continue;
                int price = _currentStore.GetAdjustedItemSellPrice(pf);
                // показываем только то, что магазин реально оплатит
                if (price > 0 && price <= _currentStore.Balance) candidates.Add((pf, price));
            }
            candidates.Sort((a, b) => b.price.CompareTo(a.price));

            foreach (var (pf, price) in candidates.Take(500))
            {
                var row = new GUIButton(
                    new RectTransform(new Vector2(1f, 0.05f), _list.Content.RectTransform),
                    style: null)
                {
                    UserData = pf,
                    Color = new Color(40, 40, 50, 220),
                    HoverColor = new Color(70, 90, 130, 255),
                    PressedColor = new Color(70, 90, 130, 255)
                };

                var inner = new GUILayoutGroup(
                    new RectTransform(Vector2.One * 0.98f, row.RectTransform, Anchor.Center),
                    isHorizontal: true, childAnchor: Anchor.CenterLeft);

                new GUITextBlock(
                    new RectTransform(new Vector2(0.62f, 1f), inner.RectTransform),
                    pf.Name.Value, textAlignment: Alignment.CenterLeft)
                { TextColor = Color.White };

                new GUITextBlock(
                    new RectTransform(new Vector2(0.22f, 1f), inner.RectTransform),
                    pf.Identifier.Value,
                    textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont)
                { TextColor = new Color(170, 170, 190) };

                new GUITextBlock(
                    new RectTransform(new Vector2(0.14f, 1f), inner.RectTransform),
                    price + " mk",
                    textAlignment: Alignment.CenterRight)
                { TextColor = new Color(120, 255, 140) };
            }
        }
    }

    internal static class SellFakePackets
    {
        private const int MaxItemsPerPacket = 20;

        public static void Send(Identifier storeId, ItemPrefab prefab, int quantity)
        {
            var client = GameMain.Client;
            if (client?.ClientPeer == null)
                throw new InvalidOperationException("Not connected.");
            if (!(GameMain.GameSession?.Campaign is MultiPlayerCampaign))
                throw new InvalidOperationException("Multiplayer campaign required.");
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            // реальные индексы локаций: на серверах с AllowDebugTeleport
            // ноль телепортнул бы карту в локацию 0
            var map = GameMain.GameSession?.Campaign?.Map;
            ushort currentLoc = (ushort)(map?.CurrentLocationIndex is int c && c >= 0 ? c : 0xFFFF);
            ushort selectedLoc = (ushort)(map?.SelectedLocationIndex is int s && s >= 0 ? s : 0xFFFF);

            int sent = 0;
            while (quantity > 0)
            {
                int batch = Math.Min(quantity, MaxItemsPerPacket);
                WriteOne(storeId, prefab, batch, currentLoc, selectedLoc);
                quantity -= batch;
                sent += batch;
            }
            LuaCsLogger.Log("[SellFake] Sent " + sent + "x " + prefab.Identifier + " -> " + storeId);
        }

        // Байт-в-байт зеркалим MultiPlayerCampaign.ClientWrite:
        // сообщение — БИТОВЫЙ поток, игра НЕ выравнивает после булов!
        private static void WriteOne(Identifier storeId, ItemPrefab prefab, int count,
            ushort currentLoc, ushort selectedLoc)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);

            msg.WriteUInt16(currentLoc);
            msg.WriteUInt16(selectedLoc);
            msg.WriteByte(0);                // selectedMissionCount
            msg.WriteBoolean(false);         // hull repairs
            msg.WriteBoolean(false);         // item repairs
            msg.WriteBoolean(false);         // lost shuttles
            // НИКАКОГО WritePadBits здесь: 3 бита + байт storeCount подряд,
            // сервер читает поток без выравнивания. Пэд сдвигал всё и убивал пакет.
            msg.WriteByte(0);                // buyCrate: storeCount
            msg.WriteByte(0);                // subSellCrate: storeCount
            msg.WriteByte(0);                // purchasedItems: storeCount
            msg.WriteByte(1);                // soldItems: storeCount = 1
            msg.WriteIdentifier(storeId);
            msg.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++)
            {
                msg.WriteIdentifier(prefab.Identifier);
                msg.WriteUInt16(0);          // itemId = NullEntityID → не найдётся → не удалится
                msg.WriteBoolean(false);     // removed
                msg.WriteByte(0);            // sellerId
                msg.WriteByte((byte)SoldItem.SellOrigin.Character); // 0
            }
            msg.WriteUInt16(0);              // purchasedUpgradeCount
            msg.WriteUInt16(0);              // purchasedItemSwapCount

            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }
    }
}
