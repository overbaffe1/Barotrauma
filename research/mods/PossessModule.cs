// ============================================================================
//  PossessModule.cs — «ВСЕЛЕНИЕ» (волна 722, ФАНТОМ)
//
//  ОТВЕТ НА ВОПРОС «можно ли вселиться в монстра без прав и читов»:
//
//  НАСТОЯЩЕЕ ВСЕЛЕНИЕ (чтобы СЕРВЕР и другие игроки видели) — НЕВОЗМОЖНО:
//    1. TAKEOVERBOT ищет ID только среди hireable аутпоста + ростера экипажа
//       (GetHireableCharacters / CrewManager.GetCharacterInfos) — монстров там НЕТ
//    2. TryTakeOverBot требует IsBot, а IsBot = HumanAIController enabled
//       (Character.cs:175) — монстры (EnemyAIController) ДАЮТ IsBot = FALSE
//    3. Инпут-роутинг: сервер применяет твой ввод ТОЛЬКО к c.Character,
//       который он сам назначает при спавне. Клиентским пакетом не меняется.
//    → сервер-авторитетное вселение = только консоль/серверный LuaCS (пермы).
//
//  ЧТО ДЕЛАЕТ ЭТОТ МОДУЛЬ — ФАНТОМ (клиент-сайд, только на твоём экране):
//    Character.Controlled = монстр → камера и УПРАВЛЕНИЕ у тебя, симуляция
//    честная (клиент симулирует всех). Сервер продолжает рулить настоящим
//    монстром (AI), его позиции прилетают по сети → резина/дёрганье.
//    Другие игроки видят: монстра под AI + твоё тело, стоящее АФК.
//
//  ⚠ АФК-КИК: тело без инпута тикает KickAFKTimer (если на сервере включён
//  KickAFKTime). АНТИДОТ: KickAFKTimer сбрасывается ЛЮБЫМ чат-пакетом
//  (ChatMessage.ServerRead: c.KickAFKTimer = 0) — изредка пиши в чат,
//  даже пустую строку, и таймер обнулится.
//
//  Управление сниппет для теста: вселение не слетает от смены Focus,
//  но слетает при смерти монстра/дисконнекте. Вернуться — кнопка или
//  Character.Controlled = GameMain.Client.Character.
// ============================================================================

