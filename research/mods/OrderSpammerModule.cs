using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace CSHUB.Modules
{
    // ============================================================
    //  ORDER SPAMMER (волна 554) — «бот, разбери реактор».
    //  Клавиша B: наводишься на предмет → список ботов, которые тебя
    //  слышат (CanHearCharacter) → выбрал бота → отправляется
    //  ORDER-чат «Deconstruct this @ предмет» адресованно этому боту.
    //  Бот реально разбирает предмет (кампания = необратимо).
    //
    //  Пакет: GameClient.SendChatMessage(OrderChatMessage) — легитный
    //  ваниль-путь (тот же что CrewManager при выборе ордера из меню).
    //  Гейт на сервере: ChatMessage.ServerRead → ORDER → targetCharacter
    //  .SetOrder — проверяется только CanHearCharacter(отправитель).
    //  Спам-фильтр для ордеров в 4 раза мягче (similarityMultiplier=0.25).
    // ============================================================
    public class OrderSpammerModule : CSModuleBase
    {
        public override string Id   => "order_spammer";
        public override string Name => "Order Spammer";
        public override string Description =>
            "Наведись на предмет → нажми B → выбери бота → бот разберёт предмет.\n\n" +
            "• Адресный ORDER-чат (targetCharacter) — легит ваниль-путь\n" +
            "• Работает на ботах, которые тебя слышат (рядом/рация)\n" +
            "• Ордера проходят спам-фильтр в 4 раза мягче обычного чата\n" +
            "• Кампания: разборка НЕОБРАТИМА — проверь что разбираешь!";
        public override string Category => "exploit";

        private static GUIMessageBox _menu;

        public override string GetLabel() => "Order Spammer 📢 (B)";

        public override void Update()
        {
            // B-клавиша прямо в игре: наведёшься на предмет + B = меню ботов
            if (PlayerInput.KeyHit(Keys.B) && Character.Controlled != null && _menu == null)
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

            Item target = FindItemUnderCursor();
            if (target == null)
            {
                GUI.AddMessage("[OrderSpam] Наведись на предмет", Color.Orange);
                return;
            }

            ShowBotPicker(target);
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

        private static void ShowBotPicker(Item target)
        {
            _menu?.Close();
            _menu = new GUIMessageBox(
                headerText: "ORDER: DECONSTRUCT",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.45f, 0.65f));

            var content = _menu.Content;
            content.ClearChildren();

            var header = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.1f), content.RectTransform),
                "Цель: " + target.Name + " (#" + target.ID + ")\nВыбери бота, который тебя слышит:",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont);
            header.TextColor = new Color(255, 200, 120);

            var list = new GUIListBox(
                new RectTransform(new Vector2(1f, 0.82f), content.RectTransform));

            Character me = Character.Controlled;
            int bots = 0;
            foreach (Character c in Character.CharacterList)
            {
                if (c == null || c.Removed || !c.IsBot || c.IsDead) { continue; }
                if (c.TeamID != me.TeamID) { continue; }
                if (!c.CanHearCharacter(me)) { continue; }

                bots++;
                var bot = c;
                var row = new GUIButton(
                    new RectTransform(new Vector2(1f, 0.14f), list.Content.RectTransform),
                    c.Name + " (" + (c.Info?.Job?.Name.Value ?? "?") + ")");
                row.Color = new Color(50, 70, 100, 230);
                row.HoverColor = new Color(80, 120, 180);
                row.OnClicked = (b, d) =>
                {
                    SendDeconstructOrder(bot, target);
                    _menu?.Close();
                    _menu = null;
                    return true;
                };
            }

            if (bots == 0)
            {
                var empty = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.15f), list.Content.RectTransform),
                    "Никого не слышно — подойди ближе или возьми рацию.",
                    textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont);
                empty.TextColor = new Color(220, 160, 90);
            }
        }

        private static void SendDeconstructOrder(Character bot, Item target)
        {
            try
            {
                OrderPrefab prefab = null;
                foreach (OrderPrefab p in OrderPrefab.Prefabs)
                {
                    if (p.Identifier.Value.Equals("deconstructthis", StringComparison.OrdinalIgnoreCase))
                    { prefab = p; break; }
                }
                if (prefab == null)
                {
                    GUI.AddMessage("[OrderSpam] OrderPrefab deconstructthis не найден", Color.Red);
                    return;
                }

                var order = new Order(prefab, Identifier.Empty, target, null, Character.Controlled);
                var msg = new OrderChatMessage(order, bot, Character.Controlled, isNewOrder: true);
                GameMain.Client?.SendChatMessage(msg);

                GUI.AddMessage("[OrderSpam] " + bot.Name + " получил приказ разобрать " + target.Name, Color.Lime);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[OrderSpam] фейл: " + e.Message, Color.Red);
            }
        }

        public override void Dispose()
        {
            _menu?.Close();
            _menu = null;
            base.Dispose();
        }
    }
}
