// ============================================================================
//  SpectreModule.cs — «СПЕКТР» (волна 723)
//  ★★★ NEW CVE-COMPLEX: SpectatePos = unvalidated server-side primitive ★★★
//
//  ПАКЕТ: UPDATE_INGAME + сегмент SpectatingPos { float X, float Y }.
//  GameServer:1489: c.SpectatePos = new Vector2(inc.ReadSingle(), inc.ReadSingle());
//  БЕЗ проверок (ни SpectateOnly, ни IsValid).
//  ★ ГЕТТЕР (Client.cs:115): SpectatePos отдаётся серверу ТОЛЬКО если
//  character == null (спектатор) ИЛИ character.IsDead (труп)!
//  ЖИВЫМ сервер на SpectatePos плевать (вернётся null).
//  => ВЕКТОР: умер/спектатор + точка ЛЖИВАЯ (камера не там) =:
//  - ваниль-спектатор обязан ДЕРЖАТЬ КАМЕРУ в точке (иначе ничего не видит),
//    а наш клиент держит камеру у себя, данные о точке всё равно СТРИМЯТСЯ
//    в клиент => мод-оверлей радара при «пустой» камере
//  - МЁРТВЫЙ в PvP ставит точку на базу врага => до конца раунда сервер
//    гонит ему позиции всех их тел (HighPrio 1000u / 1с до 10000u)
//  - ТАЛАМУС: мёртвый/спектатор будит врек (турели+спавн клеток), камера
//    при этом у своей базы = «никого не смотрит», а WreckAI считает иначе.
//  Сервер читает SpectatePos в решениях:
//
//  1) РАДАР (CharacterNetworking:56 GetPositionUpdateInterval):
//       comparePosition = recipient.SpectatePos ?? Character.WorldPosition
//     Персонажи в 1000 юнитах от ТОЧКИ → HighPrio interval 0.0с (каждый тик!),
//     до 10000 → 1с. Сервер СТРИМИТ НАМ позиции всех тел вокруг точки,
//     даже если наше тело в другом конце карты = УДАЛЁННЫЙ РАДАР/WALLHACK.
//
//  2) ХУЛЛ-СИНК (Hull.cs:34): пожары/вода/состояние отсеков вокруг точки
//     летят нам в апдейтах = мониторинг чужой субмарины вживую.
//
//  3) ТАЛАМУС-БУДИЛЬНИК (WreckAI:245 SERVER): isSomeoneNearby проверяет
//     SpectatePos ВСЕХ клиентов (радиус 2×sonar). Спит ли врек — зависит
//     от нашей точки! Ставим на врек → OperateTurrets + UpdateReinforcements
//     (спавн клеток) активны, пока наша команда ещё в пути = армия клеток
//     к их прибытию. Выключить чужой SpectatePos нельзя (только свой).
//
//  4) NaN (718): distance=NaN → все сравнения false → рентген слепнет,
//     таламус NaN не будит. Self-sabotage, в модуле не используем.
//
//  УСЛОВИЯ: раунд + ты InGame + (мёртв ИЛИ без персонажа-спектатор).
//  Живым мод честно откажется (сервер молча игнорирует).
//  Имена остальных: «усталый призрак - самый опасный игрок раунда».
// ============================================================================

