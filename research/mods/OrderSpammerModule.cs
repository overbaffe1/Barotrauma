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
            "Клик по модулю = бот под курсором разбирает ВСЁ своё.\n\n" +
            "• Адресный ORDER-чат (targetCharacter) — легит ваниль-путь\n" +
            "• Работает на СВОИХ и ЧУЖИХ ботах (чужие = красные)\n" +
            "• Режимы: Разобрать / Ждать / Следовать / Dismiss\n" +
            "• Ордера проходят спам-фильтр в 4 раза мягче чата\n" +
            "• Кампания: Разборка НЕОБРАТИМА\n" +
            "• Меню нет — просто навёлся на бота и кликнул";
        public override string Category => "exploit";

        private static GUIMessageBox _menu;
        private static Item _targetItem;
        private static int _orderIndex; // зафиксирован на deconstructthis
        private static string _lastOrder = "";

        private static readonly string[] OrderIds = { "deconstructthis" }; // только этот режим
        private static readonly string[] OrderNames = { "Разобрать предмет" };

        private static readonly Color AccentColor = new Color(100, 200, 255);
        private static readonly Color OwnBotColor = new Color(100, 255, 140);
        private static readonly Color ForeignBotColor = new Color(255, 120, 120);
        private static readonly Color DimColor = new Color(160, 170, 190);

        public override string GetLabel() => "Order Spammer 📢";

        public override void OnClick()
        {
            if (GameMain.Client == null || Character.Controlled == null)
            {
                GUI.AddMessage("[OrderSpam] Нужен персонаж в мультиплеере", Color.Orange);
                return;
            }

            // Бот под курсором (ваниль сама считает FocusedCharacter)
            Character bot = Character.Controlled.FocusedCharacter;
            if (bot == null || bot.Removed || !bot.IsBot || bot.IsDead)
            {
                GUI.AddMessage("[OrderSpam] Наведись на бота", Color.Orange);
                return;
            }

            bool hears = bot.CanHearCharacter(Character.Controlled);
            if (!hears)
            {
                GUI.AddMessage("[OrderSpam] " + bot.Name + " тебя не слышит", Color.Orange);
                return;
            }

            SendOrderToFleet(bot);
        }

        private static void CloseMenu() { }

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

        // Правильный путь (волна 566):
        // ШАГ 1: deconstructthis с character=null (TargetAllCharacters=true) —
        //        сервер добавляет в Item.DeconstructItems + рассылает всем
        // ШАГ 2: deconstructitems с character=bot — активирует AIObjectiveDeconstructItems
        private static void SendOrderToFleet(Character bot)
        {
            try
            {
                var crew = GameMain.GameSession?.CrewManager;
                if (crew == null)
                {
                    GUI.AddMessage("[OrderSpam] CrewManager недоступен", Color.Red);
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

                // Найти префабы
                OrderPrefab markPrefab = null;   // deconstructthis
                OrderPrefab activatePrefab = null; // deconstructitems
                foreach (OrderPrefab p in OrderPrefab.Prefabs)
                {
                    if (p.Identifier.Value.Equals("deconstructthis", StringComparison.OrdinalIgnoreCase))
                    { markPrefab = p; }
                    if (p.Identifier.Value.Equals("deconstructitems", StringComparison.OrdinalIgnoreCase))
                    { activatePrefab = p; }
                }
                if (markPrefab == null || activatePrefab == null)
                {
                    GUI.AddMessage("[OrderSpam] OrderPrefab не найден", Color.Red);
                    return;
                }

                // ШАГ 1: для каждого предмета — deconstructthis (character=null)
                // TargetAllCharacters=true → сервер Item.DeconstructItems.Add
                // CrewManager.SetCharacterOrder(null, order) на клиенте работает:
                // входит в TargetAllCharacters ветку (не требует character)
                int marked = 0;
                foreach (var item in targets)
                {
                    try
                    {
                        var order = new Order(markPrefab, Identifier.Empty, item, null, Character.Controlled);
                        crew.SetCharacterOrder(null, order, isNewOrder: true);
                        marked++;
                    }
                    catch (Exception e)
                    {
                        LuaCsLogger.LogError("[OrderSpam] mark: " + e.Message);
                    }
                }

                // ШАГ 2: deconstructitems боту — активирует AIObjectiveDeconstructItems
                try
                {
                    var order = new Order(activatePrefab, Identifier.Empty, bot, null, Character.Controlled);
                    crew.SetCharacterOrder(bot, order, isNewOrder: true);
                }
                catch (Exception e)
                {
                    LuaCsLogger.LogError("[OrderSpam] activate: " + e.Message);
                }

                _lastOrder = "deconstruct → " + bot.Name + " (" + marked + " предм.)";
                GUI.AddMessage("[OrderSpam] " + bot.Name + ": помечено " + marked +
                    " предметов + deconstructitems активирован", OwnBotColor);
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