using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class PossessModule : CSModuleBase
    {
        public override string Id   => "possess";
        public override string Name => "Possess";
        public override string Description =>
            "PHANTOM possession: control a monster/bot on YOUR screen (client-side Character.Controlled). " +
            "Server keeps AI authority - others see the real monster + your AFK body. Real (server-side) " +
            "possession is impossible without perms: IsBot only counts HumanAIController, monster IDs are " +
            "outside crew/hireable lookup, input routing is locked to c.Character. AFK-kick antidote: chat resets the timer.";
        public override string Category => "exploit";

        public override string GetLabel() => "Possess 👻";

        private static GUIMessageBox _box;
        private static GUILayoutGroup _listGroup;
        private static GUITextBlock _status;

        public override void OnClick()
        {
            if (_box != null) { _box.Close(); _box = null; return; }
            Open();
        }

        private static void Open()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[Possess] нет подключения", Color.Red);
                return;
            }
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.GameStarted)
            {
                GUI.AddMessage("[Possess] нужен запущенный раунд (монстры спавнятся только в нём)", Color.Orange);
            }

            _box = new GUIMessageBox(
                headerText: "POSSESS — фантом-вселение (только твой экран)",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.66f, 0.62f));

            var content = _box.Content;
            content.ClearChildren();

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.3f), content.RectTransform),
                "НАСТОЯЩЕГО вселения без пермов НЕТ (IsBot = только HumanAI, ID монстров вне crew-лукапа).\n" +
                "Это ФАНТОМ: рулишь монстра у СЕБЯ на экране, сервер продолжает его AI.\n" +
                "⚠ Твоё тело стоит АФК — изредка ПИШИ В ЧАТ (сбрасывает AFK-таймер кика).",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont)
            { TextColor = new Color(170, 140, 255) };

            var row1 = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.14f), content.RectTransform),
                isHorizontal: true);
            row1.RelativeSpacing = 0.01f;

            var focusBtn = new GUIButton(
                new RectTransform(new Vector2(0.48f, 1f), row1.RectTransform),
                "👻 ВСЕЛИТЬСЯ (под курсором)");
            focusBtn.Color = new Color(130, 80, 180);
            focusBtn.OnClicked = (b, d) =>
            {
                Character me = Character.Controlled;
                if (me == null) { SetStatus("нет контролируемого персонажа (сначала заспавнься)"); return true; }
                Character target = me.FocusedCharacter;
                if (target == null || target == me)
                {
                    SetStatus("наведи курсор на монстра/бота и жми ещё раз");
                    return true;
                }
                Possess(target);
                return true;
            };

            var nearBtn = new GUIButton(
                new RectTransform(new Vector2(0.48f, 1f), row1.RectTransform),
                "👹 БЛИЖАЙШИЙ МОНСТР");
            nearBtn.Color = new Color(150, 60, 60);
            nearBtn.OnClicked = (b, d) =>
            {
                Character me = Character.Controlled;
                Character best = null;
                float bestDist = float.MaxValue;
                foreach (Character ch in Character.CharacterList)
                {
                    if (ch == null || ch.Removed || ch.IsDead || ch == me) { continue; }
                    if (!(ch.AIController is EnemyAIController)) { continue; }
                    float d2 = Vector2.DistanceSquared(
                        me != null ? me.WorldPosition : Vector2.Zero, ch.WorldPosition);
                    if (d2 < bestDist) { bestDist = d2; best = ch; }
                }
                if (best == null)
                {
                    SetStatus("монстров рядом не синхронизировано (их нет в радиусе синка)");
                    return true;
                }
                Possess(best);
                return true;
            };

            var backBtn = new GUIButton(
                new RectTransform(new Vector2(0.97f, 0.1f), content.RectTransform),
                "↩ ВЕРНУТЬСЯ В СВОЁ ТЕЛО");
            backBtn.Color = new Color(60, 110, 150);
            backBtn.OnClicked = (b, d) =>
            {
                if (GameMain.Client?.Character != null)
                {
                    Character.Controlled = GameMain.Client.Character;
                    SetStatus("вернулся в тело");
                }
                else
                {
                    SetStatus("своё тело недоступно (мёртво/не заспавнено)");
                }
                return true;
            };

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.08f), content.RectTransform),
                "Монстры в радиусе синхронизации:", font: GUIStyle.SmallFont)
            { CanBeFocused = false, TextColor = Color.Gray };

            _listGroup = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.34f), content.RectTransform),
                isHorizontal: false);
            _listGroup.RelativeSpacing = 0.008f;

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.1f), content.RectTransform),
                "Готов.", wrap: true, font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);

            FillMonsterList();
        }

        private static void SetStatus(string text)
        {
            if (_status != null) { _status.Text = "→ " + text; }
            GUI.AddMessage("[Possess] " + text, Color.Orange);
        }

        private static void Possess(Character target)
        {
            Character.Controlled = target;
            SetStatus("вселился (фантом) в: " + (target.Name ?? target.SpeciesName) +
                " (#" + target.ID + "). Сервер рулит настоящим — жди резину. Чат = антидот АФК-кика.");
        }

        private static void FillMonsterList()
        {
            if (_listGroup == null) { return; }
            _listGroup.ClearChildren();

            int count = 0;
            foreach (Character ch in Character.CharacterList)
            {
                if (ch == null || ch.Removed || ch.IsDead) { continue; }
                if (!(ch.AIController is EnemyAIController)) { continue; }
                count++;
                if (count > 8) { break; } // не раздуваем меню

                string label = (ch.Name ?? ch.SpeciesName) + " #" + ch.ID +
                    (ch.IsDead ? " [мёртв]" : "");
                var row = new GUILayoutGroup(
                    new RectTransform(new Vector2(1f, 0.18f), _listGroup.RectTransform),
                    isHorizontal: true);
                row.RelativeSpacing = 0.01f;

                new GUITextBlock(
                    new RectTransform(new Vector2(0.62f, 1f), row.RectTransform),
                    label, textAlignment: Alignment.CenterLeft)
                { CanBeFocused = false, TextColor = Color.White };

                var btn = new GUIButton(
                    new RectTransform(new Vector2(0.38f, 1f), row.RectTransform),
                    "вселиться");
                btn.Color = new Color(90, 70, 140);
                Character target = ch;
                btn.OnClicked = (b, d) => { Possess(target); return true; };
            }

            if (count == 0)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.2f), _listGroup.RectTransform),
                    "Пусто. Монстры появляются только в раунде и в радиусе синхронизации.",
                    textColor: Color.Gray);
            }
        }
    }
}
