using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace CSHUB.Modules
{
    // ============================================================
    //  ORDER SPAMMER (волны 554/555) — «бот, разбери реактор».
    //  Клавиша B: наводишься на предмет → меню ордера → меню бота →
    //  адресный ORDER-чат этому боту. Бот реально выполняет ордер
    //  (deconstruct = необратимо в кампании).
    //
    //  Работает на СВОИХ и на ЧУЖИХ ботах (TeamID не проверяется ни
    //  сервером, ни CanHearCharacter) — важно: чужие отмечены красным.
    //  Гейт сервера: только CanHearCharacter(отправитель).
    //  Спам-фильтр ордеров в 4 раза мягче чата (0.25).
    //
    //  UI в стиле SellById: header со статусом, режим-циклер, список
    //  с цветовой кодировкой своя/чужая команда. Закрытие: кнопка
    //  «Закрыть», B повторно, или повторный клик по иконке модуля.
    // ============================================================
    public class OrderSpammerModule : CSModuleBase
    {
        public override string Id   => "order_spammer";
        public override string Name => "Order Spammer";
        public override string Description =>
            "Наведись на предмет → B → ордер + выбор бота.\n\n" +
            "• Адресный ORDER-чат (targetCharacter) — легит ваниль-путь\n" +
            "• Работает на СВОИХ и ЧУЖИХ ботах (чужие = красные)\n" +
            "• Режимы: Разобрать / Ждать / Следовать / Dismiss\n" +
            "• Ордера проходят спам-фильтр в 4 раза мягче чата\n" +
            "• Кампания: Разборка НЕОБРАТИМА\n" +
            "• Закрыть: кнопка, B повторно, или повторный клик по модулю";
        public override string Category => "exploit";

        private static GUIMessageBox _menu;
        private static Item _targetItem;
        private static int _orderIndex; // 0=deconstruct, 1=wait, 2=follow, 3=dismiss
        private static string _lastOrder = "";

        private static readonly string[] OrderIds = { "deconstructthis", "deconstructitems", "wait", "follow", "dismissed" };
        private static readonly string[] OrderNames = { "Разобрать предмет (1)", "РАЗБИРАЙ ВСЁ (1)", "Ждать здесь", "Следовать за мной", "Dismiss" };

        private static readonly Color AccentColor = new Color(100, 200, 255);
        private static readonly Color OwnBotColor = new Color(100, 255, 140);
        private static readonly Color ForeignBotColor = new Color(255, 120, 120);
        private static readonly Color DimColor = new Color(160, 170, 190);

        public override string GetLabel()
        {
            string shortName = OrderNames[_orderIndex].Split(' ')[0];
            return "Order Spammer 📢 [" + shortName + "]";
        }

        public override void Update()
        {
            if (_menu == null && PlayerInput.KeyHit(Keys.B) && Character.Controlled != null)
            {
                OnClick();
            }
        }

        public override void OnClick()
        {
            if (GameMain.Client == null || Character.Controlled == null)
            {
                GUI.AddMessage("[OrderSpam] Нужен персонаж в мультиплеере", Color.Orange);
                return;
            }

            if (_menu != null) { CloseMenu(); return; }

            Item target = FindItemUnderCursor();
            ShowMenu(target);
        }

        private static void CloseMenu()
        {
            _menu?.Close();
            _menu = null;
            _targetItem = null;
        }

        private static Item FindItemUnderCursor()
        {
            Camera cam = (Screen.Selected as GameScreen)?.Cam;
            if (cam == null) { return null; }
            Vector2 cursorWorld = cam.ScreenToWorld(PlayerInput.MousePosition);

            Item best = null;
            float bestDist = 150f;
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                float d = Vector2.Distance(it.WorldPosition, cursorWorld);
                if (d < bestDist) { bestDist = d; best = it; }
            }
            return best;
        }

        private static void ShowMenu(Item target)
        {
            _menu?.Close();
            _targetItem = target;

            _menu = new GUIMessageBox(
                headerText: "ORDER SPAMMER",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.52f, 0.72f));

            var content = _menu.Content;
            content.ClearChildren();

            var header = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.16f), content.RectTransform, Anchor.TopCenter),
                style: null);
            header.Color = new Color(40, 50, 70);

            var headerText = new GUITextBlock(
                new RectTransform(new Vector2(0.66f, 1f), header.RectTransform, Anchor.CenterLeft),
                "Наведись на БОТА → клик = разобрать ВСЁ, что у него есть",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont);
            headerText.TextColor = AccentColor;

            var closeBtn = new GUIButton(
                new RectTransform(new Vector2(0.3f, 0.6f), header.RectTransform, Anchor.CenterRight),
                "Закрыть (B)");
            closeBtn.Color = new Color(120, 60, 60);
            closeBtn.OnClicked = (b, d) => { CloseMenu(); return true; };

            var controls = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.11f), content.RectTransform, Anchor.TopCenter)
                { RelativeOffset = new Vector2(0f, 0.17f) }, style: null);
            controls.Color = new Color(30, 38, 55);

            var ctrlLayout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.98f, 0.85f), controls.RectTransform, Anchor.Center),
                isHorizontal: true);
            ctrlLayout.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.2f, 1f), ctrlLayout.RectTransform),
                "Ордер:", textAlignment: Alignment.CenterLeft);

            var orderBtn = new GUIButton(
                new RectTransform(new Vector2(0.6f, 1f), ctrlLayout.RectTransform),
                OrderNames[_orderIndex]);
            orderBtn.Color = new Color(80, 60, 130);
            orderBtn.ToolTip = "Клик = следующий ордер";
            orderBtn.OnClicked = (b, d) =>
            {
                _orderIndex = (_orderIndex + 1) % OrderIds.Length;
                orderBtn.Text = OrderNames[_orderIndex];
                return true;
            };

            var listFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.7f), content.RectTransform, Anchor.BottomCenter),
                style: null);
            listFrame.Color = new Color(20, 25, 35);

            var list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
            list.Color = new Color(20, 25, 35);

            Character me = Character.Controlled;
            int ownBots = 0, foreignBots = 0, total = 0;

            var bots = new List<Character>();
            foreach (Character c in Character.CharacterList)
            {
                if (c == null || c.Removed || !c.IsBot || c.IsDead) { continue; }
                bots.Add(c);
            }
            bots.Sort((a, b) =>
            {
                bool aOwn = a.TeamID == me.TeamID || a.IsOnPlayerTeam;
                bool bOwn = b.TeamID == me.TeamID || b.IsOnPlayerTeam;
                if (aOwn != bOwn) { return aOwn ? -1 : 1; }
                return string.CompareOrdinal(a.Name, b.Name);
            });

            foreach (Character bot in bots)
            {
                total++;
                bool hears = bot.CanHearCharacter(me);
                bool own = bot.TeamID == me.TeamID || bot.IsOnPlayerTeam;
                if (own) { ownBots++; } else { foreignBots++; }

                var captured = bot;
                var job = bot.Info?.Job?.Name.Value ?? "?";

                int itemCount = 0;
                try
                {
                    foreach (var hi in bot.HeldItems) { itemCount++; }
                    if (bot.Inventory != null)
                    {
                        foreach (var ai in bot.Inventory.AllItemsMod) { itemCount++; }
                    }
                }
                catch { }

                var row = new GUIButton(
                    new RectTransform(new Vector2(1f, 0.09f), list.Content.RectTransform), style: null);
                row.Color = new Color(34, 42, 58, 230);
                row.HoverColor = new Color(70, 90, 130, 255);
                row.ToolTip = (hears ? "слышит тебя" : "НЕ слышит")
                    + " | предметов: " + itemCount
                    + " | Команда: " + bot.TeamID + (own ? " (своя)" : " (ЧУЖАЯ)");

                var rowLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.88f), row.RectTransform, Anchor.Center),
                    isHorizontal: true);
                rowLayout.RelativeSpacing = 0.008f;

                var mark = new GUITextBlock(
                    new RectTransform(new Vector2(0.08f, 1f), rowLayout.RectTransform),
                    own ? "СВОЙ" : "ЧУЖОЙ",
                    textAlignment: Alignment.Center, font: GUIStyle.SmallFont);
                mark.TextColor = own ? OwnBotColor : ForeignBotColor;

                var nameText = new GUITextBlock(
                    new RectTransform(new Vector2(0.42f, 1f), rowLayout.RectTransform),
                    bot.Name, textAlignment: Alignment.CenterLeft);
                nameText.TextColor = own ? Color.White : ForeignBotColor;
                nameText.Font = GUIStyle.SmallFont;

                var jobText = new GUITextBlock(
                    new RectTransform(new Vector2(0.26f, 1f), rowLayout.RectTransform),
                    job, textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont);
                jobText.TextColor = DimColor;

                var cntText = new GUITextBlock(
                    new RectTransform(new Vector2(0.18f, 1f), rowLayout.RectTransform),
                    "предметов: " + itemCount,
                    textAlignment: Alignment.Center, font: GUIStyle.SmallFont);
                cntText.TextColor = itemCount > 0 ? new Color(255, 220, 100) : DimColor;

                row.OnClicked = (b, d) =>
                {
                    if (!hears)
                    {
                        GUI.AddMessage("[OrderSpam] " + captured.Name + " тебя не слышит", Color.Orange);
                        return true;
                    }
                    SendOrderToFleet(captured);
                    return true;
                };
            }

            var summary = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.05f), content.RectTransform, Anchor.BottomCenter),
                "ботов: " + total + " (своих " + ownBots + " / чужих " + foreignBots + ")" +
                (string.IsNullOrEmpty(_lastOrder) ? "" : " | " + _lastOrder),
                textAlignment: Alignment.CenterLeft, font: GUIStyle.SmallFont);
            summary.TextColor = DimColor;

            if (total == 0)
            {
                var empty = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.15f), list.Content.RectTransform),
                    "Ботов на сервере нет.", textAlignment: Alignment.Center);
                empty.TextColor = DimColor;
            }
        }

        // Разбираем ВСЁ, что есть у бота: предметы инвентаря + руки.
        // Используем CrewManager.SetCharacterOrder — легит ваниль-путь,
        // который сам отправляет правильный ORDER-чат пакет на сервер.
        private static void SendOrderToFleet(Character bot)
        {
            try
            {
                OrderPrefab prefab = null;
                foreach (OrderPrefab p in OrderPrefab.Prefabs)
                {
                    if (p.Identifier.Value.Equals(OrderIds[_orderIndex], StringComparison.OrdinalIgnoreCase))
                    { prefab = p; break; }
                }
                if (prefab == null)
                {
                    GUI.AddMessage("[OrderSpam] OrderPrefab не найден", Color.Red);
                    return;
                }

                var targets = new List<Item>();
                try
                {
                    targets.AddRange(bot.HeldItems);
                    if (bot.Inventory != null) { targets.AddRange(bot.Inventory.AllItemsMod); }
                }
                catch { }

                var distinct = new List<Item>();
                var seen = new HashSet<Item>();
                foreach (var t in targets)
                {
                    if (t == null || t.Removed) { continue; }
                    if (seen.Add(t)) { distinct.Add(t); }
                }
                targets = distinct;

                if (targets.Count == 0)
                {
                    GUI.AddMessage("[OrderSpam] у " + bot.Name + " нет предметов", Color.Orange);
                    return;
                }

                var crewManager = GameMain.GameSession?.CrewManager;
                if (crewManager == null)
                {
                    GUI.AddMessage("[OrderSpam] CrewManager недоступен", Color.Red);
                    return;
                }

                int sent = 0;
                bool isDeconstructThis = OrderIds[_orderIndex] == "deconstructthis";
                foreach (var item in targets)
                {
                    try
                    {
                        var order = new Order(prefab, Identifier.Empty, item, null, Character.Controlled);
                        crewManager.SetCharacterOrder(bot, order, isNewOrder: true);

                        // DeconstructThis: локально помечаем в Item.DeconstructItems —
                        // боты берут цели именно из этого HashSet'а
                        if (isDeconstructThis)
                        {
                            try { Item.DeconstructItems.Add(item); } catch { }
                        }
                        sent++;
                    }
                    catch { }
                }

                _lastOrder = OrderNames[_orderIndex] + " → " + bot.Name + " x" + sent;
                GUI.AddMessage("[OrderSpam] " + bot.Name + ": " + OrderNames[_orderIndex] +
                    " на " + sent + " предметов (SetCharacterOrder)", OwnBotColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[OrderSpam] фейл: " + e.Message, Color.Red);
            }
        }

        public override void Dispose()
        {
            CloseMenu();
            base.Dispose();
        }
    }
}