using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class SpectreModule : CSModuleBase
    {
        public override string Id   => "spectre";
        public override string Name => "Spectre";
        public override string Description =>
            "SpectatePos packet (2 raw floats, ZERO validation) drives SERVER-side decisions: " +
            "character sync priority (radar of any map point - enemy positions streamed to you), " +
            "hull status sync (fires/floods on enemy subs), and WreckAI thalamus wake-up " +
            "(turrets + cell spawn while your crew is away). Wallhack + thalamus alarm in one packet.";
        public override string Category => "exploit";

        public override string GetLabel() => "Spectre 📡";

        private static GUIMessageBox _box;
        private static GUITextBox _xBox;
        private static GUITextBox _yBox;
        private static GUITextBlock _status;

        public override void OnClick()
        {
            if (_box != null) { _box.Close(); _box = null; return; }
            Open();
        }

        private static void Open()
        {
            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[Spectre] нет подключения", Color.Red);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "SPECTRE — SpectatePos-примитив (радар + таламус)",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.68f, 0.6f));

            var content = _box.Content;
            content.ClearChildren();

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.3f), content.RectTransform),
                "Точка SpectatePos управляет СЕРВЕРОМ:\n" +
                "• РАДАР: сервер льёт тебе позиции всех тел в 1000u от точки (каждый тик)\n" +
                "• ХУЛЛЫ: пожары/вода вокруг точки\n" +
                "• ТАЛАМУС: точка на вреке будит турели и СПАВН КЛЕТОК\n" +
                "Нужен раунд + ты в игре. Сброс = точка на своё тело.",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont)
            { TextColor = new Color(140, 200, 255) };

            var coordRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.13f), content.RectTransform),
                isHorizontal: true);
            coordRow.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.12f, 1f), coordRow.RectTransform),
                "X:", textAlignment: Alignment.CenterRight);
            _xBox = new GUITextBox(
                new RectTransform(new Vector2(0.36f, 1f), coordRow.RectTransform),
                "0");
            new GUITextBlock(
                new RectTransform(new Vector2(0.12f, 1f), coordRow.RectTransform),
                "Y:", textAlignment: Alignment.CenterRight);
            _yBox = new GUITextBox(
                new RectTransform(new Vector2(0.36f, 1f), coordRow.RectTransform),
                "0");

            var btnRow1 = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.13f), content.RectTransform),
                isHorizontal: true);
            btnRow1.RelativeSpacing = 0.01f;

            var sendBtn = new GUIButton(
                new RectTransform(new Vector2(0.48f, 1f), btnRow1.RectTransform),
                "📡 ТОЧКУ СЮДА (X,Y)");
            sendBtn.Color = new Color(170, 70, 60);
            sendBtn.OnClicked = (b, d) =>
            {
                if (TryParse(_xBox?.Text, out float x) && TryParse(_yBox?.Text, out float y))
                {
                    Send(x, y);
                    SetStatus($"точка ({x:F0}, {y:F0}): радар + хулл-синк активны, врек рядом проснётся");
                }
                else { SetStatus("кривые координаты"); }
                return true;
            };

            var focusBtn = new GUIButton(
                new RectTransform(new Vector2(0.48f, 1f), btnRow1.RectTransform),
                "🎯 НА ТРУП ПОД КУРСОРОМ");
            focusBtn.Color = new Color(120, 80, 160);
            focusBtn.OnClicked = (b, d) =>
            {
                Character me = Character.Controlled;
                Character focus = me?.FocusedCharacter;
                if (focus == null) { SetStatus("наведи курсор на цель (персонаж)"); return true; }
                Vector2 p = focus.WorldPosition;
                Send(p.X, p.Y);
                SetStatus($"радар на {focus.DisplayName}: позиции и хуллы вокруг него — твои");
                return true;
            };

            var btnRow2 = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.13f), content.RectTransform),
                isHorizontal: true);
            btnRow2.RelativeSpacing = 0.01f;

            var selfBtn = new GUIButton(
                new RectTransform(new Vector2(0.48f, 1f), btnRow2.RectTransform),
                "☠ ПРОВЕРКА СТАТУСА");
            selfBtn.Color = new Color(60, 110, 150);
            selfBtn.OnClicked = (b, d) =>
            {
                Character me = GameMain.Client?.Character;
                if (me == null)
                {
                    SetStatus("без персонажа (спектатор) — SpectatePos СЕРВЕР ПРИНИМАЕТ, поехали");
                }
                else if (me.IsDead)
                {
                    SetStatus("ты МЁРТВ — SpectatePos работает, радар доступен до респавна");
                }
                else
                {
                    SetStatus("ты ЖИВ — сервер ИГНОРИРУЕТ SpectatePos (Client.cs:115: живым = null). Умри или стань спектатором :)");
                }
                return true;
            };

            var wreckBtn = new GUIButton(
                new RectTransform(new Vector2(0.48f, 1f), btnRow2.RectTransform),
                "💀 БУДИТЬ ВРЕК (издалека)");
            wreckBtn.Color = new Color(150, 120, 40);
            wreckBtn.OnClicked = (b, d) =>
            {
                Submarine wreck = null;
                foreach (Submarine s in Submarine.Loaded)
                {
                    if (s.Info.Type == SubmarineType.Wreck) { wreck = s; break; }
                }
                if (wreck == null) { SetStatus("врека на карте нет"); return true; }
                Vector2 p = wreck.WorldPosition;
                Send(p.X, p.Y);
                SetStatus($"врек проснулся ({p.X:F0}, {p.Y:F0}): турели + спавн клеток активны, " +
                    "пока команда в пути — там будет весело");
                return true;
            };

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.14f), content.RectTransform),
                "Готов. Пульс не нужен — точка живёт до перезаписи/дисконнекта.",
                wrap: true, font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);
        }

        private static bool TryParse(string s, out float v) => float.TryParse(s?.Trim(), out v);

        private static void SetStatus(string text)
        {
            if (_status != null) { _status.Text = "→ " + text; }
            GUI.AddMessage("[Spectre] " + text, Color.Orange);
        }

        private static void Send(float x, float y)
        {
            try
            {
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME); // == 1
                msg.WriteBoolean(true);  // midroundSyncingDone = true (ресинк НЕ триггерим)
                msg.WritePadBits();

                int pointerLocation = msg.BitPosition;
                msg.WriteInt32(0); // placeholder table pointer

                int segmentStart = msg.BitPosition;

                // SpectatingPos: два сырых float (GameServer:1490)
                msg.WriteSingle(x);
                msg.WriteSingle(y);

                int tablePosition = msg.BitPosition;
                msg.BitPosition = pointerLocation;
                msg.WriteInt32(tablePosition - pointerLocation);
                msg.BitPosition = tablePosition;

                msg.WriteUInt16(1); // segment count

                var asm = typeof(GameMain).Assembly;
                var tEnum = asm.GetType("Barotrauma.Networking.ClientNetSegment", false);
                var tSegOpen = asm.GetType("Barotrauma.Networking.Segment`1", false);
                var tSeg = tSegOpen.MakeGenericType(tEnum);
                var ci = tSeg.GetConstructor(new[] { tEnum, typeof(int) });
                object segment = ci.Invoke(new[] { Enum.Parse(tEnum, "SpectatingPos"), (object)(segmentStart - pointerLocation) });
                typeof(INetSerializableStruct).GetMethod("Write").Invoke(segment, new object[] { msg });

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Spectre] send fail: " + e.Message, Color.Red);
            }
        }
    }
}
